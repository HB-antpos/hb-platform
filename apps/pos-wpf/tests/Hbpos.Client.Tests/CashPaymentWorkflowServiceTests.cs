using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using static Hbpos.Client.Tests.SharedHeldOrderClientTestSupport;

namespace Hbpos.Client.Tests;

public sealed class CashPaymentWorkflowServiceTests
{
    [Fact]
    public void Cash_payment_workflow_rounds_cash_due_and_change_for_7_82()
    {
        var workflow = CreateWorkflow();

        var parsed = workflow.TryParseTenderedAmount("10", out var tenderedAmount);
        var remaining = CashRoundingPolicy.GetCashPayableAmount(7.82m, []);
        var change = workflow.CalculateChange("10", 7.82m);

        Assert.True(parsed);
        Assert.Equal(10m, tenderedAmount);
        Assert.Equal(7.80m, remaining);
        Assert.Equal(2.20m, change);
    }

    [Fact]
    public void Cash_payment_workflow_rounds_cash_due_and_change_for_7_83()
    {
        var workflow = CreateWorkflow();

        var remaining = CashRoundingPolicy.GetCashPayableAmount(7.83m, []);
        var change = workflow.CalculateChange("10", 7.83m);

        Assert.Equal(7.85m, remaining);
        Assert.Equal(2.15m, change);
    }

    [Fact]
    public void Cash_payment_workflow_rejects_invalid_tendered_amount()
    {
        var workflow = CreateWorkflow();

        var parsed = workflow.TryParseTenderedAmount("cash", out var tenderedAmount);
        var change = workflow.CalculateChange("cash", 7.81m);

        Assert.False(parsed);
        Assert.Equal(0m, tenderedAmount);
        Assert.Equal(0m, change);
    }

    [Fact]
    public async Task Cash_payment_workflow_persists_order_clears_cart_and_refreshes_pending_sync()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-301", "Workflow Tea", "930301", 4.4m));
        var orders = new RecordingOrderRepository();
        var syncQueue = new StubSyncQueueRepository(pendingCount: 3);
        var workflow = new CashPaymentWorkflowService(new CashCheckoutService(), orders, syncQueue);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.CompleteAsync(cart, session, "5");

        var savedOrder = Assert.Single(orders.SavedOrders);
        Assert.Same(savedOrder, result.Order);
        Assert.Equal(4.4m, savedOrder.ActualAmount);
        Assert.Equal(5m, result.TenderedAmount);
        Assert.Equal(0.6m, result.ChangeAmount);
        Assert.Empty(cart.Lines);
        Assert.Equal(3, result.PendingSyncCount);
        Assert.Equal(3, result.UpdatedSession.PendingSyncCount);
        Assert.Equal(savedOrder.OrderGuid, result.Order.OrderGuid);
    }

    [Fact]
    public async Task Cash_payment_workflow_with_open_claim_resolves_held_source_completion_context()
    {
        await using var scope = await CreateRepositoryScopeAsync();
        var holdGuid = Guid.NewGuid();
        var claimId = Guid.NewGuid();
        Assert.True(await scope.Repository.TrySavePreparedClaimAsync(new SharedHeldOrderClaimDraft(
            claimId,
            holdGuid,
            "S001",
            "POS-01",
            SharedHeldOrderClaimSource.OfflineOrigin,
            "prepare-wf",
            SampleCanonical(),
            "2026-07-28T00:00:00.000Z")));
        Assert.True(await scope.Repository.TryActivateClaimAsync(
            claimId, "prepare-wf", "activate-wf", serverRevision: null, "2026-07-28T00:00:01.000Z"));

        var cart = new PosCartService();
        // 正在结账的购物车必须确实来自该 claim：按 canonical 反向映射恢复后结账。
        cart.RestoreSharedSaleSnapshot(
            new SharedHeldOrderReverseMapper().Map(SampleCanonical(), "S001") with
            {
                SharedHeldOrderClaimId = claimId
            });
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            sharedHeldOrderRepository: scope.Repository);

        var result = await workflow.CompleteAsync(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            "11");

        var savedOrder = Assert.Single(orders.SavedOrders);
        var heldCompletion = Assert.Single(orders.HeldSources);
        Assert.Equal(savedOrder.OrderGuid, heldCompletion.Order.OrderGuid);
        Assert.Equal(holdGuid, heldCompletion.Context.HoldGuid);
        Assert.Equal(claimId, heldCompletion.Context.ClaimId);
        Assert.Equal(SharedHeldOrderClaimSource.OfflineOrigin, heldCompletion.Context.Source);
        Assert.Equal("prepare-wf", heldCompletion.Context.PrepareIdempotencyKey);
        Assert.Equal("activate-wf", heldCompletion.Context.ActivateIdempotencyKey);
    }

    [Fact]
    public async Task Cash_payment_workflow_with_edited_recalled_cart_keeps_held_source_binding()
    {
        await using var scope = await CreateRepositoryScopeAsync();
        var claimId = Guid.NewGuid();
        Assert.True(await scope.Repository.TrySavePreparedClaimAsync(new SharedHeldOrderClaimDraft(
            claimId,
            Guid.NewGuid(),
            "S001",
            "POS-01",
            SharedHeldOrderClaimSource.OfflineOrigin,
            "prepare-wf-nomatch",
            SampleCanonical(quantity: 2m),
            "2026-07-28T00:00:00.000Z")));
        Assert.True(await scope.Repository.TryActivateClaimAsync(
            claimId, "prepare-wf-nomatch", "activate-wf-nomatch", serverRevision: null, "2026-07-28T00:00:01.000Z"));

        // 召回后允许修改数量；显式 binding 仍必须随正式订单原子落盘，不能降级普通订单。
        var cart = new PosCartService();
        cart.RestoreSharedSaleSnapshot(
            new SharedHeldOrderReverseMapper().Map(SampleCanonical(quantity: 1m), "S001") with
            {
                SharedHeldOrderClaimId = claimId
            });
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            sharedHeldOrderRepository: scope.Repository);

        var result = await workflow.CompleteAsync(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            "11");

        Assert.Single(orders.SavedOrders);
        var heldCompletion = Assert.Single(orders.HeldSources);
        Assert.Equal(claimId, heldCompletion.Context.ClaimId);
        Assert.Equal(result.Order.OrderGuid, orders.SavedOrders[0].OrderGuid);
    }

    [Fact]
    public async Task Cash_payment_workflow_prepared_claim_never_binds_held_source()
    {
        await using var scope = await CreateRepositoryScopeAsync();
        var claimId = Guid.NewGuid();
        Assert.True(await scope.Repository.TrySavePreparedClaimAsync(new SharedHeldOrderClaimDraft(
            claimId,
            Guid.NewGuid(),
            "S001",
            "POS-01",
            SharedHeldOrderClaimSource.OfflineOrigin,
            "prepare-wf-prepared",
            SampleCanonical(),
            "2026-07-28T00:00:00.000Z")));

        var cart = new PosCartService();
        cart.RestoreSharedSaleSnapshot(
            new SharedHeldOrderReverseMapper().Map(SampleCanonical(), "S001"));
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            sharedHeldOrderRepository: scope.Repository);

        var result = await workflow.CompleteAsync(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            "11");

        Assert.Single(orders.SavedOrders);
        Assert.Empty(orders.HeldSources);
        Assert.Equal(result.Order.OrderGuid, orders.SavedOrders[0].OrderGuid);
    }

    [Fact]
    public async Task Cash_payment_workflow_mixed_payment_binds_matching_held_source()
    {
        await using var scope = await CreateRepositoryScopeAsync();
        var holdGuid = Guid.NewGuid();
        var claimId = Guid.NewGuid();
        Assert.True(await scope.Repository.TrySavePreparedClaimAsync(new SharedHeldOrderClaimDraft(
            claimId,
            holdGuid,
            "S001",
            "POS-01",
            SharedHeldOrderClaimSource.OfflineOrigin,
            "prepare-wf-mixed",
            SampleCanonical(),
            "2026-07-28T00:00:00.000Z")));
        Assert.True(await scope.Repository.TryActivateClaimAsync(
            claimId, "prepare-wf-mixed", "activate-wf-mixed", serverRevision: null, "2026-07-28T00:00:01.000Z"));

        var cart = new PosCartService();
        cart.RestoreSharedSaleSnapshot(
            new SharedHeldOrderReverseMapper().Map(SampleCanonical(), "S001") with
            {
                // 混合支付也必须保留已激活 claim，不能依赖 canonical 内容再次匹配。
                SharedHeldOrderClaimId = claimId
            });
        var attemptGuid = Guid.NewGuid();
        var attempts = new RecordingCardPaymentAttemptRepository();
        await attempts.CreateAsync(new LocalCardPaymentAttempt(
            attemptGuid,
            null,
            "TXN-MIXED",
            "ANZ",
            "Sandbox",
            LinklyConnectionMode.LocalIp.ToString(),
            "P",
            11m,
            LocalCardPaymentAttemptStatus.Approved,
            "{}",
            "S001",
            "POS-01",
            "C001",
            null,
            null,
            "ANZ:TXN-MIXED",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null));
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardPaymentAttemptRepository: attempts,
            sharedHeldOrderRepository: scope.Repository);

        var result = await workflow.CompletePaymentAsync(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            [new PaymentTender(
                PaymentMethodKind.Card,
                11m,
                "ANZ:TXN-MIXED",
                IdempotencyKey: $"CARD_ATTEMPT:{attemptGuid:N}")],
            cashTenderedAmount: 0m);

        var savedOrder = Assert.Single(orders.SavedOrders);
        var heldCompletion = Assert.Single(orders.HeldSources);
        Assert.Equal(savedOrder.OrderGuid, heldCompletion.Order.OrderGuid);
        Assert.Equal(holdGuid, heldCompletion.Context.HoldGuid);
        Assert.Equal(claimId, heldCompletion.Context.ClaimId);
        Assert.Equal("activate-wf-mixed", heldCompletion.Context.ActivateIdempotencyKey);
        Assert.Empty(cart.Lines);
        Assert.False(result.HasPostCommitWarning);
    }

    [Fact]
    public async Task Cash_payment_workflow_runs_blocking_local_save_off_the_calling_thread()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-UI-THREAD", "Thread Check Tea", "930UITHREAD", 4.4m));
        var orders = new RecordingOrderRepository
        {
            SaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0));
        var callingThreadId = Environment.CurrentManagedThreadId;

        var completion = workflow.CompleteAsync(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            "5");
        await orders.SaveStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.NotEqual(callingThreadId, orders.SaveThreadId);
        Assert.NotEmpty(cart.Lines);
        orders.ContinueSave.SetResult();

        await completion;
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public async Task Cash_payment_workflow_returns_completed_warning_when_pending_sync_refresh_fails()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-301W", "Workflow Warning Tea", "930301W", 4.4m));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 7);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new ThrowingPendingSyncQueueRepository());

        var result = await workflow.CompleteAsync(cart, session, "5");

        Assert.True(result.HasPostCommitWarning);
        Assert.Equal(7, result.PendingSyncCount);
        Assert.Equal(7, result.UpdatedSession.PendingSyncCount);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public async Task Cash_payment_workflow_returns_completed_warning_when_cart_clear_notification_fails()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-301C", "Cart Warning Tea", "930301C", 4.4m));
        cart.CartChanged += (_, _) => throw new InvalidOperationException("cart listener failed");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0));

        var result = await workflow.CompleteAsync(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            "5");

        Assert.True(result.HasPostCommitWarning);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public async Task Cash_payment_workflow_returns_completed_warning_when_card_attempt_marking_fails()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-301A", "Attempt Warning Tea", "930301A", 10m));
        var attemptGuid = Guid.NewGuid();
        var attempts = new RecordingCardPaymentAttemptRepository
        {
            MarkOrderCompletedException = new InvalidOperationException("attempt write failed")
        };
        await attempts.CreateAsync(new LocalCardPaymentAttempt(
            attemptGuid,
            null,
            "TXN-WARN",
            "ANZ",
            "Sandbox",
            LinklyConnectionMode.LocalIp.ToString(),
            "P",
            10m,
            LocalCardPaymentAttemptStatus.Approved,
            "{}",
            "S001",
            "POS-01",
            "C001",
            null,
            null,
            "ANZ:TXN-WARN",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardPaymentAttemptRepository: attempts);

        var result = await workflow.CompletePaymentAsync(
            cart,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            [new PaymentTender(
                PaymentMethodKind.Card,
                10m,
                "ANZ:TXN-WARN",
                IdempotencyKey: $"CARD_ATTEMPT:{attemptGuid:N}")],
            cashTenderedAmount: 0m);

        Assert.True(result.HasPostCommitWarning);
        Assert.Empty(cart.Lines);
        Assert.Single(result.Order.Payments);
    }

    [Fact]
    public async Task Linkly_recovered_refund_order_save_completes_finalize_pending_with_cas()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingCardPaymentAttemptRepository();
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new RecordingIdempotentCardRefundClient(
                new PaymentAuthorizationResult(true, "ANZ:RECOVERED-REFUND", "APPROVED", AuthorizedAmount: 4m)),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ANZ:SALE-RECOVERY",
            cartSnapshot: cart.CreateSnapshot());
        var pending = Assert.Single(attempts.Attempts);
        attempts.Attempts[0] = pending with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalCardPaymentAttemptStatus.OrderCompleted.ToString()
        };

        var result = await workflow.CompletePaymentAsync(cart, session, [tender.Tender!], cashTenderedAmount: 0m);

        var completed = Assert.Single(attempts.Attempts);
        Assert.Single(orders.SavedOrders);
        Assert.False(result.HasPostCommitWarning);
        Assert.Equal(1, attempts.FinalizeRecoveryCount);
        Assert.Equal(0, attempts.MarkOrderCompletedCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, completed.Status);
        Assert.Equal(CardRecoveryPhases.None, completed.RecoveryPhase);
        Assert.Null(completed.RecoveryTargetStatus);
    }

    [Fact]
    public async Task Linkly_recovered_refund_order_save_fails_closed_when_attempt_key_is_moved_to_non_card_tender()
    {
        var sourceCart = CreateReturnCart(4m);
        var attempts = new RecordingCardPaymentAttemptRepository();
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new RecordingIdempotentCardRefundClient(
                new PaymentAuthorizationResult(true, "ANZ:RECOVERED-NON-CARD", "APPROVED", AuthorizedAmount: 4m)),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ANZ:SALE-RECOVERY-NON-CARD",
            cartSnapshot: sourceCart.CreateSnapshot());
        var pending = Assert.Single(attempts.Attempts);
        attempts.Attempts[0] = pending with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalCardPaymentAttemptStatus.OrderCompleted.ToString()
        };
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            pending.AttemptGuid,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);
        var nonCardTender = tender.Tender! with { Method = PaymentMethodKind.Cash };

        await Assert.ThrowsAsync<CardPaymentPersistenceUnknownException>(() =>
            workflow.CompletePaymentAsync(cart, session, [nonCardTender], cashTenderedAmount: 0m));

        Assert.Empty(orders.SavedOrders);
        Assert.Equal(0, attempts.FinalizeRecoveryCount);
        var unresolved = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalCardPaymentAttemptStatus.Approved, unresolved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, unresolved.RecoveryPhase);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public async Task Linkly_recovered_refund_order_save_reports_warning_when_finalize_cas_has_no_terminal_winner()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingCardPaymentAttemptRepository { FinalizeRecoveryResult = false };
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new RecordingIdempotentCardRefundClient(
                new PaymentAuthorizationResult(true, "ANZ:RECOVERED-REFUND-CAS", "APPROVED", AuthorizedAmount: 4m)),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ANZ:SALE-RECOVERY-CAS",
            cartSnapshot: cart.CreateSnapshot());
        var pending = Assert.Single(attempts.Attempts);
        attempts.Attempts[0] = pending with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalCardPaymentAttemptStatus.OrderCompleted.ToString()
        };

        var result = await workflow.CompletePaymentAsync(cart, session, [tender.Tender!], cashTenderedAmount: 0m);

        var unresolved = Assert.Single(attempts.Attempts);
        Assert.True(result.HasPostCommitWarning);
        Assert.Equal(1, attempts.FinalizeRecoveryCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.Approved, unresolved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, unresolved.RecoveryPhase);
    }

    [Fact]
    public async Task Square_recovered_refund_order_save_completes_finalize_pending_with_cas()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new RecordingIdempotentCardRefundClient(
                new PaymentAuthorizationResult(true, "SQRF:recovered-refund", "COMPLETED", AuthorizedAmount: 4m)),
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-recovery",
            cartSnapshot: cart.CreateSnapshot());
        var pending = Assert.Single(attempts.Attempts);
        attempts.Attempts[0] = pending with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.OrderCompleted
        };

        var result = await workflow.CompletePaymentAsync(cart, session, [tender.Tender!], cashTenderedAmount: 0m);

        var completed = Assert.Single(attempts.Attempts);
        Assert.Single(orders.SavedOrders);
        Assert.False(result.HasPostCommitWarning);
        Assert.Equal(1, attempts.CompleteRecoveryFinalizationCount);
        Assert.Equal(0, attempts.MarkOrderCompletedCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, completed.Status);
        Assert.Equal(CardRecoveryPhases.None, completed.RecoveryPhase);
        Assert.Null(completed.RecoveryTargetStatus);
    }

    [Fact]
    public async Task Square_recovered_refund_order_save_fails_closed_when_attempt_key_is_moved_to_non_card_tender()
    {
        var sourceCart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new RecordingIdempotentCardRefundClient(
                new PaymentAuthorizationResult(true, "SQRF:recovered-non-card", "COMPLETED", AuthorizedAmount: 4m)),
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:recovered-non-card",
            cartSnapshot: sourceCart.CreateSnapshot());
        var pending = Assert.Single(attempts.Attempts);
        attempts.Attempts[0] = pending with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.OrderCompleted
        };
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            pending.AttemptGuid,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);
        var nonCardTender = tender.Tender! with { Method = PaymentMethodKind.Cash };

        await Assert.ThrowsAsync<CardPaymentPersistenceUnknownException>(() =>
            workflow.CompletePaymentAsync(cart, session, [nonCardTender], cashTenderedAmount: 0m));

        Assert.Empty(orders.SavedOrders);
        Assert.Equal(0, attempts.CompleteRecoveryFinalizationCount);
        var unresolved = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalSquarePaymentAttemptStatus.PaymentVerified, unresolved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, unresolved.RecoveryPhase);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public async Task Square_recovered_refund_order_save_reports_warning_when_finalize_cas_has_no_terminal_winner()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository { CompleteRecoveryFinalizationResult = false };
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new RecordingIdempotentCardRefundClient(
                new PaymentAuthorizationResult(true, "SQRF:recovered-refund-cas", "COMPLETED", AuthorizedAmount: 4m)),
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-recovery-cas",
            cartSnapshot: cart.CreateSnapshot());
        var pending = Assert.Single(attempts.Attempts);
        attempts.Attempts[0] = pending with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.OrderCompleted
        };

        var result = await workflow.CompletePaymentAsync(cart, session, [tender.Tender!], cashTenderedAmount: 0m);

        var unresolved = Assert.Single(attempts.Attempts);
        Assert.True(result.HasPostCommitWarning);
        Assert.Equal(1, attempts.CompleteRecoveryFinalizationCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.PaymentVerified, unresolved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, unresolved.RecoveryPhase);
    }

    [Fact]
    public async Task Linkly_recovered_refund_order_save_releases_owned_cart_after_finalize_cas()
    {
        var sourceCart = CreateReturnCart(4m);
        var attempts = new RecordingCardPaymentAttemptRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new RecordingIdempotentCardRefundClient(
                new PaymentAuthorizationResult(true, "ANZ:OWNED-RECOVERY", "APPROVED", AuthorizedAmount: 4m)),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ANZ:SALE-OWNED-RECOVERY",
            cartSnapshot: sourceCart.CreateSnapshot());
        var pending = Assert.Single(attempts.Attempts);
        attempts.Attempts[0] = pending with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalCardPaymentAttemptStatus.OrderCompleted.ToString()
        };
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            pending.AttemptGuid,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);

        var result = await workflow.CompletePaymentAsync(cart, session, [tender.Tender!], cashTenderedAmount: 0m);

        Assert.False(result.HasPostCommitWarning);
        Assert.True(cart.IsEmpty);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Linkly_recovered_refund_finalize_cas_failure_rolls_back_owned_cart()
    {
        var sourceCart = CreateReturnCart(4m);
        var attempts = new RecordingCardPaymentAttemptRepository { FinalizeRecoveryResult = false };
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new RecordingIdempotentCardRefundClient(
                new PaymentAuthorizationResult(true, "ANZ:OWNED-RECOVERY-FAIL", "APPROVED", AuthorizedAmount: 4m)),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ANZ:SALE-OWNED-RECOVERY-FAIL",
            cartSnapshot: sourceCart.CreateSnapshot());
        var pending = Assert.Single(attempts.Attempts);
        attempts.Attempts[0] = pending with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalCardPaymentAttemptStatus.OrderCompleted.ToString()
        };
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            pending.AttemptGuid,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);

        var result = await workflow.CompletePaymentAsync(cart, session, [tender.Tender!], cashTenderedAmount: 0m);

        Assert.True(result.HasPostCommitWarning);
        Assert.True(cart.IsEmpty);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        var unresolved = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalCardPaymentAttemptStatus.Approved, unresolved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, unresolved.RecoveryPhase);
    }

    [Fact]
    public async Task Linkly_recovered_refund_order_save_failure_rolls_back_owned_cart_and_keeps_finalize_pending()
    {
        var sourceCart = CreateReturnCart(4m);
        var attempts = new RecordingCardPaymentAttemptRepository();
        var orders = new RecordingOrderRepository
        {
            SaveException = new SqliteException("database is locked", 5)
        };
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new RecordingIdempotentCardRefundClient(
                new PaymentAuthorizationResult(true, "ANZ:OWNED-SAVE-FAIL", "APPROVED", AuthorizedAmount: 4m)),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ANZ:SALE-OWNED-SAVE-FAIL",
            cartSnapshot: sourceCart.CreateSnapshot());
        var pending = Assert.Single(attempts.Attempts);
        attempts.Attempts[0] = pending with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalCardPaymentAttemptStatus.OrderCompleted.ToString()
        };
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            pending.AttemptGuid,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);

        await Assert.ThrowsAsync<CardPaymentPersistenceUnknownException>(() =>
            workflow.CompletePaymentAsync(cart, session, [tender.Tender!], cashTenderedAmount: 0m));

        Assert.True(cart.IsEmpty);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.Empty(orders.SavedOrders);
        var unresolved = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalCardPaymentAttemptStatus.Approved, unresolved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, unresolved.RecoveryPhase);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted.ToString(), unresolved.RecoveryTargetStatus);
    }

    [Fact]
    public async Task Square_recovered_refund_order_save_releases_owned_cart_after_finalize_cas()
    {
        var sourceCart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new RecordingIdempotentCardRefundClient(
                new PaymentAuthorizationResult(true, "SQRF:owned-recovery", "COMPLETED", AuthorizedAmount: 4m)),
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:owned-recovery",
            cartSnapshot: sourceCart.CreateSnapshot());
        var pending = Assert.Single(attempts.Attempts);
        attempts.Attempts[0] = pending with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.OrderCompleted
        };
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            pending.AttemptGuid,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);

        var result = await workflow.CompletePaymentAsync(cart, session, [tender.Tender!], cashTenderedAmount: 0m);

        Assert.False(result.HasPostCommitWarning);
        Assert.True(cart.IsEmpty);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Square_recovered_refund_finalize_cas_failure_rolls_back_owned_cart()
    {
        var sourceCart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository { CompleteRecoveryFinalizationResult = false };
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new RecordingIdempotentCardRefundClient(
                new PaymentAuthorizationResult(true, "SQRF:owned-recovery-fail", "COMPLETED", AuthorizedAmount: 4m)),
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:owned-recovery-fail",
            cartSnapshot: sourceCart.CreateSnapshot());
        var pending = Assert.Single(attempts.Attempts);
        attempts.Attempts[0] = pending with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.OrderCompleted
        };
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            pending.AttemptGuid,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);

        var result = await workflow.CompletePaymentAsync(cart, session, [tender.Tender!], cashTenderedAmount: 0m);

        Assert.True(result.HasPostCommitWarning);
        Assert.True(cart.IsEmpty);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        var unresolved = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalSquarePaymentAttemptStatus.PaymentVerified, unresolved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, unresolved.RecoveryPhase);
    }

    [Fact]
    public async Task Square_recovered_refund_order_save_failure_rolls_back_owned_cart_and_keeps_finalize_pending()
    {
        var sourceCart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var orders = new RecordingOrderRepository
        {
            SaveException = new SqliteException("database is locked", 5)
        };
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new RecordingIdempotentCardRefundClient(
                new PaymentAuthorizationResult(true, "SQRF:owned-save-fail", "COMPLETED", AuthorizedAmount: 4m)),
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:owned-save-fail",
            cartSnapshot: sourceCart.CreateSnapshot());
        var pending = Assert.Single(attempts.Attempts);
        attempts.Attempts[0] = pending with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.OrderCompleted
        };
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            pending.AttemptGuid,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);

        await Assert.ThrowsAsync<CardPaymentPersistenceUnknownException>(() =>
            workflow.CompletePaymentAsync(cart, session, [tender.Tender!], cashTenderedAmount: 0m));

        Assert.True(cart.IsEmpty);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.Empty(orders.SavedOrders);
        var unresolved = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalSquarePaymentAttemptStatus.PaymentVerified, unresolved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, unresolved.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, unresolved.RecoveryTargetStatus);
    }

    [Theory]
    [InlineData(PaymentMethodKind.Cash)]
    [InlineData(PaymentMethodKind.Voucher)]
    public async Task Square_failed_refund_alternative_order_finalizes_only_after_local_save(
        PaymentMethodKind alternativeMethod)
    {
        var sourceCart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var ownerAttempt = CreateAlternativeSquareRefundAttempt(sourceCart, session);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        await attempts.CreateAsync(ownerAttempt);
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            ownerAttempt.AttemptGuid,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);
        var orders = new RecordingOrderRepository();
        var vouchers = new ApprovedVoucherTenderClient("VOUCHER:ALT-REFUND");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            voucherTenderClient: vouchers,
            squarePaymentAttemptRepository: attempts);
        var tender = new PaymentTender(
            alternativeMethod,
            -4m,
            alternativeMethod == PaymentMethodKind.Voucher ? null : "CASH:ALT-REFUND");

        var result = await workflow.CompletePaymentAsync(
            cart,
            session,
            [tender],
            cashTenderedAmount: alternativeMethod == PaymentMethodKind.Cash ? -4m : 0m);

        var savedOrder = Assert.Single(orders.SavedOrders);
        var completed = Assert.Single(attempts.Attempts);
        Assert.Equal(ownerAttempt.OperationGuid, savedOrder.OrderGuid);
        Assert.Equal(savedOrder.OrderGuid, result.Order.OrderGuid);
        Assert.Equal(1, attempts.CompleteRecoveryFinalizationCount);
        Assert.Equal(0, attempts.MarkOrderCompletedCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, completed.Status);
        Assert.Equal(CardRecoveryPhases.None, completed.RecoveryPhase);
        Assert.Null(completed.RecoveryTargetStatus);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.True(cart.IsEmpty);
        Assert.Equal(alternativeMethod == PaymentMethodKind.Voucher ? 1 : 0, vouchers.IssueRefundCallCount);
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Square_failed_refund_post_save_finalization_propagates_fatal_and_keeps_owner(
        string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal Square owner finalization")
            : new StackOverflowException("fatal Square owner finalization");
        var sourceCart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var ownerAttempt = CreateAlternativeSquareRefundAttempt(sourceCart, session);
        var attempts = new RecordingSquarePaymentAttemptRepository
        {
            CompleteRecoveryFinalizationException = fatal
        };
        await attempts.CreateAsync(ownerAttempt);
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            ownerAttempt.AttemptGuid,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            squarePaymentAttemptRepository: attempts);

        var thrown = await Record.ExceptionAsync(() =>
            workflow.CompletePaymentAsync(
                cart,
                session,
                [new PaymentTender(PaymentMethodKind.Cash, -4m, "CASH:ALT-FATAL")],
                cashTenderedAmount: -4m));

        Assert.Same(fatal, thrown);
        Assert.Single(orders.SavedOrders);
        Assert.Equal(ownerAttempt.AttemptGuid, cart.RecoveryOwnerAttemptGuid);
        Assert.False(cart.IsEmpty);
        var pending = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, pending.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, pending.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, pending.RecoveryTargetStatus);
    }

    [Fact]
    public async Task Square_recovery_owner_with_same_guid_linkly_tender_uses_square_order_identity_and_finalizes_both_exact_attempts()
    {
        var sourceCart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var sharedAttemptGuid = Guid.NewGuid();
        var linklyTender = new PaymentTender(
            PaymentMethodKind.Card,
            -1m,
            "ANZ:COLLISION-APPROVED",
            IdempotencyKey: $"CARD_ATTEMPT:{sharedAttemptGuid:N}");
        var squareOwner = CreateAlternativeSquareRefundAttempt(
            sourceCart,
            session,
            currentTenders: [linklyTender]) with
        {
            AttemptGuid = sharedAttemptGuid
        };
        var squareAttempts = new RecordingSquarePaymentAttemptRepository();
        await squareAttempts.CreateAsync(squareOwner);

        var linklyOrderGuid = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var linklyDraft = new CardPaymentOrderDraft(
            linklyOrderGuid,
            session,
            sourceCart.CreateSnapshot(),
            [],
            sourceCart.ActualAmount,
            1m,
            "R",
            "ANZ:ORIGINAL-COLLISION",
            now);
        var linklyAttempts = new RecordingCardPaymentAttemptRepository();
        await linklyAttempts.CreateAsync(new LocalCardPaymentAttempt(
            sharedAttemptGuid,
            null,
            "TXN-COLLISION-APPROVED",
            CardProcessorKind.Linkly.ToString(),
            CardTerminalEnvironment.Sandbox.ToString(),
            LinklyConnectionMode.LocalIp.ToString(),
            "R",
            1m,
            LocalCardPaymentAttemptStatus.Approved,
            JsonSerializer.Serialize(linklyDraft, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            "00",
            "APPROVED",
            linklyTender.Reference,
            now,
            now,
            now,
            null,
            "Refund",
            linklyOrderGuid));

        var cart = new PosCartService();
        var squareOwnerKey = new CardRecoveryAttemptKey(CardProcessorKind.Square, sharedAttemptGuid);
        Assert.True(cart.TryPublishRecoverySnapshot(
            squareOwnerKey,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardPaymentAttemptRepository: linklyAttempts,
            squarePaymentAttemptRepository: squareAttempts);

        var result = await workflow.CompletePaymentAsync(
            cart,
            session,
            [linklyTender, new PaymentTender(PaymentMethodKind.Cash, -3m, "CASH:COLLISION")],
            cashTenderedAmount: -3m);

        var savedOrder = Assert.Single(orders.SavedOrders);
        Assert.Equal(squareOwner.OperationGuid, savedOrder.OrderGuid);
        Assert.NotEqual(linklyOrderGuid, savedOrder.OrderGuid);
        Assert.Equal(savedOrder.OrderGuid, result.Order.OrderGuid);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, Assert.Single(linklyAttempts.Attempts).Status);
        Assert.Equal(1, linklyAttempts.MarkOrderCompletedCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, Assert.Single(squareAttempts.Attempts).Status);
        Assert.Equal(1, squareAttempts.CompleteRecoveryFinalizationCount);
        Assert.Equal(0, squareAttempts.MarkOrderCompletedCount);
        Assert.Null(cart.RecoveryOwnerAttemptKey);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public async Task Linkly_non_owner_post_order_failure_does_not_skip_exact_owner_finalization()
    {
        var sourceCart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var now = DateTimeOffset.UtcNow;
        var earlierAttemptGuid = Guid.NewGuid();
        var ownerAttemptGuid = Guid.NewGuid();
        var earlierTender = new PaymentTender(
            PaymentMethodKind.Card,
            -1m,
            "ANZ:EARLIER-APPROVED",
            IdempotencyKey: $"CARD_ATTEMPT:{earlierAttemptGuid:N}");
        var ownerTender = new PaymentTender(
            PaymentMethodKind.Card,
            -1m,
            "ANZ:OWNER-APPROVED",
            IdempotencyKey: $"CARD_ATTEMPT:{ownerAttemptGuid:N}");
        var ownerOrderGuid = Guid.NewGuid();
        var draft = new CardPaymentOrderDraft(
            ownerOrderGuid,
            session,
            sourceCart.CreateSnapshot(),
            [earlierTender, ownerTender],
            sourceCart.ActualAmount,
            1m,
            "R",
            "ANZ:ORIGINAL-MULTI",
            now);
        var attempts = new RecordingCardPaymentAttemptRepository
        {
            MarkOrderCompletedException = new InvalidOperationException("earlier Linkly finalization failed")
        };
        await attempts.CreateAsync(new LocalCardPaymentAttempt(
            earlierAttemptGuid,
            null,
            "TXN-EARLIER",
            CardProcessorKind.Linkly.ToString(),
            CardTerminalEnvironment.Sandbox.ToString(),
            LinklyConnectionMode.LocalIp.ToString(),
            "R",
            1m,
            LocalCardPaymentAttemptStatus.Approved,
            JsonSerializer.Serialize(draft, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            "00",
            "APPROVED",
            earlierTender.Reference,
            now,
            now,
            now,
            null,
            "Refund",
            ownerOrderGuid));
        await attempts.CreateAsync(new LocalCardPaymentAttempt(
            ownerAttemptGuid,
            null,
            "TXN-OWNER",
            CardProcessorKind.Linkly.ToString(),
            CardTerminalEnvironment.Sandbox.ToString(),
            LinklyConnectionMode.LocalIp.ToString(),
            "R",
            1m,
            LocalCardPaymentAttemptStatus.Approved,
            JsonSerializer.Serialize(draft, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            "00",
            "APPROVED",
            ownerTender.Reference,
            now,
            now,
            now,
            null,
            "Refund",
            ownerOrderGuid,
            RecoveryPhase: CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus: LocalCardPaymentAttemptStatus.OrderCompleted.ToString()));
        var ownerKey = new CardRecoveryAttemptKey(CardProcessorKind.Linkly, ownerAttemptGuid);
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            ownerKey,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardPaymentAttemptRepository: attempts);

        var result = await workflow.CompletePaymentAsync(
            cart,
            session,
            [earlierTender, ownerTender, new PaymentTender(PaymentMethodKind.Cash, -2m, "CASH:MULTI")],
            cashTenderedAmount: -2m);

        Assert.True(result.HasPostCommitWarning);
        Assert.Single(orders.SavedOrders);
        Assert.Equal(
            LocalCardPaymentAttemptStatus.Approved,
            attempts.Attempts.Single(attempt => attempt.AttemptGuid == earlierAttemptGuid).Status);
        Assert.Equal(
            LocalCardPaymentAttemptStatus.OrderCompleted,
            attempts.Attempts.Single(attempt => attempt.AttemptGuid == ownerAttemptGuid).Status);
        Assert.Null(cart.RecoveryOwnerAttemptKey);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public async Task Square_non_owner_post_order_failure_does_not_skip_alternative_refund_owner_finalization()
    {
        var sourceCart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var earlierAttemptGuid = Guid.NewGuid();
        var earlierTender = new PaymentTender(
            PaymentMethodKind.Card,
            -1m,
            "SQRF:EARLIER-COMPLETED",
            IdempotencyKey: $"SQUARE_ATTEMPT:{earlierAttemptGuid:N}");
        var ownerAttempt = CreateAlternativeSquareRefundAttempt(
            sourceCart,
            session,
            currentTenders: [earlierTender]);
        var earlierAttempt = ownerAttempt with
        {
            AttemptGuid = earlierAttemptGuid,
            IdempotencyKey = $"square-refund-{earlierAttemptGuid:N}",
            Status = LocalSquarePaymentAttemptStatus.PaymentVerified,
            PaymentId = "square-refund-earlier",
            PaymentStatus = "COMPLETED",
            ResponseCode = null,
            ResponseText = "Earlier Square refund completed.",
            OperationGuid = Guid.NewGuid(),
            SubmissionToken = "earlier-worker-token",
            RecoveryPhase = CardRecoveryPhases.None,
            RecoveryTargetStatus = null
        };
        var attempts = new RecordingSquarePaymentAttemptRepository
        {
            MarkOrderCompletedException = new InvalidOperationException("earlier Square finalization failed")
        };
        await attempts.CreateAsync(earlierAttempt);
        await attempts.CreateAsync(ownerAttempt);
        var ownerKey = new CardRecoveryAttemptKey(CardProcessorKind.Square, ownerAttempt.AttemptGuid);
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            ownerKey,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            squarePaymentAttemptRepository: attempts);

        var result = await workflow.CompletePaymentAsync(
            cart,
            session,
            [earlierTender, new PaymentTender(PaymentMethodKind.Cash, -3m, "CASH:SQUARE-MULTI")],
            cashTenderedAmount: -3m);

        Assert.True(result.HasPostCommitWarning);
        Assert.Single(orders.SavedOrders);
        Assert.Equal(
            LocalSquarePaymentAttemptStatus.PaymentVerified,
            attempts.Attempts.Single(attempt => attempt.AttemptGuid == earlierAttemptGuid).Status);
        Assert.Equal(
            LocalSquarePaymentAttemptStatus.Abandoned,
            attempts.Attempts.Single(attempt => attempt.AttemptGuid == ownerAttempt.AttemptGuid).Status);
        Assert.Null(cart.RecoveryOwnerAttemptKey);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public async Task Square_failed_refund_alternative_order_save_failure_rolls_back_exact_owner_and_stays_open()
    {
        var sourceCart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var ownerAttempt = CreateAlternativeSquareRefundAttempt(sourceCart, session);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        await attempts.CreateAsync(ownerAttempt);
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            ownerAttempt.AttemptGuid,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);
        var orders = new RecordingOrderRepository
        {
            SaveException = new SqliteException("database is locked", 5)
        };
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            squarePaymentAttemptRepository: attempts);

        var exception = await Assert.ThrowsAsync<CardPaymentPersistenceUnknownException>(() =>
            workflow.CompletePaymentAsync(
                cart,
                session,
                [new PaymentTender(PaymentMethodKind.Cash, -4m, "CASH:ALT-FAIL")],
                cashTenderedAmount: -4m));

        var unresolved = Assert.Single(attempts.Attempts);
        Assert.Equal(ownerAttempt.OperationGuid, exception.OrderGuid);
        Assert.Empty(orders.SavedOrders);
        Assert.Equal(0, attempts.CompleteRecoveryFinalizationCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, unresolved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, unresolved.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, unresolved.RecoveryTargetStatus);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.True(cart.IsEmpty);
    }

    [Theory]
    [InlineData("refund-line")]
    [InlineData("terminal")]
    public async Task Square_failed_refund_alternative_order_rejects_draft_identity_mismatch_before_save(
        string mismatch)
    {
        var sourceCart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var ownerAttempt = CreateAlternativeSquareRefundAttempt(sourceCart, session);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        await attempts.CreateAsync(ownerAttempt);
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            ownerAttempt.AttemptGuid,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);

        var completionSession = session;
        if (mismatch == "refund-line")
        {
            var mismatchedDraft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(
                CreateAlternativeSquareRefundAttempt(CreateReturnCart(4m), session).OrderDraftJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(mismatchedDraft);
            attempts.Attempts[0] = ownerAttempt with
            {
                OrderDraftJson = JsonSerializer.Serialize(
                    mismatchedDraft! with { OrderGuid = ownerAttempt.OperationGuid!.Value },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };
        }
        else
        {
            completionSession = session with { DeviceCode = "POS-02" };
        }

        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            squarePaymentAttemptRepository: attempts);

        await Assert.ThrowsAsync<CardPaymentPersistenceUnknownException>(() =>
            workflow.CompletePaymentAsync(
                cart,
                completionSession,
                [new PaymentTender(PaymentMethodKind.Cash, -4m, "CASH:ALT-MISMATCH")],
                cashTenderedAmount: -4m));

        Assert.Empty(orders.SavedOrders);
        var unresolved = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, unresolved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, unresolved.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, unresolved.RecoveryTargetStatus);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public async Task Square_failed_refund_saved_order_survives_finalization_cas_failure_for_restart()
    {
        var sourceCart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var ownerAttempt = CreateAlternativeSquareRefundAttempt(sourceCart, session);
        var attempts = new RecordingSquarePaymentAttemptRepository
        {
            CompleteRecoveryFinalizationResult = false
        };
        await attempts.CreateAsync(ownerAttempt);
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            ownerAttempt.AttemptGuid,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            squarePaymentAttemptRepository: attempts);

        var result = await workflow.CompletePaymentAsync(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Cash, -4m, "CASH:ALT-CAS")],
            cashTenderedAmount: -4m);

        var savedOrder = Assert.Single(orders.SavedOrders);
        var unresolved = Assert.Single(attempts.Attempts);
        Assert.True(result.HasPostCommitWarning);
        Assert.Equal(ownerAttempt.OperationGuid, savedOrder.OrderGuid);
        Assert.Equal(1, attempts.CompleteRecoveryFinalizationCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, unresolved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, unresolved.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, unresolved.RecoveryTargetStatus);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public async Task Square_failed_refund_voucher_retry_finalizes_saved_order_owner()
    {
        var sourceCart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var ownerAttempt = CreateAlternativeSquareRefundAttempt(sourceCart, session);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        await attempts.CreateAsync(ownerAttempt);
        var cart = new PosCartService();
        Assert.True(cart.TryPublishRecoverySnapshot(
            ownerAttempt.AttemptGuid,
            cart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);
        var orders = new RecordingOrderRepository();
        var vouchers = new RetriableVoucherTenderClient("VOUCHER:ALT-RETRY");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            voucherTenderClient: vouchers,
            squarePaymentAttemptRepository: attempts);

        var uploadFailure = await Assert.ThrowsAsync<PaymentUploadFailedException>(() =>
            workflow.CompletePaymentAsync(
                cart,
                session,
                [new PaymentTender(PaymentMethodKind.Voucher, -4m)],
                cashTenderedAmount: 0m));

        Assert.Single(orders.SavedOrders);
        Assert.Equal(ownerAttempt.AttemptGuid, cart.RecoveryOwnerAttemptGuid);
        Assert.Equal(CardRecoveryPhases.FinalizePending, Assert.Single(attempts.Attempts).RecoveryPhase);

        vouchers.FailIssueRefund = false;
        var retry = await workflow.RetryVoucherUploadAsync(
            uploadFailure.OrderGuid,
            cart,
            session,
            uploadFailure.TenderedAmount,
            uploadFailure.ChangeAmount);

        var completed = Assert.Single(attempts.Attempts);
        Assert.False(retry.HasPostCommitWarning);
        Assert.Equal(2, vouchers.IssueRefundCallCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, completed.Status);
        Assert.Equal(CardRecoveryPhases.None, completed.RecoveryPhase);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public async Task Square_failed_refund_pending_voucher_restart_reuses_saved_order_payment_identity()
    {
        var sourceCart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var ownerAttempt = CreateAlternativeSquareRefundAttempt(sourceCart, session);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        await attempts.CreateAsync(ownerAttempt);
        var firstCart = new PosCartService();
        Assert.True(firstCart.TryPublishRecoverySnapshot(
            ownerAttempt.AttemptGuid,
            firstCart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);
        var orders = new RecordingOrderRepository();
        var vouchers = new RetriableVoucherTenderClient("VOUCHER:ALT-RESTART");
        var firstWorkflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            voucherTenderClient: vouchers,
            squarePaymentAttemptRepository: attempts);

        await Assert.ThrowsAsync<PaymentUploadFailedException>(() =>
            firstWorkflow.CompletePaymentAsync(
                firstCart,
                session,
                [new PaymentTender(PaymentMethodKind.Voucher, -4m)],
                cashTenderedAmount: 0m));

        var savedBeforeRestart = Assert.Single(orders.SavedOrders);
        var pendingPayment = Assert.Single(savedBeforeRestart.Payments);
        Assert.Equal("VOUCHER_REFUND_PENDING", pendingPayment.Reference);
        Assert.False(string.IsNullOrWhiteSpace(pendingPayment.IdempotencyKey));

        // 模拟进程退出后由 Square recovery 重新发布同一草稿和持久化 pending tender。
        var restartedCart = new PosCartService();
        Assert.True(restartedCart.TryPublishRecoverySnapshot(
            ownerAttempt.AttemptGuid,
            restartedCart.Revision,
            sourceCart.CreateSnapshot()).Succeeded);
        vouchers.FailIssueRefund = false;
        var restartedWorkflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            voucherTenderClient: vouchers,
            squarePaymentAttemptRepository: attempts);

        var result = await restartedWorkflow.CompletePaymentAsync(
            restartedCart,
            session,
            [new PaymentTender(
                pendingPayment.Method,
                pendingPayment.Amount,
                pendingPayment.Reference,
                CardTransactions: pendingPayment.CardTransactions,
                IdempotencyKey: pendingPayment.IdempotencyKey)],
            cashTenderedAmount: 0m);

        var savedAfterRestart = Assert.Single(orders.SavedOrders);
        var issuedPayment = Assert.Single(savedAfterRestart.Payments);
        Assert.Equal(savedBeforeRestart.OrderGuid, result.Order.OrderGuid);
        Assert.Equal(pendingPayment.PaymentGuid, issuedPayment.PaymentGuid);
        Assert.Equal(pendingPayment.IdempotencyKey, issuedPayment.IdempotencyKey);
        Assert.Equal("VOUCHER:ALT-RESTART", issuedPayment.Reference);
        Assert.Equal(2, vouchers.IssueRefundCallCount);
        Assert.Equal(2, vouchers.IssueRefundIdempotencyKeys.Count);
        Assert.All(vouchers.IssueRefundIdempotencyKeys, key => Assert.Equal(pendingPayment.IdempotencyKey, key));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, Assert.Single(attempts.Attempts).Status);
        Assert.Null(restartedCart.RecoveryOwnerAttemptGuid);
        Assert.True(restartedCart.IsEmpty);
    }

    [Fact]
    public async Task Cash_payment_workflow_persists_rounded_cash_order_without_overstating_local_payment()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-302", "Workflow Soda", "930302", 7.82m));
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 2));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.CompleteAsync(cart, session, "10");

        var savedOrder = Assert.Single(orders.SavedOrders);
        var payment = Assert.Single(savedOrder.Payments);
        Assert.Equal(10m, result.TenderedAmount);
        Assert.Equal(2.20m, result.ChangeAmount);
        Assert.Equal(PaymentMethodKind.Cash, payment.Method);
        Assert.Equal(7.82m, payment.Amount);
    }

    [Fact]
    public async Task Card_tender_persists_linkly_backend_session_and_txn_ref_immediately_after_authorization()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-397", "Recoverable Card Latte", "930397", 10m));
        var orders = new RecordingOrderRepository();
        var attempts = new RecordingCardPaymentAttemptRepository();
        var linklyAttemptContextAccessor = new LinklyPaymentAttemptContextAccessor();
        var authorization = new PaymentAuthorizationResult(
            true,
            "ANZBACKEND:TXN-EARLY:session=backend-session-early:environment=Sandbox",
            "APPROVED",
            10m,
            [
                new CardTransactionDto(
                    "ANZ",
                    "TXN-EARLY",
                    null,
                    null,
                    null,
                    null,
                    null,
                    "00",
                    "APPROVED",
                    null,
                    DateTimeOffset.UtcNow,
                    10m,
                    null)
            ],
            "ANZ",
            "Sandbox",
            LinklyConnectionMode.CloudBackendAsync.ToString(),
            "P",
            "backend-session-early",
            "TXN-EARLY",
            "00",
            "APPROVED");
        var terminal = new BindingCardTerminalClient(
            linklyAttemptContextAccessor,
            authorization,
            beforeBind: () =>
            {
                var attempt = Assert.Single(attempts.Attempts);
                Assert.Equal(LocalCardPaymentAttemptStatus.Pending, attempt.Status);
                Assert.Null(attempt.SessionId);
                // 后端模式的销售引用也必须在请求终端之前落库，并经上下文交给终端客户端随请求发出；
                // 否则请求发出后断电，这一行 SessionId 与 TxnRef 皆空，恢复和主管结案都无法认领。
                Assert.Equal(LinklyLocalTxnRef.Create('P', attempt.AttemptGuid.ToString("D")), attempt.TxnRef);
                Assert.Equal(attempt.TxnRef, linklyAttemptContextAccessor.Current?.TxnRef);
            },
            afterBind: () =>
            {
                var attempt = Assert.Single(attempts.Attempts);
                Assert.Equal("backend-session-early", attempt.SessionId);
                Assert.Equal("TXN-EARLY", attempt.TxnRef);
                Assert.Equal(LocalCardPaymentAttemptStatus.SessionStarted, attempt.Status);
            });
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: linklyAttemptContextAccessor);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cancellationToken: CancellationToken.None,
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(tenderResult.Succeeded);
        var attempt = Assert.Single(attempts.Attempts);
        Assert.Equal("backend-session-early", attempt.SessionId);
        Assert.Equal("TXN-EARLY", attempt.TxnRef);
        Assert.Equal(LocalCardPaymentAttemptStatus.Approved, attempt.Status);
        Assert.Equal("ANZBACKEND:TXN-EARLY:session=backend-session-early:environment=Sandbox", attempt.PaymentReference);
        Assert.Empty(orders.SavedOrders);
    }

    [Fact]
    public async Task Card_tender_result_unknown_keeps_local_attempt_recoverable()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-397U", "Unknown Card Latte", "930397U", 10m));
        var orders = new RecordingOrderRepository();
        var attempts = new RecordingCardPaymentAttemptRepository();
        var linklyAttemptContextAccessor = new LinklyPaymentAttemptContextAccessor();
        var authorization = new PaymentAuthorizationResult(
            false,
            Message: "ANZ Linkly Cloud transaction timed out. Result unknown.",
            Processor: "ANZ",
            Environment: "Sandbox",
            ConnectionMode: LinklyConnectionMode.CloudBackendAsync.ToString(),
            TxnType: "P",
            SessionId: "backend-session-unknown",
            TxnRef: "TXN-UNKNOWN",
            StatusKey: "linkly.backend.resultUnknown",
            ResultUnknown: true);
        var terminal = new BindingCardTerminalClient(
            linklyAttemptContextAccessor,
            authorization,
            beforeBind: () => { },
            afterBind: () => { });
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: linklyAttemptContextAccessor);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cancellationToken: CancellationToken.None,
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.card.resultUnknown", tenderResult.StatusKey);
        Assert.True(tenderResult.CardResult?.RequiresRecovery);
        var attempt = Assert.Single(attempts.Attempts);
        using var draftDocument = JsonDocument.Parse(attempt.OrderDraftJson);
        var orderGuid = draftDocument.RootElement.GetProperty("orderGuid").GetGuid();
        Assert.Equal(
            new CardRecoveryAttemptKey(CardProcessorKind.Linkly, attempt.AttemptGuid),
            tenderResult.RecoveryAttemptKey);
        Assert.Equal(orderGuid, tenderResult.RecoveryOrderGuid);
        Assert.Equal("backend-session-unknown", attempt.SessionId);
        Assert.Equal("TXN-UNKNOWN", attempt.TxnRef);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, attempt.Status);
        Assert.Null(attempt.CompletedAt);
        Assert.Empty(orders.SavedOrders);
    }

    [Fact]
    public async Task Card_tender_result_unknown_without_durable_final_evidence_does_not_expose_handoff_identity()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-397U-PERSIST", "Unknown Persist Tea", "930397UPERSIST", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository
        {
            MarkRecoveringException = new InvalidOperationException("unknown state write failed")
        };
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var authorization = new PaymentAuthorizationResult(
            false,
            Message: "ANZ Linkly Cloud transaction timed out. Result unknown.",
            Processor: "ANZ",
            Environment: "Sandbox",
            ConnectionMode: LinklyConnectionMode.CloudBackendAsync.ToString(),
            TxnType: "P",
            SessionId: "backend-session-unknown-persist",
            TxnRef: "TXN-UNKNOWN-PERSIST",
            StatusKey: "linkly.backend.resultUnknown",
            ResultUnknown: true);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new BindingCardTerminalClient(accessor, authorization, () => { }, () => { }),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: accessor);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.True(result.CardResult?.RequiresRecovery);
        Assert.Null(result.RecoveryAttemptKey);
        Assert.Null(result.RecoveryOrderGuid);
        Assert.Equal(LocalCardPaymentAttemptStatus.SessionStarted, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Card_tender_cancelled_before_linkly_terminal_submission_is_cancelled_and_unlocked()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-CANCEL-PRE", "Cancelled Before Submit", "930CANCELPRE", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new CancelledCardTerminalClient(),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor());

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cancellationToken: cancellation.Token,
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.status.cardCancelled", tenderResult.StatusKey);
        Assert.Equal(CardPaymentTerminalOutcome.Cancelled, tenderResult.CardResult?.Outcome);
        var cancelledAttempt = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalCardPaymentAttemptStatus.Cancelled, cancelledAttempt.Status);
        // 后端模式的销售引用在建 attempt 时就已落库，它的存在不代表终端已接单：
        // 会话未绑定时的取消仍须按"未提交"解锁，不能像 LocalIp 那样保守锁进未知结果。
        Assert.False(string.IsNullOrWhiteSpace(cancelledAttempt.TxnRef));
        Assert.Null(cancelledAttempt.SessionId);
    }

    [Fact]
    public async Task Card_tender_cancelled_with_local_linkly_txn_ref_requires_recovery()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-CANCEL-LOCAL", "Cancelled Local Linkly", "930CANCELLOCAL", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new CancelledCardTerminalClient(),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()),
            linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor());

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cancellationToken: cancellation.Token,
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.card.resultUnknown", tenderResult.StatusKey);
        Assert.True(tenderResult.CardResult?.RequiresRecovery);
        var attempt = Assert.Single(attempts.Attempts);
        Assert.False(string.IsNullOrWhiteSpace(attempt.TxnRef));
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, attempt.Status);
    }

    [Fact]
    public async Task Card_tender_cancelled_when_attempt_lookup_fails_requires_recovery()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-CANCEL-READ", "Cancelled Attempt Read", "930CANCELREAD", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository
        {
            GetAttemptException = new InvalidOperationException("local read failed")
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new CancelledCardTerminalClient(),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor());

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cancellationToken: cancellation.Token,
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.card.resultUnknown", tenderResult.StatusKey);
        Assert.True(tenderResult.CardResult?.RequiresRecovery);
        Assert.Null(tenderResult.RecoveryAttemptKey);
        Assert.Null(tenderResult.RecoveryOrderGuid);
        Assert.Single(attempts.Attempts);
    }

    [Fact]
    public async Task Card_tender_cancelled_after_linkly_session_starts_requires_recovery()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-CANCEL-LINKLY", "Cancelled Linkly Session", "930CANCELLINKLY", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new CancellingCardTerminalClient(async cancellationToken =>
            {
                var context = accessor.Current;
                Assert.NotNull(context);
                await context!.BindSessionAsync("session-after-submit", "TXN-CANCEL", DateTimeOffset.UtcNow, cancellationToken);
            }),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: accessor);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.card.resultUnknown", tenderResult.StatusKey);
        Assert.True(tenderResult.CardResult?.RequiresRecovery);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Card_tender_cancelled_after_square_checkout_created_requires_recovery()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-CANCEL-SQUARE", "Cancelled Square Checkout", "930CANCELSQUARE", 10m));
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var accessor = new SquarePaymentAttemptContextAccessor();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new CancellingCardTerminalClient(async cancellationToken =>
            {
                var context = accessor.Current;
                Assert.NotNull(context);
                await attempts.MarkCheckoutCreatedAsync(
                    context!.AttemptGuid,
                    "checkout-after-submit",
                    "PENDING",
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }),
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts,
            squarePaymentAttemptContextAccessor: accessor);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.card.resultUnknown", tenderResult.StatusKey);
        Assert.True(tenderResult.CardResult?.RequiresRecovery);
        var attempt = Assert.Single(attempts.Attempts);
        using var draftDocument = JsonDocument.Parse(attempt.OrderDraftJson);
        var orderGuid = draftDocument.RootElement.GetProperty("orderGuid").GetGuid();
        Assert.Equal(
            new CardRecoveryAttemptKey(CardProcessorKind.Square, attempt.AttemptGuid),
            tenderResult.RecoveryAttemptKey);
        Assert.Equal(orderGuid, tenderResult.RecoveryOrderGuid);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, attempt.Status);
    }

    [Fact]
    public async Task Card_tender_terminal_exception_after_linkly_submission_requires_recovery()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-TERMINAL-EXCEPTION", "Terminal Exception Tea", "930TERMINALEX", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new ThrowingCardTerminalClient(async cancellationToken =>
            {
                var context = accessor.Current;
                Assert.NotNull(context);
                await context!.BindSessionAsync("session-after-submit", "TXN-EXCEPTION", DateTimeOffset.UtcNow, cancellationToken);
            }),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: accessor);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.card.resultUnknown", tenderResult.StatusKey);
        Assert.True(tenderResult.CardResult?.RequiresRecovery);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Card_tender_terminal_exception_before_submission_marks_attempt_failed_and_throws()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-TERMINAL-FAILED", "Terminal Failed Tea", "930TERMINALFAIL", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new ThrowingCardTerminalClient(),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot()));

        Assert.Equal("terminal transport failed", exception.Message);
        Assert.Equal(LocalCardPaymentAttemptStatus.Failed, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Local_linkly_pinpad_offline_fails_without_recovery_lock()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-PINPAD-OFFLINE", "Pinpad Offline Tea", "930PINPADOFF", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new ObservingCardTerminalClient(
                () => { },
                new PaymentAuthorizationResult(
                    false,
                    null,
                    "The card terminal (PINpad) is offline. No payment was taken.",
                    TxnType: "P",
                    ResponseCode: "PF",
                    ResponseText: "PINpad Offline",
                    StatusKey: "linkly.local.pinpadOffline")),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()),
            linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor());

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        // 刷卡机离线是确定未提交：付款页提示离线，不能留下需进恢复中心的记录。
        Assert.False(tenderResult.Succeeded);
        Assert.Equal("linkly.local.pinpadOffline", tenderResult.StatusKey);
        Assert.NotEqual(true, tenderResult.CardResult?.RequiresRecovery);
        // 终端响应码 PF 按现有口径记为 Declined（确定终态），与 TM 等终端拒绝一致。
        var attempt = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalCardPaymentAttemptStatus.Declined, attempt.Status);
        Assert.NotNull(attempt.CompletedAt);
    }

    [Fact]
    public async Task Square_card_tender_terminal_exception_after_checkout_requires_recovery()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-SQUARE-EXCEPTION", "Square Exception Tea", "930SQUAREEX", 10m));
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var accessor = new SquarePaymentAttemptContextAccessor();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new ThrowingCardTerminalClient(async cancellationToken =>
            {
                var context = accessor.Current;
                Assert.NotNull(context);
                await attempts.MarkCheckoutCreatedAsync(
                    context!.AttemptGuid,
                    "checkout-after-exception",
                    "PENDING",
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }),
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts,
            squarePaymentAttemptContextAccessor: accessor);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.card.resultUnknown", tenderResult.StatusKey);
        Assert.True(tenderResult.CardResult?.RequiresRecovery);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Card_tender_linkly_submission_binding_ignores_caller_cancellation()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-LINKLY-BIND-CANCEL", "Linkly Binding Tea", "930LINKLYBIND", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository { RejectCancelledTokens = true };
        var accessor = new LinklyPaymentAttemptContextAccessor();
        using var cancellation = new CancellationTokenSource();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new CancellingCardTerminalClient(async cancellationToken =>
            {
                cancellation.Cancel();
                var context = accessor.Current;
                Assert.NotNull(context);
                await context!.BindSessionAsync("session-bind-cancel", "TXN-BIND-CANCEL", DateTimeOffset.UtcNow, cancellationToken);
            }),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: accessor);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cancellationToken: cancellation.Token,
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.card.resultUnknown", tenderResult.StatusKey);
        Assert.True(tenderResult.CardResult?.RequiresRecovery);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Square_card_tender_checkout_binding_ignores_caller_cancellation()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-SQUARE-BIND-CANCEL", "Square Binding Tea", "930SQUAREBIND", 10m));
        var attempts = new RecordingSquarePaymentAttemptRepository { RejectCancelledTokens = true };
        var accessor = new SquarePaymentAttemptContextAccessor();
        using var cancellation = new CancellationTokenSource();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new CancellingCardTerminalClient(async cancellationToken =>
            {
                cancellation.Cancel();
                var context = accessor.Current;
                Assert.NotNull(context);
                var bindCheckout = Assert.IsType<Func<string, string?, DateTimeOffset, CancellationToken, Task>>(context!.BindCheckoutAsync);
                await bindCheckout("checkout-bind-cancel", "PENDING", DateTimeOffset.UtcNow, cancellationToken);
            }),
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts,
            squarePaymentAttemptContextAccessor: accessor);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cancellationToken: cancellation.Token,
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.card.resultUnknown", tenderResult.StatusKey);
        Assert.True(tenderResult.CardResult?.RequiresRecovery);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Square_card_tender_checkout_binding_write_failure_requires_recovery()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-SQUARE-BIND-FAIL", "Square Binding Failure Tea", "930SQUAREBINDFAIL", 10m));
        var attempts = new RecordingSquarePaymentAttemptRepository
        {
            MarkCheckoutCreatedException = new InvalidOperationException("checkout persistence failed")
        };
        var accessor = new SquarePaymentAttemptContextAccessor();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new ThrowingCardTerminalClient(async cancellationToken =>
            {
                var context = accessor.Current;
                Assert.NotNull(context);
                var bindCheckout = Assert.IsType<Func<string, string?, DateTimeOffset, CancellationToken, Task>>(context!.BindCheckoutAsync);
                await bindCheckout("checkout-bind-fail", "PENDING", DateTimeOffset.UtcNow, cancellationToken);
            }),
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts,
            squarePaymentAttemptContextAccessor: accessor);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.card.resultUnknown", tenderResult.StatusKey);
        Assert.True(tenderResult.CardResult?.RequiresRecovery);
    }

    [Fact]
    public async Task Square_sale_checkout_binding_rejects_a_stale_worker_token()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-SQUARE-STALE-SALE", "Square Stale Sale Tea", "930SQUARESTALESALE", 10m));
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var accessor = new SquarePaymentAttemptContextAccessor();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new ThrowingCardTerminalClient(async cancellationToken =>
            {
                var context = Assert.IsType<SquarePaymentAttemptContext>(accessor.Current);
                var persisted = Assert.Single(attempts.Attempts);
                Assert.False(string.IsNullOrWhiteSpace(context.SubmissionToken));
                Assert.Equal(persisted.SubmissionToken, context.SubmissionToken);
                attempts.SimulateSupervisorRetryAndNewClaim("replacement-sale-token");
                await context.BindCheckoutAsync!(
                    "stale-sale-checkout",
                    "PENDING",
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }),
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts,
            squarePaymentAttemptContextAccessor: accessor);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        var saved = Assert.Single(attempts.Attempts);
        Assert.False(result.Succeeded);
        Assert.Equal("payment.card.resultUnknown", result.StatusKey);
        Assert.True(result.CardResult?.RequiresRecovery);
        Assert.Equal("replacement-sale-token", saved.SubmissionToken);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Recovering, saved.Status);
        Assert.Null(saved.CheckoutId);
    }

    [Fact]
    public async Task Square_card_tender_unknown_local_attempt_result_requires_recovery()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-SQUARE-UNKNOWN", "Square Unknown Tea", "930SQUAREUNKNOWN", 10m));
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var context = new SquarePaymentAttemptContextAccessor();
        var terminal = new AsyncObservingCardTerminalClient(async () =>
        {
            var attempt = Assert.Single(attempts.Attempts);
            await attempts.MarkFailedAsync(
                attempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Unknown,
                null,
                null,
                null,
                "Square transport result was not confirmed.",
                DateTimeOffset.UtcNow);
        }, new PaymentAuthorizationResult(
            false,
            null,
            "Square transport result was not confirmed.",
            StatusKey: "payment.card.squareCommunicationFailed"));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts,
            squarePaymentAttemptContextAccessor: context);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.card.resultUnknown", tenderResult.StatusKey);
        Assert.True(tenderResult.CardResult?.RequiresRecovery);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Square_card_tender_explicit_checkout_cancellation_does_not_require_recovery()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-SQUARE-CANCELED", "Square Canceled Tea", "930SQUARECANCELED", 10m));
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var terminal = new AsyncObservingCardTerminalClient(async () =>
        {
            var attempt = Assert.Single(attempts.Attempts);
            await attempts.MarkCheckoutCreatedAsync(
                attempt.AttemptGuid,
                "checkout-canceled",
                "CANCELED",
                DateTimeOffset.UtcNow);
            await attempts.MarkFailedAsync(
                attempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Canceled,
                "CANCELED",
                null,
                null,
                "Customer canceled the Square payment.",
                DateTimeOffset.UtcNow);
        }, new PaymentAuthorizationResult(
            false,
            null,
            "Customer canceled the Square payment.",
            StatusKey: "payment.card.squareCanceled"));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts,
            squarePaymentAttemptContextAccessor: new SquarePaymentAttemptContextAccessor());

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.card.squareCanceled", tenderResult.StatusKey);
        Assert.False(tenderResult.CardResult?.RequiresRecovery);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Canceled, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Card_tender_authorized_amount_mismatch_marks_local_attempt_requires_review()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-398", "Mismatch Card Latte", "930398", 10m));
        var orders = new RecordingOrderRepository();
        var attempts = new RecordingCardPaymentAttemptRepository();
        var linklyAttemptContextAccessor = new LinklyPaymentAttemptContextAccessor();
        var authorization = new PaymentAuthorizationResult(
            true,
            "ANZBACKEND:TXN-MISMATCH:session=backend-session-mismatch:environment=Sandbox",
            "APPROVED",
            5m,
            [
                new CardTransactionDto(
                    "ANZ",
                    "TXN-MISMATCH",
                    null,
                    null,
                    null,
                    null,
                    null,
                    "00",
                    "APPROVED",
                    null,
                    DateTimeOffset.UtcNow,
                    5m,
                    null)
            ],
            "ANZ",
            "Sandbox",
            LinklyConnectionMode.CloudBackendAsync.ToString(),
            "P",
            "backend-session-mismatch",
            "TXN-MISMATCH",
            "00",
            "APPROVED");
        var terminal = new BindingCardTerminalClient(
            linklyAttemptContextAccessor,
            authorization,
            beforeBind: () =>
            {
                var attempt = Assert.Single(attempts.Attempts);
                Assert.Equal(LocalCardPaymentAttemptStatus.Pending, attempt.Status);
                Assert.Null(attempt.SessionId);
                // 后端模式的销售引用也必须在请求终端之前落库，并经上下文交给终端客户端随请求发出；
                // 否则请求发出后断电，这一行 SessionId 与 TxnRef 皆空，恢复和主管结案都无法认领。
                Assert.Equal(LinklyLocalTxnRef.Create('P', attempt.AttemptGuid.ToString("D")), attempt.TxnRef);
                Assert.Equal(attempt.TxnRef, linklyAttemptContextAccessor.Current?.TxnRef);
            },
            afterBind: () =>
            {
                var attempt = Assert.Single(attempts.Attempts);
                Assert.Equal("backend-session-mismatch", attempt.SessionId);
                Assert.Equal("TXN-MISMATCH", attempt.TxnRef);
                Assert.Equal(LocalCardPaymentAttemptStatus.SessionStarted, attempt.Status);
            });
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: linklyAttemptContextAccessor);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cancellationToken: CancellationToken.None,
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.card.resultUnknown", tenderResult.StatusKey);
        Assert.True(tenderResult.CardResult?.RequiresRecovery);
        Assert.Equal("Card terminal authorized amount did not match the requested amount.", tenderResult.StatusMessage);
        var attempt = Assert.Single(attempts.Attempts);
        Assert.Equal("backend-session-mismatch", attempt.SessionId);
        Assert.Equal("TXN-MISMATCH", attempt.TxnRef);
        Assert.Equal(LocalCardPaymentAttemptStatus.RequiresReview, attempt.Status);
        Assert.Equal("Card terminal authorized amount did not match the requested amount.", attempt.ResponseText);
        Assert.Empty(orders.SavedOrders);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(5)]
    public async Task Square_card_tender_approved_invalid_amount_requires_recovery(decimal authorizedAmount)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-SQUARE-AMOUNT", "Square Invalid Amount Tea", "930SQUAREAMOUNT", 10m));
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var authorization = new PaymentAuthorizationResult(
            true,
            "SQ:invalid-amount",
            "APPROVED",
            authorizedAmount,
            [new CardTransactionDto("Square", "payment-invalid", null, null, null, null, null, "00", "APPROVED", null, DateTimeOffset.UtcNow, authorizedAmount, null)],
            ResponseCode: "00",
            ResponseText: "APPROVED");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new ObservingCardTerminalClient(() => { }, authorization),
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts,
            squarePaymentAttemptContextAccessor: new SquarePaymentAttemptContextAccessor());

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        var expectedMessage = authorizedAmount <= 0m
            ? "Card terminal approved a non-positive amount. Supervisor review is required."
            : authorizedAmount > 10m
                ? "Card terminal authorized amount exceeded the remaining amount."
                : "Card terminal authorized amount did not match the requested amount.";
        Assert.False(tenderResult.Succeeded);
        Assert.Equal("payment.card.resultUnknown", tenderResult.StatusKey);
        Assert.True(tenderResult.CardResult?.RequiresRecovery);
        var attempt = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, attempt.Status);
        Assert.Equal(expectedMessage, attempt.ResponseText);
    }

    [Fact]
    public async Task Card_tender_approval_after_caller_cancellation_persists_attempt_outcome()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-CANCEL-APPROVED", "Canceled Approved Tea", "930CANCELAPPROVED", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository { RejectCancelledTokens = true };
        var accessor = new LinklyPaymentAttemptContextAccessor();
        using var cancellation = new CancellationTokenSource();
        var authorization = new PaymentAuthorizationResult(
            true,
            "ANZBACKEND:TXN-CANCEL:session=session-cancel:environment=Sandbox",
            "APPROVED",
            10m,
            [new CardTransactionDto("ANZ", "TXN-CANCEL", null, null, null, null, null, "00", "APPROVED", null, DateTimeOffset.UtcNow, 10m, null)],
            "ANZ",
            "Sandbox",
            LinklyConnectionMode.CloudBackendAsync.ToString(),
            "P",
            "session-cancel",
            "TXN-CANCEL",
            "00",
            "APPROVED");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new CancellingApprovedCardTerminalClient(accessor, cancellation, authorization),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: accessor);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cancellationToken: cancellation.Token,
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(tenderResult.Succeeded);
        Assert.Equal(LocalCardPaymentAttemptStatus.Approved, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Card_tender_approved_attempt_persistence_failure_returns_result_unknown()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-APPROVED-PERSIST", "Approved Persist Tea", "930APPROVEDPERSIST", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository
        {
            UpdateOutcomeException = new InvalidOperationException("attempt outcome write failed")
        };
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var authorization = new PaymentAuthorizationResult(
            true,
            "ANZBACKEND:TXN-PERSIST:session=session-persist:environment=Sandbox",
            "APPROVED",
            10m,
            [new CardTransactionDto("ANZ", "TXN-PERSIST", null, null, null, null, null, "00", "APPROVED", null, DateTimeOffset.UtcNow, 10m, null)],
            "ANZ",
            "Sandbox",
            LinklyConnectionMode.CloudBackendAsync.ToString(),
            "P",
            "session-persist",
            "TXN-PERSIST",
            "00",
            "APPROVED");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new BindingCardTerminalClient(accessor, authorization, () => { }, () => { }),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: accessor);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0),
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.Equal("payment.card.resultUnknown", result.StatusKey);
        Assert.True(result.CardResult?.RequiresRecovery);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Card_tender_approved_order_persists_after_caller_cancellation()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-CANCEL-SAVE", "Canceled Save Tea", "930CANCELSAVE", 10m));
        var orders = new RecordingOrderRepository { RejectCancelledTokens = true };
        var attempts = new RecordingCardPaymentAttemptRepository { RejectCancelledTokens = true };
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var authorization = new PaymentAuthorizationResult(
            true,
            "ANZBACKEND:TXN-SAVE:session=session-save:environment=Sandbox",
            "APPROVED",
            10m,
            [new CardTransactionDto("ANZ", "TXN-SAVE", null, null, null, null, null, "00", "APPROVED", null, DateTimeOffset.UtcNow, 10m, null)],
            "ANZ",
            "Sandbox",
            LinklyConnectionMode.CloudBackendAsync.ToString(),
            "P",
            "session-save",
            "TXN-SAVE",
            "00",
            "APPROVED");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: new BindingCardTerminalClient(accessor, authorization, () => { }, () => { }),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: accessor);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var completion = await workflow.CompletePaymentAsync(
            cart,
            session,
            [tenderResult.Tender!],
            cashTenderedAmount: 0m,
            cancellationToken: cancellation.Token);

        Assert.Single(orders.SavedOrders);
        Assert.Equal(completion.Order.OrderGuid, orders.SavedOrders.Single().OrderGuid);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, Assert.Single(attempts.Attempts).Status);
    }

    [Theory]
    [InlineData("prepare-io", false)]
    [InlineData("save-sqlite-busy", false)]
    [InlineData("save-sqlite-busy", true)]
    public async Task Approved_card_local_persistence_failure_starts_recovery_finalization_without_second_terminal_call(
        string failureStage,
        bool throwFromDiagnosticSubscriber)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-APPROVED-ORDER-FAIL", "Approved Order Fail Tea", "930APPROVEDORDERFAIL", 10m));
        var orders = new RecordingOrderRepository();
        var attempts = new RecordingCardPaymentAttemptRepository();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var authorization = new PaymentAuthorizationResult(
            true,
            "ANZBACKEND:TXN-ORDER-FAIL:session=session-order-fail:environment=Sandbox",
            "APPROVED",
            10m,
            [new CardTransactionDto("ANZ", "TXN-ORDER-FAIL", null, null, null, null, null, "00", "APPROVED", null, DateTimeOffset.UtcNow, 10m, null)],
            "ANZ",
            "Sandbox",
            LinklyConnectionMode.CloudBackendAsync.ToString(),
            "P",
            "session-order-fail",
            "TXN-ORDER-FAIL",
            "00",
            "APPROVED");
        var terminal = new BindingCardTerminalClient(accessor, authorization, () => { }, () => { });
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: accessor);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());
        Assert.True(tenderResult.Succeeded);
        Assert.Equal(1, terminal.AuthorizeCallCount);

        if (failureStage == "prepare-io")
        {
            attempts.GetAttemptException = new IOException("attempt read failed");
        }
        else
        {
            orders.SaveException = new SqliteException("database is locked", 5);
        }

        Action<string>? throwingLogSubscriber = null;
        if (throwFromDiagnosticSubscriber)
        {
            throwingLogSubscriber = line =>
            {
                if (line.Contains(
                        "conservative result-unknown stage=approved-order-persistence",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("diagnostic subscriber failed");
                }
            };
            ConsoleLog.LineWritten += throwingLogSubscriber;
        }

        CardPaymentPersistenceUnknownException exception;
        try
        {
            exception = await Assert.ThrowsAsync<CardPaymentPersistenceUnknownException>(() =>
                workflow.CompletePaymentAsync(
                    cart,
                    session,
                    [tenderResult.Tender!],
                    cashTenderedAmount: 0m));
        }
        finally
        {
            if (throwingLogSubscriber is not null)
            {
                ConsoleLog.LineWritten -= throwingLogSubscriber;
            }
        }

        Assert.Equal(1, terminal.AuthorizeCallCount);
        Assert.IsType(
            failureStage == "prepare-io" ? typeof(IOException) : typeof(SqliteException),
            exception.InnerException);
        var attempt = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalCardPaymentAttemptStatus.Approved, attempt.Status);
        Assert.Equal("00", attempt.ResponseCode);
        Assert.Equal("APPROVED", attempt.ResponseText);
        Assert.Equal(tenderResult.Tender!.Reference, attempt.PaymentReference);
        Assert.Equal(
            failureStage == "prepare-io" ? CardRecoveryPhases.None : CardRecoveryPhases.FinalizePending,
            attempt.RecoveryPhase);
        Assert.Equal(
            failureStage == "prepare-io" ? null : LocalCardPaymentAttemptStatus.OrderCompleted.ToString(),
            attempt.RecoveryTargetStatus);
        Assert.Equal(failureStage == "prepare-io" ? 0 : 1, attempts.PersistRecoveryOutcomeCount);
        Assert.Equal(0, attempts.TryUpdateOutcomeCount);
        Assert.Empty(orders.SavedOrders);
        Assert.Single(cart.Lines);
    }

    [Fact]
    public async Task Approved_card_persistence_finalization_cas_loser_preserves_concurrent_winner_evidence()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-APPROVED-CAS-LOSS", "Approved CAS Loss Tea", "930APPROVEDCASLOSS", 10m));
        var orders = new RecordingOrderRepository
        {
            SaveException = new SqliteException("database is locked", 5)
        };
        var attempts = new RecordingCardPaymentAttemptRepository();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var authorization = new PaymentAuthorizationResult(
            true,
            "ANZBACKEND:TXN-CAS-LOSS:session=session-cas-loss:environment=Sandbox",
            "APPROVED",
            10m,
            [new CardTransactionDto("ANZ", "TXN-CAS-LOSS", null, null, null, null, null, "00", "APPROVED", null, DateTimeOffset.UtcNow, 10m, null)],
            "ANZ",
            "Sandbox",
            LinklyConnectionMode.CloudBackendAsync.ToString(),
            "P",
            "session-cas-loss",
            "TXN-CAS-LOSS",
            "00",
            "APPROVED");
        var terminal = new BindingCardTerminalClient(accessor, authorization, () => { }, () => { });
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: accessor);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());
        var beforeRace = Assert.Single(attempts.Attempts);
        var winnerAt = beforeRace.UpdatedAt.AddSeconds(1);
        attempts.OutcomeCasWinner = beforeRace with
        {
            Status = LocalCardPaymentAttemptStatus.OrderCompleted,
            ResponseCode = "00",
            ResponseText = "CONCURRENT WINNER APPROVED",
            PaymentReference = "WINNER-PAYMENT-REFERENCE",
            CompletedAt = winnerAt,
            UpdatedAt = winnerAt
        };

        await Assert.ThrowsAsync<CardPaymentPersistenceUnknownException>(() =>
            workflow.CompletePaymentAsync(cart, session, [tender.Tender!], cashTenderedAmount: 0m));

        Assert.Equal(1, terminal.AuthorizeCallCount);
        Assert.Equal(1, attempts.PersistRecoveryOutcomeCount);
        Assert.Equal(0, attempts.TryUpdateOutcomeCount);
        var winner = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, winner.Status);
        Assert.Equal("00", winner.ResponseCode);
        Assert.Equal("CONCURRENT WINNER APPROVED", winner.ResponseText);
        Assert.Equal("WINNER-PAYMENT-REFERENCE", winner.PaymentReference);
        Assert.Equal(winnerAt, winner.UpdatedAt);
        Assert.Single(cart.Lines);
    }

    [Fact]
    public async Task Card_tender_creates_local_attempt_before_terminal_request_and_marks_order_completed_after_save()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-399", "Recoverable Card Tea", "930399", 10m));
        var orders = new RecordingOrderRepository();
        var attempts = new RecordingCardPaymentAttemptRepository();
        var linklyAttemptContextAccessor = new LinklyPaymentAttemptContextAccessor();
        var terminal = new BindingCardTerminalClient(
            linklyAttemptContextAccessor,
            new PaymentAuthorizationResult(
                true,
                "ANZBACKEND:TXN-1:session=backend-session-1:environment=Sandbox",
                "APPROVED",
                10m,
                [
                    new CardTransactionDto(
                        "ANZ",
                        "TXN-1",
                        null,
                        null,
                        null,
                        null,
                        null,
                        "00",
                        "APPROVED",
                        null,
                        DateTimeOffset.UtcNow,
                        10m,
                        null)
                ],
                "ANZ",
                "Sandbox",
                LinklyConnectionMode.CloudBackendAsync.ToString(),
                "P",
                "backend-session-1",
                "TXN-1",
                "00",
                "APPROVED"),
            beforeBind: () =>
            {
                var attempt = Assert.Single(attempts.Attempts);
                Assert.Equal(LocalCardPaymentAttemptStatus.Pending, attempt.Status);
                Assert.Contains("\"cardAmount\":10", attempt.OrderDraftJson, StringComparison.OrdinalIgnoreCase);
            },
            afterBind: () =>
            {
                var attempt = Assert.Single(attempts.Attempts);
                Assert.Equal("backend-session-1", attempt.SessionId);
                Assert.Equal("TXN-1", attempt.TxnRef);
                Assert.Equal(LocalCardPaymentAttemptStatus.SessionStarted, attempt.Status);
            });
        var backend = new RecordingLinklyBackendTerminalClient();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: linklyAttemptContextAccessor,
            linklyBackendTerminalClient: backend);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cancellationToken: CancellationToken.None,
            cartSnapshot: cart.CreateSnapshot());
        var completion = await workflow.CompletePaymentAsync(
            cart,
            session,
            [tenderResult.Tender!],
            cashTenderedAmount: 0m);

        Assert.True(tenderResult.Succeeded);
        Assert.Equal("backend-session-1", attempts.Attempts.Single().SessionId);
        Assert.Equal("TXN-1", attempts.Attempts.Single().TxnRef);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Attempts.Single().Status);
        Assert.NotNull(attempts.Attempts.Single().AcknowledgedAt);
        Assert.Equal("backend-session-1", backend.AcknowledgedSessionId);
        Assert.Equal(CardTerminalEnvironment.Sandbox, backend.AcknowledgedSettings?.Environment);
        var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(
            attempts.Attempts.Single().OrderDraftJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(draft!.OrderGuid, completion.Order.OrderGuid);
        Assert.Equal("ANZBACKEND:TXN-1:session=backend-session-1:environment=Sandbox", completion.Order.Payments.Single().Reference);
    }

    [Theory]
    [InlineData("C0", "TXN CANCELLED", LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData("TM", "OPERATOR CANCELLED", LocalCardPaymentAttemptStatus.Declined)]
    public async Task Definitive_cloud_card_failure_acknowledges_session_after_final_result_is_persisted(
        string responseCode,
        string responseText,
        LocalCardPaymentAttemptStatus expectedStatus)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-CANCELLED-ACK", "Cancelled Ack Tea", "930CANCELLEDACK", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository();
        var linklyAttemptContextAccessor = new LinklyPaymentAttemptContextAccessor();
        var terminal = new BindingCardTerminalClient(
            linklyAttemptContextAccessor,
            new PaymentAuthorizationResult(
                false,
                "ANZBACKEND:TXN-CANCELLED:session=backend-session-cancelled:environment=Sandbox",
                "ANZ Linkly Cloud transaction was cancelled.",
                null,
                [
                    new CardTransactionDto(
                        "ANZ",
                        "TXN-CANCELLED",
                        null,
                        null,
                        null,
                        null,
                        null,
                        responseCode,
                        responseText,
                        null,
                        DateTimeOffset.UtcNow,
                        10m,
                        null)
                ],
                "ANZ",
                "Sandbox",
                LinklyConnectionMode.CloudBackendAsync.ToString(),
                "P",
                "backend-session-cancelled",
                "TXN-CANCELLED",
                responseCode,
                responseText),
            beforeBind: () => { },
            afterBind: () => { });
        var backend = new RecordingLinklyBackendTerminalClient();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: linklyAttemptContextAccessor,
            linklyBackendTerminalClient: backend);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        var cancelledAttempt = Assert.Single(attempts.Attempts);
        Assert.Equal(expectedStatus, cancelledAttempt.Status);
        Assert.NotNull(cancelledAttempt.CompletedAt);
        Assert.NotNull(cancelledAttempt.AcknowledgedAt);
        Assert.Equal("backend-session-cancelled", backend.AcknowledgedSessionId);
        Assert.Equal(CardTerminalEnvironment.Sandbox, backend.AcknowledgedSettings?.Environment);
    }

    [Fact]
    public async Task Definitive_cloud_card_failure_does_not_acknowledge_when_final_result_persistence_fails()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-CANCELLED-NO-ACK", "Cancelled No Ack Tea", "930CANCELLEDNOACK", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository
        {
            UpdateOutcomeException = new InvalidOperationException("attempt outcome write failed")
        };
        var linklyAttemptContextAccessor = new LinklyPaymentAttemptContextAccessor();
        var terminal = new BindingCardTerminalClient(
            linklyAttemptContextAccessor,
            new PaymentAuthorizationResult(
                false,
                "ANZBACKEND:TXN-NO-ACK:session=backend-session-no-ack:environment=Sandbox",
                "ANZ Linkly Cloud transaction was cancelled.",
                null,
                [new CardTransactionDto("ANZ", "TXN-NO-ACK", null, null, null, null, null, "TM", "OPERATOR CANCELLED", null, DateTimeOffset.UtcNow, 10m, null)],
                "ANZ",
                "Sandbox",
                LinklyConnectionMode.CloudBackendAsync.ToString(),
                "P",
                "backend-session-no-ack",
                "TXN-NO-ACK",
                "TM",
                "OPERATOR CANCELLED"),
            beforeBind: () => { },
            afterBind: () => { });
        var backend = new RecordingLinklyBackendTerminalClient();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: linklyAttemptContextAccessor,
            linklyBackendTerminalClient: backend);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.Null(Assert.Single(attempts.Attempts).AcknowledgedAt);
        Assert.Null(backend.AcknowledgedSessionId);
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Card_order_post_commit_acknowledge_propagates_fatal_exception(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal acknowledge failure")
            : new StackOverflowException("fatal acknowledge failure");
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-ACK-FATAL", "Fatal Ack Tea", "930399F", 10m));
        var orders = new RecordingOrderRepository();
        var attempts = new RecordingCardPaymentAttemptRepository();
        var attemptGuid = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await attempts.CreateAsync(new LocalCardPaymentAttempt(
            attemptGuid,
            "backend-session-fatal",
            "TXN-ACK-FATAL",
            "ANZ",
            "Sandbox",
            LinklyConnectionMode.CloudBackendAsync.ToString(),
            "P",
            10m,
            LocalCardPaymentAttemptStatus.Approved,
            "{}",
            "S001",
            "POS-01",
            "C001",
            "00",
            "APPROVED",
            "ANZ:TXN-ACK-FATAL",
            now.AddMinutes(-1),
            now,
            now,
            null));
        var backend = new RecordingLinklyBackendTerminalClient
        {
            AcknowledgeException = fatal
        };
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyBackendTerminalClient: backend);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = new PaymentTender(
            PaymentMethodKind.Card,
            10m,
            "ANZ:TXN-ACK-FATAL",
            IdempotencyKey: $"CARD_ATTEMPT:{attemptGuid:N}");

        var thrown = await Record.ExceptionAsync(() =>
            workflow.CompletePaymentAsync(cart, session, [tender], cashTenderedAmount: 0m));

        Assert.Same(fatal, thrown);
        Assert.Single(orders.SavedOrders);
        var completed = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, completed.Status);
        Assert.Null(completed.AcknowledgedAt);
    }

    [Theory]
    [InlineData(LinklyConnectionMode.LocalIp, LinklyConnectionMode.CloudBackendAsync)]
    [InlineData(LinklyConnectionMode.CloudBackendAsync, LinklyConnectionMode.LocalIp)]
    public async Task Card_sale_uses_first_settings_snapshot_for_route_and_persisted_identity(
        LinklyConnectionMode firstMode,
        LinklyConnectionMode laterMode)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-SNAPSHOT-SALE", "Snapshot Sale Tea", "930SNAPSHOTSALE", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository();
        var squareAttempts = new RecordingSquarePaymentAttemptRepository();
        var context = new LinklyPaymentAttemptContextAccessor();
        var settingsProvider = new TxnRefSequencedCardTerminalSettingsProvider(
            firstMode == LinklyConnectionMode.LocalIp ? CreateLocalLinklySettings() : CreateBackendLinklySettings(),
            laterMode == LinklyConnectionMode.LocalIp ? CreateLocalLinklySettings() : CreateBackendLinklySettings());
        var routedLinkly = new TxnRefSnapshotLinklyTerminalClient(context);
        using var httpClient = new HttpClient();
        var configuredTerminal = new ConfiguredCardTerminalClient(
            settingsProvider,
            httpClient,
            routedLinkly,
            linklyPaymentAttemptContextAccessor: context);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: configuredTerminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: settingsProvider,
            squarePaymentAttemptRepository: squareAttempts,
            linklyPaymentAttemptContextAccessor: context);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: 10m,
            currentTenders: [],
            amountText: "10",
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded, result.StatusMessage);
        var attempt = Assert.Single(attempts.Attempts);
        Assert.Equal(CardTerminalSettings.FormatLinklyConnectionMode(firstMode), attempt.ConnectionMode);
        Assert.Equal(firstMode, routedLinkly.LastMode);
        Assert.Equal(firstMode == LinklyConnectionMode.LocalIp ? 1 : 0, routedLinkly.LocalPurchaseCalls);
        Assert.Equal(firstMode == LinklyConnectionMode.CloudBackendAsync ? 1 : 0, routedLinkly.CloudPurchaseCalls);
        Assert.Equal(attempt.AttemptGuid, routedLinkly.LastAttemptGuid);
        Assert.Equal(attempt.TxnRef, routedLinkly.LastTxnRef);
        Assert.False(string.IsNullOrWhiteSpace(attempt.TxnRef));
        Assert.Empty(squareAttempts.Attempts);
        Assert.Equal(1, settingsProvider.GetSettingsCalls);
    }

    [Theory]
    [InlineData(CardProcessorKind.Linkly, CardProcessorKind.Square)]
    [InlineData(CardProcessorKind.Square, CardProcessorKind.Linkly)]
    public async Task Card_sale_freezes_processor_and_creates_only_matching_attempt(
        CardProcessorKind firstProcessor,
        CardProcessorKind laterProcessor)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-PROCESSOR-SALE", "Processor Sale Tea", "930PROCESSORSALE", 10m));
        var linklyAttempts = new RecordingCardPaymentAttemptRepository();
        var squareAttempts = new RecordingSquarePaymentAttemptRepository();
        var linklyContext = new LinklyPaymentAttemptContextAccessor();
        var squareContext = new SquarePaymentAttemptContextAccessor();
        var settingsProvider = new TxnRefSequencedCardTerminalSettingsProvider(
            firstProcessor == CardProcessorKind.Linkly ? CreateLocalLinklySettings() : CreateSquareSettings(),
            laterProcessor == CardProcessorKind.Linkly ? CreateLocalLinklySettings() : CreateSquareSettings());
        var terminal = new SettingsBoundRecordingCardTerminalClient(linklyContext, squareContext);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: linklyAttempts,
            cardTerminalSettingsProvider: settingsProvider,
            squarePaymentAttemptRepository: squareAttempts,
            linklyPaymentAttemptContextAccessor: linklyContext,
            squarePaymentAttemptContextAccessor: squareContext);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: 10m,
            currentTenders: [],
            amountText: "10",
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded, result.StatusMessage);
        Assert.Equal(1, settingsProvider.GetSettingsCalls);
        Assert.Equal(1, terminal.BoundAuthorizeCallCount);
        Assert.Equal(0, terminal.LegacyAuthorizeCallCount);
        Assert.Equal(firstProcessor, terminal.LastSettings?.Processor);
        Assert.Equal(firstProcessor == CardProcessorKind.Linkly ? 1 : 0, linklyAttempts.Attempts.Count);
        Assert.Equal(firstProcessor == CardProcessorKind.Square ? 1 : 0, squareAttempts.Attempts.Count);
    }

    [Fact]
    public async Task Cloud_direct_sale_keeps_first_snapshot_without_creating_other_processor_attempt()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-CLOUD-DIRECT", "Cloud Direct Tea", "930CLOUDDIRECT", 10m));
        var linklyAttempts = new RecordingCardPaymentAttemptRepository();
        var squareAttempts = new RecordingSquarePaymentAttemptRepository();
        var linklyContext = new LinklyPaymentAttemptContextAccessor();
        var squareContext = new SquarePaymentAttemptContextAccessor();
        var settingsProvider = new TxnRefSequencedCardTerminalSettingsProvider(
            CreateCloudDirectLinklySettings(),
            CreateSquareSettings());
        var terminal = new SettingsBoundRecordingCardTerminalClient(linklyContext, squareContext);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: linklyAttempts,
            cardTerminalSettingsProvider: settingsProvider,
            squarePaymentAttemptRepository: squareAttempts,
            linklyPaymentAttemptContextAccessor: linklyContext,
            squarePaymentAttemptContextAccessor: squareContext);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: 10m,
            currentTenders: [],
            amountText: "10",
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded, result.StatusMessage);
        Assert.Equal(1, settingsProvider.GetSettingsCalls);
        Assert.Equal(CardProcessorKind.Linkly, terminal.LastSettings?.Processor);
        Assert.Equal(LinklyConnectionMode.CloudDirectSync, terminal.LastSettings?.LinklyConnectionMode);
        var attempt = Assert.Single(linklyAttempts.Attempts);
        Assert.Equal(LinklyConnectionMode.CloudDirectSync.ToString(), attempt.ConnectionMode);
        Assert.Equal(LinklyLocalTxnRef.Create('P', attempt.AttemptGuid.ToString("D")), attempt.TxnRef);
        Assert.Contains("\"cardAmount\":10", attempt.OrderDraftJson, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(squareAttempts.Attempts);
    }

    [Fact]
    public async Task Local_ip_card_tender_creates_recoverable_attempt_before_terminal_request_and_reuses_txn_ref()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-397", "Local Recoverable Tea", "930397", 10m));
        var orders = new RecordingOrderRepository();
        var attempts = new RecordingCardPaymentAttemptRepository();
        var linklyAttemptContextAccessor = new LinklyPaymentAttemptContextAccessor();
        LocalCardPaymentAttempt? persistedAtSubmission = null;
        var terminal = new LocalReferenceCardTerminalClient(
            linklyAttemptContextAccessor,
            () => persistedAtSubmission = attempts.Attempts.SingleOrDefault());
        var backend = new RecordingLinklyBackendTerminalClient();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()),
            linklyPaymentAttemptContextAccessor: linklyAttemptContextAccessor,
            linklyBackendTerminalClient: backend);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cancellationToken: CancellationToken.None,
            cartSnapshot: cart.CreateSnapshot());
        Assert.NotNull(persistedAtSubmission);
        Assert.Equal(LocalCardPaymentAttemptStatus.Pending, persistedAtSubmission.Status);
        Assert.Equal(LinklyConnectionMode.LocalIp.ToString(), persistedAtSubmission.ConnectionMode);
        AssertLocalTxnRef('P', persistedAtSubmission.TxnRef);
        Assert.Equal(
            LinklyLocalTxnRef.Create('P', persistedAtSubmission.AttemptGuid.ToString("D")),
            persistedAtSubmission.TxnRef);
        Assert.Contains("\"cardAmount\":10", persistedAtSubmission.OrderDraftJson, StringComparison.OrdinalIgnoreCase);
        var completion = await workflow.CompletePaymentAsync(
            cart,
            session,
            [tenderResult.Tender!],
            cashTenderedAmount: 0m);

        var attemptAfterCompletion = Assert.Single(attempts.Attempts);
        Assert.True(tenderResult.Succeeded);
        AssertLocalTxnRef('P', terminal.SeenTxnRef);
        Assert.Equal(attemptAfterCompletion.TxnRef, terminal.SeenTxnRef);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attemptAfterCompletion.Status);
        Assert.Null(attemptAfterCompletion.AcknowledgedAt);
        Assert.Null(backend.AcknowledgedSessionId);
        Assert.Equal($"ANZ:{attemptAfterCompletion.TxnRef}", completion.Order.Payments.Single().Reference);
    }

    [Fact]
    public async Task Square_card_tender_creates_dedicated_attempt_and_marks_order_completed_after_save()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-398", "Square Recoverable Tea", "930398", 10m));
        var orders = new RecordingOrderRepository();
        var squareAttempts = new RecordingSquarePaymentAttemptRepository();
        var squareContext = new SquarePaymentAttemptContextAccessor();
        var terminal = new ObservingCardTerminalClient(() =>
        {
            var attempt = Assert.Single(squareAttempts.Attempts);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Pending, attempt.Status);
            Assert.Contains("\"cardAmount\":10", attempt.OrderDraftJson, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(attempt.AttemptGuid, squareContext.Current?.AttemptGuid);
            Assert.Equal(attempt.IdempotencyKey, squareContext.Current?.IdempotencyKey);
        }, new PaymentAuthorizationResult(
            true,
            "SQ:payment-1",
            "Square",
            10m,
            [new CardTransactionDto("Square", "payment-1", null, null, null, null, null, null, "COMPLETED", null, DateTimeOffset.UtcNow, 10m, null)]));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: squareAttempts,
            squarePaymentAttemptContextAccessor: squareContext);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cancellationToken: CancellationToken.None,
            cartSnapshot: cart.CreateSnapshot());
        var completion = await workflow.CompletePaymentAsync(
            cart,
            session,
            [tenderResult.Tender!],
            cashTenderedAmount: 0m);

        Assert.True(tenderResult.Succeeded);
        var savedAttempt = Assert.Single(squareAttempts.Attempts);
        Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, savedAttempt.Status);
        Assert.Equal("SQUARE_ATTEMPT:", tenderResult.Tender!.IdempotencyKey![..15]);
        var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(
            savedAttempt.OrderDraftJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(draft!.OrderGuid, savedAttempt.OperationGuid);
        Assert.Equal(draft.OrderGuid, completion.Order.OrderGuid);
        Assert.Equal("SQ:payment-1", completion.Order.Payments.Single().Reference);
    }

    [Fact]
    public async Task Square_approved_order_save_failure_preserves_terminal_evidence()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-SQUARE-SAVE-FAIL", "Square Save Fail Tea", "930SQUARESAVEFAIL", 10m));
        var orders = new RecordingOrderRepository
        {
            SaveException = new SqliteException("database is locked", 5)
        };
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var terminal = new ObservingCardTerminalClient(
            () => { },
            new PaymentAuthorizationResult(
                true,
                "SQ:payment-save-fail",
                "COMPLETED",
                10m,
                [new CardTransactionDto(
                    "Square",
                    "payment-save-fail",
                    null,
                    null,
                    null,
                    null,
                    null,
                    "SQ00",
                    "COMPLETED",
                    null,
                    DateTimeOffset.UtcNow,
                    10m,
                    null)]));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts,
            squarePaymentAttemptContextAccessor: new SquarePaymentAttemptContextAccessor());
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());
        var persisted = Assert.Single(attempts.Attempts);
        attempts.Attempts[0] = persisted with
        {
            Status = LocalSquarePaymentAttemptStatus.PaymentVerified,
            PaymentId = "payment-save-fail",
            PaymentStatus = "COMPLETED",
            ResponseCode = "SQ00",
            ResponseText = "COMPLETED"
        };
        persisted = attempts.Attempts[0];
        var paymentId = persisted.PaymentId;
        var paymentStatus = persisted.PaymentStatus;
        var checkoutStatus = persisted.CheckoutStatus;

        await Assert.ThrowsAsync<CardPaymentPersistenceUnknownException>(() =>
            workflow.CompletePaymentAsync(cart, session, [tender.Tender!], cashTenderedAmount: 0m));

        var unresolved = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalSquarePaymentAttemptStatus.PaymentVerified, unresolved.Status);
        Assert.Equal(paymentId, unresolved.PaymentId);
        Assert.Equal(paymentStatus, unresolved.PaymentStatus);
        Assert.Equal(checkoutStatus, unresolved.CheckoutStatus);
        Assert.Equal("SQ00", unresolved.ResponseCode);
        Assert.Equal("COMPLETED", unresolved.ResponseText);
        Assert.Equal(CardRecoveryPhases.FinalizePending, unresolved.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, unresolved.RecoveryTargetStatus);
        Assert.Equal(1, attempts.BeginRecoveryFinalizationCount);
        Assert.Equal(0, attempts.TryMarkFailedCount);
        Assert.Single(cart.Lines);
    }

    [Fact]
    public async Task Square_persistence_finalization_cas_loser_preserves_concurrent_winner_evidence()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-SQUARE-CAS-LOSS", "Square CAS Loss Tea", "930SQUARECASLOSS", 10m));
        var orders = new RecordingOrderRepository
        {
            SaveException = new SqliteException("database is locked", 5)
        };
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var terminal = new ObservingCardTerminalClient(
            () => { },
            new PaymentAuthorizationResult(
                true,
                "SQ:payment-cas-loss",
                "COMPLETED",
                10m,
                [new CardTransactionDto(
                    "Square",
                    "payment-cas-loss",
                    null,
                    null,
                    null,
                    null,
                    null,
                    "SQ00",
                    "COMPLETED",
                    null,
                    DateTimeOffset.UtcNow,
                    10m,
                    null)]));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 0),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts,
            squarePaymentAttemptContextAccessor: new SquarePaymentAttemptContextAccessor());
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());
        var beforeRace = Assert.Single(attempts.Attempts) with
        {
            Status = LocalSquarePaymentAttemptStatus.PaymentVerified,
            PaymentId = "payment-cas-loss",
            PaymentStatus = "COMPLETED",
            ResponseCode = "SQ00",
            ResponseText = "COMPLETED"
        };
        attempts.Attempts[0] = beforeRace;
        var winnerAt = beforeRace.UpdatedAt.AddSeconds(1);
        attempts.BeginRecoveryFinalizationCasWinner = beforeRace with
        {
            Status = LocalSquarePaymentAttemptStatus.OrderCompleted,
            PaymentStatus = "COMPLETED",
            ResponseCode = "WINNER-SQ00",
            ResponseText = "CONCURRENT SQUARE WINNER",
            OrderCompletedAt = winnerAt,
            UpdatedAt = winnerAt
        };

        await Assert.ThrowsAsync<CardPaymentPersistenceUnknownException>(() =>
            workflow.CompletePaymentAsync(cart, session, [tender.Tender!], cashTenderedAmount: 0m));

        Assert.Equal(1, attempts.BeginRecoveryFinalizationCount);
        Assert.Equal(0, attempts.TryMarkFailedCount);
        var winner = Assert.Single(attempts.Attempts);
        Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, winner.Status);
        Assert.Equal("payment-cas-loss", winner.PaymentId);
        Assert.Equal("COMPLETED", winner.PaymentStatus);
        Assert.Equal("WINNER-SQ00", winner.ResponseCode);
        Assert.Equal("CONCURRENT SQUARE WINNER", winner.ResponseText);
        Assert.Equal(winnerAt, winner.UpdatedAt);
        Assert.Single(cart.Lines);
    }

    [Theory]
    [InlineData(
        "payment.card.squareTimedOut",
        "Square checkout timed out before the customer completed payment.",
        LocalSquarePaymentAttemptStatus.TimedOut)]
    [InlineData(
        "payment.card.squareTerminalNotPickedUp",
        "Square terminal did not pick up this checkout.",
        LocalSquarePaymentAttemptStatus.TimedOut)]
    [InlineData(
        "payment.card.squareCanceled",
        "Square checkout was not completed. Please try again.",
        LocalSquarePaymentAttemptStatus.Canceled)]
    [InlineData(
        "payment.card.squareCanceledBuyer",
        "Customer canceled the Square payment.",
        LocalSquarePaymentAttemptStatus.Canceled)]
    [InlineData(
        "payment.card.squareCanceledSeller",
        "Square checkout was canceled.",
        LocalSquarePaymentAttemptStatus.Canceled)]
    [InlineData(
        "payment.card.squareTerminalOffline",
        "Square terminal is offline. Check the terminal network and try again.",
        LocalSquarePaymentAttemptStatus.Failed)]
    public async Task Square_card_tender_preserves_friendly_failure_status_on_local_attempt(
        string statusKey,
        string message,
        LocalSquarePaymentAttemptStatus expectedAttemptStatus)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-397", "Square Failure Tea", "930397", 10m));
        var squareAttempts = new RecordingSquarePaymentAttemptRepository();
        var terminal = new ObservingCardTerminalClient(() => { }, new PaymentAuthorizationResult(
            false,
            null,
            message,
            StatusKey: statusKey));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: squareAttempts,
            squarePaymentAttemptContextAccessor: new SquarePaymentAttemptContextAccessor());
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cancellationToken: CancellationToken.None,
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(tenderResult.Succeeded);
        Assert.Equal(statusKey, tenderResult.StatusKey);
        Assert.Equal(message, tenderResult.StatusMessage);
        var savedAttempt = Assert.Single(squareAttempts.Attempts);
        Assert.Equal(expectedAttemptStatus, savedAttempt.Status);
        Assert.Equal(message, savedAttempt.ResponseText);
    }

    [Fact]
    public async Task Card_tender_fallback_success_tells_supervisor_to_change_primary_mode_in_settings()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-399", "Fallback Card Tea", "930399", 10m));
        var orders = new RecordingOrderRepository();
        var terminal = new ObservingCardTerminalClient(() => { }, new PaymentAuthorizationResult(
            true,
            "ANZCLOUD:DIRECT-FALLBACK",
            "APPROVED",
            10m,
            Processor: "ANZ",
            Environment: "Sandbox",
            ConnectionMode: LinklyConnectionMode.CloudDirectSync.ToString(),
            RequestedConnectionMode: LinklyConnectionMode.CloudBackendAsync.ToString(),
            ActualConnectionMode: LinklyConnectionMode.CloudDirectSync.ToString(),
            FallbackAttemptedModes:
            [
                LinklyConnectionMode.CloudBackendAsync.ToString(),
                LinklyConnectionMode.CloudDirectSync.ToString()
            ],
            FallbackSucceeded: true));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cancellationToken: CancellationToken.None,
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(tenderResult.Succeeded);
        Assert.Equal("payment.linklyFallback.succeeded", tenderResult.StatusKey);
        Assert.Contains("Cloud backend async", tenderResult.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Cloud direct sync", tenderResult.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("CloudBackendAsync", tenderResult.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("CloudDirectSync", tenderResult.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Settings", tenderResult.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cash_payment_workflow_keeps_local_payment_total_aligned_when_cash_rounds_down()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-304", "Rounded Down Soda", "930304", 7.82m));
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 2));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.CompletePaymentAsync(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Cash, 7.80m)],
            cashTenderedAmount: 7.80m);

        var savedOrder = Assert.Single(orders.SavedOrders);
        var payment = Assert.Single(savedOrder.Payments);
        Assert.Equal(7.80m, result.TenderedAmount);
        Assert.Equal(0m, result.ChangeAmount);
        Assert.Equal(7.82m, payment.Amount);
    }

    [Fact]
    public async Task Payment_workflow_allocates_cash_change_without_overstating_local_payments()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-303", "Workflow Soda", "930303", 7.83m));
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 2),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-001"));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.CompletePaymentAsync(
            cart,
            session,
            [
                new PaymentTender(PaymentMethodKind.Card, 5m, "CARD-001"),
                new PaymentTender(PaymentMethodKind.Cash, 2.85m)
            ],
            cashTenderedAmount: 2.85m);

        var savedOrder = Assert.Single(orders.SavedOrders);
        Assert.Equal(7.85m, result.TenderedAmount);
        Assert.Equal(0m, result.ChangeAmount);
        Assert.Collection(
            savedOrder.Payments,
            payment =>
            {
                Assert.Equal(PaymentMethodKind.Card, payment.Method);
                Assert.Equal(5m, payment.Amount);
            },
            payment =>
            {
                Assert.Equal(PaymentMethodKind.Cash, payment.Method);
                Assert.Equal(2.83m, payment.Amount);
            });
    }

    [Fact]
    public async Task Payment_workflow_add_tender_blocks_non_cash_over_remaining_and_accepts_voucher_code()
    {
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: new ApprovedVoucherTenderClient("VOUCHER-ABC"));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var blocked = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: 10m,
            currentTenders: [new PaymentTender(PaymentMethodKind.Card, 8m, "CARD-001")],
            amountText: "3");
        var voucher = await workflow.AddTenderAsync(
            PaymentMethodKind.Voucher,
            session,
            actualAmount: 10m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ABC123");

        Assert.False(blocked.Succeeded);
        Assert.Equal("payment.status.cardExceedsRemaining", blocked.StatusKey);
        Assert.True(voucher.Succeeded);
        Assert.NotNull(voucher.Tender);
        Assert.Equal(PaymentMethodKind.Voucher, voucher.Tender.Method);
        Assert.Equal(4m, voucher.Tender.Amount);
        Assert.Equal("VOUCHER-ABC", voucher.Tender.Reference);
    }

    [Fact]
    public async Task Payment_workflow_add_tender_normalizes_cash_input()
    {
        var workflow = CreateWorkflow();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var roundedDown = await workflow.AddTenderAsync(
            PaymentMethodKind.Cash,
            session,
            actualAmount: 20m,
            currentTenders: [],
            amountText: "10.02");
        var roundedUp = await workflow.AddTenderAsync(
            PaymentMethodKind.Cash,
            session,
            actualAmount: 20m,
            currentTenders: [],
            amountText: "10.03");

        Assert.True(roundedDown.Succeeded);
        Assert.NotNull(roundedDown.Tender);
        Assert.Equal(10.00m, roundedDown.Tender.Amount);
        Assert.True(roundedUp.Succeeded);
        Assert.NotNull(roundedUp.Tender);
        Assert.Equal(10.05m, roundedUp.Tender.Amount);
    }

    [Theory]
    [InlineData("10")]
    [InlineData("20")]
    public async Task Cash_tender_never_reads_card_settings_or_calls_terminal(string amountText)
    {
        var linklyAttempts = new RecordingCardPaymentAttemptRepository();
        var squareAttempts = new RecordingSquarePaymentAttemptRepository();
        var terminal = new ApprovedCardTerminalClient("CARD-MUST-NOT-RUN");
        var settingsProvider = new TxnRefSequencedCardTerminalSettingsProvider(
            CreateLocalLinklySettings(),
            CreateSquareSettings());
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: linklyAttempts,
            cardTerminalSettingsProvider: settingsProvider,
            squarePaymentAttemptRepository: squareAttempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Cash,
            session,
            actualAmount: 10m,
            currentTenders: [],
            amountText: amountText);

        Assert.True(result.Succeeded, result.StatusMessage);
        Assert.Equal(PaymentMethodKind.Cash, result.Tender?.Method);
        Assert.Equal(0, settingsProvider.GetSettingsCalls);
        Assert.Equal(0, terminal.AuthorizeCallCount);
        Assert.Empty(linklyAttempts.Attempts);
        Assert.Empty(squareAttempts.Attempts);
    }

    [Fact]
    public async Task Fully_cash_paid_order_blocks_later_card_before_settings_or_terminal()
    {
        var linklyAttempts = new RecordingCardPaymentAttemptRepository();
        var squareAttempts = new RecordingSquarePaymentAttemptRepository();
        var terminal = new ApprovedCardTerminalClient("CARD-MUST-NOT-RUN");
        var settingsProvider = new TxnRefSequencedCardTerminalSettingsProvider(
            CreateLocalLinklySettings(),
            CreateSquareSettings());
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: linklyAttempts,
            cardTerminalSettingsProvider: settingsProvider,
            squarePaymentAttemptRepository: squareAttempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: 10m,
            currentTenders: [new PaymentTender(PaymentMethodKind.Cash, 10m)],
            amountText: "10");

        Assert.False(result.Succeeded);
        Assert.Equal("payment.status.alreadyFullyPaid", result.StatusKey);
        Assert.Equal(0, settingsProvider.GetSettingsCalls);
        Assert.Equal(0, terminal.AuthorizeCallCount);
        Assert.Empty(linklyAttempts.Attempts);
        Assert.Empty(squareAttempts.Attempts);
    }

    [Theory]
    [InlineData("5", "payment.status.cardMustBeFinalTender")]
    [InlineData("11", "payment.status.cardExceedsRemaining")]
    public async Task Invalid_card_amount_is_rejected_before_settings_or_attempts(
        string amountText,
        string expectedStatusKey)
    {
        var linklyAttempts = new RecordingCardPaymentAttemptRepository();
        var squareAttempts = new RecordingSquarePaymentAttemptRepository();
        var terminal = new ApprovedCardTerminalClient("CARD-MUST-NOT-RUN");
        var settingsProvider = new TxnRefSequencedCardTerminalSettingsProvider(
            CreateLocalLinklySettings(),
            CreateSquareSettings());
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: linklyAttempts,
            cardTerminalSettingsProvider: settingsProvider,
            squarePaymentAttemptRepository: squareAttempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: 10m,
            currentTenders: [],
            amountText: amountText);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedStatusKey, result.StatusKey);
        Assert.Equal(0, settingsProvider.GetSettingsCalls);
        Assert.Equal(0, terminal.AuthorizeCallCount);
        Assert.Empty(linklyAttempts.Attempts);
        Assert.Empty(squareAttempts.Attempts);
    }

    [Fact]
    public async Task Partial_cash_then_card_for_exact_remainder_calls_card_once()
    {
        var terminal = new ApprovedCardTerminalClient("CARD-REMAINDER");
        var settingsProvider = new TxnRefSequencedCardTerminalSettingsProvider(
            CreateLocalLinklySettings(),
            CreateSquareSettings());
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: settingsProvider);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: 10m,
            currentTenders: [new PaymentTender(PaymentMethodKind.Cash, 4m)],
            amountText: "6");

        Assert.True(result.Succeeded, result.StatusMessage);
        Assert.Equal(6m, result.Tender?.Amount);
        Assert.Equal(1, settingsProvider.GetSettingsCalls);
        Assert.Equal(1, terminal.AuthorizeCallCount);
    }

    [Fact]
    public void Payment_workflow_uses_cash_rounding_after_non_cash_tender()
    {
        var workflow = CreateWorkflow();
        var remaining = CashRoundingPolicy.GetCashPayableAmount(
            7.83m,
            [new PaymentTender(PaymentMethodKind.Card, 5m, "CARD-001")]);

        Assert.Equal(2.85m, remaining);
    }

    [Fact]
    public async Task Payment_workflow_does_not_round_down_pure_non_cash_underpayment()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-305", "Card Boundary Tea", "930305", 7.82m));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-305"));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var remaining = workflow.CalculateRemainingAmount(
            7.82m,
            [new PaymentTender(PaymentMethodKind.Card, 7.80m, "CARD-305")]);

        Assert.Equal(0.02m, remaining);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.CompletePaymentAsync(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Card, 7.80m, "CARD-305")],
            cashTenderedAmount: 0m));
    }

    [Fact]
    public async Task Payment_workflow_uses_authorized_voucher_amount_for_partial_redemption()
    {
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: new ApprovedVoucherTenderClient("VOUCHER-PARTIAL", authorizedAmount: 3m));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var voucher = await workflow.AddTenderAsync(
            PaymentMethodKind.Voucher,
            session,
            actualAmount: 10m,
            currentTenders: [],
            amountText: "5",
            referenceText: "ABC123");

        Assert.True(voucher.Succeeded);
        Assert.NotNull(voucher.Tender);
        Assert.Equal(3m, voucher.Tender.Amount);
        Assert.Equal("VOUCHER-PARTIAL", voucher.Tender.Reference);
    }

    [Fact]
    public async Task Payment_workflow_blocks_duplicate_voucher_code()
    {
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: new ApprovedVoucherTenderClient("VOUCHER:ABC123:token-2"));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var duplicate = await workflow.AddTenderAsync(
            PaymentMethodKind.Voucher,
            session,
            actualAmount: 10m,
            currentTenders: [new PaymentTender(PaymentMethodKind.Voucher, 3m, "VOUCHER:ABC123:token-1")],
            amountText: "2",
            referenceText: "abc123");

        Assert.False(duplicate.Succeeded);
        Assert.Equal("payment.status.duplicateVoucher", duplicate.StatusKey);
    }

    [Fact]
    public async Task Payment_workflow_releases_voucher_reservation_from_tender_reference()
    {
        var vouchers = new ApprovedVoucherTenderClient("VOUCHER:VC100:LOCK-1:15.00");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: vouchers);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var released = await workflow.ReleaseVoucherTenderAsync(
            new PaymentTender(PaymentMethodKind.Voucher, 5m, "VOUCHER:VC100:LOCK-1:15.00"),
            session);

        Assert.True(released);
        Assert.Equal(1, vouchers.ReleaseCallCount);
        Assert.Equal("VC100", vouchers.LastReleaseVoucherCode);
        Assert.Equal("LOCK-1", vouchers.LastReleaseReservationToken);
    }

    [Fact]
    public async Task Payment_workflow_returns_false_when_voucher_release_throws()
    {
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: new ThrowingVoucherTenderClient());
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var released = await workflow.ReleaseVoucherTenderAsync(
            new PaymentTender(PaymentMethodKind.Voucher, 5m, "VOUCHER:VC100:LOCK-1:15.00"),
            session);

        Assert.False(released);
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Payment_workflow_voucher_release_propagates_fatal_exception_instance(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal voucher release failure")
            : new StackOverflowException("fatal voucher release failure");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: new ThrowingVoucherTenderClient(fatal));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => workflow.ReleaseVoucherTenderAsync(
            new PaymentTender(PaymentMethodKind.Voucher, 5m, "VOUCHER:VC100:LOCK-1:15.00"),
            session));

        Assert.Same(fatal, thrown);
    }

    [Fact]
    public async Task Payment_workflow_retries_failed_voucher_upload_without_saving_duplicate_order()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-303", "Voucher Retry Tea", "930303", 8m));
        var orders = new RecordingOrderRepository();
        var uploads = new FailingOnceOrderUploadService();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            orderUploadService: uploads);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tenders = new[]
        {
            new PaymentTender(PaymentMethodKind.Voucher, 3m, "VOUCHER:ABC123:token-1"),
            new PaymentTender(PaymentMethodKind.Cash, 5m)
        };

        var failed = await Assert.ThrowsAsync<PaymentUploadFailedException>(() => workflow.CompletePaymentAsync(
            cart,
            session,
            tenders,
            cashTenderedAmount: 5m));
        var result = await workflow.RetryVoucherUploadAsync(
            failed.OrderGuid,
            cart,
            session,
            failed.TenderedAmount,
            failed.ChangeAmount);

        Assert.Single(orders.SavedOrders);
        Assert.Equal(failed.OrderGuid, result.Order.OrderGuid);
        Assert.Equal([failed.OrderGuid, failed.OrderGuid], uploads.AttemptedOrderGuids);
        Assert.Empty(cart.Lines);
    }

    [Theory]
    [InlineData("http-timeout")]
    [InlineData("endpoint-switch")]
    public async Task Payment_workflow_voucher_upload_canceled_without_caller_cancel_keeps_saved_order_retryable(string cancelKind)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-304", "Voucher Timeout Tea", "930304", 8m));
        var orders = new RecordingOrderRepository();
        // HttpClient 超时与 API 端点切换都表现为"调用方并未取消的取消异常"，上传服务会原样抛出。
        var uploads = new CanceledOnceOrderUploadService(cancelKind == "http-timeout"
            ? new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.", new TimeoutException())
            : new OperationCanceledException("endpoint generation canceled"));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            orderUploadService: uploads);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tenders = new[]
        {
            new PaymentTender(PaymentMethodKind.Voucher, 3m, "VOUCHER:ABC123:token-1"),
            new PaymentTender(PaymentMethodKind.Cash, 5m)
        };

        // 此时订单已经落库、购物车也没清。若取消异常原样冒出去，支付页只会提示"支付未能完成"，
        // 收银员再点一次完成就会用新订单号再存一单，并复用同一张券的预占 token，两单必有一单永远传不上去。
        var failed = await Assert.ThrowsAsync<PaymentUploadFailedException>(() => workflow.CompletePaymentAsync(
            cart,
            session,
            tenders,
            cashTenderedAmount: 5m));

        var saved = Assert.Single(orders.SavedOrders);
        Assert.Equal(saved.OrderGuid, failed.OrderGuid);
        Assert.IsAssignableFrom<OperationCanceledException>(failed.InnerException);
        Assert.NotEmpty(cart.Lines);

        // 重试阶段再次超时，也必须留在"重试上传"状态，不能退回普通付款失败。
        uploads.CancelNextUpload = true;
        var retryFailed = await Assert.ThrowsAsync<PaymentUploadFailedException>(() => workflow.RetryVoucherUploadAsync(
            failed.OrderGuid,
            cart,
            session,
            failed.TenderedAmount,
            failed.ChangeAmount));
        Assert.Equal(failed.OrderGuid, retryFailed.OrderGuid);

        var result = await workflow.RetryVoucherUploadAsync(
            failed.OrderGuid,
            cart,
            session,
            failed.TenderedAmount,
            failed.ChangeAmount);

        Assert.Single(orders.SavedOrders);
        Assert.Equal(failed.OrderGuid, result.Order.OrderGuid);
        Assert.Equal([failed.OrderGuid, failed.OrderGuid, failed.OrderGuid], uploads.AttemptedOrderGuids);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public async Task Payment_workflow_voucher_upload_timeout_through_real_upload_service_retries_the_same_pending_order()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-voucher-upload-timeout-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var orders = new LocalOrderRepository(store);
            var api = new TimingOutOnceOrderSyncApiClient();
            // 用真实的上传服务与本地库，确认它在 HttpClient 超时下的真实行为：订单恢复为 Pending，并原样抛出取消异常。
            var workflow = new CashPaymentWorkflowService(
                new CashCheckoutService(),
                orders,
                new SyncQueueRepository(store),
                orderUploadService: new OrderUploadService(orders, api, new LocalOrderUploadRepository(store)));
            var cart = new PosCartService();
            cart.AddItem(CreateItem("SKU-306", "Voucher Real Upload Tea", "930306", 8m));
            var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

            var failed = await Assert.ThrowsAsync<PaymentUploadFailedException>(() => workflow.CompletePaymentAsync(
                cart,
                session,
                [
                    new PaymentTender(PaymentMethodKind.Voucher, 3m, "VOUCHER:ABC123:token-1"),
                    new PaymentTender(PaymentMethodKind.Cash, 5m)
                ],
                cashTenderedAmount: 5m));

            var pending = Assert.Single(await orders.GetRecentOrdersAsync());
            Assert.Equal(failed.OrderGuid, pending.OrderGuid);
            Assert.Equal("Pending", pending.SyncStatus);

            var result = await workflow.RetryVoucherUploadAsync(
                failed.OrderGuid,
                cart,
                session,
                failed.TenderedAmount,
                failed.ChangeAmount);

            var synced = Assert.Single(await orders.GetRecentOrdersAsync());
            Assert.Equal(failed.OrderGuid, result.Order.OrderGuid);
            Assert.Equal("Synced", synced.SyncStatus);
            Assert.Equal([failed.OrderGuid, failed.OrderGuid], api.RequestedOrderGuids);
            Assert.Empty(cart.Lines);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Payment_workflow_refund_voucher_issue_timeout_keeps_saved_order_and_idempotency_key_for_retry()
    {
        var cart = CreateReturnCart(6m);
        var orders = new RecordingOrderRepository();
        var vouchers = new TimingOutOnceVoucherTenderClient("VOUCHER_REFUND:RF-TIMEOUT");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: vouchers);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        // 发券请求超时时服务端可能已经发出退款券。必须用同一订单、同一幂等键重试；
        // 若被当成普通付款失败，再点完成会生成新订单号和新幂等键，给顾客再发一张券。
        var failed = await Assert.ThrowsAsync<PaymentUploadFailedException>(() => workflow.CompletePaymentAsync(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Voucher, -6m, "VOUCHER_REFUND_PENDING")],
            cashTenderedAmount: 0m));

        var savedBeforeRetry = Assert.Single(orders.SavedOrders);
        Assert.Equal(savedBeforeRetry.OrderGuid, failed.OrderGuid);
        var result = await workflow.RetryVoucherUploadAsync(
            failed.OrderGuid,
            cart,
            session,
            failed.TenderedAmount,
            failed.ChangeAmount);

        Assert.Single(orders.SavedOrders);
        Assert.Equal(2, vouchers.IssueRefundIdempotencyKeys.Count);
        Assert.Equal(vouchers.IssueRefundIdempotencyKeys[0], vouchers.IssueRefundIdempotencyKeys[1]);
        Assert.Equal("VOUCHER_REFUND:RF-TIMEOUT", Assert.Single(result.Order.Payments).Reference);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public async Task Payment_workflow_voucher_upload_still_propagates_cancellation_requested_by_the_caller()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-305", "Voucher Caller Cancel Tea", "930305", 8m));
        using var callerCancellation = new CancellationTokenSource();
        var uploads = new CallerCanceledOrderUploadService(callerCancellation);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            orderUploadService: uploads);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        // 调用方自己发起的取消保持 .NET 惯例原样传播；只有"不是调用方取消"的取消才折算成上传失败。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workflow.CompletePaymentAsync(
            cart,
            session,
            [
                new PaymentTender(PaymentMethodKind.Voucher, 3m, "VOUCHER:ABC123:token-1"),
                new PaymentTender(PaymentMethodKind.Cash, 5m)
            ],
            cashTenderedAmount: 5m,
            callerCancellation.Token));
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Payment_workflow_refund_voucher_issue_and_retry_propagate_fatal_exception_instance(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal refund voucher issue")
            : new StackOverflowException("fatal refund voucher issue");
        var cart = CreateReturnCart(6m);
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: new FatalVoucherTenderClient(fatal));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = new PaymentTender(PaymentMethodKind.Voucher, -6m, "VOUCHER_REFUND_PENDING");

        var thrown = await Record.ExceptionAsync(() => workflow.CompletePaymentAsync(
            cart,
            session,
            [tender],
            cashTenderedAmount: 0m));

        Assert.Same(fatal, thrown);
        var savedOrder = Assert.Single(orders.SavedOrders);
        var retryThrown = await Record.ExceptionAsync(() => workflow.RetryVoucherUploadAsync(
            savedOrder.OrderGuid,
            cart,
            session,
            tenderedAmount: 0m,
            changeAmount: 0m));

        Assert.Same(fatal, retryThrown);
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Payment_workflow_voucher_upload_and_retry_propagate_fatal_exception_instance(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal voucher upload")
            : new StackOverflowException("fatal voucher upload");
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-303-FATAL", "Fatal Voucher Upload Tea", "930303-FATAL", 8m));
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            orderUploadService: new FatalOrderUploadService(fatal));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tenders = new[]
        {
            new PaymentTender(PaymentMethodKind.Voucher, 3m, "VOUCHER:ABC123:token-fatal"),
            new PaymentTender(PaymentMethodKind.Cash, 5m)
        };

        var thrown = await Record.ExceptionAsync(() => workflow.CompletePaymentAsync(
            cart,
            session,
            tenders,
            cashTenderedAmount: 5m));

        Assert.Same(fatal, thrown);
        var savedOrder = Assert.Single(orders.SavedOrders);
        var retryThrown = await Record.ExceptionAsync(() => workflow.RetryVoucherUploadAsync(
            savedOrder.OrderGuid,
            cart,
            session,
            tenderedAmount: 5m,
            changeAmount: 0m));

        Assert.Same(fatal, retryThrown);
    }

    [Fact]
    public async Task Payment_workflow_adds_negative_cash_tender_for_refund()
    {
        var workflow = CreateWorkflow();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Cash,
            session,
            actualAmount: -7.82m,
            currentTenders: [],
            amountText: "7.82");

        Assert.True(tender.Succeeded);
        Assert.NotNull(tender.Tender);
        Assert.Equal(-7.80m, tender.Tender.Amount);
    }

    [Fact]
    public async Task Payment_workflow_blocks_partial_card_before_terminal_authorization()
    {
        var cardTerminal = new ApprovedCardTerminalClient("CARD-PARTIAL");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: cardTerminal);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: 10m,
            currentTenders: [],
            amountText: "5");

        Assert.False(tender.Succeeded);
        Assert.Equal("payment.status.cardMustBeFinalTender", tender.StatusKey);
        Assert.Equal(0, cardTerminal.AuthorizeCallCount);
    }

    [Fact]
    public async Task Payment_workflow_adds_negative_card_tender_for_refund()
    {
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-REFUND"));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -10m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-1");

        Assert.True(tender.Succeeded);
        Assert.NotNull(tender.Tender);
        Assert.Equal(-4m, tender.Tender.Amount);
        Assert.True(CardRefundReference.TryGetOriginalReference(tender.Tender.Reference, out var originalReference));
        Assert.Equal("SQ:payment-1", originalReference);
        Assert.Equal("REFUND:SQ:payment-1", CardRefundReference.GetDisplayReference(tender.Tender.Reference));
    }

    [Theory]
    [InlineData(LinklyConnectionMode.LocalIp, LinklyConnectionMode.CloudBackendAsync)]
    [InlineData(LinklyConnectionMode.CloudBackendAsync, LinklyConnectionMode.LocalIp)]
    public async Task Card_refund_uses_first_settings_snapshot_for_route_and_persisted_identity(
        LinklyConnectionMode firstMode,
        LinklyConnectionMode laterMode)
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingCardPaymentAttemptRepository();
        var squareAttempts = new RecordingSquarePaymentAttemptRepository();
        var context = new LinklyPaymentAttemptContextAccessor();
        var settingsProvider = new TxnRefSequencedCardTerminalSettingsProvider(
            firstMode == LinklyConnectionMode.LocalIp ? CreateLocalLinklySettings() : CreateBackendLinklySettings(),
            laterMode == LinklyConnectionMode.LocalIp ? CreateLocalLinklySettings() : CreateBackendLinklySettings());
        var routedLinkly = new TxnRefSnapshotLinklyTerminalClient(context);
        using var httpClient = new HttpClient();
        var configuredTerminal = new ConfiguredCardTerminalClient(
            settingsProvider,
            httpClient,
            routedLinkly,
            linklyPaymentAttemptContextAccessor: context);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: configuredTerminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: settingsProvider,
            squarePaymentAttemptRepository: squareAttempts,
            linklyPaymentAttemptContextAccessor: context);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ANZ:SNAPSHOT-SALE",
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded, result.StatusMessage);
        var attempt = Assert.Single(attempts.Attempts);
        Assert.Equal(CardTerminalSettings.FormatLinklyConnectionMode(firstMode), attempt.ConnectionMode);
        Assert.Equal(firstMode, routedLinkly.LastMode);
        Assert.Equal(firstMode == LinklyConnectionMode.LocalIp ? 1 : 0, routedLinkly.LocalRefundCalls);
        Assert.Equal(firstMode == LinklyConnectionMode.CloudBackendAsync ? 1 : 0, routedLinkly.CloudRefundCalls);
        Assert.Equal(attempt.AttemptGuid, routedLinkly.LastAttemptGuid);
        Assert.Equal(attempt.TxnRef, routedLinkly.LastTxnRef);
        Assert.Equal(attempt.TxnRef, routedLinkly.LastRefundIdempotencyKey);
        Assert.Empty(squareAttempts.Attempts);
        Assert.Equal(1, settingsProvider.GetSettingsCalls);
    }

    [Theory]
    [InlineData(CardProcessorKind.Linkly, CardProcessorKind.Square)]
    [InlineData(CardProcessorKind.Square, CardProcessorKind.Linkly)]
    public async Task Card_refund_freezes_processor_and_creates_only_matching_attempt(
        CardProcessorKind firstProcessor,
        CardProcessorKind laterProcessor)
    {
        var cart = CreateReturnCart(4m);
        var linklyAttempts = new RecordingCardPaymentAttemptRepository();
        var squareAttempts = new RecordingSquarePaymentAttemptRepository();
        var linklyContext = new LinklyPaymentAttemptContextAccessor();
        var squareContext = new SquarePaymentAttemptContextAccessor();
        var settingsProvider = new TxnRefSequencedCardTerminalSettingsProvider(
            firstProcessor == CardProcessorKind.Linkly ? CreateLocalLinklySettings() : CreateSquareSettings(),
            laterProcessor == CardProcessorKind.Linkly ? CreateLocalLinklySettings() : CreateSquareSettings());
        var terminal = new SettingsBoundRecordingCardTerminalClient(linklyContext, squareContext);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: linklyAttempts,
            cardTerminalSettingsProvider: settingsProvider,
            squarePaymentAttemptRepository: squareAttempts,
            linklyPaymentAttemptContextAccessor: linklyContext,
            squarePaymentAttemptContextAccessor: squareContext);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var originalReference = firstProcessor == CardProcessorKind.Linkly
            ? "ANZ:PROCESSOR-SALE"
            : "SQ:processor-sale";

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: originalReference,
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded, result.StatusMessage);
        Assert.Equal(1, settingsProvider.GetSettingsCalls);
        Assert.Equal(1, terminal.BoundRefundCallCount);
        Assert.Equal(0, terminal.LegacyRefundCallCount);
        Assert.Equal(firstProcessor, terminal.LastSettings?.Processor);
        Assert.Equal(firstProcessor == CardProcessorKind.Linkly ? 1 : 0, linklyAttempts.Attempts.Count);
        Assert.Equal(firstProcessor == CardProcessorKind.Square ? 1 : 0, squareAttempts.Attempts.Count);
    }

    [Fact]
    public async Task Card_refund_persists_new_linkly_txn_ref_and_passes_it_as_idempotency_key()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingCardPaymentAttemptRepository();
        LocalCardPaymentAttempt? persistedAtSubmission = null;
        RecordingIdempotentCardRefundClient? terminal = null;
        terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(true, "ANZ:REFUND-1", "APPROVED", AuthorizedAmount: 4m),
            () => persistedAtSubmission = attempts.Attempts.SingleOrDefault());
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ANZ:SALE-1",
            cartSnapshot: cart.CreateSnapshot());

        Assert.NotNull(persistedAtSubmission);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, persistedAtSubmission.Status);
        AssertLocalTxnRef('R', persistedAtSubmission.TxnRef);
        Assert.Equal(
            LinklyLocalTxnRef.Create('R', persistedAtSubmission.AttemptGuid.ToString("D")),
            persistedAtSubmission.TxnRef);
        Assert.Equal(persistedAtSubmission.TxnRef, terminal.LastIdempotencyKey);
        var attempt = Assert.Single(attempts.Attempts);
        var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(
            attempt.OrderDraftJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(tender.Succeeded);
        Assert.Equal("Refund", attempt.OperationKind);
        Assert.Equal("R", attempt.TxnType);
        AssertLocalTxnRef('R', attempt.TxnRef);
        Assert.NotEqual("SALE-1", attempt.TxnRef);
        Assert.Equal(attempt.TxnRef, terminal.LastIdempotencyKey);
        Assert.Equal("ANZ:SALE-1", terminal.LastOriginalReference);
        Assert.Equal("ANZ:SALE-1", draft!.OriginalReference);
        Assert.Equal(draft.OrderGuid, attempt.OperationGuid);
    }

    [Fact]
    public async Task Local_ip_card_refund_reuses_persisted_txn_ref_after_restart()
    {
        var cart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var now = DateTimeOffset.UtcNow;
        var draft = new CardPaymentOrderDraft(
            Guid.NewGuid(),
            session,
            cart.CreateSnapshot(),
            [],
            -4m,
            4m,
            "R",
            "ANZ:SALE-RESTART",
            now);
        const string persistedTxnRef = "R0123456789ABCDE";
        var attempts = new RecordingCardPaymentAttemptRepository();
        await attempts.CreateAsync(new LocalCardPaymentAttempt(
            Guid.Parse("6ec9586c-6db7-4e9f-aaca-d312ab93c671"),
            null,
            persistedTxnRef,
            CardProcessorKind.Linkly.ToString(),
            CardTerminalEnvironment.Sandbox.ToString(),
            LinklyConnectionMode.LocalIp.ToString(),
            "R",
            4m,
            LocalCardPaymentAttemptStatus.Pending,
            JsonSerializer.Serialize(draft, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
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
            "Refund",
            draft.OrderGuid));
        LocalCardPaymentAttempt? persistedAtSubmission = null;
        RecordingIdempotentCardRefundClient? terminal = null;
        terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(true, "ANZ:REFUND-RESTART", "APPROVED", AuthorizedAmount: 4m),
            () => persistedAtSubmission = attempts.Attempts.SingleOrDefault());
        var restartedWorkflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()));

        var tender = await restartedWorkflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ANZ:SALE-RESTART",
            cartSnapshot: cart.CreateSnapshot());

        Assert.NotNull(persistedAtSubmission);
        Assert.Equal(persistedTxnRef, persistedAtSubmission.TxnRef);
        Assert.Equal(persistedAtSubmission.TxnRef, terminal.LastIdempotencyKey);
        Assert.True(tender.Succeeded);
        Assert.Single(attempts.Attempts);
        AssertLocalTxnRef('R', terminal.LastIdempotencyKey);
        Assert.Equal(persistedTxnRef, terminal.LastIdempotencyKey);
        Assert.Equal(1, terminal.IdempotentRefundCallCount);
    }

    [Theory]
    [InlineData(LinklyConnectionMode.LocalIp, LinklyConnectionMode.CloudBackendAsync, false)]
    [InlineData(LinklyConnectionMode.CloudBackendAsync, LinklyConnectionMode.LocalIp, false)]
    [InlineData(LinklyConnectionMode.LocalIp, LinklyConnectionMode.CloudBackendAsync, true)]
    [InlineData(LinklyConnectionMode.CloudBackendAsync, LinklyConnectionMode.LocalIp, true)]
    public async Task Open_refund_attempt_with_different_connection_mode_is_blocked_before_claim_and_terminal(
        LinklyConnectionMode currentMode,
        LinklyConnectionMode persistedMode,
        bool simulateCreateRace)
    {
        var cart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        const string originalReference = "ANZ:MODE-MISMATCH";
        var persistedTxnRef = persistedMode == LinklyConnectionMode.LocalIp
            ? "R0123456789ABCDE"
            : Guid.NewGuid().ToString("N");
        var winner = CreateOpenLinklyRefundAttempt(
            session,
            cart.CreateSnapshot(),
            persistedMode,
            "R",
            persistedTxnRef,
            originalReference);
        var attempts = new RaceWinningCardPaymentAttemptRepository(winner, exposeWinnerDuringLookup: !simulateCreateRace);
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(true, "ANZ:SHOULD-NOT-REFUND", "APPROVED", AuthorizedAmount: 4m));
        var settings = (currentMode == LinklyConnectionMode.LocalIp
            ? CreateLocalLinklySettings()
            : CreateBackendLinklySettings()) with
        {
            LinklyConnectionMode = currentMode
        };
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: originalReference,
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.Equal("payment.card.resultUnknown", result.StatusKey);
        Assert.Equal(winner.AttemptGuid, result.RecoveryAttemptKey?.AttemptGuid);
        Assert.Equal(0, terminal.IdempotentRefundCallCount);
        Assert.Equal(0, attempts.RefundSubmissionClaimCalls);
        Assert.Equal(simulateCreateRace ? 1 : 0, attempts.CreateOrGetOpenRefundCalls);
        Assert.Equal(0, attempts.CreateAsyncCalls);
        Assert.Single(attempts.PersistedAttempts);
        Assert.Equal(winner, attempts.PersistedAttempt);
    }

    [Theory]
    [InlineData("P", "R0123456789ABCDE", false)]
    [InlineData("R", "R0123456789ABCDEF", false)]
    [InlineData("R", "R0123\u001f", true)]
    [InlineData("R", "RCAFÉ", true)]
    [InlineData("R", "ANZ:", false)]
    [InlineData("R", "ANZ:   ", true)]
    public async Task Local_ip_open_refund_attempt_with_invalid_type_or_reference_is_blocked_before_claim_and_terminal(
        string txnType,
        string txnRef,
        bool simulateCreateRace)
    {
        var cart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        const string originalReference = "ANZ:INVALID-LOCAL-REF";
        var winner = CreateOpenLinklyRefundAttempt(
            session,
            cart.CreateSnapshot(),
            LinklyConnectionMode.LocalIp,
            txnType,
            txnRef,
            originalReference);
        var attempts = new RaceWinningCardPaymentAttemptRepository(winner, exposeWinnerDuringLookup: !simulateCreateRace);
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(true, "ANZ:SHOULD-NOT-REFUND", "APPROVED", AuthorizedAmount: 4m));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: originalReference,
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.Equal("payment.card.resultUnknown", result.StatusKey);
        Assert.Equal(winner.AttemptGuid, result.RecoveryAttemptKey?.AttemptGuid);
        Assert.Equal(0, terminal.IdempotentRefundCallCount);
        Assert.Equal(0, attempts.RefundSubmissionClaimCalls);
        Assert.Equal(simulateCreateRace ? 1 : 0, attempts.CreateOrGetOpenRefundCalls);
        Assert.Single(attempts.PersistedAttempts);
        Assert.Equal(winner, attempts.PersistedAttempt);
    }

    [Theory]
    [InlineData("R")]
    [InlineData("R-OLD#1")]
    [InlineData("R0123456789ABCDE")]
    public async Task Local_ip_open_refund_accepts_printable_historical_persisted_reference(string persistedTxnRef)
    {
        var cart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        const string originalReference = "ANZ:SHORT-LOCAL-REF";
        var winner = CreateOpenLinklyRefundAttempt(
            session,
            cart.CreateSnapshot(),
            LinklyConnectionMode.LocalIp,
            "R",
            persistedTxnRef,
            originalReference);
        var attempts = new RaceWinningCardPaymentAttemptRepository(winner, exposeWinnerDuringLookup: true);
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(true, "ANZ:SHORT-REFUND", "APPROVED", AuthorizedAmount: 4m));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()));

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: originalReference,
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded, result.StatusMessage);
        Assert.Equal(1, terminal.IdempotentRefundCallCount);
        Assert.Equal(persistedTxnRef, terminal.LastIdempotencyKey);
        Assert.Equal(persistedTxnRef, attempts.PersistedAttempt.TxnRef);
    }

    [Fact]
    public async Task Cloud_ordinary_refund_persists_and_submits_legacy_32_hex_txn_ref()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingCardPaymentAttemptRepository();
        LocalCardPaymentAttempt? persistedAtSubmission = null;
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(true, "ANZ:CLOUD-REFUND", "APPROVED", AuthorizedAmount: 4m),
            () => persistedAtSubmission = attempts.Attempts.SingleOrDefault());
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ANZ:CLOUD-SALE",
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded, result.StatusMessage);
        Assert.NotNull(persistedAtSubmission);
        Assert.Equal(LinklyConnectionMode.CloudBackendAsync.ToString(), persistedAtSubmission.ConnectionMode);
        Assert.Equal("R", persistedAtSubmission.TxnType);
        Assert.Matches("^[0-9a-f]{32}$", persistedAtSubmission.TxnRef!);
        Assert.Equal(persistedAtSubmission.TxnRef, terminal.LastIdempotencyKey);
        Assert.DoesNotMatch("^[PR][0-9ABCDEFGHJKMNPQRSTVWXYZ]{15}$", terminal.LastIdempotencyKey!);
    }

    [Fact]
    public async Task Card_sale_caller_cancellation_before_terminal_submission_is_not_recoverable()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-NOT-SUBMITTED", "Not Submitted Tea", "930NOTSUBMITTED", 10m));
        var attempts = new RecordingCardPaymentAttemptRepository();
        using var cancellation = new CancellationTokenSource();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: new NotSubmittedCancellingCardTerminalClient(cancellation),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor());
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: 10m,
            currentTenders: [],
            amountText: "10",
            cancellationToken: cancellation.Token,
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.Equal("payment.status.cardCancelled", result.StatusKey);
        Assert.False(result.CardResult?.RequiresRecovery);
        Assert.Null(result.RecoveryAttemptKey);
        Assert.Null(result.RecoveryOrderGuid);
        Assert.Equal(LocalCardPaymentAttemptStatus.Cancelled, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Card_refund_caller_cancellation_after_claim_but_before_terminal_submission_is_not_recoverable()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingCardPaymentAttemptRepository();
        using var cancellation = new CancellationTokenSource();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: new NotSubmittedCancellingCardTerminalClient(cancellation),
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()),
            linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor());
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ANZ:SALE-NOT-SUBMITTED",
            cancellationToken: cancellation.Token,
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.Equal("payment.status.cardCancelled", result.StatusKey);
        Assert.False(result.CardResult?.RequiresRecovery);
        Assert.Null(result.RecoveryAttemptKey);
        Assert.Null(result.RecoveryOrderGuid);
        Assert.Equal(LocalCardPaymentAttemptStatus.Cancelled, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Card_refund_claim_persistence_failure_does_not_expose_handoff_identity()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingCardPaymentAttemptRepository
        {
            MarkRecoveringException = new InvalidOperationException("refund claim persistence failed")
        };
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(true, "ANZ:REFUND-CLAIM", "APPROVED", AuthorizedAmount: 4m));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ANZ:SALE-CLAIM",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.Equal("payment.card.resultUnknown", result.StatusKey);
        Assert.Null(result.RecoveryAttemptKey);
        Assert.Null(result.RecoveryOrderGuid);
        Assert.Equal(0, terminal.IdempotentRefundCallCount);
    }

    [Fact]
    public async Task Square_refund_reuses_pending_attempt_idempotency_key_after_restart()
    {
        var cart = CreateReturnCart(4m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var now = DateTimeOffset.UtcNow;
        var draft = new CardPaymentOrderDraft(
            Guid.NewGuid(),
            session,
            cart.CreateSnapshot(),
            [],
            -4m,
            4m,
            "R",
            "SQ:payment-1",
            now);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        await attempts.CreateAsync(new LocalSquarePaymentAttempt(
            Guid.NewGuid(),
            null,
            "persisted-square-refund-key",
            "DEV-1",
            "LOC-1",
            "Production",
            4m,
            400,
            "AUD",
            LocalSquarePaymentAttemptStatus.Pending,
            null,
            null,
            JsonSerializer.Serialize(draft, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            null,
            null,
            null,
            null,
            now,
            now,
            null,
            null,
            null,
            "Refund",
            draft.OrderGuid));
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(true, "SQRF:refund-1", "APPROVED", AuthorizedAmount: 4m));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);

        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-1",
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(tender.Succeeded);
        Assert.Single(attempts.Attempts);
        Assert.Equal("persisted-square-refund-key", terminal.LastIdempotencyKey);
        Assert.Equal(1, terminal.IdempotentRefundCallCount);
    }

    [Fact]
    public async Task Square_refund_result_unknown_blocks_restart_without_second_terminal_call()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(
                false,
                null,
                "Square refund result is unknown.",
                AuthorizedAmount: 4m,
                ResultUnknown: true));
        var settings = new StaticCardTerminalSettingsProvider(CreateSquareSettings());
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var firstWorkflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: settings,
            squarePaymentAttemptRepository: attempts);

        var first = await firstWorkflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-1",
            cartSnapshot: cart.CreateSnapshot());
        var persistedAttempt = Assert.Single(attempts.Attempts);
        var restartedWorkflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: settings,
            squarePaymentAttemptRepository: attempts);

        var afterRestart = await restartedWorkflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-1",
            cartSnapshot: cart.CreateSnapshot());

        Assert.False(first.Succeeded);
        // 没有 Square 退款处理中证据的未知结果仍显示通用“结果无法确认”提示。
        Assert.Equal("Card terminal result could not be confirmed. Recovery is required.", first.StatusMessage);
        Assert.False(afterRestart.Succeeded);
        Assert.Equal("payment.card.resultUnknown", afterRestart.StatusKey);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, persistedAttempt.Status);
        Assert.Equal(persistedAttempt.IdempotencyKey, terminal.LastIdempotencyKey);
        Assert.Equal(1, terminal.IdempotentRefundCallCount);
        Assert.Single(attempts.Attempts);
    }

    [Fact]
    public async Task Square_refund_accepted_while_pending_completes_the_tender_with_the_accepted_message()
    {
        const string acceptedMessage =
            "Square accepted the refund and is returning it to the original card. Most refunds complete within a few hours (up to 14 days). No card tap is needed.";
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(
                true,
                "SQRF:refund-still-pending",
                acceptedMessage,
                AuthorizedAmount: 4m,
                CardTransactions:
                [
                    new CardTransactionDto(
                        "Square", "refund-still-pending", null, null, null, null, null, null,
                        "PENDING", null, DateTimeOffset.UtcNow, 4m, null)
                ],
                StatusKey: "payment.card.squareRefundAccepted"));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-1",
            cartSnapshot: cart.CreateSnapshot());

        var persistedAttempt = Assert.Single(attempts.Attempts);
        // 已受理的 PENDING 退款与 COMPLETED 一样生成负数卡 tender，不再锁进异常中心；提示说明已受理、无需刷卡。
        Assert.True(result.Succeeded);
        Assert.Equal("payment.card.squareRefundAccepted", result.StatusKey);
        Assert.Equal(acceptedMessage, result.StatusMessage);
        Assert.Equal(-4m, result.Tender?.Amount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.PaymentVerified, persistedAttempt.Status);
        Assert.Equal("PENDING", persistedAttempt.PaymentStatus);
        Assert.Equal(1, terminal.IdempotentRefundCallCount);
    }

    [Theory]
    [InlineData("FAILED")]
    [InlineData("REJECTED")]
    public async Task Square_refund_terminal_failure_returns_durable_recovery_identity(
        string paymentStatus)
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(
                false,
                null,
                $"Square refund ended as {paymentStatus}.",
                AuthorizedAmount: 4m,
                ResponseCode: "SQUARE_REFUND_FAILURE",
                ResponseText: paymentStatus),
            beforeResult: () => attempts.SimulateRefundResponse(paymentStatus));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-terminal-failure",
            cartSnapshot: cart.CreateSnapshot());

        var persisted = Assert.Single(attempts.Attempts);
        Assert.False(result.Succeeded);
        Assert.Equal("payment.card.resultUnknown", result.StatusKey);
        Assert.True(result.CardResult?.RequiresRecovery);
        Assert.Equal(
            new CardRecoveryAttemptKey(CardProcessorKind.Square, persisted.AttemptGuid),
            result.RecoveryAttemptKey);
        Assert.Equal(persisted.OperationGuid, result.RecoveryOrderGuid);
        Assert.Equal(1, attempts.RefundFailureFinalizationCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, persisted.Status);
        Assert.Equal(paymentStatus, persisted.PaymentStatus);
        Assert.Equal("square-refund-terminal", persisted.PaymentId);
        Assert.Equal(CardRecoveryPhases.FinalizePending, persisted.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, persisted.RecoveryTargetStatus);
        Assert.Contains(
            persisted,
            await attempts.GetOpenRefundAttemptsAsync(
                session.StoreCode,
                session.DeviceCode,
                CardTerminalEnvironment.Production.ToString()));
    }

    [Fact]
    public async Task Square_refund_failure_handoff_cas_loser_preserves_completed_winner()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository
        {
            CompleteRefundDuringFailureHandoff = true
        };
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(
                false,
                null,
                "Square returned a stale failure after completion.",
                AuthorizedAmount: 4m,
                ResponseCode: "STALE_FAILURE"),
            beforeResult: () => attempts.SimulateRefundResponse("FAILED"));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-completed-winner",
            cartSnapshot: cart.CreateSnapshot());

        var winner = Assert.Single(attempts.Attempts);
        Assert.False(result.Succeeded);
        Assert.Equal("payment.card.resultUnknown", result.StatusKey);
        Assert.Equal(winner.AttemptGuid, result.RecoveryAttemptKey?.AttemptGuid);
        Assert.Equal(1, attempts.RefundFailureFinalizationCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.PaymentVerified, winner.Status);
        Assert.Equal("COMPLETED", winner.PaymentStatus);
        Assert.Equal(CardRecoveryPhases.FinalizePending, winner.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, winner.RecoveryTargetStatus);
    }

    [Fact]
    public async Task Square_refund_failure_handoff_incompatible_cas_winner_does_not_return_unverified_attempt_identity()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository
        {
            ReplaceRefundDuringFailureHandoff = true
        };
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(
                false,
                null,
                "Square refund failure raced with a replacement recovery worker.",
                AuthorizedAmount: 4m,
                ResponseCode: "STALE_FAILURE"),
            beforeResult: () => attempts.SimulateRefundResponse("FAILED"));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-incompatible-winner",
            cartSnapshot: cart.CreateSnapshot());

        var winner = Assert.Single(attempts.Attempts);
        Assert.False(result.Succeeded);
        Assert.True(result.CardResult?.RequiresRecovery);
        Assert.Null(result.RecoveryAttemptKey);
        Assert.Null(result.RecoveryOrderGuid);
        Assert.Equal("replacement-worker-token", winner.SubmissionToken);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Recovering, winner.Status);
        Assert.Equal(CardRecoveryPhases.None, winner.RecoveryPhase);
        Assert.Contains(
            winner,
            await attempts.GetOpenRefundAttemptsAsync(
                session.StoreCode,
                session.DeviceCode,
                CardTerminalEnvironment.Production.ToString()));
    }

    [Theory]
    [InlineData(LocalSquarePaymentAttemptStatus.Canceled)]
    [InlineData(LocalSquarePaymentAttemptStatus.TimedOut)]
    [InlineData(LocalSquarePaymentAttemptStatus.Abandoned)]
    [InlineData(LocalSquarePaymentAttemptStatus.OrderCompleted)]
    public async Task Square_refund_failure_handoff_terminal_cas_winner_never_returns_unreachable_identity(
        LocalSquarePaymentAttemptStatus terminalStatus)
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository
        {
            RefundFailureHandoffCasWinnerStatus = terminalStatus
        };
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(
                false,
                null,
                "Square returned a stale failure after a terminal winner.",
                AuthorizedAmount: 4m,
                ResponseCode: "STALE_FAILURE"),
            beforeResult: () => attempts.SimulateRefundResponse("FAILED"));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-terminal-winner",
            cartSnapshot: cart.CreateSnapshot());

        var winner = Assert.Single(attempts.Attempts);
        Assert.False(result.Succeeded);
        Assert.True(result.CardResult?.RequiresRecovery);
        Assert.Null(result.RecoveryAttemptKey);
        Assert.Null(result.RecoveryOrderGuid);
        Assert.Equal(terminalStatus, winner.Status);
        Assert.DoesNotContain(
            winner,
            await attempts.GetOpenRefundAttemptsAsync(
                session.StoreCode,
                session.DeviceCode,
                CardTerminalEnvironment.Production.ToString()));
    }

    [Fact]
    public async Task Square_refund_failure_handoff_repairs_exact_failed_cas_winner_into_open_queue()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository
        {
            RefundFailureHandoffCasWinnerStatus = LocalSquarePaymentAttemptStatus.Failed
        };
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(
                false,
                null,
                "Square refund failed while another local writer terminalized the attempt.",
                AuthorizedAmount: 4m,
                ResponseCode: "SQUARE_REFUND_FAILURE"),
            beforeResult: () => attempts.SimulateRefundResponse("FAILED"));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-failed-winner",
            cartSnapshot: cart.CreateSnapshot());

        var winner = Assert.Single(attempts.Attempts);
        Assert.False(result.Succeeded);
        Assert.True(result.CardResult?.RequiresRecovery);
        Assert.Equal(
            new CardRecoveryAttemptKey(CardProcessorKind.Square, winner.AttemptGuid),
            result.RecoveryAttemptKey);
        Assert.Equal(winner.OperationGuid, result.RecoveryOrderGuid);
        Assert.Equal(2, attempts.RefundFailureFinalizationCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, winner.Status);
        Assert.Equal("FAILED", winner.PaymentStatus);
        Assert.Equal(CardRecoveryPhases.FinalizePending, winner.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, winner.RecoveryTargetStatus);
        Assert.Contains(
            winner,
            await attempts.GetOpenRefundAttemptsAsync(
                session.StoreCode,
                session.DeviceCode,
                CardTerminalEnvironment.Production.ToString()));
    }

    [Fact]
    public async Task Square_refund_failure_handoff_persistent_repository_failure_does_not_return_unverified_identity()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository
        {
            RefundFailureFinalizationException = new IOException("simulated refund handoff persistence failure")
        };
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(
                false,
                null,
                "Square refund reached a terminal failure.",
                AuthorizedAmount: 4m,
                ResponseCode: "SQUARE_REFUND_FAILURE"),
            beforeResult: () => attempts.SimulateRefundResponse("FAILED"));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-handoff-persistence-failure",
            cartSnapshot: cart.CreateSnapshot());

        var persisted = Assert.Single(attempts.Attempts);
        Assert.False(result.Succeeded);
        Assert.True(result.CardResult?.RequiresRecovery);
        Assert.Null(result.RecoveryAttemptKey);
        Assert.Null(result.RecoveryOrderGuid);
        Assert.Equal(2, attempts.RefundFailureFinalizationCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Recovering, persisted.Status);
        Assert.Equal("FAILED", persisted.PaymentStatus);
        Assert.Equal(CardRecoveryPhases.None, persisted.RecoveryPhase);
    }

    [Fact]
    public async Task Square_refund_failure_handoff_transient_write_failure_repairs_exact_recovering_row()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository
        {
            RefundFailureFinalizationException = new IOException("simulated transient refund handoff failure"),
            RefundFailureFinalizationExceptionsRemaining = 1
        };
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(
                false,
                null,
                "Square refund reached a terminal failure.",
                AuthorizedAmount: 4m,
                ResponseCode: "SQUARE_REFUND_FAILURE"),
            beforeResult: () => attempts.SimulateRefundResponse("FAILED"));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-transient-recovering-handoff",
            cartSnapshot: cart.CreateSnapshot());

        var persisted = Assert.Single(attempts.Attempts);
        Assert.False(result.Succeeded);
        Assert.True(result.CardResult?.RequiresRecovery);
        Assert.Equal(
            new CardRecoveryAttemptKey(CardProcessorKind.Square, persisted.AttemptGuid),
            result.RecoveryAttemptKey);
        Assert.Equal(persisted.OperationGuid, result.RecoveryOrderGuid);
        Assert.Equal(2, attempts.RefundFailureFinalizationCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, persisted.Status);
        Assert.Equal("FAILED", persisted.PaymentStatus);
        Assert.Equal(CardRecoveryPhases.FinalizePending, persisted.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, persisted.RecoveryTargetStatus);
    }

    [Fact]
    public async Task Square_refund_failure_handoff_transient_write_failure_repairs_exact_failed_row()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository
        {
            RefundFailureFinalizationException = new IOException("simulated transient refund handoff failure"),
            RefundFailureFinalizationExceptionsRemaining = 1
        };
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(
                false,
                null,
                "Square refund reached a terminal failure.",
                AuthorizedAmount: 4m,
                ResponseCode: "SQUARE_REFUND_FAILURE"),
            beforeResult: () => attempts.SimulateRefundResponse(
                "FAILED",
                LocalSquarePaymentAttemptStatus.Failed));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-transient-handoff-failure",
            cartSnapshot: cart.CreateSnapshot());

        var persisted = Assert.Single(attempts.Attempts);
        Assert.False(result.Succeeded);
        Assert.True(result.CardResult?.RequiresRecovery);
        Assert.Equal(
            new CardRecoveryAttemptKey(CardProcessorKind.Square, persisted.AttemptGuid),
            result.RecoveryAttemptKey);
        Assert.Equal(persisted.OperationGuid, result.RecoveryOrderGuid);
        Assert.Equal(2, attempts.RefundFailureFinalizationCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, persisted.Status);
        Assert.Equal("FAILED", persisted.PaymentStatus);
        Assert.Equal(CardRecoveryPhases.FinalizePending, persisted.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, persisted.RecoveryTargetStatus);
        Assert.Contains(
            persisted,
            await attempts.GetOpenRefundAttemptsAsync(
                session.StoreCode,
                session.DeviceCode,
                CardTerminalEnvironment.Production.ToString()));
    }

    [Fact]
    public async Task Square_refund_late_approved_worker_cannot_return_tender_after_new_claim()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(
                true,
                "SQRF:late-refund",
                "COMPLETED",
                AuthorizedAmount: 4m,
                CardTransactions:
                [
                    new CardTransactionDto(
                        "Square",
                        "late-refund",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        "COMPLETED",
                        null,
                        DateTimeOffset.UtcNow,
                        4m,
                        null)
                ]),
            beforeResult: () => attempts.SimulateSupervisorRetryAndNewClaim("new-worker-token"));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-1",
            cartSnapshot: cart.CreateSnapshot());

        var saved = Assert.Single(attempts.Attempts);
        Assert.False(result.Succeeded);
        Assert.Null(result.Tender);
        Assert.Equal("payment.card.resultUnknown", result.StatusKey);
        Assert.Equal("new-worker-token", saved.SubmissionToken);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Recovering, saved.Status);
        Assert.Null(saved.PaymentId);
    }

    [Fact]
    public async Task Square_refund_late_approved_worker_cannot_overwrite_finalize_pending_winner_with_same_token()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingSquarePaymentAttemptRepository();
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(
                true,
                "SQRF:late-finalize-pending-refund",
                "COMPLETED",
                AuthorizedAmount: 4m,
                CardTransactions:
                [
                    new CardTransactionDto(
                        "Square",
                        "late-finalize-pending-refund",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        "COMPLETED",
                        null,
                        DateTimeOffset.UtcNow,
                        4m,
                        null)
                ]),
            beforeResult: attempts.SimulateRefundFailureFinalizationWinner);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: attempts);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-1",
            cartSnapshot: cart.CreateSnapshot());

        var winner = Assert.Single(attempts.Attempts);
        Assert.False(result.Succeeded);
        Assert.Null(result.Tender);
        Assert.Equal("payment.card.resultUnknown", result.StatusKey);
        Assert.Equal(1, attempts.RefundPaymentVerifiedCasCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, winner.Status);
        Assert.Equal("FAILED", winner.PaymentStatus);
        Assert.Equal("SQUARE_FAILURE", winner.ResponseCode);
        Assert.Equal(CardRecoveryPhases.FinalizePending, winner.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, winner.RecoveryTargetStatus);
    }

    [Fact]
    public async Task Approved_card_refund_order_and_attempt_completion_ignore_cancelled_ui_token()
    {
        var cart = CreateReturnCart(4m);
        var attempts = new RecordingCardPaymentAttemptRepository { RejectCancelledTokens = true };
        var orders = new RecordingOrderRepository { RejectCancelledTokens = true };
        using var cancellation = new CancellationTokenSource();
        var terminal = new RecordingIdempotentCardRefundClient(
            new PaymentAuthorizationResult(true, "ANZ:REFUND-CANCEL", "APPROVED", AuthorizedAmount: 4m),
            cancellation.Cancel);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateLocalLinklySettings()));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -4m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ANZ:SALE-CANCEL",
            cancellationToken: cancellation.Token,
            cartSnapshot: cart.CreateSnapshot());
        var completion = await workflow.CompletePaymentAsync(
            cart,
            session,
            [tender.Tender!],
            cashTenderedAmount: 0m,
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(tender.Succeeded);
        Assert.Single(orders.SavedOrders);
        Assert.Equal(completion.Order.OrderGuid, orders.SavedOrders[0].OrderGuid);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, Assert.Single(attempts.Attempts).Status);
    }

    [Fact]
    public async Task Payment_workflow_rejects_card_refund_without_original_reference()
    {
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-REFUND"));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -10m,
            currentTenders: [],
            amountText: "4");

        Assert.False(tender.Succeeded);
        Assert.Equal("payment.status.cardDeclined", tender.StatusKey);
    }

    [Fact]
    public async Task Payment_workflow_adds_negative_voucher_tender_for_refund()
    {
        var vouchers = new ApprovedVoucherTenderClient("VOUCHER_REFUND:RF123");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: vouchers);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Voucher,
            session,
            actualAmount: -10m,
            currentTenders: [],
            amountText: "6");

        Assert.True(tender.Succeeded);
        Assert.NotNull(tender.Tender);
        Assert.Equal(-6m, tender.Tender.Amount);
        Assert.Equal("VOUCHER_REFUND_PENDING", tender.Tender.Reference);
        Assert.Equal(0, vouchers.IssueRefundCallCount);
    }

    [Fact]
    public void Payment_workflow_calculates_refund_remaining_and_change_without_over_refunding()
    {
        var workflow = CreateWorkflow();

        var remainingAfterCash = workflow.CalculateRemainingAmount(
            -7.82m,
            [new PaymentTender(PaymentMethodKind.Cash, -7.80m)]);
        var remainingAfterCard = workflow.CalculateRemainingAmount(
            -10m,
            [new PaymentTender(PaymentMethodKind.Card, -4m, "SQ:payment-1")]);
        var change = workflow.CalculateChange(
            [new PaymentTender(PaymentMethodKind.Cash, -7.80m)],
            -7.82m);

        Assert.Equal(0m, remainingAfterCash);
        Assert.Equal(-6m, remainingAfterCard);
        Assert.Equal(0m, change);
    }

    [Fact]
    public async Task Payment_workflow_completes_refund_order_with_negative_payments()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-RET",
            null,
            "Returned Tea",
            "930500",
            "ITEM-RET",
            null,
            1m,
            7.82m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-500",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.CompletePaymentAsync(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Cash, -7.80m)],
            cashTenderedAmount: -7.80m);

        var payment = Assert.Single(result.Order.Payments);
        Assert.Equal(-7.80m, payment.Amount);
        Assert.Equal(-7.80m, result.TenderedAmount);
        Assert.Equal(0m, result.ChangeAmount);
    }

    [Fact]
    public async Task Payment_workflow_issues_refund_voucher_after_order_guid_exists()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-VR",
            null,
            "Voucher Refund Tea",
            "930501",
            "ITEM-VR",
            null,
            1m,
            6m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VOUCHER-1",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var orders = new RecordingOrderRepository();
        var vouchers = new ApprovedVoucherTenderClient("VOUCHER_REFUND:RF123");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: vouchers);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.CompletePaymentAsync(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Voucher, -6m, "VOUCHER_REFUND_PENDING")],
            cashTenderedAmount: 0m);

        var saved = Assert.Single(orders.SavedOrders);
        var payment = Assert.Single(saved.Payments);
        Assert.Equal(result.Order.OrderGuid.ToString("D"), vouchers.LastOrderReference);
        Assert.Equal(saved.OrderGuid.ToString("D"), vouchers.LastOrderReference);
        Assert.False(string.IsNullOrWhiteSpace(vouchers.LastIdempotencyKey));
        Assert.Equal(-6m, payment.Amount);
        Assert.Equal("VOUCHER_REFUND:RF123", payment.Reference);
        Assert.Equal(1, vouchers.IssueRefundCallCount);
    }

    [Fact]
    public async Task Payment_workflow_does_not_issue_refund_voucher_before_local_save_succeeds()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-VR-RETRY",
            null,
            "Voucher Refund Retry",
            "930503",
            "ITEM-VR-RETRY",
            null,
            1m,
            6m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VOUCHER-RETRY",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var orders = new FailingOnceOrderRepository();
        var vouchers = new ApprovedVoucherTenderClient("VOUCHER_REFUND:RF123");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: vouchers);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = (await workflow.AddTenderAsync(
            PaymentMethodKind.Voucher,
            session,
            actualAmount: -6m,
            currentTenders: [],
            amountText: "6")).Tender!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.CompletePaymentAsync(
            cart,
            session,
            [tender],
            cashTenderedAmount: 0m));
        Assert.Equal(0, vouchers.IssueRefundCallCount);

        await workflow.CompletePaymentAsync(
            cart,
            session,
            [tender],
            cashTenderedAmount: 0m);

        Assert.False(string.IsNullOrWhiteSpace(vouchers.LastIdempotencyKey));
        Assert.Equal(tender.IdempotencyKey, vouchers.LastIdempotencyKey);
        Assert.Equal(1, vouchers.IssueRefundCallCount);
        Assert.Single(orders.SavedOrders);
    }

    [Fact]
    public async Task Payment_workflow_persists_pending_voucher_refund_order_when_issue_fails()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-VR-FAIL",
            null,
            "Voucher Refund Fail",
            "930502",
            "ITEM-VR-FAIL",
            null,
            1m,
            6m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VOUCHER-FAIL",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var orders = new RecordingOrderRepository();
        var vouchers = new ApprovedVoucherTenderClient("VOUCHER_REFUND:RF123", approveRefund: false);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: vouchers);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        await Assert.ThrowsAsync<PaymentUploadFailedException>(() => workflow.CompletePaymentAsync(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Voucher, -6m, "VOUCHER_REFUND_PENDING")],
            cashTenderedAmount: 0m));

        var saved = Assert.Single(orders.SavedOrders);
        var payment = Assert.Single(saved.Payments);
        Assert.Equal("VOUCHER_REFUND_PENDING", payment.Reference);
        Assert.False(string.IsNullOrWhiteSpace(payment.IdempotencyKey));
        Assert.Equal(1, vouchers.IssueRefundCallCount);
    }

    [Fact]
    public async Task Payment_workflow_retry_reuses_pending_refund_voucher_idempotency_key_and_updates_local_reference()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-VR-RECOVER",
            null,
            "Voucher Refund Recover",
            "930504",
            "ITEM-VR-RECOVER",
            null,
            1m,
            6m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VOUCHER-RECOVER",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var orders = new RecordingOrderRepository();
        var vouchers = new RetriableVoucherTenderClient("VOUCHER_REFUND:RF123");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: vouchers);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var failed = await Assert.ThrowsAsync<PaymentUploadFailedException>(() => workflow.CompletePaymentAsync(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Voucher, -6m, "VOUCHER_REFUND_PENDING")],
            cashTenderedAmount: 0m));
        vouchers.FailIssueRefund = false;

        var savedBeforeRetry = Assert.Single(orders.SavedOrders);
        var pendingPayment = Assert.Single(savedBeforeRetry.Payments);
        var result = await workflow.RetryVoucherUploadAsync(
            savedBeforeRetry.OrderGuid,
            cart,
            session,
            tenderedAmount: 0m,
            changeAmount: 0m);

        Assert.Contains("issue failed", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, vouchers.IssueRefundCallCount);
        Assert.Equal(2, vouchers.IssueRefundIdempotencyKeys.Count);
        Assert.Equal(vouchers.IssueRefundIdempotencyKeys[0], vouchers.IssueRefundIdempotencyKeys[1]);
        Assert.Equal(pendingPayment.IdempotencyKey, vouchers.IssueRefundIdempotencyKeys[0]);
        Assert.Equal("VOUCHER_REFUND:RF123", Assert.Single(result.Order.Payments).Reference);
        Assert.Equal("VOUCHER_REFUND:RF123", Assert.Single(Assert.Single(orders.SavedOrders).Payments).Reference);
        Assert.Empty(cart.Lines);
    }

    private static ICashPaymentWorkflowService CreateWorkflow()
    {
        return new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1));
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

    private static PosCartService CreateReturnCart(decimal amount)
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND",
            null,
            "Returned Item",
            "930599",
            "ITEM-REFUND",
            null,
            1m,
            amount,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-REFUND",
            Guid.NewGuid(),
            Guid.NewGuid()));
        return cart;
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

    private static CardTerminalSettings CreateLocalLinklySettings()
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
            LinklyConnectionMode.LocalIp);
    }

    private static CardTerminalSettings CreateCloudDirectLinklySettings()
    {
        return CreateLocalLinklySettings() with
        {
            LinklyConnectionMode = LinklyConnectionMode.CloudDirectSync
        };
    }

    private static CardTerminalSettings CreateSquareSettings()
    {
        return new CardTerminalSettings(
            CardProcessorKind.Square,
            CardTerminalEnvironment.Production,
            "127.0.0.1",
            2011,
            "square-token",
            "LOC-1",
            "DEV-1",
            CardTerminalSettings.GetSquareApiBaseUrl(CardTerminalEnvironment.Production),
            TimeSpan.FromSeconds(90));
    }

    private static LocalSquarePaymentAttempt CreateAlternativeSquareRefundAttempt(
        PosCartService sourceCart,
        PosSessionState session,
        IReadOnlyList<PaymentTender>? currentTenders = null,
        string paymentStatus = "FAILED")
    {
        var now = DateTimeOffset.UtcNow;
        var orderGuid = Guid.NewGuid();
        var draft = new CardPaymentOrderDraft(
            orderGuid,
            session,
            sourceCart.CreateSnapshot(),
            currentTenders ?? [],
            sourceCart.ActualAmount,
            Math.Abs(sourceCart.ActualAmount),
            "R",
            "SQ:ORIGINAL-ALTERNATIVE",
            now);
        return new LocalSquarePaymentAttempt(
            Guid.NewGuid(),
            null,
            $"square-refund-{Guid.NewGuid():N}",
            "DEV-1",
            "LOC-1",
            CardTerminalEnvironment.Production.ToString(),
            Math.Abs(sourceCart.ActualAmount),
            decimal.ToInt64(Math.Abs(sourceCart.ActualAmount) * 100m),
            "AUD",
            LocalSquarePaymentAttemptStatus.Unknown,
            null,
            null,
            JsonSerializer.Serialize(draft, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            "square-refund-terminal",
            paymentStatus,
            "SQUARE_REFUND_FAILURE",
            "Square refund failed and requires an alternative method.",
            now,
            now,
            null,
            null,
            null,
            "Refund",
            orderGuid,
            "failure-worker-token",
            RecoveryPhase: CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus: LocalSquarePaymentAttemptStatus.Abandoned);
    }

    private sealed class RecordingOrderRepository : ILocalOrderRepository
    {
        public List<LocalOrder> SavedOrders { get; } = [];

        public List<(LocalOrder Order, LocalHeldOrderCompletionContext Context)> HeldSources { get; } = [];

        public bool RejectCancelledTokens { get; init; }

        public TaskCompletionSource? SaveStarted { get; init; }

        public TaskCompletionSource? ContinueSave { get; init; }

        public int SaveThreadId { get; private set; }

        public Exception? SaveException { get; set; }

        public async Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            if (RejectCancelledTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            SaveThreadId = Environment.CurrentManagedThreadId;
            SaveStarted?.TrySetResult();
            if (ContinueSave is not null)
            {
                await ContinueSave.Task.WaitAsync(cancellationToken);
            }

            if (SaveException is not null)
            {
                throw SaveException;
            }

            SavedOrders.Add(order);
        }

        public async Task SavePendingOrderWithHeldSourceAsync(
            LocalOrder order,
            LocalHeldOrderCompletionContext heldOrder,
            CancellationToken cancellationToken = default)
        {
            await SavePendingOrderAsync(order, cancellationToken);
            HeldSources.Add((order, heldOrder));
        }

        public Task UpdatePaymentReferenceAsync(
            Guid paymentGuid,
            string? reference,
            CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < SavedOrders.Count; index++)
            {
                var order = SavedOrders[index];
                var paymentIndex = order.Payments
                    .ToList()
                    .FindIndex(payment => payment.PaymentGuid == paymentGuid);
                if (paymentIndex < 0 || paymentIndex >= order.Payments.Count)
                {
                    continue;
                }

                var updatedPayments = order.Payments.ToList();
                updatedPayments[paymentIndex] = updatedPayments[paymentIndex] with { Reference = reference };
                SavedOrders[index] = order with { Payments = updatedPayments };
                break;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return GetRecentOrdersAsync(take, cancellationToken);
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalOrder?>(SavedOrders.LastOrDefault(order => order.OrderGuid == orderGuid));
        }
    }

    private sealed class FailingOnceOrderRepository : ILocalOrderRepository
    {
        private bool _hasFailed;

        public List<LocalOrder> SavedOrders { get; } = [];

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            if (!_hasFailed)
            {
                _hasFailed = true;
                throw new InvalidOperationException("local save failed");
            }

            SavedOrders.Add(order);
            return Task.CompletedTask;
        }

        public Task UpdatePaymentReferenceAsync(
            Guid paymentGuid,
            string? reference,
            CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < SavedOrders.Count; index++)
            {
                var order = SavedOrders[index];
                var paymentIndex = order.Payments
                    .ToList()
                    .FindIndex(payment => payment.PaymentGuid == paymentGuid);
                if (paymentIndex < 0 || paymentIndex >= order.Payments.Count)
                {
                    continue;
                }

                var updatedPayments = order.Payments.ToList();
                updatedPayments[paymentIndex] = updatedPayments[paymentIndex] with { Reference = reference };
                SavedOrders[index] = order with { Payments = updatedPayments };
                break;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return GetRecentOrdersAsync(take, cancellationToken);
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalOrder?>(SavedOrders.LastOrDefault(order => order.OrderGuid == orderGuid));
        }
    }

    private sealed class StubSyncQueueRepository(int pendingCount) : ISyncQueueRepository
    {
        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(pendingCount);
        }

        public Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SyncQueueOverview(pendingCount, 0, 0, null));
        }

        public Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SyncQueueListItem>>([]);
        }
    }

    private sealed class ThrowingPendingSyncQueueRepository : ISyncQueueRepository
    {
        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<int>(new InvalidOperationException("pending sync refresh failed"));

        public Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SyncQueueOverview(0, 0, 0, null));

        public Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SyncQueueListItem>>([]);
    }

    private static void AssertLocalTxnRef(char transactionType, string? txnRef)
    {
        Assert.NotNull(txnRef);
        Assert.Equal(16, txnRef.Length);
        Assert.Equal(transactionType, txnRef[0]);
        Assert.Matches("^[PR][0-9ABCDEFGHJKMNPQRSTVWXYZ]{15}$", txnRef);
    }

    private static LocalCardPaymentAttempt CreateOpenLinklyRefundAttempt(
        PosSessionState session,
        PosCartSnapshot cartSnapshot,
        LinklyConnectionMode connectionMode,
        string txnType,
        string? txnRef,
        string originalReference)
    {
        var now = DateTimeOffset.UtcNow;
        var draft = new CardPaymentOrderDraft(
            Guid.NewGuid(),
            session,
            cartSnapshot,
            [],
            -4m,
            4m,
            "R",
            originalReference,
            now);
        return new LocalCardPaymentAttempt(
            Guid.NewGuid(),
            null,
            txnRef,
            CardProcessorKind.Linkly.ToString(),
            CardTerminalEnvironment.Sandbox.ToString(),
            connectionMode.ToString(),
            txnType,
            4m,
            LocalCardPaymentAttemptStatus.Pending,
            JsonSerializer.Serialize(draft, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
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
            "Refund",
            draft.OrderGuid);
    }

    private sealed class RaceWinningCardPaymentAttemptRepository(
        LocalCardPaymentAttempt persistedAttempt,
        bool exposeWinnerDuringLookup) : ILocalCardPaymentAttemptRepository
    {
        private LocalCardPaymentAttempt _persistedAttempt = persistedAttempt;

        public int CreateAsyncCalls { get; private set; }
        public int CreateOrGetOpenRefundCalls { get; private set; }
        public int RefundSubmissionClaimCalls { get; private set; }
        public LocalCardPaymentAttempt PersistedAttempt => _persistedAttempt;
        public IReadOnlyList<LocalCardPaymentAttempt> PersistedAttempts => [_persistedAttempt];

        public Task CreateAsync(LocalCardPaymentAttempt attempt, CancellationToken cancellationToken = default)
        {
            CreateAsyncCalls++;
            throw new InvalidOperationException("The race-losing refund attempt must not be persisted.");
        }

        public Task<LocalCardPaymentAttempt> CreateOrGetOpenRefundAsync(
            LocalCardPaymentAttempt attempt,
            CancellationToken cancellationToken = default)
        {
            CreateOrGetOpenRefundCalls++;
            return Task.FromResult(_persistedAttempt);
        }

        public Task<bool> TryBeginRefundSubmissionAsync(
            Guid attemptGuid,
            DateTimeOffset expectedUpdatedAt,
            string submissionToken,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            RefundSubmissionClaimCalls++;
            _persistedAttempt = _persistedAttempt with
            {
                Status = LocalCardPaymentAttemptStatus.Recovering,
                SubmissionToken = submissionToken,
                UpdatedAt = updatedAt
            };
            return Task.FromResult(true);
        }

        public Task UpdateSessionAsync(
            Guid attemptGuid,
            string sessionId,
            string? txnRef,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            _persistedAttempt = _persistedAttempt with
            {
                SessionId = sessionId,
                TxnRef = txnRef,
                Status = LocalCardPaymentAttemptStatus.SessionStarted,
                UpdatedAt = updatedAt
            };
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
            _persistedAttempt = _persistedAttempt with
            {
                Status = status,
                ResponseCode = responseCode,
                ResponseText = responseText,
                PaymentReference = paymentReference,
                UpdatedAt = completedAt
            };
            return Task.CompletedTask;
        }

        public Task MarkOrderCompletedAsync(Guid attemptGuid, DateTimeOffset completedAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task MarkAcknowledgedAsync(Guid attemptGuid, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task MarkRecoveringAsync(Guid attemptGuid, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            _persistedAttempt = _persistedAttempt with
            {
                Status = LocalCardPaymentAttemptStatus.Recovering,
                UpdatedAt = updatedAt
            };
            return Task.CompletedTask;
        }

        public Task<LocalCardPaymentAttempt?> GetLatestOpenAttemptAsync(
            string storeCode,
            string deviceCode,
            string? cashierId,
            string environment,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalCardPaymentAttempt?>(_persistedAttempt);

        public Task<IReadOnlyList<LocalCardPaymentAttempt>> GetOpenRefundAttemptsAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalCardPaymentAttempt>>(
                exposeWinnerDuringLookup ? [_persistedAttempt] : []);

        public Task<LocalCardPaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalCardPaymentAttempt?>(
                attemptGuid == _persistedAttempt.AttemptGuid ? _persistedAttempt : null);
    }

    private sealed class RecordingCardPaymentAttemptRepository : ILocalCardPaymentAttemptRepository
    {
        public List<LocalCardPaymentAttempt> Attempts { get; } = [];

        public int MarkOrderCompletedCount { get; private set; }

        public int FinalizeRecoveryCount { get; private set; }

        public int TryUpdateOutcomeCount { get; private set; }

        public int PersistRecoveryOutcomeCount { get; private set; }

        public bool FinalizeRecoveryResult { get; init; } = true;

        public Exception? MarkOrderCompletedException { get; init; }

        public Exception? GetAttemptException { get; set; }

        public Exception? UpdateOutcomeException { get; init; }

        public Exception? MarkRecoveringException { get; init; }

        public LocalCardPaymentAttempt? OutcomeCasWinner { get; set; }

        public bool RejectCancelledTokens { get; init; }

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
            if (RejectCancelledTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (UpdateOutcomeException is not null)
            {
                throw UpdateOutcomeException;
            }

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

        public Task MarkOrderCompletedAsync(
            Guid attemptGuid,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            MarkOrderCompletedCount++;
            if (RejectCancelledTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (MarkOrderCompletedException is not null)
            {
                throw MarkOrderCompletedException;
            }

            Update(attemptGuid, attempt => attempt with
            {
                Status = LocalCardPaymentAttemptStatus.OrderCompleted,
                CompletedAt = attempt.CompletedAt ?? completedAt,
                UpdatedAt = completedAt
            });
            return Task.CompletedTask;
        }

        public async Task<bool> TryUpdateOutcomeAsync(
            Guid attemptGuid,
            LocalCardPaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            LocalCardPaymentAttemptStatus status,
            string? responseCode,
            string? responseText,
            string? paymentReference,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            TryUpdateOutcomeCount++;
            if (OutcomeCasWinner is not null)
            {
                var winner = OutcomeCasWinner;
                OutcomeCasWinner = null;
                Update(attemptGuid, _ => winner);
                return false;
            }

            var current = Attempts.Single(attempt => attempt.AttemptGuid == attemptGuid);
            if (current.Status != expectedStatus ||
                current.UpdatedAt != expectedUpdatedAt ||
                string.Equals(current.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) ||
                current.Status is LocalCardPaymentAttemptStatus.Declined or
                    LocalCardPaymentAttemptStatus.TimedOut or
                    LocalCardPaymentAttemptStatus.Cancelled or
                    LocalCardPaymentAttemptStatus.Failed or
                    LocalCardPaymentAttemptStatus.OrderCompleted or
                    LocalCardPaymentAttemptStatus.Abandoned ||
                !string.IsNullOrWhiteSpace(current.ResponseCode) &&
                current.ResponseCode.StartsWith("SUPERVISOR_", StringComparison.Ordinal))
            {
                return false;
            }

            await UpdateOutcomeAsync(
                attemptGuid,
                status,
                responseCode,
                responseText,
                paymentReference,
                completedAt,
                cancellationToken);
            return true;
        }

        public Task<bool> TryPersistRecoveryOutcomeAsync(
            Guid attemptGuid,
            LocalCardPaymentAttemptStatus openStatus,
            string? responseCode,
            string? responseText,
            string? paymentReference,
            LocalCardPaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            LocalCardPaymentAttemptStatus recoveryTargetStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            PersistRecoveryOutcomeCount++;
            if (OutcomeCasWinner is not null)
            {
                var winner = OutcomeCasWinner;
                OutcomeCasWinner = null;
                Update(attemptGuid, _ => winner);
                return Task.FromResult(false);
            }

            var current = Attempts.Single(attempt => attempt.AttemptGuid == attemptGuid);
            if (current.Status != expectedStatus ||
                current.UpdatedAt != expectedUpdatedAt ||
                string.Equals(current.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) ||
                current.Status is LocalCardPaymentAttemptStatus.Declined or
                    LocalCardPaymentAttemptStatus.TimedOut or
                    LocalCardPaymentAttemptStatus.Cancelled or
                    LocalCardPaymentAttemptStatus.Failed or
                    LocalCardPaymentAttemptStatus.OrderCompleted or
                    LocalCardPaymentAttemptStatus.Abandoned ||
                !string.IsNullOrWhiteSpace(current.ResponseCode) &&
                current.ResponseCode.StartsWith("SUPERVISOR_", StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Update(attemptGuid, attempt => attempt with
            {
                Status = openStatus,
                ResponseCode = responseCode,
                ResponseText = responseText,
                PaymentReference = string.IsNullOrWhiteSpace(attempt.PaymentReference)
                    ? paymentReference
                    : attempt.PaymentReference,
                RecoveryPhase = CardRecoveryPhases.FinalizePending,
                RecoveryTargetStatus = recoveryTargetStatus.ToString(),
                UpdatedAt = updatedAt
            });
            return Task.FromResult(true);
        }

        public Task<bool> TryFinalizeRecoveryOutcomeAsync(
            Guid attemptGuid,
            LocalCardPaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            LocalCardPaymentAttemptStatus recoveryTargetStatus,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            FinalizeRecoveryCount++;
            var current = Attempts.Single(attempt => attempt.AttemptGuid == attemptGuid);
            if (!FinalizeRecoveryResult ||
                current.Status != expectedStatus ||
                current.UpdatedAt != expectedUpdatedAt ||
                !string.Equals(current.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) ||
                !string.Equals(current.RecoveryTargetStatus, recoveryTargetStatus.ToString(), StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Update(attemptGuid, attempt => attempt with
            {
                Status = recoveryTargetStatus,
                RecoveryPhase = CardRecoveryPhases.None,
                RecoveryTargetStatus = null,
                CompletedAt = attempt.CompletedAt ?? completedAt,
                UpdatedAt = completedAt
            });
            return Task.FromResult(true);
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
            if (MarkRecoveringException is not null)
            {
                throw MarkRecoveringException;
            }

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
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalCardPaymentAttempt?>(Attempts.LastOrDefault());
        }

        public Task<IReadOnlyList<LocalCardPaymentAttempt>> GetOpenRefundAttemptsAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalCardPaymentAttempt>>(Attempts
                .Where(attempt =>
                    attempt.StoreCode == storeCode &&
                    attempt.DeviceCode == deviceCode &&
                    attempt.Environment == environment &&
                    attempt.OperationKind == "Refund" &&
                    attempt.Status is not (
                        LocalCardPaymentAttemptStatus.Declined or
                        LocalCardPaymentAttemptStatus.TimedOut or
                        LocalCardPaymentAttemptStatus.Cancelled or
                        LocalCardPaymentAttemptStatus.Failed or
                        LocalCardPaymentAttemptStatus.OrderCompleted or
                        LocalCardPaymentAttemptStatus.Abandoned))
                .OrderByDescending(attempt => attempt.UpdatedAt)
                .ToArray());
        }

        public Task<LocalCardPaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default)
        {
            if (RejectCancelledTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (GetAttemptException is not null)
            {
                throw GetAttemptException;
            }

            return Task.FromResult<LocalCardPaymentAttempt?>(Attempts.SingleOrDefault(attempt => attempt.AttemptGuid == attemptGuid));
        }

        private void Update(Guid attemptGuid, Func<LocalCardPaymentAttempt, LocalCardPaymentAttempt> update)
        {
            var index = Attempts.FindIndex(attempt => attempt.AttemptGuid == attemptGuid);
            Assert.True(index >= 0);
            Attempts[index] = update(Attempts[index]);
        }
    }

    private sealed class RecordingSquarePaymentAttemptRepository : ILocalSquarePaymentAttemptRepository
    {
        public List<LocalSquarePaymentAttempt> Attempts { get; } = [];

        public int MarkOrderCompletedCount { get; private set; }

        public int CompleteRecoveryFinalizationCount { get; private set; }

        public int TryMarkFailedCount { get; private set; }

        public int BeginRecoveryFinalizationCount { get; private set; }

        public int RefundResponseCasCount { get; private set; }

        public int RefundPaymentVerifiedCasCount { get; private set; }

        public int RefundFailedCasCount { get; private set; }

        public int RefundFailureFinalizationCount { get; private set; }

        public bool CompleteRecoveryFinalizationResult { get; init; } = true;

        public Exception? CompleteRecoveryFinalizationException { get; init; }

        public Exception? MarkOrderCompletedException { get; init; }

        public bool CompleteRefundDuringFailureHandoff { get; init; }

        public bool ReplaceRefundDuringFailureHandoff { get; init; }

        public LocalSquarePaymentAttemptStatus? RefundFailureHandoffCasWinnerStatus { get; init; }

        public Exception? RefundFailureFinalizationException { get; init; }
        public int RefundFailureFinalizationExceptionsRemaining { get; set; } = int.MaxValue;

        public bool RejectCancelledTokens { get; init; }

        public Exception? MarkCheckoutCreatedException { get; init; }

        public LocalSquarePaymentAttempt? MarkFailedCasWinner { get; set; }

        public LocalSquarePaymentAttempt? BeginRecoveryFinalizationCasWinner { get; set; }

        public Task<bool> TryBeginRefundSubmissionAsync(
            Guid attemptGuid,
            DateTimeOffset expectedUpdatedAt,
            string submissionToken,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            var attempt = Attempts.Single(candidate => candidate.AttemptGuid == attemptGuid);
            if (attempt.UpdatedAt != expectedUpdatedAt)
            {
                return Task.FromResult(false);
            }

            Update(attemptGuid, current => current with
            {
                Status = LocalSquarePaymentAttemptStatus.Recovering,
                ResponseCode = null,
                ResponseText = null,
                SubmissionToken = submissionToken,
                UpdatedAt = updatedAt
            });
            return Task.FromResult(true);
        }

        public Task<bool> TryRecordRefundResponseAsync(
            Guid attemptGuid,
            string submissionToken,
            string refundId,
            string refundStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            var attempt = Attempts.Single(candidate => candidate.AttemptGuid == attemptGuid);
            if (!string.Equals(attempt.SubmissionToken, submissionToken, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Update(attemptGuid, current => current with
            {
                Status = LocalSquarePaymentAttemptStatus.Recovering,
                PaymentId = refundId,
                PaymentStatus = refundStatus,
                UpdatedAt = updatedAt
            });
            return Task.FromResult(true);
        }

        public Task<bool> TryRecordRefundResponseAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            string submissionToken,
            string refundId,
            string refundStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            RefundResponseCasCount++;
            var attempt = Attempts.Single(candidate => candidate.AttemptGuid == attemptGuid);
            if (attempt.Status != expectedStatus ||
                attempt.UpdatedAt != expectedUpdatedAt ||
                !string.Equals(attempt.SubmissionToken, submissionToken, StringComparison.Ordinal) ||
                attempt.Status is not (LocalSquarePaymentAttemptStatus.Recovering or LocalSquarePaymentAttemptStatus.Unknown) ||
                BlocksAutomaticRefundWrite(
                    attempt,
                    allowCompletedEvidence: string.Equals(
                        refundStatus.Trim(),
                        "COMPLETED",
                        StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(false);
            }

            Update(attemptGuid, current => current with
            {
                Status = LocalSquarePaymentAttemptStatus.Recovering,
                PaymentId = refundId,
                PaymentStatus = refundStatus,
                UpdatedAt = updatedAt
            });
            return Task.FromResult(true);
        }

        public Task<bool> TryMarkRefundPaymentVerifiedAsync(
            Guid attemptGuid,
            string submissionToken,
            string paymentId,
            string paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            var attempt = Attempts.Single(candidate => candidate.AttemptGuid == attemptGuid);
            if (!string.Equals(attempt.SubmissionToken, submissionToken, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Update(attemptGuid, current => current with
            {
                Status = LocalSquarePaymentAttemptStatus.PaymentVerified,
                PaymentId = paymentId,
                PaymentStatus = paymentStatus,
                ResponseCode = responseCode,
                ResponseText = responseText,
                CompletedAt = completedAt,
                UpdatedAt = completedAt
            });
            return Task.FromResult(true);
        }

        public Task<bool> TryMarkRefundPaymentVerifiedAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            string submissionToken,
            string paymentId,
            string paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            RefundPaymentVerifiedCasCount++;
            var attempt = Attempts.Single(candidate => candidate.AttemptGuid == attemptGuid);
            if (attempt.Status != expectedStatus ||
                attempt.UpdatedAt != expectedUpdatedAt ||
                !string.Equals(attempt.SubmissionToken, submissionToken, StringComparison.Ordinal) ||
                BlocksAutomaticRefundWrite(attempt, allowCompletedEvidence: true))
            {
                return Task.FromResult(false);
            }

            Update(attemptGuid, current => current with
            {
                Status = LocalSquarePaymentAttemptStatus.PaymentVerified,
                PaymentId = paymentId,
                PaymentStatus = paymentStatus,
                ResponseCode = responseCode,
                ResponseText = responseText,
                RecoveryPhase = CardRecoveryPhases.FinalizePending,
                RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.OrderCompleted,
                CompletedAt = completedAt,
                UpdatedAt = completedAt
            });
            return Task.FromResult(true);
        }

        public void SimulateSupervisorRetryAndNewClaim(string submissionToken)
        {
            var attempt = Assert.Single(Attempts);
            Update(attempt.AttemptGuid, current => current with
            {
                Status = LocalSquarePaymentAttemptStatus.Recovering,
                ResponseCode = null,
                ResponseText = null,
                SubmissionToken = submissionToken,
                UpdatedAt = current.UpdatedAt.AddTicks(1)
            });
        }

        public void SimulateRefundFailureFinalizationWinner()
        {
            var attempt = Assert.Single(Attempts);
            Update(attempt.AttemptGuid, current => current with
            {
                Status = LocalSquarePaymentAttemptStatus.Unknown,
                PaymentStatus = "FAILED",
                ResponseCode = "SQUARE_FAILURE",
                ResponseText = "Square refund reached a terminal failure.",
                RecoveryPhase = CardRecoveryPhases.FinalizePending,
                RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.Abandoned,
                UpdatedAt = current.UpdatedAt.AddTicks(1)
            });
        }

        public void SimulateRefundResponse(
            string paymentStatus,
            LocalSquarePaymentAttemptStatus status = LocalSquarePaymentAttemptStatus.Recovering)
        {
            var attempt = Assert.Single(Attempts);
            Update(attempt.AttemptGuid, current => current with
            {
                Status = status,
                PaymentId = "square-refund-terminal",
                PaymentStatus = paymentStatus,
                UpdatedAt = current.UpdatedAt.AddTicks(1)
            });
        }

        public Task<bool> TryPersistRefundFailureForFinalizationAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            string submissionToken,
            string paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            RefundFailureFinalizationCount++;
            if (RefundFailureFinalizationException is not null &&
                RefundFailureFinalizationExceptionsRemaining != 0)
            {
                if (RefundFailureFinalizationExceptionsRemaining != int.MaxValue)
                {
                    RefundFailureFinalizationExceptionsRemaining--;
                }

                throw RefundFailureFinalizationException;
            }

            var attempt = Attempts.Single(candidate => candidate.AttemptGuid == attemptGuid);
            if (CompleteRefundDuringFailureHandoff)
            {
                Update(attemptGuid, current => current with
                {
                    Status = LocalSquarePaymentAttemptStatus.PaymentVerified,
                    PaymentStatus = "COMPLETED",
                    ResponseCode = "SQUARE_COMPLETED",
                    ResponseText = "A concurrent worker persisted completion.",
                    RecoveryPhase = CardRecoveryPhases.FinalizePending,
                    RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.OrderCompleted,
                    UpdatedAt = current.UpdatedAt.AddTicks(1)
                });
                return Task.FromResult(false);
            }

            if (ReplaceRefundDuringFailureHandoff)
            {
                Update(attemptGuid, current => current with
                {
                    Status = LocalSquarePaymentAttemptStatus.Recovering,
                    SubmissionToken = "replacement-worker-token",
                    UpdatedAt = current.UpdatedAt.AddTicks(1)
                });
                return Task.FromResult(false);
            }

            if (RefundFailureHandoffCasWinnerStatus is { } terminalWinnerStatus &&
                RefundFailureFinalizationCount == 1)
            {
                Update(attemptGuid, current => current with
                {
                    Status = terminalWinnerStatus,
                    PaymentStatus = terminalWinnerStatus == LocalSquarePaymentAttemptStatus.OrderCompleted
                        ? "COMPLETED"
                        : "FAILED",
                    ResponseCode = terminalWinnerStatus == LocalSquarePaymentAttemptStatus.OrderCompleted
                        ? "SQUARE_COMPLETED"
                        : "SQUARE_FAILURE",
                    ResponseText = "A concurrent worker persisted a terminal winner.",
                    RecoveryPhase = CardRecoveryPhases.None,
                    RecoveryTargetStatus = null,
                    UpdatedAt = current.UpdatedAt.AddTicks(1)
                });
                return Task.FromResult(false);
            }

            if (attempt.Status != expectedStatus ||
                attempt.UpdatedAt != expectedUpdatedAt ||
                !string.Equals(attempt.SubmissionToken, submissionToken, StringComparison.Ordinal) ||
                !string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal) ||
                string.Equals(attempt.PaymentStatus, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(false);
            }

            Update(attemptGuid, current => current with
            {
                Status = LocalSquarePaymentAttemptStatus.Unknown,
                PaymentStatus = paymentStatus.ToUpperInvariant(),
                ResponseCode = current.ResponseCode ?? responseCode,
                ResponseText = current.ResponseText ?? responseText,
                RecoveryPhase = CardRecoveryPhases.FinalizePending,
                RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.Abandoned,
                UpdatedAt = updatedAt
            });
            return Task.FromResult(true);
        }

        public Task CreateAsync(LocalSquarePaymentAttempt attempt, CancellationToken cancellationToken = default)
        {
            Attempts.Add(attempt);
            return Task.CompletedTask;
        }

        public Task MarkCheckoutCreatedAsync(
            Guid attemptGuid,
            string checkoutId,
            string? checkoutStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (RejectCancelledTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (MarkCheckoutCreatedException is not null)
            {
                throw MarkCheckoutCreatedException;
            }

            Update(attemptGuid, attempt => attempt with
            {
                CheckoutId = checkoutId,
                CheckoutStatus = checkoutStatus,
                Status = LocalSquarePaymentAttemptStatus.CheckoutCreated,
                UpdatedAt = updatedAt
            });
            return Task.CompletedTask;
        }

        public Task<bool> TryMarkCheckoutCreatedAsync(
            Guid attemptGuid,
            string submissionToken,
            string checkoutId,
            string? checkoutStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            var attempt = Attempts.Single(candidate => candidate.AttemptGuid == attemptGuid);
            if (!string.Equals(attempt.SubmissionToken, submissionToken, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            if (MarkCheckoutCreatedException is not null)
            {
                throw MarkCheckoutCreatedException;
            }

            Update(attemptGuid, current => current with
            {
                CheckoutId = checkoutId,
                CheckoutStatus = checkoutStatus,
                Status = LocalSquarePaymentAttemptStatus.CheckoutCreated,
                UpdatedAt = updatedAt
            });
            return Task.FromResult(true);
        }

        public Task MarkRecoveringAsync(Guid attemptGuid, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            Update(attemptGuid, attempt => attempt with
            {
                Status = LocalSquarePaymentAttemptStatus.Recovering,
                UpdatedAt = updatedAt
            });
            return Task.CompletedTask;
        }

        public Task UpdateCheckoutStatusAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus status,
            string? checkoutStatus,
            string? cancelReason,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            Update(attemptGuid, attempt => attempt with
            {
                Status = status,
                CheckoutStatus = checkoutStatus ?? attempt.CheckoutStatus,
                CancelReason = cancelReason ?? attempt.CancelReason,
                UpdatedAt = updatedAt
            });
            return Task.CompletedTask;
        }

        public Task MarkPaymentVerifiedAsync(
            Guid attemptGuid,
            string paymentId,
            string paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            Update(attemptGuid, attempt => attempt with
            {
                Status = LocalSquarePaymentAttemptStatus.PaymentVerified,
                PaymentId = paymentId,
                PaymentStatus = paymentStatus,
                ResponseCode = responseCode,
                ResponseText = responseText,
                CompletedAt = completedAt,
                UpdatedAt = completedAt
            });
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus status,
            string? checkoutStatus,
            string? paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset resolvedAt,
            CancellationToken cancellationToken = default,
            string? cancelReason = null)
        {
            Update(attemptGuid, attempt => attempt with
            {
                Status = status,
                CheckoutStatus = checkoutStatus ?? attempt.CheckoutStatus,
                CancelReason = cancelReason ?? attempt.CancelReason,
                PaymentStatus = paymentStatus ?? attempt.PaymentStatus,
                ResponseCode = responseCode,
                ResponseText = responseText,
                ResolvedAt = resolvedAt,
                UpdatedAt = resolvedAt
            });
            return Task.CompletedTask;
        }

        public Task<bool> TryMarkRefundFailedAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            string submissionToken,
            LocalSquarePaymentAttemptStatus status,
            string? checkoutStatus,
            string? paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset resolvedAt,
            CancellationToken cancellationToken = default,
            string? cancelReason = null)
        {
            RefundFailedCasCount++;
            var attempt = Attempts.Single(candidate => candidate.AttemptGuid == attemptGuid);
            if (attempt.Status != expectedStatus ||
                attempt.UpdatedAt != expectedUpdatedAt ||
                !string.Equals(attempt.SubmissionToken, submissionToken, StringComparison.Ordinal) ||
                BlocksAutomaticRefundWrite(attempt))
            {
                return Task.FromResult(false);
            }

            Update(attemptGuid, current => current with
            {
                Status = status,
                CheckoutStatus = checkoutStatus ?? current.CheckoutStatus,
                CancelReason = cancelReason ?? current.CancelReason,
                PaymentStatus = paymentStatus ?? current.PaymentStatus,
                ResponseCode = responseCode,
                ResponseText = responseText,
                ResolvedAt = resolvedAt,
                UpdatedAt = resolvedAt
            });
            return Task.FromResult(true);
        }

        public Task MarkOrderCompletedAsync(Guid attemptGuid, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
        {
            MarkOrderCompletedCount++;
            if (MarkOrderCompletedException is not null)
            {
                throw MarkOrderCompletedException;
            }

            Update(attemptGuid, attempt => attempt with
            {
                Status = LocalSquarePaymentAttemptStatus.OrderCompleted,
                OrderCompletedAt = completedAt,
                UpdatedAt = completedAt
            });
            return Task.CompletedTask;
        }

        public Task<bool> TryMarkFailedAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            LocalSquarePaymentAttemptStatus status,
            string? checkoutStatus,
            string? paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset resolvedAt,
            CancellationToken cancellationToken = default,
            string? cancelReason = null)
        {
            TryMarkFailedCount++;
            if (MarkFailedCasWinner is not null)
            {
                var winner = MarkFailedCasWinner;
                MarkFailedCasWinner = null;
                Update(attemptGuid, _ => winner);
                return Task.FromResult(false);
            }

            var current = Attempts.Single(attempt => attempt.AttemptGuid == attemptGuid);
            if (current.Status != expectedStatus ||
                current.UpdatedAt != expectedUpdatedAt ||
                string.Equals(current.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) ||
                current.Status is LocalSquarePaymentAttemptStatus.Canceled or
                    LocalSquarePaymentAttemptStatus.TimedOut or
                    LocalSquarePaymentAttemptStatus.Failed or
                    LocalSquarePaymentAttemptStatus.OrderCompleted or
                    LocalSquarePaymentAttemptStatus.Abandoned ||
                !string.IsNullOrWhiteSpace(current.ResponseCode) &&
                current.ResponseCode.StartsWith("SUPERVISOR_", StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Update(attemptGuid, attempt => attempt with
            {
                Status = status,
                CheckoutStatus = checkoutStatus ?? attempt.CheckoutStatus,
                CancelReason = cancelReason ?? attempt.CancelReason,
                PaymentStatus = paymentStatus ?? attempt.PaymentStatus,
                ResponseCode = responseCode,
                ResponseText = responseText,
                ResolvedAt = resolvedAt,
                UpdatedAt = resolvedAt
            });
            return Task.FromResult(true);
        }

        public Task<bool> TryBeginRecoveryFinalizationAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            LocalSquarePaymentAttemptStatus targetStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            BeginRecoveryFinalizationCount++;
            if (BeginRecoveryFinalizationCasWinner is not null)
            {
                var winner = BeginRecoveryFinalizationCasWinner;
                BeginRecoveryFinalizationCasWinner = null;
                Update(attemptGuid, _ => winner);
                return Task.FromResult(false);
            }

            var current = Attempts.Single(attempt => attempt.AttemptGuid == attemptGuid);
            if (current.Status != expectedStatus ||
                current.UpdatedAt != expectedUpdatedAt ||
                !string.Equals(current.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal) ||
                current.Status is LocalSquarePaymentAttemptStatus.Canceled or
                    LocalSquarePaymentAttemptStatus.TimedOut or
                    LocalSquarePaymentAttemptStatus.Failed or
                    LocalSquarePaymentAttemptStatus.OrderCompleted or
                    LocalSquarePaymentAttemptStatus.Abandoned)
            {
                return Task.FromResult(false);
            }

            Update(attemptGuid, attempt => attempt with
            {
                RecoveryPhase = CardRecoveryPhases.FinalizePending,
                RecoveryTargetStatus = targetStatus,
                UpdatedAt = updatedAt
            });
            return Task.FromResult(true);
        }

        public Task<bool> TryCompleteRecoveryFinalizationAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            LocalSquarePaymentAttemptStatus targetStatus,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            CompleteRecoveryFinalizationCount++;
            if (CompleteRecoveryFinalizationException is not null)
            {
                throw CompleteRecoveryFinalizationException;
            }

            var current = Attempts.Single(attempt => attempt.AttemptGuid == attemptGuid);
            if (!CompleteRecoveryFinalizationResult ||
                current.Status != expectedStatus ||
                current.UpdatedAt != expectedUpdatedAt ||
                !string.Equals(current.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) ||
                current.RecoveryTargetStatus != targetStatus)
            {
                return Task.FromResult(false);
            }

            Update(attemptGuid, attempt => attempt with
            {
                Status = targetStatus,
                RecoveryPhase = CardRecoveryPhases.None,
                RecoveryTargetStatus = null,
                OrderCompletedAt = targetStatus == LocalSquarePaymentAttemptStatus.OrderCompleted
                    ? completedAt
                    : attempt.OrderCompletedAt,
                ResolvedAt = targetStatus == LocalSquarePaymentAttemptStatus.OrderCompleted
                    ? attempt.ResolvedAt
                    : completedAt,
                UpdatedAt = completedAt
            });
            return Task.FromResult(true);
        }

        public Task<LocalSquarePaymentAttempt?> GetLatestOpenAttemptAsync(
            string storeCode,
            string deviceCode,
            string? cashierId,
            string environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalSquarePaymentAttempt?>(Attempts.LastOrDefault());
        }

        public Task<IReadOnlyList<LocalSquarePaymentAttempt>> GetOpenRefundAttemptsAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalSquarePaymentAttempt>>(Attempts
                .Where(attempt =>
                    attempt.StoreCode == storeCode &&
                    attempt.DeviceCode == deviceCode &&
                    attempt.Environment == environment &&
                    attempt.OperationKind == "Refund" &&
                    attempt.Status is not (
                        LocalSquarePaymentAttemptStatus.Canceled or
                        LocalSquarePaymentAttemptStatus.TimedOut or
                        LocalSquarePaymentAttemptStatus.Failed or
                        LocalSquarePaymentAttemptStatus.OrderCompleted or
                        LocalSquarePaymentAttemptStatus.Abandoned))
                .OrderByDescending(attempt => attempt.UpdatedAt)
                .ToArray());
        }

        public Task<LocalSquarePaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalSquarePaymentAttempt?>(Attempts.SingleOrDefault(attempt => attempt.AttemptGuid == attemptGuid));
        }

        private static bool BlocksAutomaticRefundWrite(
            LocalSquarePaymentAttempt attempt,
            bool allowCompletedEvidence = false) =>
            string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) ||
            !allowCompletedEvidence &&
            string.Equals(attempt.PaymentStatus?.Trim(), "COMPLETED", StringComparison.OrdinalIgnoreCase) ||
            attempt.Status is LocalSquarePaymentAttemptStatus.Canceled or
                LocalSquarePaymentAttemptStatus.TimedOut or
                LocalSquarePaymentAttemptStatus.Failed or
                LocalSquarePaymentAttemptStatus.OrderCompleted or
                LocalSquarePaymentAttemptStatus.Abandoned ||
            !string.IsNullOrWhiteSpace(attempt.ResponseCode) &&
            attempt.ResponseCode.StartsWith("SUPERVISOR_", StringComparison.Ordinal);

        private void Update(Guid attemptGuid, Func<LocalSquarePaymentAttempt, LocalSquarePaymentAttempt> update)
        {
            var index = Attempts.FindIndex(attempt => attempt.AttemptGuid == attemptGuid);
            Assert.True(index >= 0);
            Attempts[index] = update(Attempts[index]);
        }
    }

    private sealed class ObservingCardTerminalClient(
        Action beforeResult,
        PaymentAuthorizationResult? result = null) : ICardTerminalClient
    {
        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            beforeResult();
            return Task.FromResult(result ?? new PaymentAuthorizationResult(
                true,
                "ANZBACKEND:TXN-1:session=backend-session-1:environment=Sandbox",
                "APPROVED",
                amount,
                [
                    new CardTransactionDto(
                        "ANZ",
                        "TXN-1",
                        null,
                        null,
                        null,
                        null,
                        null,
                        "00",
                        "APPROVED",
                        null,
                        DateTimeOffset.UtcNow,
                        amount,
                        null)
                ],
                "ANZ",
                "Sandbox",
                LinklyConnectionMode.CloudBackendAsync.ToString(),
                "P",
                "backend-session-1",
                "TXN-1",
                "00",
                "APPROVED"));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class AsyncObservingCardTerminalClient(
        Func<Task> beforeResult,
        PaymentAuthorizationResult result) : ICardTerminalClient
    {
        public async Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            await beforeResult();
            return result;
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CancelledCardTerminalClient : ICardTerminalClient
    {
        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled<PaymentAuthorizationResult>(cancellationToken);

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled<PaymentAuthorizationResult>(cancellationToken);
    }

    private sealed class CancellingCardTerminalClient(
        Func<CancellationToken, Task> beforeCancellation) : ICardTerminalClient
    {
        public async Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            await beforeCancellation(cancellationToken);
            throw new OperationCanceledException();
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingCardTerminalClient(
        Func<CancellationToken, Task>? beforeThrow = null) : ICardTerminalClient
    {
        public async Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            if (beforeThrow is not null)
            {
                await beforeThrow(cancellationToken);
            }

            throw new InvalidOperationException("terminal transport failed");
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CancellingApprovedCardTerminalClient(
        ILinklyPaymentAttemptContextAccessor accessor,
        CancellationTokenSource cancellation,
        PaymentAuthorizationResult result) : ICardTerminalClient
    {
        public async Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            var context = accessor.Current;
            Assert.NotNull(context);
            await context!.BindSessionAsync(
                result.SessionId ?? throw new InvalidOperationException("Expected a session id."),
                result.TxnRef,
                DateTimeOffset.UtcNow,
                cancellationToken);
            cancellation.Cancel();
            return result;
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class BindingCardTerminalClient(
        ILinklyPaymentAttemptContextAccessor accessor,
        PaymentAuthorizationResult result,
        Action beforeBind,
        Action afterBind) : ICardTerminalClient
    {
        public int AuthorizeCallCount { get; private set; }

        public async Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            AuthorizeCallCount++;
            beforeBind();
            var context = accessor.Current;
            Assert.NotNull(context);
            await context!.BindSessionAsync(
                result.SessionId ?? throw new InvalidOperationException("Expected a session id."),
                result.TxnRef,
                DateTimeOffset.UtcNow,
                cancellationToken);
            afterBind();
            return result;
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class LocalReferenceCardTerminalClient(
        ILinklyPaymentAttemptContextAccessor accessor,
        Action beforeResult) : ICardTerminalClient
    {
        public string? SeenTxnRef { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            beforeResult();
            var context = accessor.Current;
            Assert.NotNull(context);
            Assert.False(string.IsNullOrWhiteSpace(context!.TxnRef));
            SeenTxnRef = context.TxnRef;
            return Task.FromResult(new PaymentAuthorizationResult(
                true,
                $"ANZ:{SeenTxnRef}",
                "APPROVED",
                amount,
                [
                    new CardTransactionDto(
                        "ANZ",
                        SeenTxnRef,
                        null,
                        null,
                        null,
                        null,
                        null,
                        "00",
                        "APPROVED",
                        null,
                        DateTimeOffset.UtcNow,
                        amount,
                        null)
                ],
                "ANZ",
                "Sandbox",
                LinklyConnectionMode.LocalIp.ToString(),
                "P",
                null,
                SeenTxnRef,
                "00",
                "APPROVED"));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingLinklyBackendTerminalClient : ILinklyBackendTerminalClient
    {
        public Exception? AcknowledgeException { get; init; }

        public string? AcknowledgedSessionId { get; private set; }

        public CardTerminalSettings? AcknowledgedSettings { get; private set; }

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
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> ResumeSessionUntilFinalAsync(
            CardTerminalSettings settings,
            LinklyCloudBackendSessionResponse activeStatus,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AcknowledgeSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            AcknowledgedSettings = settings;
            AcknowledgedSessionId = sessionId;
            if (AcknowledgeException is not null)
            {
                throw AcknowledgeException;
            }

            return Task.CompletedTask;
        }

        public Task AcknowledgeSupervisorResolvedSessionAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default) =>
            AcknowledgeSessionAsync(settings, sessionId, cancellationToken);
    }

    private sealed class SettingsBoundRecordingCardTerminalClient(
        ILinklyPaymentAttemptContextAccessor linklyContext,
        ISquarePaymentAttemptContextAccessor squareContext) :
        ICardTerminalClient,
        ICardTerminalSettingsBoundClient,
        IIdempotentCardRefundClient
    {
        public int BoundAuthorizeCallCount { get; private set; }

        public int BoundRefundCallCount { get; private set; }

        public int LegacyAuthorizeCallCount { get; private set; }

        public int LegacyRefundCallCount { get; private set; }

        public CardTerminalSettings? LastSettings { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            LegacyAuthorizeCallCount++;
            throw new InvalidOperationException("设置已冻结的 Card 流程不得调用旧收款入口。");
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            LegacyRefundCallCount++;
            throw new InvalidOperationException("设置已冻结的 Card 流程不得调用旧退款入口。");
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            LegacyRefundCallCount++;
            throw new InvalidOperationException("设置已冻结的 Card 流程不得调用旧幂等退款入口。");
        }

        public async Task<PaymentAuthorizationResult> AuthorizeWithSettingsAsync(
            CardTerminalSettings settings,
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BoundAuthorizeCallCount++;
            LastSettings = settings;
            if (settings.Processor == CardProcessorKind.Square)
            {
                var attempt = Assert.IsType<SquarePaymentAttemptContext>(squareContext.Current);
                Assert.NotNull(attempt.BindCheckoutAsync);
                await attempt.BindCheckoutAsync!(
                    "checkout-settings-snapshot",
                    "COMPLETED",
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
                return CreateSquareResult(amount, isRefund: false, settings);
            }

            return CreateLinklyResult(amount, "P", settings);
        }

        public async Task<PaymentAuthorizationResult> RefundWithSettingsAsync(
            CardTerminalSettings settings,
            decimal amount,
            PosSessionState session,
            string? originalReference,
            string? idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BoundRefundCallCount++;
            LastSettings = settings;
            Assert.False(string.IsNullOrWhiteSpace(idempotencyKey));
            if (settings.Processor == CardProcessorKind.Square)
            {
                var attempt = Assert.IsType<SquarePaymentAttemptContext>(squareContext.Current);
                Assert.NotNull(attempt.BindRefundAsync);
                await attempt.BindRefundAsync!(
                    "refund-settings-snapshot",
                    "COMPLETED",
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
                return CreateSquareResult(amount, isRefund: true, settings);
            }

            return CreateLinklyResult(amount, "R", settings);
        }

        private PaymentAuthorizationResult CreateLinklyResult(
            decimal amount,
            string txnType,
            CardTerminalSettings settings)
        {
            var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode);
            var txnRef = linklyContext.Current?.TxnRef;
            if (mode == LinklyConnectionMode.LocalIp)
            {
                Assert.False(string.IsNullOrWhiteSpace(txnRef));
            }

            txnRef ??= "CLOUDSNAPSHOT001";
            return new PaymentAuthorizationResult(
                true,
                $"ANZ:{txnRef}",
                "APPROVED",
                amount,
                Processor: CardProcessorKind.Linkly.ToString(),
                Environment: settings.Environment.ToString(),
                ConnectionMode: CardTerminalSettings.FormatLinklyConnectionMode(mode),
                TxnType: txnType,
                TxnRef: txnRef,
                ResponseCode: "00",
                ResponseText: "APPROVED");
        }

        private static PaymentAuthorizationResult CreateSquareResult(
            decimal amount,
            bool isRefund,
            CardTerminalSettings settings)
        {
            return new PaymentAuthorizationResult(
                true,
                isRefund ? "SQRF:refund-settings-snapshot" : "SQ:payment-settings-snapshot",
                "COMPLETED",
                amount,
                Processor: CardProcessorKind.Square.ToString(),
                Environment: settings.Environment.ToString(),
                ResponseCode: "00",
                ResponseText: "COMPLETED");
        }
    }

    private sealed class ApprovedCardTerminalClient(string reference) : ICardTerminalClient
    {
        public int AuthorizeCallCount { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            AuthorizeCallCount++;
            return Task.FromResult(new PaymentAuthorizationResult(true, reference));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(true, $"REFUND:{originalReference}", AuthorizedAmount: amount));
        }
    }

    private sealed class ApprovedVoucherTenderClient(
        string reference,
        decimal? authorizedAmount = null,
        bool approveRefund = true) : IVoucherTenderClient
    {
        public int IssueRefundCallCount { get; private set; }

        public int ReleaseCallCount { get; private set; }

        public string? LastOrderReference { get; private set; }

        public string? LastIdempotencyKey { get; private set; }

        public string? LastReleaseVoucherCode { get; private set; }

        public string? LastReleaseReservationToken { get; private set; }

        public Task<PaymentAuthorizationResult> RedeemAsync(
            decimal amount,
            PosSessionState session,
            string? voucherCode,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("ABC123", voucherCode);
            return Task.FromResult(new PaymentAuthorizationResult(true, reference, AuthorizedAmount: authorizedAmount));
        }

        public Task<PaymentAuthorizationResult> IssueRefundAsync(
            decimal amount,
            PosSessionState session,
            string orderReference,
            string idempotencyKey,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            IssueRefundCallCount++;
            LastOrderReference = orderReference;
            LastIdempotencyKey = idempotencyKey;
            return Task.FromResult(approveRefund
                ? new PaymentAuthorizationResult(true, reference, AuthorizedAmount: authorizedAmount ?? amount)
                : new PaymentAuthorizationResult(false, null, "issue failed"));
        }

        public Task<bool> ReleaseAsync(
            PosSessionState session,
            string voucherCode,
            string reservationToken,
            CancellationToken cancellationToken = default)
        {
            ReleaseCallCount++;
            LastReleaseVoucherCode = voucherCode;
            LastReleaseReservationToken = reservationToken;
            return Task.FromResult(true);
        }
    }

    private sealed class RetriableVoucherTenderClient(string reference) : IVoucherTenderClient
    {
        public bool FailIssueRefund { get; set; } = true;

        public int IssueRefundCallCount { get; private set; }

        public List<string> IssueRefundIdempotencyKeys { get; } = [];

        public Task<PaymentAuthorizationResult> RedeemAsync(
            decimal amount,
            PosSessionState session,
            string? voucherCode,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PaymentAuthorizationResult> IssueRefundAsync(
            decimal amount,
            PosSessionState session,
            string orderReference,
            string idempotencyKey,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            IssueRefundCallCount++;
            IssueRefundIdempotencyKeys.Add(idempotencyKey);
            return Task.FromResult(FailIssueRefund
                ? new PaymentAuthorizationResult(false, null, "issue failed")
                : new PaymentAuthorizationResult(true, reference, AuthorizedAmount: amount));
        }

        public Task<bool> ReleaseAsync(
            PosSessionState session,
            string voucherCode,
            string reservationToken,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FatalVoucherTenderClient(Exception issueRefundException) : IVoucherTenderClient
    {
        public Task<PaymentAuthorizationResult> RedeemAsync(
            decimal amount,
            PosSessionState session,
            string? voucherCode,
            CancellationToken cancellationToken = default) =>
            Task.FromException<PaymentAuthorizationResult>(new NotSupportedException());

        public Task<PaymentAuthorizationResult> IssueRefundAsync(
            decimal amount,
            PosSessionState session,
            string orderReference,
            string idempotencyKey,
            string? reason = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<PaymentAuthorizationResult>(issueRefundException);

        public Task<bool> ReleaseAsync(
            PosSessionState session,
            string voucherCode,
            string reservationToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class RecordingIdempotentCardRefundClient(
        PaymentAuthorizationResult result,
        Action? beforeResult = null) : ICardTerminalClient, IIdempotentCardRefundClient
    {
        public int IdempotentRefundCallCount { get; private set; }

        public string? LastOriginalReference { get; private set; }

        public string? LastIdempotencyKey { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Durable refunds must use the idempotent overload.");

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            IdempotentRefundCallCount++;
            LastOriginalReference = originalReference;
            LastIdempotencyKey = idempotencyKey;
            beforeResult?.Invoke();
            return Task.FromResult(result);
        }
    }

    private sealed class NotSubmittedCancellingCardTerminalClient(
        CancellationTokenSource cancellation) : ICardTerminalClient, IIdempotentCardRefundClient
    {
        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default) =>
            Task.FromException<PaymentAuthorizationResult>(CreateException());

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            Task.FromException<PaymentAuthorizationResult>(CreateException());

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            Task.FromException<PaymentAuthorizationResult>(CreateException());

        private CardTerminalNotSubmittedException CreateException()
        {
            cancellation.Cancel();
            var inner = new OperationCanceledException(cancellation.Token);
            return new CardTerminalNotSubmittedException(inner, cancellation.Token);
        }
    }

    private sealed class ThrowingVoucherTenderClient : IVoucherTenderClient
    {
        private readonly Exception _releaseException;

        public ThrowingVoucherTenderClient(Exception? releaseException = null)
        {
            _releaseException = releaseException ?? new HttpRequestException("release unavailable");
        }

        public Task<PaymentAuthorizationResult> RedeemAsync(
            decimal amount,
            PosSessionState session,
            string? voucherCode,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PaymentAuthorizationResult> IssueRefundAsync(
            decimal amount,
            PosSessionState session,
            string orderReference,
            string idempotencyKey,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> ReleaseAsync(
            PosSessionState session,
            string voucherCode,
            string reservationToken,
            CancellationToken cancellationToken = default)
        {
            throw _releaseException;
        }
    }

    private sealed class FailingOnceOrderUploadService : IOrderUploadService
    {
        private bool _hasFailed;

        public List<Guid> AttemptedOrderGuids { get; } = [];

        public Task UploadOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            AttemptedOrderGuids.Add(orderGuid);
            if (!_hasFailed)
            {
                _hasFailed = true;
                throw new InvalidOperationException("network unavailable");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FatalOrderUploadService(Exception uploadException) : IOrderUploadService
    {
        public Task UploadOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default) =>
            Task.FromException(uploadException);
    }

    private sealed class CanceledOnceOrderUploadService(OperationCanceledException canceled) : IOrderUploadService
    {
        public bool CancelNextUpload { get; set; } = true;

        public List<Guid> AttemptedOrderGuids { get; } = [];

        public Task UploadOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            AttemptedOrderGuids.Add(orderGuid);
            if (!CancelNextUpload)
            {
                return Task.CompletedTask;
            }

            CancelNextUpload = false;
            return Task.FromException(canceled);
        }
    }

    private sealed class TimingOutOnceOrderSyncApiClient : IOrderSyncApiClient
    {
        public List<Guid> RequestedOrderGuids { get; } = [];

        public Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken = default)
        {
            RequestedOrderGuids.Add(request.OrderGuid);
            // HttpClient 超时抛出的就是这个形状：TaskCanceledException 包一层 TimeoutException，调用方令牌并未触发。
            return RequestedOrderGuids.Count == 1
                ? Task.FromException<OrderSyncResponse>(new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.",
                    new TimeoutException()))
                : Task.FromResult(new OrderSyncResponse(request.OrderGuid, true, false, "Synced"));
        }
    }

    private sealed class CallerCanceledOrderUploadService(CancellationTokenSource callerCancellation) : IOrderUploadService
    {
        public Task UploadOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            // 模拟上传进行到一半时调用方取消：令牌已触发，异常也携带同一个令牌。
            callerCancellation.Cancel();
            return Task.FromCanceled(cancellationToken);
        }
    }

    private sealed class TimingOutOnceVoucherTenderClient(string reference) : IVoucherTenderClient
    {
        private bool _hasTimedOut;

        public List<string> IssueRefundIdempotencyKeys { get; } = [];

        public Task<PaymentAuthorizationResult> RedeemAsync(
            decimal amount,
            PosSessionState session,
            string? voucherCode,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PaymentAuthorizationResult> IssueRefundAsync(
            decimal amount,
            PosSessionState session,
            string orderReference,
            string idempotencyKey,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            IssueRefundIdempotencyKeys.Add(idempotencyKey);
            if (_hasTimedOut)
            {
                return Task.FromResult(new PaymentAuthorizationResult(true, reference, AuthorizedAmount: amount));
            }

            _hasTimedOut = true;
            return Task.FromException<PaymentAuthorizationResult>(new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.",
                new TimeoutException()));
        }

        public Task<bool> ReleaseAsync(
            PosSessionState session,
            string voucherCode,
            string reservationToken,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}

internal sealed class TxnRefSequencedCardTerminalSettingsProvider(
    CardTerminalSettings first,
    CardTerminalSettings later) : ICardTerminalSettingsProvider
{
    private int _calls;

    public int GetSettingsCalls => Volatile.Read(ref _calls);

    public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = Interlocked.Increment(ref _calls);
        return Task.FromResult(call == 1 ? first : later);
    }
}

internal sealed class TxnRefSnapshotLinklyTerminalClient(
    ILinklyPaymentAttemptContextAccessor context) : ILinklyTerminalClient
{
    public int LocalPurchaseCalls { get; private set; }
    public int CloudPurchaseCalls { get; private set; }
    public int LocalRefundCalls { get; private set; }
    public int CloudRefundCalls { get; private set; }
    public LinklyConnectionMode? LastMode { get; private set; }
    public Guid? LastAttemptGuid { get; private set; }
    public string? LastTxnRef { get; private set; }
    public string? LastRefundIdempotencyKey { get; private set; }

    public Task<LinklyConnectionTestResult> TestConnectionAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default) =>
        CompleteAsync("P", amount, settings, persistedTxnRef: null, cancellationToken);

    public Task<PaymentAuthorizationResult> PurchaseWithReferenceAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string txnRef,
        CancellationToken cancellationToken = default) =>
        CompleteAsync("P", amount, settings, txnRef, cancellationToken);

    public Task<PaymentAuthorizationResult> RecoverLastTransactionAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string txnRef,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default) =>
        CompleteAsync("R", amount, settings, persistedTxnRef: null, cancellationToken);

    public Task<PaymentAuthorizationResult> RefundWithReferenceAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        string refundTxnRef,
        CancellationToken cancellationToken = default)
    {
        LastRefundIdempotencyKey = refundTxnRef;
        return CompleteAsync("R", amount, settings, refundTxnRef, cancellationToken);
    }

    public Task<PaymentAuthorizationResult> VoidAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    private async Task<PaymentAuthorizationResult> CompleteAsync(
        string txnType,
        decimal amount,
        CardTerminalSettings settings,
        string? persistedTxnRef,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode);
        var attempt = context.Current;
        var txnRef = persistedTxnRef ?? attempt?.TxnRef ??
            (txnType == "P" ? "PLOCALSNAPSHOT01" : "RLOCALSNAPSHOT01");
        LastMode = mode;
        LastAttemptGuid = attempt?.AttemptGuid;
        LastTxnRef = txnRef;

        if (mode == LinklyConnectionMode.LocalIp)
        {
            if (txnType == "P")
            {
                LocalPurchaseCalls++;
            }
            else
            {
                LocalRefundCalls++;
            }
        }
        else
        {
            if (txnType == "P")
            {
                CloudPurchaseCalls++;
            }
            else
            {
                CloudRefundCalls++;
            }

            if (attempt is not null)
            {
                await attempt.BindSessionAsync(
                    $"snapshot-{txnType.ToLowerInvariant()}-session",
                    txnRef,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
            }
        }

        return new PaymentAuthorizationResult(
            true,
            $"ANZ:{txnRef}",
            "APPROVED",
            amount,
            [new CardTransactionDto(
                "ANZ",
                txnRef,
                null,
                null,
                null,
                null,
                null,
                "00",
                "APPROVED",
                null,
                DateTimeOffset.UtcNow,
                amount,
                null)],
            "ANZ",
            settings.Environment.ToString(),
            CardTerminalSettings.FormatLinklyConnectionMode(mode),
            txnType,
            mode == LinklyConnectionMode.LocalIp ? null : $"snapshot-{txnType.ToLowerInvariant()}-session",
            txnRef,
            "00",
            "APPROVED");
    }
}
