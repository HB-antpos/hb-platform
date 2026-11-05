using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.Zeller;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

/// <summary>
/// 收款流程里的 Zeller：付款页刷卡 → 加付款行 → 订单落盘沿用草稿订单 GUID → 尝试标记已建单。
/// 用真的本地库（含 LocalSchemaService 建表）、真的 Zeller 客户端和尝试表，只有刷卡机是脚本化的网关。
/// </summary>
public sealed class ZellerPaymentWorkflowTests
{
    private const string TransactionUuid = "65c4b343-03e6-49ef-a54d-012e3f2c222c";
    private const string OriginalTransactionUuid = "e65e0a8d-0000-4000-8000-000000000001";
    private const string OriginalReference = "ZELLER:HBZ-799287a33dc24df1bca3d33eea19b35a:" + OriginalTransactionUuid;
    // 主管确认已付款、查不到 Zeller 交易时的订单引用：只有 reference，没有交易号。
    private const string UnverifiedOriginalReference = "ZELLER:HBZ-799287a33dc24df1bca3d33eea19b35a";

    [Fact]
    public async Task Approved_sale_adds_a_zeller_tender_and_completes_the_draft_order()
    {
        await using var fixture = await ZellerWorkflowFixture.CreateAsync();
        fixture.Gateway.OnPurchase = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(Approved(request.AmountCents, request.Reference)));
        var cart = SaleCart(12.5m);

        var result = await fixture.Workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            fixture.Session,
            12.5m,
            [],
            "12.50",
            cancellationToken: CancellationToken.None,
            cartSnapshot: cart.CreateSnapshot());

        Assert.True(result.Succeeded, result.StatusKey);
        var tender = Assert.IsType<PaymentTender>(result.Tender);
        Assert.Equal(12.5m, tender.Amount);
        Assert.True(ZellerPaymentReference.TryParsePayment(tender.Reference, out var parts));
        Assert.Equal(TransactionUuid, parts.TransactionUuid);
        Assert.True(CashPaymentWorkflowService.TryReadZellerAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid));
        var cardTransaction = Assert.Single(tender.CardTransactions!);
        Assert.Equal("Zeller", cardTransaction.Processor);
        Assert.Equal(TransactionUuid, cardTransaction.TxnRef);

        var attempt = (await fixture.Attempts.GetAsync(attemptGuid))!;
        Assert.Equal(ZellerAttemptStatus.Approved, attempt.Status);
        var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(attempt.OrderDraftJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.NotEqual(Guid.Empty, draft.OrderGuid);
        Assert.Equal("P", draft.TxnType);

        var completion = await fixture.Workflow.CompletePaymentAsync(cart, fixture.Session, [tender], 0m);

        Assert.Equal(draft.OrderGuid, completion.Order!.OrderGuid);
        Assert.NotNull(await fixture.Orders.GetOrderAsync(draft.OrderGuid));
        Assert.Equal(ZellerAttemptStatus.OrderCompleted, (await fixture.Attempts.GetAsync(attemptGuid))!.Status);
    }

    [Fact]
    public async Task Declined_sale_returns_a_bank_decline_without_locking_the_page()
    {
        await using var fixture = await ZellerWorkflowFixture.CreateAsync();
        fixture.Gateway.OnPurchase = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(
            Approved(request.AmountCents, request.Reference) with
            {
                Status = ZellerTransactionStatus.Declined,
                ResponseCode = "51",
                ResponseText = "INSUFFICIENT FUNDS"
            }));

        var result = await AddSaleAsync(fixture, 10m);

        Assert.False(result.Succeeded);
        Assert.Equal("payment.card.zeller.declined", result.StatusKey);
        Assert.Equal("INSUFFICIENT FUNDS", result.StatusMessage);
        Assert.True(result.IsTerminalDecline);
        Assert.Equal(CardPaymentErrorKind.CardDeclined, result.CardResult!.ErrorKind);
        Assert.False(result.CardResult.RequiresRecovery);
        Assert.Null(result.RecoveryAttemptKey);
    }

    [Fact]
    public async Task Cancelled_sale_before_the_card_is_presented_is_not_paid()
    {
        await using var fixture = await ZellerWorkflowFixture.CreateAsync();

        var result = await AddSaleAsync(fixture, 10m);

        Assert.False(result.Succeeded);
        Assert.StartsWith("payment.card.zeller.", result.StatusKey, StringComparison.Ordinal);
        Assert.False(result.CardResult!.RequiresRecovery);
        Assert.Null(result.StatusMessage);
        Assert.Null(result.RecoveryAttemptKey);
    }

    [Fact]
    public async Task Unknown_sale_locks_the_page_with_the_zeller_recovery_identity()
    {
        await using var fixture = await ZellerWorkflowFixture.CreateAsync();
        // 刷卡机已开始处理后断网：只能按 reference 核实，核实一直失败就是结果未知。
        fixture.Gateway.OnPurchase = (_, events, _) =>
        {
            events?.Report(new ZellerTerminalEvent(ZellerTerminalEventKind.AwaitingPaymentMethod, "AwaitingPaymentMethod"));
            events?.Report(new ZellerTerminalEvent(ZellerTerminalEventKind.Processing, "Processing"));
            return Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.NetworkFailure, "network"));
        };
        fixture.Gateway.OnQuery = (_, _) => ZellerQueryResult.Failed(ZellerErrorType.NetworkFailure, "offline");

        var result = await AddSaleAsync(fixture, 10m);

        Assert.False(result.Succeeded);
        Assert.Equal("payment.card.zeller.resultUnknown", result.StatusKey);
        Assert.True(result.CardResult!.RequiresRecovery);
        Assert.Null(result.StatusMessage);
        var key = Assert.IsType<CardRecoveryAttemptKey>(result.RecoveryAttemptKey);
        Assert.Equal(CardProcessorKind.Zeller, key.Processor);
        var attempt = (await fixture.Attempts.GetAsync(key.AttemptGuid))!;
        Assert.Equal(ZellerAttemptStatus.Unknown, attempt.Status);
        var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(attempt.OrderDraftJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(draft.OrderGuid, result.RecoveryOrderGuid);
    }

    [Fact]
    public async Task Approved_refund_wraps_the_original_reference_and_refunds_by_transaction_uuid()
    {
        await using var fixture = await ZellerWorkflowFixture.CreateAsync();
        fixture.Gateway.OnRefund = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(
            Approved(request.AmountCents, request.Reference) with { Type = ZellerTransactionType.Refund }));
        var cart = ReturnCart(4m);

        var result = await fixture.Workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            fixture.Session,
            -4m,
            [],
            "4",
            OriginalReference,
            CancellationToken.None,
            cart.CreateSnapshot());

        Assert.True(result.Succeeded, result.StatusKey);
        var tender = result.Tender!;
        Assert.Equal(-4m, tender.Amount);
        Assert.True(CardRefundReference.TryGetOriginalReference(tender.Reference, out var original));
        Assert.Equal(OriginalReference, original);
        var request = Assert.Single(fixture.Gateway.RefundRequests.ToArray());
        Assert.Equal(OriginalTransactionUuid, request.OriginalTransactionUuid);
        Assert.Equal(400, request.AmountCents);
    }

    [Fact]
    public async Task Refund_of_an_unverified_original_is_refused_before_the_terminal()
    {
        await using var fixture = await ZellerWorkflowFixture.CreateAsync();

        var result = await fixture.Workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            fixture.Session,
            -4m,
            [],
            "4",
            "ZELLER:HBZ-799287a33dc24df1bca3d33eea19b35a",
            CancellationToken.None,
            ReturnCart(4m).CreateSnapshot());

        Assert.False(result.Succeeded);
        Assert.Equal("payment.card.zeller.originalUnverified", result.StatusKey);
        Assert.Empty(fixture.Gateway.Calls.ToArray());
    }

    [Fact]
    public async Task Refund_of_an_unverified_original_stays_blocked_when_the_switch_is_off()
    {
        await using var fixture = await ZellerWorkflowFixture.CreateAsync(allowUnmatchedRefund: false);

        var result = await AddRefundAsync(fixture, 4m, UnverifiedOriginalReference);

        Assert.False(result.Succeeded);
        Assert.Equal("payment.card.zeller.originalUnverified", result.StatusKey);
        Assert.Empty(fixture.Gateway.Calls.ToArray());
        Assert.Empty(await fixture.Attempts.ListOpenAsync(fixture.Session.StoreCode, fixture.Session.DeviceCode));
    }

    [Fact]
    public async Task Refund_of_an_unverified_original_becomes_a_card_present_refund_when_the_switch_is_on()
    {
        await using var fixture = await ZellerWorkflowFixture.CreateAsync(allowUnmatchedRefund: true);
        fixture.Gateway.OnRefund = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(
            Approved(request.AmountCents, request.Reference) with { Type = ZellerTransactionType.Refund }));

        var result = await AddRefundAsync(fixture, 4m, UnverifiedOriginalReference);

        Assert.True(result.Succeeded, result.StatusKey);
        var request = Assert.Single(fixture.Gateway.RefundRequests.ToArray());
        Assert.True(request.IsUnmatched);
        Assert.Null(request.OriginalTransactionUuid);
        Assert.Equal(400, request.AmountCents);

        var tender = result.Tender!;
        Assert.Equal(-4m, tender.Amount);
        // 中文注释：订单里仍记着原付款引用，付款页靠它扣减这笔原付款的可退额度。
        Assert.True(CardRefundReference.TryGetOriginalReference(tender.Reference, out var original));
        Assert.Equal(UnverifiedOriginalReference, original);
        Assert.True(CashPaymentWorkflowService.TryReadZellerAttemptTenderKey(tender.IdempotencyKey, out var attemptGuid));
        var attempt = (await fixture.Attempts.GetAsync(attemptGuid))!;
        Assert.Equal(ZellerAttemptOperation.Refund, attempt.OperationKind);
        Assert.Null(attempt.OriginalTransactionUuid);
    }

    [Fact]
    public async Task A_refund_that_can_be_matched_still_refunds_the_original_transaction_when_the_switch_is_on()
    {
        await using var fixture = await ZellerWorkflowFixture.CreateAsync(allowUnmatchedRefund: true);
        fixture.Gateway.OnRefund = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(
            Approved(request.AmountCents, request.Reference) with { Type = ZellerTransactionType.Refund }));

        var result = await AddRefundAsync(fixture, 4m, OriginalReference);

        Assert.True(result.Succeeded, result.StatusKey);
        var request = Assert.Single(fixture.Gateway.RefundRequests.ToArray());
        Assert.False(request.IsUnmatched);
        Assert.Equal(OriginalTransactionUuid, request.OriginalTransactionUuid);
    }

    [Fact]
    public async Task A_declined_unmatched_refund_is_not_a_refund_and_does_not_lock_the_page()
    {
        await using var fixture = await ZellerWorkflowFixture.CreateAsync(allowUnmatchedRefund: true);
        fixture.Gateway.OnRefund = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(
            Approved(request.AmountCents, request.Reference) with
            {
                Type = ZellerTransactionType.Refund,
                Status = ZellerTransactionStatus.Declined,
                ResponseCode = "05",
                ResponseText = "DO NOT HONOUR"
            }));

        var result = await AddRefundAsync(fixture, 4m, UnverifiedOriginalReference);

        Assert.False(result.Succeeded);
        Assert.True(result.IsTerminalDecline);
        Assert.False(result.CardResult!.RequiresRecovery);
        Assert.Null(result.RecoveryAttemptKey);
    }

    [Fact]
    public async Task Sale_without_a_cart_snapshot_is_refused_before_anything_is_stored()
    {
        await using var fixture = await ZellerWorkflowFixture.CreateAsync();

        var result = await fixture.Workflow.AddTenderAsync(PaymentMethodKind.Card, fixture.Session, 10m, [], "10", cancellationToken: CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("payment.card.zeller.invalidRequest", result.StatusKey);
        Assert.Empty(fixture.Gateway.Calls.ToArray());
        Assert.Empty(await fixture.Attempts.ListOpenAsync(fixture.Session.StoreCode, fixture.Session.DeviceCode));
    }

    [Fact]
    public async Task Zeller_selected_without_the_client_reports_the_component_unavailable()
    {
        await using var fixture = await ZellerWorkflowFixture.CreateAsync(withClient: false);

        var result = await AddSaleAsync(fixture, 10m);

        Assert.False(result.Succeeded);
        Assert.Equal("payment.card.zeller.sdkUnavailable", result.StatusKey);
        Assert.Empty(fixture.Gateway.Calls.ToArray());
    }

    [Theory]
    [InlineData("10", true, 1000)]
    [InlineData("0.01", true, 1)]
    [InlineData("9999.99", true, 999999)]
    [InlineData("0", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("1.005", false, 0)]
    public void Amounts_convert_to_whole_cents_only(string amount, bool expected, int cents)
    {
        var converted = CashPaymentWorkflowService.TryConvertToZellerCents(
            decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
            out var actual);

        Assert.Equal(expected, converted);
        Assert.Equal(cents, actual);
    }

    [Fact]
    public void Tender_key_round_trips()
    {
        var attemptGuid = Guid.NewGuid();

        var key = CashPaymentWorkflowService.FormatZellerAttemptTenderKey(attemptGuid);

        Assert.StartsWith("ZELLER_ATTEMPT:", key, StringComparison.Ordinal);
        Assert.True(CashPaymentWorkflowService.TryReadZellerAttemptTenderKey(key, out var read));
        Assert.Equal(attemptGuid, read);
        Assert.False(CashPaymentWorkflowService.TryReadZellerAttemptTenderKey($"SQUARE_ATTEMPT:{attemptGuid:N}", out _));
        Assert.False(CashPaymentWorkflowService.TryReadZellerAttemptTenderKey(null, out _));
    }

    private static Task<PaymentTenderAttemptResult> AddRefundAsync(ZellerWorkflowFixture fixture, decimal amount, string originalReference) =>
        fixture.Workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            fixture.Session,
            -amount,
            [],
            amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            originalReference,
            CancellationToken.None,
            ReturnCart(amount).CreateSnapshot());

    private static Task<PaymentTenderAttemptResult> AddSaleAsync(ZellerWorkflowFixture fixture, decimal amount) =>
        fixture.Workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            fixture.Session,
            amount,
            [],
            amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken: CancellationToken.None,
            cartSnapshot: SaleCart(amount).CreateSnapshot());

    private static ZellerTransaction Approved(int amountCents, string reference) =>
        new(
            ZellerTransactionStatus.Approved,
            ZellerTransactionType.Purchase,
            amountCents,
            TransactionUuid,
            reference,
            ResponseCode: "00",
            ResponseText: "APPROVED",
            ApprovalCode: "123456",
            PanMasked: "...2902",
            Scheme: "VISA",
            ReceiptText: "RECEIPT");

    private static PosCartService SaleCart(decimal price)
    {
        var cart = new PosCartService();
        cart.AddItem(new SellableItemDto(
            StoreCode: "S001",
            ProductCode: "SKU-ZELLER",
            ReferenceCode: null,
            DisplayName: "Zeller Tea",
            LookupCode: "930ZEL",
            ItemNumber: "SKU-ZELLER",
            Barcode: "930ZEL",
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow));
        return cart;
    }

    private static PosCartService ReturnCart(decimal amount)
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-ZELLER-RETURN",
            null,
            "Returned Zeller Tea",
            "930ZER",
            "ITEM-ZELLER-RETURN",
            null,
            1m,
            amount,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-ZELLER",
            Guid.NewGuid(),
            Guid.NewGuid()));
        return cart;
    }

    private sealed class ZellerWorkflowFixture : IAsyncDisposable
    {
        private readonly string _databasePath;

        private ZellerWorkflowFixture(string databasePath, LocalSqliteStore store, bool withClient, bool allowUnmatchedRefund)
        {
            _databasePath = databasePath;
            Attempts = new LocalZellerPaymentAttemptRepository(store);
            Orders = new LocalOrderRepository(store);
            // 核实窗口压到几十毫秒，结果未知的用例不用等 3 分钟；迟到复核推到 1 小时后，测试期间不会触发。
            var client = new ZellerCardTerminalClient(
                Gateway,
                Attempts,
                new ZellerClientOptions
                {
                    VerificationInterval = TimeSpan.FromMilliseconds(10),
                    VerificationWindow = TimeSpan.FromMilliseconds(60),
                    LateRecheckDelay = TimeSpan.FromHours(1)
                });
            Workflow = new CashPaymentWorkflowService(
                new CashCheckoutService(),
                Orders,
                new SyncQueueRepository(store),
                cardTerminalSettingsProvider: new StaticSettingsProvider(
                    CardTerminalSettings.FromEnvironment() with
                    {
                        Processor = CardProcessorKind.Zeller,
                        ZellerAllowUnmatchedRefund = allowUnmatchedRefund
                    }),
                zellerCardTerminalClient: withClient ? client : null,
                zellerPaymentAttemptRepository: Attempts);
        }

        public ScriptedZellerGateway Gateway { get; } = new();

        public LocalZellerPaymentAttemptRepository Attempts { get; }

        public LocalOrderRepository Orders { get; }

        public CashPaymentWorkflowService Workflow { get; }

        public PosSessionState Session { get; } = new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        public static async Task<ZellerWorkflowFixture> CreateAsync(bool withClient = true, bool allowUnmatchedRefund = false)
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-zeller-workflow-{Guid.NewGuid():N}.db");
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            return new ZellerWorkflowFixture(databasePath, store, withClient, allowUnmatchedRefund);
        }

        public async ValueTask DisposeAsync() => await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(_databasePath);
    }

    private sealed class StaticSettingsProvider(CardTerminalSettings settings) : ICardTerminalSettingsProvider
    {
        public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
    }
}
