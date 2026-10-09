using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

// H2：收银员登录/启动恢复时，对“确定性”记录（已批准待建单、待收尾、已完成未 ack）自动定点恢复。
// 前提是不破坏“不自动重发”：自动恢复不查询刷卡机、不发起任何交易，结果未知的记录仍留给异常中心。
public sealed partial class CardPaymentRecoveryServiceTests
{
    private const string AutoLocalTxnRef = "P000000000000911";

    private static PaymentAuthorizationResult AutoRecoveryTerminalResult(
        string txnRef,
        bool approved,
        string responseCode,
        string responseText) =>
        new(
            approved,
            $"ANZ:{txnRef}",
            "ANZ Linkly",
            10m,
            [CreateLocalCardTransaction(txnRef, responseCode, responseText)],
            "ANZ",
            "Sandbox",
            LinklyConnectionMode.LocalIp.ToString(),
            "P",
            null,
            txnRef,
            responseCode,
            responseText);

    private static CardPaymentRecoveryService CreateAutoRecoveryService(
        PaymentFlowTestFixture fixture,
        FakeLinklyBackendTerminalClient backend,
        ILinklyTerminalClient? localTerminal = null,
        ICardTerminalSettingsProvider? settingsProvider = null)
    {
        return new CardPaymentRecoveryService(
            fixture.AttemptRepository,
            settingsProvider ?? fixture.SettingsProvider,
            backend,
            new CashCheckoutService(),
            fixture.OrderRepository,
            fixture.SyncQueueRepository,
            linklyTerminalClient: localTerminal,
            cloudTerminalClient: fixture.CloudClient);
    }

    private static LocalCardPaymentAttempt CreateApprovedLocalAttempt(
        string txnRef,
        LinklyConnectionMode mode = LinklyConnectionMode.LocalIp)
    {
        return CreateAttempt(
            sessionId: mode == LinklyConnectionMode.CloudDirectSync ? $"SESSION-{txnRef}" : null,
            txnRef: txnRef,
            status: LocalCardPaymentAttemptStatus.Approved,
            connectionMode: mode) with
        {
            AttemptGuid = Guid.NewGuid(),
            ResponseCode = "00",
            ResponseText = "APPROVED",
            PaymentReference = $"ANZ:{txnRef}"
        };
    }

    [Theory]
    [InlineData(LinklyConnectionMode.LocalIp)]
    [InlineData(LinklyConnectionMode.CloudDirectSync)]
    public async Task AutoRecoverDeterministicAsync_builds_the_order_for_an_approved_attempt_without_asking_the_terminal(
        LinklyConnectionMode mode)
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: false);
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var attempt = CreateApprovedLocalAttempt(AutoLocalTxnRef, mode);
        await fixture.AttemptRepository.CreateAsync(attempt);
        var terminal = new FakeLinklyTerminalClient(AutoRecoveryTerminalResult(AutoLocalTxnRef, true, "00", "APPROVED"));
        var backend = new FakeLinklyBackendTerminalClient();
        var service = CreateAutoRecoveryService(fixture, backend, terminal);

        var summary = await service.AutoRecoverDeterministicAsync(fixture.Session);

        Assert.Equal(1, summary.Examined);
        Assert.Equal(1, summary.RecoveredOrders);
        var order = Assert.Single(await fixture.OrderRepository.GetRecentOrdersAsync());
        var saved = await fixture.OrderRepository.GetOrderAsync(order.OrderGuid);
        var payment = Assert.Single(saved!.Payments);
        Assert.Equal($"CARD_ATTEMPT:{attempt.AttemptGuid:N}", payment.IdempotencyKey);
        Assert.Equal(
            LocalCardPaymentAttemptStatus.OrderCompleted,
            (await fixture.AttemptRepository.GetAttemptAsync(attempt.AttemptGuid))!.Status);
        // 不破坏“不自动重发”：没有任何终端/Linkly 查询，更没有新的扣款提交。
        Assert.Equal(0, terminal.RecoverCallCount);
        Assert.Equal(0, cloudApi.GetTransactionCount);
        Assert.Equal(0, cloudApi.SendCount);
        Assert.Equal(0, backend.StatusCallCount);
        Assert.Equal(0, backend.PurchaseCallCount);

        // 已经收尾的记录不会再次处理，也不会重复建单。
        var again = await service.AutoRecoverDeterministicAsync(fixture.Session);
        Assert.Equal(0, again.Examined);
        Assert.Single(await fixture.OrderRepository.GetRecentOrdersAsync());
    }

    [Fact]
    public async Task AutoRecoverDeterministicAsync_finishes_a_finalize_pending_approved_attempt()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: false);
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var attempt = CreateApprovedLocalAttempt(AutoLocalTxnRef) with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalCardPaymentAttemptStatus.OrderCompleted.ToString()
        };
        await fixture.AttemptRepository.CreateAsync(attempt);
        var service = CreateAutoRecoveryService(fixture, new FakeLinklyBackendTerminalClient());

        var summary = await service.AutoRecoverDeterministicAsync(fixture.Session);

        Assert.Equal(1, summary.RecoveredOrders);
        var finished = (await fixture.AttemptRepository.GetAttemptAsync(attempt.AttemptGuid))!;
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, finished.Status);
        Assert.Equal(CardRecoveryPhases.None, finished.RecoveryPhase);
        Assert.Equal(0, cloudApi.GetTransactionCount);
    }

    [Fact]
    public async Task AutoRecoverDeterministicAsync_completes_a_supervisor_confirmed_paid_attempt()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: false);
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var attempt = CreateApprovedLocalAttempt(AutoLocalTxnRef) with
        {
            ResponseCode = ActiveSessionSupervisorResolutionCodes.ConfirmedPaid,
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalCardPaymentAttemptStatus.OrderCompleted.ToString()
        };
        await fixture.AttemptRepository.CreateAsync(attempt);
        var service = CreateAutoRecoveryService(fixture, new FakeLinklyBackendTerminalClient());

        var summary = await service.AutoRecoverDeterministicAsync(fixture.Session);

        Assert.Equal(1, summary.RecoveredOrders);
        Assert.Single(await fixture.OrderRepository.GetRecentOrdersAsync());
        Assert.Equal(
            LocalCardPaymentAttemptStatus.OrderCompleted,
            (await fixture.AttemptRepository.GetAttemptAsync(attempt.AttemptGuid))!.Status);
    }

    [Fact]
    public async Task AutoRecoverDeterministicAsync_acknowledges_completed_and_finally_failed_cloud_backend_sessions()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: false);
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var completed = CreateAttempt(
            "SESSION-AUTO-DONE",
            "TXN-AUTO-DONE",
            LocalCardPaymentAttemptStatus.OrderCompleted) with
        {
            AttemptGuid = Guid.NewGuid(),
            ResponseCode = "00"
        };
        var declined = CreateAttempt(
            "SESSION-AUTO-DECLINED",
            "TXN-AUTO-DECLINED",
            LocalCardPaymentAttemptStatus.Declined) with
        {
            AttemptGuid = Guid.NewGuid(),
            ResponseCode = "05",
            CompletedAt = DateTimeOffset.Parse("2026-06-05T10:00:00+10:00")
        };
        await fixture.AttemptRepository.CreateAsync(completed);
        await fixture.AttemptRepository.CreateAsync(declined);
        var backend = new FakeLinklyBackendTerminalClient();
        var service = CreateAutoRecoveryService(fixture, backend);

        var summary = await service.AutoRecoverDeterministicAsync(fixture.Session);

        Assert.Equal(2, summary.Examined);
        Assert.Equal(0, summary.RecoveredOrders);
        Assert.Equal(2, backend.AcknowledgeCallCount);
        Assert.NotNull((await fixture.AttemptRepository.GetAttemptAsync(completed.AttemptGuid))!.AcknowledgedAt);
        Assert.NotNull((await fixture.AttemptRepository.GetAttemptAsync(declined.AttemptGuid))!.AcknowledgedAt);
        Assert.Empty(await fixture.AttemptRepository.GetOpenAttemptsAsync("S001", "POS-01", "Sandbox"));
        Assert.Equal(0, backend.StatusCallCount);
    }

    [Fact]
    public async Task AutoRecoverDeterministicAsync_keeps_attempts_open_when_the_acknowledge_still_fails()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: false);
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var first = CreateAttempt("SESSION-AUTO-A", "TXN-AUTO-A", LocalCardPaymentAttemptStatus.OrderCompleted) with
        {
            AttemptGuid = Guid.NewGuid()
        };
        var second = CreateAttempt("SESSION-AUTO-B", "TXN-AUTO-B", LocalCardPaymentAttemptStatus.OrderCompleted) with
        {
            AttemptGuid = Guid.NewGuid()
        };
        await fixture.AttemptRepository.CreateAsync(first);
        await fixture.AttemptRepository.CreateAsync(second);
        var backend = new FakeLinklyBackendTerminalClient
        {
            AcknowledgeException = new HttpRequestException("ack failed")
        };
        var service = CreateAutoRecoveryService(fixture, backend);

        var summary = await service.AutoRecoverDeterministicAsync(fixture.Session);

        // 一条失败不影响其余；两条都仍在异常中心队列里，等下一次登录或人工恢复。
        Assert.Equal(2, summary.Examined);
        Assert.Equal(2, backend.AcknowledgeCallCount);
        Assert.Equal(2, (await fixture.AttemptRepository.GetOpenAttemptsAsync("S001", "POS-01", "Sandbox")).Count);
    }

    [Theory]
    [InlineData(LocalCardPaymentAttemptStatus.Pending, LinklyConnectionMode.LocalIp)]
    [InlineData(LocalCardPaymentAttemptStatus.SessionStarted, LinklyConnectionMode.LocalIp)]
    [InlineData(LocalCardPaymentAttemptStatus.Recovering, LinklyConnectionMode.LocalIp)]
    [InlineData(LocalCardPaymentAttemptStatus.RequiresReview, LinklyConnectionMode.LocalIp)]
    [InlineData(LocalCardPaymentAttemptStatus.Recovering, LinklyConnectionMode.CloudBackendAsync)]
    [InlineData(LocalCardPaymentAttemptStatus.SessionStarted, LinklyConnectionMode.CloudBackendAsync)]
    [InlineData(LocalCardPaymentAttemptStatus.Recovering, LinklyConnectionMode.CloudDirectSync)]
    public async Task AutoRecoverDeterministicAsync_never_touches_results_that_are_still_unknown(
        LocalCardPaymentAttemptStatus status,
        LinklyConnectionMode mode)
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: false);
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var attempt = CreateAttempt(
            mode == LinklyConnectionMode.LocalIp ? null : "SESSION-UNKNOWN",
            AutoLocalTxnRef,
            status,
            connectionMode: mode) with
        {
            AttemptGuid = Guid.NewGuid()
        };
        await fixture.AttemptRepository.CreateAsync(attempt);
        var terminal = new FakeLinklyTerminalClient(AutoRecoveryTerminalResult(AutoLocalTxnRef, true, "00", "APPROVED"));
        var backend = new FakeLinklyBackendTerminalClient();
        var service = CreateAutoRecoveryService(fixture, backend, terminal);

        var summary = await service.AutoRecoverDeterministicAsync(fixture.Session);

        Assert.Equal(0, summary.Examined);
        Assert.Equal(status, (await fixture.AttemptRepository.GetAttemptAsync(attempt.AttemptGuid))!.Status);
        Assert.Equal(0, backend.AcknowledgeCallCount);
        Assert.Equal(0, terminal.RecoverCallCount);
        Assert.Equal(0, cloudApi.GetTransactionCount);
        Assert.Equal(0, backend.StatusCallCount);
        Assert.Empty(await fixture.OrderRepository.GetRecentOrdersAsync());
    }

    [Fact]
    public async Task AutoRecoverDeterministicAsync_leaves_supervisor_not_paid_and_unreadable_drafts_to_the_recovery_center()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: false);
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var notPaid = CreateAttempt(
            null,
            AutoLocalTxnRef,
            LocalCardPaymentAttemptStatus.Recovering,
            connectionMode: LinklyConnectionMode.LocalIp) with
        {
            AttemptGuid = Guid.NewGuid(),
            ResponseCode = ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid,
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalCardPaymentAttemptStatus.Abandoned.ToString()
        };
        var unreadable = CreateApprovedLocalAttempt("P000000000000912") with
        {
            OrderDraftJson = "{ not a draft"
        };
        await fixture.AttemptRepository.CreateAsync(notPaid);
        await fixture.AttemptRepository.CreateAsync(unreadable);
        var cart = new PosCartService();
        var service = CreateAutoRecoveryService(fixture, new FakeLinklyBackendTerminalClient());

        var summary = await service.AutoRecoverDeterministicAsync(fixture.Session);

        // 还原草稿需要界面交接，损坏草稿需要主管出口：都不自动处理，也不会碰购物车。
        Assert.Equal(0, summary.Examined);
        Assert.Empty(await fixture.OrderRepository.GetRecentOrdersAsync());
        Assert.True(cart.IsEmpty);
        Assert.Equal(2, (await fixture.AttemptRepository.GetOpenAttemptsAsync("S001", "POS-01", "Sandbox")).Count);
    }

    [Fact]
    public async Task AutoRecoverDeterministicAsync_ignores_non_linkly_processors()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: false);
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        await fixture.AttemptRepository.CreateAsync(CreateApprovedLocalAttempt(AutoLocalTxnRef));
        var service = CreateAutoRecoveryService(
            fixture,
            new FakeLinklyBackendTerminalClient(),
            settingsProvider: new FakeSquareCardTerminalSettingsProvider());

        var summary = await service.AutoRecoverDeterministicAsync(fixture.Session);

        Assert.Equal(0, summary.Examined);
        Assert.Empty(await fixture.OrderRepository.GetRecentOrdersAsync());
    }

    [Fact]
    public async Task Coordinator_routes_auto_recovery_to_the_linkly_service_only()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: false);
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        await fixture.AttemptRepository.CreateAsync(CreateApprovedLocalAttempt(AutoLocalTxnRef));
        var linkly = CreateAutoRecoveryService(fixture, new FakeLinklyBackendTerminalClient());
        var coordinator = new CardPaymentRecoveryCoordinator(
            fixture.SettingsProvider,
            linkly,
            new ThrowingSquareRecoveryService());

        var summary = await coordinator.AutoRecoverDeterministicAsync(fixture.Session);

        Assert.Equal(1, summary.RecoveredOrders);
        Assert.Single(await fixture.OrderRepository.GetRecentOrdersAsync());
    }

    private sealed class ThrowingSquareRecoveryService : ISquarePaymentRecoveryService
    {
        public Task<CardPaymentRecoveryResult> RecoverLatestAsync(
            PosCartService cart,
            PosSessionState session,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CardRefundSupervisorResolutionResult> ResolveRefundAsync(
            CardRefundSupervisorResolution resolution,
            PosCartService cart,
            PosSessionState session,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CardRecoveryQueueItem>> ListHistoryAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CardRecoveryQueueItem>> ListOpenAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CardPaymentRecoveryResult> RecoverAttemptAsync(
            Guid attemptGuid,
            PosCartService cart,
            PosSessionState session,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CardRecoveryResolutionResult> ResolveAttemptAsync(
            Guid attemptGuid,
            CardRecoverySupervisorDecision decision,
            string reason,
            string? evidence,
            string? reference,
            PosCartService cart,
            PosSessionState session,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
