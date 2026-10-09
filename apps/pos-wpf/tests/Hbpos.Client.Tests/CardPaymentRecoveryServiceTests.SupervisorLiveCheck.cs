using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Tests;

// 主管结案前的实时核验（H1）、TxnRef-only 结案补会话号（M16）、已批准未建单记录的受控出口（M14）、
// 付款页恢复到孤儿批准时不得被 ack 抹掉（M30）。
public sealed partial class CardPaymentRecoveryServiceTests
{
    private const string LocalIpTxnRef = "P000000000000901";
    private const string DirectTxnRef = "P000000000000902";

    private static CardPaymentSupervisorResolution SupervisorResolution(
        LocalCardPaymentAttempt attempt,
        CardPaymentSupervisorDecision decision,
        string? paymentReference = null)
    {
        return new CardPaymentSupervisorResolution(
            attempt.AttemptGuid,
            CardProcessorKind.Linkly,
            decision,
            "Supervisor review",
            "MANAGER-01",
            "USER-MANAGER-01",
            "Manager One",
            Evidence: decision == CardPaymentSupervisorDecision.ContinueWaiting
                ? null
                : "Bank evidence reviewed by supervisor",
            PaymentReference: decision == CardPaymentSupervisorDecision.ConfirmPaid ? paymentReference : null);
    }

    private static LocalCardPaymentAttempt CreateCompletedSaleFor(string sessionId, string txnRef)
    {
        return CreateAttempt(sessionId, txnRef, LocalCardPaymentAttemptStatus.OrderCompleted) with
        {
            AttemptGuid = Guid.NewGuid(),
            ResponseCode = "00",
            ResponseText = "APPROVED",
            PaymentReference = $"ANZ:{txnRef}",
            AcknowledgedAt = DateTimeOffset.Parse("2026-06-05T10:01:00+10:00")
        };
    }

    private static PaymentAuthorizationResult LocalTerminalResult(
        string txnRef,
        bool approved,
        string responseCode,
        string responseText,
        bool resultUnknown = false,
        string? returnedTxnRef = null)
    {
        var returned = returnedTxnRef ?? txnRef;
        return new PaymentAuthorizationResult(
            approved,
            $"ANZ:{returned}",
            "ANZ Linkly",
            10m,
            [CreateLocalCardTransaction(returned, responseCode, responseText)],
            "ANZ",
            "Sandbox",
            LinklyConnectionMode.LocalIp.ToString(),
            "P",
            null,
            returned,
            responseCode,
            responseText,
            ResultUnknown: resultUnknown);
    }

    private static CardPaymentRecoveryService CreateDirectService(
        FakeCardPaymentAttemptRepository attempts,
        FakeLocalOrderRepository orders,
        FakeLinklyBackendTerminalClient backend,
        ILinklyCloudTerminalClient directTerminal)
    {
        return new CardPaymentRecoveryService(
            attempts,
            new FakeCardTerminalSettingsProvider(LinklyConnectionMode.CloudDirectSync),
            backend,
            new CashCheckoutService(),
            orders,
            new FakeSyncQueueRepository(),
            cloudTerminalClient: directTerminal);
    }

    private sealed class RecordingOperationAuditLogger : IOperationAuditLogger
    {
        public List<BlazorApp.Shared.DTOs.OperationAuditEventDto> Events { get; } = [];

        public void Record(BlazorApp.Shared.DTOs.OperationAuditEventDto auditEvent) => Events.Add(auditEvent);
    }

    // ───────────── H1：CloudBackendAsync 实时核验 ─────────────

    [Theory]
    [InlineData(CardPaymentSupervisorDecision.ConfirmPaid)]
    [InlineData(CardPaymentSupervisorDecision.ConfirmNotPaid)]
    public async Task ResolvePaymentAsync_backend_session_still_pending_only_allows_continue_waiting(
        CardPaymentSupervisorDecision decision)
    {
        var attempt = CreateAttempt("S-LIVE-PENDING", "TXN-LIVE-PENDING", LocalCardPaymentAttemptStatus.Recovering);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var backend = new FakeLinklyBackendTerminalClient
        {
            Status = CreateStatus("Pending", "S-LIVE-PENDING", "TXN-LIVE-PENDING", null, null)
        };
        var orders = new FakeLocalOrderRepository();
        var service = CreateService(attempts, orders, backend);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, decision, "REF-1"),
            new PosCartService(),
            Session);

        Assert.False(result.Succeeded);
        Assert.False(result.ResolutionPersisted);
        Assert.True(result.LockRetained);
        Assert.Contains("still processing", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Continue waiting", result.Message, StringComparison.Ordinal);
        Assert.Null(attempts.LastPaymentJournal);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, attempts.Status);
        Assert.Equal(1, backend.StatusCallCount);
        Assert.Equal("S-LIVE-PENDING", backend.StatusSessionId);
        Assert.Equal(0, backend.AcknowledgeCallCount);
        Assert.Equal(0, orders.SaveCount);
    }

    [Fact]
    public async Task ResolvePaymentAsync_continue_waiting_does_not_query_and_stays_available_while_pending()
    {
        var attempt = CreateAttempt("S-LIVE-WAIT", "TXN-LIVE-WAIT", LocalCardPaymentAttemptStatus.Recovering);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var backend = new FakeLinklyBackendTerminalClient
        {
            Status = CreateStatus("Pending", "S-LIVE-WAIT", "TXN-LIVE-WAIT", null, null)
        };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), backend);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ContinueWaiting),
            new PosCartService(),
            Session);

        Assert.True(result.Succeeded);
        Assert.True(result.LockRetained);
        Assert.Equal(0, backend.StatusCallCount);
        Assert.Equal(ActiveSessionSupervisorResolutionCodes.ContinueWaiting, attempts.ResponseCode);
    }

    [Fact]
    public async Task ResolvePaymentAsync_backend_approved_session_rejects_confirm_not_paid()
    {
        var attempt = CreateAttempt("S-LIVE-APPROVED", "TXN-LIVE-APPROVED", LocalCardPaymentAttemptStatus.Recovering);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var backend = new FakeLinklyBackendTerminalClient
        {
            Status = CreateStatus(
                "Completed", "S-LIVE-APPROVED", "TXN-LIVE-APPROVED", "00", "APPROVED", transactionSuccess: true)
        };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), backend);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmNotPaid),
            new PosCartService(),
            Session);

        Assert.False(result.Succeeded);
        Assert.True(result.LockRetained);
        Assert.False(result.ResolutionPersisted);
        Assert.Contains("approved", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Run recovery", result.Message, StringComparison.Ordinal);
        Assert.Null(attempts.LastPaymentJournal);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, attempts.Status);
        Assert.Equal(0, backend.AcknowledgeCallCount);
    }

    [Fact]
    public async Task ResolvePaymentAsync_backend_approved_session_allows_consistent_confirm_paid()
    {
        var attempt = CreateAttempt("S-LIVE-APPROVED-OK", "TXN-LIVE-APPROVED-OK", LocalCardPaymentAttemptStatus.Recovering);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var backend = new FakeLinklyBackendTerminalClient
        {
            Status = CreateStatus(
                "Completed", "S-LIVE-APPROVED-OK", "TXN-LIVE-APPROVED-OK", "00", "APPROVED", transactionSuccess: true)
        };
        var orders = new FakeLocalOrderRepository();
        var service = CreateService(attempts, orders, backend);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmPaid, "BANK-REF-OK"),
            new PosCartService(),
            Session);

        Assert.True(result.Succeeded);
        Assert.NotNull(attempts.LastPaymentJournal);
        Assert.Equal(1, orders.SaveCount);
        Assert.Equal(1, backend.StatusCallCount);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("NotSubmitted")]
    [InlineData("Cancelled")]
    public async Task ResolvePaymentAsync_backend_failed_session_rejects_confirm_paid_but_allows_not_paid(
        string finalStatus)
    {
        var attempt = CreateAttempt("S-LIVE-FAILED", "TXN-LIVE-FAILED", LocalCardPaymentAttemptStatus.Recovering);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var backend = new FakeLinklyBackendTerminalClient
        {
            Status = CreateStatus(finalStatus, "S-LIVE-FAILED", "TXN-LIVE-FAILED", "05", "DECLINED")
        };
        var orders = new FakeLocalOrderRepository();
        var service = CreateService(attempts, orders, backend);

        var paid = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmPaid, "BANK-REF-FAILED"),
            new PosCartService(),
            Session);

        Assert.False(paid.Succeeded);
        Assert.False(paid.ResolutionPersisted);
        Assert.Contains("not approved", paid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(attempts.LastPaymentJournal);
        Assert.Equal(0, orders.SaveCount);

        var notPaid = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmNotPaid),
            new PosCartService(),
            Session);

        Assert.True(notPaid.ResolutionPersisted);
        Assert.NotNull(attempts.LastPaymentJournal);
    }

    [Theory]
    [InlineData(CardPaymentSupervisorDecision.ConfirmPaid)]
    [InlineData(CardPaymentSupervisorDecision.ConfirmNotPaid)]
    public async Task ResolvePaymentAsync_backend_query_failure_fails_closed(CardPaymentSupervisorDecision decision)
    {
        var attempt = CreateAttempt("S-LIVE-ERROR", "TXN-LIVE-ERROR", LocalCardPaymentAttemptStatus.Recovering);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var backend = new FakeLinklyBackendTerminalClient
        {
            StatusException = new HttpRequestException(
                "gateway down",
                inner: null,
                statusCode: System.Net.HttpStatusCode.ServiceUnavailable)
        };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), backend);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, decision, "BANK-REF-ERR"),
            new PosCartService(),
            Session);

        Assert.False(result.Succeeded);
        Assert.False(result.ResolutionPersisted);
        Assert.True(result.LockRetained);
        Assert.Contains("could not check", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(attempts.LastPaymentJournal);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, attempts.Status);
        Assert.Equal(0, backend.AcknowledgeCallCount);
    }

    [Theory]
    [InlineData("SupervisorResolved", null)]
    [InlineData("Completed", null)]
    public async Task ResolvePaymentAsync_backend_session_without_conclusive_result_leaves_decision_to_supervisor(
        string status,
        bool? transactionSuccess)
    {
        var attempt = CreateAttempt("S-LIVE-INCONCLUSIVE", "TXN-LIVE-INCONCLUSIVE", LocalCardPaymentAttemptStatus.Recovering);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var backend = new FakeLinklyBackendTerminalClient
        {
            Status = CreateStatus(
                status, "S-LIVE-INCONCLUSIVE", "TXN-LIVE-INCONCLUSIVE", null, null, transactionSuccess)
        };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), backend);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmNotPaid),
            new PosCartService(),
            Session);

        Assert.True(result.ResolutionPersisted);
        Assert.Equal(1, backend.StatusCallCount);
    }

    // ───────────── H1：LocalIp / CloudDirectSync 实时核验 ─────────────

    private static LocalCardPaymentAttempt CreateLocalIpPendingAttempt() =>
        CreateAttempt(
            sessionId: null,
            txnRef: LocalIpTxnRef,
            status: LocalCardPaymentAttemptStatus.Recovering,
            connectionMode: LinklyConnectionMode.LocalIp);

    [Fact]
    public async Task ResolvePaymentAsync_local_ip_terminal_reports_approved_rejects_confirm_not_paid()
    {
        var attempt = CreateLocalIpPendingAttempt();
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var terminal = new FakeLinklyTerminalClient(LocalTerminalResult(LocalIpTxnRef, true, "00", "APPROVED"));
        var backend = new FakeLinklyBackendTerminalClient();
        var service = CreateService(
            attempts,
            new FakeLocalOrderRepository(),
            backend,
            new FakeCardTerminalSettingsProvider(LinklyConnectionMode.LocalIp),
            terminal);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmNotPaid),
            new PosCartService(),
            Session);

        Assert.False(result.Succeeded);
        Assert.False(result.ResolutionPersisted);
        Assert.Contains("approved", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, terminal.RecoverCallCount);
        Assert.Equal(LocalIpTxnRef, terminal.LastTxnRef);
        Assert.Null(attempts.LastPaymentJournal);
    }

    [Fact]
    public async Task ResolvePaymentAsync_local_ip_terminal_reports_decline_rejects_confirm_paid()
    {
        var attempt = CreateLocalIpPendingAttempt();
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var terminal = new FakeLinklyTerminalClient(LocalTerminalResult(LocalIpTxnRef, false, "05", "DECLINED"));
        var service = CreateService(
            attempts,
            new FakeLocalOrderRepository(),
            new FakeLinklyBackendTerminalClient(),
            new FakeCardTerminalSettingsProvider(LinklyConnectionMode.LocalIp),
            terminal);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmPaid, "BANK-REF"),
            new PosCartService(),
            Session);

        Assert.False(result.Succeeded);
        Assert.False(result.ResolutionPersisted);
        Assert.Contains("not approved", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(attempts.LastPaymentJournal);
    }

    [Theory]
    [InlineData(CardPaymentSupervisorDecision.ConfirmPaid)]
    [InlineData(CardPaymentSupervisorDecision.ConfirmNotPaid)]
    public async Task ResolvePaymentAsync_local_ip_unreachable_terminal_fails_closed(
        CardPaymentSupervisorDecision decision)
    {
        var attempt = CreateLocalIpPendingAttempt();
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var terminal = new FakeLinklyTerminalClient(
            LocalTerminalResult(LocalIpTxnRef, false, "00", "unknown", resultUnknown: true));
        var service = CreateService(
            attempts,
            new FakeLocalOrderRepository(),
            new FakeLinklyBackendTerminalClient(),
            new FakeCardTerminalSettingsProvider(LinklyConnectionMode.LocalIp),
            terminal);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, decision, "BANK-REF"),
            new PosCartService(),
            Session);

        Assert.False(result.Succeeded);
        Assert.False(result.ResolutionPersisted);
        Assert.True(result.LockRetained);
        Assert.Contains("could not check", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(attempts.LastPaymentJournal);
    }

    [Fact]
    public async Task ResolvePaymentAsync_local_ip_without_terminal_client_fails_closed()
    {
        var attempt = CreateLocalIpPendingAttempt();
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var service = CreateService(
            attempts,
            new FakeLocalOrderRepository(),
            new FakeLinklyBackendTerminalClient(),
            new FakeCardTerminalSettingsProvider(LinklyConnectionMode.LocalIp));

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmNotPaid),
            new PosCartService(),
            Session);

        Assert.False(result.ResolutionPersisted);
        Assert.Contains("could not check", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolvePaymentAsync_local_ip_terminal_showing_another_transaction_leaves_decision_to_supervisor()
    {
        var attempt = CreateLocalIpPendingAttempt();
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var terminal = new FakeLinklyTerminalClient(
            LocalTerminalResult(LocalIpTxnRef, true, "00", "APPROVED", returnedTxnRef: "P000000000000999"));
        var service = CreateService(
            attempts,
            new FakeLocalOrderRepository(),
            new FakeLinklyBackendTerminalClient(),
            new FakeCardTerminalSettingsProvider(LinklyConnectionMode.LocalIp),
            terminal);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmNotPaid),
            new PosCartService(),
            Session);

        Assert.True(result.ResolutionPersisted);
        Assert.Equal(1, terminal.RecoverCallCount);
    }

    [Fact]
    public async Task ResolvePaymentAsync_cloud_direct_reports_approved_rejects_confirm_not_paid()
    {
        var attempt = CreateAttempt(
            "SESSION-DIRECT-LIVE",
            DirectTxnRef,
            LocalCardPaymentAttemptStatus.Recovering,
            connectionMode: LinklyConnectionMode.CloudDirectSync);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var direct = new QueryOnlyDirectTerminal(LocalTerminalResult(DirectTxnRef, true, "00", "APPROVED"));
        var service = CreateDirectService(
            attempts,
            new FakeLocalOrderRepository(),
            new FakeLinklyBackendTerminalClient(),
            direct);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmNotPaid),
            new PosCartService(),
            Session);

        Assert.False(result.ResolutionPersisted);
        Assert.Contains("approved", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, direct.QueryCount);
        Assert.Equal("SESSION-DIRECT-LIVE", direct.SessionId);
        Assert.Equal(DirectTxnRef, direct.TxnRef);
    }

    [Theory]
    [InlineData(CardPaymentSupervisorDecision.ConfirmPaid)]
    [InlineData(CardPaymentSupervisorDecision.ConfirmNotPaid)]
    public async Task ResolvePaymentAsync_cloud_direct_without_final_result_only_allows_continue_waiting(
        CardPaymentSupervisorDecision decision)
    {
        var attempt = CreateAttempt(
            "SESSION-DIRECT-PENDING",
            DirectTxnRef,
            LocalCardPaymentAttemptStatus.Recovering,
            connectionMode: LinklyConnectionMode.CloudDirectSync);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        // 直连 GET 202（仍在处理）与网络失败都收敛为 ResultUnknown：无法证明没有迟到的结果，失败关闭。
        var direct = new QueryOnlyDirectTerminal(
            LocalTerminalResult(DirectTxnRef, false, "00", "unknown", resultUnknown: true));
        var service = CreateDirectService(
            attempts,
            new FakeLocalOrderRepository(),
            new FakeLinklyBackendTerminalClient(),
            direct);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, decision, "BANK-REF"),
            new PosCartService(),
            Session);

        Assert.False(result.ResolutionPersisted);
        Assert.True(result.LockRetained);
        Assert.Contains("could not check", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(attempts.LastPaymentJournal);

        var waiting = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ContinueWaiting),
            new PosCartService(),
            Session);
        Assert.True(waiting.Succeeded);
        Assert.Equal(1, direct.QueryCount);
    }

    // ───────────── M16：只有 TxnRef 的 attempt 结案前先认领真实 SessionId ─────────────

    [Fact]
    public async Task ResolvePaymentAsync_txn_ref_only_attempt_binds_real_session_before_acknowledging()
    {
        var attempt = CreateAttempt(null, "TXN-REF-ONLY", LocalCardPaymentAttemptStatus.Recovering);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var backend = new FakeLinklyBackendTerminalClient
        {
            ResumableStatus = CreateStatus(
                "Completed", "REAL-SESSION-001", "TXN-REF-ONLY", "00", "APPROVED", transactionSuccess: true)
        };
        var orders = new FakeLocalOrderRepository();
        var service = CreateService(attempts, orders, backend);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmPaid, "BANK-REF-REAL"),
            new PosCartService(),
            Session);

        Assert.True(result.Succeeded);
        Assert.Equal("REAL-SESSION-001", attempts.SessionId);
        Assert.Equal("REAL-SESSION-001", attempts.LastPaymentJournal!.SessionId);
        Assert.Equal(1, backend.AcknowledgeCallCount);
        // 必须 ack 真实会话，而不是拿 TxnRef 充当 SessionId 去撞 404。
        Assert.Equal("REAL-SESSION-001", backend.AcknowledgedSessionId);
        Assert.Equal(1, orders.SaveCount);
        Assert.NotNull(attempts.AcknowledgedAt);
    }

    [Fact]
    public async Task ResolvePaymentAsync_txn_ref_only_attempt_without_backend_session_never_acknowledges_txn_ref()
    {
        var attempt = CreateAttempt(null, "TXN-REF-ONLY-2", LocalCardPaymentAttemptStatus.Recovering);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var backend = new FakeLinklyBackendTerminalClient { ResumableStatus = null };
        var orders = new FakeLocalOrderRepository();
        var service = CreateService(attempts, orders, backend);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmPaid, "BANK-REF-NONE"),
            new PosCartService(),
            Session);

        Assert.True(result.Succeeded);
        Assert.Equal(1, orders.SaveCount);
        Assert.Equal(0, backend.AcknowledgeCallCount);
        Assert.Null(backend.AcknowledgedSessionId);
        Assert.Null(attempts.SessionId);
    }

    [Fact]
    public async Task ResolvePaymentAsync_txn_ref_only_attempt_ignores_resumable_session_of_another_transaction()
    {
        var attempt = CreateAttempt(null, "TXN-REF-ONLY-3", LocalCardPaymentAttemptStatus.Recovering);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var backend = new FakeLinklyBackendTerminalClient
        {
            ResumableStatus = CreateStatus("Pending", "OTHER-SESSION", "OTHER-TXN", null, null)
        };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), backend);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmNotPaid),
            new PosCartService(),
            Session);

        // 另一笔交易仍在途不能阻止本笔结案，也不能把本笔绑定到别人的会话上。
        Assert.True(result.ResolutionPersisted);
        Assert.Null(attempts.SessionId);
    }

    // ───────────── M14：已批准未建单记录的受控出口 ─────────────

    private static LocalCardPaymentAttempt CreateApprovedReviewAttempt(
        LocalCardPaymentAttemptStatus status = LocalCardPaymentAttemptStatus.RequiresReview) =>
        CreateAttempt("S-APPROVED-REVIEW", "TXN-APPROVED-REVIEW", status) with
        {
            ResponseCode = "00",
            ResponseText = "Card terminal authorized amount did not match the requested amount.",
            PaymentReference = "ANZ:TXN-APPROVED-REVIEW"
        };

    [Fact]
    public async Task ResolvePaymentAsync_approved_evidence_exit_closes_record_restores_draft_and_audits()
    {
        var attempt = CreateApprovedReviewAttempt();
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var backend = new FakeLinklyBackendTerminalClient();
        var orders = new FakeLocalOrderRepository();
        var service = CreateService(attempts, orders, backend);
        var cart = new PosCartService();

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmNotPaid),
            cart,
            Session);

        Assert.True(result.Succeeded);
        Assert.True(result.ResolutionPersisted);
        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, result.RecoveryResult?.Outcome);
        Assert.Contains("refunded or handled elsewhere", result.Message, StringComparison.OrdinalIgnoreCase);
        // 出口不是“确认未付款”：已扣款的事实不变，所以不做实时矛盾核验。
        Assert.Equal(0, backend.StatusCallCount);
        Assert.Equal(1, backend.AcknowledgeCallCount);
        Assert.Equal(0, orders.SaveCount);
        Assert.Single(cart.Lines);

        var journal = Assert.IsType<LocalFinancialSupervisorResolution>(attempts.LastPaymentJournal);
        using var audit = System.Text.Json.JsonDocument.Parse(journal.AuditPayloadJson);
        Assert.Equal(
            CardPaymentRecoveryService.ApprovedEvidenceExitReasonCode,
            audit.RootElement.GetProperty("reasonCode").GetString());
        var properties = audit.RootElement.GetProperty("properties");
        Assert.Equal("approved-evidence-exit", properties.GetProperty("action").GetString());
        Assert.Equal("00", properties.GetProperty("result").GetString());
        Assert.Equal("ANZ:TXN-APPROVED-REVIEW", properties.GetProperty("financialReference").GetString());
        Assert.Equal("RequiresReview", properties.GetProperty("status").GetString());

        Assert.True(await service.CompleteDraftHandoffAsync(attempt.AttemptGuid, cart));
        Assert.Equal(LocalCardPaymentAttemptStatus.Abandoned, attempts.Status);
        Assert.NotNull(attempts.AcknowledgedAt);
    }

    [Fact]
    public async Task ResolvePaymentAsync_approved_evidence_exit_still_closes_when_draft_is_unreadable()
    {
        var attempt = CreateApprovedReviewAttempt(LocalCardPaymentAttemptStatus.Approved) with
        {
            OrderDraftJson = "{ this is not a valid draft"
        };
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var backend = new FakeLinklyBackendTerminalClient();
        var service = CreateService(attempts, new FakeLocalOrderRepository(), backend);
        var cart = new PosCartService();

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmNotPaid),
            cart,
            Session);

        Assert.True(result.ResolutionPersisted);
        Assert.False(result.LockRetained);
        Assert.Equal(CardPaymentRecoveryOutcome.ActiveSessionNotPaid, result.RecoveryResult?.Outcome);
        Assert.Contains("refunded or handled elsewhere", result.RecoveryResult!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LocalCardPaymentAttemptStatus.Abandoned, attempts.Status);
        Assert.NotNull(attempts.AcknowledgedAt);
        Assert.True(cart.IsEmpty);
        Assert.Equal(1, backend.AcknowledgeCallCount);
    }

    [Fact]
    public async Task ResolvePaymentAsync_approved_evidence_exit_closes_generic_active_session_record()
    {
        var attempt = CreateAttempt("S-ORPHAN-GENERIC", "TXN-ORPHAN-GENERIC", LocalCardPaymentAttemptStatus.Approved) with
        {
            OperationKind = "ActiveSession",
            OperationGuid = Guid.NewGuid(),
            OrderDraftJson = "{}",
            Amount = 0m,
            ResponseCode = "00",
            PaymentReference = "TXN-ORPHAN-GENERIC",
            AcknowledgedAt = DateTimeOffset.Parse("2026-06-05T10:02:00+10:00")
        };
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var backend = new FakeLinklyBackendTerminalClient();
        var service = CreateService(attempts, new FakeLocalOrderRepository(), backend);

        var result = await service.ResolvePaymentAsync(
            SupervisorResolution(attempt, CardPaymentSupervisorDecision.ConfirmNotPaid),
            new PosCartService(),
            Session);

        Assert.True(result.ResolutionPersisted);
        Assert.Equal(CardPaymentRecoveryOutcome.ActiveSessionNotPaid, result.RecoveryResult?.Outcome);
        Assert.Equal(LocalCardPaymentAttemptStatus.Abandoned, attempts.Status);
        // 记录早已 ack（接管时 ack 过）：不会再向后端重复 ack，只在本地终态化。
        Assert.Equal(0, backend.AcknowledgeCallCount);
    }

    [Fact]
    public async Task ResolvePaymentAsync_approved_evidence_still_rejects_confirm_paid_and_continue_waiting()
    {
        var attempt = CreateApprovedReviewAttempt();
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var service = CreateService(attempts, new FakeLocalOrderRepository(), new FakeLinklyBackendTerminalClient());

        foreach (var decision in new[]
                 {
                     CardPaymentSupervisorDecision.ConfirmPaid,
                     CardPaymentSupervisorDecision.ContinueWaiting
                 })
        {
            var result = await service.ResolvePaymentAsync(
                SupervisorResolution(attempt, decision, "BANK-REF"),
                new PosCartService(),
                Session);

            Assert.False(result.Succeeded);
            Assert.Equal("Bank payment evidence already exists. Run recovery instead.", result.Message);
            Assert.Null(attempts.LastPaymentJournal);
        }
    }

    // ───────────── 文案 ─────────────

    [Theory]
    [InlineData("cardRecovery.linkly.resolveLivePending")]
    [InlineData("cardRecovery.linkly.resolveLiveQueryFailed")]
    [InlineData("cardRecovery.linkly.resolveLiveApproved")]
    [InlineData("cardRecovery.linkly.resolveLiveNotApproved")]
    [InlineData("cardRecovery.linkly.approvedClosedRestored")]
    [InlineData("cardRecovery.linkly.approvedClosedCleared")]
    [InlineData("cardRecovery.linkly.activeSessionApprovedOrderPending")]
    [InlineData("cardRecovery.linkly.activeSessionApprovedOrphan")]
    [InlineData("cardRecovery.center.action.closeApprovedHandled")]
    [InlineData("cardRecovery.payment.section.approvedInstructions")]
    [InlineData("cardRecovery.payment.field.approvedEvidence")]
    public void Supervisor_live_check_and_exit_messages_are_localized_in_both_languages(string key)
    {
        var localization = new Hbpos.Client.Wpf.Localization.LocalizationService();
        var english = localization.T(key);
        localization.SetCulture("zh-CN");
        var chinese = localization.T(key);

        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.False(string.IsNullOrWhiteSpace(chinese));
        Assert.NotEqual($"[[{key}]]", english);
        Assert.NotEqual($"[[{key}]]", chinese);
        Assert.NotEqual(english, chinese);
        Assert.Matches("[\u4e00-\u9fff]", chinese);
    }

    // ───────────── M30：付款页恢复到孤儿批准 ─────────────

    private static LinklyCloudBackendSessionResponse OrphanApprovedStatus() =>
        CreateStatus(
            "Completed", "ACTIVE-ORPHAN", "TXN-ORPHAN", "00", "APPROVED", transactionSuccess: true);

    [Fact]
    public async Task RecoverActiveSessionAsync_approved_session_without_matching_order_is_orphan_and_not_acknowledged()
    {
        var attempts = new FakeCardPaymentAttemptRepository(null);
        var backend = new FakeLinklyBackendTerminalClient { ResumableStatus = OrphanApprovedStatus() };
        var audit = new RecordingOperationAuditLogger();
        var service = new CardPaymentRecoveryService(
            attempts,
            new FakeCardTerminalSettingsProvider(),
            backend,
            new CashCheckoutService(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            operationAuditLogger: audit);
        var cart = CreateCurrentCart();

        var result = await service.RecoverActiveSessionAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Contains("no order", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not charge again", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.PaymentSupervisorDetails);
        Assert.Equal("ACTIVE-ORPHAN", result.PaymentSupervisorDetails!.SessionId);
        // 不 ack：服务端会话与本机记录都保持未结，直到主管处理。
        Assert.Equal(0, backend.AcknowledgeCallCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.Approved, attempts.Status);
        Assert.Null(attempts.AcknowledgedAt);
        Assert.Single(cart.Lines);
        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal("CARD_PAYMENT_SUPERVISOR_RESOLUTION", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("ORPHAN_APPROVED_SESSION", auditEvent.ReasonCode);
        Assert.Contains("ACTIVE-ORPHAN", auditEvent.SafeMessage);
        Assert.Equal("Linkly", auditEvent.PaymentMethod);

        // 重复点击恢复仍保持未结，且不会重复上报审计。
        var again = await service.RecoverActiveSessionAsync(cart, Session);
        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, again.Outcome);
        Assert.Equal(0, backend.AcknowledgeCallCount);
        Assert.Single(audit.Events);
    }

    [Fact]
    public async Task RecoverActiveSessionAsync_approved_session_with_unsaved_sale_order_is_left_to_that_attempt()
    {
        var pendingSale = CreateAttempt("ACTIVE-ORPHAN", "TXN-ORPHAN", LocalCardPaymentAttemptStatus.Approved) with
        {
            AttemptGuid = Guid.NewGuid(),
            ResponseCode = "00",
            PaymentReference = "ANZ:TXN-ORPHAN"
        };
        var attempts = new FakeCardPaymentAttemptRepository(null) { RecentAttempts = [pendingSale] };
        var backend = new FakeLinklyBackendTerminalClient { ResumableStatus = OrphanApprovedStatus() };
        var audit = new RecordingOperationAuditLogger();
        var service = new CardPaymentRecoveryService(
            attempts,
            new FakeCardTerminalSettingsProvider(),
            backend,
            new CashCheckoutService(),
            new FakeLocalOrderRepository(),
            new FakeSyncQueueRepository(),
            operationAuditLogger: audit);

        var result = await service.RecoverActiveSessionAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Contains("order has not been saved yet", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, backend.AcknowledgeCallCount);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task RecoverActiveSessionAsync_approved_session_lookup_failure_fails_closed()
    {
        var attempts = new FakeCardPaymentAttemptRepository(null)
        {
            RecentAttemptsException = new InvalidOperationException("sqlite busy")
        };
        var backend = new FakeLinklyBackendTerminalClient { ResumableStatus = OrphanApprovedStatus() };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), backend);

        var result = await service.RecoverActiveSessionAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Equal(0, backend.AcknowledgeCallCount);
    }

    [Fact]
    public async Task RecoverAttemptAsync_targeted_active_session_orphan_approval_is_not_acknowledged()
    {
        var attempt = CreateAttempt("ACTIVE-ORPHAN", "TXN-ORPHAN", LocalCardPaymentAttemptStatus.Recovering) with
        {
            OperationKind = "ActiveSession",
            OperationGuid = Guid.NewGuid(),
            OrderDraftJson = "{}",
            Amount = 0m
        };
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var backend = new FakeLinklyBackendTerminalClient { Status = OrphanApprovedStatus() };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), backend);

        var result = await service.RecoverAttemptAsync(attempt.AttemptGuid, new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Equal(0, backend.AcknowledgeCallCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.Approved, attempts.Status);
        Assert.Null(attempts.AcknowledgedAt);
    }
}
