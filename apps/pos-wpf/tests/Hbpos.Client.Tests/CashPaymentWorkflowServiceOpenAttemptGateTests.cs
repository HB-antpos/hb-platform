using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

// H2：LocalIp / CloudDirectSync 在授权新的卡扣款前没有任何闸门——本机仍有未结的 Sale（结果未知、已批准未落单……）
// 时，“移至异常中心并开始新单”后可以再次收款，重启后也只有角标。这里在发起扣款前按本机未结队列拦截。
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
    public async Task Unfinished_sale_attempt_blocks_a_new_card_charge_before_the_terminal_is_called(
        LocalCardPaymentAttemptStatus status,
        string attemptMode)
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: true);
        cloudApi.ReleaseApprovedResult();
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var open = CreateAttempt(fixture, status, attemptMode);
        await fixture.AttemptRepository.CreateAsync(open);

        var result = await AddCardTenderAsync(fixture.Workflow, fixture);

        Assert.False(result.Succeeded);
        Assert.Equal(CashPaymentWorkflowService.OpenCardAttemptBlocksPaymentStatusKey, result.StatusKey);
        Assert.Contains("unfinished card transaction", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        // 不是“新交易结果未知”：付款页不上锁，但会提示去异常中心。
        Assert.False(result.CardResult?.RequiresRecovery);
        Assert.Equal(CardPaymentErrorKind.ActiveSessionRequiresRecovery, result.CardResult?.ErrorKind);
        Assert.Equal(0, cloudApi.SendCount);
        var stillOpen = Assert.Single(await OpenAttemptsAsync(fixture));
        Assert.Equal(open.AttemptGuid, stillOpen.AttemptGuid);
    }

    [Fact]
    public async Task Approved_but_unrecorded_active_session_blocks_a_new_card_charge()
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

        Assert.False(result.Succeeded);
        Assert.Equal(CashPaymentWorkflowService.OpenCardAttemptBlocksPaymentStatusKey, result.StatusKey);
        Assert.Equal(0, cloudApi.SendCount);
    }

    [Theory]
    [InlineData(LocalCardPaymentAttemptStatus.Approved, LocalCardPaymentAttemptStatus.OrderCompleted)]
    [InlineData(LocalCardPaymentAttemptStatus.Recovering, LocalCardPaymentAttemptStatus.Abandoned)]
    public async Task Attempt_stuck_in_finalize_pending_blocks_a_new_card_charge(
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

        Assert.False(result.Succeeded);
        Assert.Equal(CashPaymentWorkflowService.OpenCardAttemptBlocksPaymentStatusKey, result.StatusKey);
        Assert.Equal(0, cloudApi.SendCount);
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
    public async Task Cloud_backend_mode_keeps_server_gate_and_takeover_for_cloud_backend_attempts_but_not_for_local_ones()
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

        // 同为 CloudBackendAsync：服务端会话闸门 + 接管流程负责，这里不拦截（保持原有行为）。
        var backendResult = await AddCardTenderAsync(workflow, fixture);
        Assert.True(backendResult.Succeeded);
        Assert.Equal(1, terminal.AuthorizeCount);

        // 换了连接模式后遗留的本地未结记录，服务端看不见，必须本地拦截。
        await fixture.AttemptRepository.CreateAsync(
            CreateAttempt(fixture, LocalCardPaymentAttemptStatus.Recovering, "LocalIp") with
            {
                AttemptGuid = Guid.NewGuid(),
                TxnRef = "P000000000000103"
            });
        var localResult = await AddCardTenderAsync(workflow, fixture);
        Assert.False(localResult.Succeeded);
        Assert.Equal(CashPaymentWorkflowService.OpenCardAttemptBlocksPaymentStatusKey, localResult.StatusKey);
        Assert.Equal(1, terminal.AuthorizeCount);
    }

    [Fact]
    public async Task Unreadable_open_queue_fails_closed_without_calling_the_terminal()
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

        Assert.False(result.Succeeded);
        Assert.Equal(CashPaymentWorkflowService.OpenCardAttemptBlocksPaymentStatusKey, result.StatusKey);
        Assert.Equal(0, terminal.AuthorizeCount);
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

    [Fact]
    public async Task Gate_runs_before_attempt_creation_so_blocked_payments_leave_no_new_attempt()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: true);
        cloudApi.ReleaseApprovedResult();
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        await fixture.AttemptRepository.CreateAsync(
            CreateAttempt(fixture, LocalCardPaymentAttemptStatus.Recovering, "LocalIp"));

        for (var i = 0; i < 3; i++)
        {
            var result = await AddCardTenderAsync(fixture.Workflow, fixture);
            Assert.False(result.Succeeded);
        }

        Assert.Single(await fixture.AttemptRepository.GetRecentAttemptsAsync("S001", "POS-01", "Sandbox"));
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
