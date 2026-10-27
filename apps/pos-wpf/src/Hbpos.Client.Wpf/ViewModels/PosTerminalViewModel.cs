using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.ViewModels;

public enum StatusFeedbackKind
{
    Neutral,
    Success,
    Warning,
    Error
}

public sealed partial class PosTerminalViewModel : ObservableObject, IScannerInputTarget, IDisposable
{
    public const string PageId = "PosTerminal";

    public string ScannerPageId => PageId;

    private readonly LocalSellableItemIndex _priceIndex;
    private readonly PosCartService _cart;
    private readonly IPosTerminalWorkflowService _workflowService;
    private readonly IPromotionEvaluationService? _promotionEvaluationService;
    private readonly PosTerminalActions _actions;
    private readonly PosTerminalScanController _scanController;
    private readonly ILocalizationService? _localization;
    private readonly IUserFeedbackService _userFeedbackService;
    private readonly ICashierSessionContext _cashierSessionContext;
    private readonly bool _enforcePermissions;
    private readonly IRawScannerService? _rawScannerService;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? _syncCatalogAsync;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? _resetCatalogAsync;
    private readonly Func<CancellationToken, Task<bool>>? _refreshOnlineAsync;
    private readonly Func<string, CancellationToken, Task<bool>>? _tryLoginCashierFromScannerFallbackAsync;
    private readonly IOperationAuditLogger? _operationAuditLogger;
    private readonly IOperationAuthorizationService? _operationAuthorizationService;
    private readonly Func<Guid, PosSessionState, CancellationToken, Task>? _releaseSharedHeldOrderAsync;
    // 设置页「无交易开钱箱需要再次扫码」的当前值；每次点开钱箱都现读，改设置后立即生效，不缓存。
    private readonly Func<CancellationToken, Task<bool>>? _isNoSaleCashDrawerRescanRequiredAsync;
    // 设置页「退货操作需要再次扫码」的当前值；同样每次点「退款」都现读。
    private readonly Func<CancellationToken, Task<bool>>? _isReturnsRescanRequiredAsync;
    private readonly Func<string?, string, CancellationToken, IReadOnlyList<SellableItemDto>> _searchCatalogMatches;
    private string _statusKey = "pos.status.ready";
    private object[] _statusArgs = [];
    private string? _statusText;
    private string? _activeScanTraceId;
    private DateTimeOffset? _activeScanStartedAt;
    private long _cartChangedSequence;
    private bool _isPromotionEvaluationRunning;
    private string? _queuedPromotionEvaluationReason;
    private Task _pendingPromotionEvaluationTask = Task.CompletedTask;
    private CancellationTokenSource? _matchesRefreshCts;
    private long _matchesRefreshVersion;
    private CancellationTokenSource? _manualSearchCts;
    private long _manualSearchVersion;
    // 收银会话的身份（门店 / 设备 / 收银员）。联机状态、待同步数等字段变化不算换会话，不应让在途补查作废。
    private (string StoreCode, string DeviceCode, string CashierId) _sessionIdentity;
    // 正在等远程补查结果的扫码：从补查发起到整条扫码链路（加购、促销排队、审计）结束才完成，供点击收款时等待。
    private readonly List<TaskCompletionSource> _pendingRemoteLookupScans = [];
    // 收款等待补查的预算计时走 TimeProvider，测试可注入 FakeTimeProvider。
    private readonly TimeProvider _timeProvider;
    private bool _isOpeningPayment;
    private bool _disposed;

    [ObservableProperty]
    private PosSessionState _session;

    [ObservableProperty]
    private string _scanText = string.Empty;

    [ObservableProperty]
    private string _keypadBuffer = string.Empty;

    [ObservableProperty]
    private SellableItemDto? _selectedItem;

    [ObservableProperty]
    private CartLine? _selectedCartLine;

    [ObservableProperty]
    private bool _isMatchesPopupOpen;

    // 中文注释：结账完成后不再跳转整页成功页，上一单以卡片显示在购物车区域，扫码或加入商品后收起。
    [ObservableProperty]
    private PaymentSuccessViewModel? _lastSale;

    [ObservableProperty]
    private bool _isLastSaleVisible;

    [ObservableProperty]
    private bool _isTouchKeyboardOpen;

    [ObservableProperty]
    private bool _isWholeOrderOperation;

    [ObservableProperty]
    private StatusFeedbackKind _statusFeedbackKind = StatusFeedbackKind.Neutral;

    [ObservableProperty]
    private int _statusPulseToken;

    [ObservableProperty]
    private int _cardRecoveryOpenCount;

    public PosTerminalViewModel(
        LocalSellableItemIndex priceIndex,
        PosCartService cart,
        PosSessionState session,
        Action? onOpenPayment,
        Func<Task>? onOpenSpecialProductsAsync = null,
        ILocalizationService? localization = null,
        IUserFeedbackService? userFeedbackService = null,
        Func<string, string, CancellationToken, Task<RemoteLookupRefreshResult>>? remoteLookupRefreshAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? reloadCatalogAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? syncCatalogAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? resetCatalogAsync = null,
        Func<CancellationToken, Task<bool>>? refreshOnlineAsync = null,
        IRawScannerService? rawScannerService = null,
        Func<Task>? onReregisterDeviceAsync = null,
        IPosTerminalWorkflowService? workflowService = null,
        Func<Task>? onHoldOrderAsync = null,
        Func<Task>? onRecallOrderAsync = null,
        Func<Task>? onOpenHistoryAsync = null,
        Func<Task>? onOpenDailyCloseAsync = null,
        Func<Task>? onOpenSettingsAsync = null,
        Action? onOpenCustomerDisplay = null,
        Action? onOpenReturns = null,
        Func<Task<ReceiptPrintResult>>? onPrintLastReceiptAsync = null,
        Func<Task<ReceiptPrintResult>>? onOpenCashDrawerAsync = null,
        Func<Task>? onExitApplicationAsync = null,
        Func<string, CancellationToken, Task<bool>>? tryLoginCashierFromScannerFallbackAsync = null,
        ICashierSessionContext? cashierSessionContext = null,
        bool enforcePermissionsWhenNoCashier = false,
        IPromotionEvaluationService? promotionEvaluationService = null,
        IOperationAuditLogger? operationAuditLogger = null,
        Func<Task>? onLockCashierAsync = null,
        IOperationAuthorizationService? operationAuthorizationService = null,
        Func<string?, string, CancellationToken, IReadOnlyList<SellableItemDto>>? searchCatalogMatches = null,
        Func<Guid, PosSessionState, CancellationToken, Task>? releaseSharedHeldOrderAsync = null,
        Func<Task>? onOpenCardRecoveryCenterAsync = null,
        TimeProvider? timeProvider = null,
        Func<CancellationToken, Task<bool>>? isNoSaleCashDrawerRescanRequiredAsync = null,
        Func<CancellationToken, Task<bool>>? isReturnsRescanRequiredAsync = null,
        IHeldOrderTodayCountService? heldOrderTodayCountService = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        // 「取单」按钮角标的统计服务；为 null（测试 / 预览）时不显示角标、不起定时器。
        _heldOrderTodayCountService = heldOrderTodayCountService;
        _heldOrderCountScope = (session.StoreCode, session.DeviceCode, session.IsOnline, session.CashierSession is not null);
        _isNoSaleCashDrawerRescanRequiredAsync = isNoSaleCashDrawerRescanRequiredAsync;
        _isReturnsRescanRequiredAsync = isReturnsRescanRequiredAsync;
        _priceIndex = priceIndex;
        _searchCatalogMatches = searchCatalogMatches ?? ((storeCode, query, cancellationToken) =>
            _priceIndex.Search(storeCode, query, cancellationToken));
        _cart = cart;
        _workflowService = workflowService ?? new PosTerminalWorkflowService(priceIndex, cart, remoteLookupRefreshAsync, reloadCatalogAsync);
        _promotionEvaluationService = promotionEvaluationService;
        _operationAuditLogger = operationAuditLogger;
        _operationAuthorizationService = operationAuthorizationService;
        _releaseSharedHeldOrderAsync = releaseSharedHeldOrderAsync;
        _actions = PosTerminalActions.FromLegacyCallbacks(
            onOpenPayment,
            onOpenReturns,
            onOpenSpecialProductsAsync,
            onHoldOrderAsync,
            onRecallOrderAsync,
            onOpenHistoryAsync,
            onOpenDailyCloseAsync,
            onOpenSettingsAsync,
            onOpenCustomerDisplay,
            onPrintLastReceiptAsync,
            onOpenCashDrawerAsync,
            onExitApplicationAsync,
            onReregisterDeviceAsync,
            onLockCashierAsync,
            onOpenCardRecoveryCenterAsync);
        _scanController = new PosTerminalScanController(cart);
        // 待选条：只做界面投影，加购 / 放弃都回到本页的方法（授权、审计、状态栏都在这里统一处理）。
        PendingChoices = new PendingScanChoicePanelViewModel(
            _workflowService.PendingChoices,
            localization,
            _timeProvider,
            SelectPendingChoiceAsync,
            AbandonPendingChoice,
            AbandonAllPendingChoices);
        _session = session;
        _sessionIdentity = GetSessionIdentity(session);
        _localization = localization;
        _userFeedbackService = userFeedbackService ?? NoopUserFeedbackService.Instance;
        _cashierSessionContext = cashierSessionContext ?? new CashierSessionContext();
        _enforcePermissions = enforcePermissionsWhenNoCashier;
        if (session.CashierSession is not null)
        {
            _cashierSessionContext.SetCurrent(session.CashierSession);
        }

        _syncCatalogAsync = syncCatalogAsync;
        _resetCatalogAsync = resetCatalogAsync;
        _refreshOnlineAsync = refreshOnlineAsync;
        _tryLoginCashierFromScannerFallbackAsync = tryLoginCashierFromScannerFallbackAsync;
        _rawScannerService = rawScannerService;
        if (_localization is not null)
        {
            _localization.CultureChanged += OnCultureChanged;
        }

        _cart.CartChanged += OnCartChanged;
        _workflowService.CatalogReloaded += OnWorkflowCatalogReloaded;
        _rawScannerService?.Subscribe(PageId, OnRawBarcodeScanned);

        ScanCommand = new AsyncRelayCommand(SearchAndAddAsync);
        NumberInputCommand = new RelayCommand<string>(AppendScanText);
        KeypadInputCommand = new RelayCommand<string>(AppendKeypadBuffer);
        ToggleTouchKeyboardCommand = new RelayCommand(ToggleTouchKeyboard);
        AddOpenItemCommand = new AsyncRelayCommand(AddOpenItemAsync, CanAddOpenItem);
        AddSelectedCommand = new AsyncRelayCommand(AddSelectedAsync, () => SelectedItem is not null);
        SelectMatchCommand = new AsyncRelayCommand<SellableItemDto>(SelectMatchAsync);
        RemoveLineCommand = new AsyncRelayCommand<CartLine>(RemoveLineAsync);
        IncreaseLineCommand = new AsyncRelayCommand<CartLine>(IncreaseLineAsync, line => line is not null && !line.IsLocked && _cart.Lines.Contains(line));
        DecreaseLineCommand = new AsyncRelayCommand<CartLine>(
            DecreaseLineAsync,
            line => line is not null &&
                    !line.IsLocked &&
                    line.Quantity > 1m &&
                    _cart.Lines.Contains(line));
        ModifySelectedLineQuantityCommand = new AsyncRelayCommand(ModifySelectedLineQuantityAsync);
        ModifySelectedLinePriceCommand = new AsyncRelayCommand(ModifySelectedLinePriceAsync);
        ApplySelectedLineDiscountAmountCommand = new AsyncRelayCommand(ApplySelectedLineDiscountAmountAsync);
        ApplySelectedLineDiscountPercentCommand = new AsyncRelayCommand(ApplySelectedLineDiscountPercentAsync);
        ApplyQuickDiscountPercentCommand = new AsyncRelayCommand<string>(ApplyQuickDiscountPercentAsync);
        ClearSearchCommand = new RelayCommand(ClearSearch, () => !string.IsNullOrWhiteSpace(ScanText));
        ClearCartCommand = new AsyncRelayCommand(ClearCartAsync, () => !_cart.IsEmpty);
        DismissLastSaleCommand = new RelayCommand(DismissLastSale);
        OpenPaymentCommand = new AsyncRelayCommand(OpenPaymentAsync, () => !_cart.IsEmpty);
        OpenReturnsCommand = new AsyncRelayCommand(OpenReturnsAsync);
        OpenSpecialProductsCommand = new AsyncRelayCommand(OpenSpecialProductsAsync);
        HoldOrderCommand = new AsyncRelayCommand(HoldOrderAsync, () => !_cart.IsEmpty);
        RecallOrderCommand = new AsyncRelayCommand(RecallOrderAsync);
        OpenHistoryCommand = new AsyncRelayCommand(OpenHistoryAsync);
        OpenDailyCloseCommand = new AsyncRelayCommand(OpenDailyCloseAsync);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);
        OpenCustomerDisplayCommand = new AsyncRelayCommand(OpenCustomerDisplayAsync);
        OpenCardRecoveryCenterCommand = new AsyncRelayCommand(
            OpenCardRecoveryCenterAsync,
            () => _actions.OpenCardRecoveryCenterAsync is not null);
        PrintLastReceiptCommand = new AsyncRelayCommand(PrintLastReceiptAsync, () => _actions.CanPrintLastReceipt);
        OpenCashDrawerCommand = new AsyncRelayCommand(OpenCashDrawerAsync, () => _actions.CanOpenCashDrawer);
        LockCashierCommand = new AsyncRelayCommand(LockCashierAsync);
        ExitApplicationCommand = new AsyncRelayCommand(ExitApplicationAsync, () => _actions.CanExitApplication);
        // 手动目录操作使用命令令牌，保留调用方取消能力，同时不影响后台无超时同步。
        SyncCommand = new AsyncRelayCommand(SyncAsync);
        ResetCatalogCommand = new AsyncRelayCommand(ResetCatalogAsync);
        ReregisterDeviceCommand = new AsyncRelayCommand(ReregisterDeviceAsync);
    }

    public ObservableCollection<SellableItemDto> Matches { get; } = [];

    /// <summary>扫码枪扫到「一码多商品」后的待选条（先进先出，不选不消失，期间继续扫的商品照常加购）。</summary>
    public PendingScanChoicePanelViewModel PendingChoices { get; }

    public ObservableCollection<CartLine> CartLines { get; } = [];

    public IAsyncRelayCommand ScanCommand { get; }

    public IRelayCommand<string> NumberInputCommand { get; }

    public IRelayCommand<string> KeypadInputCommand { get; }

    public IRelayCommand ToggleTouchKeyboardCommand { get; }

    public IRelayCommand AddOpenItemCommand { get; }

    public IRelayCommand AddSelectedCommand { get; }

    public IRelayCommand<SellableItemDto> SelectMatchCommand { get; }

    public IRelayCommand<CartLine> RemoveLineCommand { get; }

    public IRelayCommand<CartLine> IncreaseLineCommand { get; }

    public IRelayCommand<CartLine> DecreaseLineCommand { get; }

    public IRelayCommand ModifySelectedLineQuantityCommand { get; }

    public IRelayCommand ModifySelectedLinePriceCommand { get; }

    public IRelayCommand ApplySelectedLineDiscountAmountCommand { get; }

    public IRelayCommand ApplySelectedLineDiscountPercentCommand { get; }

    public IRelayCommand<string> ApplyQuickDiscountPercentCommand { get; }

    public IRelayCommand ClearSearchCommand { get; }

    public IRelayCommand ClearCartCommand { get; }

    public IRelayCommand DismissLastSaleCommand { get; }

    public IRelayCommand OpenPaymentCommand { get; }

    public IRelayCommand OpenReturnsCommand { get; }

    public IAsyncRelayCommand OpenSpecialProductsCommand { get; }

    public IAsyncRelayCommand HoldOrderCommand { get; }

    public IAsyncRelayCommand RecallOrderCommand { get; }

    public IAsyncRelayCommand OpenHistoryCommand { get; }

    public IAsyncRelayCommand OpenDailyCloseCommand { get; }

    public IAsyncRelayCommand OpenSettingsCommand { get; }

    public IRelayCommand OpenCustomerDisplayCommand { get; }

    public IAsyncRelayCommand OpenCardRecoveryCenterCommand { get; }

    public IAsyncRelayCommand PrintLastReceiptCommand { get; }

    public IAsyncRelayCommand OpenCashDrawerCommand { get; }

    public IAsyncRelayCommand LockCashierCommand { get; }

    public IAsyncRelayCommand ExitApplicationCommand { get; }

    public IAsyncRelayCommand SyncCommand { get; }

    public IAsyncRelayCommand ResetCatalogCommand { get; }

    public IAsyncRelayCommand ReregisterDeviceCommand { get; }

    public event EventHandler? PaymentRequested;

    public string ScreenTitleText => T("pos.terminal.title");

    public string SearchPlaceholderText => T("pos.terminal.search.placeholder");

    public string SearchButtonText => T("pos.terminal.search.action");

    public string AddSelectedText => T("pos.terminal.addSelected");

    public string CartTitleText => T("pos.terminal.cart.title");

    public string TotalsLabelText => T("pos.terminal.totals.total");

    public string PayNowText => T("pos.terminal.payNow");

    public string ClearCartText => T("pos.terminal.actions.clearCart");

    public string HoldOrderText => T("pos.terminal.actions.holdOrder");

    public string RecallOrderText => T("pos.terminal.actions.recallOrder");

    public string HistoryText => T("pos.terminal.actions.history");

    public string DailyCloseText => T("pos.terminal.actions.dailyClose");

    public string SettingsText => T("pos.terminal.actions.settings");

    public string CustomerDisplayText => T("pos.terminal.actions.customerDisplay");

    public string PrintLastReceiptText => T("pos.terminal.actions.printLastReceipt");

    public string OpenCashDrawerText => T("pos.terminal.actions.openCashDrawer");

    public string LockCashierText => T("pos.terminal.actions.lock");

    public string ExitApplicationText => T("pos.terminal.actions.exitApplication");

    public string MemberText => T("pos.terminal.actions.member");

    public string SyncText => T("pos.terminal.actions.sync");

    public string CatalogResetText => T("pos.terminal.actions.catalogReset");

    public string ReregisterDeviceText => T("pos.terminal.actions.reregisterDevice");

    public string OnlineText => T(Session.IsOnline ? "pos.status.online" : "pos.status.offline");

    public string PendingSyncText => Format("pos.status.pendingSync", Session.PendingSyncCount);

    public bool HasOpenCardRecoveryAttempts => CardRecoveryOpenCount > 0;

    public string CardRecoveryCenterText => T("shell.page.cardRecovery");

    public string CardRecoveryOpenText => Format("cardRecovery.center.openCount", CardRecoveryOpenCount);

    public string CardRecoveryCenterAutomationName => HasOpenCardRecoveryAttempts
        ? $"{CardRecoveryCenterText} {CardRecoveryOpenText}"
        : CardRecoveryCenterText;

    public string StatusMessage => _statusText ?? Format(_statusKey, _statusArgs);

    public decimal TotalAmount => _cart.TotalAmount;

    public decimal DiscountAmount => _cart.DiscountAmount;

    public decimal ActualAmount => _cart.ActualAmount;

    public decimal CartItemQuantity => _cart.Lines.Sum(line => line.SignedQuantity);

    public int CartSkuCount => _cart.Lines.Count;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // 页面销毁后（重新登录、锁定重建等）旧页面里仍在途的补查结果不得再改动共享购物车。
        _workflowService.AdvanceCartGeneration("view-model-disposed");
        // 页面销毁（重新登录 / 重建页面）：待选随页面丢弃，留一条日志说明丢了几个。
        _workflowService.AbandonAllPendingChoices(PendingChoiceReasons.Disposed);
        PendingChoices.Dispose();
        CancelMatchesRefresh();
        CancelManualSearch();
        // 停掉挂单角标的定时刷新并作废在途统计，页面销毁后不再写属性。
        DisposeHeldOrderCount();
        if (_localization is not null)
        {
            _localization.CultureChanged -= OnCultureChanged;
        }

        _cart.CartChanged -= OnCartChanged;
        _workflowService.CatalogReloaded -= OnWorkflowCatalogReloaded;
        _rawScannerService?.Unsubscribe(PageId);
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RaiseLocalizedProperties();
    }

    partial void OnSelectedItemChanged(SellableItemDto? value)
    {
        AddSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnScanTextChanged(string value)
    {
        CancelManualSearch();
        ClearSearchCommand.NotifyCanExecuteChanged();
        if (IsMatchesPopupOpen)
        {
            RefreshMatches();
        }
    }

    partial void OnIsMatchesPopupOpenChanged(bool value)
    {
        if (!value)
        {
            CancelMatchesRefresh();
        }
        else
        {
            // 中文注释：每次重开都按当前快照重建，避免关闭、清空后仍显示旧匹配。
            RefreshMatches();
        }
    }

    partial void OnKeypadBufferChanged(string value)
    {
        AddOpenItemCommand.NotifyCanExecuteChanged();
    }

    partial void OnSessionChanged(PosSessionState value)
    {
        CancelManualSearch();
        var sessionIdentity = GetSessionIdentity(value);
        if (sessionIdentity != _sessionIdentity)
        {
            // 换门店 / 设备：待选商品属于旧门店的目录，不能带到新会话；只换收银员（锁屏后换人登录）时购物车也保留，待选随购物车保留。
            var storeOrDeviceChanged =
                sessionIdentity.StoreCode != _sessionIdentity.StoreCode ||
                sessionIdentity.DeviceCode != _sessionIdentity.DeviceCode;
            // 换门店 / 设备 / 收银员：此前扫码发起的补查结果不属于新会话，作废。
            _sessionIdentity = sessionIdentity;
            _workflowService.AdvanceCartGeneration("session-changed");
            if (storeOrDeviceChanged)
            {
                _workflowService.AbandonAllPendingChoices(PendingChoiceReasons.Session);
            }

            // 一次性整单开关不能带进另一位收银员 / 另一台设备的会话。
            IsWholeOrderOperation = false;
        }

        if (value.CashierSession is not null)
        {
            _cashierSessionContext.SetCurrent(value.CashierSession);
        }

        OnPropertyChanged(nameof(OnlineText));
        OnPropertyChanged(nameof(PendingSyncText));
        // 换门店 / 设备、联机状态或收银员登录状态变化时重算「取单」角标（首页可见时才刷新）。
        OnSessionChangedForHeldOrderCount(value);
    }

    partial void OnCardRecoveryOpenCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasOpenCardRecoveryAttempts));
        OnPropertyChanged(nameof(CardRecoveryOpenText));
        OnPropertyChanged(nameof(CardRecoveryCenterAutomationName));
    }

    public void LoadMatches(IEnumerable<SellableItemDto> items)
    {
        Matches.ReplaceWith(items.Take(8));
    }

    public void RefreshCart()
    {
        RefreshCartCore("manual-refresh");
    }

    internal void RevealCartLine(CartLine line)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!_cart.Lines.Contains(line))
        {
            stopwatch.Stop();
            LogCartOperation("reveal-cart-line", line, success: false, stopwatch.ElapsedMilliseconds, "line-not-in-cart");
            return;
        }

        if (!CartLines.Contains(line))
        {
            SyncCartLines(_cart.Lines);
        }

        SelectCartLine(line);
        RefreshCartState();
        stopwatch.Stop();
        LogCartOperation("reveal-cart-line", line, success: true, stopwatch.ElapsedMilliseconds);
    }

    private void RefreshCartCore(string operation, string? traceId = null, DateTimeOffset? scanStartedAt = null)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var syncCartStopwatch = Stopwatch.StartNew();
        SyncCartLines(_cart.Lines);
        syncCartStopwatch.Stop();

        var stateRefreshStopwatch = Stopwatch.StartNew();
        RefreshCartState();
        stateRefreshStopwatch.Stop();
        totalStopwatch.Stop();

        LogCartPerf(
            $"{FormatTraceId(traceId)}operation={operation} storeCode={Session.StoreCode} cartLines={_cart.Lines.Count} scanAgeMs={FormatElapsedSince(scanStartedAt)} syncCartElapsedMs={syncCartStopwatch.ElapsedMilliseconds} stateRefreshElapsedMs={stateRefreshStopwatch.ElapsedMilliseconds} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
    }

    private void RefreshCartState()
    {
        OnPropertyChanged(nameof(TotalAmount));
        OnPropertyChanged(nameof(DiscountAmount));
        OnPropertyChanged(nameof(ActualAmount));
        OnPropertyChanged(nameof(CartItemQuantity));
        OnPropertyChanged(nameof(CartSkuCount));
        IncreaseLineCommand.NotifyCanExecuteChanged();
        DecreaseLineCommand.NotifyCanExecuteChanged();
        ClearCartCommand.NotifyCanExecuteChanged();
        OpenPaymentCommand.NotifyCanExecuteChanged();
        HoldOrderCommand.NotifyCanExecuteChanged();
    }

    private void SyncCartLines(IReadOnlyList<CartLine> sourceLines)
    {
        for (var i = CartLines.Count - 1; i >= 0; i--)
        {
            if (!sourceLines.Contains(CartLines[i]))
            {
                CartLines.RemoveAt(i);
            }
        }

        for (var sourceIndex = 0; sourceIndex < sourceLines.Count; sourceIndex++)
        {
            var line = sourceLines[sourceIndex];
            var currentIndex = CartLines.IndexOf(line);
            if (currentIndex < 0)
            {
                CartLines.Insert(Math.Min(sourceIndex, CartLines.Count), line);
            }
            else if (currentIndex != sourceIndex)
            {
                CartLines.Move(currentIndex, sourceIndex);
            }
        }

        if (SelectedCartLine is not null && !sourceLines.Contains(SelectedCartLine))
        {
            SelectedCartLine = null;
        }
    }

    public void ShowLastSale(PaymentSuccessViewModel sale)
    {
        LastSale = sale;
        IsLastSaleVisible = true;
    }

    public void DismissLastSale()
    {
        IsLastSaleVisible = false;
    }

    private static (string StoreCode, string DeviceCode, string CashierId) GetSessionIdentity(PosSessionState session)
    {
        return (session.StoreCode, session.DeviceCode, session.CashierId);
    }

    private void OnCartChanged(object? sender, EventArgs e)
    {
        if (!_cart.IsEmpty)
        {
            DismissLastSale();
        }
        else
        {
            // 购物车变空：不论是本页清空，还是付款完成 / 挂单 / 其它流程清空了共享购物车，都意味着换了一单。
            // 此前扫码发起、还没回来的补查结果不能落进下一单。
            _workflowService.AdvanceCartGeneration("cart-emptied");
            // 一次性整单开关随这一单结束：清空购物车、挂单、付款完成后都不能把「整单」带给下一位顾客。
            IsWholeOrderOperation = false;
        }

        _cartChangedSequence++;
        RefreshCartCore("cart-changed", _activeScanTraceId, _activeScanStartedAt);
    }

    /// <param name="isWholeOrderOperation">
    /// 折扣类操作发起时的整单开关快照。整单开关是一次性的，按下折扣键就已复位，
    /// 所以审计类型（整单折扣 / 单品折扣）必须用快照，不能再读当前属性。
    /// </param>
    /// <param name="traceId">
    /// 可选的关联 traceId：待选选定入车时传入原扫码的 traceId，让购物车审计与扫码日志串得起来；
    /// 不传则沿用原行为（审计里不带 traceId）。
    /// </param>
    private PosTerminalWorkflowResult ExecuteCartMutation(
        string reason,
        Func<PosTerminalWorkflowResult> operation,
        bool isWholeOrderOperation = false,
        string? traceId = null)
    {
        var before = OperationAuditEvents.CaptureCart(_cart.Lines);
        var operationType = MapCartOperationType(reason, isWholeOrderOperation);
        var previousSequence = _cartChangedSequence;
        PosTerminalWorkflowResult result;
        try
        {
            result = operation();
        }
        catch (Exception ex)
        {
            var correlation = OperationAuditEvents.CreateCorrelation();
            OperationAuditEvents.RecordCartChange(
                _operationAuditLogger,
                operationType,
                Session,
                before,
                OperationAuditEvents.CaptureCart(_cart.Lines),
                outcome: "Failed",
                reasonCode: reason,
                safeMessage: ex.GetType().Name,
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            ConsoleLog.WriteError(
                "OperationAudit",
                $"cart operation failed operation={reason} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            throw;
        }

        ApplyWorkflowResult(result);
        QueuePromotionEvaluationIfCartChanged(previousSequence, reason);
        var after = OperationAuditEvents.CaptureCart(_cart.Lines);
        if (OperationAuditEvents.HasChanged(before, after))
        {
            // 只在购物车真实变化后记录成功，促销后台重算不会单独生成员工操作事件。
            OperationAuditEvents.RecordCartChange(
                _operationAuditLogger,
                operationType,
                Session,
                before,
                after,
                reasonCode: reason,
                traceId: traceId);
        }

        return result;
    }

    private static string MapCartOperationType(string reason, bool isWholeOrderOperation)
    {
        return reason switch
        {
            "add-open-item" or "manual-add-selected" or "manual-select-match" or "scan-choice-select" => OperationAuditTypes.CartItemAdd,
            "remove-line" => OperationAuditTypes.CartItemRemove,
            "increase-line" or "decrease-line" or "modify-quantity" => OperationAuditTypes.CartItemQuantityChange,
            "modify-price" => OperationAuditTypes.CartItemPriceChange,
            "manual-discount-amount" or "manual-discount-percent" or "quick-discount-percent" =>
                isWholeOrderOperation
                    ? OperationAuditTypes.CartOrderDiscountChange
                    : OperationAuditTypes.CartLineDiscountChange,
            "clear-cart" => OperationAuditTypes.CartClear,
            _ => OperationAuditTypes.CartItemAdd
        };
    }

    private void QueuePromotionEvaluationIfCartChanged(long previousSequence, string reason)
    {
        if (_promotionEvaluationService is null || _cartChangedSequence == previousSequence)
        {
            return;
        }

        // 中文注释：扫码和改数量只排队促销重算，真正等待发生在收款或挂单前。
        _queuedPromotionEvaluationReason = reason;
        if (_isPromotionEvaluationRunning)
        {
            return;
        }

        _pendingPromotionEvaluationTask = RunPromotionEvaluationLoopAsync();
    }

    private async Task RunPromotionEvaluationLoopAsync()
    {
        if (_isPromotionEvaluationRunning)
        {
            return;
        }

        _isPromotionEvaluationRunning = true;
        try
        {
            while (_queuedPromotionEvaluationReason is string reason)
            {
                _queuedPromotionEvaluationReason = null;
                await ReevaluatePromotionsAsync(reason);
            }
        }
        finally
        {
            _isPromotionEvaluationRunning = false;
            if (_queuedPromotionEvaluationReason is not null)
            {
                _pendingPromotionEvaluationTask = RunPromotionEvaluationLoopAsync();
            }
        }
    }

    private async Task WaitForPendingPromotionEvaluationAsync()
    {
        while (true)
        {
            var pendingTask = _pendingPromotionEvaluationTask;
            if (pendingTask.IsCompleted)
            {
                return;
            }

            await pendingTask;
        }
    }

    private async Task ReevaluatePromotionsAsync(string reason)
    {
        if (_promotionEvaluationService is null)
        {
            return;
        }

        try
        {
            var discounts = await _promotionEvaluationService.EvaluateAsync(
                _cart.Lines.ToArray(),
                Session.StoreCode,
                DateTimeOffset.Now);
            _cart.ApplyPromotionDiscounts(discounts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PromotionComputationBudgetExceededException ex)
        {
            // 超限时清掉本单全部旧自动促销；ApplyPromotionDiscounts 会保留手工和目录折扣。
            _cart.ApplyPromotionDiscounts([]);
            ConsoleLog.Write(
                "Promotion",
                $"promotion evaluation skipped reason={reason} budget-exceeded {ex.ToDiagnosticText()}");
            SetStatusText(T("pos.status.promotionBudgetExceeded"), StatusFeedbackKind.Warning);
        }
        catch (Exception ex)
        {
            // 中文注释：促销失败时保留当前折扣，只提示降级，避免收银金额被半更新。
            ConsoleLog.Write("Promotion", $"promotion evaluation failed reason={reason} error={ex.Message}");
            SetStatusText(T("pos.status.promotionFallback"), StatusFeedbackKind.Warning);
        }
    }

    private void OnWorkflowCatalogReloaded(object? sender, PosTerminalCatalogReloadedEventArgs e)
    {
        RefreshMatches(requireOpenNonEmptySearch: true);
    }

    private void AppendScanText(string? value)
    {
        if (value == "Enter")
        {
            _ = SearchAndAddSafeAsync();
            IsTouchKeyboardOpen = false;
            return;
        }

        if (value == "Back")
        {
            ScanText = ScanText.Length > 0 ? ScanText[..^1] : string.Empty;
            return;
        }

        if (value == "Space")
        {
            ScanText += " ";
            return;
        }

        if (value == "Clear")
        {
            ScanText = string.Empty;
            IsMatchesPopupOpen = false;
            IsTouchKeyboardOpen = false;
            return;
        }

        ScanText += value;
    }

    private void AppendKeypadBuffer(string? value)
    {
        if (value == "QuickHalf")
        {
            ReplaceKeypadDecimal("50");
            return;
        }

        if (value == "QuickNinetyNine")
        {
            ReplaceKeypadDecimal("99");
            return;
        }

        if (value == "Back")
        {
            KeypadBuffer = KeypadBuffer.Length > 0 ? KeypadBuffer[..^1] : string.Empty;
            return;
        }

        if (value == "Clear")
        {
            KeypadBuffer = string.Empty;
            return;
        }

        if (string.IsNullOrEmpty(value) || value == "Enter" || value == "Space")
        {
            return;
        }

        if (value == ".")
        {
            if (!KeypadBuffer.Contains('.'))
            {
                KeypadBuffer = KeypadBuffer.Length == 0 ? "0." : KeypadBuffer + ".";
            }

            return;
        }

        if (value.Length != 1 || !char.IsDigit(value[0]) || HasTwoDecimalDigits(KeypadBuffer))
        {
            return;
        }

        KeypadBuffer += value;
    }

    private void ReplaceKeypadDecimal(string decimalDigits)
    {
        var wholePart = KeypadBuffer.Split('.')[0];
        KeypadBuffer = $"{(string.IsNullOrEmpty(wholePart) ? "0" : wholePart)}.{decimalDigits}";
    }

    private static bool HasTwoDecimalDigits(string value)
    {
        var decimalPointIndex = value.IndexOf('.', StringComparison.Ordinal);
        return decimalPointIndex >= 0 && value.Length - decimalPointIndex - 1 >= 2;
    }

    private void ToggleTouchKeyboard()
    {
        IsTouchKeyboardOpen = !IsTouchKeyboardOpen;
        if (IsTouchKeyboardOpen)
        {
            IsMatchesPopupOpen = false;
        }
    }

    private async Task SearchAndAddAsync()
    {
        var storeCode = Session.StoreCode;
        var scanText = ScanText;
        var cancellationSource = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _manualSearchCts, cancellationSource);
        var version = Interlocked.Increment(ref _manualSearchVersion);
        previous?.Cancel();
        try
        {
            await ExecuteScanAsync(
                _scanController.CreateManual(scanText),
                statusTextOverrideFactory: null,
                cancellationToken: cancellationSource.Token,
                isCurrent: () => !_disposed &&
                    version == Volatile.Read(ref _manualSearchVersion) &&
                    string.Equals(scanText, ScanText, StringComparison.Ordinal) &&
                    string.Equals(storeCode, Session.StoreCode, StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            // 手工输入已更新、清空或离开当前会话时，不应用旧搜索结果。
        }
        finally
        {
            Interlocked.CompareExchange(ref _manualSearchCts, null, cancellationSource);
            cancellationSource.Dispose();
        }
    }

    private async Task SearchAndAddSafeAsync()
    {
        try
        {
            await SearchAndAddAsync();
        }
        catch (Exception ex)
        {
            // 触摸键盘 Enter 不能 await，异常在后台任务中记录，避免未观察异常。
            ConsoleLog.Write("PosScan", $"manual async processing failed scanText={ScanText} error={ex.Message}");
        }
    }

    private bool CanAddOpenItem()
    {
        return decimal.TryParse(KeypadBuffer, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) &&
            value > 0m;
    }

    private async Task AddOpenItemAsync()
    {
        var keypadBufferSnapshot = KeypadBuffer;
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Sales.AddOpenItem, "add-open-item");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        ExecuteCartMutation("add-open-item", () => _workflowService.AddOpenItem(Session, keypadBufferSnapshot));
    }

    private async Task AddSelectedAsync()
    {
        var selectedItemSnapshot = SelectedItem;
        if (selectedItemSnapshot is null)
        {
            return;
        }

        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Sales.AddItem, "add-selected");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        ExecuteCartMutation("manual-add-selected", () => _workflowService.AddSelectedItem(
            Session,
            selectedItemSnapshot,
            clearScanText: false,
            closeMatchesPopup: false,
            operation: "manual-add-selected"));
    }

    private async Task SelectMatchAsync(SellableItemDto? item)
    {
        if (item is null)
        {
            return;
        }

        var itemSnapshot = item;
        SelectedItem = itemSnapshot;
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Sales.AddItem, "select-match");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        ExecuteCartMutation("manual-select-match", () => _workflowService.AddSelectedItem(
            Session,
            itemSnapshot,
            clearScanText: true,
            closeMatchesPopup: true,
            operation: "manual-select-match"));
    }

    /// <summary>
    /// 收银员在待选条里选定了某个候选商品：先过「加购」授权，再由工作流服务加购并让该待选出队。
    /// 选定的商品在此刻入车（追加末尾 / 合并到同商品行），审计带原扫码的 traceId。
    /// </summary>
    private async Task SelectPendingChoiceAsync(PendingScanChoice choice, SellableItemDto item)
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Sales.AddItem, "select-pending-choice");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        ExecuteCartMutation(
            "scan-choice-select",
            () => _workflowService.ResolvePendingChoice(Session, choice.Id, item),
            traceId: choice.TraceId);
    }

    private void AbandonPendingChoice(PendingScanChoice choice)
    {
        if (!_workflowService.AbandonPendingChoice(choice.Id, PendingChoiceReasons.Cashier))
        {
            return;
        }

        var remaining = _workflowService.PendingChoices?.Count ?? 0;
        SetStatusCore("pos.status.pendingChoiceAbandoned", [remaining], StatusFeedbackKind.Warning);
    }

    /// <summary>放弃全部待选（付款 / 挂单 / 取单 / 退出前的确认，或「全部放弃」）；提示放弃了几个。</summary>
    private void AbandonAllPendingChoices(string reason)
    {
        var abandoned = _workflowService.AbandonAllPendingChoices(reason);
        if (abandoned > 0)
        {
            SetStatusCore("pos.status.pendingChoiceAbandonedAll", [abandoned], StatusFeedbackKind.Warning);
        }
    }

    private async Task RemoveLineAsync(CartLine? line)
    {
        var lineSnapshot = line;
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Sales.RemoveLine, "remove-line");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        if (IsLastBoundSharedHeldOrderLine(lineSnapshot) &&
            !await TryReleaseSharedHeldOrderAsync())
        {
            return;
        }

        // owner-release 会原子清空已恢复购物车；兼容仅解绑的测试/替代实现时再走普通删行。
        if (lineSnapshot is not null && !_cart.Lines.Contains(lineSnapshot))
        {
            SetStatus("pos.status.ready");
            return;
        }

        ExecuteCartMutation("remove-line", () => _workflowService.RemoveLine(lineSnapshot));
    }

    private async Task IncreaseLineAsync(CartLine? line)
    {
        var lineSnapshot = line;
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Sales.ChangeQuantity, "increase-line");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        var stopwatch = Stopwatch.StartNew();
        var result = ExecuteCartMutation("increase-line", () => _workflowService.IncreaseLine(lineSnapshot));
        stopwatch.Stop();
        if (lineSnapshot is not null && string.Equals(result.StatusKey, "pos.status.ready", StringComparison.Ordinal))
        {
            LogCartOperation("increase-line", lineSnapshot, success: true, stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task DecreaseLineAsync(CartLine? line)
    {
        var lineSnapshot = line;
        if (lineSnapshot is null ||
            lineSnapshot.IsLocked ||
            lineSnapshot.Quantity <= 1m ||
            !_cart.Lines.Contains(lineSnapshot))
        {
            return;
        }

        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Sales.ChangeQuantity, "decrease-line");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        // 权限等待期间数量可能变化；到达下限后必须保留商品行且不能释放恢复挂单 claim。
        if (lineSnapshot.IsLocked ||
            lineSnapshot.Quantity <= 1m ||
            !_cart.Lines.Contains(lineSnapshot))
        {
            return;
        }

        ExecuteCartMutation("decrease-line", () => _workflowService.DecreaseLine(lineSnapshot));
    }

    /// <summary>
    /// 工作流给出了退货行拒绝提示时，直接按错误态展示（状态栏 + 错误音）并返回 true；
    /// 调用方据此在弹授权 / 主管 PIN 之前就返回，不为一个注定被拒绝的操作索要授权。
    /// </summary>
    private bool TryShowRejectionBeforeAuthorization(PosTerminalWorkflowResult? rejection)
    {
        if (rejection is null)
        {
            return false;
        }

        ApplyWorkflowResult(rejection);
        return true;
    }

    private async Task ModifySelectedLineQuantityAsync()
    {
        var lineSnapshot = SelectedCartLine;
        var keypadBufferSnapshot = KeypadBuffer;
        if (TryShowRejectionBeforeAuthorization(_workflowService.GetQuantityEditRejection(lineSnapshot)))
        {
            return;
        }

        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Sales.ChangeQuantity, "modify-quantity");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        ExecuteCartMutation("modify-quantity", () => _workflowService.ModifySelectedLineQuantity(lineSnapshot, keypadBufferSnapshot));
    }

    private async Task ModifySelectedLinePriceAsync()
    {
        var lineSnapshot = SelectedCartLine;
        var keypadBufferSnapshot = KeypadBuffer;
        if (TryShowRejectionBeforeAuthorization(_workflowService.GetPriceEditRejection(lineSnapshot)))
        {
            return;
        }

        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Sales.ChangePrice, "modify-price");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        ExecuteCartMutation("modify-price", () => _workflowService.ModifySelectedLinePrice(lineSnapshot, keypadBufferSnapshot));
    }

    /// <summary>
    /// 整单开关是一次性的：按下任一折扣键就用掉这一次，取走当前值并立即复位。
    /// 与后续成败无关——被拒绝（金额超限、没有可打折商品）、主管授权被取消或未通过，开关都已回到默认。
    /// </summary>
    private bool ConsumeWholeOrderOperation()
    {
        var snapshot = IsWholeOrderOperation;
        IsWholeOrderOperation = false;
        return snapshot;
    }

    private async Task ApplySelectedLineDiscountAmountAsync()
    {
        var isWholeOrderSnapshot = ConsumeWholeOrderOperation();
        var permissionCode = isWholeOrderSnapshot
            ? Permissions.PosTerminal.Sales.OrderManualDiscount
            : Permissions.PosTerminal.Sales.LineManualDiscount;
        var lineSnapshot = SelectedCartLine;
        var keypadBufferSnapshot = KeypadBuffer;
        // 行模式下选中退货行的折扣注定被拒绝：先提示，不弹授权 / 主管 PIN。
        // 整单模式不看选中行（整单折扣只作用于销售行、跳过退货行），工作流照常放行、弹授权；
        // 唯一例外是购物车里只有退货行、没有可打折的销售行，同样先提示、不弹授权。
        if (TryShowRejectionBeforeAuthorization(_workflowService.GetDiscountRejection(lineSnapshot, isWholeOrderSnapshot)))
        {
            return;
        }

        using var permissionGrant = await AuthorizeAsync(permissionCode, "manual-discount-amount");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        ExecuteCartMutation(
            "manual-discount-amount",
            () => _workflowService.ApplySelectedLineDiscountAmount(lineSnapshot, keypadBufferSnapshot, isWholeOrderSnapshot),
            isWholeOrderSnapshot);
    }

    private async Task ApplySelectedLineDiscountPercentAsync()
    {
        var isWholeOrderSnapshot = ConsumeWholeOrderOperation();
        var permissionCode = isWholeOrderSnapshot
            ? Permissions.PosTerminal.Sales.OrderManualDiscount
            : Permissions.PosTerminal.Sales.LineManualDiscount;
        var lineSnapshot = SelectedCartLine;
        var keypadBufferSnapshot = KeypadBuffer;
        if (TryShowRejectionBeforeAuthorization(_workflowService.GetDiscountRejection(lineSnapshot, isWholeOrderSnapshot)))
        {
            return;
        }

        using var permissionGrant = await AuthorizeAsync(permissionCode, "manual-discount-percent");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        ExecuteCartMutation(
            "manual-discount-percent",
            () => _workflowService.ApplySelectedLineDiscountPercent(lineSnapshot, keypadBufferSnapshot, isWholeOrderSnapshot),
            isWholeOrderSnapshot);
    }

    private async Task ApplyQuickDiscountPercentAsync(string? value)
    {
        var isWholeOrderSnapshot = ConsumeWholeOrderOperation();
        var permissionCode = GetQuickDiscountPermissionCode(value, isWholeOrderSnapshot);
        if (permissionCode is null)
        {
            // 快速入口固定为五个档位，禁止通过命令参数绕过逐档权限。
            SetStatusText(
                "快速折扣仅支持 10%、20%、30%、40%、50%",
                StatusFeedbackKind.Error,
                UserFeedbackCue.OperationError);
            return;
        }

        var lineSnapshot = SelectedCartLine;
        var valueSnapshot = value;
        if (TryShowRejectionBeforeAuthorization(_workflowService.GetDiscountRejection(lineSnapshot, isWholeOrderSnapshot)))
        {
            return;
        }

        using var permissionGrant = await AuthorizeAsync(permissionCode, "quick-discount-percent");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        ExecuteCartMutation(
            "quick-discount-percent",
            () => _workflowService.ApplyQuickDiscountPercent(lineSnapshot, valueSnapshot, isWholeOrderSnapshot),
            isWholeOrderSnapshot);
    }

    private static string? GetQuickDiscountPermissionCode(string? value, bool isWholeOrderOperation)
    {
        return (isWholeOrderOperation, value?.Trim()) switch
        {
            (false, "10") => Permissions.PosTerminal.Sales.LineQuickDiscount10Percent,
            (false, "20") => Permissions.PosTerminal.Sales.LineQuickDiscount20Percent,
            (false, "30") => Permissions.PosTerminal.Sales.LineQuickDiscount30Percent,
            (false, "40") => Permissions.PosTerminal.Sales.LineQuickDiscount40Percent,
            (false, "50") => Permissions.PosTerminal.Sales.LineQuickDiscount50Percent,
            (true, "10") => Permissions.PosTerminal.Sales.OrderQuickDiscount10Percent,
            (true, "20") => Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent,
            (true, "30") => Permissions.PosTerminal.Sales.OrderQuickDiscount30Percent,
            (true, "40") => Permissions.PosTerminal.Sales.OrderQuickDiscount40Percent,
            (true, "50") => Permissions.PosTerminal.Sales.OrderQuickDiscount50Percent,
            _ => null
        };
    }

    private void SelectCartLine(CartLine line)
    {
        if (ReferenceEquals(SelectedCartLine, line))
        {
            SelectedCartLine = null;
        }

        SelectedCartLine = line;
    }

    private void LogCartOperation(
        string operation,
        SellableItemDto item,
        bool success,
        long totalElapsedMs,
        string? reason = null,
        string? traceId = null)
    {
        LogCartPerf(
            $"{FormatTraceId(traceId)}operation={operation} storeCode={Session.StoreCode} productCode={LogValue(item.ProductCode)} lookupCode={LogValue(item.LookupCode)} success={FormatBool(success)} cartLines={_cart.Lines.Count} totalAmount={FormatAmount(_cart.TotalAmount)} actualAmount={FormatAmount(_cart.ActualAmount)} totalElapsedMs={totalElapsedMs}{FormatReason(reason)}");
    }

    private void LogCartOperation(
        string operation,
        CartLine? line,
        bool success,
        long totalElapsedMs,
        string? reason = null,
        string? traceId = null)
    {
        LogCartPerf(
            $"{FormatTraceId(traceId)}operation={operation} storeCode={Session.StoreCode} productCode={LogValue(line?.ProductCode)} lookupCode={LogValue(line?.LookupCode)} success={FormatBool(success)} cartLines={_cart.Lines.Count} totalAmount={FormatAmount(_cart.TotalAmount)} actualAmount={FormatAmount(_cart.ActualAmount)} totalElapsedMs={totalElapsedMs}{FormatReason(reason)}");
    }

    private static void LogCartPerf(string message)
    {
        ConsoleLog.Write("CartPerf", message);
    }

    private static string FormatBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string FormatAmount(decimal value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string FormatReason(string? reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? string.Empty : $" reason={reason.Trim()}";
    }

    private static string FormatTraceId(string? traceId)
    {
        return string.IsNullOrWhiteSpace(traceId) ? string.Empty : $"traceId={traceId} ";
    }

    private static string FormatElapsedSince(DateTimeOffset? startedAt)
    {
        return startedAt is null
            ? "<none>"
            : Math.Max(0, (DateTimeOffset.Now - startedAt.Value).TotalMilliseconds).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private void OnRawBarcodeScanned(RawBarcodeScannedEventArgs e)
    {
        ProcessScannerBarcode(e.Barcode, e.DevicePath, "raw", e.ScannedAt);
    }

    public bool ProcessScannerBarcode(string barcode, string devicePath, string source)
    {
        _ = ProcessScannerBarcodeSafeAsync(barcode, devicePath, source, null);
        return true;
    }

    private Task ProcessScannerBarcodeAsync(string barcode, string devicePath, string source, DateTimeOffset? scannedAt)
    {
        return ExecuteScanAsync(
            _scanController.CreateScanner(barcode, devicePath, source, scannedAt),
            result => FormatScannerResultStatus(barcode, result));
    }

    private void ProcessScannerBarcode(string barcode, string devicePath, string source, DateTimeOffset? scannedAt)
    {
        _ = ProcessScannerBarcodeSafeAsync(barcode, devicePath, source, scannedAt);
    }

    private async Task ProcessScannerBarcodeSafeAsync(string barcode, string devicePath, string source, DateTimeOffset? scannedAt)
    {
        try
        {
            if (EmergencyLoginTokenCodec.HasSupportedPrefix(barcode.Trim()) &&
                _tryLoginCashierFromScannerFallbackAsync is not null)
            {
                // 原始扫描枪也必须在商品工作流前分流，令牌不得触发本地或远程商品查询。
                await _tryLoginCashierFromScannerFallbackAsync(barcode, CancellationToken.None);
                return;
            }

            await ProcessScannerBarcodeAsync(barcode, devicePath, source, scannedAt);
        }
        catch (Exception ex)
        {
            // 扫描枪入口不能 await，异常必须在后台任务内记录，避免静默吞掉。
            ConsoleLog.Write(
                "PosScan",
                $"scanner async processing failed barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(barcode)} source={source} device={devicePath} error={ex.Message}");
        }
    }

    private void ClearSearch()
    {
        CancelManualSearch();
        ScanText = string.Empty;
        IsMatchesPopupOpen = false;
        IsTouchKeyboardOpen = false;
    }

    private async Task ClearCartAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Sales.ClearCart, "clear-cart");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        if (!await TryReleaseSharedHeldOrderAsync())
        {
            return;
        }

        ExecuteCartMutation("clear-cart", () => _workflowService.ClearCart());
        // 清空购物车即换单：这一单扫到、还没选的商品一并作废（日志留痕，避免静默丢）。
        _workflowService.AbandonAllPendingChoices(PendingChoiceReasons.ClearCart);
    }

    private bool IsLastBoundSharedHeldOrderLine(CartLine? line)
    {
        return line is not null &&
               _cart.Lines.Count == 1 &&
               ReferenceEquals(_cart.Lines[0], line) &&
               _cart.CreateSnapshot().SharedHeldOrderClaimId is not null;
    }

    private async Task<bool> TryReleaseSharedHeldOrderAsync()
    {
        if (_cart.CreateSnapshot().SharedHeldOrderClaimId is not Guid claimId)
        {
            return true;
        }

        if (_releaseSharedHeldOrderAsync is null)
        {
            SetStatus("pos.status.sharedHeldOrderReleaseFailed");
            return false;
        }

        try
        {
            await _releaseSharedHeldOrderAsync(claimId, Session, CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException
                and not StackOverflowException
                and not OperationCanceledException)
        {
            // 只记录 claim 标识与异常类型，禁止把 payload 或服务端响应写入日志。
            ConsoleLog.Write(
                "HeldOrderRelease",
                $"owner release failed claimId={claimId:D} error={exception.GetType().Name}");
            SetStatus("pos.status.sharedHeldOrderReleaseFailed");
            return false;
        }

        if (_cart.CreateSnapshot().SharedHeldOrderClaimId is not null)
        {
            SetStatus("pos.status.sharedHeldOrderReleaseFailed");
            return false;
        }

        return true;
    }

    private async Task OpenPaymentAsync()
    {
        // 补查期间收款会先等待，这段时间内重复点击不能把付款页打开两次。
        if (_isOpeningPayment)
        {
            return;
        }

        // 还有待选商品没处理：现在付款这些实物会漏收。先弹确认（返回处理 / 放弃后付款），放在授权之前，免得白弹一次授权。
        if (PendingChoices.TryBlock(PendingChoiceReasons.Pay, OpenPaymentAsync))
        {
            return;
        }

        _isOpeningPayment = true;
        try
        {
            using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Payment.View, "open-payment");
            if (permissionGrant is null)
            {
                return;
            }

            using var authorizationActivation = permissionGrant.Activate();
            // 刚扫的商品可能还在远程补查：先等在途补查结束（最多 2 秒，await 不阻塞 UI），商品加进购物车后再收款，
            // 否则带着缺商品的购物车进付款页，迟到的结果只能被丢弃，收银员却以为已经扫进去了。
            await WaitForPendingRemoteLookupsAsync();
            // 进入支付前等待本轮促销尝试结束，避免使用过期金额。
            await WaitForPendingPromotionEvaluationAsync();

            // 等待期间购物车可能被清空或页面已销毁：沿用「购物车为空不可收款」的规则，不进付款页。
            if (_disposed || _cart.IsEmpty)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var result = _workflowService.GuardPayment();
            ApplyWorkflowResult(result);
            if (!result.PaymentAllowed)
            {
                stopwatch.Stop();
                return;
            }

            PaymentRequested?.Invoke(this, EventArgs.Empty);
            _actions.OpenPayment?.Invoke();
            stopwatch.Stop();
            LogCartOperation("open-payment", (CartLine?)null, success: true, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            _isOpeningPayment = false;
        }
    }

    /// <summary>
    /// 等待点击收款时已在途的远程补查结束（加购、超时、未找到、被丢弃都算结束）。
    /// 只等点击那一刻已经发起的补查，之后才扫的商品不在范围内；最长等一个补查超时，
    /// 补查自身一旦卡死（回调不响应取消）也不会把收款拖住，超时后照常收款，迟到的结果按世代规则丢弃。
    /// </summary>
    private async Task WaitForPendingRemoteLookupsAsync()
    {
        Task[] pendingScans;
        lock (_pendingRemoteLookupScans)
        {
            if (_pendingRemoteLookupScans.Count == 0)
            {
                return;
            }

            pendingScans = _pendingRemoteLookupScans.Select(scan => scan.Task).ToArray();
        }

        var allCompleted = Task.WhenAll(pendingScans);
        if (allCompleted.IsCompleted)
        {
            return;
        }

        using var delayCancellation = new CancellationTokenSource();
        var budget = Task.Delay(PosTerminalWorkflowService.RemoteLookupTimeout, _timeProvider, delayCancellation.Token);
        var finished = await Task.WhenAny(allCompleted, budget);
        delayCancellation.Cancel();
        if (!ReferenceEquals(finished, allCompleted))
        {
            ConsoleLog.Write(
                "PosScan",
                $"open-payment wait for remote lookup timed out pendingScans={pendingScans.Length} timeoutMs={PosTerminalWorkflowService.RemoteLookupTimeout.TotalMilliseconds:0}");
        }
    }

    private TaskCompletionSource RegisterPendingRemoteLookupScan()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingRemoteLookupScans)
        {
            _pendingRemoteLookupScans.Add(completion);
        }

        return completion;
    }

    private void CompletePendingRemoteLookupScan(TaskCompletionSource? completion)
    {
        if (completion is null)
        {
            return;
        }

        lock (_pendingRemoteLookupScans)
        {
            _pendingRemoteLookupScans.Remove(completion);
        }

        completion.TrySetResult();
    }

    private async Task OpenReturnsAsync()
    {
        // 退货操作：设置里开启后，点侧栏「退款」进入退货页时，即使当前收银员自己有权限也要再扫一次员工条码确认；
        // 进入退货页之后的加行、确认退货（ReceiptReturnsViewModel 里各自的授权）不再重复扫码。
        var requireRescan = await IsReturnsRescanRequiredAsync();
        using var permissionGrant = await AuthorizeAsync(
            Permissions.PosTerminal.Returns.View,
            "open-returns",
            requireFreshScan: requireRescan);
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        // 退货页会往同一个购物车里加退货行：离开收银页前先作废在途补查，避免销售商品稍后混进退货单。
        _workflowService.AdvanceCartGeneration("open-returns");
        _actions.OpenReturns?.Invoke();
    }

    private async Task OpenSpecialProductsAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.SpecialProducts.View, "open-special-products");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        if (_actions.OpenSpecialProductsAsync is not null)
        {
            await _actions.OpenSpecialProductsAsync();
        }
    }

    private async Task HoldOrderAsync()
    {
        // 挂单保存的是此刻的购物车，待选商品不在里面：先让收银员处理或确认放弃。
        if (PendingChoices.TryBlock(PendingChoiceReasons.Hold, HoldOrderAsync))
        {
            return;
        }

        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Sales.HoldOrder, "hold-order");
        if (permissionGrant is null)
        {
            return;
        }

        // 挂单会持久化金额，必须先等当前促销重算完成。
        using var authorizationActivation = permissionGrant.Activate();
        // 挂单意味着这一单结束：一次性整单开关不带到下一单（购物车被清空时事件里也会复位，这里先显式收口）。
        IsWholeOrderOperation = false;
        await WaitForPendingPromotionEvaluationAsync();
        // 挂单保存的是此刻的购物车：此后才回来的补查结果不能再混进这一单（保存完购物车就被清空，商品会凭空丢失）。
        _workflowService.AdvanceCartGeneration("hold-order");

        if (_actions.HoldOrderAsync is not null)
        {
            var snapshot = OperationAuditEvents.CaptureCart(_cart.Lines);
            await RecordActionCallbackAsync(
                OperationAuditTypes.OrderHold,
                _actions.HoldOrderAsync,
                snapshot,
                "HOLD");
            // 挂单落库后立即重算首页「取单」角标：只读本机 + 缓存的共享汇总，不等远端；
            // 刷新失败只记日志，不影响挂单结果（刷新方法本身永不抛出）。
            LastHeldOrderCountRefreshTask = RefreshHeldOrderTodayCountAsync();
        }
    }

    private async Task RecallOrderAsync()
    {
        // 取单会换成另一单：待选商品属于当前这位顾客，不能悄悄带到别人的订单上。
        if (PendingChoices.TryBlock(PendingChoiceReasons.Recall, RecallOrderAsync))
        {
            return;
        }

        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Sales.RecallOrder, "recall-order");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        // 取单会把另一单恢复进购物车，属于换单：作废取单前发起的在途补查。
        _workflowService.AdvanceCartGeneration("recall-order");
        // 换单也要复位一次性整单开关，别让取单前点亮的「整单」落到恢复出来的那一单上。
        IsWholeOrderOperation = false;
        if (_actions.RecallOrderAsync is not null)
        {
            // 这里仅导航到暂存订单列表，真实取单成功由 TransactionHistoryViewModel 记录。
            await _actions.RecallOrderAsync();
        }
    }

    private async Task OpenHistoryAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.History.View, "open-history");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        if (_actions.OpenHistoryAsync is not null)
        {
            await _actions.OpenHistoryAsync();
        }
    }

    private async Task OpenCardRecoveryCenterAsync()
    {
        using var permissionGrant = await AuthorizeAsync(
            Permissions.PosTerminal.Payment.View,
            "open-card-recovery-center");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        if (_actions.OpenCardRecoveryCenterAsync is not null)
        {
            await _actions.OpenCardRecoveryCenterAsync();
        }
    }

    private async Task OpenDailyCloseAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.DailyClose.View, "open-daily-close");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        if (_actions.OpenDailyCloseAsync is not null)
        {
            await _actions.OpenDailyCloseAsync();
        }
    }

    private async Task OpenSettingsAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.View, "open-settings");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        if (_actions.OpenSettingsAsync is not null)
        {
            await _actions.OpenSettingsAsync();
        }
    }

    private async Task OpenCustomerDisplayAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.CustomerDisplay.Manage, "open-customer-display");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        _actions.OpenCustomerDisplay?.Invoke();
    }

    private async Task PrintLastReceiptAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Receipt.PrintLast, "print-last-receipt");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        if (_actions.PrintLastReceiptAsync is null)
        {
            return;
        }

        SetStatusText(T("receipt.print.printing"));
        var result = await _actions.PrintLastReceiptAsync();
        OperationAuditEvents.RecordAction(
            _operationAuditLogger,
            OperationAuditTypes.ReceiptReprint,
            result.Succeeded ? "Succeeded" : "Failed",
            Session,
            OperationAuditEvents.CaptureCart(_cart.Lines),
            reasonCode: "LAST_RECEIPT",
            safeMessage: result.Succeeded ? null : result.Message);
        SetStatusText(
            result.Message,
            result.Succeeded ? StatusFeedbackKind.Success : StatusFeedbackKind.Error,
            result.Succeeded ? null : UserFeedbackCue.OperationError);
    }

    /// <summary>
    /// 读设置页的「…需要再次扫码」开关（无交易开钱箱、退货共用）。没有设置来源时按「不需要」（旧行为）；
    /// 读取失败时按「需要」处理：这是安全类开关，不知道配置时宁可多扫一次码，也不能悄悄放过。
    /// </summary>
    private static async Task<bool> ReadRescanRequirementAsync(
        Func<CancellationToken, Task<bool>>? source,
        string settingName)
    {
        if (source is null)
        {
            return false;
        }

        try
        {
            return await source(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteError(
                "RescanSetting",
                $"read {settingName} rescan setting failed error={ex.GetType().Name}; requiring rescan",
                null,
                ex);
            return true;
        }
    }

    private Task<bool> IsNoSaleCashDrawerRescanRequiredAsync() =>
        ReadRescanRequirementAsync(_isNoSaleCashDrawerRescanRequiredAsync, "no-sale cash drawer");

    private Task<bool> IsReturnsRescanRequiredAsync() =>
        ReadRescanRequirementAsync(_isReturnsRescanRequiredAsync, "returns");

    private async Task OpenCashDrawerAsync()
    {
        // 无交易开钱箱（侧栏按钮）：设置里开启后，即使当前收银员自己有权限也要再扫一次员工条码确认。
        // 收现金后的自动开钱箱走 MainViewModel 的另一条路径，不受影响。
        var requireRescan = await IsNoSaleCashDrawerRescanRequiredAsync();
        using var permissionGrant = await AuthorizeAsync(
            Permissions.PosTerminal.CashDrawer.Open,
            "open-cash-drawer",
            requireFreshScan: requireRescan);
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        if (_actions.OpenCashDrawerAsync is null)
        {
            return;
        }

        SetStatusText(T("cashDrawer.opening"));
        var result = await _actions.OpenCashDrawerAsync();
        OperationAuditEvents.RecordAction(
            _operationAuditLogger,
            OperationAuditTypes.CashDrawerOpen,
            result.Succeeded ? "Succeeded" : "Failed",
            Session,
            OperationAuditEvents.CaptureCart(_cart.Lines),
            reasonCode: "MANUAL",
            safeMessage: result.Succeeded ? null : result.Message);
        SetStatusText(
            result.Succeeded ? T("cashDrawer.opened") : result.Message,
            result.Succeeded ? StatusFeedbackKind.Success : StatusFeedbackKind.Error,
            result.Succeeded ? null : UserFeedbackCue.OperationError);
    }

    private async Task RecordActionCallbackAsync(
        string operationType,
        Func<Task> callback,
        OperationAuditCartSnapshot snapshot,
        string reasonCode)
    {
        var correlation = OperationAuditEvents.CreateCorrelation();
        try
        {
            await callback();
            var after = OperationAuditEvents.CaptureCart(_cart.Lines);
            if (!OperationAuditEvents.HasChanged(snapshot, after))
            {
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    operationType,
                    "Failed",
                    Session,
                    snapshot,
                    reasonCode: $"{reasonCode}_NO_STATE_CHANGE",
                    safeMessage: "NO_STATE_CHANGE",
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
                return;
            }

            OperationAuditEvents.RecordCartChange(
                _operationAuditLogger,
                operationType,
                Session,
                snapshot,
                after,
                reasonCode: reasonCode,
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
        }
        catch (Exception ex)
        {
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                operationType,
                "Failed",
                Session,
                snapshot,
                reasonCode: reasonCode,
                safeMessage: ex.GetType().Name,
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            ConsoleLog.WriteError(
                "OperationAudit",
                $"operation failed operation={operationType} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            throw;
        }
    }

    private async Task ExitApplicationAsync()
    {
        // 退出应用会丢弃内存里的待选：先确认。
        if (PendingChoices.TryBlock(PendingChoiceReasons.Exit, ExitApplicationAsync))
        {
            return;
        }

        if (_actions.ExitApplicationAsync is not null)
        {
            await _actions.ExitApplicationAsync();
        }
    }

    private async Task LockCashierAsync()
    {
        // 锁屏后可能由同一位收银员解锁（会话身份不变，不会触发 OnSessionChanged 的复位），这里显式复位。
        IsWholeOrderOperation = false;
        if (_actions.LockCashierAsync is not null)
        {
            await _actions.LockCashierAsync();
        }
    }

    private async Task ReregisterDeviceAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.DeviceRegistration, "reregister-device");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        if (_actions.ReregisterDeviceAsync is not null)
        {
            await _actions.ReregisterDeviceAsync();
        }
    }

    private async Task ExecuteScanAsync(
        PosTerminalScanPlan plan,
        Func<PosTerminalWorkflowResult, string?>? statusTextOverrideFactory,
        CancellationToken cancellationToken = default,
        Func<bool>? isCurrent = null)
    {
        // 中文注释：统一承接手动检索与扫描枪入口，只让 VM 负责 UI 状态投影和结果应用。
        // 任何扫码都代表开始下一单，先收起上一单的成功卡片。
        DismissLastSale();
        var totalStopwatch = Stopwatch.StartNew();
        _scanController.LogStarted(plan, Session.StoreCode);

        if (plan.ApplyBarcodeToScanText)
        {
            var setScanTextStopwatch = Stopwatch.StartNew();
            ScanText = plan.Barcode;
            if (plan.CloseTouchKeyboard)
            {
                IsTouchKeyboardOpen = false;
            }

            setScanTextStopwatch.Stop();
            _scanController.LogInputApplied(plan, setScanTextStopwatch.ElapsedMilliseconds);
        }

        _activeScanTraceId = plan.TraceId;
        _activeScanStartedAt = plan.StartedAt;
        var workflowStopwatch = Stopwatch.StartNew();
        PosTerminalWorkflowResult result;
        var applyStopwatch = new Stopwatch();
        // 补查发起时登记，整条扫码链路结束（含 finally）才完成；点击收款时据此等待在途补查。
        TaskCompletionSource? remoteLookupCompletion = null;
        try
        {
            var hasCashierSession = _cashierSessionContext.CurrentSession is not null || Session.CashierSession is not null;
            // 关键逻辑：只有未登录时才把扫码解释为收银员登录；已登录后的权限不足统一进入单次授权，绝不切换当前收银员。
            var hasAddItemPermission =
                (!_enforcePermissions && !hasCashierSession) ||
                _cashierSessionContext.HasPermission(Permissions.PosTerminal.Sales.AddItem);
            if (!hasAddItemPermission &&
                !hasCashierSession &&
                await TryApplyCashierLoginFallbackAsync(plan, result: null, cancellationToken))
            {
                workflowStopwatch.Stop();
                totalStopwatch.Stop();
                _scanController.LogFinished(
                    plan,
                    "cashier-login",
                    false,
                    _cart.Lines.Count,
                    workflowStopwatch.ElapsedMilliseconds,
                    0,
                    totalStopwatch.ElapsedMilliseconds);
                return;
            }

            using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Sales.AddItem, "scan-add-item", cancellationToken);
            if (permissionGrant is null)
            {
                return;
            }
            using var authorizationActivation = permissionGrant.Activate();

            var auditBefore = OperationAuditEvents.CaptureCart(_cart.Lines);
            var previousSequence = _cartChangedSequence;
            var remoteLookupDispatched = false;
            var remoteLookupApplied = false;
            var scanHooks = new PosScanHooks
            {
                // 本地未命中后远程补查最长要等 2 秒：发起的同一时刻就切换状态栏并给一次受理提示音，
                // 不然收银员看不到任何反应，多半会再扫一次。
                OnInterimResult = interimResult =>
                {
                    remoteLookupDispatched = true;
                    remoteLookupCompletion ??= RegisterPendingRemoteLookupScan();
                    if (isCurrent is not null && !isCurrent())
                    {
                        return;
                    }

                    ApplyWorkflowResult(interimResult, statusTextOverrideFactory?.Invoke(interimResult));
                },
                // 补查结果回来时购物车可能已被别的扫码改动过：审计前快照与促销排队基线必须取在这一次加购之前，
                // 否则前后快照会把等待期间其它扫码的商品一并记进这一条审计。
                OnBeforeRemoteApply = () =>
                {
                    remoteLookupApplied = true;
                    auditBefore = OperationAuditEvents.CaptureCart(_cart.Lines);
                    previousSequence = _cartChangedSequence;
                    // 等待期间别的扫码可能已把当前扫码的 trace 信息清掉，补回来让后续购物车日志仍能串起这次扫码。
                    _activeScanTraceId = plan.TraceId;
                    _activeScanStartedAt = plan.StartedAt;
                }
            };
            result = await _workflowService.ProcessScanAsync(
                Session,
                plan.Barcode,
                plan.PreferExactLookup,
                plan.Source,
                plan.TraceId,
                scanHooks,
                cancellationToken);
            workflowStopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();
            if (isCurrent is not null && !isCurrent())
            {
                return;
            }
            if (!hasCashierSession &&
                await TryApplyCashierLoginFallbackAsync(plan, result, cancellationToken))
            {
                totalStopwatch.Stop();
                _scanController.LogFinished(
                    plan,
                    "cashier-login",
                    false,
                    _cart.Lines.Count,
                    workflowStopwatch.ElapsedMilliseconds,
                    0,
                    totalStopwatch.ElapsedMilliseconds);
                return;
            }

            applyStopwatch.Start();
            ApplyWorkflowResult(result, statusTextOverrideFactory?.Invoke(result));
            if (result.ScanConflictHandled)
            {
                // 多商品码已记入待选队列（或队列已满被拒收）：条码处理完了，别留在搜索框里被下一次手工输入拼接。
                ScanText = string.Empty;
            }

            applyStopwatch.Stop();
            // 远程补查已发起却没有加购（超时、未找到、结果被丢弃）时，这次扫码并没有改动购物车：
            // 等待期间购物车的变化属于别的操作，不能触发促销排队，也不能记成这次扫码的审计。
            if (!remoteLookupDispatched || remoteLookupApplied)
            {
                QueuePromotionEvaluationIfCartChanged(previousSequence, "scan");
                var auditAfter = OperationAuditEvents.CaptureCart(_cart.Lines);
                if (OperationAuditEvents.HasChanged(auditBefore, auditAfter))
                {
                    OperationAuditEvents.RecordCartChange(
                        _operationAuditLogger,
                        OperationAuditTypes.CartItemAdd,
                        Session,
                        auditBefore,
                        auditAfter,
                        reasonCode: "SCAN",
                        traceId: plan.TraceId);
                }
            }
        }
        finally
        {
            _activeScanTraceId = null;
            _activeScanStartedAt = null;
            // 加购、促销排队、审计都已处理完（或扫码被取消 / 出错），可以放行等待中的收款。
            CompletePendingRemoteLookupScan(remoteLookupCompletion);
        }

        totalStopwatch.Stop();
        if (result.SelectedCartLine is not null && string.Equals(result.StatusKey, "pos.status.added", StringComparison.Ordinal))
        {
            LogCartOperation("scan-auto-add", result.SelectedCartLine, success: true, totalStopwatch.ElapsedMilliseconds, traceId: plan.TraceId);
        }

        _scanController.LogFinished(
            plan,
            result.StatusKey,
            result.SelectedCartLine is not null,
            _cart.Lines.Count,
            workflowStopwatch.ElapsedMilliseconds,
            applyStopwatch.ElapsedMilliseconds,
            totalStopwatch.ElapsedMilliseconds);
    }

    private async Task<bool> TryApplyCashierLoginFallbackAsync(
        PosTerminalScanPlan plan,
        PosTerminalWorkflowResult? result,
        CancellationToken cancellationToken)
    {
        if (_tryLoginCashierFromScannerFallbackAsync is null ||
            (result is not null && !IsCatalogMiss(result)))
        {
            return false;
        }

        var loggedIn = await _tryLoginCashierFromScannerFallbackAsync(plan.Barcode, cancellationToken);
        if (!loggedIn)
        {
            return false;
        }

        // 关键逻辑：未登录时允许扫码先进收银员登录；已能查商品时只在商品明确 miss 后解释为收银员条码。
        Matches.Clear();
        SelectedItem = null;
        IsMatchesPopupOpen = false;
        IsTouchKeyboardOpen = false;
        ScanText = string.Empty;
        // 未注入本地化服务时保留旧中文提示；正常路径缓存资源键以支持运行时切换语言。
        if (_localization is null)
        {
            SetStatusText("收银员登录成功", StatusFeedbackKind.Success);
        }
        else
        {
            SetStatusCore("shell.cashierLogin.status.success", [], StatusFeedbackKind.Success);
        }
        ConsoleLog.Write(
            "CashierLogin",
            $"scanner fallback cashier login succeeded source={plan.Source} device={plan.DevicePath} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(plan.Barcode)}");
        return true;
    }

    private static bool IsCatalogMiss(PosTerminalWorkflowResult result)
    {
        return string.Equals(result.StatusKey, "pos.status.noLocalMatch", StringComparison.Ordinal) &&
            result.Matches is not null &&
            result.Matches.Count == 0;
    }

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.System.Sync, "sync", cancellationToken);
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await RunCatalogDownloadAsync(
            _syncCatalogAsync,
            T("pos.catalogSync.starting", "Syncing catalog..."),
            T("pos.catalogSync.completed", "Catalog sync completed."),
            T("pos.catalogSync.failed", "Catalog sync failed"),
            cancellationToken);
    }

    private async Task ResetCatalogAsync(CancellationToken cancellationToken)
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.CatalogReset, "reset-catalog", cancellationToken);
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await RunCatalogDownloadAsync(
            _resetCatalogAsync,
            T("pos.catalogReset.starting", "Resetting catalog..."),
            T("pos.catalogReset.completed", "Catalog reset completed."),
            T("pos.catalogReset.failed", "Catalog reset failed"),
            cancellationToken);
    }

    private async Task RunCatalogDownloadAsync(
        Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? downloadCatalogAsync,
        string startingMessage,
        string completedMessage,
        string failedPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_refreshOnlineAsync is not null)
            {
                var isOnline = await _refreshOnlineAsync(cancellationToken);
                Session = Session with { IsOnline = isOnline };
            }

            if (!Session.IsOnline)
            {
                SetStatusText(T("pos.status.catalogSyncSkipped", "Offline: catalog sync skipped."));
                return;
            }

            if (downloadCatalogAsync is null)
            {
                SetStatus("pos.status.ready");
                return;
            }

            SetStatusText(startingMessage);
            await downloadCatalogAsync(cancellationToken);
            RefreshMatches(requireOpenNonEmptySearch: true);
            SetStatusText(completedMessage);
        }
        catch (OperationCanceledException)
        {
            // 取消后恢复可操作状态，避免界面永久停留在“正在同步”。
            SetStatus("pos.status.ready");
        }
        catch (Exception ex)
        {
            SetStatusText($"{failedPrefix}: {ex.Message}", StatusFeedbackKind.Error, UserFeedbackCue.OperationError);
        }
    }

    private void RefreshMatches(bool requireOpenNonEmptySearch = false)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = RefreshMatchesOnDispatcherSafeAsync(dispatcher, requireOpenNonEmptySearch);
            return;
        }

        if (_disposed ||
            (requireOpenNonEmptySearch && (!IsMatchesPopupOpen || string.IsNullOrWhiteSpace(ScanText))))
        {
            return;
        }

        // 中文注释：这些 UI 状态只在 Dispatcher 上读取，后台搜索仅持有不可变快照。
        var storeCode = Session.StoreCode;
        var query = ScanText;
        var popupOpen = IsMatchesPopupOpen;
        var cancellationSource = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _matchesRefreshCts, cancellationSource);
        var version = Interlocked.Increment(ref _matchesRefreshVersion);
        previous?.Cancel();
        _ = RefreshMatchesSafeAsync(
            version,
            storeCode,
            query,
            popupOpen,
            cancellationSource,
            cancellationSource.Token);
    }

    private async Task RefreshMatchesOnDispatcherSafeAsync(
        System.Windows.Threading.Dispatcher dispatcher,
        bool requireOpenNonEmptySearch)
    {
        try
        {
            await dispatcher.InvokeAsync(
                () => RefreshMatches(requireOpenNonEmptySearch),
                System.Windows.Threading.DispatcherPriority.DataBind).Task;
        }
        catch (TaskCanceledException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            // 应用退出时不再排队新的目录刷新。
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("CatalogSearch", $"dispatcher refresh failed error={ex.Message}");
        }
    }

    private async Task RefreshMatchesSafeAsync(
        long version,
        string? storeCode,
        string query,
        bool popupOpen,
        CancellationTokenSource cancellationSource,
        CancellationToken cancellationToken)
    {
        try
        {
            var matches = string.IsNullOrWhiteSpace(query)
                ? await Task.Run(() => _priceIndex.GetFirst(storeCode, take: 8), cancellationToken)
                : await Task.Run(() => _searchCatalogMatches(storeCode, query, cancellationToken), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            await ApplyMatchesOnDispatcherAsync(version, storeCode, query, popupOpen, matches, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            // 新搜索、关闭面板或释放 VM 时的预期取消。
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("CatalogSearch", $"match refresh failed error={ex.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _matchesRefreshCts, null, cancellationSource);
            cancellationSource.Dispose();
        }
    }

    private async Task ApplyMatchesOnDispatcherAsync(
        long version,
        string? storeCode,
        string query,
        bool popupOpen,
        IReadOnlyList<SellableItemDto> matches,
        CancellationToken cancellationToken)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyMatchesIfCurrent(version, storeCode, query, popupOpen, matches);
            return;
        }

        await dispatcher.InvokeAsync(
            () => ApplyMatchesIfCurrent(version, storeCode, query, popupOpen, matches),
            System.Windows.Threading.DispatcherPriority.DataBind,
            cancellationToken).Task;
    }

    private void ApplyMatchesIfCurrent(
        long version,
        string? storeCode,
        string query,
        bool popupOpen,
        IReadOnlyList<SellableItemDto> matches)
    {
        if (_disposed ||
            version != Volatile.Read(ref _matchesRefreshVersion) ||
            !string.Equals(query, ScanText, StringComparison.Ordinal) ||
            !string.Equals(storeCode, Session.StoreCode, StringComparison.Ordinal) ||
            popupOpen != IsMatchesPopupOpen)
        {
            return;
        }

        Matches.ReplaceWith(matches);
        SelectedItem = Matches.FirstOrDefault();
    }

    private void CancelMatchesRefresh()
    {
        Interlocked.Increment(ref _matchesRefreshVersion);
        var cancellationSource = Interlocked.Exchange(ref _matchesRefreshCts, null);
        cancellationSource?.Cancel();
    }

    private void CancelManualSearch()
    {
        Interlocked.Increment(ref _manualSearchVersion);
        var cancellationSource = Interlocked.Exchange(ref _manualSearchCts, null);
        cancellationSource?.Cancel();
    }

    private void ApplyWorkflowResult(PosTerminalWorkflowResult result, string? statusTextOverride = null)
    {
        if (result.MatchesPopupOpen is bool matchesPopupOpen)
        {
            IsMatchesPopupOpen = matchesPopupOpen;
        }

        if (result.Matches is not null)
        {
            // 工作流结果是本次扫码的权威快照，取消打开弹窗时触发的通用搜索，避免异步结果覆盖它。
            CancelMatchesRefresh();
            Matches.ReplaceWith(result.Matches);
            SelectedItem = result.SelectedItem;
        }

        if (result.TouchKeyboardOpen is bool touchKeyboardOpen)
        {
            IsTouchKeyboardOpen = touchKeyboardOpen;
        }

        if (result.WholeOrderOperation is bool wholeOrderOperation)
        {
            IsWholeOrderOperation = wholeOrderOperation;
        }

        if (result.ClearScanText)
        {
            ScanText = string.Empty;
        }

        if (result.ClearKeypadBuffer)
        {
            KeypadBuffer = string.Empty;
        }

        if (result.SelectedCartLine is not null)
        {
            SelectCartLine(result.SelectedCartLine);
        }

        if (!string.IsNullOrWhiteSpace(result.StatusKey))
        {
            var (feedbackKind, cue) = ClassifyStatusFeedback(result.StatusKey!);
            if (!string.IsNullOrWhiteSpace(statusTextOverride))
            {
                SetStatusText(statusTextOverride!, feedbackKind, cue);
            }
            else
            {
                SetStatusCore(result.StatusKey!, result.StatusArgs, feedbackKind, cue);
            }
        }
        else if (!string.IsNullOrWhiteSpace(statusTextOverride))
        {
            SetStatusText(statusTextOverride!, StatusFeedbackKind.Neutral);
        }
    }

    private string FormatScannerResultStatus(string barcode, PosTerminalWorkflowResult result)
    {
        var resultText = string.IsNullOrWhiteSpace(result.StatusKey)
            ? StatusMessage
            : Format(result.StatusKey!, result.StatusArgs);

        var template = T("pos.status.scannerResult");
        if (string.Equals(template, "pos.status.scannerResult", StringComparison.Ordinal))
        {
            template = "Scan {0}: {1}";
        }

        return string.Format(
            _localization?.CurrentCulture ?? CultureInfo.CurrentCulture,
            template,
            barcode,
            resultText);
    }

    private void SetStatus(string key, params object[] args)
    {
        SetStatusCore(key, args, StatusFeedbackKind.Neutral);
    }

    private void SetStatusCore(
        string key,
        object[] args,
        StatusFeedbackKind feedbackKind = StatusFeedbackKind.Neutral,
        UserFeedbackCue? cue = null)
    {
        _statusText = null;
        _statusKey = key;
        _statusArgs = args;
        ApplyStatusFeedback(feedbackKind, cue);
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void SetStatusText(
        string message,
        StatusFeedbackKind feedbackKind = StatusFeedbackKind.Neutral,
        UserFeedbackCue? cue = null)
    {
        _statusText = message;
        ApplyStatusFeedback(feedbackKind, cue);
        OnPropertyChanged(nameof(StatusMessage));
    }

    private bool TryRequirePermission(string permissionCode)
    {
        if ((!_enforcePermissions && _cashierSessionContext.CurrentSession is null && Session.CashierSession is null) ||
            _cashierSessionContext.RequirePermission(permissionCode, out var message))
        {
            return true;
        }

        // 中文注释：命令执行前二次校验，避免只靠按钮可用状态保护高风险操作。
        var deniedOperationType = GetDeniedAuditOperationType(permissionCode);
        if (deniedOperationType is not null)
        {
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                deniedOperationType,
                "Denied",
                Session,
                OperationAuditEvents.CaptureCart(_cart.Lines),
                reasonCode: "PERMISSION_DENIED",
                safeMessage: message);
        }

        SetStatusText(message, StatusFeedbackKind.Error, UserFeedbackCue.OperationError);
        return false;
    }

    private Task<ViewModelAuthorizationGrant?> AuthorizeAsync(
        string permissionCode,
        string action,
        CancellationToken cancellationToken = default,
        bool requireFreshScan = false) =>
        ViewModelOperationAuthorization.AuthorizeAsync(
            _operationAuthorizationService,
            TryRequirePermission,
            permissionCode,
            "pos-terminal",
            action,
            Session,
            cancellationToken,
            requireFreshScan);

    private static string? GetDeniedAuditOperationType(string permissionCode)
    {
        return permissionCode switch
        {
            Permissions.PosTerminal.Sales.AddItem or Permissions.PosTerminal.Sales.AddOpenItem => OperationAuditTypes.CartItemAdd,
            Permissions.PosTerminal.Sales.RemoveLine => OperationAuditTypes.CartItemRemove,
            Permissions.PosTerminal.Sales.ChangeQuantity => OperationAuditTypes.CartItemQuantityChange,
            Permissions.PosTerminal.Sales.ChangePrice => OperationAuditTypes.CartItemPriceChange,
            Permissions.PosTerminal.Sales.LineDiscount or
            Permissions.PosTerminal.Sales.LineManualDiscount or
            Permissions.PosTerminal.Sales.LineQuickDiscount10Percent or
            Permissions.PosTerminal.Sales.LineQuickDiscount20Percent or
            Permissions.PosTerminal.Sales.LineQuickDiscount30Percent or
            Permissions.PosTerminal.Sales.LineQuickDiscount40Percent or
            Permissions.PosTerminal.Sales.LineQuickDiscount50Percent => OperationAuditTypes.CartLineDiscountChange,
            Permissions.PosTerminal.Sales.OrderDiscount or
            Permissions.PosTerminal.Sales.OrderManualDiscount or
            Permissions.PosTerminal.Sales.OrderQuickDiscount10Percent or
            Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent or
            Permissions.PosTerminal.Sales.OrderQuickDiscount30Percent or
            Permissions.PosTerminal.Sales.OrderQuickDiscount40Percent or
            Permissions.PosTerminal.Sales.OrderQuickDiscount50Percent => OperationAuditTypes.CartOrderDiscountChange,
            Permissions.PosTerminal.Sales.ClearCart => OperationAuditTypes.CartClear,
            Permissions.PosTerminal.Sales.HoldOrder => OperationAuditTypes.OrderHold,
            Permissions.PosTerminal.Sales.RecallOrder => OperationAuditTypes.OrderRecall,
            Permissions.PosTerminal.CashDrawer.Open => OperationAuditTypes.CashDrawerOpen,
            Permissions.PosTerminal.Receipt.PrintLast => OperationAuditTypes.ReceiptReprint,
            _ => null
        };
    }

    private void ApplyStatusFeedback(StatusFeedbackKind feedbackKind, UserFeedbackCue? cue)
    {
        StatusFeedbackKind = feedbackKind;
        StatusPulseToken++;
        if (cue is UserFeedbackCue feedbackCue)
        {
            _userFeedbackService.Play(feedbackCue);
        }
    }

    private static (StatusFeedbackKind Kind, UserFeedbackCue? Cue) ClassifyStatusFeedback(string statusKey)
    {
        return statusKey switch
        {
            "pos.status.added" => (StatusFeedbackKind.Success, UserFeedbackCue.ScanAdded),
            "pos.status.multipleMatches" => (StatusFeedbackKind.Warning, UserFeedbackCue.ScanMultipleMatches),
            // 多商品码记入待选：与旧弹窗同一提示音（提醒收银员这里有个选择要做）；选定入车沿用「已加入」成功音。
            "pos.status.pendingChoiceQueued" => (StatusFeedbackKind.Warning, UserFeedbackCue.ScanMultipleMatches),
            "pos.status.pendingChoiceResolved" => (StatusFeedbackKind.Success, UserFeedbackCue.ScanAdded),
            "pos.status.pendingChoiceResolvedLast" => (StatusFeedbackKind.Success, UserFeedbackCue.ScanAdded),
            // 队列已满：这次扫码没有被记录，必须用错误音让收银员注意到。
            "pos.status.pendingChoiceFull" => (StatusFeedbackKind.Error, UserFeedbackCue.OperationError),
            "pos.status.pendingChoiceGone" => (StatusFeedbackKind.Warning, null),
            "pos.status.pendingChoiceAbandoned" => (StatusFeedbackKind.Warning, null),
            "pos.status.noLocalMatch" => (StatusFeedbackKind.Error, UserFeedbackCue.ScanNoMatch),
            // 远程补查开始：沿用现有的按键点击音作为「扫码已受理」，不新增音效；加购成功时仍会播 ScanAdded。
            "pos.status.remoteLookupStarted" => (StatusFeedbackKind.Neutral, UserFeedbackCue.ButtonClick),
            // 同码重复扫码只提示，不再响一次，避免让收银员误以为又加了一件。
            "pos.status.remoteLookupInProgress" => (StatusFeedbackKind.Warning, null),
            // 结果被丢弃意味着这件商品没有进购物车，需要收银员重扫，用错误音提醒。
            "pos.status.remoteLookupDiscarded" => (StatusFeedbackKind.Warning, UserFeedbackCue.OperationError),
            "cart.status.zeroPriceItem" => (StatusFeedbackKind.Error, UserFeedbackCue.OperationError),
            "cart.status.quantityMustBeInteger" => (StatusFeedbackKind.Error, UserFeedbackCue.OperationError),
            "pos.status.sharedHeldOrderReleaseFailed" => (StatusFeedbackKind.Error, UserFeedbackCue.OperationError),
            "pos.status.invalidKeypadValue" => (StatusFeedbackKind.Error, UserFeedbackCue.OperationError),
            "pos.status.selectCartLine" => (StatusFeedbackKind.Error, UserFeedbackCue.OperationError),
            "pos.status.discountAmountTooHigh" => (StatusFeedbackKind.Error, UserFeedbackCue.OperationError),
            "pos.status.discountPercentOutOfRange" => (StatusFeedbackKind.Error, UserFeedbackCue.OperationError),
            "pos.status.quantityMustBePositive" => (StatusFeedbackKind.Error, UserFeedbackCue.OperationError),
            // 退货行的折扣 / 数量 / 价格被拒绝：沿用其它校验失败的错误态与错误音，不新增音效。
            "pos.status.returnLineDiscountBlocked" => (StatusFeedbackKind.Error, UserFeedbackCue.OperationError),
            "pos.status.orderDiscountNoSaleLines" => (StatusFeedbackKind.Error, UserFeedbackCue.OperationError),
            "pos.status.returnLineQuantityBlocked" => (StatusFeedbackKind.Error, UserFeedbackCue.OperationError),
            "pos.status.returnLinePriceBlocked" => (StatusFeedbackKind.Error, UserFeedbackCue.OperationError),
            _ => (StatusFeedbackKind.Neutral, null)
        };
    }

    private string T(string key)
    {
        return _localization?.T(key) ?? key;
    }

    private string T(string key, string fallback)
    {
        var value = _localization?.T(key);
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }

    private string Format(string key, params object[] args)
    {
        var template = T(key);
        return args.Length == 0
            ? template
            : string.Format(_localization?.CurrentCulture ?? System.Globalization.CultureInfo.CurrentCulture, template, args);
    }

    private void RaiseLocalizedProperties()
    {
        OnPropertyChanged(nameof(ScreenTitleText));
        OnPropertyChanged(nameof(SearchPlaceholderText));
        OnPropertyChanged(nameof(SearchButtonText));
        OnPropertyChanged(nameof(AddSelectedText));
        OnPropertyChanged(nameof(CartTitleText));
        OnPropertyChanged(nameof(TotalsLabelText));
        OnPropertyChanged(nameof(PayNowText));
        OnPropertyChanged(nameof(ClearCartText));
        OnPropertyChanged(nameof(HoldOrderText));
        OnPropertyChanged(nameof(RecallOrderText));
        OnPropertyChanged(nameof(HistoryText));
        OnPropertyChanged(nameof(DailyCloseText));
        OnPropertyChanged(nameof(SettingsText));
        OnPropertyChanged(nameof(CustomerDisplayText));
        OnPropertyChanged(nameof(PrintLastReceiptText));
        OnPropertyChanged(nameof(OpenCashDrawerText));
        OnPropertyChanged(nameof(LockCashierText));
        OnPropertyChanged(nameof(ExitApplicationText));
        OnPropertyChanged(nameof(MemberText));
        OnPropertyChanged(nameof(SyncText));
        OnPropertyChanged(nameof(CatalogResetText));
        OnPropertyChanged(nameof(ReregisterDeviceText));
        OnPropertyChanged(nameof(OnlineText));
        OnPropertyChanged(nameof(PendingSyncText));
        OnPropertyChanged(nameof(CardRecoveryCenterText));
        OnPropertyChanged(nameof(CardRecoveryOpenText));
        OnPropertyChanged(nameof(CardRecoveryCenterAutomationName));
        OnPropertyChanged(nameof(StatusMessage));
    }
}
