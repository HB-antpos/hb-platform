using System.Text.Json;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class SquarePaymentRecoveryServiceRegressionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly PosSessionState Session = new(
        "HB POS",
        "S001",
        "Main Store",
        "POS-01",
        "C001",
        "Alice",
        true,
        0);

    private static readonly Guid AttemptGuid =
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static readonly Guid OrderGuid =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_order_short_circuit_requires_exact_square_attempt_tender_key(
        bool hasExactTenderKey)
    {
        var attempts = new RecordingSquareAttemptRepository(CreateVerifiedAttempt());
        var orders = new RecordingLocalOrderRepository(
            CreateExistingOrder(hasExactTenderKey));
        var service = CreateService(attempts, orders);
        var cart = new PosCartService();
        cart.RestoreSnapshot(CreateActiveCartSnapshot());
        var before = cart.CreateSnapshot();

        var result = await service.RecoverLatestAsync(cart, Session);

        if (!hasExactTenderKey)
        {
            Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
            Assert.Equal(0, orders.SaveCount);
            Assert.Equal(0, attempts.MarkOrderCompletedCount);
            Assert.Equal(before.Lines, cart.CreateSnapshot().Lines);
            Assert.Equal(before.SharedHeldOrderClaimId, cart.CreateSnapshot().SharedHeldOrderClaimId);
            return;
        }

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(OrderGuid, result.Order?.OrderGuid);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(1, attempts.MarkOrderCompletedCount);
    }

    [Fact]
    public async Task ResolveRefundAsync_confirm_refunded_requires_real_reference_before_repository_write()
    {
        var attempt = CreateVerifiedAttempt() with
        {
            OperationKind = "Refund",
            Status = LocalSquarePaymentAttemptStatus.Recovering,
            PaymentId = null,
            PaymentStatus = null,
            RecoveryPhase = CardRecoveryPhases.None,
            RecoveryTargetStatus = null,
            ResponseCode = null
        };
        var attempts = new RecordingSquareAttemptRepository(attempt);
        var service = CreateService(
            attempts,
            new RecordingLocalOrderRepository(CreateExistingOrder(hasExactTenderKey: true)));

        var result = await service.ResolveRefundAsync(
            new CardRefundSupervisorResolution(
                attempt.AttemptGuid,
                CardProcessorKind.Square,
                CardRefundSupervisorDecision.ConfirmRefunded,
                Reason: "Supervisor note only",
                Evidence: null,
                RefundReference: "   "),
            new PosCartService(),
            Session);

        Assert.False(result.Succeeded);
        Assert.Equal(0, attempts.ResolveRefundCount);
        var unchanged = Assert.IsType<LocalSquarePaymentAttempt>(
            await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(attempt, unchanged);
        Assert.Equal(attempt.Status, unchanged.Status);
        Assert.Equal(attempt.UpdatedAt, unchanged.UpdatedAt);
        Assert.Null(unchanged.SupervisorFinancialReference);
        Assert.Null(unchanged.ResponseCode);
    }

    [Fact]
    public async Task ResolveRefundAsync_confirm_refunded_with_reference_only_writes_journal_with_non_empty_reason()
    {
        var attempt = CreateVerifiedAttempt() with
        {
            OperationKind = "Refund",
            Status = LocalSquarePaymentAttemptStatus.Recovering,
            PaymentId = null,
            PaymentStatus = null,
            RecoveryPhase = CardRecoveryPhases.None,
            RecoveryTargetStatus = null,
            ResponseCode = null
        };
        var attempts = new RecordingSquareAttemptRepository(attempt);
        var service = CreateService(
            attempts,
            new RecordingLocalOrderRepository(CreateExistingOrder(hasExactTenderKey: true)));

        // UI 只在“继续等待”时要求备注；仅填退款号确认退款时，主管结案日志仍不能因空原因被拒绝落库。
        await service.ResolveRefundAsync(
            new CardRefundSupervisorResolution(
                attempt.AttemptGuid,
                CardProcessorKind.Square,
                CardRefundSupervisorDecision.ConfirmRefunded,
                Reason: string.Empty,
                Evidence: null,
                RefundReference: "refund-ref-1"),
            new PosCartService(),
            Session);

        Assert.Equal(1, attempts.ResolveRefundCount);
        var journal = Assert.IsType<LocalFinancialSupervisorResolution>(attempts.LastRefundJournal);
        Assert.False(string.IsNullOrWhiteSpace(journal.Reason));
        Assert.Equal("refund-ref-1", journal.FinancialReference);
    }

    [Fact]
    public async Task ResolveRefundAsync_missing_square_refund_reference_uses_localized_message()
    {
        var attempt = CreateVerifiedAttempt() with
        {
            OperationKind = "Refund",
            Status = LocalSquarePaymentAttemptStatus.Recovering,
            PaymentId = null,
            PaymentStatus = null,
            RecoveryPhase = CardRecoveryPhases.None,
            RecoveryTargetStatus = null,
            ResponseCode = null
        };
        var attempts = new RecordingSquareAttemptRepository(attempt);
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        var service = new SquarePaymentRecoveryService(
            attempts,
            new RecordingSquareSettingsProvider(),
            new RecordingSquareTerminalPaymentClient(),
            new CashCheckoutService(),
            new RecordingLocalOrderRepository(CreateExistingOrder(hasExactTenderKey: true)),
            localization);

        var result = await service.ResolveRefundAsync(
            new CardRefundSupervisorResolution(
                attempt.AttemptGuid,
                CardProcessorKind.Square,
                CardRefundSupervisorDecision.ConfirmRefunded,
                Reason: string.Empty,
                Evidence: null,
                RefundReference: null),
            new PosCartService(),
            Session);

        Assert.False(result.Succeeded);
        Assert.Equal("主管确认 Square 已退款时必须提供真实退款参考号。", result.Message);
        Assert.Equal(0, attempts.ResolveRefundCount);
    }

    [Fact]
    public async Task ListOpenAsync_maps_square_payment_status_to_queue_item()
    {
        var attempt = CreateVerifiedAttempt();
        var attempts = new RecordingSquareAttemptRepository(attempt);
        var service = CreateService(
            attempts,
            new RecordingLocalOrderRepository(CreateExistingOrder(hasExactTenderKey: true)));

        var item = Assert.Single(await service.ListOpenAsync(Session));

        Assert.Equal(attempt.PaymentStatus, item.PaymentStatus);
    }

    private static SquarePaymentRecoveryService CreateService(
        RecordingSquareAttemptRepository attempts,
        RecordingLocalOrderRepository orders) =>
        new(
            attempts,
            new RecordingSquareSettingsProvider(),
            new RecordingSquareTerminalPaymentClient(),
            new CashCheckoutService(),
            orders);

    private static LocalSquarePaymentAttempt CreateVerifiedAttempt()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-23T09:00:00+10:00");
        var draft = new CardPaymentOrderDraft(
            OrderGuid,
            Session,
            new PosCartSnapshot(
            [
                new PosCartLineSnapshot(
                    "S001",
                    "SKU-10",
                    null,
                    "Test Item",
                    "930010",
                    "ITEM-10",
                    null,
                    1m,
                    10m,
                    0m,
                    null,
                    Hbpos.Contracts.Catalog.PriceSourceKind.StoreRetailPrice,
                    "Store price")
            ]),
            [],
            10m,
            10m,
            "P",
            null,
            timestamp);

        return new LocalSquarePaymentAttempt(
            AttemptGuid,
            "CHECKOUT-001",
            "square-attempt-001",
            "SQ-DEVICE",
            "SQ-LOCATION",
            "Sandbox",
            10m,
            1000,
            "AUD",
            LocalSquarePaymentAttemptStatus.PaymentVerified,
            "COMPLETED",
            null,
            JsonSerializer.Serialize(draft, JsonOptions),
            "S001",
            "POS-01",
            "C001",
            "SQ-PAYMENT-001",
            "COMPLETED",
            null,
            "Provider verified during recovery.",
            timestamp.AddMinutes(-1),
            timestamp,
            timestamp,
            null,
            null,
            "Sale",
            OrderGuid,
            null,
            null,
            CardRecoveryPhases.FinalizePending,
            LocalSquarePaymentAttemptStatus.OrderCompleted,
            null);
    }

    private static LocalOrder CreateExistingOrder(bool hasExactTenderKey)
    {
        var payment = new LocalPayment(
            Guid.NewGuid(),
            PaymentMethodKind.Card,
            10m,
            "SQ:SQ-PAYMENT-001",
            IdempotencyKey: hasExactTenderKey
                ? $"SQUARE_ATTEMPT:{AttemptGuid:N}"
                : "SQUARE_ATTEMPT:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        return new LocalOrder(
            OrderGuid,
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.Parse("2026-08-23T09:00:00+10:00"),
            10m,
            0m,
            10m,
            [],
            [payment]);
    }

    private static PosCartSnapshot CreateActiveCartSnapshot() =>
        new(
        [
            new PosCartLineSnapshot(
                "S001",
                "SKU-ACTIVE",
                null,
                "Active Item",
                "930000",
                "ITEM-ACTIVE",
                null,
                1m,
                2m,
                0m,
                null,
                Hbpos.Contracts.Catalog.PriceSourceKind.StoreRetailPrice,
                "Store price")
        ]);

    private sealed class RecordingSquareAttemptRepository(LocalSquarePaymentAttempt attempt)
        : ILocalSquarePaymentAttemptRepository
    {
        private LocalSquarePaymentAttempt _attempt = attempt;

        public int MarkOrderCompletedCount { get; private set; }

        public int ResolveRefundCount { get; private set; }

        public LocalFinancialSupervisorResolution? LastRefundJournal { get; private set; }

        public Task CreateAsync(
            LocalSquarePaymentAttempt attempt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> TryRecordRefundResponseAsync(
            Guid attemptGuid,
            string submissionToken,
            string refundId,
            string refundStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task MarkCheckoutCreatedAsync(
            Guid attemptGuid,
            string checkoutId,
            string? checkoutStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkRecoveringAsync(
            Guid attemptGuid,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateCheckoutStatusAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus status,
            string? checkoutStatus,
            string? cancelReason,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkPaymentVerifiedAsync(
            Guid attemptGuid,
            string paymentId,
            string paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkFailedAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus status,
            string? checkoutStatus,
            string? paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset resolvedAt,
            CancellationToken cancellationToken = default,
            string? cancelReason = null) => Task.CompletedTask;

        public Task MarkOrderCompletedAsync(
            Guid attemptGuid,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            MarkOrderCompletedCount++;
            _attempt = _attempt with
            {
                Status = LocalSquarePaymentAttemptStatus.OrderCompleted,
                RecoveryPhase = CardRecoveryPhases.None,
                RecoveryTargetStatus = null,
                OrderCompletedAt = completedAt,
                UpdatedAt = completedAt
            };
            return Task.CompletedTask;
        }

        public Task<bool> ResolveRefundWithJournalAsync(
            CardRefundAttemptResolution resolution,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            LocalFinancialSupervisorResolution journal,
            CancellationToken cancellationToken = default)
        {
            ResolveRefundCount++;
            LastRefundJournal = journal;
            return Task.FromResult(false);
        }

        public Task<LocalSquarePaymentAttempt?> GetLatestOpenAttemptAsync(
            string storeCode,
            string deviceCode,
            string? cashierId,
            string environment,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalSquarePaymentAttempt?>(_attempt);

        public Task<IReadOnlyList<LocalSquarePaymentAttempt>> GetOpenRefundAttemptsAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalSquarePaymentAttempt>>([]);

        public Task<IReadOnlyList<LocalSquarePaymentAttempt>> GetOpenAttemptsAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalSquarePaymentAttempt>>([_attempt]);

        public Task<LocalSquarePaymentAttempt?> GetAttemptAsync(
            Guid attemptGuid,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalSquarePaymentAttempt?>(_attempt);
    }

    private sealed class RecordingLocalOrderRepository(LocalOrder existingOrder)
        : ILocalOrderRepository
    {
        public int SaveCount { get; private set; }

        public Task SavePendingOrderAsync(
            LocalOrder order,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            int take = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);

        public Task<LocalOrder?> GetOrderAsync(
            Guid orderGuid,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalOrder?>(orderGuid == existingOrder.OrderGuid ? existingOrder : null);
    }

    private sealed class RecordingSquareSettingsProvider : ICardTerminalSettingsProvider
    {
        public Task<CardTerminalSettings> GetSettingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CardTerminalSettings(
                CardProcessorKind.Square,
                CardTerminalEnvironment.Sandbox,
                "127.0.0.1",
                2011,
                "SQ-DEVICE",
                "SQ-LOCATION",
                "token",
                "https://connect.squareupsandbox.com",
                TimeSpan.FromSeconds(30),
                LinklyConnectionMode.LocalIp));
    }

    private sealed class RecordingSquareTerminalPaymentClient : ISquareTerminalPaymentClient
    {
        public Task<SquareCheckoutStatusResult> GetCheckoutAsync(
            CardTerminalSettings settings,
            string checkoutId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The verified attempt must not query checkout.");

        public Task<SquarePaymentStatusResult> GetPaymentAsync(
            CardTerminalSettings settings,
            string paymentId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The verified attempt must not query payment.");

        public Task<SquareRefundStatusResult> GetRefundAsync(
            CardTerminalSettings settings,
            string refundId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The verified sale must not query refund.");
    }
}
