using System.Net;
using Hbpos.Api.Services;
using Hbpos.Contracts.Square;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

// 收银机直连 Square（默认关闭，按门店开关）的服务端部分：凭据下发、结账归属登记、非服务端退款告警。
public sealed class SquareDirectServiceTests
{
    private const string Store = "1031";
    private const string Device = "POS_1031_0851";
    private const string SecretToken = "EAAAl-super-secret-token-value";

    // ===== 凭据下发 =====

    [Fact]
    public async Task IssueCredential_is_denied_by_default_when_store_switch_is_off()
    {
        var harness = new Harness();

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            harness.Service.IssueCredentialAsync("Production", Store, Device, CancellationToken.None));

        Assert.Equal("SQUARE_DIRECT_DISABLED", exception.Code);
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal(0, harness.Tokens.Calls);
        Assert.Contains(harness.Repository.Audits, a => a.EventType == "CredentialDenied" && a.DeviceCode == Device);
    }

    [Fact]
    public async Task IssueCredential_returns_store_token_and_audits_without_storing_the_token()
    {
        var harness = new Harness();
        harness.Repository.EnabledStores.Add(Store);

        var credential = await harness.Service.IssueCredentialAsync("production", Store, Device, CancellationToken.None);

        Assert.Equal("Production", credential.Environment);
        Assert.Equal(SecretToken, credential.AccessToken);
        Assert.Equal(harness.Tokens.UpdatedAt.ToUnixTimeMilliseconds(), credential.CredentialVersion);
        Assert.Equal(harness.Now + TimeSpan.FromHours(12), credential.ValidUntil);
        var audit = Assert.Single(harness.Repository.Audits, a => a.EventType == "CredentialIssued");
        Assert.Equal(Store, audit.StoreCode);
        // 审计只记指纹和版本，绝不能出现令牌本身。
        Assert.DoesNotContain(SecretToken, audit.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("fingerprint=", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueCredential_only_serves_enabled_stores()
    {
        var harness = new Harness();
        harness.Repository.EnabledStores.Add("1042");

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            harness.Service.IssueCredentialAsync("Production", Store, Device, CancellationToken.None));

        Assert.Equal("SQUARE_DIRECT_DISABLED", exception.Code);
    }

    [Fact]
    public async Task IssueCredential_rate_limits_a_device_after_ten_requests_in_an_hour()
    {
        var harness = new Harness();
        harness.Repository.EnabledStores.Add(Store);
        for (var i = 0; i < 10; i++)
        {
            await harness.Service.IssueCredentialAsync("Production", Store, Device, CancellationToken.None);
        }

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            harness.Service.IssueCredentialAsync("Production", Store, Device, CancellationToken.None));

        Assert.Equal("SQUARE_DIRECT_RATE_LIMITED", exception.Code);
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        // 另一台设备不受影响。
        var other = await harness.Service.IssueCredentialAsync("Production", Store, "POS_1031_0900", CancellationToken.None);
        Assert.Equal(SecretToken, other.AccessToken);
    }

    [Fact]
    public async Task IssueCredential_rejects_unknown_environment()
    {
        var harness = new Harness();
        harness.Repository.EnabledStores.Add(Store);

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            harness.Service.IssueCredentialAsync("Staging", Store, Device, CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [Fact]
    public async Task IssueCredential_reports_missing_token_as_not_found()
    {
        var harness = new Harness { NoToken = true };
        harness.Repository.EnabledStores.Add(Store);

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            harness.Service.IssueCredentialAsync("Production", Store, Device, CancellationToken.None));

        Assert.Equal("SQUARE_TOKEN_NOT_CONFIGURED", exception.Code);
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    // ===== 结账归属登记 =====

    [Fact]
    public async Task RegisterOrigin_binds_checkout_after_square_confirms_it_belongs_to_the_device()
    {
        var harness = new Harness();
        harness.Repository.EnabledStores.Add(Store);
        harness.Rest.Checkouts["chk-1"] = Checkout("chk-1", $"{Device}-20261020100000123", $"HBPOS {Store} {Device}");

        var response = await harness.Service.RegisterOriginAsync("Production", "chk-1", Store, Device, CancellationToken.None);

        Assert.Equal(Device, response.DeviceCode);
        var session = Assert.Single(harness.Sessions.Sessions.Values);
        Assert.Equal(Store, session.OriginStoreCode);
        Assert.Equal(Device, session.OriginDeviceCode);
        Assert.Equal("PENDING", session.Status);
        Assert.Contains(harness.Repository.Audits, a => a.EventType == "OriginRegistered" && a.CheckoutId == "chk-1");
        // 核实用的是服务端自己的令牌，不是收银机上报的任何东西。
        Assert.Equal(SecretToken, harness.Rest.LastAccessToken);
    }

    [Fact]
    public async Task RegisterOrigin_is_idempotent_for_the_same_device()
    {
        var harness = new Harness();
        harness.Repository.EnabledStores.Add(Store);
        harness.Rest.Checkouts["chk-1"] = Checkout("chk-1", $"{Device}-1", $"HBPOS {Store} {Device}");

        await harness.Service.RegisterOriginAsync("Production", "chk-1", Store, Device, CancellationToken.None);
        var second = await harness.Service.RegisterOriginAsync("Production", "chk-1", Store, Device, CancellationToken.None);

        Assert.Equal("chk-1", second.CheckoutId);
        Assert.Single(harness.Sessions.Sessions);
    }

    [Fact]
    public async Task RegisterOrigin_rejects_a_checkout_whose_reference_belongs_to_another_device()
    {
        var harness = new Harness();
        harness.Repository.EnabledStores.Add(Store);
        harness.Rest.Checkouts["chk-1"] = Checkout("chk-1", "POS_1042_0001-20261020100000123", "HBPOS 1042 POS_1042_0001");

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            harness.Service.RegisterOriginAsync("Production", "chk-1", Store, Device, CancellationToken.None));

        Assert.Equal("SQUARE_DIRECT_ORIGIN_MISMATCH", exception.Code);
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Empty(harness.Sessions.Sessions);
        Assert.Contains(harness.Repository.Audits, a => a.EventType == "OriginRejected");
    }

    [Fact]
    public async Task RegisterOrigin_rejects_a_checkout_without_reference_id()
    {
        var harness = new Harness();
        harness.Repository.EnabledStores.Add(Store);
        harness.Rest.Checkouts["chk-1"] = Checkout("chk-1", referenceId: null, note: null);

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            harness.Service.RegisterOriginAsync("Production", "chk-1", Store, Device, CancellationToken.None));

        Assert.Equal("SQUARE_DIRECT_ORIGIN_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task RegisterOrigin_rejects_when_note_names_a_different_store()
    {
        var harness = new Harness();
        harness.Repository.EnabledStores.Add(Store);
        harness.Rest.Checkouts["chk-1"] = Checkout("chk-1", $"{Device}-1", "HBPOS 1042 SOMEONE_ELSE");

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            harness.Service.RegisterOriginAsync("Production", "chk-1", Store, Device, CancellationToken.None));

        Assert.Equal("SQUARE_DIRECT_ORIGIN_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task RegisterOrigin_cannot_steal_a_checkout_already_bound_to_another_device()
    {
        var harness = new Harness();
        harness.Repository.EnabledStores.Add(Store);
        harness.Rest.Checkouts["chk-1"] = Checkout("chk-1", $"{Device}-1", $"HBPOS {Store} {Device}");
        harness.Sessions.Sessions["Production::chk-1"] = new SquareCheckoutSessionRecord
        {
            Environment = "Production",
            CheckoutId = "chk-1",
            Status = "PENDING",
            RawCheckoutJson = "{}",
            OriginStoreCode = "1042",
            OriginDeviceCode = "POS_1042_0001"
        };

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            harness.Service.RegisterOriginAsync("Production", "chk-1", Store, Device, CancellationToken.None));

        Assert.Equal("SQUARE_CHECKOUT_SCOPE_CONFLICT", exception.Code);
        Assert.Equal("POS_1042_0001", harness.Sessions.Sessions["Production::chk-1"].OriginDeviceCode);
    }

    [Fact]
    public async Task RegisterOrigin_requires_the_store_switch()
    {
        var harness = new Harness();
        harness.Rest.Checkouts["chk-1"] = Checkout("chk-1", $"{Device}-1", $"HBPOS {Store} {Device}");

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            harness.Service.RegisterOriginAsync("Production", "chk-1", Store, Device, CancellationToken.None));

        Assert.Equal("SQUARE_DIRECT_DISABLED", exception.Code);
        Assert.Equal(0, harness.Rest.GetCheckoutCalls);
    }

    [Fact]
    public async Task RegisterOrigin_reports_unknown_checkout_as_not_found()
    {
        var harness = new Harness();
        harness.Repository.EnabledStores.Add(Store);

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            harness.Service.RegisterOriginAsync("Production", "missing", Store, Device, CancellationToken.None));

        Assert.Equal("SQUARE_DIRECT_CHECKOUT_NOT_FOUND", exception.Code);
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    // ===== 退款对账告警 =====

    [Fact]
    public async Task Refund_created_by_the_server_does_not_alert()
    {
        var harness = DirectStoreWithPayment();
        await harness.Service.RecordServerRefundAsync("Production", "ref-1", "pay-1", CancellationToken.None);

        await harness.Service.CheckRefundNotificationAsync("Production", "ref-1", "pay-1", CancellationToken.None);

        Assert.DoesNotContain(harness.Repository.Audits, a => a.EventType == "RefundNotFromServer");
        Assert.Empty(harness.Logger.Errors);
    }

    [Fact]
    public async Task Refund_not_created_by_the_server_on_a_direct_store_payment_alerts_once()
    {
        var harness = DirectStoreWithPayment();

        await harness.Service.CheckRefundNotificationAsync("Production", "ref-x", "pay-1", CancellationToken.None);
        // refund.created 之后还有 refund.updated：同一笔退款只告警一次。
        await harness.Service.CheckRefundNotificationAsync("Production", "ref-x", "pay-1", CancellationToken.None);

        var alert = Assert.Single(harness.Repository.Audits, a => a.EventType == "RefundNotFromServer");
        Assert.Equal(Store, alert.StoreCode);
        Assert.Equal(Device, alert.DeviceCode);
        Assert.Equal("ref-x", alert.RefundId);
        Assert.Single(harness.Logger.Errors);
    }

    [Fact]
    public async Task Refund_on_a_store_without_the_direct_switch_is_ignored()
    {
        var harness = DirectStoreWithPayment();
        harness.Repository.EnabledStores.Clear();

        await harness.Service.CheckRefundNotificationAsync("Production", "ref-x", "pay-1", CancellationToken.None);

        Assert.Empty(harness.Repository.Audits);
    }

    [Fact]
    public async Task Refund_on_an_unknown_payment_is_ignored()
    {
        var harness = new Harness();
        harness.Repository.EnabledStores.Add(Store);

        await harness.Service.CheckRefundNotificationAsync("Production", "ref-x", "pay-unknown", CancellationToken.None);

        Assert.Empty(harness.Repository.Audits);
    }

    // ===== 工具 =====

    private static Harness DirectStoreWithPayment()
    {
        var harness = new Harness();
        harness.Repository.EnabledStores.Add(Store);
        harness.Sessions.Sessions["Production::chk-1"] = new SquareCheckoutSessionRecord
        {
            Environment = "Production",
            CheckoutId = "chk-1",
            Status = "COMPLETED",
            RawCheckoutJson = "{}",
            PaymentId = "pay-1",
            OriginStoreCode = Store,
            OriginDeviceCode = Device
        };
        return harness;
    }

    private static SquareTerminalCheckoutRecord Checkout(string id, string? referenceId, string? note) =>
        new(id, "Production", Status: "PENDING", DeviceId: "DEV-1", LocationId: "LOC-1",
            AmountMoney: new SquareMoneyDto(1347, "AUD"), ReferenceId: referenceId, Note: note);

    private sealed class Harness
    {
        public Harness()
        {
            Repository = new InMemoryDirectRepository();
            Tokens = new FakeTokenService(this);
            Rest = new FakeRestClient();
            Sessions = new InMemorySessions();
            Logger = new CapturingLogger();
            Service = new SquareDirectService(
                Repository,
                Tokens,
                Rest,
                Sessions,
                Logger,
                new FixedTimeProvider(Now),
                refundRaceGrace: TimeSpan.Zero);
        }

        public DateTimeOffset Now { get; } = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

        public bool NoToken { get; set; }

        public InMemoryDirectRepository Repository { get; }

        public FakeTokenService Tokens { get; }

        public FakeRestClient Rest { get; }

        public InMemorySessions Sessions { get; }

        public CapturingLogger Logger { get; }

        public SquareDirectService Service { get; }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeTokenService(Harness harness) : ISquareTokenService
    {
        public int Calls { get; private set; }

        public DateTimeOffset UpdatedAt { get; } = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        public Task<SquareTokenResponse?> GetActiveTokenAsync(string environment, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<SquareTokenResponse?>(
                harness.NoToken ? null : new SquareTokenResponse(environment, SecretToken, UpdatedAt));
        }
    }

    /// <summary>只实现 GetCheckoutAsync，其余方法被调用就说明直连登记误用了别的 Square 接口。</summary>
    private sealed class FakeRestClient : ISquareTerminalRestClient
    {
        public Dictionary<string, SquareTerminalCheckoutRecord> Checkouts { get; } = new(StringComparer.Ordinal);

        public int GetCheckoutCalls { get; private set; }

        public string? LastAccessToken { get; private set; }

        public Task<SquareTerminalCheckoutRecord?> GetCheckoutAsync(
            string environment,
            string accessToken,
            string checkoutId,
            CancellationToken cancellationToken)
        {
            GetCheckoutCalls++;
            LastAccessToken = accessToken;
            return Task.FromResult(Checkouts.GetValueOrDefault(checkoutId));
        }

        public Task<IReadOnlyList<SquareLocationDto>> GetLocationsAsync(string environment, string accessToken, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<IReadOnlyList<SquareDeviceDto>> GetDevicesAsync(string environment, string accessToken, string locationId, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<IReadOnlyList<SquareDeviceCodeDto>> GetDeviceCodesAsync(string environment, string accessToken, string locationId, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<SquareDeviceCodeDto> CreateDeviceCodeAsync(string environment, string accessToken, SquareCreateDeviceCodeRequest request, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<SquareDeviceCodeDto?> GetDeviceCodeAsync(string environment, string accessToken, string deviceCodeId, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<SquareTerminalCheckoutRecord> CreateCheckoutAsync(string environment, string accessToken, SquareCreateCheckoutRequest request, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<SquareTerminalCheckoutRecord> CancelCheckoutAsync(string environment, string accessToken, string checkoutId, SquareCheckoutActionRequest request, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<SquareTerminalCheckoutRecord> DismissCheckoutAsync(string environment, string accessToken, string checkoutId, SquareCheckoutActionRequest request, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<SquarePaymentStatusDto?> GetPaymentAsync(string environment, string accessToken, string paymentId, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<SquareRefundResponse> CreateRefundAsync(string environment, string accessToken, SquareRefundRequest request, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<SquareRefundResponse?> GetRefundAsync(string environment, string accessToken, string refundId, CancellationToken cancellationToken) =>
            throw Unexpected();

        private static NotSupportedException Unexpected() => new("direct registration must only read the checkout");
    }

    private sealed class InMemoryDirectRepository : ISquareDirectRepository
    {
        private readonly HashSet<string> _serverRefunds = [];

        public HashSet<string> EnabledStores { get; } = [];

        public List<SquareDirectAuditRecord> Audits { get; } = [];

        public Task<bool> IsStoreEnabledAsync(string storeCode, CancellationToken cancellationToken) =>
            Task.FromResult(EnabledStores.Contains(storeCode));

        public Task AddAuditAsync(SquareDirectAuditRecord audit, CancellationToken cancellationToken)
        {
            Audits.Add(audit);
            return Task.CompletedTask;
        }

        public Task<int> CountAuditAsync(string eventType, string deviceCode, DateTime sinceUtc, CancellationToken cancellationToken) =>
            Task.FromResult(Audits.Count(a => a.EventType == eventType && a.DeviceCode == deviceCode && a.CreatedAt >= sinceUtc));

        public Task<bool> HasAuditAsync(string eventType, string refundId, CancellationToken cancellationToken) =>
            Task.FromResult(Audits.Any(a => a.EventType == eventType && a.RefundId == refundId));

        public Task RecordServerRefundAsync(string environment, string refundId, string? paymentId, CancellationToken cancellationToken)
        {
            _serverRefunds.Add($"{environment}::{refundId}");
            return Task.CompletedTask;
        }

        public Task<bool> IsServerRefundAsync(string environment, string refundId, CancellationToken cancellationToken) =>
            Task.FromResult(_serverRefunds.Contains($"{environment}::{refundId}"));
    }

    private sealed class InMemorySessions : ISquareCheckoutSessionRepository
    {
        public Dictionary<string, SquareCheckoutSessionRecord> Sessions { get; } = new(StringComparer.Ordinal);

        public Task<bool> TryAddWebhookEventAsync(SquareWebhookEventRecord webhookEvent, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task UpsertCheckoutSessionAsync(SquareCheckoutSessionRecord session, CancellationToken cancellationToken)
        {
            var key = $"{session.Environment}::{session.CheckoutId}";
            if (Sessions.TryGetValue(key, out var existing))
            {
                // 模拟生产 MERGE：已有来源不被覆盖。
                session.OriginStoreCode = existing.OriginStoreCode ?? session.OriginStoreCode;
                session.OriginDeviceCode = existing.OriginDeviceCode ?? session.OriginDeviceCode;
            }

            Sessions[key] = session;
            return Task.CompletedTask;
        }

        public Task<bool> RefreshCheckoutSessionFromLookupAsync(
            SquareCheckoutSessionRecord session,
            string originStoreCode,
            string originDeviceCode,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<SquareCheckoutSessionRecord?> BindCheckoutOriginAsync(
            string environment,
            string checkoutId,
            string originStoreCode,
            string originDeviceCode,
            CancellationToken cancellationToken)
        {
            Sessions.TryGetValue($"{environment}::{checkoutId}", out var session);
            if (session is not null)
            {
                session.OriginStoreCode ??= originStoreCode;
                session.OriginDeviceCode ??= originDeviceCode;
            }

            return Task.FromResult(session);
        }

        public Task<SquareCheckoutSessionRecord?> GetCheckoutSessionAsync(
            string environment,
            string checkoutId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Sessions.GetValueOrDefault($"{environment}::{checkoutId}"));

        public Task<SquareCheckoutSessionRecord?> GetCheckoutSessionByPaymentIdAsync(
            string environment,
            string paymentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Sessions.Values.FirstOrDefault(s => s.Environment == environment && s.PaymentId == paymentId));
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<SquareDirectService>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}

public sealed class SquareDirectSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_creates_switch_audit_and_server_refund_tables_without_touching_token_table()
    {
        var executor = new CapturingExecutor();
        var initializer = new SqlSugarSquareDirectSchemaInitializer(executor);

        await initializer.InitializeAsync();

        var combined = string.Join(Environment.NewLine, executor.Statements);
        Assert.Equal(4, executor.Statements.Count);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_SquareDirectStore]', N'U') IS NULL", combined);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_SquareDirectAudit]', N'U') IS NULL", combined);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_SquareServerRefund]', N'U') IS NULL", combined);
        // 默认关闭：开关列默认 0，没有行也等于关闭。
        Assert.Contains("[IsEnabled] BIT NOT NULL CONSTRAINT [DF_POSM_SquareDirectStore_IsEnabled] DEFAULT (0)", combined);
        Assert.DoesNotContain("DROP TABLE", combined, StringComparison.OrdinalIgnoreCase);
        // 只新建三张表，不碰令牌表。
        Assert.DoesNotContain("[dbo].[POSM_SquareToken]", combined, StringComparison.Ordinal);
        // 建表在前、索引在后，各自成批。
        var tableIndex = executor.Statements.FindIndex(sql => sql.Contains("CREATE TABLE [dbo].[POSM_SquareDirectAudit]"));
        var indexIndex = executor.Statements.FindIndex(sql => sql.Contains("CREATE INDEX [IX_POSM_SquareDirectAudit_Type_Device_Time]"));
        Assert.True(tableIndex >= 0 && indexIndex > tableIndex);
    }

    [Fact]
    public async Task Startup_initializer_creates_token_tables_first_then_direct_tables()
    {
        var executor = new CapturingExecutor();
        var initializer = new SquareTokenAndDirectSchemaInitializer(
            new SqlSugarSquareTokenSchemaInitializer(executor),
            new SqlSugarSquareDirectSchemaInitializer(executor),
            NullLogger<SquareTokenAndDirectSchemaInitializer>.Instance);

        await initializer.InitializeAsync();

        Assert.Equal(8, executor.Statements.Count);
        Assert.Contains("POSM_SquareToken", executor.Statements[0]);
        Assert.Contains("POSM_SquareDirectStore", executor.Statements[4]);
    }

    [Fact]
    public async Task Startup_initializer_does_not_fail_startup_when_direct_ddl_fails()
    {
        var executor = new CapturingExecutor { FailWhenContains = "POSM_SquareDirectStore" };
        var initializer = new SquareTokenAndDirectSchemaInitializer(
            new SqlSugarSquareTokenSchemaInitializer(executor),
            new SqlSugarSquareDirectSchemaInitializer(executor),
            NullLogger<SquareTokenAndDirectSchemaInitializer>.Instance);

        // 令牌表照常建好；直连是默认关闭的新功能，它建表失败不能拖垮整个 POS API。
        await initializer.InitializeAsync();

        Assert.Equal(5, executor.Statements.Count);
    }

    [Fact]
    public async Task Startup_initializer_still_fails_startup_when_token_ddl_fails()
    {
        var executor = new CapturingExecutor { FailWhenContains = "POSM_SquareToken" };
        var initializer = new SquareTokenAndDirectSchemaInitializer(
            new SqlSugarSquareTokenSchemaInitializer(executor),
            new SqlSugarSquareDirectSchemaInitializer(executor),
            NullLogger<SquareTokenAndDirectSchemaInitializer>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync());
    }

    private sealed class CapturingExecutor : ISquareTokenSchemaSqlExecutor
    {
        public List<string> Statements { get; } = [];

        public string? FailWhenContains { get; init; }

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            Statements.Add(sql);
            return FailWhenContains is not null && sql.Contains(FailWhenContains, StringComparison.Ordinal)
                ? Task.FromException(new InvalidOperationException("ddl failed"))
                : Task.CompletedTask;
        }
    }
}
