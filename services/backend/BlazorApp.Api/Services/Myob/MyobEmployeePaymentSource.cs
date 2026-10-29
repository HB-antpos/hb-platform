using System.Collections.Concurrent;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Myob;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

/// <summary>
/// 某公司文件里每位员工的第一个银行账户（Contact/EmployeePaymentDetails），员工 UID → 账户。
/// 只给「资料同步」用，所以和员工名册分开拉取、分开缓存：拉不到（scope 不够 / MYOB 暂时不可用）时只影响银行字段，不拖垮绑定页。
/// </summary>
public interface IMyobEmployeePaymentSource
{
    /// <exception cref="MyobApiException">连接不存在 / 需要重新授权 / MYOB 暂时不可用 / 没有权限读付款详情。</exception>
    Task<IReadOnlyDictionary<string, MyobBankAccountRecord>> GetAsync(
        string connectionGuid, bool refresh, CancellationToken cancellationToken);
}

internal sealed class MyobEmployeePaymentSource : IMyobEmployeePaymentSource
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private readonly ISqlSugarClient _db;
    private readonly IMyobApiClient _client;
    private readonly IMyobTokenProvider _tokens;
    private readonly IMemoryCache _cache;

    public MyobEmployeePaymentSource(
        SqlSugarContext context,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        IMemoryCache cache)
        : this(context.Db, client, tokens, cache)
    {
    }

    internal MyobEmployeePaymentSource(
        ISqlSugarClient db,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        IMemoryCache cache)
    {
        _db = db;
        _client = client;
        _tokens = tokens;
        _cache = cache;
    }

    internal static string CacheKey(string connectionGuid) => $"myob:payment:{connectionGuid}";

    public async Task<IReadOnlyDictionary<string, MyobBankAccountRecord>> GetAsync(
        string connectionGuid, bool refresh, CancellationToken cancellationToken)
    {
        var key = CacheKey(connectionGuid);
        IReadOnlyDictionary<string, MyobBankAccountRecord>? cached;
        if (!refresh && _cache.TryGetValue(key, out cached) && cached is not null)
        {
            return cached;
        }

        var gate = Locks.GetOrAdd(connectionGuid, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _cache.TryGetValue(key, out cached) && cached is not null)
            {
                return cached;
            }

            var connection = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == connectionGuid)
                ?? throw new MyobApiException(MyobErrorCodes.ConnectionNotFound, "MYOB 连接不存在");
            var accessToken = await _tokens.GetAccessTokenAsync(connectionGuid, cancellationToken);
            var records = await _client.GetEmployeePaymentDetailsAsync(connection.BusinessId, accessToken, cancellationToken);

            var data = (IReadOnlyDictionary<string, MyobBankAccountRecord>)records
                .Where(record => record.BankAccounts.Count > 0)
                .GroupBy(record => record.EmployeeUid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().BankAccounts[0], StringComparer.OrdinalIgnoreCase);
            // 与员工名册同样缓存 10 分钟；含银行账号，只放进程内存，不落盘。
            _cache.Set(key, data, MyobEmployeeDirectory.CacheLifetime);
            return data;
        }
        finally
        {
            gate.Release();
        }
    }
}
