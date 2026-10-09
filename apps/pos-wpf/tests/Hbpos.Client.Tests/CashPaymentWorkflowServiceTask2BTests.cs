using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class CashPaymentWorkflowServiceTask2BTests
{
    [Fact]
    public async Task Takeover_creates_generic_review_session_and_persists_before_ack_then_starts_new()
    {
        var events = new List<string>();
        var attempts = new RecordingAttemptRepository(events);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(
            events,
            FinalApprovedSession("active-session-1", "TXN-OLD", transactionSuccess: null));
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new StubOrderRepository(),
            new StubSyncQueueRepository(),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
            linklyPaymentAttemptContextAccessor: accessor,
            linklyBackendTerminalClient: backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B", "Takeover Tea", "930T2B", 10m));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cancellationToken: CancellationToken.None,
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded);
        Assert.True(terminal.TakeoverResult?.Succeeded);

        // 新付款 attempt 身份不变，ActiveSession attempt 使用独立 AttemptGuid。
        var saleAttempt = Assert.Single(attempts.Attempts, attempt => attempt.OperationKind == "Sale");
        var activeAttempt = Assert.Single(attempts.Attempts, attempt => attempt.OperationKind == "ActiveSession");
        Assert.Equal(terminal.SeenAttemptGuid, saleAttempt.AttemptGuid);
        Assert.NotEqual(saleAttempt.AttemptGuid, activeAttempt.AttemptGuid);
        Assert.Equal("active-session-1", activeAttempt.SessionId);
        Assert.Equal("U", activeAttempt.TxnType);
        Assert.Equal(LocalCardPaymentAttemptStatus.RequiresReview, activeAttempt.Status);
        Assert.NotNull(activeAttempt.AcknowledgedAt);
        Assert.Contains(activeAttempt, await attempts.GetOpenAttemptsAsync(
            session.StoreCode,
            session.DeviceCode,
            settings.Environment.ToString()));

        // 最终证据持久化必须先于 acknowledge，ack 必须先于新扣款。
        Assert.True(events.IndexOf("persist-final") < events.IndexOf("acknowledge"));
        Assert.True(events.IndexOf("acknowledge") < events.IndexOf("new-start"));
    }

    [Fact]
    public async Task Takeover_persist_failure_returns_failed_without_ack_or_new_start()
    {
        var events = new List<string>();
        var attempts = new RecordingAttemptRepository(events, persistException: new InvalidOperationException("persist failed"));
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(events, FinalApprovedSession("active-session-1", "TXN-OLD"));
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new StubOrderRepository(),
            new StubSyncQueueRepository(),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
            linklyPaymentAttemptContextAccessor: accessor,
            linklyBackendTerminalClient: backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-PF", "Persist Fail Tea", "930T2BPF", 10m));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cancellationToken: CancellationToken.None,
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.False(terminal.TakeoverResult?.Succeeded);
        Assert.DoesNotContain(events, item => item == "acknowledge");
        Assert.DoesNotContain(events, item => item == "new-start");
    }

    [Fact]
    public async Task Takeover_ack_failure_returns_failed_after_persist_without_new_start()
    {
        var events = new List<string>();
        var attempts = new RecordingAttemptRepository(events);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(
            events,
            FinalApprovedSession("active-session-1", "TXN-OLD"),
            ackException: new InvalidOperationException("ack failed"));
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new StubOrderRepository(),
            new StubSyncQueueRepository(),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
            linklyPaymentAttemptContextAccessor: accessor,
            linklyBackendTerminalClient: backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-AF", "Ack Fail Tea", "930T2BAF", 10m));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cancellationToken: CancellationToken.None,
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.False(terminal.TakeoverResult?.Succeeded);
        Assert.Contains("persist-final", events);
        Assert.Contains("acknowledge", events);
        Assert.DoesNotContain(events, item => item == "new-start");
    }

    [Fact]
    public async Task Takeover_updates_exact_existing_sale_without_creating_duplicate_active_session()
    {
        var events = new List<string>();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var existing = CreateExistingAttempt(
            "Sale",
            "active-session-1",
            "TXN-OLD",
            session,
            orderGuid: Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var attempts = new RecordingAttemptRepository(events, initialAttempts: [existing]);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(
            events,
            FinalApprovedSession("active-session-1", "TXN-OLD", transactionSuccess: null) with
            {
                CardTransaction = VerifiedCardTransaction("TXN-OLD", 1000)
            });
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = CreateWorkflow(terminal, attempts, settings, accessor, backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-EXIST", "Existing Tea", "930T2BEXIST", 10m));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(attempts.Attempts, attempt => attempt.OperationKind == "ActiveSession");
        var updated = Assert.Single(attempts.Attempts, attempt => attempt.AttemptGuid == existing.AttemptGuid);
        Assert.Equal(LocalCardPaymentAttemptStatus.Approved, updated.Status);
        Assert.NotNull(updated.AcknowledgedAt);
        Assert.Contains(updated, await attempts.GetOpenAttemptsAsync(
            session.StoreCode,
            session.DeviceCode,
            settings.Environment.ToString()));
    }

    [Fact]
    public async Task Takeover_accepts_pending_status_with_verified_official_success_evidence()
    {
        var events = new List<string>();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var existing = CreateExistingAttempt("Sale", "active-session-1", "TXN-OLD", session, Guid.NewGuid());
        var attempts = new RecordingAttemptRepository(events, initialAttempts: [existing]);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(
            events,
            PendingApprovedSession("active-session-1", "TXN-OLD"));
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = CreateWorkflow(terminal, attempts, settings, accessor, backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-PENDING", "Pending Approved Tea", "930T2BPENDING", 10m));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded);
        var updated = Assert.Single(attempts.Attempts, attempt => attempt.AttemptGuid == existing.AttemptGuid);
        Assert.Equal(LocalCardPaymentAttemptStatus.Approved, updated.Status);
        Assert.Contains("acknowledge", events);
        Assert.Contains("new-start", events);
    }

    [Fact]
    public async Task Takeover_preserves_existing_txn_ref_when_final_status_omits_it()
    {
        var events = new List<string>();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var existing = CreateExistingAttempt("Sale", "active-session-1", "TXN-OLD", session, Guid.NewGuid());
        var attempts = new RecordingAttemptRepository(events, initialAttempts: [existing]);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var finalStatus = FinalApprovedSession("active-session-1", "TXN-OLD") with
        {
            TxnRef = null,
            CardTransaction = VerifiedCardTransaction("TXN-OLD", 1000)
        };
        var backend = new RecordingBackendTerminalClient(events, finalStatus);
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = CreateWorkflow(terminal, attempts, settings, accessor, backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-REF", "Reference Tea", "930T2BREF", 10m));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded);
        var updated = Assert.Single(attempts.Attempts, attempt => attempt.AttemptGuid == existing.AttemptGuid);
        Assert.Equal("TXN-OLD", updated.TxnRef);
        Assert.Equal("TXN-OLD", updated.PaymentReference);
    }

    [Fact]
    public async Task Takeover_refund_session_cas_rejection_does_not_ack_or_start_new_payment()
    {
        var events = new List<string>();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var existing = CreateExistingAttempt("Refund", "active-session-1", "TXN-OLD", session, Guid.NewGuid()) with
        {
            SubmissionToken = "refund-token"
        };
        var attempts = new RecordingAttemptRepository(
            events,
            initialAttempts: [existing],
            rejectRefundSessionCas: true);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(events, FinalApprovedSession("active-session-1", "TXN-OLD"));
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = CreateWorkflow(terminal, attempts, settings, accessor, backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-CAS", "CAS Tea", "930T2BCAS", 10m));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.False(result.CardResult?.RequiresRecovery);
        Assert.DoesNotContain("acknowledge", events);
        Assert.DoesNotContain("new-start", events);
        Assert.Equal(
            "TXN-OLD",
            Assert.Single(attempts.Attempts, attempt => attempt.AttemptGuid == existing.AttemptGuid).TxnRef);
    }

    [Fact]
    public async Task Takeover_refund_without_submission_token_does_not_ack_or_start_new_payment()
    {
        var events = new List<string>();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var existing = CreateExistingAttempt("Refund", "active-session-1", "TXN-OLD", session, Guid.NewGuid()) with
        {
            SubmissionToken = null
        };
        var attempts = new RecordingAttemptRepository(events, initialAttempts: [existing]);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(events, FinalApprovedSession("active-session-1", "TXN-OLD"));
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = CreateWorkflow(terminal, attempts, settings, accessor, backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-NO-TOKEN", "No Token Tea", "930T2BNOTOKEN", 10m));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("acknowledge", events);
        Assert.DoesNotContain("new-start", events);
        Assert.Null(Assert.Single(attempts.Attempts, attempt => attempt.AttemptGuid == existing.AttemptGuid).AcknowledgedAt);
    }

    [Theory]
    [InlineData(1008L)]
    [InlineData(null)]
    public async Task Takeover_rejects_final_approval_whose_amount_is_not_verified_for_the_existing_sale(long? approvedAmountMinor)
    {
        // 接管把旧销售记为 Approved 之后，恢复会把它当作已持久化的金融事实、按草稿金额自动落单。
        // 所以已完成的批准在这里也必须核验金额：终端批准 10.08 而订单是 10.00，或服务端根本核验不出明细，
        // 都不能记为 Approved，也不能确认会话——留给恢复流程判为未知、交主管处理。
        var events = new List<string>();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var existing = CreateExistingAttempt("Sale", "active-session-1", "TXN-OLD", session, Guid.NewGuid());
        var attempts = new RecordingAttemptRepository(events, initialAttempts: [existing]);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var finalStatus = FinalApprovedSession("active-session-1", "TXN-OLD") with
        {
            CardTransaction = approvedAmountMinor is long amountMinor
                ? VerifiedCardTransaction("TXN-OLD", amountMinor)
                : null
        };
        var backend = new RecordingBackendTerminalClient(events, finalStatus);
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = CreateWorkflow(terminal, attempts, settings, accessor, backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-FINAL-AMOUNT", "Final Amount Tea", "930T2BFINALAMT", 10m));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("acknowledge", events);
        Assert.DoesNotContain("new-start", events);
        var untouched = Assert.Single(attempts.Attempts, attempt => attempt.AttemptGuid == existing.AttemptGuid);
        Assert.NotEqual(LocalCardPaymentAttemptStatus.Approved, untouched.Status);
    }

    [Theory]
    [InlineData("TXN-OTHER", 1000L)]
    [InlineData("TXN-OLD", 999L)]
    public async Task Takeover_rejects_pending_approval_evidence_that_does_not_match_attempt(
        string notificationTxnRef,
        long amountMinor)
    {
        var events = new List<string>();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var existing = CreateExistingAttempt("Sale", "active-session-1", "TXN-OLD", session, Guid.NewGuid());
        var attempts = new RecordingAttemptRepository(events, initialAttempts: [existing]);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var finalStatus = PendingApprovedSession(
            "active-session-1",
            "TXN-OLD",
            notificationTxnRef,
            amountMinor) with
        {
            TxnRef = null
        };
        var backend = new RecordingBackendTerminalClient(events, finalStatus);
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = CreateWorkflow(terminal, attempts, settings, accessor, backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-EVIDENCE", "Evidence Tea", "930T2BEVIDENCE", 10m));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("acknowledge", events);
        Assert.DoesNotContain("new-start", events);
    }

    [Fact]
    public async Task Takeover_rejects_ambiguous_session_identity_without_ack_or_new_start()
    {
        var events = new List<string>();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var attempts = new RecordingAttemptRepository(
            events,
            initialAttempts:
            [
                CreateExistingAttempt("Sale", "active-session-1", "TXN-OLD", session, Guid.NewGuid()),
                CreateExistingAttempt("Refund", "active-session-1", "TXN-OLD", session, Guid.NewGuid())
            ]);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(events, FinalApprovedSession("active-session-1", "TXN-OLD"));
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = CreateWorkflow(terminal, attempts, settings, accessor, backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-AMB", "Ambiguous Tea", "930T2BAMB", 10m));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.False(result.CardResult?.RequiresRecovery);
        Assert.DoesNotContain(events, item => item == "acknowledge");
        Assert.DoesNotContain(events, item => item == "new-start");
        var current = Assert.Single(attempts.Attempts, attempt =>
            attempt.OperationKind == "Sale" && attempt.AttemptGuid == terminal.SeenAttemptGuid);
        Assert.Equal(LocalCardPaymentAttemptStatus.Failed, current.Status);
    }

    [Fact]
    public async Task Takeover_uses_unique_txn_ref_only_when_existing_attempt_has_no_conflicting_session()
    {
        var events = new List<string>();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var existing = CreateExistingAttempt("Sale", null, "TXN-OLD", session, Guid.NewGuid());
        var attempts = new RecordingAttemptRepository(events, initialAttempts: [existing]);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(
            events,
            FinalApprovedSession("active-session-1", "TXN-OLD") with
            {
                CardTransaction = VerifiedCardTransaction("TXN-OLD", 1000)
            });
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = CreateWorkflow(terminal, attempts, settings, accessor, backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-TXN", "Txn Tea", "930T2BTXN", 10m));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(attempts.Attempts, attempt => attempt.OperationKind == "ActiveSession");
        var updated = Assert.Single(attempts.Attempts, attempt => attempt.AttemptGuid == existing.AttemptGuid);
        Assert.Equal("active-session-1", updated.SessionId);
        Assert.Equal(LocalCardPaymentAttemptStatus.Approved, updated.Status);
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Repayment")]
    public async Task Takeover_binds_the_session_to_the_unacknowledged_installment_attempt_instead_of_creating_a_generic_record(
        string installmentKind)
    {
        // 分期 attempt 不在异常中心的未结队列里；后端模式下它的 TxnRef 在发请求前就已落库。
        // 遗留的未确认会话必须按 TxnRef 认领到分期自己的 attempt，否则会另建一条来源不明的 ActiveSession 待复核记录，
        // 分期操作却再也找不回自己的会话。
        var events = new List<string>();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var installmentAttempt = CreateExistingAttempt("Sale", null, "TXN-OLD", session, Guid.NewGuid()) with
        {
            OperationKind = installmentKind,
            OperationGuid = Guid.NewGuid(),
            Status = LocalCardPaymentAttemptStatus.Pending
        };
        var attempts = new RecordingAttemptRepository(events, initialAttempts: [installmentAttempt]);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(
            events,
            FinalApprovedSession("active-session-1", "TXN-OLD") with
            {
                CardTransaction = VerifiedCardTransaction("TXN-OLD", 1000)
            });
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = CreateWorkflow(terminal, attempts, settings, accessor, backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-INST", "Installment Takeover Tea", "930T2BINST", 10m));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(attempts.Attempts, attempt => attempt.OperationKind == "ActiveSession");
        var updated = Assert.Single(attempts.Attempts, attempt => attempt.AttemptGuid == installmentAttempt.AttemptGuid);
        Assert.Equal("active-session-1", updated.SessionId);
        Assert.Equal(LocalCardPaymentAttemptStatus.Approved, updated.Status);
        Assert.NotNull(updated.AcknowledgedAt);
        Assert.True(events.IndexOf("persist-final") < events.IndexOf("acknowledge"));
    }

    [Fact]
    public async Task Takeover_ignores_an_installment_attempt_with_a_different_txn_ref()
    {
        var events = new List<string>();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var installmentAttempt = CreateExistingAttempt("Sale", null, "TXN-OTHER", session, Guid.NewGuid()) with
        {
            OperationKind = "Repayment",
            OperationGuid = Guid.NewGuid(),
            Status = LocalCardPaymentAttemptStatus.Pending
        };
        var attempts = new RecordingAttemptRepository(events, initialAttempts: [installmentAttempt]);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(
            events,
            FinalApprovedSession("active-session-1", "TXN-OLD") with
            {
                CardTransaction = VerifiedCardTransaction("TXN-OLD", 1000)
            });
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = CreateWorkflow(terminal, attempts, settings, accessor, backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-INST2", "Installment Other Tea", "930T2BINST2", 10m));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded);
        Assert.Single(attempts.Attempts, attempt => attempt.OperationKind == "ActiveSession");
        var untouched = Assert.Single(attempts.Attempts, attempt => attempt.AttemptGuid == installmentAttempt.AttemptGuid);
        Assert.Null(untouched.SessionId);
        Assert.Equal(LocalCardPaymentAttemptStatus.Pending, untouched.Status);
    }

    [Fact]
    public async Task Takeover_rejects_txn_ref_match_with_different_existing_session_without_duplicate()
    {
        var events = new List<string>();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var existing = CreateExistingAttempt("Sale", "different-session", "TXN-OLD", session, Guid.NewGuid());
        var attempts = new RecordingAttemptRepository(events, initialAttempts: [existing]);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(events, FinalApprovedSession("active-session-1", "TXN-OLD"));
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor,
            settings,
            ActivePendingSession("active-session-1", "TXN-OLD"),
            events);
        var workflow = CreateWorkflow(terminal, attempts, settings, accessor, backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-TXN-CONFLICT", "Txn Conflict Tea", "930T2BTXNC", 10m));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.False(result.CardResult?.RequiresRecovery);
        Assert.DoesNotContain(attempts.Attempts, attempt => attempt.OperationKind == "ActiveSession");
        Assert.DoesNotContain(events, item => item == "acknowledge");
        Assert.DoesNotContain(events, item => item == "new-start");
    }

    [Fact]
    public async Task Takeover_rejects_active_session_outside_current_scope_without_ack_or_new_start()
    {
        var events = new List<string>();
        var attempts = new RecordingAttemptRepository(events);
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(events, FinalApprovedSession("active-session-1", "TXN-OLD"));
        var settings = CreateBackendLinklySettings();
        var wrongScope = ActivePendingSession("active-session-1", "TXN-OLD") with { StoreCode = "S999" };
        var terminal = new TakeoverInvokingCardTerminalClient(accessor, settings, wrongScope, events);
        var workflow = CreateWorkflow(terminal, attempts, settings, accessor, backend);
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-T2B-SCOPE", "Scope Tea", "930T2BSCOPE", 10m));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.False(result.CardResult?.RequiresRecovery);
        Assert.DoesNotContain(events, item => item == "acknowledge");
        Assert.DoesNotContain(events, item => item == "new-start");
    }

    private static CardTerminalSettings CreateBackendLinklySettings()
    {
        return new CardTerminalSettings(
            CardProcessorKind.Linkly,
            CardTerminalEnvironment.Sandbox,
            "127.0.0.1",
            2011,
            null,
            null,
            null,
            CardTerminalSettings.GetSquareApiBaseUrl(CardTerminalEnvironment.Sandbox),
            TimeSpan.FromSeconds(90),
            LinklyConnectionMode.CloudBackendAsync);
    }

    private static CashPaymentWorkflowService CreateWorkflow(
        ICardTerminalClient terminal,
        ILocalCardPaymentAttemptRepository attempts,
        CardTerminalSettings settings,
        ILinklyPaymentAttemptContextAccessor accessor,
        ILinklyBackendTerminalClient backend)
    {
        return new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new StubOrderRepository(),
            new StubSyncQueueRepository(),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
            linklyPaymentAttemptContextAccessor: accessor,
            linklyBackendTerminalClient: backend);
    }

    private static LocalCardPaymentAttempt CreateExistingAttempt(
        string operationKind,
        string? sessionId,
        string? txnRef,
        PosSessionState session,
        Guid orderGuid)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem($"SKU-{operationKind}", $"{operationKind} Tea", $"930{operationKind}", 10m));
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var draft = new CardPaymentOrderDraft(
            orderGuid,
            session,
            cart.CreateSnapshot(),
            [],
            operationKind == "Refund" ? -10m : 10m,
            10m,
            operationKind == "Refund" ? "R" : "P",
            null,
            now);
        return new LocalCardPaymentAttempt(
            Guid.NewGuid(),
            sessionId,
            txnRef,
            CardProcessorKind.Linkly.ToString(),
            CardTerminalEnvironment.Sandbox.ToString(),
            nameof(LinklyConnectionMode.CloudBackendAsync),
            draft.TxnType,
            10m,
            LocalCardPaymentAttemptStatus.Recovering,
            JsonSerializer.Serialize(draft),
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            null,
            null,
            null,
            now,
            now,
            null,
            null,
            operationKind,
            operationKind == "Refund" ? orderGuid : null);
    }

    private static SellableItemDto CreateItem(string productCode, string name, string lookupCode, decimal price)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: name,
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static LinklyCloudBackendSessionResponse ActivePendingSession(string sessionId, string txnRef)
    {
        return new LinklyCloudBackendSessionResponse(
            "Sandbox",
            "S001",
            "POS-01",
            sessionId,
            "Pending",
            txnRef,
            ResponseCode: null,
            ResponseText: null,
            RecoveryAction: null,
            DisplayText: "Processing",
            CancelKeyFlag: false,
            OKKeyFlag: false,
            AcceptYesKeyFlag: false,
            DeclineNoKeyFlag: false,
            AuthoriseKeyFlag: false,
            InputType: null,
            GraphicCode: null,
            DisplayLines: null,
            ReceiptText: null,
            RecoveryCount: 0,
            ReceiptPrintedAt: null,
            ClientAcknowledgedAt: null,
            LastHttpStatus: 409,
            Notifications: []);
    }

    private static LinklyCloudBackendSessionResponse FinalApprovedSession(
        string sessionId,
        string txnRef,
        bool? transactionSuccess = true)
    {
        return new LinklyCloudBackendSessionResponse(
            "Sandbox",
            "S001",
            "POS-01",
            sessionId,
            "Completed",
            txnRef,
            ResponseCode: "00",
            ResponseText: "APPROVED",
            RecoveryAction: null,
            DisplayText: "APPROVED",
            CancelKeyFlag: false,
            OKKeyFlag: false,
            AcceptYesKeyFlag: false,
            DeclineNoKeyFlag: false,
            AuthoriseKeyFlag: false,
            InputType: null,
            GraphicCode: null,
            DisplayLines: null,
            ReceiptText: "APPROVED RECEIPT",
            RecoveryCount: 0,
            ReceiptPrintedAt: null,
            ClientAcknowledgedAt: null,
            LastHttpStatus: 200,
            Notifications: [],
            TransactionSuccess: transactionSuccess);
    }

    // 服务端核验通过的批准结果会带卡交易明细（金额按分）。
    private static LinklyCloudBackendCardTransactionDto VerifiedCardTransaction(string txnRef, long amountMinor)
    {
        return new LinklyCloudBackendCardTransactionDto(
            txnRef,
            null,
            "AUTH-T2B",
            "VISA",
            "****1111",
            "MERCHANT-T2B",
            "00",
            "APPROVED",
            "42",
            DateTimeOffset.Parse("2026-06-05T10:01:00+10:00"),
            amountMinor);
    }

    private static LinklyCloudBackendSessionResponse PendingApprovedSession(
        string sessionId,
        string txnRef,
        string? notificationTxnRef = null,
        long amountMinor = 1000)
    {
        return ActivePendingSession(sessionId, txnRef) with
        {
            LastHttpStatus = 200,
            DisplayText = "APPROVED",
            Notifications =
            [
                new LinklyCloudBackendNotificationDto(
                    "transaction",
                    $$"""{ "Response": { "Success": true, "TxnRef": "{{notificationTxnRef ?? txnRef}}", "ResponseCode": "00", "ResponseText": "APPROVED", "AmtPurchase": {{amountMinor}} } }""",
                    DateTimeOffset.UtcNow)
            ]
        };
    }

    private sealed class TakeoverInvokingCardTerminalClient(
        ILinklyPaymentAttemptContextAccessor accessor,
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse activeStatus,
        IList<string> events) : ICardTerminalClient
    {
        public LinklyActiveSessionTakeoverResult? TakeoverResult { get; private set; }

        public Guid? SeenAttemptGuid { get; private set; }

        public async Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            var context = accessor.Current;
            Assert.NotNull(context);
            Assert.NotNull(context!.TakeOverActiveSessionAsync);
            SeenAttemptGuid = context.AttemptGuid;
            TakeoverResult = await context.TakeOverActiveSessionAsync!(settings, activeStatus, cancellationToken);
            if (!TakeoverResult.Succeeded)
            {
                return new PaymentAuthorizationResult(
                    false,
                    null,
                    TakeoverResult.Message,
                    StatusKey: "linkly.backend.activeSessionRequiresRecovery");
            }

            events.Add("new-start");
            return new PaymentAuthorizationResult(true, "ANZBACKEND:TXN-NEW:session=new-session:environment=Sandbox", AuthorizedAmount: amount);
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingBackendTerminalClient(
        IList<string> events,
        LinklyCloudBackendSessionResponse finalStatus,
        Exception? ackException = null) : ILinklyBackendTerminalClient
    {
        public Task<LinklyCloudBackendSessionResponse> ResumeSessionUntilFinalAsync(
            CardTerminalSettings settings,
            LinklyCloudBackendSessionResponse activeStatus,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(finalStatus);

        public Task AcknowledgeSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            events.Add("acknowledge");
            return ackException is null
                ? Task.CompletedTask
                : Task.FromException(ackException);
        }

        public Task AcknowledgeSupervisorResolvedSessionAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default) =>
            AcknowledgeSessionAsync(settings, sessionId, cancellationToken);

        public Task<LinklyConnectionTestResult> TestConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyConnectionTestResult> TestTransactionStatusAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> PurchaseAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LinklyCloudBackendSessionResponse?>(null);

        public Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingAttemptRepository(
        IList<string> events,
        Exception? persistException = null,
        IEnumerable<LocalCardPaymentAttempt>? initialAttempts = null,
        bool rejectRefundSessionCas = false) : ILocalCardPaymentAttemptRepository
    {
        public List<LocalCardPaymentAttempt> Attempts { get; } = initialAttempts?.ToList() ?? [];

        public Task CreateAsync(LocalCardPaymentAttempt attempt, CancellationToken cancellationToken = default)
        {
            Attempts.Add(attempt);
            return Task.CompletedTask;
        }

        public Task UpdateSessionAsync(
            Guid attemptGuid,
            string sessionId,
            string? txnRef,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            Update(attemptGuid, attempt => attempt with
            {
                SessionId = sessionId,
                TxnRef = txnRef,
                Status = LocalCardPaymentAttemptStatus.SessionStarted,
                UpdatedAt = updatedAt
            });
            return Task.CompletedTask;
        }

        public Task UpdateOutcomeAsync(
            Guid attemptGuid,
            LocalCardPaymentAttemptStatus status,
            string? responseCode,
            string? responseText,
            string? paymentReference,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            if (persistException is not null)
            {
                throw persistException;
            }

            events.Add("persist-final");
            Update(attemptGuid, attempt => attempt with
            {
                Status = status,
                ResponseCode = responseCode,
                ResponseText = responseText,
                PaymentReference = paymentReference,
                CompletedAt = completedAt,
                UpdatedAt = completedAt
            });
            return Task.CompletedTask;
        }

        public Task<bool> TryUpdateRefundSessionAsync(
            Guid attemptGuid,
            string submissionToken,
            string sessionId,
            string? txnRef,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            var attempt = Attempts.Single(candidate => candidate.AttemptGuid == attemptGuid);
            if (rejectRefundSessionCas ||
                !string.Equals(attempt.SubmissionToken, submissionToken, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Update(attemptGuid, current => current with
            {
                SessionId = sessionId,
                TxnRef = txnRef,
                Status = LocalCardPaymentAttemptStatus.SessionStarted,
                UpdatedAt = updatedAt
            });
            return Task.FromResult(true);
        }

        public Task<bool> TryUpdateRefundOutcomeAsync(
            Guid attemptGuid,
            string submissionToken,
            LocalCardPaymentAttemptStatus status,
            string? responseCode,
            string? responseText,
            string? paymentReference,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            var attempt = Attempts.Single(candidate => candidate.AttemptGuid == attemptGuid);
            if (!string.Equals(attempt.SubmissionToken, submissionToken, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            events.Add("persist-final");
            Update(attemptGuid, current => current with
            {
                Status = status,
                ResponseCode = responseCode,
                ResponseText = responseText,
                PaymentReference = paymentReference,
                CompletedAt = completedAt,
                UpdatedAt = completedAt
            });
            return Task.FromResult(true);
        }

        public Task MarkOrderCompletedAsync(
            Guid attemptGuid,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            Update(attemptGuid, attempt => attempt with
            {
                Status = LocalCardPaymentAttemptStatus.OrderCompleted,
                CompletedAt = attempt.CompletedAt ?? completedAt,
                UpdatedAt = completedAt
            });
            return Task.CompletedTask;
        }

        public Task MarkAcknowledgedAsync(
            Guid attemptGuid,
            DateTimeOffset acknowledgedAt,
            CancellationToken cancellationToken = default)
        {
            Update(attemptGuid, attempt => attempt with
            {
                AcknowledgedAt = acknowledgedAt,
                UpdatedAt = acknowledgedAt
            });
            return Task.CompletedTask;
        }

        public Task MarkRecoveringAsync(
            Guid attemptGuid,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            Update(attemptGuid, attempt => attempt with
            {
                Status = LocalCardPaymentAttemptStatus.Recovering,
                UpdatedAt = updatedAt
            });
            return Task.CompletedTask;
        }

        public Task<LocalCardPaymentAttempt?> GetLatestOpenAttemptAsync(
            string storeCode,
            string deviceCode,
            string? cashierId,
            string environment,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalCardPaymentAttempt?>(Attempts.LastOrDefault());

        public Task<IReadOnlyList<LocalCardPaymentAttempt>> GetOpenRefundAttemptsAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalCardPaymentAttempt>>(Array.Empty<LocalCardPaymentAttempt>());

        public Task<IReadOnlyList<LocalCardPaymentAttempt>> GetOpenAttemptsAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalCardPaymentAttempt>>(Attempts
                .Where(attempt =>
                    string.Equals(attempt.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(attempt.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(attempt.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
                    attempt.OperationKind is "Sale" or "Refund" or "ActiveSession" &&
                    (attempt.Status is LocalCardPaymentAttemptStatus.Pending or
                        LocalCardPaymentAttemptStatus.SessionStarted or
                        LocalCardPaymentAttemptStatus.Recovering or
                        LocalCardPaymentAttemptStatus.Approved or
                        LocalCardPaymentAttemptStatus.RequiresReview))
                .OrderByDescending(attempt => attempt.UpdatedAt)
                .ToArray());

        public Task<IReadOnlyList<LocalCardPaymentAttempt>> GetUnacknowledgedInstallmentAttemptsAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalCardPaymentAttempt>>(Attempts
                .Where(attempt =>
                    string.Equals(attempt.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(attempt.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(attempt.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
                    attempt.OperationKind is "Create" or "Repayment" &&
                    attempt.AcknowledgedAt is null &&
                    attempt.TxnRef is not null)
                .ToArray());

        public Task<LocalCardPaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalCardPaymentAttempt?>(Attempts.SingleOrDefault(attempt => attempt.AttemptGuid == attemptGuid));

        private void Update(Guid attemptGuid, Func<LocalCardPaymentAttempt, LocalCardPaymentAttempt> update)
        {
            var index = Attempts.FindIndex(attempt => attempt.AttemptGuid == attemptGuid);
            Assert.True(index >= 0);
            Attempts[index] = update(Attempts[index]);
        }
    }

    private sealed class StaticCardTerminalSettingsProvider(CardTerminalSettings settings) : ICardTerminalSettingsProvider
    {
        public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);
    }

    private sealed class StubOrderRepository : ILocalOrderRepository
    {
        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOrderSummary>>(Array.Empty<LocalOrderSummary>());

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOrderSummary>>(Array.Empty<LocalOrderSummary>());

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalOrder?>(null);
    }

    private sealed class StubSyncQueueRepository : ISyncQueueRepository
    {
        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SyncQueueOverview(0, 0, 0, null));

        public Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SyncQueueListItem>>(Array.Empty<SyncQueueListItem>());
    }
}
