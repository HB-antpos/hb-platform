using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hbpos.Api.Data;
using Hbpos.Contracts.Square;
using SqlSugar;

namespace Hbpos.Api.Services;

/// <summary>
/// 收银机直连 Square（默认关闭，按门店开关）。服务端负责：
/// ① 开关打开的门店才下发令牌（审计 + 限流）；
/// ② 直连创建的结账，向 Square 核实归属后才登记（恢复中心 / 退款查询仍靠「门店 + 设备」校验）；
/// ③ 记录服务端自己发起的退款，Square 推来「不是服务端发起」的退款时告警（令牌被盗用时最重要的发现手段）。
/// </summary>
public interface ISquareDirectService
{
    Task<SquareDirectCredentialResponse> IssueCredentialAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);

    Task<SquareDirectOriginResponse> RegisterOriginAsync(
        string environment,
        string checkoutId,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);

    /// <summary>服务端创建退款成功后记下退款号，供 webhook 比对。</summary>
    Task RecordServerRefundAsync(
        string environment,
        string refundId,
        string? paymentId,
        CancellationToken cancellationToken);

    /// <summary>收到 refund.* 通知后比对：直连门店的付款被「非服务端」退款时写告警。</summary>
    Task CheckRefundNotificationAsync(
        string environment,
        string refundId,
        string? paymentId,
        CancellationToken cancellationToken);
}

public sealed class SquareDirectService(
    ISquareDirectRepository repository,
    ISquareTokenService squareTokenService,
    ISquareTerminalRestClient restClient,
    ISquareCheckoutSessionRepository checkoutSessionRepository,
    ILogger<SquareDirectService> logger,
    TimeProvider? timeProvider = null,
    TimeSpan? refundRaceGrace = null) : ISquareDirectService
{
    internal const string DisabledCode = "SQUARE_DIRECT_DISABLED";
    internal const string InvalidEnvironmentCode = "SQUARE_DIRECT_INVALID_ENVIRONMENT";
    internal const string RateLimitedCode = "SQUARE_DIRECT_RATE_LIMITED";
    internal const string TokenNotConfiguredCode = "SQUARE_TOKEN_NOT_CONFIGURED";
    internal const string CheckoutNotFoundCode = "SQUARE_DIRECT_CHECKOUT_NOT_FOUND";
    internal const string OriginMismatchCode = "SQUARE_DIRECT_ORIGIN_MISMATCH";
    internal const string ScopeConflictCode = "SQUARE_CHECKOUT_SCOPE_CONFLICT";

    internal const string AuditCredentialIssued = "CredentialIssued";
    internal const string AuditCredentialDenied = "CredentialDenied";
    internal const string AuditOriginRegistered = "OriginRegistered";
    internal const string AuditOriginRejected = "OriginRejected";
    internal const string AuditRefundNotFromServer = "RefundNotFromServer";

    /// <summary>本机凭据最长有效期：总部关掉开关后，收银机最迟一个周期内删掉令牌。</summary>
    internal static readonly TimeSpan CredentialLifetime = TimeSpan.FromHours(12);

    /// <summary>每台设备每小时最多领取次数；正常使用一天只领 2~3 次，超出多半是异常。</summary>
    internal const int MaxCredentialsPerDevicePerHour = 10;

    /// <summary>退款 webhook 可能抢在「服务端记下退款号」之前到达，先等一会儿再判，避免误报。</summary>
    internal static readonly TimeSpan DefaultRefundRaceGrace = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _refundRaceGrace = refundRaceGrace ?? DefaultRefundRaceGrace;

    public async Task<SquareDirectCredentialResponse> IssueCredentialAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironmentOrThrow(environment);
        if (!await repository.IsStoreEnabledAsync(storeCode, cancellationToken))
        {
            await AuditAsync(AuditCredentialDenied, storeCode, deviceCode, normalizedEnvironment, detail: "store switch is off", cancellationToken: cancellationToken);
            throw new SquareTerminalBackendException(
                DisabledCode,
                "Direct Square payment is not enabled for this store.",
                HttpStatusCode.Forbidden);
        }

        var since = _timeProvider.GetUtcNow().UtcDateTime.AddHours(-1);
        var recent = await repository.CountAuditAsync(AuditCredentialIssued, deviceCode, since, cancellationToken);
        if (recent >= MaxCredentialsPerDevicePerHour)
        {
            await AuditAsync(AuditCredentialDenied, storeCode, deviceCode, normalizedEnvironment, detail: "rate limited", cancellationToken: cancellationToken);
            throw new SquareTerminalBackendException(
                RateLimitedCode,
                "Too many credential requests from this device. Try again later.",
                HttpStatusCode.TooManyRequests);
        }

        // 门店专用令牌优先、否则共用令牌：沿用 #802 的解析，门店范围取自设备令牌声明。
        var token = await squareTokenService.GetActiveTokenAsync(normalizedEnvironment, cancellationToken);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new SquareTerminalBackendException(
                TokenNotConfiguredCode,
                "Square token is not configured for this environment.",
                HttpStatusCode.NotFound);
        }

        var version = token.UpdatedAt.ToUnixTimeMilliseconds();
        // 审计只记令牌指纹（SHA-256 前 8 位），用于事后判断哪个版本被领走，绝不记令牌本身。
        await AuditAsync(
            AuditCredentialIssued,
            storeCode,
            deviceCode,
            normalizedEnvironment,
            detail: $"version={version} fingerprint={Fingerprint(token.AccessToken)}",
            cancellationToken: cancellationToken);
        return new SquareDirectCredentialResponse(
            normalizedEnvironment,
            token.AccessToken,
            version,
            _timeProvider.GetUtcNow() + CredentialLifetime);
    }

    public async Task<SquareDirectOriginResponse> RegisterOriginAsync(
        string environment,
        string checkoutId,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironmentOrThrow(environment);
        if (string.IsNullOrWhiteSpace(checkoutId))
        {
            throw new SquareTerminalBackendException(CheckoutNotFoundCode, "checkoutId is required.", HttpStatusCode.BadRequest);
        }

        if (!await repository.IsStoreEnabledAsync(storeCode, cancellationToken))
        {
            throw new SquareTerminalBackendException(
                DisabledCode,
                "Direct Square payment is not enabled for this store.",
                HttpStatusCode.Forbidden);
        }

        var token = await squareTokenService.GetActiveTokenAsync(normalizedEnvironment, cancellationToken);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new SquareTerminalBackendException(
                TokenNotConfiguredCode,
                "Square token is not configured for this environment.",
                HttpStatusCode.NotFound);
        }

        // 不信收银机说的：用服务端自己的令牌向 Square 查这笔结账，再核对归属。
        var checkout = await restClient.GetCheckoutAsync(normalizedEnvironment, token.AccessToken, checkoutId.Trim(), cancellationToken);
        if (checkout is null || string.IsNullOrWhiteSpace(checkout.CheckoutId))
        {
            throw new SquareTerminalBackendException(
                CheckoutNotFoundCode,
                "Square checkout was not found.",
                HttpStatusCode.NotFound);
        }

        if (!OriginMatches(checkout, storeCode, deviceCode))
        {
            await AuditAsync(
                AuditOriginRejected,
                storeCode,
                deviceCode,
                normalizedEnvironment,
                checkoutId: checkout.CheckoutId,
                detail: "reference_id/note do not match the calling device",
                cancellationToken: cancellationToken);
            throw new SquareTerminalBackendException(
                OriginMismatchCode,
                "Square checkout does not belong to the calling POS device.",
                HttpStatusCode.Forbidden);
        }

        var paymentIds = checkout.PaymentIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToArray() ?? [];
        await checkoutSessionRepository.UpsertCheckoutSessionAsync(
            new SquareCheckoutSessionRecord
            {
                Environment = checkout.Environment,
                CheckoutId = checkout.CheckoutId,
                Status = checkout.Status ?? string.Empty,
                Amount = checkout.AmountMoney?.Amount,
                Currency = checkout.AmountMoney?.Currency,
                DeviceId = checkout.DeviceId,
                LocationId = checkout.LocationId,
                OriginStoreCode = storeCode,
                OriginDeviceCode = deviceCode,
                PaymentId = paymentIds.FirstOrDefault(),
                PaymentIdsJson = paymentIds.Length == 0 ? null : System.Text.Json.JsonSerializer.Serialize(paymentIds),
                RawCheckoutJson = "{}",
                UpdatedAt = checkout.UpdatedAt ?? _timeProvider.GetUtcNow()
            },
            cancellationToken);

        // 来源只允许首次绑定：已被另一台 POS 绑定就拒绝，不能把别人的结账转到自己名下。
        var bound = await checkoutSessionRepository.BindCheckoutOriginAsync(
            normalizedEnvironment,
            checkout.CheckoutId,
            storeCode,
            deviceCode,
            cancellationToken);
        if (bound is null ||
            !string.Equals(bound.OriginStoreCode, storeCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(bound.OriginDeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase))
        {
            await AuditAsync(
                AuditOriginRejected,
                storeCode,
                deviceCode,
                normalizedEnvironment,
                checkoutId: checkout.CheckoutId,
                detail: "already bound to another device",
                cancellationToken: cancellationToken);
            throw new SquareTerminalBackendException(
                ScopeConflictCode,
                "Square checkout is already bound to a different POS device.",
                HttpStatusCode.Forbidden);
        }

        await AuditAsync(
            AuditOriginRegistered,
            storeCode,
            deviceCode,
            normalizedEnvironment,
            checkoutId: checkout.CheckoutId,
            cancellationToken: cancellationToken);
        return new SquareDirectOriginResponse(checkout.CheckoutId, normalizedEnvironment, storeCode, deviceCode);
    }

    public Task RecordServerRefundAsync(
        string environment,
        string refundId,
        string? paymentId,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = SquareTokenService.NormalizeEnvironment(environment);
        return normalizedEnvironment is null || string.IsNullOrWhiteSpace(refundId)
            ? Task.CompletedTask
            : repository.RecordServerRefundAsync(normalizedEnvironment, refundId.Trim(), paymentId, cancellationToken);
    }

    public async Task CheckRefundNotificationAsync(
        string environment,
        string refundId,
        string? paymentId,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = SquareTokenService.NormalizeEnvironment(environment);
        if (normalizedEnvironment is null || string.IsNullOrWhiteSpace(refundId) || string.IsNullOrWhiteSpace(paymentId))
        {
            return;
        }

        var id = refundId.Trim();
        if (await repository.IsServerRefundAsync(normalizedEnvironment, id, cancellationToken))
        {
            return;
        }

        // 服务端创建退款后才会记下退款号，Square 的通知可能更快到达；先等一会儿再判。
        if (_refundRaceGrace > TimeSpan.Zero)
        {
            await Task.Delay(_refundRaceGrace, _timeProvider, cancellationToken);
        }

        if (await repository.IsServerRefundAsync(normalizedEnvironment, id, cancellationToken))
        {
            return;
        }

        // 只关心「直连门店」的付款：付款对应结账登记的来源门店，开关处于打开状态。
        var session = await checkoutSessionRepository.GetCheckoutSessionByPaymentIdAsync(
            normalizedEnvironment,
            paymentId.Trim(),
            cancellationToken);
        var storeCode = session?.OriginStoreCode;
        if (string.IsNullOrWhiteSpace(storeCode) ||
            !await repository.IsStoreEnabledAsync(storeCode, cancellationToken))
        {
            return;
        }

        // refund.created / refund.updated 会反复通知同一笔退款，只告警一次。
        if (await repository.HasAuditAsync(AuditRefundNotFromServer, id, cancellationToken))
        {
            return;
        }

        await AuditAsync(
            AuditRefundNotFromServer,
            storeCode,
            session?.OriginDeviceCode,
            normalizedEnvironment,
            checkoutId: session?.CheckoutId,
            refundId: id,
            detail: $"paymentId={paymentId.Trim()}",
            cancellationToken: cancellationToken);
        logger.LogError(
            "Square refund {RefundId} on payment {PaymentId} of direct-mode store {StoreCode} was not created by the server. Check whether a POS token was misused.",
            id,
            paymentId.Trim(),
            storeCode);
    }

    private static bool OriginMatches(SquareTerminalCheckoutRecord checkout, string storeCode, string deviceCode)
    {
        // 收银机创建结账时 reference_id = 「设备编号-时间戳」、note = 「HBPOS 门店 设备编号」。
        if (string.IsNullOrWhiteSpace(checkout.ReferenceId) ||
            !checkout.ReferenceId.StartsWith(deviceCode + "-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(checkout.Note) ||
            checkout.Note.Contains(storeCode, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeEnvironmentOrThrow(string environment)
    {
        return SquareTokenService.NormalizeEnvironment(environment)
            ?? throw new SquareTerminalBackendException(
                InvalidEnvironmentCode,
                "Square environment must be Production or Sandbox.",
                HttpStatusCode.BadRequest);
    }

    internal static string Fingerprint(string accessToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(accessToken));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    private Task AuditAsync(
        string eventType,
        string? storeCode,
        string? deviceCode,
        string? environment,
        string? checkoutId = null,
        string? refundId = null,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        return repository.AddAuditAsync(
            new SquareDirectAuditRecord
            {
                EventType = eventType,
                StoreCode = storeCode,
                DeviceCode = deviceCode,
                Environment = environment,
                CheckoutId = checkoutId,
                RefundId = refundId,
                Detail = detail,
                CreatedAt = _timeProvider.GetUtcNow().UtcDateTime
            },
            cancellationToken);
    }
}

public sealed class SquareDirectAuditRecord
{
    public long Id { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string? StoreCode { get; set; }

    public string? DeviceCode { get; set; }

    public string? Environment { get; set; }

    public string? CheckoutId { get; set; }

    public string? RefundId { get; set; }

    public string? Detail { get; set; }

    public DateTime CreatedAt { get; set; }
}

public interface ISquareDirectRepository
{
    Task<bool> IsStoreEnabledAsync(string storeCode, CancellationToken cancellationToken);

    Task AddAuditAsync(SquareDirectAuditRecord audit, CancellationToken cancellationToken);

    Task<int> CountAuditAsync(string eventType, string deviceCode, DateTime sinceUtc, CancellationToken cancellationToken);

    Task<bool> HasAuditAsync(string eventType, string refundId, CancellationToken cancellationToken);

    Task RecordServerRefundAsync(string environment, string refundId, string? paymentId, CancellationToken cancellationToken);

    Task<bool> IsServerRefundAsync(string environment, string refundId, CancellationToken cancellationToken);
}

public sealed class SqlSugarSquareDirectRepository(HbposSqlSugarContext dbContext) : ISquareDirectRepository
{
    public async Task<bool> IsStoreEnabledAsync(string storeCode, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM [dbo].[POSM_SquareDirectStore]
                WHERE [StoreCode] = @StoreCode AND [IsEnabled] = 1
            ) THEN 1 ELSE 0 END AS INT);
            """;
        var value = await dbContext.PosmDb.Ado.GetScalarAsync(sql, new SugarParameter("@StoreCode", storeCode));
        return Convert.ToInt32(value ?? 0) > 0;
    }

    public Task AddAuditAsync(SquareDirectAuditRecord audit, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [dbo].[POSM_SquareDirectAudit]
                ([EventType], [StoreCode], [DeviceCode], [Environment], [CheckoutId], [RefundId], [Detail], [CreatedAt])
            VALUES
                (@EventType, @StoreCode, @DeviceCode, @Environment, @CheckoutId, @RefundId, @Detail, @CreatedAt);
            """;
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@EventType", audit.EventType),
            new SugarParameter("@StoreCode", audit.StoreCode, System.Data.DbType.String),
            new SugarParameter("@DeviceCode", audit.DeviceCode, System.Data.DbType.String),
            new SugarParameter("@Environment", audit.Environment, System.Data.DbType.String),
            new SugarParameter("@CheckoutId", audit.CheckoutId, System.Data.DbType.String),
            new SugarParameter("@RefundId", audit.RefundId, System.Data.DbType.String),
            new SugarParameter("@Detail", audit.Detail, System.Data.DbType.String),
            new SugarParameter("@CreatedAt", audit.CreatedAt));
    }

    public async Task<int> CountAuditAsync(string eventType, string deviceCode, DateTime sinceUtc, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) FROM [dbo].[POSM_SquareDirectAudit]
            WHERE [EventType] = @EventType AND [DeviceCode] = @DeviceCode AND [CreatedAt] >= @Since;
            """;
        var value = await dbContext.PosmDb.Ado.GetScalarAsync(
            sql,
            new SugarParameter("@EventType", eventType),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@Since", sinceUtc));
        return Convert.ToInt32(value ?? 0);
    }

    public async Task<bool> HasAuditAsync(string eventType, string refundId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) FROM [dbo].[POSM_SquareDirectAudit]
            WHERE [EventType] = @EventType AND [RefundId] = @RefundId;
            """;
        var value = await dbContext.PosmDb.Ado.GetScalarAsync(
            sql,
            new SugarParameter("@EventType", eventType),
            new SugarParameter("@RefundId", refundId));
        return Convert.ToInt32(value ?? 0) > 0;
    }

    public Task RecordServerRefundAsync(string environment, string refundId, string? paymentId, CancellationToken cancellationToken)
    {
        // 同一退款号重复记录（幂等重试）不报错：已存在就什么都不做。
        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM [dbo].[POSM_SquareServerRefund] WHERE [Environment] = @Environment AND [RefundId] = @RefundId)
            BEGIN
                INSERT INTO [dbo].[POSM_SquareServerRefund] ([Environment], [RefundId], [PaymentId])
                VALUES (@Environment, @RefundId, @PaymentId);
            END;
            """;
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@RefundId", refundId),
            new SugarParameter("@PaymentId", paymentId, System.Data.DbType.String));
    }

    public async Task<bool> IsServerRefundAsync(string environment, string refundId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) FROM [dbo].[POSM_SquareServerRefund]
            WHERE [Environment] = @Environment AND [RefundId] = @RefundId;
            """;
        var value = await dbContext.PosmDb.Ado.GetScalarAsync(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@RefundId", refundId));
        return Convert.ToInt32(value ?? 0) > 0;
    }
}

/// <summary>
/// 启动时依次建令牌表和直连相关三张表。挂在 ISquareTokenSchemaInitializer 这个注册上，
/// 测试把它整体替换成空实现时直连建表也一并跳过；直连是默认关闭的新功能，
/// 它的建表失败只记日志，绝不能让整个 POS API 起不来。
/// </summary>
public sealed class SquareTokenAndDirectSchemaInitializer(
    SqlSugarSquareTokenSchemaInitializer tokenInitializer,
    SqlSugarSquareDirectSchemaInitializer directInitializer,
    ILogger<SquareTokenAndDirectSchemaInitializer> logger) : ISquareTokenSchemaInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await tokenInitializer.InitializeAsync(cancellationToken);
        try
        {
            await directInitializer.InitializeAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Square direct-mode schema initialization failed; direct mode stays unavailable.");
        }
    }
}

public sealed class SqlSugarSquareDirectSchemaInitializer(
    ISquareTokenSchemaSqlExecutor sqlExecutor)
{
    // 门店开关：没有行 = 关闭。总部（主 API）按门店写入；默认不下发任何令牌。
    internal const string EnsureStoreTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_SquareDirectStore]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_SquareDirectStore] (
                [StoreCode] NVARCHAR(32) NOT NULL CONSTRAINT [PK_POSM_SquareDirectStore] PRIMARY KEY,
                [IsEnabled] BIT NOT NULL CONSTRAINT [DF_POSM_SquareDirectStore_IsEnabled] DEFAULT (0),
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_SquareDirectStore_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedBy] NVARCHAR(128) NULL
            );
        END;
        """;

    // 审计：令牌下发 / 拒绝、结账归属登记 / 拒绝、非服务端退款告警。不存令牌本身，只存指纹。
    internal const string EnsureAuditTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_SquareDirectAudit]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_SquareDirectAudit] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_SquareDirectAudit] PRIMARY KEY,
                [EventType] NVARCHAR(40) NOT NULL,
                [StoreCode] NVARCHAR(32) NULL,
                [DeviceCode] NVARCHAR(64) NULL,
                [Environment] NVARCHAR(32) NULL,
                [CheckoutId] NVARCHAR(128) NULL,
                [RefundId] NVARCHAR(128) NULL,
                [Detail] NVARCHAR(512) NULL,
                [CreatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_SquareDirectAudit_CreatedAt] DEFAULT (SYSUTCDATETIME())
            );
        END;
        """;

    internal const string EnsureAuditIndexSql = """
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = N'IX_POSM_SquareDirectAudit_Type_Device_Time'
              AND object_id = OBJECT_ID(N'[dbo].[POSM_SquareDirectAudit]')
        )
        BEGIN
            CREATE INDEX [IX_POSM_SquareDirectAudit_Type_Device_Time]
            ON [dbo].[POSM_SquareDirectAudit]([EventType], [DeviceCode], [CreatedAt]);
        END;
        """;

    // 服务端自己创建的退款号：webhook 里出现的退款不在此表，就说明不是服务端发起的。
    internal const string EnsureServerRefundTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_SquareServerRefund]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_SquareServerRefund] (
                [Environment] NVARCHAR(32) NOT NULL,
                [RefundId] NVARCHAR(128) NOT NULL,
                [PaymentId] NVARCHAR(128) NULL,
                [CreatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_SquareServerRefund_CreatedAt] DEFAULT (SYSUTCDATETIME()),
                CONSTRAINT [PK_POSM_SquareServerRefund] PRIMARY KEY ([Environment], [RefundId])
            );
        END;
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // 建表与建索引分批执行：同一批里引用刚建的对象会在编译期报错。
        await sqlExecutor.ExecuteAsync(EnsureStoreTableSql, cancellationToken);
        await sqlExecutor.ExecuteAsync(EnsureAuditTableSql, cancellationToken);
        await sqlExecutor.ExecuteAsync(EnsureAuditIndexSql, cancellationToken);
        await sqlExecutor.ExecuteAsync(EnsureServerRefundTableSql, cancellationToken);
    }
}
