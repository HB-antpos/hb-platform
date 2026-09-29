using System.ComponentModel;
using System.Globalization;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;

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
    public async Task Suspend_without_suspended_order_service_reports_unavailable()
    {
        var navigator = CreateNavigator(suspendedOrderService: null);

        await navigator.SuspendCurrentOrderAsync();

        Assert.Equal(new[] { "main.suspendedUnavailable" }, _statusMessages);
    }

    [Fact]
    public async Task Suspend_success_reports_first_eight_upper_case_characters_of_order_id()
    {
        var orderGuid = Guid.Parse("abcdef12-3456-7890-abcd-ef1234567890");
        var service = new FakeSuspendedOrderService { Result = orderGuid };
        _localization.Overrides["main.suspendedSaved"] = "Saved #{0}";
        var navigator = CreateNavigator(service);
        navigator.Session = Session with { CashierId = "C009" };

        await navigator.SuspendCurrentOrderAsync();

        Assert.Equal(new[] { "Saved #ABCDEF12" }, _statusMessages);
        Assert.Equal("C009", Assert.Single(service.SuspendedSessions).CashierId);
        Assert.Empty(_screens);
    }

    [Fact]
    public async Task Suspend_failure_reports_exception_message_without_navigating()
    {
        var service = new FakeSuspendedOrderService { Failure = new InvalidOperationException("挂单写入失败") };
        var navigator = CreateNavigator(service);
        AttachPosTerminal(navigator);

        await navigator.SuspendCurrentOrderAsync();

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
