using System.ComponentModel;
using System.Globalization;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Microsoft.Extensions.Time.Testing;
using PaymentMethodKind = Hbpos.Contracts.Orders.PaymentMethodKind;

namespace Hbpos.Client.Tests;

/// <summary>
/// 直接覆盖 ScreenNavigator 中不需要创建子页面的导航逻辑：回到 POS、空购物车不进付款页、
/// 启动页解析、新交易重置、挂单/取单状态提示，以及缺少可选服务时的降级提示。
/// </summary>
/// <remarks>
/// MainChildViewModelFactory 需要约 20 个必填服务，且只在创建付款/设置/历史等子页面时使用；
/// 这些路径已由 MainViewModelScannerTests 通过真实 MainViewModel 覆盖。这里传入 null 工厂，
/// 若将来被测路径开始创建子页面，会以 NullReferenceException 明确暴露，而不是静默通过。
/// </remarks>
public sealed class ScreenNavigatorTests : IDisposable
{
    private static readonly PosSessionState Session = new("HB POS", "S01", "Main Branch", "POS-01", "C001", "Alice", true, 0);

    private readonly PosCartService _cart = new();
    private readonly KeyEchoLocalization _localization = new();
    private readonly List<object?> _screens = [];
    private readonly List<string> _statusMessages = [];
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public void Show_pos_sets_pos_terminal_as_current_screen()
    {
        var navigator = CreateNavigator();
        var pos = AttachPosTerminal(navigator);

        navigator.ShowPos();

        Assert.Same(pos, navigator.CurrentScreen);
        Assert.Equal(new object?[] { pos }, _screens);
        Assert.True(navigator.IsPosTerminalScreenActive);
        Assert.False(navigator.IsFallbackScreenActive);
    }

    [Fact]
    public void Show_pos_without_pos_terminal_is_a_no_op()
    {
        var navigator = CreateNavigator();

        navigator.ShowPos();

        Assert.Null(navigator.CurrentScreen);
        Assert.Empty(_screens);
    }

    [Fact]
    public void Set_current_screen_notifies_host_and_marks_unknown_screen_as_fallback()
    {
        var navigator = CreateNavigator();
        AttachPosTerminal(navigator);
        var other = new object();

        navigator.SetCurrentScreen(other);

        Assert.Same(other, navigator.CurrentScreen);
        Assert.Equal(new[] { other }, _screens);
        Assert.False(navigator.IsPosTerminalScreenActive);
        Assert.False(navigator.IsCashPaymentScreenActive);
        Assert.False(navigator.IsSpecialProductsScreenActive);
        Assert.True(navigator.IsFallbackScreenActive);
    }

    [Fact]
    public void Show_cash_payment_with_empty_cart_returns_to_pos_without_creating_payment_screen()
    {
        var navigator = CreateNavigator();
        var pos = AttachPosTerminal(navigator);

        Assert.False(navigator.ShowCashPaymentCommand.CanExecute(null));
        navigator.ShowCashPayment();

        Assert.Same(pos, navigator.CurrentScreen);
        Assert.Null(navigator.CashPayment);
        Assert.Null(navigator.CachedCashPaymentScreen);
    }

    [Fact]
    public void Cash_payment_command_availability_follows_cart_contents()
    {
        var navigator = CreateNavigator();

        Assert.False(navigator.ShowCashPaymentCommand.CanExecute(null));

        _cart.AddItem(CreateItem("SKU-1", 3.50m));
        Assert.True(navigator.ShowCashPaymentCommand.CanExecute(null));

        _cart.Clear();
        Assert.False(navigator.ShowCashPaymentCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pos")]
    [InlineData("  POS  ")]
    [InlineData("unknown-screen")]
    [InlineData("cash")]
    [InlineData("PAYMENT")]
    public void Startup_navigation_falls_back_to_pos_for_default_unknown_and_empty_cart_payment(string? initialScreen)
    {
        var navigator = CreateNavigator();
        var pos = AttachPosTerminal(navigator);

        navigator.NavigateFromStartup(initialScreen);

        Assert.Same(pos, navigator.CurrentScreen);
        Assert.Null(navigator.CashPayment);
    }

    [Fact]
    public void Reset_for_new_transaction_clears_cart_refreshes_payment_command_and_returns_to_pos()
    {
        var navigator = CreateNavigator();
        var pos = AttachPosTerminal(navigator);
        _cart.AddItem(CreateItem("SKU-1", 2m));
        navigator.SetCurrentScreen(new object());
        var canExecuteChanges = 0;
        navigator.ShowCashPaymentCommand.CanExecuteChanged += (_, _) => canExecuteChanges++;

        navigator.ResetForNewTransaction();

        Assert.True(_cart.IsEmpty);
        Assert.True(canExecuteChanges >= 1);
        Assert.False(navigator.ShowCashPaymentCommand.CanExecute(null));
        Assert.Same(pos, navigator.CurrentScreen);
    }

    [Fact]
    public async Task Recalled_suspended_order_returns_to_pos_with_status()
    {
        var navigator = CreateNavigator();
        var pos = AttachPosTerminal(navigator);
        navigator.SetCurrentScreen(new object());

        await navigator.OnSuspendedOrderRecalledAsync();

        Assert.Same(pos, navigator.CurrentScreen);
        Assert.Equal(new[] { "main.suspendedRecalled" }, _statusMessages);
    }

    [Fact]
    public async Task Pos_visibility_drives_held_order_badge_refresh_and_recall_forces_remote_refresh()
    {
        var navigator = CreateNavigator();
        var counter = new FakeHeldOrderTodayCountService { Count = 2 };
        var clock = new FakeTimeProvider();
        var pos = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            _cart,
            Session,
            onOpenPayment: null,
            localization: _localization,
            timeProvider: clock,
            heldOrderTodayCountService: counter);
        _disposables.Add(pos);
        navigator.PosTerminal = pos;
        navigator.SetCachedPosTerminalScreen(pos);

        // 进入首页：立即做一次普通刷新（远端走缓存）。
        navigator.ShowPos();
        await pos.LastHeldOrderCountRefreshTask!;
        Assert.Equal(2, pos.HeldOrderTodayCount);
        Assert.False(Assert.Single(counter.Calls).Force);

        // 去别的页面（取单页）再取单成功回来：必须带一次强制刷新，角标立即反映少了一张。
        navigator.SetCurrentScreen(new object());
        counter.Count = 1;
        await navigator.OnSuspendedOrderRecalledAsync();
        Assert.Contains(counter.Calls, call => call.Force);
        Assert.Equal(1, pos.HeldOrderTodayCount);

        // 离开首页后不再轮询。
        navigator.SetCurrentScreen(new object());
        var callsWhileHidden = counter.CallCount;
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(callsWhileHidden, counter.CallCount);
    }

    [Fact]
    public async Task Suspend_without_suspended_order_service_reports_unavailable()
    {
        var navigator = CreateNavigator(suspendedOrderService: null);

        var suspendedGuid = await navigator.SuspendCurrentOrderAsync();

        Assert.Null(suspendedGuid);
        Assert.Equal(new[] { "main.suspendedUnavailable" }, _statusMessages);
    }

    [Fact]
    public async Task Suspend_success_reports_the_last_eight_upper_case_characters_of_order_id()
    {
        var orderGuid = Guid.Parse("abcdef12-3456-7890-abcd-ef1234567890");
        var service = new FakeSuspendedOrderService { Result = orderGuid };
        _localization.Overrides["main.suspendedSaved"] = "Saved #{0}";
        var navigator = CreateNavigator(service);
        navigator.Session = Session with { CashierId = "C009" };

        var suspendedGuid = await navigator.SuspendCurrentOrderAsync();

        // 挂单 Guid 交给收银页写入 ORDER_HOLD 审计。
        Assert.Equal(orderGuid, suspendedGuid);
        Assert.Equal(new[] { "Saved #34567890" }, _statusMessages);
        Assert.Equal("C009", Assert.Single(service.SuspendedSessions).CashierId);
        Assert.Empty(_screens);
    }

    [Fact]
    public async Task Suspend_failure_reports_exception_message_without_navigating()
    {
        var service = new FakeSuspendedOrderService { Failure = new InvalidOperationException("挂单写入失败") };
        var navigator = CreateNavigator(service);
        AttachPosTerminal(navigator);

        var suspendedGuid = await navigator.SuspendCurrentOrderAsync();

        Assert.Null(suspendedGuid);
        Assert.Equal(new[] { "挂单写入失败" }, _statusMessages);
        Assert.Empty(_screens);
    }

    [Fact]
    public async Task Card_recovery_center_without_recovery_service_reports_unavailable_and_keeps_screen()
    {
        var navigator = CreateNavigator();
        var pos = AttachPosTerminal(navigator);
        navigator.ShowPos();
        _screens.Clear();

        await navigator.ShowCardRecoveryCenterAsync();

        Assert.Equal(new[] { "cardRecovery.center.status.unavailable" }, _statusMessages);
        Assert.Empty(_screens);
        Assert.Same(pos, navigator.CurrentScreen);
        Assert.Null(navigator.CardRecoveryCenter);
    }

    [Fact]
    public async Task Settings_without_card_terminal_setup_service_reports_unavailable()
    {
        var navigator = CreateNavigator();

        await navigator.ShowSettingsAsync();

        Assert.Equal(new[] { "main.settingsUnavailable" }, _statusMessages);
        Assert.Null(navigator.Settings);
        Assert.Empty(_screens);
    }

    [Fact]
    public async Task Screens_whose_view_models_are_not_ready_are_ignored()
    {
        var navigator = CreateNavigator();

        navigator.ShowReturns();
        navigator.ShowInstallmentCenter();
        await navigator.ShowSpecialProductsAsync();

        Assert.Empty(_screens);
        Assert.Null(navigator.CurrentScreen);
        Assert.Null(navigator.InstallmentCenter);
    }

    [Fact]
    public void Installment_center_opened_from_history_returns_to_the_same_history_screen_without_reloading()
    {
        var navigator = CreateNavigator();
        var receiptQuery = new CountingReceiptQueryService();
        var history = AttachHistory(navigator, receiptQuery);
        // 历史页上的查询状态（关键词）必须在往返分期中心之后原样保留。
        history.SearchText = "keep-me";
        var center = AttachInstallmentCenter(navigator);

        navigator.ShowInstallmentCenterFromHistory();

        Assert.Same(center, navigator.CurrentScreen);
        Assert.Equal(InstallmentCenterBackTarget.History, center.BackTarget);
        // 没注入本地化服务时用源码里的英文兜底文案。
        Assert.Equal("Back to History", center.BackToPaymentText);
        var queriesBeforeBack = receiptQuery.QueryCallCount;

        center.BackToPaymentCommand.Execute(null);

        Assert.Same(history, navigator.CurrentScreen);
        Assert.Equal(new object?[] { center, history }, _screens);
        // 返回只切换当前屏幕：历史页不重新查询，来源与关键词原样保留。
        Assert.Equal(queriesBeforeBack, receiptQuery.QueryCallCount);
        Assert.Equal("keep-me", history.SearchText);
        Assert.True(history.IsLocalSourceSelected);
    }

    [Fact]
    public void Installment_center_opened_from_history_falls_back_to_pos_when_history_was_released()
    {
        var navigator = CreateNavigator();
        var pos = AttachPosTerminal(navigator);
        AttachHistory(navigator, new CountingReceiptQueryService());
        var center = AttachInstallmentCenter(navigator);
        navigator.ShowInstallmentCenterFromHistory();

        // 例如设备重新注册后清屏，历史页视图模型已被置空。
        navigator.TransactionHistory = null;
        center.BackToPaymentCommand.Execute(null);

        Assert.Same(pos, navigator.CurrentScreen);
    }

    [Fact]
    public void Installment_center_opened_from_payment_still_returns_to_payment_and_history_entry_does_not_leak()
    {
        var navigator = CreateNavigator();
        _cart.AddItem(CreateItem("SKU-1", 3.50m));
        var payment = AttachPayment(navigator);
        var history = AttachHistory(navigator, new CountingReceiptQueryService());
        var center = AttachInstallmentCenter(navigator);

        // 先从历史页进入再返回，然后换成付款页入口：去向与文案必须跟着入口重设，不能沿用上一次。
        navigator.ShowInstallmentCenterFromHistory();
        center.BackToPaymentCommand.Execute(null);
        Assert.Same(history, navigator.CurrentScreen);

        navigator.ShowInstallmentCenter();

        Assert.Same(center, navigator.CurrentScreen);
        Assert.Equal(InstallmentCenterBackTarget.Payment, center.BackTarget);
        Assert.Equal("Back to Payment", center.BackToPaymentText);

        center.BackToPaymentCommand.Execute(null);

        Assert.Same(payment, navigator.CurrentScreen);
    }

    [Fact]
    public async Task Installment_center_entry_from_history_carries_the_cart_snapshot_and_starts_loading()
    {
        var navigator = CreateNavigator();
        AttachHistory(navigator, new CountingReceiptQueryService());
        var center = AttachInstallmentCenter(navigator);
        var readyStatus = center.StatusMessage;

        // 购物车为空：快照为 null，分期中心能处理。
        navigator.ShowInstallmentCenterFromHistory();
        Assert.Null(center.CartSnapshot);

        _cart.AddItem(CreateItem("SKU-1", 3.50m));
        navigator.ShowInstallmentCenterFromHistory();

        Assert.NotNull(center.CartSnapshot);
        Assert.Equal(_cart.ActualAmount, center.CartSnapshot!.ActualAmount);
        // 进入即触发 LoadAsync：状态栏从「就绪」提示变成加载结果。
        await WaitUntilAsync(() => center.StatusMessage != readyStatus);
    }

    [Fact]
    public void Current_cart_snapshot_is_null_for_empty_cart_and_projects_lines_otherwise()
    {
        var navigator = CreateNavigator();
        Assert.Null(navigator.CreateCurrentCartSnapshot());

        _cart.AddItem(CreateItem("SKU-1", 3.50m));
        _cart.AddItem(CreateItem("SKU-2", 1.25m));
        _cart.AddItem(CreateItem("SKU-1", 3.50m));

        var snapshot = navigator.CreateCurrentCartSnapshot();

        Assert.NotNull(snapshot);
        Assert.Equal(_cart.TotalAmount, snapshot.TotalAmount);
        Assert.Equal(_cart.DiscountAmount, snapshot.DiscountAmount);
        Assert.Equal(_cart.ActualAmount, snapshot.ActualAmount);
        Assert.Equal(_cart.Lines.Count, snapshot.Lines.Count);
        foreach (var (line, projected) in _cart.Lines.Zip(snapshot.Lines))
        {
            Assert.Equal(line.ProductCode, projected.ProductCode);
            Assert.Equal(line.DisplayName, projected.DisplayName);
            Assert.Equal(line.LookupCode, projected.LookupCode);
            Assert.Equal(line.Quantity, projected.Quantity);
            Assert.Equal(line.UnitPrice, projected.UnitPrice);
            Assert.Equal(line.ActualAmount, projected.ActualAmount);
        }
    }

    [Fact]
    public void Refresh_cart_related_state_without_child_screens_only_refreshes_payment_command()
    {
        var navigator = CreateNavigator();
        var canExecuteChanges = 0;
        navigator.ShowCashPaymentCommand.CanExecuteChanged += (_, _) => canExecuteChanges++;

        navigator.RefreshCartRelatedState();

        Assert.Equal(1, canExecuteChanges);
        Assert.Empty(_screens);
    }

    [Fact]
    public void Apply_session_pushes_current_session_to_existing_screens()
    {
        var navigator = CreateNavigator();
        var pos = AttachPosTerminal(navigator);
        var updated = Session with { CashierId = "C777", CashierName = "Bob", IsOnline = false };
        navigator.Session = updated;

        navigator.ApplySessionToScreens();

        Assert.Same(updated, pos.Session);
    }

    [Fact]
    public void Clear_screens_disposes_and_detaches_pos_terminal()
    {
        var navigator = CreateNavigator();
        AttachPosTerminal(navigator);

        navigator.ClearScreens();

        Assert.Null(navigator.PosTerminal);
        Assert.Null(navigator.CashPayment);
        Assert.Null(navigator.CachedCashPaymentScreen);
    }

    private ScreenNavigator CreateNavigator(ISuspendedOrderService? suspendedOrderService = null) =>
        new(
            factory: null!,
            _cart,
            _localization,
            suspendedOrderService,
            cardTerminalSetupService: null,
            testSalesDataResetService: null,
            confirmationDialogService: null!,
            customerDisplayOrchestrator: null!,
            linklyFallbackPromptCoordinator: null,
            cardPaymentRecoveryService: null,
            onCardRecoveryCenterResultAsync: (_, _) => Task.CompletedTask,
            syncCatalogAndReloadAsync: _ => Task.CompletedTask,
            resetCatalogAndReloadAsync: _ => Task.CompletedTask,
            checkForAppUpdateAsync: null,
            beginDeviceReregistrationAsync: () => throw new NotSupportedException("不应触发设备重新注册。"),
            recoverActiveCardPaymentSessionFromPaymentAsync: null,
            onInstallmentOrderCreatedAsync: _ => Task.CompletedTask,
            setScreen: _screens.Add,
            onPaymentCreated: _ => throw new NotSupportedException("不应创建付款页。"),
            onPaymentDisposed: _ => throw new NotSupportedException("不应释放付款页。"),
            printSelectedHistoryReceiptAsync: _ => Task.CompletedTask,
            setStatusMessage: _statusMessages.Add,
            getLastCompletedOrder: () => null,
            setLastCompletedOrder: _ => { })
        {
            Session = Session
        };

    private PosTerminalViewModel AttachPosTerminal(ScreenNavigator navigator)
    {
        var pos = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            _cart,
            Session,
            onOpenPayment: null,
            localization: _localization);
        _disposables.Add(pos);
        navigator.PosTerminal = pos;
        navigator.SetCachedPosTerminalScreen(pos);
        return pos;
    }

    private TransactionHistoryViewModel AttachHistory(ScreenNavigator navigator, IReceiptQueryService receiptQuery)
    {
        var history = new TransactionHistoryViewModel(receiptQuery);
        _disposables.Add(history);
        navigator.TransactionHistory = history;
        return history;
    }

    /// <summary>
    /// 直接挂一个「返回」回调接到导航器的分期中心：与 CreateInstallmentCenterViewModel 生产接线一致，
    /// 但不需要创建 MainChildViewModelFactory（本测试类传入的是 null 工厂）。
    /// </summary>
    private InstallmentCenterViewModel AttachInstallmentCenter(ScreenNavigator navigator)
    {
        var center = new InstallmentCenterViewModel(
            NoopInstallmentOrderService.Instance,
            Session,
            _ => Task.CompletedTask,
            navigator.BackFromInstallmentCenter);
        _disposables.Add(center);
        navigator.InstallmentCenter = center;
        return center;
    }

    private PaymentViewModel AttachPayment(ScreenNavigator navigator)
    {
        var payment = new PaymentViewModel(_cart, new NoopPaymentWorkflowService(), Session);
        _disposables.Add(payment);
        navigator.CashPayment = payment;
        return payment;
    }

    private static SellableItemDto CreateItem(string productCode, decimal price) =>
        new(
            "S01",
            productCode,
            null,
            "Item " + productCode,
            "LOOKUP-" + productCode,
            "ITEM-" + productCode,
            null,
            price,
            PriceSourceKind.StoreRetailPrice,
            "Store",
            1m,
            null);

    /// <summary>只统计查询次数的本地订单查询替身：用来证明「返回历史页」没有触发重新查询。</summary>
    private sealed class CountingReceiptQueryService : IReceiptQueryService
    {
        public int QueryCallCount { get; private set; }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            QueryCallCount++;
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            QueryCallCount++;
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<ReceiptDetails?> GetReceiptAsync(Guid orderGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReceiptDetails?>(null);

        public Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ReceiptDetails?>(null);
    }

    /// <summary>付款页只用来当「返回目标」，不会真的收款：用到的工作流方法一律不应被调用。</summary>
    private sealed class NoopPaymentWorkflowService : ICashPaymentWorkflowService
    {
        public bool TryParseTenderedAmount(string? amountTenderedText, out decimal tenderedAmount) =>
            decimal.TryParse(amountTenderedText, out tenderedAmount);

        public decimal CalculateChange(string? amountTenderedText, decimal actualAmount) => 0m;

        public decimal CalculateTenderedAmount(IReadOnlyList<PaymentTender> tenders) => tenders.Sum(tender => tender.Amount);

        public decimal CalculateRemainingAmount(decimal actualAmount, IReadOnlyList<PaymentTender> tenders) =>
            Math.Max(0m, actualAmount - CalculateTenderedAmount(tenders));

        public decimal CalculateChange(IReadOnlyList<PaymentTender> tenders, decimal actualAmount) => 0m;

        public Task<PaymentTenderAttemptResult> AddTenderAsync(
            PaymentMethodKind method,
            PosSessionState session,
            decimal actualAmount,
            IReadOnlyList<PaymentTender> currentTenders,
            string? amountText,
            string? referenceText = null,
            CancellationToken cancellationToken = default,
            PosCartSnapshot? cartSnapshot = null) =>
            throw new NotSupportedException();

        public Task<CashPaymentWorkflowResult> CompleteAsync(
            PosCartService cart,
            PosSessionState session,
            string? amountTenderedText,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CashPaymentWorkflowResult> CompletePaymentAsync(
            PosCartService cart,
            PosSessionState session,
            IReadOnlyList<PaymentTender> tenders,
            decimal cashTenderedAmount,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CashPaymentWorkflowResult> RetryVoucherUploadAsync(
            Guid orderGuid,
            PosCartService cart,
            PosSessionState session,
            decimal tenderedAmount,
            decimal changeAmount,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class KeyEchoLocalization : ILocalizationService
    {
        public Dictionary<string, string> Overrides { get; } = [];

        public IReadOnlyList<CultureInfo> AvailableCultures { get; } = [CultureInfo.InvariantCulture];

        public CultureInfo CurrentCulture => CultureInfo.InvariantCulture;

        public event EventHandler? CultureChanged
        {
            add { }
            remove { }
        }

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public void SetCulture(string cultureName) => throw new NotSupportedException();

        public void SetCulture(CultureInfo culture) => throw new NotSupportedException();

        public Task SetCultureAsync(string cultureName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string T(string key) => Overrides.TryGetValue(key, out var value) ? value : key;
    }

    private sealed class FakeSuspendedOrderService : ISuspendedOrderService
    {
        public Guid Result { get; init; }

        public Exception? Failure { get; init; }

        public List<PosSessionState> SuspendedSessions { get; } = [];

        public Task<SuspendedOrder> SuspendCurrentOrderAsync(PosSessionState session, CancellationToken cancellationToken = default)
        {
            SuspendedSessions.Add(session);
            if (Failure is not null)
            {
                return Task.FromException<SuspendedOrder>(Failure);
            }

            return Task.FromResult(new SuspendedOrder(
                Result,
                session.StoreCode,
                session.DeviceCode,
                session.CashierId,
                session.CashierName,
                DateTimeOffset.UnixEpoch,
                0m,
                0m,
                0m,
                SuspendedOrderStatus.Pending,
                []));
        }

        public Task<IReadOnlyList<SuspendedOrderSummary>> GetPendingOrdersAsync(
            string storeCode,
            string? deviceCode = null,
            string? keyword = null,
            int take = 100,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SuspendedOrder?> GetOrderAsync(Guid suspendedOrderGuid, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SuspendedOrder> RecallOrderAsync(Guid suspendedOrderGuid, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
