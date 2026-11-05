using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.Zeller;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>
/// 卡交易恢复里的 Zeller 提供方：按草稿补单（不重复建单）、启动自动恢复、主管结案写日志、恢复中心队列。
/// 用真的本地库、真的收款流程造出各种状态的尝试，只有刷卡机是脚本化的网关。
/// </summary>
public sealed class ZellerCardRecoveryProviderTests
{
    private const string TransactionUuid = "65c4b343-03e6-49ef-a54d-012e3f2c222c";

    [Fact]
    public async Task Unknown_sale_found_approved_on_recheck_is_completed_from_its_draft()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var attempt = await fixture.CreateUnknownSaleAsync(12.5m);
        fixture.Gateway.OnQuery = (reference, _) => ZellerQueryResult.Found(Approved(1250, reference));

        var result = await fixture.Provider.RecoverAttemptAsync(attempt.AttemptGuid, new PosCartService(), fixture.Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        var order = Assert.IsType<LocalOrder>(result.Order);
        Assert.Equal(DraftOf(attempt).OrderGuid, order.OrderGuid);
        var payment = Assert.Single(order.Payments, payment => payment.Method == PaymentMethodKind.Card);
        Assert.Equal(CashPaymentWorkflowService.FormatZellerAttemptTenderKey(attempt.AttemptGuid), payment.IdempotencyKey);
        Assert.True(ZellerPaymentReference.TryParsePayment(payment.Reference, out var parts));
        Assert.Equal(TransactionUuid, parts.TransactionUuid);
        Assert.NotNull(await fixture.Orders.GetOrderAsync(order.OrderGuid));
        Assert.Equal(ZellerAttemptStatus.OrderCompleted, (await fixture.Attempts.GetAsync(attempt.AttemptGuid))!.Status);
    }

    [Fact]
    public async Task Recovering_twice_never_saves_the_order_twice()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var attempt = await fixture.CreateApprovedSaleWithoutOrderAsync(10m);

        var first = await fixture.Provider.RecoverAttemptAsync(attempt.AttemptGuid, new PosCartService(), fixture.Session);
        var second = await fixture.Provider.RecoverAttemptAsync(attempt.AttemptGuid, new PosCartService(), fixture.Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, first.Outcome);
        Assert.Equal(CardPaymentRecoveryOutcome.None, second.Outcome);
        Assert.Single(await fixture.Orders.GetRecentOrdersAsync());
    }

    [Fact]
    public async Task Order_already_saved_by_the_payment_page_is_only_marked_completed()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var attempt = await fixture.CreateApprovedSaleWithoutOrderAsync(10m);
        // 付款页已经落单，但标记已建单那一步没做成（这里用不带 Zeller 客户端的流程落单来模拟）。
        await fixture.SaveOrderWithoutMarkingAsync(attempt);
        Assert.Equal(ZellerAttemptStatus.Approved, (await fixture.Attempts.GetAsync(attempt.AttemptGuid))!.Status);

        var result = await fixture.Provider.RecoverAttemptAsync(attempt.AttemptGuid, new PosCartService(), fixture.Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(DraftOf(attempt).OrderGuid, result.Order!.OrderGuid);
        Assert.Single(await fixture.Orders.GetRecentOrdersAsync());
        Assert.Equal(ZellerAttemptStatus.OrderCompleted, (await fixture.Attempts.GetAsync(attempt.AttemptGuid))!.Status);
    }

    [Fact]
    public async Task Auto_recovery_completes_idle_approved_attempts_only()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var idle = await fixture.CreateApprovedSaleWithoutOrderAsync(10m);
        fixture.Time.SetUtcNow(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1));

        var summary = await fixture.Provider.AutoRecoverDeterministicAsync(fixture.Session);

        Assert.Equal(1, summary.RecoveredOrders);
        Assert.Equal(ZellerAttemptStatus.OrderCompleted, (await fixture.Attempts.GetAsync(idle.AttemptGuid))!.Status);
    }

    [Fact]
    public async Task Auto_recovery_leaves_a_just_approved_attempt_to_the_payment_page()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var fresh = await fixture.CreateApprovedSaleWithoutOrderAsync(10m);
        fixture.Time.SetUtcNow(DateTimeOffset.UtcNow);

        var summary = await fixture.Provider.AutoRecoverDeterministicAsync(fixture.Session);

        Assert.Equal(0, summary.RecoveredOrders);
        Assert.Equal(ZellerAttemptStatus.Approved, (await fixture.Attempts.GetAsync(fresh.AttemptGuid))!.Status);
    }

    [Fact]
    public async Task Supervisor_confirm_paid_completes_the_order_with_an_unverified_reference_and_journals_it()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var attempt = await fixture.CreateUnknownSaleAsync(10m);

        var result = await fixture.Provider.ResolveAttemptAsync(
            attempt.AttemptGuid,
            CardRecoverySupervisorDecision.ConfirmProcessed,
            "terminal showed APPROVED",
            "photo of terminal",
            "264753970917",
            new PosCartService(),
            fixture.Session);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.ResolutionPersisted);
        Assert.False(result.LockRetained);
        var order = result.RecoveryResult!.Order!;
        var payment = Assert.Single(order.Payments, payment => payment.Method == PaymentMethodKind.Card);
        Assert.True(ZellerPaymentReference.IsUnverified(payment.Reference));
        Assert.Equal("Zeller Supervisor", Assert.Single(payment.CardTransactions!).Processor);
        Assert.Equal(1, await fixture.CountJournalAsync("ConfirmProcessed"));
        Assert.Equal(ZellerAttemptStatus.OrderCompleted, (await fixture.Attempts.GetAsync(attempt.AttemptGuid))!.Status);
    }

    [Fact]
    public async Task Supervisor_confirm_not_paid_closes_the_attempt_and_allows_retry()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var attempt = await fixture.CreateUnknownSaleAsync(10m);

        var result = await fixture.Provider.ResolveAttemptAsync(
            attempt.AttemptGuid,
            CardRecoverySupervisorDecision.ConfirmNotProcessed,
            "terminal showed DECLINED",
            null,
            null,
            new PosCartService(),
            fixture.Session);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.RetryAllowed);
        Assert.False(result.LockRetained);
        Assert.Null(result.RecoveryResult);
        Assert.Equal(ZellerAttemptStatus.Canceled, (await fixture.Attempts.GetAsync(attempt.AttemptGuid))!.Status);
        Assert.Equal(1, await fixture.CountJournalAsync("ConfirmNotProcessed"));
        Assert.Empty(await fixture.Orders.GetRecentOrdersAsync());
    }

    [Fact]
    public async Task Supervisor_continue_waiting_keeps_the_lock_and_the_attempt_open()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var attempt = await fixture.CreateUnknownSaleAsync(10m);

        var result = await fixture.Provider.ResolveAttemptAsync(
            attempt.AttemptGuid,
            CardRecoverySupervisorDecision.ContinueWaiting,
            "ask Zeller support",
            null,
            null,
            new PosCartService(),
            fixture.Session);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.LockRetained);
        Assert.Equal(ZellerAttemptStatus.Unknown, (await fixture.Attempts.GetAsync(attempt.AttemptGuid))!.Status);
        Assert.Equal(1, await fixture.CountJournalAsync("ContinueWaiting"));
    }

    [Fact]
    public async Task Queue_lists_open_and_awaiting_attempts_and_history_adds_closed_ones()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var unknown = await fixture.CreateUnknownSaleAsync(10m);
        var approved = await fixture.CreateApprovedSaleWithoutOrderAsync(20m);
        var declined = await fixture.CreateDeclinedSaleAsync(30m);

        var open = await fixture.Provider.ListOpenAsync(fixture.Session);
        var history = await fixture.Provider.ListHistoryAsync(fixture.Session);

        Assert.Equal(
            new[] { unknown.AttemptGuid, approved.AttemptGuid }.Order(),
            open.Select(item => item.AttemptGuid).Order());
        Assert.All(open, item =>
        {
            Assert.Equal(CardProcessorKind.Zeller, item.Processor);
            Assert.Equal("Sale", item.OperationKind);
            Assert.True(item.IsOpen);
            Assert.False(string.IsNullOrWhiteSpace(item.OrderDraftJson));
        });
        Assert.Equal(20m, Assert.Single(open, item => item.AttemptGuid == approved.AttemptGuid).Amount);
        var closed = Assert.Single(history, item => item.AttemptGuid == declined.AttemptGuid);
        Assert.False(closed.IsOpen);
        Assert.Equal("Declined", closed.Status);
    }

    [Fact]
    public async Task Attempts_from_another_till_are_not_recovered_here()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var attempt = await fixture.CreateApprovedSaleWithoutOrderAsync(10m);
        var otherTill = fixture.Session with { DeviceCode = "POS-02" };

        var result = await fixture.Provider.RecoverAttemptAsync(attempt.AttemptGuid, new PosCartService(), otherTill);

        Assert.Equal(CardPaymentRecoveryOutcome.None, result.Outcome);
        Assert.Equal(ZellerAttemptStatus.Approved, (await fixture.Attempts.GetAsync(attempt.AttemptGuid))!.Status);
    }

    [Fact]
    public async Task Journal_is_not_written_when_the_attempt_update_loses_the_race()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var attempt = await fixture.CreateUnknownSaleAsync(10m);
        var stale = (await fixture.Attempts.GetAsync(attempt.AttemptGuid))!;
        await fixture.Attempts.TryUpdateAsync(stale, stale with { VerificationRounds = stale.VerificationRounds + 1 });
        var journal = new LocalFinancialSupervisorResolution(
            Guid.NewGuid(), LocalFinancialSupervisorResolutionTarget.ActiveSession, "Zeller", "Production", "S001", "POS-01",
            stale.AttemptGuid, null, null, stale.Reference, "ContinueWaiting", "C001", null, "Alice", "race", null, null, null,
            DateTimeOffset.UtcNow, Guid.NewGuid(), "{}");

        var updated = await fixture.Attempts.TryUpdateWithJournalAsync(stale, stale with { VerificationRounds = 99 }, journal);

        Assert.Null(updated);
        Assert.Equal(0, await fixture.CountJournalAsync("ContinueWaiting"));
    }

    [Fact]
    public void Draft_validation_rejects_a_draft_from_another_amount_or_till()
    {
        var attempt = new LocalZellerPaymentAttempt(
            Guid.NewGuid(), ZellerAttemptOperation.Sale, "HBZ-799287a33dc24df1bca3d33eea19b35a", 1000, ZellerAttemptStatus.Approved,
            "{}", "S001", "POS-01", "C001", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var cart = SaleCart(10m);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var draft = new CardPaymentOrderDraft(Guid.NewGuid(), session, cart.CreateSnapshot(), [], 10m, 10m, "P", null, DateTimeOffset.UtcNow);

        Assert.Null(ZellerCardRecoveryProvider.ValidateDraft(attempt, draft));
        Assert.Equal("amount", ZellerCardRecoveryProvider.ValidateDraft(attempt with { AmountCents = 999 }, draft));
        Assert.Equal("identity", ZellerCardRecoveryProvider.ValidateDraft(attempt with { DeviceCode = "POS-02" }, draft));
        Assert.Equal("identity", ZellerCardRecoveryProvider.ValidateDraft(attempt, draft with { TxnType = "R" }));
        Assert.Equal("incomplete", ZellerCardRecoveryProvider.ValidateDraft(attempt, draft with { OrderGuid = Guid.Empty }));
    }

    private static CardPaymentOrderDraft DraftOf(LocalZellerPaymentAttempt attempt) =>
        JsonSerializer.Deserialize<CardPaymentOrderDraft>(attempt.OrderDraftJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

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
            Scheme: "VISA");

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

    private sealed class RecoveryFixture : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly LocalSqliteStore _store;
        private readonly CardTerminalSettings _settings = CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Zeller };

        private RecoveryFixture(string databasePath, LocalSqliteStore store)
        {
            _databasePath = databasePath;
            _store = store;
            Attempts = new LocalZellerPaymentAttemptRepository(store);
            Orders = new LocalOrderRepository(store);
            Client = new ZellerCardTerminalClient(
                Gateway,
                Attempts,
                new ZellerClientOptions
                {
                    VerificationInterval = TimeSpan.FromMilliseconds(10),
                    VerificationWindow = TimeSpan.FromMilliseconds(60),
                    LateRecheckDelay = TimeSpan.FromHours(1)
                });
            Recovery = new ZellerPaymentRecoveryService(Gateway, Attempts, Client);
            Provider = new ZellerCardRecoveryProvider(
                Recovery,
                Client,
                Attempts,
                new CashCheckoutService(),
                Orders,
                new StaticSettingsProvider(_settings),
                timeProvider: Time);
            Workflow = CreateWorkflow(Client);
        }

        public ScriptedZellerGateway Gateway { get; } = new();

        public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

        public LocalZellerPaymentAttemptRepository Attempts { get; }

        public LocalOrderRepository Orders { get; }

        public ZellerCardTerminalClient Client { get; }

        public ZellerPaymentRecoveryService Recovery { get; }

        public ZellerCardRecoveryProvider Provider { get; }

        public CashPaymentWorkflowService Workflow { get; }

        public PosSessionState Session { get; } = new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        public static async Task<RecoveryFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-zeller-recovery-{Guid.NewGuid():N}.db");
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            return new RecoveryFixture(databasePath, store);
        }

        /// <summary>刷卡机开始处理后断网、核实一直失败：结果未知的销售。</summary>
        public async Task<LocalZellerPaymentAttempt> CreateUnknownSaleAsync(decimal amount)
        {
            Gateway.OnPurchase = (_, events, _) =>
            {
                events?.Report(new ZellerTerminalEvent(ZellerTerminalEventKind.AwaitingPaymentMethod, "AwaitingPaymentMethod"));
                events?.Report(new ZellerTerminalEvent(ZellerTerminalEventKind.Processing, "Processing"));
                return Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.NetworkFailure, "network"));
            };
            Gateway.OnQuery = (_, _) => ZellerQueryResult.Failed(ZellerErrorType.NetworkFailure, "offline");
            var result = await AddSaleAsync(amount);
            Assert.Equal("payment.card.zeller.resultUnknown", result.StatusKey);
            return (await Attempts.GetAsync(result.RecoveryAttemptKey!.Value.AttemptGuid))!;
        }

        /// <summary>批准了、但订单没有落盘（付款页在落单前退出）。</summary>
        public async Task<LocalZellerPaymentAttempt> CreateApprovedSaleWithoutOrderAsync(decimal amount)
        {
            Gateway.OnPurchase = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(Approved(request.AmountCents, request.Reference)));
            var result = await AddSaleAsync(amount);
            Assert.True(result.Succeeded, result.StatusKey);
            Assert.True(CashPaymentWorkflowService.TryReadZellerAttemptTenderKey(result.Tender!.IdempotencyKey, out var attemptGuid));
            LastApprovedTender = result.Tender;
            LastApprovedCart = _lastCart;
            return (await Attempts.GetAsync(attemptGuid))!;
        }

        public async Task<LocalZellerPaymentAttempt> CreateDeclinedSaleAsync(decimal amount)
        {
            Gateway.OnPurchase = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(
                Approved(request.AmountCents, request.Reference) with { Status = ZellerTransactionStatus.Declined, ResponseCode = "51" }));
            await AddSaleAsync(amount);
            return (await Attempts.ListRecentAsync(Session.StoreCode, Session.DeviceCode, 1)).Single();
        }

        public PaymentTender? LastApprovedTender { get; private set; }

        public PosCartService? LastApprovedCart { get; private set; }

        /// <summary>用不带 Zeller 客户端的收款流程落单：订单按草稿 GUID 保存，但尝试不会被标记已建单。</summary>
        public async Task SaveOrderWithoutMarkingAsync(LocalZellerPaymentAttempt attempt)
        {
            var workflow = CreateWorkflow(client: null);
            await workflow.CompletePaymentAsync(LastApprovedCart!, Session, [LastApprovedTender!], 0m);
        }

        public async Task<int> CountJournalAsync(string decision)
        {
            await using var connection = await _store.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM LocalFinancialSupervisorResolutions WHERE Processor = 'Zeller' AND Decision = $Decision;";
            command.Parameters.AddWithValue("$Decision", decision);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync() => await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(_databasePath);

        private PosCartService? _lastCart;

        private Task<PaymentTenderAttemptResult> AddSaleAsync(decimal amount)
        {
            _lastCart = SaleCart(amount);
            return Workflow.AddTenderAsync(
                PaymentMethodKind.Card,
                Session,
                amount,
                [],
                amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken: CancellationToken.None,
                cartSnapshot: _lastCart.CreateSnapshot());
        }

        private CashPaymentWorkflowService CreateWorkflow(ZellerCardTerminalClient? client) =>
            new(
                new CashCheckoutService(),
                Orders,
                new SyncQueueRepository(_store),
                cardTerminalSettingsProvider: new StaticSettingsProvider(_settings),
                zellerCardTerminalClient: client,
                zellerPaymentAttemptRepository: Attempts);
    }

    private sealed class StaticSettingsProvider(CardTerminalSettings settings) : ICardTerminalSettingsProvider
    {
        public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
    }
}
