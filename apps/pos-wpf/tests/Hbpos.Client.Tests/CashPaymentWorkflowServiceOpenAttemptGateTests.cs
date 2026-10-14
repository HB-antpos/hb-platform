using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

// 业务要求（2026-10-14）：无论本机还有多少未结的 Linkly 记录（结果未知、已批准未落单、终态化卡住……），
// 都不能阻止下一单发起付款请求。10-02 刷卡机离线遗留的两条记录曾让 POS_1013_0222 连续多日无法刷卡。
// 旧的“新扣款闸门”（H2）已改为只记日志；未结记录仍留在卡交易异常中心，由主管或自动恢复处理。
public sealed class CashPaymentWorkflowServiceOpenAttemptGateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-05T10:00:00+10:00");

    [Theory]
    [InlineData(LocalCardPaymentAttemptStatus.Pending, "LocalIp")]
    [InlineData(LocalCardPaymentAttemptStatus.SessionStarted, "CloudDirectSync")]
    [InlineData(LocalCardPaymentAttemptStatus.Recovering, "LocalIp")]
    [InlineData(LocalCardPaymentAttemptStatus.RequiresReview, "CloudDirectSync")]
    [InlineData(LocalCardPaymentAttemptStatus.Approved, "LocalIp")]
    [InlineData(LocalCardPaymentAttemptStatus.Recovering, "CloudBackendAsync")]
    public async Task Unfinished_sale_attempt_never_blocks_a_new_card_charge(
        LocalCardPaymentAttemptStatus status,
        string attemptMode)
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: true);
        cloudApi.ReleaseApprovedResult();
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var open = CreateAttempt(fixture, status, attemptMode);
        await fixture.AttemptRepository.CreateAsync(open);

        var result = await AddCardTenderAsync(fixture.Workflow, fixture);

        Assert.True(result.Succeeded);
        Assert.NotEqual(CashPaymentWorkflowService.OpenCardAttemptBlocksPaymentStatusKey, result.StatusKey);
        Assert.Equal(1, cloudApi.SendCount);
        // 旧的未结记录原样保留在异常中心，没有被这次付款改动或悄悄结案。
        var stillOpen = Assert.Single(await OpenAttemptsAsync(fixture), a => a.AttemptGuid == open.AttemptGuid);
        Assert.Equal(status, stillOpen.Status);
    }

    [Fact]
    public async Task Approved_but_unrecorded_active_session_never_blocks_a_new_card_charge()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: true);
        cloudApi.ReleaseApprovedResult();
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var orphan = CreateAttempt(fixture, LocalCardPaymentAttemptStatus.Approved, "CloudDirectSync") with
        {
            OperationKind = "ActiveSession",
            OperationGuid = Guid.NewGuid(),
            OrderDraftJson = "{}",
            Amount = 0m,
            AcknowledgedAt = Now
        };
        await fixture.AttemptRepository.CreateOrGetActiveSessionAsync(orphan);

        var result = await AddCardTenderAsync(fixture.Workflow, fixture);

        Assert.True(result.Succeeded);
        Assert.Equal(1, cloudApi.SendCount);
    }

    [Theory]
    [InlineData(LocalCardPaymentAttemptStatus.Approved, LocalCardPaymentAttemptStatus.OrderCompleted)]
    [InlineData(LocalCardPaymentAttemptStatus.Recovering, LocalCardPaymentAttemptStatus.Abandoned)]
    public async Task Attempt_stuck_in_finalize_pending_never_blocks_a_new_card_charge(
        LocalCardPaymentAttemptStatus status,
        LocalCardPaymentAttemptStatus target)
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: true);
        cloudApi.ReleaseApprovedResult();
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        await fixture.AttemptRepository.CreateAsync(CreateAttempt(fixture, status, "LocalIp") with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = target.ToString()
        });

        var result = await AddCardTenderAsync(fixture.Workflow, fixture);

        Assert.True(result.Succeeded);
        Assert.Equal(1, cloudApi.SendCount);
    }

    [Theory]
    [InlineData(LocalCardPaymentAttemptStatus.Declined)]
    [InlineData(LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData(LocalCardPaymentAttemptStatus.TimedOut)]
    [InlineData(LocalCardPaymentAttemptStatus.Failed)]
    [InlineData(LocalCardPaymentAttemptStatus.OrderCompleted)]
    [InlineData(LocalCardPaymentAttemptStatus.Abandoned)]
    public async Task Settled_attempts_do_not_block_a_new_card_charge(LocalCardPaymentAttemptStatus status)
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: true);
        cloudApi.ReleaseApprovedResult();
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        await fixture.AttemptRepository.CreateAsync(CreateAttempt(fixture, status, "CloudDirectSync") with
        {
            CompletedAt = Now,
            AcknowledgedAt = Now
        });

        var result = await AddCardTenderAsync(fixture.Workflow, fixture);

        Assert.True(result.Succeeded);
        Assert.Equal(1, cloudApi.SendCount);
    }

    [Fact]
    public async Task Open_attempt_of_another_device_or_environment_does_not_block()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: true);
        cloudApi.ReleaseApprovedResult();
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        await fixture.AttemptRepository.CreateAsync(
            CreateAttempt(fixture, LocalCardPaymentAttemptStatus.Recovering, "LocalIp") with
            {
                AttemptGuid = Guid.NewGuid(),
                DeviceCode = "POS-OTHER",
                TxnRef = "P000000000000101"
            });
        await fixture.AttemptRepository.CreateAsync(
            CreateAttempt(fixture, LocalCardPaymentAttemptStatus.Recovering, "LocalIp") with
            {
                AttemptGuid = Guid.NewGuid(),
                Environment = "Production",
                TxnRef = "P000000000000102"
            });

        var result = await AddCardTenderAsync(fixture.Workflow, fixture);

        Assert.True(result.Succeeded);
        Assert.Equal(1, cloudApi.SendCount);
    }

    [Fact]
    public async Task Cloud_backend_mode_does_not_block_on_either_cloud_backend_or_leftover_local_attempts()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: true);
        cloudApi.ReleaseApprovedResult();
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var backendSettings = fixture.Settings with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync };
        var terminal = new CountingCardTerminalClient();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            fixture.OrderRepository,
            fixture.SyncQueueRepository,
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: fixture.AttemptRepository,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(backendSettings));
        await fixture.AttemptRepository.CreateAsync(
            CreateAttempt(fixture, LocalCardPaymentAttemptStatus.Recovering, "CloudBackendAsync") with
            {
                SessionId = "SESSION-BACKEND-OPEN"
            });

        // 同为 CloudBackendAsync：客户端不拦截（服务端会话闸门与接管流程另行负责）。
        var backendResult = await AddCardTenderAsync(workflow, fixture);
        Assert.True(backendResult.Succeeded);
        Assert.Equal(1, terminal.AuthorizeCount);

        // 换了连接模式后遗留的本地未结记录：客户端同样不再拦截下一单。
        await fixture.AttemptRepository.CreateAsync(
            CreateAttempt(fixture, LocalCardPaymentAttemptStatus.Recovering, "LocalIp") with
            {
                AttemptGuid = Guid.NewGuid(),
                TxnRef = "P000000000000103"
            });
        var localResult = await AddCardTenderAsync(workflow, fixture);
        Assert.True(localResult.Succeeded);
        Assert.Equal(2, terminal.AuthorizeCount);
    }

    [Fact]
    public async Task Unreadable_open_queue_does_not_block_the_charge()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: true);
        cloudApi.ReleaseApprovedResult();
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var terminal = new CountingCardTerminalClient();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            fixture.OrderRepository,
            fixture.SyncQueueRepository,
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: new UnreadableQueueAttemptRepository(),
            cardTerminalSettingsProvider: fixture.SettingsProvider);

        var result = await AddCardTenderAsync(workflow, fixture);

        // 读不到未结队列只是少一条日志，绝不能因此拒绝付款。
        Assert.True(result.Succeeded);
        Assert.Equal(1, terminal.AuthorizeCount);
    }

    [Fact]
    public async Task Repository_without_an_open_queue_does_not_block_the_charge()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: true);
        cloudApi.ReleaseApprovedResult();
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var terminal = new CountingCardTerminalClient();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            fixture.OrderRepository,
            fixture.SyncQueueRepository,
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: new UnreadableQueueAttemptRepository(unsupported: true),
            cardTerminalSettingsProvider: fixture.SettingsProvider);

        var result = await AddCardTenderAsync(workflow, fixture);

        Assert.True(result.Succeeded);
        Assert.Equal(1, terminal.AuthorizeCount);
    }

    private static Task<PaymentTenderAttemptResult> AddCardTenderAsync(
        ICashPaymentWorkflowService workflow,
        PaymentFlowTestFixture fixture)
    {
        var cart = fixture.CreateSaleCart();
        return workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            fixture.Session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());
    }

    private static Task<IReadOnlyList<LocalCardPaymentAttempt>> OpenAttemptsAsync(PaymentFlowTestFixture fixture) =>
        fixture.AttemptRepository.GetOpenAttemptsAsync("S001", "POS-01", "Sandbox");

    private static LocalCardPaymentAttempt CreateAttempt(
        PaymentFlowTestFixture fixture,
        LocalCardPaymentAttemptStatus status,
        string connectionMode) =>
        new(
            Guid.NewGuid(),
            SessionId: connectionMode == "LocalIp" ? null : "SESSION-OPEN-001",
            TxnRef: "P000000000000100",
            Processor: "Linkly",
            Environment: "Sandbox",
            ConnectionMode: connectionMode,
            TxnType: "P",
            Amount: 10m,
            Status: status,
            OrderDraftJson: "{}",
            StoreCode: fixture.Session.StoreCode,
            DeviceCode: fixture.Session.DeviceCode,
            CashierId: fixture.Session.CashierId,
            ResponseCode: null,
            ResponseText: null,
            PaymentReference: null,
            CreatedAt: Now.AddMinutes(-2),
            UpdatedAt: Now.AddMinutes(-1),
            CompletedAt: null,
            AcknowledgedAt: null);

    private sealed class CountingCardTerminalClient : ICardTerminalClient
    {
        public int AuthorizeCount { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            AuthorizeCount++;
            return Task.FromResult(new PaymentAuthorizationResult(
                true,
                "ANZ:P000000000000555",
                AuthorizedAmount: amount));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnreadableQueueAttemptRepository(bool unsupported = false) : ILocalCardPaymentAttemptRepository
    {
        public Task CreateAsync(LocalCardPaymentAttempt attempt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateSessionAsync(
            Guid attemptGuid,
            string sessionId,
            string? txnRef,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateOutcomeAsync(
            Guid attemptGuid,
            LocalCardPaymentAttemptStatus status,
            string? responseCode,
            string? responseText,
            string? paymentReference,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkOrderCompletedAsync(
            Guid attemptGuid,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkAcknowledgedAsync(
            Guid attemptGuid,
            DateTimeOffset acknowledgedAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkRecoveringAsync(
            Guid attemptGuid,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<LocalCardPaymentAttempt?> GetLatestOpenAttemptAsync(
            string storeCode,
            string deviceCode,
            string? cashierId,
            string environment,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalCardPaymentAttempt?>(null);

        public Task<IReadOnlyList<LocalCardPaymentAttempt>> GetOpenRefundAttemptsAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalCardPaymentAttempt>>([]);

        public Task<IReadOnlyList<LocalCardPaymentAttempt>> GetOpenAttemptsAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken = default) =>
            unsupported
                ? throw new NotSupportedException("Open attempt queue is not wired for this repository.")
                : throw new InvalidOperationException("The local card attempt store is unreadable.");

        public Task<LocalCardPaymentAttempt?> GetAttemptAsync(
            Guid attemptGuid,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalCardPaymentAttempt?>(null);
    }
}
