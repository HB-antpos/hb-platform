using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Security;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

/// <summary>授权回调的处理结果：Web 据此把浏览器重定向回 MYOB 页并提示成功 / 失败。</summary>
public sealed record MyobCallbackResult(bool Success, string? ConnectionGuid, string? ErrorCode);

/// <summary>
/// MYOB 连接管理（方案第 5 节）：授权入口、OAuth 回调落库、ABN 维护、验证、断开，以及「分店 → 公司文件」映射。
/// 权限（Myob.Connection.Manage）由控制器把关；回调是 MYOB 经浏览器跳回的匿名请求，靠带过期时间的加密 state 证明发起人。
/// </summary>
public interface IMyobConnectionService
{
    Task<ApiResponse<List<MyobConnectionDto>>> ListConnectionsAsync(CancellationToken cancellationToken);

    Task<ApiResponse<MyobAuthorizeUrlDto>> BuildAuthorizeUrlAsync(string userGuid, CancellationToken cancellationToken);

    Task<MyobCallbackResult> HandleCallbackAsync(
        string? code,
        string? state,
        string? businessId,
        string? error,
        CancellationToken cancellationToken);

    Task<ApiResponse<MyobConnectionDto>> UpdateAbnAsync(string connectionGuid, string? abn, CancellationToken cancellationToken);

    Task<ApiResponse<MyobConnectionDto>> VerifyAsync(string connectionGuid, CancellationToken cancellationToken);

    Task<ApiResponse<MyobConnectionDto>> DisconnectAsync(string connectionGuid, CancellationToken cancellationToken);

    Task<ApiResponse<MyobStoreMappingsDto>> GetStoreMappingsAsync(CancellationToken cancellationToken);

    Task<ApiResponse<MyobStoreMappingsDto>> UpdateStoreMappingsAsync(
        MyobUpdateStoreMappingsRequest request,
        string userGuid,
        CancellationToken cancellationToken);
}

public sealed class MyobConnectionService : IMyobConnectionService
{
    /// <summary>授权发起到回调的最长间隔；MYOB 授权码本身只有 2~5 分钟有效。</summary>
    internal static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private const int MaxErrorLength = 500;

    private readonly ISqlSugarClient _db;
    private readonly IMyobApiClient _client;
    private readonly IMyobTokenProvider _tokens;
    private readonly MyobSecretProtector _protector;
    private readonly MyobOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MyobConnectionService> _logger;

    public MyobConnectionService(
        SqlSugarContext context,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        MyobSecretProtector protector,
        IOptions<MyobOptions> options,
        ILogger<MyobConnectionService> logger,
        TimeProvider? timeProvider = null)
        : this(context.Db, client, tokens, protector, options, logger, timeProvider)
    {
    }

    internal MyobConnectionService(
        ISqlSugarClient db,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        MyobSecretProtector protector,
        IOptions<MyobOptions> options,
        ILogger<MyobConnectionService> logger,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _client = client;
        _tokens = tokens;
        _protector = protector;
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ───────────────────────── 连接列表 ─────────────────────────

    public async Task<ApiResponse<List<MyobConnectionDto>>> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        var connections = await _db.Queryable<MyobConnection>().ToListAsync();
        return ApiResponse<List<MyobConnectionDto>>.OK(await ToDtosAsync(connections));
    }

    // ───────────────────────── 授权入口 ─────────────────────────

    public Task<ApiResponse<MyobAuthorizeUrlDto>> BuildAuthorizeUrlAsync(string userGuid, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return Task.FromResult(ApiResponse<MyobAuthorizeUrlDto>.Error(
                "尚未配置 MYOB 开发者应用（Myob:ClientId / ClientSecret / RedirectUri）",
                MyobErrorCodes.NotConfigured));
        }

        // state 只带发起人和随机数，10 分钟内有效；回调时验证它来自本系统的授权入口，防止伪造回调。
        var payload = JsonSerializer.Serialize(new StatePayload(userGuid, Guid.NewGuid().ToString("N")));
        var state = _protector.ProtectState(payload, _time.GetUtcNow().Add(StateLifetime));

        var query = string.Join(
            "&",
            $"client_id={Uri.EscapeDataString(_options.ClientId)}",
            $"redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}",
            "response_type=code",
            $"scope={Uri.EscapeDataString(_options.Scopes)}",
            $"state={Uri.EscapeDataString(state)}",
            "prompt=consent");
        var url = $"{_options.AuthorizeUrl}?{query}";
        return Task.FromResult(ApiResponse<MyobAuthorizeUrlDto>.OK(new MyobAuthorizeUrlDto { Url = url }));
    }

    // ───────────────────────── 授权回调 ─────────────────────────

    public async Task<MyobCallbackResult> HandleCallbackAsync(
        string? code,
        string? state,
        string? businessId,
        string? error,
        CancellationToken cancellationToken)
    {
        // 先验 state：无效 state 的请求一律不处理，也不区分原因。
        if (string.IsNullOrWhiteSpace(state)
            || !_protector.TryUnprotectState(state, out var statePlain)
            || !TryReadState(statePlain, out var userGuid))
        {
            return new MyobCallbackResult(false, null, MyobErrorCodes.InvalidState);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            // 用户在 MYOB 页面点了拒绝，或 MYOB 返回其他授权错误。
            return new MyobCallbackResult(false, null, MyobErrorCodes.AuthorizationDenied);
        }

        if (!_options.IsConfigured)
        {
            return new MyobCallbackResult(false, null, MyobErrorCodes.NotConfigured);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return new MyobCallbackResult(false, null, MyobErrorCodes.TokenExchangeFailed);
        }

        if (string.IsNullOrWhiteSpace(businessId))
        {
            // 没有 businessId 说明授权没有选定公司文件，无法确定 cf_uri。
            return new MyobCallbackResult(false, null, MyobErrorCodes.BusinessIdMissing);
        }

        var cleanBusinessId = businessId.Trim();
        try
        {
            var tokens = await _client.ExchangeCodeAsync(code, cancellationToken);
            if (string.IsNullOrEmpty(tokens.RefreshToken))
            {
                // 没有 refresh token 就无法长期使用，等同授权失败。
                return new MyobCallbackResult(false, null, MyobErrorCodes.TokenExchangeFailed);
            }

            // 先确认 token 真能访问该公司文件，再落库；失败则不留半成品连接。
            var company = await _client.GetCompanyAsync(cleanBusinessId, tokens.AccessToken, cancellationToken);
            var connectionGuid = await UpsertConnectionAsync(cleanBusinessId, company, tokens.RefreshToken, userGuid);
            _tokens.Invalidate(connectionGuid);
            return new MyobCallbackResult(true, connectionGuid, null);
        }
        catch (MyobApiException ex)
        {
            // 日志只记业务 ID 与错误码，不记授权码 / token。
            _logger.LogWarning("MYOB 授权回调失败：BusinessId={BusinessId} Code={Code} Status={Status}",
                cleanBusinessId, ex.Code, ex.StatusCode);
            return new MyobCallbackResult(false, null, ex.Code);
        }
    }

    /// <summary>同一个公司文件重新授权＝更新原连接（保留分店映射），不会产生第二条。</summary>
    private async Task<string> UpsertConnectionAsync(
        string businessId,
        MyobCompanyInfo company,
        string refreshToken,
        string userGuid)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var abn = ExtractAbn(company.AbnOrTfn);
        var protectedToken = _protector.ProtectRefreshToken(refreshToken);

        var existing = await _db.Queryable<MyobConnection>().FirstAsync(item => item.BusinessId == businessId);
        if (existing is null)
        {
            var created = new MyobConnection
            {
                ConnectionGuid = Guid.NewGuid().ToString(),
                BusinessId = businessId,
                BusinessName = company.CompanyName,
                Abn = abn,
                RefreshTokenProtected = protectedToken,
                Status = MyobConnectionStatuses.Active,
                AuthorizedByUserGuid = userGuid,
                AuthorizedAt = now,
                LastRefreshedAt = now,
                CreatedAt = now,
            };
            await _db.Insertable(created).ExecuteCommandAsync();
            return created.ConnectionGuid;
        }

        existing.BusinessName = company.CompanyName;
        // MYOB 读到有效 ABN 就以它为准；读不到时保留之前手填的值。
        existing.Abn = abn ?? existing.Abn;
        existing.RefreshTokenProtected = protectedToken;
        existing.Status = MyobConnectionStatuses.Active;
        existing.AuthorizedByUserGuid = userGuid;
        existing.AuthorizedAt = now;
        existing.LastRefreshedAt = now;
        existing.LastError = null;
        existing.UpdatedAt = now;
        await _db.Updateable(existing).ExecuteCommandAsync();
        return existing.ConnectionGuid;
    }

    // ───────────────────────── ABN / 验证 / 断开 ─────────────────────────

    public async Task<ApiResponse<MyobConnectionDto>> UpdateAbnAsync(
        string connectionGuid,
        string? abn,
        CancellationToken cancellationToken)
    {
        var connection = await FindAsync(connectionGuid);
        if (connection is null)
        {
            return NotFound();
        }

        var normalized = MyobAbn.Normalize(abn);
        if (normalized is not null && !MyobAbn.IsValid(normalized))
        {
            return ApiResponse<MyobConnectionDto>.Error("ABN 必须是 11 位有效数字", MyobErrorCodes.InvalidAbn);
        }

        connection.Abn = normalized;
        connection.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.Updateable(connection).UpdateColumns(item => new { item.Abn, item.UpdatedAt }).ExecuteCommandAsync();
        return ApiResponse<MyobConnectionDto>.OK(await ToDtoAsync(connection));
    }

    public async Task<ApiResponse<MyobConnectionDto>> VerifyAsync(string connectionGuid, CancellationToken cancellationToken)
    {
        var connection = await FindAsync(connectionGuid);
        if (connection is null)
        {
            return NotFound();
        }

        try
        {
            var accessToken = await _tokens.GetAccessTokenAsync(connectionGuid, cancellationToken);
            var company = await _client.GetCompanyAsync(connection.BusinessId, accessToken, cancellationToken);

            // 取库里最新值再改：GetAccessTokenAsync 可能刚刷新过 token 并落库。
            connection = (await FindAsync(connectionGuid))!;
            connection.BusinessName = company.CompanyName;
            connection.Abn = ExtractAbn(company.AbnOrTfn) ?? connection.Abn;
            connection.LastRefreshedAt = _time.GetUtcNow().UtcDateTime;
            connection.LastError = null;
            connection.UpdatedAt = connection.LastRefreshedAt;
            await _db.Updateable(connection)
                .UpdateColumns(item => new { item.BusinessName, item.Abn, item.LastRefreshedAt, item.LastError, item.UpdatedAt })
                .ExecuteCommandAsync();
            return ApiResponse<MyobConnectionDto>.OK(await ToDtoAsync(connection), "验证通过");
        }
        catch (MyobApiException ex)
        {
            // 取库里最新状态（可能已被标成 NeedsReauth），失败时随错误一起返回给页面刷新。
            connection = (await FindAsync(connectionGuid)) ?? connection;
            if (ex.Code != MyobErrorCodes.NeedsReauthorization && ex.Code != MyobErrorCodes.ConnectionNotActive)
            {
                connection.LastError = Truncate(ex.Message);
                connection.UpdatedAt = _time.GetUtcNow().UtcDateTime;
                await _db.Updateable(connection).UpdateColumns(item => new { item.LastError, item.UpdatedAt }).ExecuteCommandAsync();
            }

            return ApiResponse<MyobConnectionDto>.FailWithData(await ToDtoAsync(connection), ex.Message, ex.Code);
        }
    }

    public async Task<ApiResponse<MyobConnectionDto>> DisconnectAsync(string connectionGuid, CancellationToken cancellationToken)
    {
        var connection = await FindAsync(connectionGuid);
        if (connection is null)
        {
            return NotFound();
        }

        await _db.Ado.BeginTranAsync();
        try
        {
            // 断开＝清除 token（不再能访问该公司文件）并移除所有分店映射；连接记录本身保留，便于以后重新授权时找回。
            await _db.Deleteable<MyobStoreMapping>().Where(item => item.ConnectionGuid == connectionGuid).ExecuteCommandAsync();
            connection.Status = MyobConnectionStatuses.Disconnected;
            connection.RefreshTokenProtected = null;
            connection.LastError = null;
            connection.UpdatedAt = _time.GetUtcNow().UtcDateTime;
            await _db.Updateable(connection)
                .UpdateColumns(item => new { item.Status, item.RefreshTokenProtected, item.LastError, item.UpdatedAt })
                .ExecuteCommandAsync();
            await _db.Ado.CommitTranAsync();
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();
            throw;
        }

        _tokens.Invalidate(connectionGuid);
        return ApiResponse<MyobConnectionDto>.OK(await ToDtoAsync(connection));
    }

    // ───────────────────────── 分店映射 ─────────────────────────

    public async Task<ApiResponse<MyobStoreMappingsDto>> GetStoreMappingsAsync(CancellationToken cancellationToken) =>
        ApiResponse<MyobStoreMappingsDto>.OK(await BuildMappingsAsync());

    public async Task<ApiResponse<MyobStoreMappingsDto>> UpdateStoreMappingsAsync(
        MyobUpdateStoreMappingsRequest request,
        string userGuid,
        CancellationToken cancellationToken)
    {
        var items = request.Items ?? [];
        var storeCodes = items.Select(item => item.StoreCode?.Trim() ?? string.Empty).ToList();
        if (storeCodes.Any(string.IsNullOrEmpty) || storeCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != storeCodes.Count)
        {
            return ApiResponse<MyobStoreMappingsDto>.Error("分店编号不能为空，且同一次提交里不能重复", MyobErrorCodes.StoreNotFound);
        }

        var knownStores = (await _db.Queryable<Store>()
                .Where(store => storeCodes.Contains(store.StoreCode) && !store.IsDeleted)
                .Select(store => store.StoreCode)
                .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = storeCodes.Where(code => !knownStores.Contains(code)).ToList();
        if (missing.Count > 0)
        {
            return ApiResponse<MyobStoreMappingsDto>.Error($"分店不存在：{string.Join("、", missing)}", MyobErrorCodes.StoreNotFound);
        }

        var targetGuids = items.Select(item => item.ConnectionGuid).Where(guid => !string.IsNullOrWhiteSpace(guid)).Distinct().ToList();
        var activeGuids = (await _db.Queryable<MyobConnection>()
                .Where(item => targetGuids.Contains(item.ConnectionGuid) && item.Status == MyobConnectionStatuses.Active)
                .Select(item => item.ConnectionGuid)
                .ToListAsync())
            .ToHashSet();
        if (targetGuids.Any(guid => !activeGuids.Contains(guid!)))
        {
            return ApiResponse<MyobStoreMappingsDto>.Error("只能映射到状态为 Active 的 MYOB 连接", MyobErrorCodes.ConnectionNotActive);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await _db.Ado.BeginTranAsync();
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                var storeCode = storeCodes[index];
                var target = items[index].ConnectionGuid;
                if (string.IsNullOrWhiteSpace(target))
                {
                    await _db.Deleteable<MyobStoreMapping>().Where(item => item.StoreCode == storeCode).ExecuteCommandAsync();
                    continue;
                }

                var existing = await _db.Queryable<MyobStoreMapping>().FirstAsync(item => item.StoreCode == storeCode);
                if (existing is null)
                {
                    await _db.Insertable(new MyobStoreMapping
                    {
                        StoreCode = storeCode,
                        ConnectionGuid = target,
                        MappedBy = userGuid,
                        MappedAt = now,
                    }).ExecuteCommandAsync();
                }
                else if (existing.ConnectionGuid != target)
                {
                    existing.ConnectionGuid = target;
                    existing.MappedBy = userGuid;
                    existing.MappedAt = now;
                    await _db.Updateable(existing).ExecuteCommandAsync();
                }
            }

            await _db.Ado.CommitTranAsync();
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();
            throw;
        }

        return ApiResponse<MyobStoreMappingsDto>.OK(await BuildMappingsAsync());
    }

    private async Task<MyobStoreMappingsDto> BuildMappingsAsync()
    {
        var stores = await _db.Queryable<Store>()
            .Where(store => !store.IsDeleted)
            .OrderBy(store => store.StoreCode)
            .Select(store => new { store.StoreCode, store.StoreName, store.ABN, store.IsActive })
            .ToListAsync();
        var mappings = (await _db.Queryable<MyobStoreMapping>().ToListAsync())
            .ToDictionary(item => item.StoreCode, StringComparer.OrdinalIgnoreCase);
        var connections = await _db.Queryable<MyobConnection>().ToListAsync();
        var activeConnections = connections.Where(item => item.Status == MyobConnectionStatuses.Active).ToList();

        var rows = stores.Select(store =>
        {
            var storeAbn = MyobAbn.Normalize(store.ABN);
            mappings.TryGetValue(store.StoreCode, out var mapping);
            return new MyobStoreMappingItemDto
            {
                StoreCode = store.StoreCode,
                StoreName = store.StoreName,
                StoreAbn = storeAbn,
                IsActive = store.IsActive,
                ConnectionGuid = mapping?.ConnectionGuid,
                SuggestedConnectionGuid = mapping is null ? SuggestConnection(storeAbn, activeConnections) : null,
                MappedAt = mapping?.MappedAt,
            };
        }).ToList();

        return new MyobStoreMappingsDto
        {
            Stores = rows,
            Connections = await ToDtosAsync(activeConnections),
        };
    }

    /// <summary>
    /// 按 ABN 一致建议连接：只有「恰好一个」Active 连接 ABN 相同才建议。
    /// 两个公司文件共用同一 ABN（如分公司）时不替人决定，留空由人工选。
    /// </summary>
    internal static string? SuggestConnection(string? storeAbn, IReadOnlyList<MyobConnection> activeConnections)
    {
        if (storeAbn is null)
        {
            return null;
        }

        var matches = activeConnections.Where(item => item.Abn == storeAbn).ToList();
        return matches.Count == 1 ? matches[0].ConnectionGuid : null;
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private async Task<MyobConnection?> FindAsync(string connectionGuid) =>
        await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == connectionGuid);

    private static ApiResponse<MyobConnectionDto> NotFound() =>
        ApiResponse<MyobConnectionDto>.Error("MYOB 连接不存在", MyobErrorCodes.ConnectionNotFound);

    /// <summary>MYOB 的 ABNOrTFN 可能是 TFN（9 位）或空：只有通过校验的 11 位 ABN 才采用。</summary>
    internal static string? ExtractAbn(string? abnOrTfn)
    {
        var digits = MyobAbn.Normalize(abnOrTfn);
        return MyobAbn.IsValid(digits) ? digits : null;
    }

    private static string Truncate(string value) => value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];

    private static bool TryReadState(string json, out string userGuid)
    {
        userGuid = string.Empty;
        try
        {
            var payload = JsonSerializer.Deserialize<StatePayload>(json);
            if (payload is null || string.IsNullOrWhiteSpace(payload.U))
            {
                return false;
            }

            userGuid = payload.U;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<MyobConnectionDto> ToDtoAsync(MyobConnection connection) =>
        (await ToDtosAsync([connection]))[0];

    private async Task<List<MyobConnectionDto>> ToDtosAsync(IReadOnlyList<MyobConnection> connections)
    {
        if (connections.Count == 0)
        {
            return [];
        }

        var guids = connections.Select(item => item.ConnectionGuid).ToList();
        var mappings = await _db.Queryable<MyobStoreMapping>().Where(item => guids.Contains(item.ConnectionGuid)).ToListAsync();
        var userGuids = connections
            .Select(item => item.AuthorizedByUserGuid)
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Distinct()
            .ToList();
        var names = userGuids.Count == 0
            ? new Dictionary<string, string?>()
            : (await _db.Queryable<User>()
                    .Where(user => userGuids.Contains(user.UserGUID))
                    .Select(user => new { user.UserGUID, user.FullName, user.Username })
                    .ToListAsync())
                .ToDictionary(user => user.UserGUID, user => (string?)(string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName));

        // Active 在前，其次 NeedsReauth，最后 Disconnected；同状态按公司名排序，页面不用再排。
        return connections
            .OrderBy(item => item.Status switch
            {
                MyobConnectionStatuses.Active => 0,
                MyobConnectionStatuses.NeedsReauth => 1,
                _ => 2,
            })
            .ThenBy(item => item.BusinessName, StringComparer.OrdinalIgnoreCase)
            .Select(item => new MyobConnectionDto
            {
                ConnectionGuid = item.ConnectionGuid,
                BusinessId = item.BusinessId,
                BusinessName = item.BusinessName,
                Abn = item.Abn,
                Status = item.Status,
                AuthorizedByUserGuid = item.AuthorizedByUserGuid,
                AuthorizedByName = item.AuthorizedByUserGuid is not null && names.TryGetValue(item.AuthorizedByUserGuid, out var name) ? name : null,
                AuthorizedAt = item.AuthorizedAt,
                LastRefreshedAt = item.LastRefreshedAt,
                LastError = item.LastError,
                StoreCodes = mappings
                    .Where(mapping => mapping.ConnectionGuid == item.ConnectionGuid)
                    .Select(mapping => mapping.StoreCode)
                    .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            })
            .ToList();
    }

    private sealed record StatePayload(string U, string N);
}
