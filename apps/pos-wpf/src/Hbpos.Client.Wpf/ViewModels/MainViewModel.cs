using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Security;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Converters;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.Facades;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed record DeviceReregistrationStartResult(bool Started, string StatusMessage)
{
    public static DeviceReregistrationStartResult StartedWith(string statusMessage) => new(true, statusMessage);

    public static DeviceReregistrationStartResult Blocked(string statusMessage) => new(false, statusMessage);
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const string DefaultTestStoreCode = "1002";

    private readonly LocalSellableItemIndex _priceIndex;
    private readonly PosCartService _cart;
    private readonly CashCheckoutService _checkout;
    private readonly ILocalSchemaService _schema;
    private readonly IPosCoreServices _core;
    private readonly IPaymentTerminalFacade _paymentTerminal;
    private readonly IPrintFacade _print;
    private readonly IPosInfrastructureFacade _infra;
    private readonly ILocalCatalogRepository _catalogRepository;
    private readonly IShellCultureService _shellCultureService;
    private readonly IShellCatalogService _shellCatalogService;
    private readonly IRemoteLookupRefreshService _remoteLookupRefresh;
    private readonly ISpecialProductService _specialProductService;
    private readonly IConnectivityApiClient _connectivityApiClient;
    private readonly IMainShellStartupService _mainShellStartupService;
    private readonly ILocalOrderRepository _orderRepository;
    private readonly IShellSyncCenterService _shellSyncCenterService;
    private readonly IOrderUploadExecutionService _orderUploadExecutionService;
    private readonly ILocalizationService _localization;
    private readonly ICustomerDisplayOrchestrator _customerDisplayOrchestrator;
    private readonly IRawScannerService _rawScannerService;
    private readonly IUserFeedbackService _userFeedbackService;
    private readonly IReceiptQueryService _receiptQueryService;
    private readonly IReceiptPrintService _receiptPrintService;
    private readonly IReceiptPrinterSettingsStore? _receiptPrinterSettingsStore;
    private readonly IReceiptTextFormatter _receiptTextFormatter;
    private readonly IStoreReceiptProfileApiClient? _storeReceiptProfileApiClient;
    private readonly ILinklyBankReceiptPrinter? _linklyBankReceiptPrinter;
    private readonly IInstallmentOrderService _installmentOrderService;
    private readonly ISuspendedOrderService? _suspendedOrderService;
    private readonly IRemoteOrderHistoryService? _remoteOrderHistoryService;
    private readonly ICashPaymentWorkflowService _cashPaymentWorkflowService;
    private readonly IVoucherApiClient? _voucherApiClient;
    private readonly ICardTerminalClient? _cardTerminalClient;
    private readonly ICardTerminalSetupService? _cardTerminalSetupService;
    private readonly IDeviceRegistrationWorkflowService _deviceRegistrationWorkflowService;
    private readonly ISpecialProductsWorkflowService _specialProductsWorkflowService;
    private readonly IReceiptReturnsWorkflowService _receiptReturnsWorkflowService;
    private readonly IPromotionEvaluationService? _promotionEvaluationService;
    private readonly IDailyCloseService _dailyCloseService;
    private readonly IDailyClosePrintService _dailyClosePrintService;
    private readonly ILinklySettlementService? _linklySettlementService;
    private readonly ICashDrawerService _cashDrawerService;
    private readonly IApplicationExitService _applicationExitService;
    private readonly IConfirmationDialogService _confirmationDialogService;
    private readonly ITestSalesDataResetService? _testSalesDataResetService;
    private readonly ILinklyTerminalDialogPresenter? _linklyTerminalDialogPresenter;
    private readonly ICardPaymentRecoveryService? _cardPaymentRecoveryService;
    private readonly ICardRecoveryResultDialogService? _cardRecoveryResultDialogService;
    private readonly ILinklyFallbackPromptCoordinator? _linklyFallbackPromptCoordinator;
    private readonly ICashierSessionContext _cashierSessionContext;
    private readonly ICashierLoginService? _cashierLoginService;
    private readonly IOperationAuditLogger? _operationAuditLogger;
    private readonly IOperationAuthorizationService? _operationAuthorizationService;
    private readonly IPosRuntimeStatusApiClient? _runtimeStatusApiClient;
    private readonly CashierSessionRefreshService? _cashierSessionRefreshService;
    private readonly IRemoteMaintenanceService? _remoteMaintenanceService;
    private readonly IPaymentMethodSettingsService? _paymentMethodSettingsService;
    private readonly ICatalogSyncStatusService? _catalogSyncStatusService;
    private readonly Dispatcher? _uiDispatcher;
    private readonly ISharedHeldOrderCoordinator? _sharedHeldOrderCoordinator;
    private readonly ISharedHeldOrderApiClient? _sharedHeldOrderApiClient;
    private readonly ISharedHeldOrderRepository? _sharedHeldOrderRepository;
    private readonly ISharedHeldOrderPublicationWorker? _sharedHeldOrderPublicationWorker;
    private readonly bool _enforceCashierPermissions;
    private readonly PosTerminalWorkflowFactory _posTerminalWorkflowFactory;
    private readonly MainChildViewModelFactory _mainChildViewModelFactory;
    private readonly ScreenNavigator _screenNavigator;
    private readonly IWindowOwnerProvider? _windowOwnerProvider;
    private readonly Func<CancellationToken, Task<AppUpdateCoordinatorResult>>? _checkForAppUpdateAsync;
    private readonly IAppUpdateChannelProvider? _appUpdateChannelProvider;
    private readonly ApiServerSettingsViewModel? _apiServerSettings;
    private readonly AttendanceQrPanelViewModel? _attendanceQrPanel;
    private readonly CustomerDisplayShellController _customerDisplayShellController;
    private readonly DeviceReregistrationCoordinator _deviceReregistrationCoordinator;
    private readonly CatalogStartupCoordinator _catalogStartupCoordinator;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _connectivityTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _catalogDownloadHideTimer = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly object _connectivityRefreshSync = new();

    private bool _isApplyingCulture;
    private bool _schemaReady;
    private LocalOrder? _lastCompletedOrder;
    private LocalDeviceCache? _pendingDeviceRegistrationCache;
    private Task? _deviceRegistrationStoreLoadTask;
    private DeviceActivationRecoveryMode? _startupActivationRecoveryMode;
    private bool _startupActivationRecoveryIsUnreadable;
    private Task? _posPostShowStartupTask;
    private AppStartupOptions? _startupOptions;
    private int _disposed;
    private bool _startupCardRecoveryPendingAfterCashierLogin;
    private bool _shutdownLogoutRecorded;
    private bool _allowApiServerSwitchNavigation;
    private object? _apiServerSwitchFrozenScreen;
    private int _applicationExitStarted;
    private int _shutdownStarted;
    private long _languageSaveRequestVersion;
    private Task<bool>? _connectivityRefreshTask;
    private Task _shutdownCancellationTask = Task.CompletedTask;
    private Task _paymentShutdownCancellationTask = Task.CompletedTask;

    private SyncOrchestrator? _syncOrchestrator;
    private CardRecoveryPresenter? _cardRecoveryPresenter;
    private readonly MainReceiptCoordinator _receiptCoordinator;

    [ObservableProperty]
    private PosSessionState _session = new("HB POS", DefaultTestStoreCode, "Main Branch", "Terminal 04", "C001", "Alice", false, 0);

    public object? CurrentScreen
    {
        get => _screenNavigator.CurrentScreen;
        set
        {
            if (IsApiServerSwitching && !_allowApiServerSwitchNavigation)
            {
                return;
            }

            _screenNavigator.SetCurrentScreen(value);
        }
    }

    public PosTerminalViewModel? CachedPosTerminalScreen => _screenNavigator.CachedPosTerminalScreen;

    public PaymentViewModel? CachedCashPaymentScreen => _screenNavigator.CachedCashPaymentScreen;

    public SpecialProductsViewModel? CachedSpecialProductsScreen => _screenNavigator.CachedSpecialProductsScreen;

    public TransactionHistoryViewModel? CachedTransactionHistoryScreen => _screenNavigator.TransactionHistory;

    public SettingsViewModel? CachedSettingsScreen => _screenNavigator.Settings;

    public AppUpdateState AppUpdate { get; }

    public IOperationAuthorizationService? OperationAuthorization => _operationAuthorizationService;

    [ObservableProperty]
    private string _selectedCultureName = LocalizationService.DefaultCultureName;

    [ObservableProperty]
    private string _onlineStateText = string.Empty;

    public string PendingSyncText
    {
        get => _syncOrchestrator?.PendingSyncText ?? string.Empty;
        set
        {
            if (_syncOrchestrator is not null)
            {
                _syncOrchestrator.PendingSyncText = value;
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isApiServerSwitching;

    [ObservableProperty]
    private string _currentTime = string.Empty;

    [ObservableProperty]
    private string _terminalInfo = string.Empty;

    [ObservableProperty]
    private string _storeInfo = string.Empty;

    [ObservableProperty]
    private string _cashierInfo = string.Empty;

    [ObservableProperty]
    private string _cashierBarcodeInput = string.Empty;

    [ObservableProperty]
    private string _versionStatusText = string.Empty;

    public string OrderSyncStatusText
    {
        get => _syncOrchestrator?.OrderSyncStatusText ?? string.Empty;
        set
        {
            if (_syncOrchestrator is not null)
            {
                _syncOrchestrator.OrderSyncStatusText = value;
                OnPropertyChanged();
            }
        }
    }

    public string SyncCenterDetailTitle
    {
        get => _syncOrchestrator?.SyncCenterDetailTitle ?? string.Empty;
        set
        {
            if (_syncOrchestrator is not null)
            {
                _syncOrchestrator.SyncCenterDetailTitle = value;
                OnPropertyChanged();
            }
        }
    }

    public string LastOrderSyncErrorText
    {
        get => _syncOrchestrator?.LastOrderSyncErrorText ?? string.Empty;
        set
        {
            if (_syncOrchestrator is not null)
            {
                _syncOrchestrator.LastOrderSyncErrorText = value;
                OnPropertyChanged();
            }
        }
    }

    public string LastAuditSyncErrorText
    {
        get => _syncOrchestrator?.LastAuditSyncErrorText ?? string.Empty;
        set
        {
            if (_syncOrchestrator is not null)
            {
                _syncOrchestrator.LastAuditSyncErrorText = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsSyncCenterExpanded
    {
        get => _syncOrchestrator?.IsSyncCenterExpanded ?? false;
        set
        {
            if (_syncOrchestrator is not null)
            {
                _syncOrchestrator.IsSyncCenterExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    private bool _isDeviceReregistrationDialogOpen;

    public bool IsCardRecoveryResultDialogOpen
    {
        get => _cardRecoveryPresenter?.IsCardRecoveryResultDialogOpen ?? false;
        set
        {
            if (_cardRecoveryPresenter is not null)
            {
                _cardRecoveryPresenter.IsCardRecoveryResultDialogOpen = value;
                OnPropertyChanged();
            }
        }
    }

    public CardRecoveryResultDialogViewModel? CardRecoveryResultDialog
    {
        get => _cardRecoveryPresenter?.CardRecoveryResultDialog;
        set
        {
            if (_cardRecoveryPresenter is not null)
            {
                _cardRecoveryPresenter.CardRecoveryResultDialog = value;
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    private bool _isCustomerDisplayOpen;

    [ObservableProperty]
    private CustomerDisplayWindowMode _customerDisplayWindowMode = CustomerDisplayWindowMode.Closed;

    public int PendingUploadCount
    {
        get => _syncOrchestrator?.PendingUploadCount ?? 0;
        set
        {
            if (_syncOrchestrator is not null)
            {
                _syncOrchestrator.PendingUploadCount = value;
                OnPropertyChanged();
            }
        }
    }

    public int FailedUploadCount
    {
        get => _syncOrchestrator?.FailedUploadCount ?? 0;
        set
        {
            if (_syncOrchestrator is not null)
            {
                _syncOrchestrator.FailedUploadCount = value;
                OnPropertyChanged();
            }
        }
    }

    public int SyncingOrderCount
    {
        get => _syncOrchestrator?.SyncingOrderCount ?? 0;
        set
        {
            if (_syncOrchestrator is not null)
            {
                _syncOrchestrator.SyncingOrderCount = value;
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    private bool _isCatalogDownloadProgressVisible;

    [ObservableProperty]
    private double _catalogDownloadProgressValue;

    [ObservableProperty]
    private string _catalogDownloadProgressText = string.Empty;

    [ObservableProperty]
    private string _catalogDownloadProgressDetailText = string.Empty;

    [ObservableProperty]
    private bool _isCatalogDownloadProgressFailed;

    public bool IsOrderSyncRetrying
    {
        get => _syncOrchestrator?.IsOrderSyncRetrying ?? false;
        set
        {
            if (_syncOrchestrator is not null)
            {
                _syncOrchestrator.IsOrderSyncRetrying = value;
                OnPropertyChanged();
            }
        }
    }


    public MainViewModel(
        IPosCoreServices core,
        IPosInfrastructureFacade infra,
        IPaymentTerminalFacade paymentTerminal,
        IPrintFacade print,
        IShellCultureService shellCultureService,
        IShellCatalogService shellCatalogService,
        ILocalCatalogRepository catalogRepository,
        IRemoteLookupRefreshService remoteLookupRefresh,
        ISpecialProductService specialProductService,
        IMainShellStartupService mainShellStartupService,
        ILocalOrderRepository orderRepository,
        IShellSyncCenterService shellSyncCenterService,
        ILocalizationService localization,
        ICustomerDisplayOrchestrator customerDisplayOrchestrator,
        IReceiptQueryService receiptQueryService,
        ICashPaymentWorkflowService cashPaymentWorkflowService,
        IDeviceRegistrationWorkflowService deviceRegistrationWorkflowService,
        ISpecialProductsWorkflowService specialProductsWorkflowService,
        PosTerminalWorkflowFactory posTerminalWorkflowFactory,
        ISuspendedOrderService? suspendedOrderService = null,
        IRemoteOrderHistoryService? remoteOrderHistoryService = null,
        IPromotionEvaluationService? promotionEvaluationService = null,
        IReceiptReturnsWorkflowService? receiptReturnsWorkflowService = null,
        IOrderUploadExecutionService? orderUploadExecutionService = null,
        IDailyCloseService? dailyCloseService = null,
        IDailyClosePrintService? dailyClosePrintService = null,
        ICashDrawerService? cashDrawerService = null,
        IInstallmentOrderService? installmentOrderService = null,
        ITestSalesDataResetService? testSalesDataResetService = null,
        IWindowOwnerProvider? windowOwnerProvider = null,
        AppUpdateState? appUpdateState = null,
        Func<CancellationToken, Task<AppUpdateCoordinatorResult>>? checkForAppUpdateAsync = null,
        IAppUpdateChannelProvider? appUpdateChannelProvider = null,
        ICashierSessionContext? cashierSessionContext = null,
        ICashierLoginService? cashierLoginService = null,
        IPosRuntimeStatusApiClient? runtimeStatusApiClient = null,
        bool enforceCashierPermissions = false,
        IOperationAuditLogger? operationAuditLogger = null,
        ApiServerSettingsViewModel? apiServerSettings = null,
        IOperationAuthorizationService? operationAuthorizationService = null,
        ApiRuntimeEndpointState? runtimeEndpointState = null,
        AttendanceQrPanelViewModel? attendanceQrPanel = null,
        ILinklySettlementService? linklySettlementService = null,
        ILinklySettlementUploadQueueReader? linklySettlementUploadQueueReader = null,
        ILinklySettlementUploadExecutionService? linklySettlementUploadExecutionService = null,
        ISharedHeldOrderCoordinator? sharedHeldOrderCoordinator = null,
        ISharedHeldOrderApiClient? sharedHeldOrderApiClient = null,
        ISharedHeldOrderRepository? sharedHeldOrderRepository = null,
        ISharedHeldOrderPublicationWorker? sharedHeldOrderPublicationWorker = null,
        IStoreReceiptProfileApiClient? storeReceiptProfileApiClient = null,
        CashierSessionRefreshService? cashierSessionRefreshService = null,
        IRemoteMaintenanceService? remoteMaintenanceService = null,
        IPaymentMethodSettingsService? paymentMethodSettingsService = null,
        ICatalogSyncStatusService? catalogSyncStatusService = null)
    {
        _core = core;
        _infra = infra;
        _paymentTerminal = paymentTerminal;
        _print = print;
        _priceIndex = core.PriceIndex;
        _cart = core.Cart;
        _checkout = core.Checkout;
        _schema = core.Schema;
        _shellCultureService = shellCultureService;
        _shellCatalogService = shellCatalogService;
        _catalogRepository = catalogRepository;
        _remoteLookupRefresh = remoteLookupRefresh;
        _specialProductService = specialProductService;
        _connectivityApiClient = infra.ConnectivityApiClient;
        _mainShellStartupService = mainShellStartupService;
        _orderRepository = orderRepository;
        _shellSyncCenterService = shellSyncCenterService;
        _orderUploadExecutionService = orderUploadExecutionService ?? NoopOrderUploadExecutionService.Instance;
        _localization = localization;
        _customerDisplayOrchestrator = customerDisplayOrchestrator;
        _rawScannerService = infra.RawScannerService;
        _userFeedbackService = infra.UserFeedbackService ?? NoopUserFeedbackService.Instance;
        _receiptQueryService = receiptQueryService;
        _receiptPrintService = print.ReceiptPrintService ?? new NoopReceiptPrintService(_localization);
        _receiptPrinterSettingsStore = print.ReceiptPrinterSettingsStore;
        _receiptTextFormatter = print.ReceiptTextFormatter ?? new ReceiptTextFormatter();
        _storeReceiptProfileApiClient = storeReceiptProfileApiClient;
        _linklyBankReceiptPrinter = print.LinklyBankReceiptPrinter;
        _installmentOrderService = installmentOrderService ?? NoopInstallmentOrderService.Instance;
        _suspendedOrderService = suspendedOrderService;
        _remoteOrderHistoryService = remoteOrderHistoryService;
        _cashPaymentWorkflowService = cashPaymentWorkflowService;
        _voucherApiClient = paymentTerminal.VoucherApiClient;
        _cardTerminalClient = paymentTerminal.CardTerminalClient;
        _cardTerminalSetupService = paymentTerminal.CardTerminalSetupService;
        _deviceRegistrationWorkflowService = deviceRegistrationWorkflowService;
        _specialProductsWorkflowService = specialProductsWorkflowService;
        _promotionEvaluationService = promotionEvaluationService;
        _receiptReturnsWorkflowService = receiptReturnsWorkflowService ?? new ReceiptReturnsWorkflowService(
            _receiptQueryService,
            _orderRepository,
            _remoteOrderHistoryService,
            _priceIndex,
            _cart,
            _localization);
        _dailyCloseService = dailyCloseService ?? NoopDailyCloseService.Instance;
        _dailyClosePrintService = dailyClosePrintService ?? NoopDailyClosePrintService.Instance;
        _linklySettlementService = linklySettlementService;
        _cashDrawerService = cashDrawerService ?? new NoopCashDrawerService(_localization);
        _applicationExitService = infra.ApplicationExitService ?? new WpfApplicationExitService();
        _confirmationDialogService = infra.ConfirmationDialogService ?? new WpfConfirmationDialogService(_localization);
        ConfirmationDialog = _confirmationDialogService as IConfirmationDialogPresenter ??
            throw new InvalidOperationException("The confirmation dialog service must expose its presenter.");
        _testSalesDataResetService = testSalesDataResetService;
        _linklyTerminalDialogPresenter = paymentTerminal.LinklyTerminalDialogPresenter;
        _cardPaymentRecoveryService = paymentTerminal.CardPaymentRecoveryService;
        _cardRecoveryResultDialogService = paymentTerminal.CardRecoveryResultDialogService;
        _linklyFallbackPromptCoordinator = paymentTerminal.LinklyFallbackPromptCoordinator;
        _cashierSessionContext = cashierSessionContext ?? new CashierSessionContext();
        _cashierLoginService = cashierLoginService;
        _operationAuditLogger = operationAuditLogger;
        _operationAuthorizationService = operationAuthorizationService;
        _sharedHeldOrderCoordinator = sharedHeldOrderCoordinator;
        _sharedHeldOrderApiClient = sharedHeldOrderApiClient;
        _sharedHeldOrderRepository = sharedHeldOrderRepository;
        _sharedHeldOrderPublicationWorker = sharedHeldOrderPublicationWorker;
        if (runtimeEndpointState is not null && Application.Current is not null)
        {
            ProductThumbnailImageSourceConverter.ConfigureApiBaseAddressProvider(
                () => runtimeEndpointState.CurrentAddress);
        }
        _runtimeStatusApiClient = runtimeStatusApiClient;
        _cashierSessionRefreshService = cashierSessionRefreshService;
        _remoteMaintenanceService = remoteMaintenanceService;
        _paymentMethodSettingsService = paymentMethodSettingsService;
        _catalogSyncStatusService = catalogSyncStatusService;
        var applicationDispatcher = Application.Current?.Dispatcher;
        // 主界面只绑定到构造它的 WPF Dispatcher，避免之后误用其他测试或退出流程遗留的全局 Application。
        _uiDispatcher = applicationDispatcher?.CheckAccess() == true
            ? applicationDispatcher
            : null;
        _enforceCashierPermissions = enforceCashierPermissions;
        _windowOwnerProvider = windowOwnerProvider;
        AppUpdate = appUpdateState ?? new AppUpdateState();
        _checkForAppUpdateAsync = checkForAppUpdateAsync;
        _appUpdateChannelProvider = appUpdateChannelProvider;
        _apiServerSettings = apiServerSettings;
        _attendanceQrPanel = attendanceQrPanel;
        _posTerminalWorkflowFactory = posTerminalWorkflowFactory;
        _mainChildViewModelFactory = CreateMainChildViewModelFactory();

        _cardRecoveryPresenter = CreateCardRecoveryPresenter();

        _syncOrchestrator = CreateSyncOrchestrator(
            linklySettlementUploadQueueReader,
            linklySettlementUploadExecutionService);

        _screenNavigator = CreateScreenNavigator();

        if (_operationAuthorizationService is not null)
        {
            // 关键逻辑：授权遮罩拥有独立扫码页，避免授权条码落入当前业务页面。
            _operationAuthorizationService.PropertyChanged += OnOperationAuthorizationPropertyChanged;
            _rawScannerService.Subscribe(
                _operationAuthorizationService.ScannerPageId,
                OnOperationAuthorizationBarcodeScanned);
        }

        _screenNavigator.PaymentSuccess = _mainChildViewModelFactory.CreatePaymentSuccessViewModel();
        PaymentSuccess.NewTransactionRequested += OnPaymentSuccessNewTransactionRequested;
        PaymentSuccess.PrintReceiptRequested += OnPaymentSuccessPrintReceiptRequested;

        _receiptCoordinator = new MainReceiptCoordinator(
            _receiptPrintService,
            _cashDrawerService,
            _localization,
            msg => StatusMessage = msg ?? string.Empty);

        _customerDisplayShellController = new CustomerDisplayShellController(
            _customerDisplayOrchestrator,
            _localization,
            () => CustomerDisplay,
            () => Session,
            () => _cart,
            mode => CustomerDisplayWindowMode = mode,
            () => CustomerDisplayWindowMode,
            msg => StatusMessage = msg ?? string.Empty);

        _deviceReregistrationCoordinator = new DeviceReregistrationCoordinator(
            _mainShellStartupService,
            _localization,
            _shellSyncCenterService,
            _cart,
            snapshot => ApplySyncCenterSnapshot(snapshot),
            msg => StatusMessage = msg ?? string.Empty,
            () => _startupOptions?.PreviewMode == true);

        _catalogStartupCoordinator = new CatalogStartupCoordinator(
            _shellCatalogService,
            _catalogRepository,
            _localization,
            msg => StatusMessage = msg ?? string.Empty);

        ShowPosCommand = _screenNavigator.ShowPosCommand;
        ShowCashPaymentCommand = _screenNavigator.ShowCashPaymentCommand;
        ShowReturnsCommand = _screenNavigator.ShowReturnsCommand;
        ShowPaymentSuccessCommand = _screenNavigator.ShowPaymentSuccessCommand;
        ShowHistoryCommand = _screenNavigator.ShowHistoryCommand;
        ShowDailyCloseCommand = _screenNavigator.ShowDailyCloseCommand;
        ShowCustomerDisplayCommand = _screenNavigator.ShowCustomerDisplayCommand;
        ShowSettingsCommand = _screenNavigator.ShowSettingsCommand;
        ToggleSyncCenterCommand = _syncOrchestrator.ToggleSyncCenterCommand;
        RetrySyncOrderCommand = _syncOrchestrator.RetrySyncOrderCommand;
        RetryAllSyncOrdersCommand = _syncOrchestrator.RetryAllSyncOrdersCommand;
        ToggleCustomerDisplayWindowCommand = new AsyncRelayCommand(ToggleCustomerDisplayWindowAsync);
        CloseCustomerDisplayWindowCommand = new AsyncRelayCommand(CloseCustomerDisplayWindowFromCommandAsync);
        ShowCustomerDisplayNormalCommand = new AsyncRelayCommand(() => SetCustomerDisplayWindowModeFromCommandAsync(CustomerDisplayWindowMode.Normal));
        ShowCustomerDisplayFullscreenCommand = new AsyncRelayCommand(() => SetCustomerDisplayWindowModeFromCommandAsync(CustomerDisplayWindowMode.Fullscreen));
        ToggleCultureCommand = new AsyncRelayCommand(
            ToggleCultureAsync,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        ResetScannerBindingCommand = new AsyncRelayCommand(ResetScannerBindingAsync);
        LoginCashierCommand = new AsyncRelayCommand(LoginCashierFromInputAsync, CanLoginCashierFromInput);
        ExitApplicationCommand = new AsyncRelayCommand(ExitApplicationAsync);
        CloseCardRecoveryResultDialogCommand = _cardRecoveryPresenter.CloseCardRecoveryResultDialogCommand;
        PrintRecoveredReceiptCommand = _cardRecoveryPresenter.PrintRecoveredReceiptCommand;
        RetryActiveSessionRecoveryCommand = _cardRecoveryPresenter.RetryActiveSessionRecoveryCommand;
        ResolveCardRefundCommand = _cardRecoveryPresenter.ResolveCardRefundCommand;
        ResolveCardPaymentCommand = _cardRecoveryPresenter.ResolveCardPaymentCommand;

        _cart.CartChanged += OnCartChanged;
        _localization.CultureChanged += OnCultureChanged;
        _customerDisplayOrchestrator.Closed += OnCustomerDisplayClosed;
        _customerDisplayOrchestrator.FullscreenRequested += OnCustomerDisplayFullscreenRequested;
        _clockTimer.Tick += OnClockTimerTick;
        _connectivityTimer.Tick += OnConnectivityTimerTick;
        _catalogDownloadHideTimer.Tick += OnCatalogDownloadHideTimerTick;
        if (_cashierSessionRefreshService is not null)
        {
            _cashierSessionRefreshService.SessionRejected += OnCashierSessionRejected;
        }
        RefreshLocalizedShell(resetStatus: true);
    }

    private MainChildViewModelFactory CreateMainChildViewModelFactory() =>
        new(
            _deviceRegistrationWorkflowService,
            _receiptQueryService,
            _suspendedOrderService,
            _remoteOrderHistoryService,
            _receiptTextFormatter,
            _receiptPrinterSettingsStore,
            _installmentOrderService,
            _localization,
            _cardTerminalClient,
            _priceIndex,
            _cart,
            _catalogRepository,
            _specialProductService,
            _specialProductsWorkflowService,
            _receiptReturnsWorkflowService,
            _cashPaymentWorkflowService,
            _cardTerminalSetupService,
            _rawScannerService,
            _dailyCloseService,
            _dailyClosePrintService,
            _userFeedbackService,
            _promotionEvaluationService,
            _receiptPrintService,
            _cardRecoveryResultDialogService,
            _cashierSessionContext,
            _enforceCashierPermissions,
            _checkForAppUpdateAsync,
            _appUpdateChannelProvider,
             _operationAuditLogger,
             // 中文说明：组合根只转交 DI 单例，确保注册页与设置页共享同一份重启状态。
             apiServerSettings: _apiServerSettings,
             // 中文说明：主壳与全部子页面共享同一个单次操作授权服务，授权不会变成新的登录会话。
             operationAuthorizationService: _operationAuthorizationService,
             orderUploadExecutionService: _orderUploadExecutionService,
             linklySettlementService: _linklySettlementService,
             sharedHeldOrderCoordinator: _sharedHeldOrderCoordinator,
             sharedHeldOrderApiClient: _sharedHeldOrderApiClient,
             sharedHeldOrderRepository: _sharedHeldOrderRepository,
             sharedHeldOrderPublicationWorker: _sharedHeldOrderPublicationWorker,
             storeReceiptProfileApiClient: _storeReceiptProfileApiClient,
             remoteMaintenanceService: _remoteMaintenanceService,
             paymentMethodSettingsService: _paymentMethodSettingsService,
             catalogSyncStatusService: _catalogSyncStatusService);

    private CardRecoveryPresenter CreateCardRecoveryPresenter() =>
        new(
            _cardPaymentRecoveryService,
            _cardRecoveryResultDialogService,
            _receiptQueryService,
            _receiptPrinterSettingsStore,
            _receiptTextFormatter,
            _localization,
            _linklyFallbackPromptCoordinator,
            _linklyBankReceiptPrinter,
            _mainChildViewModelFactory,
            _cart,
            setStatusMessage: msg => StatusMessage = msg ?? string.Empty,
            setPaymentRecoveryBlocked: (blocked, message) =>
                CashPayment?.SetCurrentCardRecoveryRequired(blocked, message),
            getOwner: () => CurrentOwner,
            navigateToPaymentOnDraft: () =>
            {
                ExecuteCardRecoveryPresenterUiStep(
                    "prepare-cached-payment",
                    _screenNavigator.PrepareCachedCashPaymentScreen);
                // PrepareForEntry 是 durable handoff 的核心步骤；失败必须交给 presenter 保留 owner 与恢复锁。
                CashPayment?.PrepareForEntry(Session);
                ExecuteCardRecoveryPresenterUiStep(
                    "navigate-payment",
                    () => CurrentScreen = CashPayment);
                return Task.CompletedTask;
            },
            getSession: () => Session,
            setSession: value => ExecuteCardRecoveryPresenterUiStep(
                "apply-session",
                () => Session = value),
            onCardRecoveryOrderCompleted: order =>
            {
                _lastCompletedOrder = order;
                // 中文注释：恢复完成的是另一单，不能沿用上一单的收尾告警。
                PaymentSuccess.HasPostCommitWarning = false;
                PaymentSuccess.LoadFromOrder(order);
                _screenNavigator.ShowCompletedSaleOnPos();
                // 真实恢复订单完成后仅在首次进入成功页时播放一次结账成功音。
                _userFeedbackService.Play(UserFeedbackCue.Checkout);
                PosTerminal?.RefreshCart();
                CashPayment?.RefreshCart();
            },
            onCardRecoveryDraftRestored: (restoredTenders, restoredStatusMessage) =>
            {
                PosTerminal?.RefreshCart();
                if (restoredTenders is { Count: > 0 })
                {
                    CashPayment?.RestoreRecoveredPaymentTenders(restoredTenders, restoredStatusMessage);
                    return;
                }

                CashPayment?.RefreshCart();
            },
            tryApplyCardRecoveryDraft: (requiresAlternativeRefundMethod, restoredTenders, restoredStatusMessage) =>
            {
                PosTerminal?.RefreshCart();
                return CashPayment?.TryApplyRecoveredPaymentProjection(
                    requiresAlternativeRefundMethod,
                    restoredTenders,
                    restoredStatusMessage) ?? false;
            },
            completeRecoveredDraftHandoffAsync: (attemptKey, cancellationToken) =>
                _cardPaymentRecoveryService is CardPaymentRecoveryCoordinator coordinator
                    ? coordinator.CompleteDraftHandoffAsync(attemptKey, _cart, cancellationToken)
                    : Task.FromResult(false),
            refreshPendingSyncAsync: () => RefreshPendingSyncAsync(),
            printReceiptAsync: (receipt, reason) => PrintReceiptWithShellPermissionAsync(receipt, reason),
            canPrintReceipt: () => IsShellPermissionAllowed(Permissions.PosTerminal.Receipt.PrintLast),
            notifyShowCashPaymentCanExecuteChanged: () => ShowCashPaymentCommand!.NotifyCanExecuteChanged(),
            notifyPrintRecoveredReceiptCanExecuteChanged: () => PrintRecoveredReceiptCommand!.NotifyCanExecuteChanged(),
            notifyPropertyChanged: name => OnPropertyChanged(name),
            operationAuthorizationService: _operationAuthorizationService,
            operationAuditLogger: _operationAuditLogger,
            requirePermission: TryRequireShellPermission,
            setAlternativeRefundMethodRequired: required => CashPayment?.SetAlternativeRefundMethodRequired(required));

    private static void ExecuteCardRecoveryPresenterUiStep(string stage, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // MainViewModel 的 setter 会先提交 backing state 再通知；订阅者失败不能撤销已提交的恢复交接。
            try
            {
                ConsoleLog.WriteError(
                    "CardRecovery",
                    $"recovery presenter UI step failed stage={stage}",
                    exception: ex);
            }
            catch (Exception logException) when (
                logException is not OutOfMemoryException and not StackOverflowException)
            {
                // 诊断外围失败不能覆盖恢复交接结果。
            }
        }
    }

    private SyncOrchestrator CreateSyncOrchestrator(
        ILinklySettlementUploadQueueReader? linklySettlementUploadQueueReader,
        ILinklySettlementUploadExecutionService? linklySettlementUploadExecutionService) =>
        new(
            _shellSyncCenterService,
            _orderUploadExecutionService,
            _localization,
            setStatusMessage: msg => StatusMessage = msg ?? string.Empty,
            onPendingSyncCountChanged: count => Session = Session with { PendingSyncCount = count },
            getPendingSyncCount: () => Session.PendingSyncCount,
            refreshShell: () => RefreshLocalizedShell(),
            notifyPropertyChanged: name => OnPropertyChanged(name),
            linklySettlementUploadQueueReader: linklySettlementUploadQueueReader,
            linklySettlementUploadExecutionService: linklySettlementUploadExecutionService);

    internal void ConfigureAuditSyncCenter(
        ClientLogOutboxStore logOutboxStore,
        OperationAuditUploadService operationAuditUploadService,
        DeviceAuthorizationState deviceAuthorizationState)
    {
        _syncOrchestrator?.ConfigureAudit(logOutboxStore, operationAuditUploadService, deviceAuthorizationState);
    }

    private ScreenNavigator CreateScreenNavigator() =>
        new(
            _mainChildViewModelFactory,
            _cart,
            _localization,
            _suspendedOrderService,
            _cardTerminalSetupService,
            _testSalesDataResetService,
            _confirmationDialogService,
            _customerDisplayOrchestrator,
            _linklyFallbackPromptCoordinator,
            _cardPaymentRecoveryService,
            HandleCardRecoveryCenterResultAsync,
            SyncCatalogAndReloadAsync,
            ResetCatalogAndReloadAsync,
            _checkForAppUpdateAsync,
            BeginDeviceReregistrationAsync,
            () => _cardRecoveryPresenter!.RecoverActiveCardPaymentSessionFromPaymentAsync(),
            OnInstallmentOrderCreatedAsync,
            setScreen: OnScreenChanged,
            onPaymentCreated: vm =>
            {
                vm.PaymentCompleted += OnPaymentCompleted;
                vm.PropertyChanged += OnCashPaymentPropertyChanged;
                var cardRecoveryPresenter = _cardRecoveryPresenter!;
                vm.ConfigureCardPaymentHandoff(
                    cardRecoveryPresenter.PrepareCardPaymentHandoffAsync,
                    async (candidate, request) =>
                    {
                        var handedOff = await cardRecoveryPresenter.HandoffCardPaymentAsync(candidate, request);
                        if (handedOff)
                        {
                            await RefreshCardRecoveryCountAsync();
                        }

                        return handedOff;
                    },
                    () => _ = _screenNavigator.ShowCardRecoveryCenterAsync());
            },
            onPaymentDisposed: vm =>
            {
                vm.PaymentCompleted -= OnPaymentCompleted;
                vm.PropertyChanged -= OnCashPaymentPropertyChanged;
            },
            printSelectedHistoryReceiptAsync: vm => PrintSelectedHistoryReceiptAsync(vm),
            setStatusMessage: msg => StatusMessage = msg,
            getLastCompletedOrder: () => _lastCompletedOrder,
            setLastCompletedOrder: value => _lastCompletedOrder = value);

    public PosTerminalViewModel? PosTerminal
    {
        get => _screenNavigator.PosTerminal;
        set => _screenNavigator.PosTerminal = value;
    }

    public SpecialProductsViewModel? SpecialProducts
    {
        get => _screenNavigator.SpecialProducts;
        set => _screenNavigator.SpecialProducts = value;
    }

    public PaymentViewModel? CashPayment
    {
        get => _screenNavigator.CashPayment;
        set => _screenNavigator.CashPayment = value;
    }

    public InstallmentCenterViewModel? InstallmentCenter
    {
        get => _screenNavigator.InstallmentCenter;
        set => _screenNavigator.InstallmentCenter = value;
    }

    public InstallmentCreateViewModel? InstallmentCreate
    {
        get => _screenNavigator.InstallmentCreate;
        set => _screenNavigator.InstallmentCreate = value;
    }

    public ReceiptReturnsViewModel? ReceiptReturns
    {
        get => _screenNavigator.ReceiptReturns;
        set => _screenNavigator.ReceiptReturns = value;
    }

    public PaymentSuccessViewModel PaymentSuccess
    {
        get => _screenNavigator.PaymentSuccess;
    }

    public TransactionHistoryViewModel? TransactionHistory
    {
        get => _screenNavigator.TransactionHistory;
        set => _screenNavigator.TransactionHistory = value;
    }

    public DailyCloseViewModel? DailyClose
    {
        get => _screenNavigator.DailyClose;
        set => _screenNavigator.DailyClose = value;
    }

    public CustomerDisplayViewModel CustomerDisplay => _screenNavigator.CustomerDisplay;

    private DeviceRegistrationViewModel? _deviceRegistration;

    public DeviceRegistrationViewModel? DeviceRegistration
    {
        get => _deviceRegistration;
        private set
        {
            if (ReferenceEquals(_deviceRegistration, value))
            {
                return;
            }

            var previous = _deviceRegistration;
            if (SetProperty(ref _deviceRegistration, value))
            {
                // 设备注册页会订阅本地化并保留审批轮询，替换或关闭时必须立即释放旧实例。
                previous?.Dispose();
            }
        }
    }

    public SettingsViewModel? Settings => _screenNavigator.Settings;

    public ApiServerSettingsViewModel? ApiServerSettings => _apiServerSettings;

    public ILinklyTerminalDialogPresenter? LinklyTerminalDialog => _linklyTerminalDialogPresenter;

    public IConfirmationDialogPresenter ConfirmationDialog { get; }

    public bool IsPosTerminalScreenActive => ReferenceEquals(CurrentScreen, CachedPosTerminalScreen);

    public bool IsCashPaymentScreenActive => ReferenceEquals(CurrentScreen, CachedCashPaymentScreen);

    public bool IsSpecialProductsScreenActive => ReferenceEquals(CurrentScreen, CachedSpecialProductsScreen);

    public bool IsTransactionHistoryScreenActive => ReferenceEquals(CurrentScreen, CachedTransactionHistoryScreen);

    public bool IsSettingsScreenActive => ReferenceEquals(CurrentScreen, CachedSettingsScreen);

    public bool IsFallbackScreenActive => CurrentScreen is not null &&
        !IsPosTerminalScreenActive &&
        !IsCashPaymentScreenActive &&
        !IsSpecialProductsScreenActive &&
        !IsTransactionHistoryScreenActive &&
        !IsSettingsScreenActive;

    public bool IsCashierLoginOverlayOpen =>
        CurrentScreen is not null &&
        !ReferenceEquals(CurrentScreen, DeviceRegistration) &&
        !IsDeviceReregistrationDialogOpen &&
        Session.CashierSession is null;

    public string ActivePageTitleText => GetActivePageTitleText();

    private static readonly ObservableCollection<SyncQueueListItem> _emptySyncOrders = [];
    private static readonly ObservableCollection<OperationAuditQueueListItem> _emptyAuditLogs = [];

    public ObservableCollection<SyncQueueListItem> SyncCenterOrders => _syncOrchestrator?.SyncCenterOrders ?? _emptySyncOrders;

    public ObservableCollection<OperationAuditQueueListItem> SyncCenterAuditLogs => _syncOrchestrator?.SyncCenterAuditLogs ?? _emptyAuditLogs;

    public IRelayCommand ShowPosCommand { get; }

    public IRelayCommand CloseCardRecoveryResultDialogCommand { get; }

    public IAsyncRelayCommand PrintRecoveredReceiptCommand { get; }

    public IRelayCommand RetryActiveSessionRecoveryCommand { get; }

    public IAsyncRelayCommand<CardRefundSupervisorDecision> ResolveCardRefundCommand { get; }

    public IAsyncRelayCommand<CardPaymentSupervisorDecision> ResolveCardPaymentCommand { get; }

    public IRelayCommand ShowCashPaymentCommand { get; }

    public IRelayCommand ShowReturnsCommand { get; }

    public IAsyncRelayCommand ShowPaymentSuccessCommand { get; }

    public IAsyncRelayCommand ShowHistoryCommand { get; }

    public IAsyncRelayCommand ShowDailyCloseCommand { get; }

    public IRelayCommand ShowCustomerDisplayCommand { get; }

    public IAsyncRelayCommand ShowSettingsCommand { get; }

    public IAsyncRelayCommand ToggleSyncCenterCommand { get; }

    public IAsyncRelayCommand<SyncQueueListItem?> RetrySyncOrderCommand { get; }

    public IAsyncRelayCommand RetryAllSyncOrdersCommand { get; }

    public IAsyncRelayCommand RetrySelectedSyncOrdersCommand => _syncOrchestrator!.RetrySelectedSyncOrdersCommand;

    public IRelayCommand SelectAllSyncOrdersCommand => _syncOrchestrator!.SelectAllSyncOrdersCommand;

    public IAsyncRelayCommand RetrySelectedAuditLogsCommand => _syncOrchestrator!.RetrySelectedAuditLogsCommand;

    public IRelayCommand SelectAllAuditLogsCommand => _syncOrchestrator!.SelectAllAuditLogsCommand;

    public IAsyncRelayCommand ToggleCustomerDisplayWindowCommand { get; }

    public IAsyncRelayCommand CloseCustomerDisplayWindowCommand { get; }

    public IAsyncRelayCommand ShowCustomerDisplayNormalCommand { get; }

    public IAsyncRelayCommand ShowCustomerDisplayFullscreenCommand { get; }

    public IAsyncRelayCommand ToggleCultureCommand { get; }

    public IAsyncRelayCommand ResetScannerBindingCommand { get; }

    public IAsyncRelayCommand LoginCashierCommand { get; }

    public IAsyncRelayCommand ExitApplicationCommand { get; }

    public void BeginShutdown()
    {
        lock (_connectivityRefreshSync)
        {
            if (_shutdownStarted != 0)
            {
                return;
            }

            Volatile.Write(ref _shutdownStarted, 1);
            // CancellationToken 回调属于外部代码，必须离开 Dispatcher，避免关机截止时间被同步回调绕过。
            _shutdownCancellationTask = CancelForShutdownAsync(
                _shutdownCancellation,
                "main-view-model");
            // 中文注释：持有精确付款取消 Task，shutdown coordinator 必须正式观察其中的致命异常。
            _paymentShutdownCancellationTask = CashPayment?.BeginShutdown() ?? Task.CompletedTask;
        }

        _clockTimer.Stop();
        _connectivityTimer.Stop();
        _catalogDownloadHideTimer.Stop();

    }

    public void Dispose()
    {
        lock (_connectivityRefreshSync)
        {
            if (_disposed != 0)
            {
                return;
            }

            Volatile.Write(ref _disposed, 1);
        }

        BeginShutdown();
        _clockTimer.Tick -= OnClockTimerTick;
        _connectivityTimer.Tick -= OnConnectivityTimerTick;
        _catalogDownloadHideTimer.Tick -= OnCatalogDownloadHideTimerTick;
        if (_cashierSessionRefreshService is not null)
        {
            _cashierSessionRefreshService.SessionRejected -= OnCashierSessionRejected;
        }

        _cart.CartChanged -= OnCartChanged;
        _localization.CultureChanged -= OnCultureChanged;
        _customerDisplayOrchestrator.Closed -= OnCustomerDisplayClosed;
        _customerDisplayOrchestrator.FullscreenRequested -= OnCustomerDisplayFullscreenRequested;
        if (_operationAuthorizationService is not null)
        {
            _operationAuthorizationService.RevokeAll();
            _operationAuthorizationService.PropertyChanged -= OnOperationAuthorizationPropertyChanged;
            _rawScannerService.Unsubscribe(_operationAuthorizationService.ScannerPageId);
        }
        _cardRecoveryPresenter?.DetachDialogService();

        PaymentSuccess.NewTransactionRequested -= OnPaymentSuccessNewTransactionRequested;
        PaymentSuccess.PrintReceiptRequested -= OnPaymentSuccessPrintReceiptRequested;

        // 主壳销毁时统一释放当前缓存子页面，避免 singleton 服务事件继续持有旧页面。
        _screenNavigator.ClearScreens();
        DeviceRegistration = null;
        CancelStartupCatalogIndexLoad();
        DisposeShutdownCancellationWhenSafe();
    }

    public async Task InitializeAsync(AppStartupOptions startupOptions)
    {
        _startupOptions = startupOptions;
        // 关键逻辑：重新初始化会创建新的页面实例，必须丢弃上一轮 post-show 任务，避免新注册页永久停在“正在加载门店”。
        _deviceRegistrationStoreLoadTask = null;
        _startupActivationRecoveryMode = null;
        _startupActivationRecoveryIsUnreadable = false;
        _posPostShowStartupTask = null;
        await _schema.InitializeAsync();
        _schemaReady = true;

        await RestoreLanguageAsync(startupOptions);
        // 关键逻辑：登录遮罩复用设置页与设备注册页的单例，未登录时也要显示当前服务器地址。
        _apiServerSettings?.Load();
        DeviceActivationRecoveryMode? pendingActivationRecoveryMode = null;
        if (!startupOptions.PreviewMode)
        {
            try
            {
                pendingActivationRecoveryMode =
                    await _deviceRegistrationWorkflowService.GetPendingActivationRecoveryModeAsync();
            }
            catch (DeviceActivationRecoveryUnreadableException)
            {
                // 恢复文件存在但无法安全读取时，禁止旧 Enabled 缓存回到离线营业。
                DeviceRegistration = CreateDeviceRegistrationViewModel(startupOptions);
                _pendingDeviceRegistrationCache = null;
                DeviceRegistration.Prepare(null);
                DeviceRegistration.CanCancel = false;
                DeviceRegistration.IsBusy = true;
                DeviceRegistration.IsActivationRecoveryPending = true;
                DeviceRegistration.StatusMessage = _localization.T(
                    "deviceActivation.status.recoveryUnreadable");
                _startupActivationRecoveryIsUnreadable = true;
                CurrentScreen = DeviceRegistration;
                RefreshClock();
                _clockTimer.Start();
                return;
            }
        }

        var startupResult = await _mainShellStartupService.EvaluateAsync(Session, startupOptions.PreviewMode);
        Session = startupResult.Session;
        if (pendingActivationRecoveryMode is not null)
        {
            // 恢复记录优先于旧 Enabled 缓存：响应丢失时旧缓存仍可能可离线营业，绝不能清掉开通码继续旧分店。
            DeviceRegistration = CreateDeviceRegistrationViewModel(startupOptions);
            _pendingDeviceRegistrationCache = startupResult.CachedDevice;
            if (pendingActivationRecoveryMode == DeviceActivationRecoveryMode.Rebind)
            {
                DeviceRegistration.PrepareReregister(
                    startupResult.CachedDevice?.StoreCode ?? Session.StoreCode);
                DeviceRegistration.CanCancel = false;
            }
            else
            {
                DeviceRegistration.Prepare(startupResult.CachedDevice);
            }

            DeviceRegistration.IsActivationRecoveryPending = true;
            DeviceRegistration.IsBusy = true;
            DeviceRegistration.StatusMessage = _localization.T("deviceActivation.status.recovering");
            _startupActivationRecoveryMode = pendingActivationRecoveryMode;
            CurrentScreen = DeviceRegistration;
            RefreshClock();
            _clockTimer.Start();
            return;
        }

        if (startupResult.RequiresDeviceRegistration)
        {
            DeviceRegistration = CreateDeviceRegistrationViewModel(startupOptions);
            _pendingDeviceRegistrationCache = startupResult.CachedDevice;
            DeviceRegistration.Prepare(startupResult.CachedDevice);
            CurrentScreen = DeviceRegistration;
            RefreshClock();
            _clockTimer.Start();
            return;
        }

        await ClearActivationRecoverySafeAsync();
        await InitializePosExperienceAsync(startupOptions);
    }

    internal async Task ReinitializeAfterServerSwitchAsync(CancellationToken cancellationToken)
    {
        _allowApiServerSwitchNavigation = true;
        try
        {
            await ReinitializeAfterServerSwitchCoreAsync(cancellationToken);
        }
        finally
        {
            _allowApiServerSwitchNavigation = false;
        }
    }

    private async Task ReinitializeAfterServerSwitchCoreAsync(CancellationToken cancellationToken)
    {
        var startupOptions = _startupOptions ?? new AppStartupOptions([], false, null, null);
        _connectivityTimer.Stop();
        _deviceRegistrationStoreLoadTask = null;
        _startupActivationRecoveryMode = null;
        _startupActivationRecoveryIsUnreadable = false;
        _posPostShowStartupTask = null;
        CancelStartupCatalogIndexLoad();
        _screenNavigator.ClearScreens();
        _screenNavigator.SetCachedPosTerminalScreen(null);
        _screenNavigator.SetCachedSpecialProductsScreen(null);
        DeviceRegistration = null;
        _mainShellStartupService.ClearAuthorization();

        // 服务器边界切换后清空旧身份与门店上下文，再只使用目标分区重新建立壳层。
        Session = Session with
        {
            StoreCode = string.Empty,
            StoreName = string.Empty,
            DeviceCode = string.Empty,
            CashierId = string.Empty,
            CashierName = string.Empty,
            CashierSession = null,
            IsOnline = false,
            PendingSyncCount = 0
        };

        await _schema.InitializeAsync(cancellationToken);
        var startupResult = await _mainShellStartupService.EvaluateAfterServerSwitchAsync(
            Session,
            cancellationToken);
        Session = startupResult.Session;
        if (startupResult.RequiresDeviceRegistration)
        {
            DeviceRegistration = CreateDeviceRegistrationViewModel(startupOptions);
            _pendingDeviceRegistrationCache = startupResult.CachedDevice;
            DeviceRegistration.Prepare(startupResult.CachedDevice);
            CurrentScreen = DeviceRegistration;
            _deviceRegistrationStoreLoadTask = DeviceRegistration.ResumeActivationOrLoadLegacyAsync(startupResult.CachedDevice);
            return;
        }

        await InitializePosExperienceAsync(startupOptions);
        // 热切换必须等待后续启动链完成，确保失败能进入 PostCommitFailed，壳层也不会提前解冻。
        await ContinuePosStartupAfterShownAsync(startupOptions, CurrentOwner);
    }

    public Task ContinueStartupAfterShownAsync(AppStartupOptions startupOptions, Window? owner = null)
    {
        if (startupOptions.PreviewMode)
        {
            return Task.CompletedTask;
        }

        if (DeviceRegistration is not null && CurrentScreen == DeviceRegistration)
        {
            _deviceRegistrationStoreLoadTask ??= ContinueDeviceRegistrationStartupAsync(startupOptions);
            return _deviceRegistrationStoreLoadTask;
        }

        return ContinuePosStartupAfterShownAsync(startupOptions, owner);
    }

    private async Task ContinueDeviceRegistrationStartupAsync(AppStartupOptions startupOptions)
    {
        var registration = DeviceRegistration;
        if (registration is null)
        {
            return;
        }

        if (_startupActivationRecoveryIsUnreadable)
        {
            // 文件仍存在但不可读时只允许管理员修复本地权限/数据后重启，不能加载旧缓存。
            registration.IsBusy = true;
            registration.StatusMessage = _localization.T("deviceActivation.status.recoveryUnreadable");
            return;
        }

        await registration.ResumeActivationOrLoadLegacyAsync(_pendingDeviceRegistrationCache);
        if (_startupActivationRecoveryMode is null
            || !ReferenceEquals(DeviceRegistration, registration)
            || !ReferenceEquals(CurrentScreen, registration))
        {
            return;
        }

        var stillPending = await _deviceRegistrationWorkflowService.GetPendingActivationRecoveryModeAsync();
        if (stillPending is not null)
        {
            // 网络或响应仍不确定时，继续锁在恢复页，禁止旧缓存回到离线营业。
            registration.IsBusy = true;
            return;
        }

        // 确定性拒绝已经由 workflow 清除恢复记录，此时才重新应用既有设备缓存/离线规则。
        _startupActivationRecoveryMode = null;
        _startupActivationRecoveryIsUnreadable = false;
        var startupResult = await _mainShellStartupService.EvaluateAsync(Session, startupOptions.PreviewMode);
        Session = startupResult.Session;
        _pendingDeviceRegistrationCache = startupResult.CachedDevice;
        if (startupResult.RequiresDeviceRegistration)
        {
            registration.Prepare(startupResult.CachedDevice);
            await registration.ResumeActivationOrLoadLegacyAsync(startupResult.CachedDevice);
            return;
        }

        DeviceRegistration = null;
        await InitializePosExperienceAsync(startupOptions);
        _ = ContinuePosStartupAfterShownAsync(startupOptions, CurrentOwner);
    }

    public bool TryProcessKeyboardScannerInput(string barcode)
    {
        if (IsApiServerSwitching)
        {
            return true;
        }

        if (_operationAuthorizationService?.IsPromptOpen == true)
        {
            return _operationAuthorizationService.ProcessScannerBarcode(barcode);
        }

        if (IsDeviceReregistrationDialogOpen && DeviceRegistration is IScannerInputTarget registrationScannerTarget)
        {
            return registrationScannerTarget.ProcessScannerBarcode(
                barcode,
                "keyboard-focus-fallback",
                "keyboard-fallback");
        }

        if (ReferenceEquals(CurrentScreen, DeviceRegistration)
            && DeviceRegistration is IScannerInputTarget initialRegistrationScannerTarget)
        {
            return initialRegistrationScannerTarget.ProcessScannerBarcode(
                barcode,
                "keyboard-focus-fallback",
                "keyboard-fallback");
        }

        if (HasAsciiCaseInsensitiveActivationPrefix(barcode))
        {
            // 普通业务页永远不能把设备开通码当作商品、订单或员工条码继续处理。
            return true;
        }

        if (EmergencyLoginTokenCodec.HasSupportedPrefix(barcode.Trim()) &&
            TryProcessCashierLoginInput(barcode))
        {
            // 紧急令牌优先于当前商品页，禁止进入商品查询和页面日志。
            return true;
        }

        if (CurrentScreen is IScannerInputTarget scannerInputTarget)
        {
            return scannerInputTarget.ProcessScannerBarcode(
                barcode,
                "keyboard-focus-fallback",
                "keyboard-fallback");
        }

        if (TryProcessCashierLoginInput(barcode))
        {
            return true;
        }

        ConsoleLog.Write(
            "RawScanner",
            $"keyboard fallback scan ignored because active screen cannot handle scanner screen={CurrentScreen?.GetType().Name ?? "<none>"} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(barcode)}");
        return true;
    }

    public async Task LoginCashierByBarcodeAsync(string barcode, CancellationToken cancellationToken = default)
    {
        await ProcessCashierLoginInputAsync(barcode, updateFailureStatus: true, cancellationToken);
    }

    public Task<bool> TryLoginCashierFromScannerFallbackAsync(
        string barcode,
        CancellationToken cancellationToken = default)
    {
        return ProcessCashierLoginInputAsync(barcode, updateFailureStatus: false, cancellationToken);
    }

    private bool CanLoginCashierFromInput()
    {
        return !string.IsNullOrWhiteSpace(CashierBarcodeInput) && _cashierLoginService is not null;
    }

    private async Task LoginCashierFromInputAsync()
    {
        var input = CashierBarcodeInput.Trim();
        if (input.Length == 0)
        {
            StatusMessage = _localization.T("shell.cashierLogin.status.required");
            return;
        }

        CashierBarcodeInput = string.Empty;
        await ProcessCashierLoginInputAsync(input, updateFailureStatus: true, CancellationToken.None);
    }

    private bool TryProcessCashierLoginInput(string barcode)
    {
        if (_cashierLoginService is null)
        {
            return false;
        }

        _ = ProcessCashierLoginInputSafeAsync(barcode);
        return true;
    }

    private async Task ProcessCashierLoginInputSafeAsync(string barcode)
    {
        try
        {
            await ProcessCashierLoginInputAsync(barcode, updateFailureStatus: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("CashierLogin", $"cashier login input failed error={ex.Message}");
            StatusMessage = _localization.T("shell.cashierLogin.status.failed");
        }
    }

    private async Task<bool> ProcessCashierLoginInputAsync(
        string barcode,
        bool updateFailureStatus,
        CancellationToken cancellationToken)
    {
        // 手工检索取消后，紧急令牌和普通登录都不能再写入旧收银员会话。
        cancellationToken.ThrowIfCancellationRequested();
        var input = barcode.Trim();
        if (input.Length == 0)
        {
            return false;
        }

        if (_cashierLoginService is null)
        {
            return false;
        }

        CashierLoginResult result;
        try
        {
            result = await _cashierLoginService.LoginAsync(
                Session.StoreCode,
                Session.DeviceCode,
                input,
                attemptOnline: Session.IsOnline,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var correlation = OperationAuditEvents.CreateCorrelation();
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.CashierLogin,
                "Failed",
                CreateCashierLoginAuditSession(),
                reasonCode: "LOGIN_EXCEPTION",
                safeMessage: ex.GetType().Name,
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            ConsoleLog.WriteError(
                "CashierLogin",
                $"cashier login failed error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            throw;
        }

        // 某些登录服务无法及时响应取消；在应用成功或失败状态前再次拦截旧请求。
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Succeeded || result.Session is null)
        {
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.CashierLogin,
                "Denied",
                CreateCashierLoginAuditSession(),
                reasonCode: "LOGIN_REJECTED",
                safeMessage: result.Message);
            if (updateFailureStatus)
            {
                // 关键逻辑：界面只按稳定错误码本地化，避免直接显示后端语言或内部消息。
                StatusMessage = ResolveCashierLoginStatusMessage(result.ErrorCode);
            }

            return false;
        }

        var sessionApplyStopwatch = Stopwatch.StartNew();
        ApplyCashierSession(result.Session);
        sessionApplyStopwatch.Stop();
        ConsoleLog.Write(
            "CashierLoginTiming",
            $"sessionApplyMs={sessionApplyStopwatch.ElapsedMilliseconds} offlineCached={result.Session.IsOfflineCached}");
        OperationAuditEvents.RecordAction(
            _operationAuditLogger,
            OperationAuditTypes.CashierLogin,
            "Succeeded",
            Session,
            reasonCode: result.Session.IsOfflineCached ? "OFFLINE_CACHE" : "ONLINE");
        StatusMessage = result.Session.IsOfflineCached
            ? _localization.T("shell.cashierLogin.status.offlineSuccess")
            : _localization.T("shell.cashierLogin.status.success");
        await ReportRuntimeStatusSafeAsync(Session.IsOnline, cancellationToken);
        await RecoverPendingStartupCardPaymentAttemptAfterLoginAsync();
        await RecoverSharedHeldClaimsSafelyAsync();
        return true;
    }

    private string ResolveCashierLoginStatusMessage(string? errorCode)
    {
        var resourceKey = errorCode switch
        {
            "CASHIER_LOGIN_FAILED" => "shell.cashierLogin.status.invalidOrDisabled",
            "CASHIER_LOGIN_API_UNAVAILABLE" => "shell.cashierLogin.status.apiUnavailable",
            "EMERGENCY_LOGIN_SERVICE_UNAVAILABLE" => "shell.cashierLogin.status.emergencyServiceUnavailable",
            "EMERGENCY_TOKEN_EXPIRED" => "shell.cashierLogin.status.emergencyExpired",
            "EMERGENCY_TOKEN_NOT_ACTIVE" => "shell.cashierLogin.status.emergencyNotActive",
            "EMERGENCY_TOKEN_KEY_UNKNOWN" => "shell.cashierLogin.status.emergencyKeyUnknown",
            "EMERGENCY_TOKEN_TOO_LONG" => "shell.cashierLogin.status.emergencyTooLong",
            "EMERGENCY_CLOCK_ROLLBACK" => "shell.cashierLogin.status.emergencyClockRollback",
            "EMERGENCY_TOKEN_WRONG_STORE" => "shell.cashierLogin.status.emergencyWrongStore",
            _ when errorCode?.StartsWith("EMERGENCY_TOKEN_", StringComparison.Ordinal) == true =>
                "shell.cashierLogin.status.emergencyInvalid",
            _ => "shell.cashierLogin.status.failed"
        };

        return _localization.T(resourceKey);
    }

    private PosSessionState CreateCashierLoginAuditSession(CashierSessionDto? candidateSession = null)
    {
        // 登录尚未成功时不能使用 MainViewModel 的演示占位员工；仅保留已认证会话或明确候选身份。
        var establishedSession = candidateSession ?? _cashierSessionContext.CurrentSession ?? Session.CashierSession;
        return Session with
        {
            CashierId = establishedSession?.CashierId ?? string.Empty,
            CashierName = establishedSession?.CashierName ?? string.Empty,
            CashierSession = establishedSession
        };
    }

    private void ApplyCashierSession(CashierSessionDto cashierSession)
    {
        RevokeOperationAuthorizationsForSessionChange();
        // 切换收银员时只更新当前 POS 会话；紧急授权由专用登录服务验证，不写普通缓存。
        _cashierSessionContext.SetCurrent(cashierSession);
        Session = CashierSessionContext.ApplyToSession(Session, cashierSession);
    }

    private async Task ActivateDeviceAsync(DeviceActivatedEventArgs args, AppStartupOptions startupOptions)
    {
        _deviceRegistrationStoreLoadTask = null;
        _startupActivationRecoveryMode = null;
        _startupActivationRecoveryIsUnreadable = false;
        _posPostShowStartupTask = null;
        if (DeviceRegistration is not null)
        {
            DeviceRegistration.IsBusy = true;
            DeviceRegistration.StatusMessage = _localization.T("startup.stage.loadingProducts");
        }

        if (args.IsReregistered)
        {
            // 换店成功后旧设备和旧分店收银员会话均不可复用；购物车已由前置门禁保证为空。
            _mainShellStartupService.ClearAuthorization();
            _cart.Clear();
        }

        var activationSession = args.IsReregistered
            ? Session with
            {
                CashierId = string.Empty,
                CashierName = string.Empty,
                CashierSession = null
            }
            : Session;
        Session = activationSession with
        {
            StoreCode = args.StoreCode,
            StoreName = args.StoreName,
            DeviceCode = args.DeviceCode
        };
        if (!string.IsNullOrWhiteSpace(args.AuthorizationCode))
        {
            _mainShellStartupService.SetAuthorizedDevice(
                args.DeviceCode,
                args.StoreCode,
                args.HardwareId,
                args.AuthorizationCode);
        }

        await InitializePosExperienceAsync(startupOptions);
        IsDeviceReregistrationDialogOpen = false;
        DeviceRegistration = null;
        _ = ContinuePosStartupAfterShownAsync(startupOptions, CurrentOwner);
    }

    private async Task InitializePosExperienceAsync(AppStartupOptions startupOptions)
    {
        if (!startupOptions.PreviewMode)
        {
            await RecoverPendingInstallmentOperationsSafelyAsync();
            await RecoverSharedHeldClaimsSafelyAsync();
        }

        _screenNavigator.ClearScreens();
        _screenNavigator.SetCachedPosTerminalScreen(null);
        _screenNavigator.SetCachedSpecialProductsScreen(null);
        CancelStartupCatalogIndexLoad();
        IReadOnlyList<SellableItemDto> cachedItems = [];
        if (startupOptions.PreviewMode)
        {
            cachedItems = CreateStarterItems();
            await _shellCatalogService.ReplacePreviewCatalogAsync(cachedItems);
        }

        var posWorkflowService = _posTerminalWorkflowFactory(RefreshRemoteLookupAsync, ReloadCatalogIndexAsync);
        PosTerminal = _mainChildViewModelFactory.CreatePosTerminalViewModel(
            Session,
            _screenNavigator.ShowCashPayment,
            _screenNavigator.ShowSpecialProductsAsync,
            onHoldOrderAsync: _screenNavigator.SuspendCurrentOrderAsync,
            onRecallOrderAsync: _screenNavigator.ShowSuspendedHistoryAsync,
            onOpenHistoryAsync: _screenNavigator.ShowHistoryAsync,
            onOpenDailyCloseAsync: _screenNavigator.ShowDailyCloseAsync,
            onOpenSettingsAsync: _screenNavigator.ShowSettingsAsync,
            onOpenCustomerDisplay: _screenNavigator.ShowCustomerDisplay,
            syncCatalogAsync: SyncCatalogAndReloadAsync,
            resetCatalogAsync: ResetCatalogAndReloadAsync,
            refreshOnlineAsync: RefreshOnlineStateAsync,
            onReregisterDeviceAsync: BeginDeviceReregistrationFromPosAsync,
            workflowService: posWorkflowService,
            onOpenReturns: _screenNavigator.ShowReturns,
            onPrintLastReceiptAsync: PrintLatestReceiptAsync,
            onOpenCashDrawerAsync: OpenCashDrawerAsync,
            onExitApplicationAsync: ExitApplicationAsync,
            tryLoginCashierFromScannerFallbackAsync: TryLoginCashierFromScannerFallbackAsync,
            onLockCashierAsync: LockCashierAsync,
            onOpenCardRecoveryCenterAsync: _screenNavigator.ShowCardRecoveryCenterAsync);
        // POS 页面在启动或重注册后才创建，考勤面板必须在此处随新页面挂载。
        PosTerminal.AttendanceQrPanel = _attendanceQrPanel;
        SpecialProducts = _mainChildViewModelFactory.CreateSpecialProductsViewModel(
            Session,
            _screenNavigator.ShowPos,
            line => PosTerminal?.RevealCartLine(line));
        _screenNavigator.SetCachedPosTerminalScreen(PosTerminal);
        ReceiptReturns = _mainChildViewModelFactory.CreateReceiptReturnsViewModel(
            Session,
            _screenNavigator.ShowPos,
            line => PosTerminal?.RevealCartLine(line));
        if (cachedItems.Count > 0)
        {
            PosTerminal.LoadMatches(cachedItems);
        }

        TransactionHistory = _screenNavigator.CreateTransactionHistoryViewModel();

        if (startupOptions.PreviewMode)
        {
            AddPreviewCartItems(cachedItems);
            _lastCompletedOrder = await CreatePreviewOrderAsync(cachedItems);
        }

        await RefreshPendingSyncAsync();
        RefreshClock();
        _clockTimer.Start();
        _screenNavigator.ApplySessionToScreens();
        _screenNavigator.PrepareCachedCashPaymentScreen();
        // 诊断启动卡顿时先关闭客显预热，避免启动阶段创建隐藏窗口。
        ConsoleLog.Write(
            "CustomerDisplay",
            $"startup prewarm skipped store={Session.StoreCode} device={Session.DeviceCode} reason=auto-open-disabled");
        await BeginStartupCatalogIndexLoadAsync(startupOptions);
        await PreloadStartupSpecialProductsDataAsync(startupOptions);
        _screenNavigator.NavigateFromStartup(startupOptions.InitialScreen);
    }

    private async Task RecoverPendingInstallmentOperationsSafelyAsync()
    {
        try
        {
            // 中文注释：服务端/本地操作使用 CAS 领取状态；此处不重置或读取购物车，启动与服务切换可安全重复调用。
            var recovered = await Task.Run(
                () => _installmentOrderService.RecoverPendingOperationsAsync(Session, CancellationToken.None),
                CancellationToken.None);
            if (recovered.Count > 0)
            {
                ConsoleLog.Write(
                    "InstallmentRecovery",
                    $"startup recovery completed store={Session.StoreCode} count={recovered.Count}");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 未知金融结果必须继续保持锁定；启动恢复失败不能阻断 POS 或清空后来建立的购物车。
            ConsoleLog.WriteError(
                "InstallmentRecovery",
                $"startup recovery failed store={Session.StoreCode} error={ex.GetType().Name}",
                null,
                ex);
        }
    }

    /// <summary>
    /// 登录/启动恢复会话后 best-effort 执行本地共享挂单 claim 恢复：
    /// 只恢复 OfflineOrigin 本地事实（Prepared 补激活、Active 回灌购物车），
    /// 不访问 API，API 离线绝不能阻止登录/启动；任何异常只记录日志。
    /// </summary>
    private async Task RecoverSharedHeldClaimsSafelyAsync()
    {
        if (_sharedHeldOrderCoordinator is null || Session.CashierSession is null)
        {
            return;
        }

        try
        {
            var recovered = await _sharedHeldOrderCoordinator.RecoverLocalClaimsAsync(
                Session,
                CancellationToken.None);
            if (recovered.RestoredClaimIds.Count > 0)
            {
                ConsoleLog.Write(
                    "SharedHeldRecovery",
                    $"local claim recovery completed store={Session.StoreCode} count={recovered.RestoredClaimIds.Count}");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "SharedHeldRecovery",
                $"local claim recovery failed store={Session.StoreCode} error={ex.GetType().Name}",
                null,
                ex);
        }

        try
        {
            // 本地恢复完成后再用 claims/mine 调和 RemoteClaim 崩溃窗口；
            // API 离线只记录并保留 durable 事实，绝不回滚登录或自动释放 Active。
            var reconciled = await _sharedHeldOrderCoordinator.ReconcileClaimsAsync(
                Session,
                CancellationToken.None);
            if (reconciled.RestoredClaimIds.Count > 0)
            {
                ConsoleLog.Write(
                    "SharedHeldRecovery",
                    $"remote claim recovery completed store={Session.StoreCode} count={reconciled.RestoredClaimIds.Count}");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "SharedHeldRecovery",
                $"remote claim recovery deferred store={Session.StoreCode} error={ex.GetType().Name}",
                null,
                ex);
        }
    }

    private Task ContinuePosStartupAfterShownAsync(AppStartupOptions startupOptions, Window? owner)
    {
        if (startupOptions.PreviewMode)
        {
            return Task.CompletedTask;
        }

        _posPostShowStartupTask ??= ContinuePosStartupAfterShownCoreAsync(owner);
        return _posPostShowStartupTask;
    }

    private async Task ContinuePosStartupAfterShownCoreAsync(Window? owner)
    {
        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"post-show open start store={Session.StoreCode} device={Session.DeviceCode} ownerPresent={owner is not null}");
        try
        {
            // 诊断启动卡顿时关闭客显自动打开；手动客显按钮仍可正常打开。
            ConsoleLog.Write(
                "CustomerDisplay",
                $"post-show open skipped store={Session.StoreCode} device={Session.DeviceCode} ownerPresent={owner is not null} reason=auto-open-disabled");
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"post-show open completed store={Session.StoreCode} device={Session.DeviceCode} displayMode={CustomerDisplayWindowMode} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"post-show open failed store={Session.StoreCode} device={Session.DeviceCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }

        ConsoleLog.Write(
            "SpecialProducts",
            $"startup home preload skipped store={Session.StoreCode} reason=moved-before-main-window");
        await RecoverStartupCardPaymentAttemptAsync();
        await RefreshOnlineStateAsync(CancellationToken.None, autoRetryOrders: true);
        _connectivityTimer.Start();
        BeginInitialCatalogSync();
    }

    private async Task<IReadOnlyList<SellableItemDto>> LoadStartupCatalogIndexAsync(CancellationToken cancellationToken)
    {
        return await _catalogStartupCoordinator.LoadLocalCatalogForStartupAsync(
            Session.StoreCode,
            cancellationToken,
            items => PosTerminal?.LoadMatches(items),
            () => { PosTerminal?.RefreshCart(); CashPayment?.RefreshCart(); });
    }

    private async Task<IReadOnlyList<SellableItemDto>> BeginStartupCatalogIndexLoadAsync(AppStartupOptions startupOptions)
        => await _catalogStartupCoordinator.LoadStartupCatalogIndexAsync(
            Session.StoreCode,
            startupOptions.PreviewMode,
            items => PosTerminal?.LoadMatches(items),
            () => { PosTerminal?.RefreshCart(); CashPayment?.RefreshCart(); });

    private void CancelStartupCatalogIndexLoad() => _catalogStartupCoordinator.CancelStartupLoad();

    partial void OnSessionChanged(PosSessionState value)
    {
        if (value.CashierSession is not null)
        {
            if (!ReferenceEquals(_cashierSessionContext.CurrentSession, value.CashierSession))
            {
                RevokeOperationAuthorizationsForSessionChange();
            }
            _cashierSessionContext.SetCurrent(value.CashierSession);
        }
        else
        {
            // 关键逻辑：登出后立即销毁待授权操作，授权不能跨收银员会话继续。
            RevokeOperationAuthorizationsForSessionChange();
            _cashierSessionContext.Clear();
        }

        _screenNavigator.Session = value;
        RefreshLocalizedShell();
        _screenNavigator.ApplySessionToScreens();
        OnPropertyChanged(nameof(IsCashierLoginOverlayOpen));
    }

    partial void OnCashierBarcodeInputChanged(string value)
    {
        LoginCashierCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 屏幕切换的统一入口：通知绑定刷新 CurrentScreen、缓存屏幕、active state、页面标题和扫码 active page。
    /// 集中在此方法内，避免多处分散通知导致的绑定遗漏（如"顶部显示收银但内容为空"）。
    /// </summary>
    private void OnScreenChanged(object? screen)
    {
        if (IsApiServerSwitching &&
            !_allowApiServerSwitchNavigation &&
            !ReferenceEquals(screen, _apiServerSwitchFrozenScreen))
        {
            _screenNavigator.SetCurrentScreen(_apiServerSwitchFrozenScreen);
            return;
        }

        _operationAuthorizationService?.Cancel();
        OnPropertyChanged(nameof(CurrentScreen));
        if (!ReferenceEquals(screen, _screenNavigator!.ReceiptReturns))
        {
            _screenNavigator.ReceiptReturns?.ResetToDefault();
        }

        RaiseScreenHostStateChanged();
        OnPropertyChanged(nameof(IsCashierLoginOverlayOpen));
        RefreshActiveScannerPage();
    }

    private void OnOperationAuthorizationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IOperationAuthorizationService.IsPromptOpen))
        {
            return;
        }

        // 关键逻辑：授权开始时抢占扫码页，结束后恢复当前页面，不改变当前收银员或页面。
        _rawScannerService.ClearPendingInput();
        RefreshActiveScannerPage();
    }

    partial void OnIsApiServerSwitchingChanged(bool value)
    {
        if (!value)
        {
            _apiServerSwitchFrozenScreen = null;
            RefreshActiveScannerPage();
            return;
        }

        // 切换边界关闭所有常用交互入口，避免扫描、导航或临时提权生成新的旧环境工作。
        _apiServerSwitchFrozenScreen = CurrentScreen;
        _operationAuthorizationService?.Cancel();
        _rawScannerService.ClearPendingInput();
        _rawScannerService.SetActivePage(null);
    }

    private void OnOperationAuthorizationBarcodeScanned(RawBarcodeScannedEventArgs args)
    {
        _operationAuthorizationService?.ProcessScannerBarcode(args.Barcode);
    }

    private void RefreshActiveScannerPage()
    {
        _rawScannerService.SetGlobalBarcodeInterceptor(ShouldConsumeReservedActivationBarcode);
        if (IsApiServerSwitching)
        {
            _rawScannerService.SetActivePage(null);
            return;
        }

        var pageId = _operationAuthorizationService?.IsPromptOpen == true
            ? _operationAuthorizationService.ScannerPageId
            : IsDeviceReregistrationDialogOpen
                ? (DeviceRegistration as IScannerInputTarget)?.ScannerPageId
                : (CurrentScreen as IScannerInputTarget)?.ScannerPageId;
        _rawScannerService.SetActivePage(pageId);
    }

    private bool ShouldConsumeReservedActivationBarcode(RawBarcodeScannedEventArgs args)
    {
        if (!HasAsciiCaseInsensitiveActivationPrefix(args.Barcode))
        {
            return false;
        }

        // 授权弹窗、换店弹窗、首次开通页依次保留现有扫码优先级；其余页面全局吞掉开通码。
        return _operationAuthorizationService?.IsPromptOpen != true
            && !IsDeviceReregistrationDialogOpen
            && !(DeviceRegistration is not null && ReferenceEquals(CurrentScreen, DeviceRegistration));
    }

    private static bool HasAsciiCaseInsensitiveActivationPrefix(string? value)
    {
        const string prefix = "HBDEV1-";
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var prefixIndex = 0;
        foreach (var inputCharacter in value)
        {
            if (inputCharacter is ' ' or '\t' or '\n' or '\v' or '\f' or '\r')
            {
                continue;
            }

            var character = inputCharacter;
            if (character is >= 'a' and <= 'z')
            {
                character = (char)(character - ('a' - 'A'));
            }

            if (character != prefix[prefixIndex])
            {
                return false;
            }

            prefixIndex++;
            if (prefixIndex == prefix.Length)
            {
                return true;
            }
        }

        return false;
    }

    private void RaiseScreenHostStateChanged()
    {
        // 缓存屏幕在 ScreenNavigator 内部创建，切换页面时必须通知宿主重新读取内容绑定。
        OnPropertyChanged(nameof(CachedPosTerminalScreen));
        OnPropertyChanged(nameof(CachedCashPaymentScreen));
        OnPropertyChanged(nameof(CachedSpecialProductsScreen));
        OnPropertyChanged(nameof(CachedTransactionHistoryScreen));
        OnPropertyChanged(nameof(CachedSettingsScreen));
        OnPropertyChanged(nameof(IsPosTerminalScreenActive));
        OnPropertyChanged(nameof(IsCashPaymentScreenActive));
        OnPropertyChanged(nameof(IsSpecialProductsScreenActive));
        OnPropertyChanged(nameof(IsTransactionHistoryScreenActive));
        OnPropertyChanged(nameof(IsSettingsScreenActive));
        OnPropertyChanged(nameof(IsFallbackScreenActive));
        OnPropertyChanged(nameof(ActivePageTitleText));
    }

    partial void OnCustomerDisplayWindowModeChanged(CustomerDisplayWindowMode value)
    {
        IsCustomerDisplayOpen = value != CustomerDisplayWindowMode.Closed;
    }

    partial void OnIsDeviceReregistrationDialogOpenChanged(bool value)
    {
        // 关键逻辑：设备重注册弹窗优先级更高，打开或关闭时都要重新计算收银员登录遮盖。
        OnPropertyChanged(nameof(IsCashierLoginOverlayOpen));
        _rawScannerService.ClearPendingInput();
        RefreshActiveScannerPage();
    }

    partial void OnSelectedCultureNameChanged(string value)
    {
        if (_isApplyingCulture || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _ = ApplyLanguageSafeAsync(value);
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedShell();
        OnPropertyChanged(nameof(SelectedCultureName));
    }

    private async Task RestoreLanguageAsync(AppStartupOptions startupOptions)
    {
        ApplySelectedCultureName(await _shellCultureService.RestoreAsync(startupOptions, _schemaReady));
    }

    private async Task ApplyLanguageSafeAsync(string cultureName)
    {
        var requestVersion = Interlocked.Increment(ref _languageSaveRequestVersion);
        try
        {
            var applyTask = _shellCultureService.ApplyAsync(cultureName, persist: true, _schemaReady);
            // 服务会在首个 await 前切换运行文化，选择器也要立即反映本次选择。
            ApplySelectedCultureName(_localization.CurrentCulture.Name);
            var appliedCulture = await applyTask;
            if (requestVersion != Volatile.Read(ref _languageSaveRequestVersion))
            {
                return;
            }

            ApplySelectedCultureName(appliedCulture);
        }
        catch (Exception ex)
        {
            if (requestVersion != Volatile.Read(ref _languageSaveRequestVersion))
            {
                return;
            }

            // SetCulture 已先于本地设置写入执行；保存失败时保留本次运行语言，并让选择器回显实际文化。
            ConsoleLog.WriteError("ShellCulture", "language persistence failed", null, ex);
            ApplySelectedCultureName(_localization.CurrentCulture.Name);
            StatusMessage = _localization.T("shell.language.saveFailedRestartMayRevert");
        }
    }

    private Task ToggleCultureAsync()
    {
        var nextCultureName = string.Equals(
            _localization.CurrentCulture.Name,
            LocalizationService.ChineseCultureName,
            StringComparison.OrdinalIgnoreCase)
            ? LocalizationService.DefaultCultureName
            : LocalizationService.ChineseCultureName;
        return ApplyLanguageSafeAsync(nextCultureName);
    }

    private void RefreshLocalizedShell(bool resetStatus = false)
    {
        OnlineStateText = _localization.T(Session.IsOnline ? "Online" : "Offline");
        _syncOrchestrator?.RefreshLocalizedText();
        TerminalInfo = string.Format(_localization.CurrentCulture, _localization.T("shell.footer.terminalInfo"), Session.DeviceCode);
        StoreInfo = string.Format(_localization.CurrentCulture, _localization.T("shell.top.storeInfo"), Session.StoreName, Session.StoreCode);
        CashierInfo = string.Format(_localization.CurrentCulture, _localization.T("shell.top.cashierInfo"), Session.CashierName);
        VersionStatusText = _localization.T("shell.footer.versionReady");
        OnPropertyChanged(nameof(ActivePageTitleText));
        if (resetStatus || string.IsNullOrWhiteSpace(StatusMessage))
        {
            StatusMessage = _localization.T("StatusOfflineReady");
        }
    }

    private string GetActivePageTitleText()
    {
        // 启动更新闸门可能在页面 VM 创建前显示主窗口，两个 null 不能被误判为同一页面。
        if (InstallmentCenter is not null && ReferenceEquals(CurrentScreen, InstallmentCenter))
        {
            return InstallmentCenter?.PageTitleText ?? "分期中心";
        }

        if (InstallmentCreate is not null && ReferenceEquals(CurrentScreen, InstallmentCreate))
        {
            return InstallmentCreate?.PageTitleText ?? "创建分期";
        }

        return _localization.T(GetActivePageTitleKey());
    }

    private string GetActivePageTitleKey()
    {
        if (CurrentScreen is null)
        {
            return "shell.page.pos";
        }

        if (ReferenceEquals(CurrentScreen, PosTerminal))
        {
            return "shell.page.pos";
        }

        if (ReferenceEquals(CurrentScreen, CashPayment))
        {
            return CashPayment?.PaymentMode switch
            {
                PaymentEntryMode.Refund => "shell.page.refund",
                PaymentEntryMode.ZeroSettlement => "shell.page.zeroSettlement",
                _ => "shell.page.payment"
            };
        }

        if (ReferenceEquals(CurrentScreen, SpecialProducts))
        {
            return "shell.page.specialProducts";
        }

        if (ReferenceEquals(CurrentScreen, ReceiptReturns))
        {
            return "shell.page.returns";
        }

        if (ReferenceEquals(CurrentScreen, TransactionHistory))
        {
            return "shell.page.history";
        }

        if (ReferenceEquals(CurrentScreen, DailyClose))
        {
            return "shell.page.dailyClose";
        }

        if (ReferenceEquals(CurrentScreen, Settings))
        {
            return "shell.page.settings";
        }

        if (ReferenceEquals(CurrentScreen, _screenNavigator.CardRecoveryCenter))
        {
            return "shell.page.cardRecovery";
        }

        if (ReferenceEquals(CurrentScreen, CustomerDisplay))
        {
            return "shell.page.customerDisplay";
        }

        if (ReferenceEquals(CurrentScreen, DeviceRegistration))
        {
            return "shell.page.deviceRegistration";
        }

        return "shell.page.loading";
    }

    private async Task RefreshPendingSyncAsync()
    {
        if (_syncOrchestrator is not null)
        {
            await _syncOrchestrator.RefreshPendingSyncAsync();
        }
    }


    private DeviceRegistrationViewModel CreateDeviceRegistrationViewModel(AppStartupOptions startupOptions)
    {
        return _mainChildViewModelFactory.CreateDeviceRegistrationViewModel(
            args => ActivateDeviceAsync(args, startupOptions),
            ApplyDeviceReregistered,
            CancelDeviceReregistration);
    }

    private async Task ClearActivationRecoverySafeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _deviceRegistrationWorkflowService.ClearActivationRecoveryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 已验证设备不能因临时恢复记录清理失败而阻断营业；日志不得包含一次性开通码。
            ConsoleLog.Write(
                "DeviceActivation",
                $"activation recovery cleanup failed error={ex.GetType().Name}");
        }
    }

    private void ApplySelectedCultureName(string cultureName)
    {
        _isApplyingCulture = true;
        SelectedCultureName = cultureName;
        _isApplyingCulture = false;
    }

    private void ApplySyncCenterSnapshot(ShellSyncCenterSnapshot snapshot)
    {
        _syncOrchestrator?.ApplySyncCenterSnapshot(snapshot);
    }

    private void RefreshClock()
    {
        CurrentTime = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private void BeginInitialCatalogSync()
    {
        if (!Session.IsOnline)
        {
            return;
        }

        _ = TryInitialCatalogSyncAsync();
    }

    private void BeginSpecialProductsHomePreload()
    {
        if (SpecialProducts is null)
        {
            return;
        }

        _screenNavigator.PrepareCachedSpecialProductsScreen();
        _ = TryPreloadSpecialProductsHomeAsync();
    }

    private async Task PreloadStartupSpecialProductsDataAsync(AppStartupOptions startupOptions)
    {
        if (startupOptions.PreviewMode || SpecialProducts is null)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write("SpecialProducts", $"startup data preload start store={Session.StoreCode}");
        try
        {
            _screenNavigator.PrepareCachedSpecialProductsScreen();
            // 启动阶段只预加载特殊商品数据，图片缩略图留给页面进入后异步加载。
            await SpecialProducts.PreloadAsync(CancellationToken.None);
            stopwatch.Stop();
            ConsoleLog.Write(
                "SpecialProducts",
                $"startup data preload completed store={Session.StoreCode} items={SpecialProducts.SpecialItems.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write(
                "SpecialProducts",
                $"startup data preload failed store={Session.StoreCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
        }
    }

    private async Task TryPreloadSpecialProductsHomeAsync()
    {
        try
        {
            if (SpecialProducts is not null)
            {
                await SpecialProducts.PreloadFirstPageThumbnailsAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("SpecialProducts", $"startup home preload failed store={Session.StoreCode} error={ex.Message}");
        }
    }

    private async Task<bool> RefreshOnlineStateAsync(CancellationToken cancellationToken)
    {
        return await RefreshOnlineStateAsync(cancellationToken, autoRetryOrders: false);
    }

    private async Task<bool> RefreshOnlineStateAsync(CancellationToken cancellationToken, bool autoRetryOrders)
    {
        Task<bool> refreshTask;
        lock (_connectivityRefreshSync)
        {
            if (IsLifetimeEnding || _connectivityRefreshTask is not null)
            {
                return Session.IsOnline;
            }

            var publicationGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            refreshTask = RefreshOnlineStateAfterPublicationAsync(
                publicationGate.Task,
                cancellationToken,
                autoRetryOrders);
            _connectivityRefreshTask = refreshTask;
            publicationGate.TrySetResult();
        }

        try
        {
            return await refreshTask;
        }
        finally
        {
            lock (_connectivityRefreshSync)
            {
                if (ReferenceEquals(_connectivityRefreshTask, refreshTask))
                {
                    _connectivityRefreshTask = null;
                }
            }
        }
    }

    private async Task<bool> RefreshOnlineStateAfterPublicationAsync(
        Task publicationGate,
        CancellationToken cancellationToken,
        bool autoRetryOrders)
    {
        await publicationGate;
        return await RefreshOnlineStateCoreAsync(cancellationToken, autoRetryOrders);
    }

    private async Task<bool> RefreshOnlineStateCoreAsync(CancellationToken cancellationToken, bool autoRetryOrders)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownCancellation.Token);
        var refreshCancellation = linkedCancellation.Token;
        try
        {
            var isOnline = await _connectivityApiClient.CheckOnlineAsync(refreshCancellation);
            if (!TryApplyConnectivityState(isOnline))
            {
                return Session.IsOnline;
            }

            if (IsLifetimeEnding)
            {
                return Session.IsOnline;
            }

            await ReportRuntimeStatusSafeAsync(isOnline, refreshCancellation);
            if (IsLifetimeEnding)
            {
                return Session.IsOnline;
            }

            if (isOnline && autoRetryOrders)
            {
                await TryAutoRetryPendingOrdersAsync(refreshCancellation);
            }

            return isOnline;
        }
        catch (OperationCanceledException) when (IsLifetimeEnding)
        {
            return Session.IsOnline;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (IsLifetimeEnding)
            {
                return Session.IsOnline;
            }

            // API 探测失败只代表当前离线，不能阻断已授权设备继续使用本地缓存收银。
            ConsoleLog.Write("Connectivity", $"online check failed; fallback offline error={ex.Message}");
            if (!TryApplyConnectivityState(false))
            {
                return Session.IsOnline;
            }

            if (IsLifetimeEnding)
            {
                return Session.IsOnline;
            }

            await ReportRuntimeStatusSafeAsync(false, refreshCancellation);
            return false;
        }
    }

    private bool TryApplyConnectivityState(bool isOnline)
    {
        lock (_connectivityRefreshSync)
        {
            if (IsLifetimeEnding)
            {
                return false;
            }

            if (Session.IsOnline != isOnline)
            {
                Session = Session with { IsOnline = isOnline };
            }

            return true;
        }
    }

    private async Task TryInitialCatalogSyncAsync()
    {
        var hasLocalCatalogItems = await _catalogStartupCoordinator.HasLocalCatalogItemsAsync(Session.StoreCode);
        var syncMode = hasLocalCatalogItems ? "background-refresh" : "initial-full-download";
        try
        {
            // 启动后的商品同步已在 POS 显示之后执行，不能再用启动超时截断，避免半包缓存反复无法补全。
            ConsoleLog.Write(
                "CatalogSync",
                $"initial sync mode={syncMode} localItemCount={(hasLocalCatalogItems ? ">0" : "0")} timeoutSeconds=none");
            var cachedItems = await SyncCatalogAndReloadAsync(CancellationToken.None);
            PosTerminal?.LoadMatches(cachedItems);
            PosTerminal?.RefreshCart();
            CashPayment?.RefreshCart();
        }
        catch (OperationCanceledException)
        {
            ConsoleLog.Write("CatalogSync", $"initial sync canceled mode={syncMode} timeoutSeconds=none");
            StatusMessage = hasLocalCatalogItems
                ? _localization.T("main.catalogSync.timedOut")
                : _localization.T("main.catalogSync.initialDownloadTimedOut");
        }
        catch (Exception ex)
        {
            var statusKey = hasLocalCatalogItems
                ? "main.catalogSync.failed"
                : "main.catalogSync.initialDownloadFailed";
            StatusMessage = string.Format(_localization.CurrentCulture, _localization.T(statusKey), ex.Message);
        }
    }

    private Task TryAutoRetryPendingOrdersAsync(CancellationToken cancellationToken) =>
        _syncOrchestrator?.TryAutoRetryPendingAsync(cancellationToken) ?? Task.CompletedTask;

    private Task<RemoteLookupRefreshResult> RefreshRemoteLookupAsync(
        string storeCode,
        string lookupCode,
        CancellationToken cancellationToken)
    {
        return _remoteLookupRefresh.RefreshLookupAsync(storeCode, lookupCode, cancellationToken);
    }

    private Task<IReadOnlyList<SellableItemDto>> SyncCatalogAndReloadAsync(CancellationToken cancellationToken)
    {
        return SyncCatalogAndReloadAsync(forceFullDownload: false, cancellationToken);
    }

    private Task<IReadOnlyList<SellableItemDto>> ResetCatalogAndReloadAsync(CancellationToken cancellationToken)
    {
        return SyncCatalogAndReloadAsync(forceFullDownload: true, cancellationToken);
    }

    private async Task<IReadOnlyList<SellableItemDto>> SyncCatalogAndReloadAsync(
        bool forceFullDownload,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<CatalogSyncProgress>(ApplyCatalogDownloadProgress);
        return await _shellCatalogService.SyncCatalogAndReloadAsync(
            Session.StoreCode,
            forceFullDownload,
            progress,
            cancellationToken);
    }

    private void ApplyCatalogDownloadProgress(CatalogSyncProgress progress)
    {
        _catalogDownloadHideTimer.Stop();
        IsCatalogDownloadProgressVisible = true;
        IsCatalogDownloadProgressFailed = progress.Stage == CatalogSyncProgressStage.Failed;

        if (progress.Stage == CatalogSyncProgressStage.Failed)
        {
            CatalogDownloadProgressText = _localization.T("shell.catalogDownload.failed");
            CatalogDownloadProgressDetailText = progress.ErrorMessage ?? string.Empty;
            StartCatalogDownloadHideTimer(TimeSpan.FromSeconds(15));
            return;
        }

        var stagePercent = CalculateCatalogDownloadStagePercent(progress);
        CatalogDownloadProgressValue = stagePercent;

        if (progress.Stage is CatalogSyncProgressStage.Preparing or CatalogSyncProgressStage.Comparing)
        {
            var stageTitleKey = progress.Stage == CatalogSyncProgressStage.Preparing
                ? "shell.catalogDownload.preparing"
                : "shell.catalogDownload.comparing";
            CatalogDownloadProgressText = string.Format(
                _localization.CurrentCulture,
                "{0} {1}%",
                _localization.T(stageTitleKey),
                stagePercent);
            CatalogDownloadProgressDetailText = string.Format(
                _localization.CurrentCulture,
                _localization.T("shell.catalogDownload.compareDetail"),
                progress.ComparedCount,
                progress.ComparePages,
                progress.UpsertedCount,
                progress.DeletedCount,
                FormatElapsed(progress.ElapsedMilliseconds));
            return;
        }

        var titleKey = progress.Stage == CatalogSyncProgressStage.Completed
            ? "shell.catalogDownload.completed"
            : "shell.catalogDownload.downloading";
        CatalogDownloadProgressText = string.Format(
            _localization.CurrentCulture,
            _localization.T(titleKey),
            stagePercent);
        CatalogDownloadProgressDetailText = string.Format(
            _localization.CurrentCulture,
            _localization.T("shell.catalogDownload.detail"),
            progress.DownloadedCount,
            progress.TotalCount,
            progress.RemotePages,
            progress.UpsertedCount,
            progress.DeletedCount,
            FormatElapsed(progress.ElapsedMilliseconds));

        if (progress.Stage == CatalogSyncProgressStage.Completed)
        {
            StartCatalogDownloadHideTimer(TimeSpan.FromSeconds(5));
        }
    }

    private static int CalculateCatalogDownloadStagePercent(CatalogSyncProgress progress)
    {
        // 核对和下载分别按当前阶段计算，阶段切换时允许重新从 0% 开始。
        return progress.Stage switch
        {
            CatalogSyncProgressStage.Preparing => 0,
            CatalogSyncProgressStage.Comparing when progress.TotalCount <= 0 => 0,
            CatalogSyncProgressStage.Comparing => Math.Clamp(
                (int)(progress.ComparedCount * 100L / progress.TotalCount),
                0,
                100),
            CatalogSyncProgressStage.Completed => 100,
            _ => Math.Clamp(progress.Percent, 0, 100)
        };
    }

    private void StartCatalogDownloadHideTimer(TimeSpan interval)
    {
        _catalogDownloadHideTimer.Interval = interval;
        _catalogDownloadHideTimer.Start();
    }

    private string FormatElapsed(long elapsedMilliseconds)
    {
        return string.Format(
            _localization.CurrentCulture,
            _localization.T("shell.catalogDownload.elapsedSeconds"),
            elapsedMilliseconds / 1000d);
    }

    private async Task<IReadOnlyList<SellableItemDto>> ReloadCatalogIndexAsync(CancellationToken cancellationToken = default)
    {
        return await ReloadCatalogIndexAsync(Session.StoreCode, cancellationToken);
    }

    private async Task<IReadOnlyList<SellableItemDto>> ReloadCatalogIndexAsync(string storeCode, CancellationToken cancellationToken)
    {
        return await _shellCatalogService.LoadLocalCatalogAsync(storeCode, cancellationToken);
    }

    private void PrewarmCustomerDisplay() => _customerDisplayShellController.Prewarm();

    private async Task BeginDeviceReregistrationFromPosAsync()
    {
        await BeginDeviceReregistrationAsync();
    }

    private async Task<DeviceReregistrationStartResult> BeginDeviceReregistrationAsync()
    {
        var blocked = await _deviceReregistrationCoordinator.CheckCanBeginAsync();
        if (blocked is not null)
        {
            return blocked;
        }

        var startupOptions = _startupOptions ?? new AppStartupOptions([], false, null, null);
        DeviceRegistration = CreateDeviceRegistrationViewModel(startupOptions);
        _startupActivationRecoveryMode = null;
        _startupActivationRecoveryIsUnreadable = false;
        _pendingDeviceRegistrationCache = null;
        _deviceRegistrationStoreLoadTask = null;
        _screenNavigator.ClearCashPaymentCache();
        DeviceRegistration.PrepareReregister(Session.StoreCode);
        IsDeviceReregistrationDialogOpen = true;
        return DeviceReregistrationStartResult.StartedWith(DeviceRegistration.StatusMessage);
    }

    private void ApplyDeviceReregistered()
    {
        _deviceReregistrationCoordinator.ClearAuthorization();
        _posPostShowStartupTask = null;
        CancelStartupCatalogIndexLoad();
        _screenNavigator.ClearScreens();
        _screenNavigator.SetCachedPosTerminalScreen(null);
        _screenNavigator.SetCachedSpecialProductsScreen(null);
        _screenNavigator.PosTerminal = null;
        _screenNavigator.SpecialProducts = null;
        _screenNavigator.InstallmentCenter = null;
        _screenNavigator.InstallmentCreate = null;
        _screenNavigator.ReceiptReturns = null;
        _screenNavigator.TransactionHistory = null;
        _lastCompletedOrder = null;
        _cart.Clear();
        SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Closed, CurrentOwner);
        StatusMessage = _deviceReregistrationCoordinator.SubmittedStatusMessage;
    }

    private void CancelDeviceReregistration()
    {
        var wasCurrentScreen = ReferenceEquals(CurrentScreen, DeviceRegistration);
        if (PosTerminal is null)
        {
            IsDeviceReregistrationDialogOpen = false;
            DeviceRegistration = null;
            return;
        }

        DeviceRegistration = null;
        IsDeviceReregistrationDialogOpen = false;
        _deviceRegistrationStoreLoadTask = null;
        if (wasCurrentScreen)
        {
            _screenNavigator.ShowPos();
        }
        StatusMessage = _deviceReregistrationCoordinator.CancelStatusMessage;
    }

    private void OnCartChanged(object? sender, EventArgs e)
    {
        _screenNavigator.RefreshCartRelatedState();
        _screenNavigator.LoadCustomerDisplayFromCart();
    }

    private Task<bool> RecoverCardPaymentAttemptAsync(bool navigateToPaymentOnDraft) =>
        _cardRecoveryPresenter?.RecoverCardPaymentAttemptAsync(navigateToPaymentOnDraft) ?? Task.FromResult(false);

    private async Task RecoverStartupCardPaymentAttemptAsync()
    {
        if (Session.CashierSession is null)
        {
            // 启动阶段只统计异常数量；等收银员身份建立后再按当前门店、设备与环境查询。
            _startupCardRecoveryPendingAfterCashierLogin = true;
            return;
        }

        _startupCardRecoveryPendingAfterCashierLogin = false;
        await RefreshCardRecoveryCountAsync();
    }

    private async Task RecoverPendingStartupCardPaymentAttemptAfterLoginAsync()
    {
        if (!_startupCardRecoveryPendingAfterCashierLogin)
        {
            return;
        }

        // pending 只消费一次；这里只刷新提示数量，不查询终端、不恢复订单，也不弹阻断对话框。
        _startupCardRecoveryPendingAfterCashierLogin = false;
        await RefreshCardRecoveryCountAsync();
    }

    private void OnCashierSessionRejected(object? sender, CashierSessionRejectedEventArgs eventArgs)
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(
                () => ApplyRejectedCashierSessionSafe(eventArgs.RejectedSession),
                DispatcherPriority.DataBind);
            return;
        }

        ApplyRejectedCashierSessionSafe(eventArgs.RejectedSession);
    }

    private void ApplyRejectedCashierSessionSafe(CashierSessionDto rejectedSession)
    {
        try
        {
            ApplyRejectedCashierSession(rejectedSession);
        }
        catch (Exception ex)
        {
            // 后台刷新事件不得把异常逃逸到 WPF Dispatcher，否则会导致整个 POS 退出。
            ConsoleLog.WriteError(
                "CashierSession",
                "同步收银员会话失效状态失败。",
                exception: ex);
        }
    }

    private void ApplyRejectedCashierSession(CashierSessionDto rejectedSession)
    {
        var shellCashier = Session.CashierSession;
        if (IsLifetimeEnding ||
            _cashierSessionContext.CurrentSession is not null ||
            shellCashier is null ||
            !HasSameCashierScope(shellCashier, rejectedSession))
        {
            return;
        }

        RevokeOperationAuthorizationsForSessionChange();
        OperationAuditEvents.RecordAction(
            _operationAuditLogger,
            OperationAuditTypes.CashierLogout,
            "Succeeded",
            CreateCashierLoginAuditSession(shellCashier),
            reasonCode: "SESSION_REJECTED");

        // 服务端已明确拒绝会话时，界面与鉴权上下文必须一起退回扫码登录；购物车和设备授权保持不变。
        _cashierSessionContext.Clear();
        Session = Session with
        {
            CashierId = string.Empty,
            CashierName = string.Empty,
            CashierSession = null
        };
        CashierBarcodeInput = string.Empty;
        StatusMessage = _localization.T("shell.cashierLogin.status.sessionExpired");
        _ = ReportRuntimeStatusSafeAsync(Session.IsOnline, CancellationToken.None, clearCashier: true);
    }

    private static bool HasSameCashierScope(
        CashierSessionDto left,
        CashierSessionDto right)
    {
        return string.Equals(left.UserGuid, right.UserGuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.StoreCode, right.StoreCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.DeviceCode, right.DeviceCode, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshCardRecoveryCountAsync()
    {
        if (_cardPaymentRecoveryService is null || PosTerminal is null)
        {
            return;
        }

        try
        {
            var openAttempts = await _cardPaymentRecoveryService.ListOpenAsync(Session, CancellationToken.None);
            PosTerminal.CardRecoveryOpenCount = openAttempts.Count;
        }
        catch (Exception ex)
        {
            // 异常中心不可用不能阻断收银；保留现有数量并记录诊断，稍后可在专门页面手动刷新。
            ConsoleLog.Write(
                "CardRecovery",
                $"startup queue scan failed store={Session.StoreCode} device={Session.DeviceCode} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task HandleCardRecoveryCenterResultAsync(
        CardRecoveryAttemptKey selectedKey,
        CardPaymentRecoveryResult result)
    {
        if (result.Outcome == CardPaymentRecoveryOutcome.DraftRestored)
        {
            // Recovery Center 与启动恢复共用同一条原子交接，不能再次手工投影并绕过 owner 收尾。
            var presenter = _cardRecoveryPresenter ??
                throw new InvalidOperationException("Card recovery presenter is unavailable.");
            var postCommitWarning =
                await presenter.HandoffRecoveredCardDraftFromRecoveryCenterAsync(result);
            if (!string.IsNullOrWhiteSpace(postCommitWarning))
            {
                // 让异常中心显示“已提交但仍锁定”的明确告警，不能回退成普通成功提示。
                throw new CardRecoveryDraftHandoffPostCommitException(postCommitWarning);
            }

            return;
        }

        if (result.UpdatedSession is not null)
        {
            await ExecuteCardRecoveryPostResultStepAsync(
                "apply-updated-session",
                () =>
                {
                    Session = result.UpdatedSession;
                    return Task.CompletedTask;
                },
                allowPaymentCompletedWarning: result.Outcome == CardPaymentRecoveryOutcome.OrderCompleted);
        }

        await ExecuteCardRecoveryPostResultStepAsync(
            "set-result-status",
            () =>
            {
                StatusMessage = result.Message;
                return Task.CompletedTask;
            },
            allowPaymentCompletedWarning: result.Outcome == CardPaymentRecoveryOutcome.OrderCompleted);
        if (result.Outcome == CardPaymentRecoveryOutcome.OrderCompleted)
        {
            if (result.Order is not null)
            {
                _lastCompletedOrder = result.Order;
            }

            var cachedCashPayment = _screenNavigator.CachedCashPaymentScreen;
            var paymentRecoveryIdentity = cachedCashPayment?.CreateCardPaymentHandoffRequest();
            var recoveredPaymentKey = selectedKey.Processor switch
            {
                CardProcessorKind.Linkly => $"CARD_ATTEMPT:{selectedKey.AttemptGuid:N}",
                CardProcessorKind.Square => $"SQUARE_ATTEMPT:{selectedKey.AttemptGuid:N}",
                _ => null
            };
            // 中文注释：批准后的落盘异常只有冻结订单身份；补单须同时匹配订单和所选 attempt 的付款证据。
            var completedPersistenceOrder = cachedCashPayment?.IsCardPaymentRecoveryRequired == true &&
                paymentRecoveryIdentity?.RecoveryOrderGuid is { } recoveryOrderGuid &&
                recoveryOrderGuid != Guid.Empty &&
                recoveredPaymentKey is not null &&
                result.Order?.OrderGuid == recoveryOrderGuid &&
                result.Order.Payments.Any(payment =>
                    payment.Method == PaymentMethodKind.Card &&
                    string.Equals(payment.IdempotencyKey, recoveredPaymentKey, StringComparison.Ordinal));
            if (cachedCashPayment is not null &&
                ReferenceEquals(cachedCashPayment, CashPayment) &&
                (paymentRecoveryIdentity?.RecoveryAttemptKey == selectedKey || completedPersistenceOrder))
            {
                // 只有核心购物车和 tender 都确认清空后，付款页才会释放同 key 锁；失败时继续 fail-closed。
                await ExecuteCardRecoveryPostResultStepAsync(
                    "complete-payment-handoff",
                    () =>
                    {
                        if (!cachedCashPayment.CompleteCardPaymentHandoff())
                        {
                            throw new InvalidOperationException("Card recovery payment handoff remained locked.");
                        }

                        if (cachedCashPayment.LastCardPaymentHandoffHadWarning)
                        {
                            throw new InvalidOperationException(
                                "Card recovery payment handoff completed with a UI follow-up warning.");
                        }

                        return Task.CompletedTask;
                    });
            }

            await ExecuteCardRecoveryPostResultStepAsync(
                "pos-cart-refresh",
                () =>
                {
                    PosTerminal?.RefreshCart();
                    return Task.CompletedTask;
                });
            await ExecuteCardRecoveryPostResultStepAsync(
                "cash-payment-cart-refresh",
                () =>
                {
                    CashPayment?.RefreshCart();
                    return Task.CompletedTask;
                });
            await ExecuteCardRecoveryPostResultStepAsync(
                "pending-sync-refresh",
                RefreshPendingSyncAsync);
            return;
        }

    }

    private async Task ExecuteCardRecoveryPostResultStepAsync(
        string stage,
        Func<Task> action,
        bool allowPaymentCompletedWarning = true)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 金融结果已经确定；普通 UI/同步收尾异常只能告警并继续，不能回写异常中心为失败。
            if (allowPaymentCompletedWarning)
            {
                try
                {
                    SetPaymentCompletedWarning();
                }
                catch (Exception warningException) when (
                    warningException is not OutOfMemoryException and not StackOverflowException)
                {
                    // 告警本身也是 UI 收尾，失败时仍保留已经落库的金融结果。
                }
            }

            try
            {
                ConsoleLog.WriteError(
                    "CardRecovery",
                    $"card recovery post-result step failed stage={stage}",
                    null,
                    ex);
            }
            catch (Exception logException) when (
                logException is not OutOfMemoryException and not StackOverflowException)
            {
                // 日志订阅者失败不能中断后续收尾，也不能覆盖已确定的金融结果。
            }
        }
    }

    private Window? CurrentOwner => _windowOwnerProvider?.CurrentOwner;

    private void OnPaymentSuccessNewTransactionRequested(object? sender, EventArgs e)
    {
        _screenNavigator.ResetForNewTransaction();
    }

    private void OnPaymentSuccessPrintReceiptRequested(object? sender, EventArgs e)
    {
        _ = HandlePaymentSuccessPrintReceiptRequestedAsync();
    }

    private async Task HandlePaymentSuccessPrintReceiptRequestedAsync()
    {
        try
        {
            using var grant = await AuthorizeShellOperationAsync(
                Permissions.PosTerminal.Receipt.PrintLast,
                "print-payment-success-receipt");
            if (grant is null)
            {
                return;
            }

            using var activation = grant.Activate();
            await PrintPaymentSuccessReceiptAsync();
        }
        catch (Exception ex)
        {
            // 事件桥不得让授权、查询或打印异常逃逸到 Dispatcher，否则 WPF 会按未处理异常退出。
            var safeMessage = ex.GetType().Name;
            StatusMessage = string.Format(
                _localization.CurrentCulture,
                _localization.T("receipt.print.failed"),
                safeMessage);
            ConsoleLog.WriteError(
                "ReceiptPrint",
                $"payment success receipt print failed error={safeMessage}",
                exception: ex);
        }
    }

    private void OnCustomerDisplayClosed(object? sender, EventArgs e)
    {
        CustomerDisplayWindowMode = CustomerDisplayWindowMode.Closed;
    }

    private void OnCustomerDisplayFullscreenRequested(object? sender, EventArgs e)
    {
        // 与主窗口客显按钮同一条命令：先过客显管理权限，再切模式并同步按钮状态。
        if (ShowCustomerDisplayFullscreenCommand.CanExecute(null))
        {
            ShowCustomerDisplayFullscreenCommand.Execute(null);
        }
    }

    private void OnClockTimerTick(object? sender, EventArgs e)
    {
        RefreshClock();
        ExpireEmergencySessionIfNeeded(DateTimeOffset.UtcNow);
    }

    internal void ExpireEmergencySessionIfNeeded(DateTimeOffset now)
    {
        var contextCashier = _cashierSessionContext.CurrentSession;
        var cashier = contextCashier ?? Session.CashierSession;
        var rejectedByServer = contextCashier is null && cashier?.IsEmergencyOverride == true;
        if (cashier?.IsEmergencyOverride != true ||
            (!rejectedByServer && cashier.AuthorizationExpiresAtUtc > now))
        {
            return;
        }

        OperationAuditEvents.RecordAction(
            _operationAuditLogger,
            OperationAuditTypes.CashierLogout,
            "Succeeded",
            Session,
            reasonCode: "EMERGENCY_EXPIRED");
        _cashierSessionContext.Clear();
        Session = Session with
        {
            CashierId = string.Empty,
            CashierName = string.Empty,
            CashierSession = null
        };
        _screenNavigator.ApplySessionToScreens();
        OnPropertyChanged(nameof(IsCashierLoginOverlayOpen));
        StatusMessage = rejectedByServer
            ? "紧急登录已被服务端拒绝，请重新登录"
            : "紧急登录已到期，请重新登录";
    }

    private void OnConnectivityTimerTick(object? sender, EventArgs e)
    {
        _ = RefreshOnlineStateFromTimerSafeAsync();
    }

    private async Task RefreshOnlineStateFromTimerSafeAsync()
    {
        try
        {
            await RefreshOnlineStateAsync(CancellationToken.None, autoRetryOrders: true);
        }
        catch (OperationCanceledException) when (IsShutdownRequested)
        {
            // 关闭时的预期取消不能返回 Dispatcher。
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError("Connectivity", "connectivity timer refresh failed", null, ex);
        }
    }

    public async Task ReportOfflineForShutdownAsync(CancellationToken cancellationToken = default)
    {
        BeginShutdown();
        // 中文注释：取消 Task 即使超过 runtime-offline 的单步预算也继续由协调器观察；
        // 步骤 token 只能中止后续网络清理，不能丢弃迟到的终端 OOM/SO。
        await ObserveCriticalShutdownCancellationsAsync();
        if (!_shutdownLogoutRecorded && Session.CashierSession is not null)
        {
            _shutdownLogoutRecorded = true;
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.CashierLogout,
                "Succeeded",
                Session,
                reasonCode: "APP_SHUTDOWN");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await ObserveConnectivityRefreshForShutdownAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await ReportRuntimeStatusSafeAsync(
            false,
            cancellationToken,
            clearCashier: true,
            throwOnCancellation: true);
    }

    private async Task ObserveCriticalShutdownCancellationsAsync()
    {
        Task shellCancellationTask;
        Task paymentCancellationTask;
        lock (_connectivityRefreshSync)
        {
            shellCancellationTask = _shutdownCancellationTask;
            paymentCancellationTask = _paymentShutdownCancellationTask;
        }

        var fatalExceptionSource = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var shellObservationTask = ObserveShutdownCancellationTaskAsync(
            shellCancellationTask,
            "main-view-model",
            fatalExceptionSource);
        var paymentObservationTask = ObserveShutdownCancellationTaskAsync(
            paymentCancellationTask,
            "payment-view-model",
            fatalExceptionSource);
        var allObservationTasks = Task.WhenAll(shellObservationTask, paymentObservationTask);

        await Task.WhenAny(allObservationTasks, fatalExceptionSource.Task);
        if (fatalExceptionSource.Task.IsCompletedSuccessfully)
        {
            var fatalException = await fatalExceptionSource.Task;
            ExceptionDispatchInfo.Capture(fatalException).Throw();
        }

        await allObservationTasks;
    }

    private static async Task ObserveShutdownCancellationTaskAsync(
        Task cancellationTask,
        string stage,
        TaskCompletionSource<Exception> fatalExceptionSource)
    {
        try
        {
            await cancellationTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var fatalException = FindFatalException(ex);
            if (fatalException is not null)
            {
                fatalExceptionSource.TrySetResult(fatalException);
                return;
            }

            ConsoleLog.WriteError(
                "Shutdown",
                $"shutdown cancellation observation failed stage={stage} error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
    }

    private async Task ObserveConnectivityRefreshForShutdownAsync(CancellationToken cancellationToken)
    {
        Task<bool>? pendingRefresh;
        lock (_connectivityRefreshSync)
        {
            pendingRefresh = _connectivityRefreshTask;
        }

        if (pendingRefresh is null || pendingRefresh.IsCompleted)
        {
            return;
        }

        try
        {
            await pendingRefresh.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError("Connectivity", "closing connectivity refresh failed", null, ex);
        }
    }

    private bool IsShutdownRequested => Volatile.Read(ref _shutdownStarted) != 0;

    private bool IsLifetimeEnding =>
        IsShutdownRequested || Volatile.Read(ref _disposed) != 0;

    private async Task ReportRuntimeStatusSafeAsync(
        bool isOnline,
        CancellationToken cancellationToken,
        bool clearCashier = false,
        bool throwOnCancellation = false)
    {
        if (_runtimeStatusApiClient is null)
        {
            return;
        }

        try
        {
            var cashier = clearCashier ? null : Session.CashierSession ?? _cashierSessionContext.CurrentSession;
            // 中文注释：运行状态上报是后台看板信息，失败不能阻断本机收银和离线缓存流程。
            await _runtimeStatusApiClient.ReportAsync(
                new PosRuntimeStatusReport(
                    isOnline,
                    cashier?.CashierId,
                    cashier?.CashierName),
                cancellationToken);
        }
        catch (OperationCanceledException) when (
            throwOnCancellation && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("RuntimeStatus", $"runtime status report failed error={ex.Message}");
        }
    }

    private static Task CancelForShutdownAsync(
        CancellationTokenSource cancellation,
        string stage)
    {
        return Task.Run(() =>
        {
            try
            {
                cancellation.Cancel();
            }
            catch (Exception ex)
            {
                var fatalException = FindFatalException(ex);
                if (fatalException is not null)
                {
                    // CancellationTokenSource 会聚合回调异常；致命异常必须以原实例穿过 shutdown coordinator。
                    ExceptionDispatchInfo.Capture(fatalException).Throw();
                }

                ConsoleLog.WriteError(
                    "Shutdown",
                    $"shutdown cancellation failed stage={stage} error={ex.GetType().Name} message={ex.Message}",
                    exception: ex);
            }
        });
    }

    private static Exception? FindFatalException(Exception exception)
    {
        if (exception is OutOfMemoryException or StackOverflowException)
        {
            return exception;
        }

        if (exception is AggregateException aggregateException)
        {
            foreach (var innerException in aggregateException.InnerExceptions)
            {
                var fatalException = FindFatalException(innerException);
                if (fatalException is not null)
                {
                    return fatalException;
                }
            }

            return null;
        }

        return exception.InnerException is null
            ? null
            : FindFatalException(exception.InnerException);
    }

    private void DisposeShutdownCancellationWhenSafe()
    {
        Task cancellationTask;
        Task<bool>? connectivityRefreshTask;
        lock (_connectivityRefreshSync)
        {
            cancellationTask = _shutdownCancellationTask;
            connectivityRefreshTask = _connectivityRefreshTask;
        }

        _ = DisposeShutdownCancellationWhenSafeAsync(
            cancellationTask,
            connectivityRefreshTask);
    }

    private async Task DisposeShutdownCancellationWhenSafeAsync(
        Task cancellationTask,
        Task<bool>? connectivityRefreshTask)
    {
        try
        {
            await cancellationTask.ConfigureAwait(false);
            if (connectivityRefreshTask is not null)
            {
                await connectivityRefreshTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError(
                "Shutdown",
                $"shutdown cancellation cleanup wait failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
        finally
        {
            _shutdownCancellation.Dispose();
        }
    }

    private void OnCatalogDownloadHideTimerTick(object? sender, EventArgs e)
    {
        _catalogDownloadHideTimer.Stop();
        IsCatalogDownloadProgressVisible = false;
    }

    private async Task ShowInstallmentCreateAsync(PosCartServiceSnapshot? cartSnapshot) =>
        await _screenNavigator.ShowInstallmentCreateAsync(cartSnapshot);

    private void OnCashPaymentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaymentViewModel.PaymentMode) &&
            ReferenceEquals(CurrentScreen, CashPayment))
        {
            OnPropertyChanged(nameof(ActivePageTitleText));
        }
    }

    private void OnPaymentCompleted(object? sender, PaymentCompletedEventArgs e) =>
        _ = HandlePaymentCompletedSafelyAsync(e);

    private async Task HandlePaymentCompletedSafelyAsync(PaymentCompletedEventArgs e)
    {
        try
        {
            await HandlePaymentCompletedCoreAsync(e);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 该入口来自同步事件，绝不能把未知后续异常重新抛回 Dispatcher。
            ConsoleLog.WriteError("PaymentCompleted", "payment follow-up failed outside an isolated stage", null, ex);
            SetPaymentCompletedWarning();
        }
    }

    private async Task HandlePaymentCompletedCoreAsync(PaymentCompletedEventArgs e)
    {
        // 支付成功卡片必须优先呈现；其余设备、同步与打印都是不可反向影响收款的后续处理。
        await ExecutePaymentCompletionFollowUpAsync(
            "success-page-load-sync",
            () =>
            {
                _lastCompletedOrder = e.Order;
                PaymentSuccess.HasPostCommitWarning = e.HasPostCommitWarning;
                PaymentSuccess.LoadFromOrder(e.Order);
                return Task.CompletedTask;
            });
        if (e.HasPostCommitWarning)
        {
            SetPaymentCompletedWarning();
        }
        await ExecutePaymentCompletionFollowUpAsync(
            "success-page-navigate",
            () =>
            {
                // 中文注释：成功结果以卡片显示在收银主页购物车区域，收银员可直接扫下一单。
                _screenNavigator.ShowCompletedSaleOnPos();
                ShowCashPaymentCommand.NotifyCanExecuteChanged();
                return Task.CompletedTask;
            });
        await ExecutePaymentCompletionFollowUpAsync(
            "payment-feedback",
            () =>
            {
                _userFeedbackService.Play(UserFeedbackCue.Checkout);
                return Task.CompletedTask;
            });
        await ExecutePaymentCompletionFollowUpAsync(
            "success-page-load",
            () => PaymentSuccess.LoadFromOrderAsync(e.Order));
        await ExecutePaymentCompletionFollowUpAsync("pending-sync-refresh", RefreshPendingSyncAsync);
        await ExecutePaymentCompletionFollowUpAsync("cash-drawer", () => OpenCashDrawerAfterPaymentAsync(e.Order));

        // 普通销售成功后不自动打印；退款和代金券余额的既有打印习惯保持不变。
        if (IsPureVoucherRefund(e.Order))
        {
            await ExecutePaymentCompletionFollowUpAsync(
                "voucher-refund-print",
                async () =>
                {
                    var receipt = ReceiptQueryService.CreateReceipt(e.Order);
                    if (receipt.RefundVoucher is not null)
                    {
                        await PrintReceiptWithShellPermissionAsync(receipt, ReceiptPrintReason.VoucherRefundAuto);
                    }
                });

            return;
        }

        if (MainReceiptCoordinator.ContainsCardPayment(e.Order) && e.Order.ActualAmount < 0m)
        {
            await ExecutePaymentCompletionFollowUpAsync(
                "card-refund-print",
                () => PrintReceiptWithShellPermissionAsync(
                    ReceiptQueryService.CreateReceipt(e.Order),
                    ReceiptPrintReason.CardAuto));
            return;
        }

        if (e.Order.ActualAmount >= 0m)
        {
            await ExecutePaymentCompletionFollowUpAsync(
                "voucher-balance-print",
                () => PrintVoucherBalancesAsync(e.Order));
        }
    }

    private async Task OpenCashDrawerAfterPaymentAsync(LocalOrder order)
    {
        if (!MainReceiptCoordinator.ContainsCashPayment(order))
        {
            return;
        }

        var drawerPermissionAllowed = IsShellPermissionAllowed(Permissions.PosTerminal.CashDrawer.Open);
        var cashDrawerResult = await OpenCashDrawerWithShellPermissionAsync();
        OperationAuditEvents.RecordAction(
            _operationAuditLogger,
            OperationAuditTypes.CashDrawerOpen,
            cashDrawerResult.Succeeded ? "Succeeded" : drawerPermissionAllowed ? "Failed" : "Denied",
            Session,
            reasonCode: "PAYMENT_COMPLETE",
            safeMessage: cashDrawerResult.Succeeded ? null : cashDrawerResult.Message,
            paymentMethod: PaymentMethodKind.Cash.ToString(),
            paymentAmount: order.Payments
                .Where(payment => payment.Method == PaymentMethodKind.Cash)
                .Sum(payment => payment.Amount),
            orderGuid: order.OrderGuid.ToString("D"));
        if (!cashDrawerResult.Succeeded)
        {
            SetPaymentCompletedWarning();
        }
    }

    private async Task ExecutePaymentCompletionFollowUpAsync(string stage, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError("PaymentCompleted", $"payment follow-up failed stage={stage}", null, ex);
            SetPaymentCompletedWarning();
        }
    }

    private void SetPaymentCompletedWarning()
    {
        // 中文注释：收款已落地后的任何收尾失败都必须在成功卡片留下可见提示，阻止收银员重复收款。
        PaymentSuccess.HasPostCommitWarning = true;
        StatusMessage = _localization.T("payment.status.completedWarning");
    }

    private static bool IsPureVoucherRefund(LocalOrder order) =>
        order.ActualAmount < 0m &&
        order.Payments.Count == 1 &&
        order.Payments[0].Method == PaymentMethodKind.Voucher &&
        order.Payments[0].Amount < 0m;

    private async Task PrintVoucherBalancesAsync(LocalOrder order)
    {
        var receipt = ReceiptQueryService.CreateReceipt(order);
        var balances = receipt.Payments
            .Where(payment => payment.Method == PaymentMethodKind.Voucher && payment.Amount > 0m)
            .Select(payment => new
            {
                VoucherCode = payment.DisplayReference?.Trim(),
                RemainingBalance = payment.VoucherRemainingBalance
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.VoucherCode) && item.RemainingBalance is > 0m)
            .GroupBy(item => item.VoucherCode!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                // 中文注释：保持券码首次出现顺序，但余额必须采用同券最后一笔兑换后的最终值。
                VoucherCode = group.First().VoucherCode,
                RemainingBalance = group.Last().RemainingBalance
            })
            .ToList();

        (string VoucherCode, ReceiptPrintResult Result)? firstFailure = null;
        foreach (var balance in balances)
        {
            var voucherCode = balance.VoucherCode!;
            // 中文注释：每张余额券独立尝试打印；单张失败不能阻断其余券，也不能回滚已完成交易。
            var result = await PrintReceiptWithShellPermissionAsync(
                receipt with
                {
                    RefundVoucher = null,
                    VoucherBalance = new VoucherBalanceReceipt(voucherCode, balance.RemainingBalance!.Value)
                },
                ReceiptPrintReason.VoucherBalanceAuto);
            firstFailure ??= result.Succeeded ? null : (voucherCode, result);
        }

        if (firstFailure is { } failure)
        {
            StatusMessage = string.Format(
                _localization.CurrentCulture,
                _localization.T("receipt.print.failed"),
                $"{failure.VoucherCode}: {failure.Result.Message}");
        }
    }

    private async Task OnInstallmentOrderCreatedAsync(InstallmentOrderSummary order)
    {
        try
        {
            var localOrder = await _installmentOrderService.GetLocalOrderAsync(order.OrderId);
            if (localOrder is not null)
            {
                localOrder = await ConfirmInstallmentPickupAfterPaidOffAsync(order, localOrder);
                await PrintReceiptWithShellPermissionAsync(
                    InstallmentReceiptMapper.CreateReceipt(localOrder),
                    ReceiptPrintReason.InstallmentAuto);
            }
        }
        finally
        {
            // 分期首单完成后无论打印结果如何都回到 POS，避免收银员继续在已清空的付款页操作。
            CashPayment?.PrepareForEntry(Session);
            _screenNavigator.ResetForNewTransaction();
            PosTerminal?.RefreshCart();
        }
    }

    private async Task<LocalInstallmentOrder> ConfirmInstallmentPickupAfterPaidOffAsync(
        InstallmentOrderSummary order,
        LocalInstallmentOrder localOrder)
    {
        if (!ShouldConfirmInstallmentPickupAfterPaidOff(order, localOrder))
        {
            return localOrder;
        }

        var confirmed = await _confirmationDialogService.ConfirmInstallmentPickupAfterPaidOffAsync();
        if (!confirmed)
        {
            return localOrder;
        }

        try
        {
            // 分期付清后先确认提货，确认成功后重新读取本地快照，确保自动小票打印真实提货状态。
            var result = await _installmentOrderService.ConfirmPickupAsync(order.OrderId, Session);
            if (!result.Succeeded)
            {
                SetInstallmentPickupConfirmFailedStatus(result.Message);
                return localOrder;
            }

            return await _installmentOrderService.GetLocalOrderAsync(order.OrderId) ?? localOrder;
        }
        catch (Exception ex)
        {
            SetInstallmentPickupConfirmFailedStatus(ex.Message);
            return localOrder;
        }
    }

    private static bool ShouldConfirmInstallmentPickupAfterPaidOff(
        InstallmentOrderSummary order,
        LocalInstallmentOrder localOrder)
    {
        if (localOrder.PickupInfo is not null)
        {
            return false;
        }

        var localPaidOff = localOrder.Status == InstallmentStatus.PaidOff && localOrder.BalanceAmount <= 0m;
        var summaryPaidOff = order.CanConfirmPickup && order.OutstandingAmount <= 0m;
        return localPaidOff || summaryPaidOff;
    }

    private void SetInstallmentPickupConfirmFailedStatus(string? message)
    {
        StatusMessage = string.Format(
            _localization.CurrentCulture,
            _localization.T("payment.installment.status.pickupConfirmFailed"),
            string.IsNullOrWhiteSpace(message) ? _localization.T("payment.installment.status.actionFailed") : message);
    }

    private async Task<ReceiptPrintResult> PrintLatestReceiptAsync() =>
        await _receiptCoordinator.PrintLatestAsync();

    private async Task<ReceiptPrintResult> OpenCashDrawerAsync() =>
        await _receiptCoordinator.OpenCashDrawerAsync();

    private async Task<ReceiptPrintResult> OpenCashDrawerWithShellPermissionAsync()
    {
        if (!TryRequireShellPermission(Permissions.PosTerminal.CashDrawer.Open))
        {
            return new ReceiptPrintResult(false, StatusMessage);
        }

        return await OpenCashDrawerAsync();
    }

    private async Task<ReceiptPrintResult> PrintReceiptWithShellPermissionAsync(ReceiptDetails receipt, ReceiptPrintReason reason)
    {
        // 关键逻辑：自动卡支付/恢复小票是系统完成交易流程，不等同于人工补打上一张。
        var isAutomatic = reason is ReceiptPrintReason.CardAuto or
            ReceiptPrintReason.InstallmentAuto or
            ReceiptPrintReason.VoucherRefundAuto or
            ReceiptPrintReason.VoucherBalanceAuto;
        if (isAutomatic)
        {
            return await _receiptCoordinator.PrintReceiptAsync(receipt, reason);
        }

        using var grant = await AuthorizeShellOperationAsync(
            Permissions.PosTerminal.Receipt.PrintLast,
            "print-receipt");
        if (grant is null)
        {
            return new ReceiptPrintResult(
                false,
                _operationAuthorizationService?.StatusMessage ?? StatusMessage,
                receipt.OrderGuid);
        }

        using var activation = grant.Activate();
        return await _receiptCoordinator.PrintReceiptAsync(receipt, reason);
    }

    private async Task ExitApplicationAsync()
    {
        if (!await _confirmationDialogService.ConfirmExitApplicationAsync())
        {
            return;
        }

        if (Interlocked.Exchange(ref _applicationExitStarted, 1) != 0)
        {
            return;
        }

        // 关键逻辑：两处退出入口共用本方法，只有首次确认成功才关闭客显并退出应用。
        SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Closed, CurrentOwner);
        _applicationExitService.Exit();
    }

    internal async Task LockCashierAsync()
    {
        var cashier = _cashierSessionContext.CurrentSession ?? Session.CashierSession;
        if (cashier is null)
        {
            return;
        }

        RevokeOperationAuthorizationsForSessionChange();

        OperationAuditEvents.RecordAction(
            _operationAuditLogger,
            OperationAuditTypes.CashierLogout,
            "Succeeded",
            CreateCashierLoginAuditSession(cashier),
            reasonCode: "MANUAL_LOCK");

        // 关键逻辑：手动锁屏只清当前收银员会话，购物车、离线缓存和设备授权必须原样保留。
        _cashierSessionContext.Clear();
        Session = Session with
        {
            CashierId = string.Empty,
            CashierName = string.Empty,
            CashierSession = null
        };
        CashierBarcodeInput = string.Empty;
        _screenNavigator.ApplySessionToScreens();
        OnPropertyChanged(nameof(IsCashierLoginOverlayOpen));
        StatusMessage = _localization.T("shell.cashierLogin.status.locked");
        await ReportRuntimeStatusSafeAsync(Session.IsOnline, CancellationToken.None, clearCashier: true);
    }

    private void RevokeOperationAuthorizationsForSessionChange()
    {
        _operationAuthorizationService?.RevokeAll();
        // 中文注释：无条码退货弹窗会暂存一次 grant；换收银员时同时关闭弹窗并销毁该 grant。
        _screenNavigator?.ReceiptReturns?.ResetToDefault();
    }

    private async Task PrintPaymentSuccessReceiptAsync()
    {
        if (PaymentSuccess.TransactionId is Guid orderGuid)
        {
            await _receiptCoordinator.PrintSuccessAsync(orderGuid);
        }
    }

    private async Task PrintSelectedHistoryReceiptAsync(TransactionHistoryViewModel history)
    {
        var selectedOrder = history.SelectedOrder;
        if (selectedOrder is null)
        {
            return;
        }

        try
        {
            // 只有打印服务返回结果后才记录补打成功或失败，避免把点击动作误记成已打印。
            ReceiptPrintResult result;
            if (selectedOrder.Source is
                TransactionHistorySource.RemoteOrders or
                TransactionHistorySource.InstallmentOrders)
            {
                if (history.SelectedReceipt is not { } loadedReceipt ||
                    loadedReceipt.OrderGuid != selectedOrder.OrderGuid)
                {
                    var message = _localization.T("receipt.print.noReceiptFound");
                    StatusMessage = string.Format(
                        _localization.CurrentCulture,
                        _localization.T("receipt.print.failed"),
                        message);
                    result = new ReceiptPrintResult(false, message, selectedOrder.OrderGuid);
                }
                else
                {
                    // 远程订单和分期订单都不能回退到普通本地订单查询，直接打印历史页已加载的完整小票。
                    result = await _receiptCoordinator.PrintReceiptAsync(loadedReceipt, ReceiptPrintReason.Reprint);
                }
            }
            else
            {
                result = await _receiptCoordinator.PrintReceiptAsync(
                    selectedOrder.OrderGuid,
                    ReceiptPrintReason.Reprint);
            }
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.ReceiptReprint,
                result.Succeeded ? "Succeeded" : "Failed",
                Session,
                reasonCode: "HISTORY",
                safeMessage: result.Succeeded ? null : result.Message,
                orderGuid: selectedOrder.OrderGuid.ToString("D"));
        }
        catch (Exception ex)
        {
            var correlation = OperationAuditEvents.CreateCorrelation();
            var safeMessage = ex.GetType().Name;
            StatusMessage = string.Format(
                _localization.CurrentCulture,
                _localization.T("receipt.print.failed"),
                safeMessage);
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.ReceiptReprint,
                "Failed",
                Session,
                reasonCode: "HISTORY_EXCEPTION",
                safeMessage: safeMessage,
                orderGuid: selectedOrder.OrderGuid.ToString("D"),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            ConsoleLog.WriteError(
                "ReceiptReprintAudit",
                $"history receipt reprint failed error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            // 历史补打由 async void 事件桥触发；异常已审计并展示后必须在此终止，避免逃逸到 Dispatcher 导致应用退出。
        }
    }

    private async Task<ReceiptPrintResult> PrintReceiptAsync(Guid orderGuid, ReceiptPrintReason reason) =>
        await _receiptCoordinator.PrintReceiptAsync(orderGuid, reason);

    private async Task<ReceiptPrintResult> PrintReceiptAsync(ReceiptDetails receipt, ReceiptPrintReason reason) =>
        await _receiptCoordinator.PrintReceiptAsync(receipt, reason);

    private async Task ToggleCustomerDisplayWindowAsync()
    {
        using var grant = await AuthorizeShellOperationAsync(
            Permissions.PosTerminal.CustomerDisplay.Manage,
            "toggle-customer-display");
        if (grant is null)
        {
            return;
        }

        var owner = CurrentOwner;
        if (owner is null)
        {
            return;
        }

        using var activation = grant.Activate();
        _customerDisplayShellController.Toggle(owner);
    }

    public async Task ToggleCustomerDisplayWindow(Window? owner)
    {
        using var grant = await AuthorizeShellOperationAsync(
            Permissions.PosTerminal.CustomerDisplay.Manage,
            "toggle-customer-display");
        if (grant is null)
        {
            return;
        }

        using var activation = grant.Activate();
        _customerDisplayShellController.Toggle(owner);
    }

    public void SetCustomerDisplayWindowMode(CustomerDisplayWindowMode mode, Window? owner) => _customerDisplayShellController.SetMode(mode, owner);

    private void OpenCustomerDisplayWindow(Window? owner) => _customerDisplayShellController.Open(owner);

    private async Task CloseCustomerDisplayWindowFromCommandAsync()
    {
        using var grant = await AuthorizeShellOperationAsync(
            Permissions.PosTerminal.CustomerDisplay.Manage,
            "close-customer-display");
        if (grant is null)
        {
            return;
        }

        using var activation = grant.Activate();
        _customerDisplayShellController.Close(CurrentOwner);
    }

    private async Task SetCustomerDisplayWindowModeFromCommandAsync(CustomerDisplayWindowMode mode)
    {
        using var grant = await AuthorizeShellOperationAsync(
            Permissions.PosTerminal.CustomerDisplay.Manage,
            "set-customer-display-mode");
        if (grant is null)
        {
            return;
        }

        using var activation = grant.Activate();
        SetCustomerDisplayWindowMode(mode, CurrentOwner);
    }

    private async Task ResetScannerBindingAsync()
    {
        using var grant = await AuthorizeShellOperationAsync(
            Permissions.PosTerminal.Settings.DeviceRegistration,
            "reset-scanner-binding");
        if (grant is null)
        {
            return;
        }

        using var activation = grant.Activate();
        await _rawScannerService.ResetBindingAsync();
        StatusMessage = _localization.T("main.scannerBindingReset");
    }

    private async Task<ViewModelAuthorizationGrant?> AuthorizeShellOperationAsync(
        string permissionCode,
        string action,
        CancellationToken cancellationToken = default) =>
        await ViewModelOperationAuthorization.AuthorizeAsync(
            _operationAuthorizationService,
            TryRequireShellPermission,
            permissionCode,
            "main-shell",
            action,
            Session,
            cancellationToken);

    private bool TryRequireShellPermission(string permissionCode)
    {
        if ((!_enforceCashierPermissions && _cashierSessionContext.CurrentSession is null && Session.CashierSession is null) ||
            _cashierSessionContext.RequirePermission(permissionCode, out var message))
        {
            return true;
        }

        // 关键逻辑：顶栏命令也必须经过当前收银员上下文，避免绕过页面内按钮权限。
        StatusMessage = message;
        return false;
    }

    private bool IsShellPermissionAllowed(string permissionCode)
    {
        if (!_enforceCashierPermissions && _cashierSessionContext.CurrentSession is null && Session.CashierSession is null)
        {
            return true;
        }

        return _cashierSessionContext.HasPermission(permissionCode);
    }

    private void AddPreviewCartItems(IReadOnlyList<SellableItemDto> items)
    {
        _cart.Clear();
        foreach (var item in items.Take(3))
        {
            _cart.AddItem(item);
        }

        if (items.Count > 1)
        {
            _cart.AddItem(items[1]);
        }

        ShowCashPaymentCommand.NotifyCanExecuteChanged();
    }

    private async Task<LocalOrder> CreatePreviewOrderAsync(IReadOnlyList<SellableItemDto> items)
    {
        var previewCart = new PosCartService();
        foreach (var item in items.Take(2))
        {
            previewCart.AddItem(item);
        }

        if (items.Count > 0)
        {
            previewCart.AddItem(items[0]);
        }

        var result = _checkout.CreateCashOrder(previewCart, Session, previewCart.ActualAmount);
        await _orderRepository.SavePendingOrderAsync(result.Order);
        PaymentSuccess.LoadFromOrder(result.Order);
        return result.Order;
    }

    private static IReadOnlyList<SellableItemDto> CreateStarterItems()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            new(DefaultTestStoreCode, "SKU-001", null, "Organic Fuji Apple", "690001", "SKU-001", "690001", 4.50m, PriceSourceKind.StoreRetailPrice, "Store Price", 1m, now),
            new(DefaultTestStoreCode, "SKU-002", null, "Whole Milk 1L", "690002", "SKU-002", "690002", 3.20m, PriceSourceKind.ProductBase, "Base Price", 1m, now),
            new(DefaultTestStoreCode, "SKU-003", "SET-003", "Greek Yogurt Blueberry", "690003", "SKU-003", "690003", 3.75m, PriceSourceKind.StoreMultiCodeProduct, "Multi-code Store Price", 1m, now),
            new(DefaultTestStoreCode, "SKU-004", "CLR-004", "Cold Brew Concentrate", "690004", "SKU-004", "690004", 12.90m, PriceSourceKind.StoreClearancePrice, "Clearance Price", 1m, now)
        ];
    }
}

internal static class ObservableCollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }
}
