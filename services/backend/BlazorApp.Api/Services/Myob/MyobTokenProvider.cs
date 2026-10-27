using System.Collections.Concurrent;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Security;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

/// <summary>
/// 给某个 MYOB 连接提供可用的 access token：先查内存缓存，过期则用库里加密的 refresh token 换新。
/// 要点：
/// - access token 只放内存缓存（约 20 分钟有效），不入库；
/// - refresh 响应里的 refresh_token 可能轮换，拿到新值必须立即加密落库，否则下次刷新会因旧 token 失效而掉线；
/// - 同一连接的刷新串行（per-connection 锁），避免并发请求各刷一次、互相作废对方刚拿到的 refresh token；
/// - invalid_grant（refresh token 失效）→ 连接标 NeedsReauth，提示 Administrator 重新授权；其他失败只记 LastError，状态不变（可能是 MYOB 临时故障）。
/// </summary>
public interface IMyobTokenProvider
{
    /// <exception cref="MyobApiException">连接不存在 / 未启用 / 需要重新授权 / MYOB 暂时不可用。</exception>
    Task<string> GetAccessTokenAsync(string connectionGuid, CancellationToken cancellationToken);

    /// <summary>丢弃缓存的 access token（断开连接、重新授权后调用）。</summary>
    void Invalidate(string connectionGuid);
}

public sealed class MyobTokenProvider : IMyobTokenProvider
{
    /// <summary>提前过期的安全余量：避免拿到一个马上失效的 token 去调接口。</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan MinCacheLifetime = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private readonly ISqlSugarClient _db;
    private readonly IMyobApiClient _client;
    private readonly MyobSecretProtector _protector;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;

    public MyobTokenProvider(
        SqlSugarContext context,
        IMyobApiClient client,
        MyobSecretProtector protector,
        IMemoryCache cache,
        TimeProvider? timeProvider = null)
        : this(context.Db, client, protector, cache, timeProvider)
    {
    }

    internal MyobTokenProvider(
        ISqlSugarClient db,
        IMyobApiClient client,
        MyobSecretProtector protector,
        IMemoryCache cache,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _client = client;
        _protector = protector;
        _cache = cache;
        _time = timeProvider ?? TimeProvider.System;
    }

    internal static string CacheKey(string connectionGuid) => $"myob:access-token:{connectionGuid}";

    public void Invalidate(string connectionGuid) => _cache.Remove(CacheKey(connectionGuid));

    public async Task<string> GetAccessTokenAsync(string connectionGuid, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(CacheKey(connectionGuid), out string? cached) && !string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        var gate = Locks.GetOrAdd(connectionGuid, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // 等锁期间别的请求可能已经刷新完成。
            if (_cache.TryGetValue(CacheKey(connectionGuid), out cached) && !string.IsNullOrEmpty(cached))
            {
                return cached;
            }

            return await RefreshAsync(connectionGuid, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> RefreshAsync(string connectionGuid, CancellationToken cancellationToken)
    {
        var connection = await _db.Queryable<MyobConnection>()
            .Where(item => item.ConnectionGuid == connectionGuid)
            .FirstAsync();
        if (connection is null)
        {
            throw new MyobApiException(MyobErrorCodes.ConnectionNotFound, "MYOB 连接不存在");
        }

        if (connection.Status == MyobConnectionStatuses.NeedsReauth)
        {
            throw new MyobApiException(MyobErrorCodes.NeedsReauthorization, "MYOB 授权已失效，请由公司文件的 Administrator 重新授权");
        }

        if (connection.Status != MyobConnectionStatuses.Active || string.IsNullOrEmpty(connection.RefreshTokenProtected))
        {
            throw new MyobApiException(MyobErrorCodes.ConnectionNotActive, "MYOB 连接未启用");
        }

        string refreshToken;
        try
        {
            refreshToken = _protector.UnprotectRefreshToken(connection.RefreshTokenProtected);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // 密钥环丢失 / 密文损坏：只能重新授权。
            await MarkAsync(connection, MyobConnectionStatuses.NeedsReauth, "已保存的授权无法解密，需要重新授权");
            throw new MyobApiException(MyobErrorCodes.NeedsReauthorization, "已保存的授权无法解密，需要重新授权");
        }

        MyobTokenResponse tokens;
        try
        {
            tokens = await _client.RefreshAsync(refreshToken, cancellationToken);
        }
        catch (MyobApiException ex) when (ex.IsAuthorizationRevoked)
        {
            await MarkAsync(connection, MyobConnectionStatuses.NeedsReauth, "MYOB 授权已失效（refresh token 过期或被撤销），需要重新授权");
            throw new MyobApiException(MyobErrorCodes.NeedsReauthorization, "MYOB 授权已失效，请由公司文件的 Administrator 重新授权");
        }
        catch (MyobApiException ex)
        {
            // 临时故障：状态不变，只留下原因方便排查。
            await MarkAsync(connection, connection.Status, ex.Message);
            throw;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        connection.LastRefreshedAt = now;
        connection.LastError = null;
        connection.UpdatedAt = now;
        if (!string.IsNullOrEmpty(tokens.RefreshToken) && tokens.RefreshToken != refreshToken)
        {
            connection.RefreshTokenProtected = _protector.ProtectRefreshToken(tokens.RefreshToken);
        }

        await _db.Updateable(connection)
            .UpdateColumns(item => new { item.RefreshTokenProtected, item.LastRefreshedAt, item.LastError, item.UpdatedAt })
            .ExecuteCommandAsync();

        var lifetime = TimeSpan.FromSeconds(tokens.ExpiresInSeconds) - ExpirySkew;
        if (lifetime < MinCacheLifetime)
        {
            lifetime = MinCacheLifetime;
        }

        _cache.Set(CacheKey(connectionGuid), tokens.AccessToken, lifetime);
        return tokens.AccessToken;
    }

    private async Task MarkAsync(MyobConnection connection, string status, string error)
    {
        connection.Status = status;
        connection.LastError = error.Length <= 500 ? error : error[..500];
        connection.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.Updateable(connection)
            .UpdateColumns(item => new { item.Status, item.LastError, item.UpdatedAt })
            .ExecuteCommandAsync();
        _cache.Remove(CacheKey(connection.ConnectionGuid));
    }
}
