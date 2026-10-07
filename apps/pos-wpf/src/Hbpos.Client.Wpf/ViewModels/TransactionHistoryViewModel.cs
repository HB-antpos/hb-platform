using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Windows.Markup;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.HeldOrders;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using HeldClaimStatus = Hbpos.Client.Wpf.Models.SharedHeldOrderClaimStatus;
using HeldPublicationStatus = Hbpos.Client.Wpf.Models.SharedHeldOrderPublicationStatus;
using HeldServerStatus = Hbpos.Contracts.HeldOrders.SharedHeldOrderStatus;

namespace Hbpos.Client.Wpf.ViewModels;

public enum TransactionHistorySource
{
    LocalOrders,
    RemoteOrders,
    InstallmentOrders,
    HeldOrders
}

public sealed record HistorySourceOption(TransactionHistorySource Source, string Label);

public sealed record TerminalFilterOption(string? DeviceCode, string Label);

/// <summary>挂单页签：本机（当前 device）与非本机（其他 device）。</summary>
public enum HeldOrderViewScope
{
    Local,
    Other
}

/// <summary>
/// 单行共享操作的可变状态：HistoryOrderListItem 是不可变 record，行级 busy 由该对象承载，
/// 通过 INotifyPropertyChanged 通知 DataGrid 立即刷新按钮状态。
/// </summary>
public sealed class HeldShareRowState : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsBusy)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed record HistoryOrderListItem(
    Guid OrderGuid,
    TransactionHistorySource Source,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset OccurredAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    int LineCount,
    string PaymentSummary,
    string StatusLabel,
    bool IsSuspendedOrder = false,
    bool CanRecall = false,
    InstallmentOrderSummary? InstallmentOrder = null,
    bool IsInstallmentOrder = false,
    bool CanContinueInstallmentPayment = false,
    bool CanConfirmInstallmentPickup = false,
    string CustomerPhone = "",
    string SyncStatus = "",
    CultureInfo? DisplayCulture = null,
    bool IsHeldOrder = false,
    HeldOrderBadgeKind HeldBadgeKind = HeldOrderBadgeKind.LocalHold,
    string HeldStatusDetail = "",
    Guid? HeldClaimId = null,
    bool CanForceRelease = false,
    bool CanRemoteRecall = false,
    bool CanOfflineRecall = false,
    bool CanLegacyRecall = false,
    bool CanDeleteHeldOrder = false,
    bool CanShare = false,
    string ShareStatusLabel = "")
{
    public RowSelectionState Selection { get; } = new();

    public HeldShareRowState Share { get; } = new();

    public bool CanReupload => Source == TransactionHistorySource.LocalOrders &&
        !IsSuspendedOrder &&
        !IsInstallmentOrder &&
        (SyncStatus.Equals("Synced", StringComparison.OrdinalIgnoreCase) ||
         SyncStatus.Equals("Pending", StringComparison.OrdinalIgnoreCase) ||
         SyncStatus.Equals("Failed", StringComparison.OrdinalIgnoreCase));

    // 历史摘要没有独立的退货类型；本地与在线的已完成退货均以负实收金额持久化。
    public bool IsReturnOrder => ActualAmount < 0m;

    public bool CanViewOrderDetails => !IsHeldOrder || IsSuspendedOrder;

    public string ShortOrderId => OrderGuid.ToString("N")[..8].ToUpperInvariant();

    public string DisplayOrderId => InstallmentOrder?.OrderNumber ?? ShortOrderId;

    public string SoldAtDisplay => OccurredAt.ToLocalTime().ToString(
        "MMM dd, yyyy HH:mm",
        DisplayCulture ?? CultureInfo.GetCultureInfo(LocalizationService.DefaultCultureName));
}

internal sealed record HistoryOrderDetailLine(
    string DisplayName,
    string? ProductCode,
    string? ItemNumber,
    string LookupCode,
    string? ProductImage,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal ActualAmount)
{
    public string ItemNumberDisplay => string.IsNullOrWhiteSpace(ItemNumber) ? "-" : ItemNumber.Trim();

    public string LookupCodeDisplay => string.IsNullOrWhiteSpace(LookupCode) ? "-" : LookupCode.Trim();

    public string MetadataDisplay => $"{ItemNumberDisplay}  {LookupCodeDisplay}";

    public string QuantityDisplay => Quantity.ToString("0.##");
}

public sealed partial class TransactionHistoryViewModel : ObservableObject, IScannerInputTarget, IDisposable
{
    public const string PageId = "TransactionHistory";

    private const int ReuploadBatchSize = 500;
    private static readonly TimeSpan HeldAutoRefreshInterval = TimeSpan.FromSeconds(10);

    private readonly IReceiptQueryService? _receiptQueryService;
    private readonly ISuspendedOrderService? _suspendedOrderService;
    private readonly IRemoteOrderHistoryService? _remoteOrderHistoryService;
    private readonly ISharedHeldOrderCoordinator? _sharedHeldOrderCoordinator;
    private readonly ISharedHeldOrderApiClient? _sharedHeldOrderApiClient;
    private readonly ISharedHeldOrderRepository? _sharedHeldOrderRepository;
    private readonly ISharedHeldOrderPublicationWorker? _sharedHeldOrderPublicationWorker;
    private readonly IInstallmentOrderService _installmentOrderService;
    private readonly IReceiptTextFormatter _receiptTextFormatter;
    private readonly IReceiptPrinterSettingsStore? _receiptPrinterSettingsStore;
    private readonly Func<Task>? _onSuspendedOrderRecalledAsync;
    private readonly Func<InstallmentOrderSummary, Task>? _continueInstallmentPaymentAsync;
    private readonly Action? _returnToPos;
    private readonly ILocalizationService? _localization;
    private readonly ICashierSessionContext _cashierSessionContext;
    private readonly bool _enforcePermissions;
    private readonly IOperationAuditLogger? _operationAuditLogger;
    private readonly IOperationAuthorizationService? _operationAuthorizationService;
    private readonly IOrderUploadExecutionService _orderUploadExecutionService;
    private readonly IConfirmationDialogService? _confirmationDialogService;
    private readonly LocalSellableItemIndex? _localSellableItemIndex;
    private readonly IRawScannerService? _rawScannerService;
    private readonly TimeProvider _timeProvider;
    private readonly ObservableCollection<HistoryOrderDetailLine> _orderDetailLines = [];
    private LocalInstallmentOrder? _selectedInstallmentDetails;
    private bool _suppressSelectedOrderLoad;
    private bool _suppressSourceAutoLoad;
    private bool _disposed;
    private int _activeQueryCount;
    private int _activeConfirmationDialogCount;
    private int _scannerLoadCommandInvocationCount;
    private bool _isScreenVisible;
    private CancellationTokenSource? _heldLoadCancellation;
    private CancellationTokenSource? _heldRemoteLoadCancellation;
    private Task<IReadOnlyList<SharedHeldOrderListItemDto>>? _heldRemoteRequestTask;
    private long _heldLoadGeneration;
    private long _selectedReceiptLoadGeneration;
    private readonly Dictionary<HeldOrderViewScope, IReadOnlyDictionary<Guid, LocalHeldRow>> _heldLocalRowsCache = [];
    private IReadOnlyList<SharedHeldOrderListItemDto> _heldRemoteRowsCache = [];
    private IReadOnlySet<Guid> _heldSyntheticRemoteClaimGuids = new HashSet<Guid>();
    private readonly HashSet<Guid> _lockedInstallmentGuids = [];
    private bool _isInstallmentRecoveryStateUnknown;
    private bool _heldRemoteCacheReady;
    private ITimer? _heldAutoRefreshTimer;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _dateFilterText = string.Empty;

    [ObservableProperty]
    private DateTime? _dateFrom = DateTime.Today;

    [ObservableProperty]
    private DateTime? _dateTo = DateTime.Today;

    [ObservableProperty]
    private string _storeFilterText = string.Empty;

    [ObservableProperty]
    private string _terminalFilterText = string.Empty;

    [ObservableProperty]
    private TerminalFilterOption? _selectedTerminalOption;

    [ObservableProperty]
    private HistorySourceOption? _selectedSourceOption;

    [ObservableProperty]
    private HistoryOrderListItem? _selectedOrder;

    [ObservableProperty]
    private ReceiptDetails? _selectedReceipt;

    [ObservableProperty]
    private bool _isReceiptPreviewOpen;

    [ObservableProperty]
    private bool _isReceiptPreviewLoading;

    [ObservableProperty]
    private bool _isOrderDetailsOpen;

    [ObservableProperty]
    private string _orderDetailsErrorMessage = string.Empty;

    [ObservableProperty]
    private bool _isOrderDetailsEmpty;

    [ObservableProperty]
    private bool _hasOrderDetailsPayments;

    [ObservableProperty]
    private decimal _previewSubtotal;

    [ObservableProperty]
    private decimal _previewDiscount;

    [ObservableProperty]
    private decimal _previewTotal;

    [ObservableProperty]
    private string _previewOrderId = "-";

    [ObservableProperty]
    private string _previewSoldAt = "-";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _heldOrdersRemoteStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isHeldRemoteRefreshing;

    [ObservableProperty]
    private bool _isForceReleaseReasonPromptOpen;

    [ObservableProperty]
    private string _forceReleaseReason = string.Empty;

    [ObservableProperty]
    private HistoryOrderListItem? _forceReleaseCandidate;

    [ObservableProperty]
    private bool _isReuploading;

    [ObservableProperty]
    private PosSessionState _session = new("HB POS", "1002", "Main Branch", "Terminal 04", "C001", "Alice", false, 0);

    public TransactionHistoryViewModel()
        : this(null, null, null, null, null, null, null, null, null, null, false, null, null, null, null, null, null, null, null, null, null, null, null, null, initialize: true)
    {
    }

    public TransactionHistoryViewModel(ILocalOrderRepository orderRepository)
        : this(new ReceiptQueryService(orderRepository), null, null, null, null, null, null, null, null, null, false, null, null, null, null, null, null, null, null, null, null, null, null, null, initialize: true)
    {
    }

    public TransactionHistoryViewModel(IReceiptQueryService receiptQueryService)
        : this(receiptQueryService, null, null, null, null, null, null, null, null, null, false, null, null, null, null, null, null, null, null, null, null, null, null, null, initialize: true)
    {
    }

    public TransactionHistoryViewModel(
        IReceiptQueryService receiptQueryService,
        ISuspendedOrderService? suspendedOrderService,
        IRemoteOrderHistoryService? remoteOrderHistoryService,
        PosSessionState session,
        Func<Task>? onSuspendedOrderRecalledAsync = null,
        Action? returnToPos = null,
        ILocalizationService? localization = null,
        IReceiptTextFormatter? receiptTextFormatter = null,
        IReceiptPrinterSettingsStore? receiptPrinterSettingsStore = null,
        ICashierSessionContext? cashierSessionContext = null,
        bool enforcePermissionsWhenNoCashier = false,
        IInstallmentOrderService? installmentOrderService = null,
        Func<InstallmentOrderSummary, Task>? continueInstallmentPaymentAsync = null,
        IOperationAuditLogger? operationAuditLogger = null,
        IOperationAuthorizationService? operationAuthorizationService = null,
        IOrderUploadExecutionService? orderUploadExecutionService = null,
        IConfirmationDialogService? confirmationDialogService = null,
        ISharedHeldOrderCoordinator? sharedHeldOrderCoordinator = null,
        ISharedHeldOrderApiClient? sharedHeldOrderApiClient = null,
        ISharedHeldOrderRepository? sharedHeldOrderRepository = null,
        TimeProvider? timeProvider = null,
        ISharedHeldOrderPublicationWorker? sharedHeldOrderPublicationWorker = null,
        LocalSellableItemIndex? localSellableItemIndex = null,
        IRawScannerService? rawScannerService = null)
        : this(receiptQueryService, suspendedOrderService, remoteOrderHistoryService, session, onSuspendedOrderRecalledAsync, returnToPos, localization, receiptTextFormatter, receiptPrinterSettingsStore, cashierSessionContext, enforcePermissionsWhenNoCashier, installmentOrderService, continueInstallmentPaymentAsync, operationAuditLogger, operationAuthorizationService, orderUploadExecutionService, confirmationDialogService, sharedHeldOrderCoordinator, sharedHeldOrderApiClient, sharedHeldOrderRepository, timeProvider, sharedHeldOrderPublicationWorker, localSellableItemIndex, rawScannerService, initialize: true)
    {
    }

    private TransactionHistoryViewModel(
        IReceiptQueryService? receiptQueryService,
        ISuspendedOrderService? suspendedOrderService,
        IRemoteOrderHistoryService? remoteOrderHistoryService,
        PosSessionState? session,
        Func<Task>? onSuspendedOrderRecalledAsync,
        Action? returnToPos,
        ILocalizationService? localization,
        IReceiptTextFormatter? receiptTextFormatter,
        IReceiptPrinterSettingsStore? receiptPrinterSettingsStore,
        ICashierSessionContext? cashierSessionContext,
        bool enforcePermissionsWhenNoCashier,
        IInstallmentOrderService? installmentOrderService,
        Func<InstallmentOrderSummary, Task>? continueInstallmentPaymentAsync,
        IOperationAuditLogger? operationAuditLogger,
        IOperationAuthorizationService? operationAuthorizationService,
        IOrderUploadExecutionService? orderUploadExecutionService,
        IConfirmationDialogService? confirmationDialogService,
        ISharedHeldOrderCoordinator? sharedHeldOrderCoordinator,
        ISharedHeldOrderApiClient? sharedHeldOrderApiClient,
        ISharedHeldOrderRepository? sharedHeldOrderRepository,
        TimeProvider? timeProvider,
        ISharedHeldOrderPublicationWorker? sharedHeldOrderPublicationWorker,
        LocalSellableItemIndex? localSellableItemIndex,
        IRawScannerService? rawScannerService,
        bool initialize)
    {
        _receiptQueryService = receiptQueryService;
        _suspendedOrderService = suspendedOrderService;
        _remoteOrderHistoryService = remoteOrderHistoryService;
        _sharedHeldOrderCoordinator = sharedHeldOrderCoordinator;
        _sharedHeldOrderApiClient = sharedHeldOrderApiClient;
        _sharedHeldOrderRepository = sharedHeldOrderRepository;
        _sharedHeldOrderPublicationWorker = sharedHeldOrderPublicationWorker;
        _installmentOrderService = installmentOrderService ?? NoopInstallmentOrderService.Instance;
        _onSuspendedOrderRecalledAsync = onSuspendedOrderRecalledAsync;
        _continueInstallmentPaymentAsync = continueInstallmentPaymentAsync;
        _returnToPos = returnToPos;
        _localization = localization;
        _receiptTextFormatter = receiptTextFormatter ?? new ReceiptTextFormatter();
        _receiptPrinterSettingsStore = receiptPrinterSettingsStore;
        _cashierSessionContext = cashierSessionContext ?? new CashierSessionContext();
        _enforcePermissions = enforcePermissionsWhenNoCashier;
        _operationAuditLogger = operationAuditLogger;
        _operationAuthorizationService = operationAuthorizationService;
        _orderUploadExecutionService = orderUploadExecutionService ?? NoopOrderUploadExecutionService.Instance;
        _confirmationDialogService = confirmationDialogService;
        _localSellableItemIndex = localSellableItemIndex;
        _rawScannerService = rawScannerService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_localization is not null)
        {
            _localization.CultureChanged += OnCultureChanged;
        }

        if (session is not null)
        {
            Session = session;
            if (session.CashierSession is not null)
            {
                _cashierSessionContext.SetCurrent(session.CashierSession);
            }

            StoreFilterText = $"{session.StoreName} ({session.StoreCode})";
            TerminalFilterText = session.DeviceCode;
        }

        RefreshTerminalOptions(selectAllTerminals: session is null);

        RefreshSourceOptions(TransactionHistorySource.LocalOrders);

        LoadCommand = new AsyncRelayCommand(ExecuteLoadCommandAsync);
        ReturnToPosCommand = new RelayCommand(ReturnToPos, CanReturnToPos);
        RecallSelectedCommand = new AsyncRelayCommand(RecallSelectedAsync, CanRecallSelected);
        RecallOrderCommand = new AsyncRelayCommand<HistoryOrderListItem>(RecallOrderAsync, CanRecallOrder);
        ContinueInstallmentPaymentCommand = new AsyncRelayCommand<HistoryOrderListItem>(ContinueInstallmentPaymentAsync, CanContinueInstallmentPayment);
        ConfirmInstallmentPickupCommand = new AsyncRelayCommand<HistoryOrderListItem>(ConfirmInstallmentPickupAsync, CanConfirmInstallmentPickup);
        ReprintCommand = new AsyncRelayCommand(ReprintSelectedAsync, CanReprintSelected);
        RefundCommand = new RelayCommand(() => { }, () => false);
        SelectAllReuploadableCommand = new RelayCommand(SelectAllReuploadable, CanStartReupload);
        ReuploadSelectedCommand = new AsyncRelayCommand(ReuploadSelectedAsync, CanStartReupload);
        ReuploadDateRangeCommand = new AsyncRelayCommand(ReuploadDateRangeAsync, CanReuploadDateRange);
        ApplyQuickDateRangeCommand = new AsyncRelayCommand<string>(ApplyQuickDateRangeAsync);
        DeleteHeldOrderCommand = new AsyncRelayCommand<HistoryOrderListItem>(DeleteHeldOrderAsync, CanDeleteHeldOrder);
        ShareHeldOrderCommand = new AsyncRelayCommand<HistoryOrderListItem>(ShareHeldOrderAsync, CanShareHeldOrder);
        ForceReleaseHeldOrderCommand = new RelayCommand<HistoryOrderListItem>(RequestForceRelease, CanForceReleaseOrder);
        ConfirmForceReleaseCommand = new AsyncRelayCommand(ConfirmForceReleaseAsync, CanConfirmForceRelease);
        CancelForceReleaseCommand = new RelayCommand(CancelForceRelease, CanCancelForceRelease);

        _rawScannerService?.Subscribe(PageId, OnRawBarcodeScanned);
    }

    public event EventHandler? ReprintRequested;

    public string ScannerPageId => PageId;

    public bool ProcessScannerBarcode(string barcode, string devicePath, string source)
    {
        var normalizedBarcode = barcode?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedBarcode))
        {
            ConsoleLog.Write(
                "TransactionHistory",
                $"scanner consumed source={source} device={devicePath} searched=false reason=empty-barcode");
            return true;
        }

        if (EmergencyLoginTokenCodec.HasSupportedPrefix(normalizedBarcode))
        {
            // 关键逻辑：历史页只消费紧急登录令牌，禁止写入查询条件或触发订单查询。
            ConsoleLog.Write(
                "TransactionHistory",
                $"scanner consumed source={source} device={devicePath} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(normalizedBarcode)} searched=false reason=emergency-token");
            return true;
        }

        if (_disposed)
        {
            ConsoleLog.Write(
                "TransactionHistory",
                $"scanner consumed source={source} device={devicePath} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(normalizedBarcode)} searched=false reason=disposed");
            return true;
        }

        if (IsReceiptPreviewOpen || IsOrderDetailsOpen || IsForceReleaseReasonPromptOpen)
        {
            // 关键逻辑：模态内容打开时吞掉扫码，避免在后台替换当前结果和选择状态。
            ConsoleLog.Write(
                "TransactionHistory",
                $"scanner consumed source={source} device={devicePath} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(normalizedBarcode)} searched=false reason=modal-open");
            return true;
        }

        if (Volatile.Read(ref _activeConfirmationDialogCount) > 0)
        {
            ConsoleLog.Write(
                "TransactionHistory",
                $"scanner consumed source={source} device={devicePath} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(normalizedBarcode)} searched=false reason=confirmation-dialog-open");
            return true;
        }

        if (Volatile.Read(ref _activeQueryCount) > 0)
        {
            ConsoleLog.Write(
                "TransactionHistory",
                $"scanner consumed source={source} device={devicePath} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(normalizedBarcode)} searched=false reason=query-busy");
            return true;
        }

        SearchText = normalizedBarcode;
        Interlocked.Increment(ref _scannerLoadCommandInvocationCount);
        try
        {
            LoadCommand.Execute(null);
        }
        finally
        {
            Interlocked.Decrement(ref _scannerLoadCommandInvocationCount);
        }

        ConsoleLog.Write(
            "TransactionHistory",
            $"scanner consumed source={source} device={devicePath} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(normalizedBarcode)} searched=true");
        return true;
    }

    private void OnRawBarcodeScanned(RawBarcodeScannedEventArgs args)
    {
        ProcessScannerBarcode(args.Barcode, args.DevicePath, "raw");
    }

    private async Task<bool> ShowConfirmationDialogAsync(Func<Task<bool>> showDialogAsync)
    {
        Interlocked.Increment(ref _activeConfirmationDialogCount);
        try
        {
            // 关键逻辑：确认框等待期间 Raw Input 仍可能到达，必须与键盘扫码使用相同的后台查询门禁。
            return await showDialogAsync();
        }
        finally
        {
            Interlocked.Decrement(ref _activeConfirmationDialogCount);
        }
    }

    private Task ExecuteLoadCommandAsync()
    {
        var searchText = SearchText ?? string.Empty;
        if (Volatile.Read(ref _scannerLoadCommandInvocationCount) == 0 &&
            _rawScannerService is IScannerInputDeduplicator deduplicator &&
            !deduplicator.TryAcceptScanDelivery(
                searchText,
                "history-load-command",
                _timeProvider.GetUtcNow()))
        {
            // 关键逻辑：查询框有焦点时键盘缓冲会让位给文本输入，Enter 绑定仍要消除同一次 Raw 扫码的重复提交。
            ConsoleLog.Write(
                "TransactionHistory",
                $"load command duplicate suppressed barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(searchText)}");
            return Task.CompletedTask;
        }

        return LoadAsync();
    }

    public ObservableCollection<HistorySourceOption> SourceOptions { get; } = [];

    public ObservableCollection<TerminalFilterOption> TerminalOptions { get; } = [];

    public ObservableCollection<HistoryOrderListItem> Orders { get; } = [];

    public ObservableCollection<ReceiptPreviewLine> ReceiptLines { get; } = [];

    public ObservableCollection<ReceiptPaymentLine> Payments { get; } = [];

    public ObservableCollection<ReceiptPreviewRow> ReceiptPreviewRows { get; } = [];

    public System.Collections.IEnumerable OrderDetailLines => _orderDetailLines;

    internal IReadOnlyList<HistoryOrderDetailLine> OrderDetailLinesForTests => _orderDetailLines;

    public IAsyncRelayCommand LoadCommand { get; }

    /// <summary>筛选栏快捷日期范围（参数见 QuickDateRange* 常量），设好起止日期后立即按当前筛选查询。</summary>
    public IAsyncRelayCommand<string> ApplyQuickDateRangeCommand { get; }

    public IRelayCommand ReturnToPosCommand { get; }

    public IAsyncRelayCommand RecallSelectedCommand { get; }

    public IAsyncRelayCommand<HistoryOrderListItem> RecallOrderCommand { get; }

    public IAsyncRelayCommand<HistoryOrderListItem> ContinueInstallmentPaymentCommand { get; }

    public IAsyncRelayCommand<HistoryOrderListItem> ConfirmInstallmentPickupCommand { get; }

    public IRelayCommand ReprintCommand { get; }

    public IRelayCommand RefundCommand { get; }

    public IRelayCommand SelectAllReuploadableCommand { get; }

    public IAsyncRelayCommand ReuploadSelectedCommand { get; }

    public IAsyncRelayCommand ReuploadDateRangeCommand { get; }

    public IAsyncRelayCommand<HistoryOrderListItem> DeleteHeldOrderCommand { get; }

    public IAsyncRelayCommand<HistoryOrderListItem> ShareHeldOrderCommand { get; }

    public IRelayCommand<HistoryOrderListItem> ForceReleaseHeldOrderCommand { get; }

    public IAsyncRelayCommand ConfirmForceReleaseCommand { get; }

    public IRelayCommand CancelForceReleaseCommand { get; }

    public TransactionHistorySource SelectedSource => SelectedSourceOption?.Source ?? TransactionHistorySource.LocalOrders;

    public bool IsRecallVisible => SelectedOrder?.CanRecall == true;

    public bool IsReprintVisible => CanReprintSelected();

    public bool IsContinueInstallmentPaymentVisible => CanContinueInstallmentPayment(SelectedOrder);

    public bool IsConfirmInstallmentPickupVisible => CanConfirmInstallmentPickup(SelectedOrder);

    public string OrderDetailsOrderId => ValueOrDash(
        SelectedReceipt?.OrderDisplay,
        SelectedOrder?.DisplayOrderId);

    public string OrderDetailsSourceLabel => SelectedOrder is { } order
        ? SourceOptions.FirstOrDefault(option => option.Source == order.Source)?.Label ?? order.Source.ToString()
        : "-";

    public string OrderDetailsStatusLabel => CurrentOrderInstallmentDetails is { } details
        ? LocalizeInstallmentStatus(details.Status.ToString())
        : ValueOrDash(SelectedOrder?.StatusLabel);

    public string OrderDetailsSoldAt => SelectedReceipt is { } receipt
        ? receipt.SoldAt.ToLocalTime().ToString("MMM dd, yyyy HH:mm", CurrentDisplayCulture)
        : SelectedOrder?.SoldAtDisplay ?? "-";

    public string OrderDetailsStoreCode => ValueOrDash(SelectedReceipt?.StoreCode, SelectedOrder?.StoreCode);

    public string OrderDetailsDeviceCode => ValueOrDash(SelectedReceipt?.DeviceCode, SelectedOrder?.DeviceCode);

    public string OrderDetailsCashierName => ValueOrDash(
        SelectedReceipt?.CashierName,
        SelectedOrder?.IsInstallmentOrder == true ? null : SelectedOrder?.CashierName);

    public bool HasOrderDetailsCustomer => CurrentOrderInstallmentDetails is { } details
        ? !string.IsNullOrWhiteSpace(details.CustomerName) || !string.IsNullOrWhiteSpace(details.CustomerPhone)
        : SelectedOrder?.InstallmentOrder is { } summary &&
            (!string.IsNullOrWhiteSpace(summary.CustomerName) || !string.IsNullOrWhiteSpace(summary.CustomerPhone));

    public string OrderDetailsCustomerName => CurrentOrderInstallmentDetails is { } details
        ? ValueOrDash(details.CustomerName)
        : ValueOrDash(SelectedOrder?.InstallmentOrder?.CustomerName);

    public string OrderDetailsCustomerPhone => CurrentOrderInstallmentDetails is { } details
        ? ValueOrDash(details.CustomerPhone)
        : ValueOrDash(SelectedOrder?.InstallmentOrder?.CustomerPhone);

    public bool HasOrderDetailsTenderedAmount => SelectedReceipt?.TenderedAmount is not null;

    public decimal OrderDetailsTenderedAmount => SelectedReceipt?.TenderedAmount ?? 0m;

    public bool HasOrderDetailsChangeAmount => SelectedReceipt?.ChangeAmount is not null;

    public decimal OrderDetailsChangeAmount => SelectedReceipt?.ChangeAmount ?? 0m;

    public bool HasOrderDetailsInstallmentAmounts =>
        CurrentOrderInstallmentDetails is not null || SelectedOrder?.InstallmentOrder is not null;

    public decimal OrderDetailsPaidAmount =>
        CurrentOrderInstallmentDetails?.PaidAmount ?? SelectedOrder?.InstallmentOrder?.PaidAmount ?? 0m;

    public decimal OrderDetailsOutstandingAmount =>
        CurrentOrderInstallmentDetails?.BalanceAmount ?? SelectedOrder?.InstallmentOrder?.OutstandingAmount ?? 0m;

    public bool IsOrderDetailsFinancialContentVisible =>
        SelectedReceipt is not null &&
        !IsReceiptPreviewLoading &&
        string.IsNullOrWhiteSpace(OrderDetailsErrorMessage);

    // 补打已取消分期时用于一并补打退款券；只返回与当前选中单匹配的快照，避免切单后打错券。
    internal LocalInstallmentOrder? SelectedInstallmentDetailsForReprint => CurrentOrderInstallmentDetails;

    private LocalInstallmentOrder? CurrentOrderInstallmentDetails
    {
        get
        {
            if (SelectedOrder is not { IsInstallmentOrder: true } order ||
                _selectedInstallmentDetails is not { } details)
            {
                return null;
            }

            return details.InstallmentGuid == order.OrderGuid || details.OrderGuid == order.OrderGuid
                ? details
                : null;
        }
    }

    public bool IsLocalSourceSelected
    {
        get => SelectedSource == TransactionHistorySource.LocalOrders;
        set
        {
            if (value)
            {
                SetSelectedSource(TransactionHistorySource.LocalOrders);
            }
        }
    }

    public bool IsOnlineSourceSelected
    {
        get => SelectedSource == TransactionHistorySource.RemoteOrders;
        set
        {
            if (value)
            {
                SetSelectedSource(TransactionHistorySource.RemoteOrders);
            }
        }
    }

    public bool IsInstallmentSourceSelected
    {
        get => SelectedSource == TransactionHistorySource.InstallmentOrders;
        set
        {
            if (value)
            {
                SetSelectedSource(TransactionHistorySource.InstallmentOrders);
            }
        }
    }

    public bool IsHeldSourceSelected
    {
        get => SelectedSource == TransactionHistorySource.HeldOrders;
        set
        {
            if (value)
            {
                SetSelectedSource(TransactionHistorySource.HeldOrders);
            }
        }
    }

    /// <summary>挂单页签选择器与终端筛选只互斥显示：挂单模式隐藏 terminal filter。</summary>
    public bool IsTerminalFilterVisible => SelectedSource != TransactionHistorySource.HeldOrders;

    public bool IsHeldScopeSelectorVisible => SelectedSource == TransactionHistorySource.HeldOrders;

    public bool IsHeldRemoteRefreshVisible =>
        IsHeldSourceSelected && IsHeldOtherScopeSelected && IsHeldRemoteRefreshing;

    public bool IsHeldRemoteErrorVisible =>
        IsHeldSourceSelected &&
        IsHeldOtherScopeSelected &&
        !string.IsNullOrWhiteSpace(HeldOrdersRemoteStatusMessage);

    private HeldOrderViewScope SelectedHeldScope { get; set; } = HeldOrderViewScope.Local;

    public bool IsHeldLocalScopeSelected
    {
        get => SelectedHeldScope == HeldOrderViewScope.Local;
        set
        {
            if (value)
            {
                SetHeldScope(HeldOrderViewScope.Local);
            }
        }
    }

    public bool IsHeldOtherScopeSelected
    {
        get => SelectedHeldScope == HeldOrderViewScope.Other;
        set
        {
            if (value)
            {
                SetHeldScope(HeldOrderViewScope.Other);
            }
        }
    }

    private void SetHeldScope(HeldOrderViewScope scope)
    {
        if (SelectedHeldScope == scope)
        {
            return;
        }

        SelectedHeldScope = scope;
        OnPropertyChanged(nameof(IsHeldLocalScopeSelected));
        OnPropertyChanged(nameof(IsHeldOtherScopeSelected));
        OnPropertyChanged(nameof(IsHeldRemoteRefreshVisible));
        OnPropertyChanged(nameof(IsHeldRemoteErrorVisible));
        if (SelectedSource == TransactionHistorySource.HeldOrders)
        {
            CancelHeldLoad();
            _ = LoadAsync(CancellationToken.None);
        }
    }

    private void ResetHeldScopeToLocal()
    {
        if (SelectedHeldScope == HeldOrderViewScope.Local)
        {
            return;
        }

        SelectedHeldScope = HeldOrderViewScope.Local;
        OnPropertyChanged(nameof(IsHeldLocalScopeSelected));
        OnPropertyChanged(nameof(IsHeldOtherScopeSelected));
        OnPropertyChanged(nameof(IsHeldRemoteRefreshVisible));
        OnPropertyChanged(nameof(IsHeldRemoteErrorVisible));
    }

    partial void OnIsHeldRemoteRefreshingChanged(bool value) =>
        OnPropertyChanged(nameof(IsHeldRemoteRefreshVisible));

    partial void OnHeldOrdersRemoteStatusMessageChanged(string value) =>
        OnPropertyChanged(nameof(IsHeldRemoteErrorVisible));

    public bool IsStandardSourceSelected => !IsInstallmentSourceSelected;

    public bool IsForceReleaseVisible => SelectedOrder?.CanForceRelease == true;

    public string HeldOrdersSourceLabel => T("history.source.held");

    public string DeleteHeldOrderLabel => T("history.held.delete");

    public string ShareHeldOrderLabel => T("history.held.share");

    public string ShareStatusHeaderLabel => T("history.held.shareStatus");

    public string HeldLocalScopeLabel => T("history.held.localScope");

    public string HeldOtherScopeLabel => T("history.held.otherScope");

    public string ForceReleaseLabel => T("history.held.forceRelease");

    public string ForceReleaseHeaderLabel => T("history.held.forceRelease");

    public string ForceReleaseReasonLabel => T("history.held.forceReleaseReason");

    public string ForceReleaseConfirmLabel => T("history.held.forceReleaseConfirm");

    public string ForceReleaseCancelLabel => T("history.held.forceReleaseCancel");

    public string TitleText => T("TransactionHistory");

    public string SearchHintText => T("history.search");

    public string ReceiptPreviewLabel => T("success.receiptPreview");

    public string ReprintLabel => T("history.reprint");

    public string RefundLabel => T("history.refund");

    public XmlLanguage CurrentUiLanguage =>
        XmlLanguage.GetLanguage(CurrentDisplayCulture.IetfLanguageTag);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _activeQueryCount);
        try
        {
            StatusMessage = string.Empty;
            if (SelectedSource == TransactionHistorySource.HeldOrders)
            {
                // 挂单列表由本地/远端分阶段更新，不能在远端完成后再用旧返回值整表覆盖。
                await LoadHeldOrdersAsync(cancellationToken);
                if (SelectedSource != TransactionHistorySource.HeldOrders)
                {
                    return;
                }

                if (SelectedOrder is null)
                {
                    InvalidateSelectedReceiptLoad();
                    ClearReceiptPreview();
                    IsReceiptPreviewLoading = false;
                    return;
                }

                await LoadSelectedReceiptSafelyAsync(SelectedOrder, cancellationToken);
                return;
            }

            var orders = SelectedSource switch
            {
                TransactionHistorySource.RemoteOrders => await LoadRemoteOrdersAsync(cancellationToken),
                TransactionHistorySource.InstallmentOrders => await LoadInstallmentOrdersAsync(cancellationToken),
                _ => await LoadLocalAndSuspendedOrdersAsync(cancellationToken)
            };

            Orders.ReplaceWith(orders);
            _suppressSelectedOrderLoad = true;
            SelectedOrder = Orders.FirstOrDefault();
            _suppressSelectedOrderLoad = false;

            if (SelectedOrder is null)
            {
                InvalidateSelectedReceiptLoad();
                ClearReceiptPreview();
                IsReceiptPreviewLoading = false;
                return;
            }

            // 列表查询完成即可进入历史页；首行详情独立加载，详情故障不能撤销已显示的列表。
            _ = LoadSelectedReceiptSafelyAsync(SelectedOrder);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            // HttpClient 超时也会表现为取消异常；仅让调用方主动取消继续向上传播。
            Orders.Clear();
            InvalidateSelectedReceiptLoad();
            ClearReceiptPreview();
            IsReceiptPreviewLoading = false;
            StatusMessage = ex.Message;
        }
        finally
        {
            Interlocked.Decrement(ref _activeQueryCount);
        }
    }

    /// <summary>
    /// 收银页“取回”入口：直接落在挂单（Held）页签的本机范围，日期回到当天。
    /// </summary>
    public Task ShowSuspendedOrdersAsync(CancellationToken cancellationToken = default)
    {
        ResetDateRangeToToday();
        _suppressSourceAutoLoad = true;
        SelectedSourceOption = SourceOptions.First(x => x.Source == TransactionHistorySource.HeldOrders);
        _suppressSourceAutoLoad = false;
        // 上次离开时已停在挂单页签则不会触发来源变更回调，这里显式回到本机范围。
        ResetHeldScopeToLocal();
        return LoadAsync(cancellationToken);
    }

    /// <summary>
    /// 每次从导航进入历史页时把日期范围重置为当天。
    /// 视图模型首次创建后常驻复用，字段初值只在创建那天有效；收银机跨夜不重启时
    /// 若不重置，次日进入仍会停在前一天。
    /// </summary>
    public void ResetDateRangeToToday()
    {
        var today = _timeProvider.GetLocalNow().Date;
        DateFrom = today;
        DateTo = today;
    }

    public const string QuickDateRangeToday = "today";
    public const string QuickDateRangeYesterday = "yesterday";
    public const string QuickDateRangeLast7Days = "last7days";

    private Task ApplyQuickDateRangeAsync(string? range)
    {
        var today = _timeProvider.GetLocalNow().Date;
        (DateTime From, DateTime To)? dates = range switch
        {
            QuickDateRangeToday => (today, today),
            QuickDateRangeYesterday => (today.AddDays(-1), today.AddDays(-1)),
            // 近 7 天含今天：今天往前推 6 天。
            QuickDateRangeLast7Days => (today.AddDays(-6), today),
            _ => null
        };
        if (dates is null)
        {
            return Task.CompletedTask;
        }

        DateFrom = dates.Value.From;
        DateTo = dates.Value.To;
        // 不走 LoadCommand：它的扫码去重以查询框文字为键，快捷按钮不是扫码，不能被同一文字的去重吞掉。
        return LoadAsync();
    }

    partial void OnSelectedSourceOptionChanged(HistorySourceOption? value)
    {
        if (value?.Source == TransactionHistorySource.HeldOrders)
        {
            // 每次进入挂单页都从本机开始，避免沿用上次离开时的非本机页签。
            ResetHeldScopeToLocal();
        }
        else
        {
            CancelHeldLoad();
        }

        OnPropertyChanged(nameof(SelectedSource));
        OnPropertyChanged(nameof(IsRecallVisible));
        OnPropertyChanged(nameof(IsReprintVisible));
        OnPropertyChanged(nameof(IsLocalSourceSelected));
        OnPropertyChanged(nameof(IsOnlineSourceSelected));
        OnPropertyChanged(nameof(IsInstallmentSourceSelected));
        OnPropertyChanged(nameof(IsHeldSourceSelected));
        OnPropertyChanged(nameof(IsTerminalFilterVisible));
        OnPropertyChanged(nameof(IsHeldScopeSelectorVisible));
        OnPropertyChanged(nameof(IsHeldRemoteRefreshVisible));
        OnPropertyChanged(nameof(IsHeldRemoteErrorVisible));
        OnPropertyChanged(nameof(IsStandardSourceSelected));
        OnPropertyChanged(nameof(IsForceReleaseVisible));
        OnPropertyChanged(nameof(IsContinueInstallmentPaymentVisible));
        OnPropertyChanged(nameof(IsConfirmInstallmentPickupVisible));
        ForceReleaseHeldOrderCommand?.NotifyCanExecuteChanged();
        DeleteHeldOrderCommand?.NotifyCanExecuteChanged();
        ShareHeldOrderCommand?.NotifyCanExecuteChanged();
        ConfirmForceReleaseCommand?.NotifyCanExecuteChanged();
        CancelForceReleaseCommand?.NotifyCanExecuteChanged();
        ReprintCommand?.NotifyCanExecuteChanged();
        RecallSelectedCommand?.NotifyCanExecuteChanged();
        RecallOrderCommand?.NotifyCanExecuteChanged();
        ContinueInstallmentPaymentCommand?.NotifyCanExecuteChanged();
        ConfirmInstallmentPickupCommand?.NotifyCanExecuteChanged();
        NotifyReuploadCanExecuteChanged();
        UpdateHeldAutoRefresh();
        if (!_suppressSourceAutoLoad)
        {
            _ = LoadAsync(CancellationToken.None);
        }
    }

    partial void OnSelectedOrderChanged(HistoryOrderListItem? value)
    {
        ReprintCommand?.NotifyCanExecuteChanged();
        OpenOrderDetailsCommand.NotifyCanExecuteChanged();
        RetryOrderDetailsCommand.NotifyCanExecuteChanged();
        RecallSelectedCommand?.NotifyCanExecuteChanged();
        RecallOrderCommand?.NotifyCanExecuteChanged();
        ContinueInstallmentPaymentCommand?.NotifyCanExecuteChanged();
        ConfirmInstallmentPickupCommand?.NotifyCanExecuteChanged();
        ForceReleaseHeldOrderCommand?.NotifyCanExecuteChanged();
        DeleteHeldOrderCommand?.NotifyCanExecuteChanged();
        ShareHeldOrderCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsRecallVisible));
        OnPropertyChanged(nameof(IsReprintVisible));
        OnPropertyChanged(nameof(IsForceReleaseVisible));
        OnPropertyChanged(nameof(IsContinueInstallmentPaymentVisible));
        OnPropertyChanged(nameof(IsConfirmInstallmentPickupVisible));
        NotifyOrderDetailsProperties();

        if (_suppressSelectedOrderLoad)
        {
            return;
        }

        _ = LoadSelectedReceiptSafelyAsync(value);
    }

    private async Task LoadSelectedReceiptSafelyAsync(
        HistoryOrderListItem? expectedOrder,
        CancellationToken cancellationToken = default)
    {
        var generation = BeginSelectedReceiptLoad();
        if (!string.IsNullOrWhiteSpace(OrderDetailsErrorMessage) &&
            string.Equals(StatusMessage, OrderDetailsErrorMessage, StringComparison.Ordinal))
        {
            StatusMessage = string.Empty;
        }

        OrderDetailsErrorMessage = string.Empty;
        IsOrderDetailsEmpty = false;
        IsReceiptPreviewLoading = true;
        try
        {
            await LoadSelectedReceiptAsync(expectedOrder, generation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!IsCurrentSelectedReceiptLoad(expectedOrder, generation))
            {
                return;
            }

            // 属性变更回调不能把故障 Task 留在后台；HttpClient 超时在这里统一转为可见状态。
            ClearReceiptPreview();
            OrderDetailsErrorMessage = ex is OperationCanceledException
                ? T("history.detailsLoadTimeout")
                : ex.Message;
            StatusMessage = OrderDetailsErrorMessage;
        }
        finally
        {
            if (IsCurrentSelectedReceiptLoad(expectedOrder, generation))
            {
                IsReceiptPreviewLoading = false;
            }
        }
    }

    private long BeginSelectedReceiptLoad() =>
        Interlocked.Increment(ref _selectedReceiptLoadGeneration);

    private void InvalidateSelectedReceiptLoad() =>
        Interlocked.Increment(ref _selectedReceiptLoadGeneration);

    private bool IsCurrentSelectedReceiptLoad(HistoryOrderListItem? expectedOrder, long generation)
    {
        if (generation != Volatile.Read(ref _selectedReceiptLoadGeneration))
        {
            return false;
        }

        var selectedOrder = SelectedOrder;
        if (selectedOrder is null || expectedOrder is null)
        {
            return selectedOrder is null && expectedOrder is null;
        }

        // 挂单后台刷新会替换同一订单的行对象；代次负责阻止 A→B→A 的旧请求，
        // 逻辑身份校验则允许同一订单刷新后的新行继续接收当前请求结果。
        return selectedOrder.OrderGuid == expectedOrder.OrderGuid &&
            selectedOrder.Source == expectedOrder.Source &&
            selectedOrder.IsSuspendedOrder == expectedOrder.IsSuspendedOrder &&
            selectedOrder.IsInstallmentOrder == expectedOrder.IsInstallmentOrder &&
            selectedOrder.IsHeldOrder == expectedOrder.IsHeldOrder;
    }

    partial void OnSelectedReceiptChanged(ReceiptDetails? value)
    {
        RefreshOrderDetailLines(value);
        ReprintCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsReprintVisible));
        NotifyOrderDetailsProperties();
    }

    partial void OnIsReceiptPreviewLoadingChanged(bool value)
    {
        RetryOrderDetailsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsOrderDetailsFinancialContentVisible));
    }

    partial void OnOrderDetailsErrorMessageChanged(string value) =>
        OnPropertyChanged(nameof(IsOrderDetailsFinancialContentVisible));

    partial void OnIsOrderDetailsOpenChanged(bool value)
    {
        RetryOrderDetailsCommand.NotifyCanExecuteChanged();
    }

    partial void OnSessionChanged(PosSessionState value)
    {
        CancelHeldLoad();
        _heldLocalRowsCache.Clear();
        _heldRemoteRowsCache = [];
        _heldSyntheticRemoteClaimGuids = new HashSet<Guid>();
        _heldRemoteCacheReady = false;
        HeldOrdersRemoteStatusMessage = string.Empty;
        IsHeldRemoteRefreshing = false;
        ResetHeldScopeToLocal();
        if (value.CashierSession is not null)
        {
            _cashierSessionContext.SetCurrent(value.CashierSession);
        }

        StoreFilterText = $"{value.StoreName} ({value.StoreCode})";
        RefreshTerminalOptions(SelectedTerminalOption?.DeviceCode is null);
        ConfirmInstallmentPickupCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsConfirmInstallmentPickupVisible));
    }

    partial void OnSelectedTerminalOptionChanged(TerminalFilterOption? value)
    {
        TerminalFilterText = value?.DeviceCode ?? T("history.allTerminals");
    }

    private async Task<IReadOnlyList<HistoryOrderListItem>> LoadLocalAndSuspendedOrdersAsync(CancellationToken cancellationToken)
    {
        var localOrdersTask = LoadLocalOrdersAsync(cancellationToken);
        var suspendedOrdersTask = LoadSuspendedOrdersAsync(cancellationToken);
        await Task.WhenAll(localOrdersTask, suspendedOrdersTask);

        return localOrdersTask.Result
            .Concat(suspendedOrdersTask.Result)
            .OrderByDescending(order => order.OccurredAt)
            .ToList();
    }

    private async Task<IReadOnlyList<HistoryOrderListItem>> LoadLocalOrdersAsync(CancellationToken cancellationToken)
    {
        if (_receiptQueryService is null)
        {
            return [];
        }

        var query = new LocalOrderHistoryQuery(
            ParseDateFrom(DateFrom),
            ParseDateTo(DateTo),
            SelectedTerminalDeviceCode,
            NormalizeKeyword(SearchText));
        var orders = await _receiptQueryService.GetRecentOrdersAsync(query, 100, cancellationToken);
        return orders
            .Select(order => new HistoryOrderListItem(
                order.OrderGuid,
                TransactionHistorySource.LocalOrders,
                order.StoreCode,
                order.DeviceCode,
                order.CashierName,
                order.SoldAt,
                order.TotalAmount,
                order.DiscountAmount,
                order.ActualAmount,
                order.LineCount,
                order.PaymentSummary,
                order.StatusLabel,
                SyncStatus: order.SyncStatus,
                DisplayCulture: CurrentDisplayCulture))
            .ToList();
    }

    partial void OnDateFromChanged(DateTime? value) => ReuploadDateRangeCommand?.NotifyCanExecuteChanged();

    partial void OnDateToChanged(DateTime? value) => ReuploadDateRangeCommand?.NotifyCanExecuteChanged();

    partial void OnIsReuploadingChanged(bool value) => NotifyReuploadCanExecuteChanged();

    private void SelectAllReuploadable()
    {
        foreach (var order in Orders.Where(order => order.CanReupload))
        {
            order.Selection.IsSelected = true;
        }
    }

    private async Task ReuploadSelectedAsync()
    {
        var selected = Orders
            .Where(order => order.CanReupload && order.Selection.IsSelected)
            .Select(order => order.OrderGuid)
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        IsReuploading = true;
        try
        {
            var result = await _orderUploadExecutionService.ExecuteSelectedAsync(selected);
            await LoadAsync();
            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                T("history.reuploadCompleted"),
                result.UploadedCount,
                result.FailedCount);
        }
        finally
        {
            IsReuploading = false;
        }
    }

    private bool CanStartReupload() => IsLocalSourceSelected && !IsReuploading;

    private bool CanReuploadDateRange() =>
        CanStartReupload() &&
        DateFrom is not null &&
        DateTo is not null &&
        DateFrom.Value.Date <= DateTo.Value.Date;

    private void NotifyReuploadCanExecuteChanged()
    {
        SelectAllReuploadableCommand?.NotifyCanExecuteChanged();
        ReuploadSelectedCommand?.NotifyCanExecuteChanged();
        ReuploadDateRangeCommand?.NotifyCanExecuteChanged();
    }

    private async Task ReuploadDateRangeAsync()
    {
        var dateFrom = DateFrom;
        var dateTo = DateTo;
        if (!CanReuploadDateRange() || dateFrom is null || dateTo is null)
        {
            return;
        }

        var soldFrom = ParseDateFrom(dateFrom)!.Value;
        var soldTo = ParseDateTo(dateTo)!.Value;

        IsReuploading = true;
        try
        {
            var orderGuids = await _orderUploadExecutionService.GetReuploadableOrderGuidsAsync(
                soldFrom,
                soldTo,
                SelectedTerminalDeviceCode);
            if (orderGuids.Count == 0)
            {
                StatusMessage = T("history.reuploadRangeEmpty");
                return;
            }

            var batchCount = (orderGuids.Count + ReuploadBatchSize - 1) / ReuploadBatchSize;
            var confirmationDialogService = _confirmationDialogService;
            if (confirmationDialogService is null ||
                !await ShowConfirmationDialogAsync(() => confirmationDialogService.ConfirmOrderDateRangeReuploadAsync(
                    orderGuids.Count,
                    batchCount,
                    dateFrom.Value,
                    dateTo.Value)))
            {
                StatusMessage = T("history.reuploadRangeCancelled");
                return;
            }

            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                T("history.reuploadRangeProgress"),
                orderGuids.Count,
                batchCount);
            var attemptedCount = 0;
            var uploadedCount = 0;
            var failedCount = 0;
            var wasInterrupted = false;
            foreach (var batch in orderGuids.Chunk(ReuploadBatchSize))
            {
                var result = await _orderUploadExecutionService.ExecuteSelectedAsync(batch);
                attemptedCount += result.AttemptedCount;
                uploadedCount += result.UploadedCount;
                failedCount += result.FailedCount;
                if (result.WasInterrupted)
                {
                    wasInterrupted = true;
                    break;
                }
            }

            await LoadAsync();
            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                T(wasInterrupted ? "history.reuploadRangeInterrupted" : "history.reuploadRangeCompleted"),
                attemptedCount,
                uploadedCount,
                failedCount);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = T("history.reuploadRangeCancelled");
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(CultureInfo.CurrentCulture, T("history.reuploadRangeFailed"), ex.Message);
        }
        finally
        {
            IsReuploading = false;
        }
    }

    private async Task<IReadOnlyList<HistoryOrderListItem>> LoadSuspendedOrdersAsync(CancellationToken cancellationToken)
    {
        if (_suspendedOrderService is null)
        {
            return [];
        }

        var orders = await _suspendedOrderService.GetPendingOrdersAsync(
            Session.StoreCode,
            SelectedTerminalDeviceCode,
            NormalizeKeyword(SearchText),
            100,
            cancellationToken);
        var from = ParseDateFrom(DateFrom);
        var to = ParseDateTo(DateTo);
        return orders
            .Where(order => from is null || order.SuspendedAt >= from.Value)
            .Where(order => to is null || order.SuspendedAt <= to.Value)
            .Select(order => new HistoryOrderListItem(
                order.SuspendedOrderGuid,
                TransactionHistorySource.LocalOrders,
                order.StoreCode,
                order.DeviceCode,
                order.CashierName,
                order.SuspendedAt,
                order.TotalAmount,
                order.DiscountAmount,
                order.ActualAmount,
                order.LineCount,
                T("history.payment.suspended"),
                T("history.status.pendingRecall"),
                IsSuspendedOrder: true,
                CanRecall: true,
                DisplayCulture: CurrentDisplayCulture))
            .ToList();
    }

    private sealed record LocalHeldRow(
        SuspendedOrderSummary Order,
        SharedHeldOrderPublication? Publication,
        HeldClaimStatus? ClaimStatus,
        Guid? ClaimId);

    private sealed record LocalHeldLoadResult(
        IReadOnlyDictionary<Guid, LocalHeldRow> Rows,
        IReadOnlySet<Guid> SyntheticRemoteClaimHoldGuids);

    /// <summary>
    /// 共享挂单源：合并本机待发布/已发布挂单与服务端 Pending 汇总，按 HoldGuid 去重。
    /// 每轮刷新取消上一轮并递增代次；本地 SQLite 先显示，远端结果只在代次仍有效时收敛。
    /// </summary>
    private Task<IReadOnlyList<HistoryOrderListItem>> LoadHeldOrdersAsync(CancellationToken cancellationToken)
    {
        var generation = ++_heldLoadGeneration;
        var scope = SelectedHeldScope;
        var currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousCancellation = _heldLoadCancellation;
        _heldLoadCancellation = currentCancellation;
        previousCancellation?.Cancel();
        return CompleteHeldLoadAsync(scope, generation, currentCancellation);
    }

    private async Task<IReadOnlyList<HistoryOrderListItem>> CompleteHeldLoadAsync(
        HeldOrderViewScope scope,
        long generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            return await LoadHeldOrdersGuardedAsync(scope, generation, cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_heldLoadCancellation, cancellation))
            {
                _heldLoadCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelHeldLoad()
    {
        _heldLoadGeneration++;
        _heldLoadCancellation?.Cancel();
        var remoteCancellation = _heldRemoteLoadCancellation;
        _heldRemoteLoadCancellation = null;
        _heldRemoteRequestTask = null;
        remoteCancellation?.Cancel();
    }

    private async Task<IReadOnlyList<HistoryOrderListItem>> LoadHeldOrdersGuardedAsync(
        HeldOrderViewScope scope,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await LoadHeldOrdersCoreAsync(scope, generation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return CurrentHeldRows();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
            // 本地读取失败只保留当前页签自己的缓存，绝不把另一页签或其他来源行带过来。
            var retained = CachedHeldRows(scope);
            ReplaceHeldOrdersIfCurrent(retained, generation);
            return retained;
        }
    }

    private IReadOnlyList<HistoryOrderListItem> CurrentHeldRows() =>
        Orders.Where(order => order.Source == TransactionHistorySource.HeldOrders).ToArray();

    private async Task<IReadOnlyList<HistoryOrderListItem>> LoadHeldOrdersCoreAsync(
        HeldOrderViewScope scope,
        long generation,
        CancellationToken cancellationToken)
    {
        // 切页先显示该页签上次缓存；首次进入无缓存时立即清掉上一来源/页签残留。
        ReplaceHeldOrdersIfCurrent(CachedHeldRows(scope), generation);

        // 1) 本地先加载并立即展示（本机页签默认；非本机页签只显示其他 device）。
        var localLoad = await LoadLocalHeldRowsAsync(scope, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != _heldLoadGeneration)
        {
            return CurrentHeldRows();
        }

        var localRows = localLoad.Rows;
        _heldSyntheticRemoteClaimGuids = localLoad.SyntheticRemoteClaimHoldGuids;
        _heldLocalRowsCache[scope] = localRows;
        var cachedRemoteRows = FilterRemoteHeldRows(
            _heldRemoteRowsCache,
            scope,
            localLoad.SyntheticRemoteClaimHoldGuids);
        var localFirst = MergeHeldRows(localRows, cachedRemoteRows, remoteAuthoritative: false);
        ReplaceHeldOrdersIfCurrent(localFirst, generation);

        if (_sharedHeldOrderApiClient is null || !Session.IsOnline)
        {
            // 离线列表只能以本地快照为准；发起远端刷新会把成功空响应误当成权威收敛。
            return localFirst;
        }

        // 2) 远端独立后台刷新：LoadAsync 在本地 ready 后立即返回；失败保留缓存，
        // 成功才按服务端 Pending 列表权威收敛 Published 行。
        if (scope == HeldOrderViewScope.Other)
        {
            HeldOrdersRemoteStatusMessage = string.Empty;
        }
        LastHeldRemoteRefreshTask = StartHeldRemoteRefresh(
            localRows,
            localLoad.SyntheticRemoteClaimHoldGuids,
            scope,
            generation);
        return localFirst;
    }

    private IReadOnlyList<HistoryOrderListItem> CachedHeldRows(HeldOrderViewScope scope)
    {
        var localRows = _heldLocalRowsCache.TryGetValue(scope, out var cachedLocalRows)
            ? cachedLocalRows
            : new Dictionary<Guid, LocalHeldRow>();
        var remoteRows = _heldRemoteCacheReady
            ? FilterRemoteHeldRows(_heldRemoteRowsCache, scope, _heldSyntheticRemoteClaimGuids)
            : [];
        return MergeHeldRows(localRows, remoteRows, remoteAuthoritative: false);
    }

    internal Task? LastHeldRemoteRefreshTask { get; private set; }

    private Task StartHeldRemoteRefresh(
        IReadOnlyDictionary<Guid, LocalHeldRow> localRows,
        IReadOnlySet<Guid> syntheticRemoteClaimHoldGuids,
        HeldOrderViewScope scope,
        long generation)
    {
        var remoteRequest = GetOrStartHeldRemoteRequest();
        IsHeldRemoteRefreshing = true;
        return CompleteHeldRemoteRefreshAsync(
            localRows,
            syntheticRemoteClaimHoldGuids,
            scope,
            generation,
            remoteRequest);
    }

    private Task<IReadOnlyList<SharedHeldOrderListItemDto>> GetOrStartHeldRemoteRequest()
    {
        if (_heldRemoteRequestTask is { IsCompleted: false })
        {
            return _heldRemoteRequestTask;
        }

        var cancellation = new CancellationTokenSource();
        _heldRemoteLoadCancellation = cancellation;
        var request = FetchHeldRemoteRowsAsync(cancellation);
        _heldRemoteRequestTask = request;
        return request;
    }

    private async Task<IReadOnlyList<SharedHeldOrderListItemDto>> FetchHeldRemoteRowsAsync(
        CancellationTokenSource cancellation)
    {
        try
        {
            return await _sharedHeldOrderApiClient!.ListPendingAsync(cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_heldRemoteLoadCancellation, cancellation))
            {
                _heldRemoteLoadCancellation = null;
                _heldRemoteRequestTask = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task CompleteHeldRemoteRefreshAsync(
        IReadOnlyDictionary<Guid, LocalHeldRow> localRows,
        IReadOnlySet<Guid> syntheticRemoteClaimHoldGuids,
        HeldOrderViewScope scope,
        long generation,
        Task<IReadOnlyList<SharedHeldOrderListItemDto>> remoteRequest)
    {
        try
        {
            var remoteRows = await remoteRequest;
            if (generation != _heldLoadGeneration)
            {
                return;
            }

            _heldRemoteRowsCache = remoteRows;
            _heldRemoteCacheReady = true;
            if (scope == HeldOrderViewScope.Other)
            {
                HeldOrdersRemoteStatusMessage = string.Empty;
            }

            var filteredRemoteRows = FilterRemoteHeldRows(
                remoteRows,
                scope,
                syntheticRemoteClaimHoldGuids);
            var merged = MergeHeldRows(localRows, filteredRemoteRows, remoteAuthoritative: true);
            ReplaceHeldOrdersIfCurrent(merged, generation);
        }
        catch (OperationCanceledException)
        {
            // 新筛选、切页或离开页面会主动取消；旧结果不得覆盖当前代次。
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation != _heldLoadGeneration)
            {
                return;
            }

            if (scope == HeldOrderViewScope.Other)
            {
                HeldOrdersRemoteStatusMessage = string.Format(
                    CultureInfo.CurrentCulture,
                    T("history.held.remoteUnavailable"),
                    ex.Message);
            }

            var retainedRemoteRows = _heldRemoteCacheReady
                ? FilterRemoteHeldRows(
                    _heldRemoteRowsCache,
                    scope,
                    syntheticRemoteClaimHoldGuids)
                : [];
            var retained = MergeHeldRows(localRows, retainedRemoteRows, remoteAuthoritative: false);
            ReplaceHeldOrdersIfCurrent(retained, generation);
        }
        finally
        {
            if (generation == _heldLoadGeneration)
            {
                IsHeldRemoteRefreshing = false;
            }
        }
    }

    private void ReplaceHeldOrdersIfCurrent(
        IReadOnlyList<HistoryOrderListItem> rows,
        long generation)
    {
        if (generation != _heldLoadGeneration)
        {
            return;
        }

        var selectedGuid = SelectedOrder?.OrderGuid;
        _suppressSelectedOrderLoad = true;
        Orders.ReplaceWith(rows);
        SelectedOrder = selectedGuid is { } preserved
            ? Orders.FirstOrDefault(order => order.OrderGuid == preserved)
            : Orders.FirstOrDefault();
        _suppressSelectedOrderLoad = false;
    }

    private IReadOnlyList<HistoryOrderListItem> MergeHeldRows(
        IReadOnlyDictionary<Guid, LocalHeldRow> localRows,
        IReadOnlyList<(Guid HoldGuid, SharedHeldOrderListItemDto Item)> remoteRows,
        bool remoteAuthoritative)
    {
        var merged = new List<HistoryOrderListItem>(localRows.Count + remoteRows.Count);
        var remoteHoldGuids = new HashSet<Guid>();
        foreach (var remote in remoteRows)
        {
            remoteHoldGuids.Add(remote.HoldGuid);
            if (localRows.TryGetValue(remote.HoldGuid, out var local))
            {
                merged.Add(BuildHeldOrderRow(
                    local.Order,
                    local.Publication,
                    local.ClaimStatus,
                    local.ClaimId,
                    remote.Item));
            }
            else
            {
                merged.Add(BuildHeldOrderRow(
                    null,
                    null,
                    null,
                    null,
                    remote.Item));
            }
        }

        foreach (var (holdGuid, local) in localRows)
        {
            if (remoteHoldGuids.Contains(holdGuid))
            {
                continue;
            }

            if (remoteAuthoritative &&
                local.Publication?.Status == HeldPublicationStatus.Published &&
                local.ClaimStatus is null)
            {
                // 服务端成功返回且不再含该 Published 挂单：已被 claim/完成/取消，在线列表收敛移除。
                continue;
            }

            merged.Add(BuildHeldOrderRow(
                local.Order,
                local.Publication,
                local.ClaimStatus,
                local.ClaimId,
                null));
        }

        return merged
            .OrderByDescending(order => order.OccurredAt)
            .ToList();
    }

    private async Task<LocalHeldLoadResult> LoadLocalHeldRowsAsync(
        HeldOrderViewScope scope,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, LocalHeldRow>();
        if (_suspendedOrderService is null)
        {
            return new LocalHeldLoadResult(result, new HashSet<Guid>());
        }

        // 本机先按当前 device 限定，避免其他终端占满 take；非本机查全店后再把
        // 当前 device 的 synthetic RemoteClaim 归入非本机。
        var deviceFilter = scope == HeldOrderViewScope.Local ? Session.DeviceCode : null;
        var summaries = await _suspendedOrderService.GetPendingOrdersAsync(
            Session.StoreCode,
            deviceFilter,
            NormalizeKeyword(SearchText),
            100,
            cancellationToken);
        var from = ParseDateFrom(DateFrom);
        var to = ParseDateTo(DateTo);
        var filtered = summaries
            .Where(order => from is null || order.SuspendedAt >= from.Value)
            .Where(order => to is null || order.SuspendedAt <= to.Value)
            .ToList();

        var publications = new Dictionary<Guid, SharedHeldOrderPublication?>();
        if (_sharedHeldOrderRepository is not null)
        {
            var publicationTasks = filtered.Select(async order =>
            {
                try
                {
                    return (order.SuspendedOrderGuid, await _sharedHeldOrderRepository
                        .GetPublicationAsync(order.SuspendedOrderGuid, cancellationToken));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return (order.SuspendedOrderGuid, (SharedHeldOrderPublication?)null);
                }
            }).ToArray();
            foreach (var pair in await Task.WhenAll(publicationTasks))
            {
                publications[pair.Item1] = pair.Item2;
            }
        }

        var claimsByHold = new Dictionary<Guid, SharedHeldOrderClaimRecovery>();
        if (_sharedHeldOrderRepository is not null)
        {
            var claims = await _sharedHeldOrderRepository.FindRecoverableClaimsAsync(
                Session.StoreCode,
                Session.DeviceCode,
                cancellationToken);
            claimsByHold = claims.ToDictionary(claim => claim.HoldGuid);
        }
        var syntheticRemoteClaimHoldGuids = claimsByHold.Values
            .Where(claim => claim.Source == SharedHeldOrderClaimSource.RemoteClaim)
            .Select(claim => claim.HoldGuid)
            .ToHashSet();

        foreach (var order in filtered)
        {
            var claim = claimsByHold.GetValueOrDefault(order.SuspendedOrderGuid);
            var isSyntheticRemoteClaim = claim?.Source == SharedHeldOrderClaimSource.RemoteClaim;
            var isCurrentDevice = string.Equals(
                order.DeviceCode,
                Session.DeviceCode,
                StringComparison.OrdinalIgnoreCase);
            var belongsToLocal = isCurrentDevice && !isSyntheticRemoteClaim;
            if (scope == HeldOrderViewScope.Local ? !belongsToLocal : belongsToLocal)
            {
                continue;
            }

            result[order.SuspendedOrderGuid] = new LocalHeldRow(
                order,
                publications.GetValueOrDefault(order.SuspendedOrderGuid),
                claim?.Status,
                claim?.ClaimId);
        }

        return new LocalHeldLoadResult(result, syntheticRemoteClaimHoldGuids);
    }

    private IReadOnlyList<(Guid HoldGuid, SharedHeldOrderListItemDto Item)> FilterRemoteHeldRows(
        IReadOnlyList<SharedHeldOrderListItemDto> rows,
        HeldOrderViewScope scope,
        IReadOnlySet<Guid> syntheticRemoteClaimHoldGuids)
    {
        var from = ParseDateFrom(DateFrom);
        var to = ParseDateTo(DateTo);
        var keyword = NormalizeKeyword(SearchText);
        return rows
            .Where(item => string.Equals(item.StoreCode, Session.StoreCode, StringComparison.OrdinalIgnoreCase))
            .Where(item =>
            {
                var isCurrentDevice = string.Equals(
                    item.DeviceCode,
                    Session.DeviceCode,
                    StringComparison.OrdinalIgnoreCase);
                // synthetic RemoteClaim 的来源优先级高于服务端 DeviceCode：即使服务端
                // 在状态收敛窗口仍返回同一行，也只能归入“非本机”，避免双页签重复。
                var belongsToLocal = isCurrentDevice &&
                    !syntheticRemoteClaimHoldGuids.Contains(item.HoldGuid);
                return scope == HeldOrderViewScope.Local ? belongsToLocal : !belongsToLocal;
            })
            .Where(item => from is null || item.HeldAtUtc >= from.Value)
            .Where(item => to is null || item.HeldAtUtc <= to.Value)
            .Where(item => keyword is null ||
                item.HeldByCashierName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.DeviceCode.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.HoldGuid, item))
            .ToList();
    }

    private HistoryOrderListItem BuildHeldOrderRow(
        SuspendedOrderSummary? local,
        SharedHeldOrderPublication? publication,
        HeldClaimStatus? claimStatus,
        Guid? claimId,
        SharedHeldOrderListItemDto? remote)
    {
        var holdGuid = remote?.HoldGuid ?? local!.SuspendedOrderGuid;
        var serverStatus = remote is null ? null : (HeldServerStatus?)HeldServerStatus.Pending;
        var badge = HeldOrderStatusResolver.Resolve(publication?.Status, serverStatus, claimStatus);
        var isDeletePending = publication is
        {
            Status: HeldPublicationStatus.Blocked,
            ErrorCode: "LOCAL_DELETE_PENDING_REMOTE" or "LOCAL_DELETE_PENDING_LOCAL"
        };
        var blockDetail = isDeletePending
            ? T("history.held.deletePending")
            : publication is null
            ? string.Empty
            : string.Join(
                " ",
                new[] { publication.ErrorCode, publication.ErrorMessage }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
        var hasLocalCopy = local is not null;
        var isThisDevice = string.Equals(
            remote?.DeviceCode ?? local!.DeviceCode,
            Session.DeviceCode,
            StringComparison.OrdinalIgnoreCase);
        var shareRequested = publication?.ShareRequestedAtIso is not null;
        // 仅本机真实 Pending 且未请求可共享：未请求、本机、无 claim/删除暂存/消费/已发布。
        var canShare = hasLocalCopy &&
            isThisDevice &&
            !isDeletePending &&
            claimStatus is null &&
            remote is null &&
            publication?.ConsumedAtIso is null &&
            publication?.ShareRequestedAtIso is null &&
            publication?.Status is HeldPublicationStatus.NeedsEvaluation or null;
        // 只有服务端确认（Published 或远端 Pending 可见）才显示“已共享”；
        // 请求落库到服务端确认之间均为“待共享”，评估阻断才是“无法共享”。
        var shareStatusLabel = canShare
            ? string.Empty
            : remote is not null || publication?.Status == HeldPublicationStatus.Published
                ? T("history.held.shared")
                : shareRequested && publication?.Status is
                    (HeldPublicationStatus.NeedsEvaluation or HeldPublicationStatus.PendingPublish)
                    ? T("history.held.sharePending")
                    : T("history.held.cannotShare");
        var canRemoteRecall = !isDeletePending &&
            serverStatus == HeldServerStatus.Pending &&
            claimStatus is null &&
            Session.IsOnline;
        var canOfflineRecall = !isDeletePending &&
            hasLocalCopy &&
            publication?.Status is HeldPublicationStatus.PendingPublish or HeldPublicationStatus.Published &&
            claimStatus is null;
        var canLegacyRecall = hasLocalCopy &&
            !isDeletePending &&
            !canRemoteRecall &&
            !canOfflineRecall &&
            (publication is null ||
             publication.Status is HeldPublicationStatus.NeedsEvaluation or HeldPublicationStatus.Blocked);
        var canDeleteHeldOrder = hasLocalCopy &&
            string.Equals(local!.StoreCode, Session.StoreCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(local.DeviceCode, Session.DeviceCode, StringComparison.OrdinalIgnoreCase) &&
            publication?.ConsumedAtIso is null &&
            claimStatus is null;

        return new HistoryOrderListItem(
            holdGuid,
            TransactionHistorySource.HeldOrders,
            remote?.StoreCode ?? local!.StoreCode,
            remote?.DeviceCode ?? local!.DeviceCode,
            remote?.HeldByCashierName ?? local!.CashierName,
            remote?.HeldAtUtc ?? local!.SuspendedAt,
            ToMoney(remote?.TotalCents) ?? local!.TotalAmount,
            ToMoney(remote?.DiscountCents) ?? local!.DiscountAmount,
            ToMoney(remote?.ActualCents) ?? local!.ActualAmount,
            remote?.LineCount ?? local!.LineCount,
            T("history.payment.suspended"),
            BuildHeldStatusLabel(badge, blockDetail),
            IsSuspendedOrder: hasLocalCopy,
            CanRecall: canRemoteRecall || canOfflineRecall || canLegacyRecall,
            DisplayCulture: CurrentDisplayCulture,
            IsHeldOrder: true,
            HeldBadgeKind: badge,
            HeldStatusDetail: blockDetail,
            HeldClaimId: claimId,
            CanForceRelease: claimStatus is HeldClaimStatus.Prepared or HeldClaimStatus.Active,
            CanRemoteRecall: canRemoteRecall,
            CanOfflineRecall: canOfflineRecall,
            CanLegacyRecall: canLegacyRecall,
            CanDeleteHeldOrder: canDeleteHeldOrder,
            CanShare: canShare,
            ShareStatusLabel: shareStatusLabel);
    }

    private static decimal? ToMoney(long? cents)
    {
        return cents is null ? null : cents.Value / 100m;
    }

    private string BuildHeldStatusLabel(HeldOrderBadgeKind kind, string detail)
    {
        var label = kind switch
        {
            HeldOrderBadgeKind.LocalPendingPublish => T("history.held.pendingPublish"),
            HeldOrderBadgeKind.Published => T("history.held.published"),
            HeldOrderBadgeKind.RemotePending => T("history.held.remotePending"),
            HeldOrderBadgeKind.LocalClaimPrepared => T("history.held.claimPrepared"),
            HeldOrderBadgeKind.LocalClaimActive => T("history.held.claimActive"),
            HeldOrderBadgeKind.ClaimedByOther => T("history.held.claimedByOther"),
            HeldOrderBadgeKind.Completed => T("history.held.completed"),
            HeldOrderBadgeKind.Blocked => T("history.held.blocked"),
            _ => T("history.held.localHold")
        };
        return string.IsNullOrWhiteSpace(detail) ? label : $"{label}: {detail}";
    }

    private async Task<IReadOnlyList<HistoryOrderListItem>> LoadRemoteOrdersAsync(CancellationToken cancellationToken)
    {
        if (_remoteOrderHistoryService is null)
        {
            return [];
        }

        var result = await _remoteOrderHistoryService.QueryAsync(
            new RemoteOrderHistoryQuery(
                Session.StoreCode,
                ParseDateFrom(DateFrom),
                ParseDateTo(DateTo),
                SelectedTerminalDeviceCode,
                NormalizeKeyword(SearchText),
                100),
            cancellationToken);
        return result.Orders.Select(order => new HistoryOrderListItem(
            order.OrderGuid,
            TransactionHistorySource.RemoteOrders,
            order.StoreCode,
            order.DeviceCode,
            order.CashierName,
            order.SoldAt,
            order.TotalAmount,
            order.DiscountAmount,
            order.ActualAmount,
            order.LineCount,
            order.PaymentSummary,
            order.StatusLabel,
            DisplayCulture: CurrentDisplayCulture)).ToList();
    }

    private async Task<IReadOnlyList<HistoryOrderListItem>> LoadInstallmentOrdersAsync(CancellationToken cancellationToken)
    {
        _isInstallmentRecoveryStateUnknown = true;
        ConfirmInstallmentPickupCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsConfirmInstallmentPickupVisible));
        var from = ParseDateFrom(DateFrom);
        var to = ParseDateTo(DateTo);
        var historyTask = _installmentOrderService.QueryHistoryAsync(
            Session,
            new InstallmentHistorySearchQuery(
                UpdatedFrom: from,
                UpdatedTo: to,
                DeviceCode: SelectedTerminalDeviceCode,
                Keyword: NormalizeKeyword(SearchText),
                Take: 100),
            cancellationToken);
        var recoveryLockTask = LoadInstallmentRecoveryLocksWithinDeadlineAsync(cancellationToken);
        await Task.WhenAll(historyTask, recoveryLockTask);
        var orders = await historyTask;
        var recoveryLockState = await recoveryLockTask;
        _lockedInstallmentGuids.Clear();
        if (recoveryLockState.IsKnown)
        {
            _lockedInstallmentGuids.UnionWith(recoveryLockState.LockedInstallments);
        }

        // 恢复锁查询失败或超时必须保持未知状态，列表仍可展示，但提货继续禁用。
        _isInstallmentRecoveryStateUnknown = !recoveryLockState.IsKnown;
        ConfirmInstallmentPickupCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsConfirmInstallmentPickupVisible));
        return orders
            .Where(order => SelectedTerminalDeviceCode is null ||
                string.Equals(order.DeviceCode, SelectedTerminalDeviceCode, StringComparison.OrdinalIgnoreCase))
            .Where(order => from is null || order.UpdatedAt >= from.Value)
            .Where(order => to is null || order.UpdatedAt <= to.Value)
            .OrderByDescending(order => order.UpdatedAt)
            .Select(order => new HistoryOrderListItem(
                order.OrderId,
                TransactionHistorySource.InstallmentOrders,
                Session.StoreCode,
                order.DeviceCode,
                order.CustomerName,
                order.UpdatedAt,
                order.TotalAmount,
                0m,
                order.OutstandingAmount,
                0,
                FormatMoney(order.PaidAmount),
                LocalizeInstallmentStatus(order.Status),
                InstallmentOrder: order,
                IsInstallmentOrder: true,
                CanContinueInstallmentPayment: order.CanAddRepayment,
                CanConfirmInstallmentPickup: order.CanConfirmPickup && !_lockedInstallmentGuids.Contains(order.OrderId),
                CustomerPhone: order.CustomerPhone,
                DisplayCulture: CurrentDisplayCulture))
            .ToList();
    }

    private async Task<(bool IsKnown, IReadOnlySet<Guid> LockedInstallments)> LoadInstallmentRecoveryLocksWithinDeadlineAsync(
        CancellationToken cancellationToken)
    {
        using var recoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        recoveryTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var locked = await _installmentOrderService.GetLockedInstallmentGuidsAsync(Session, recoveryTimeout.Token);
            return (true, locked);
        }
        catch (Exception ex) when (
            ex is not OutOfMemoryException &&
            ex is not StackOverflowException &&
            (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            return (false, new HashSet<Guid>());
        }
    }

    private async Task LoadSelectedReceiptAsync(
        HistoryOrderListItem? selectedOrder,
        long generation,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentSelectedReceiptLoad(selectedOrder, generation))
        {
            return;
        }

        if (selectedOrder is null)
        {
            ClearReceiptPreview();
            return;
        }

        // 远程独有挂单没有本地明细快照；仅选中行也不得触发本地小票读取或远程 Claim。
        if (selectedOrder.IsHeldOrder && !selectedOrder.IsSuspendedOrder)
        {
            ClearReceiptPreview();
            return;
        }

        // 切换订单后先清空旧小票，避免弹窗加载期间短暂显示上一笔交易。
        ClearReceiptPreview();

        // 远程订单必须使用当前已加载的小票直接补打；切换订单时先使旧详情失效，避免打印错单。
        if (selectedOrder.IsInstallmentOrder)
        {
            // 分期历史使用本地快照映射正式小票；详情完整时同一对象同时供预览和补打使用。
            var installmentDetails = await LoadInstallmentPreviewDetailsAsync(selectedOrder.OrderGuid, cancellationToken);
            if (!IsCurrentSelectedReceiptLoad(selectedOrder, generation))
            {
                return;
            }

            if (installmentDetails is not null)
            {
                // 中文注释：有本地分期快照时复用正式小票映射，小票弹窗才能显示正常抬头和提货信息。
                var installmentReceipt = InstallmentReceiptMapper.CreateReceipt(installmentDetails);
                var previewSettings = await LoadPreviewSettingsAsync(cancellationToken);
                if (!IsCurrentSelectedReceiptLoad(selectedOrder, generation))
                {
                    return;
                }

                // 权威分期详情的商品、付款、状态和金额必须作为同一快照发布，不能混用列表摘要。
                _selectedInstallmentDetails = installmentDetails;
                SelectedReceipt = installmentReceipt;
                ReceiptLines.ReplaceWith(installmentReceipt.Lines);
                Payments.ReplaceWith(installmentReceipt.Payments);
                ReceiptPreviewRows.ReplaceWith(BuildPreviewRows(
                    installmentReceipt,
                    previewSettings));
                PreviewSubtotal = installmentReceipt.TotalAmount;
                PreviewDiscount = installmentReceipt.DiscountAmount;
                PreviewTotal = installmentReceipt.ActualAmount;
                PreviewOrderId = installmentReceipt.TransactionIdDisplay;
                PreviewSoldAt = installmentReceipt.SoldAt.ToLocalTime().ToString(
                    "MMM dd, yyyy HH:mm",
                    CurrentDisplayCulture);
                return;
            }

            if (Session.IsOnline)
            {
                // 在线详情失败或不存在时必须 fail closed，禁止用本地旧快照或 summary 金融字段补打。
                ClearReceiptPreview();
                SetOrderDetailsUnavailable();
                return;
            }

            ReceiptLines.Clear();
            Payments.Clear();
            ReceiptPreviewRows.ReplaceWith(BuildInstallmentPreviewRows(selectedOrder, installmentDetails));
            PreviewSubtotal = selectedOrder.TotalAmount;
            PreviewDiscount = selectedOrder.InstallmentOrder?.PaidAmount ?? 0m;
            PreviewTotal = selectedOrder.ActualAmount;
            PreviewOrderId = selectedOrder.DisplayOrderId;
            PreviewSoldAt = selectedOrder.SoldAtDisplay;
            SetOrderDetailsUnavailable();
            return;
        }

        ReceiptDetails? receipt = selectedOrder.IsSuspendedOrder
            ? await GetSuspendedReceiptAsync(selectedOrder.OrderGuid, cancellationToken)
            : selectedOrder.Source switch
            {
                TransactionHistorySource.RemoteOrders => _remoteOrderHistoryService is null
                    ? null
                    : await _remoteOrderHistoryService.GetDetailsAsync(selectedOrder.OrderGuid, cancellationToken),
                _ => _receiptQueryService is null ? null : await _receiptQueryService.GetReceiptAsync(selectedOrder.OrderGuid, cancellationToken)
            };

        if (!IsCurrentSelectedReceiptLoad(selectedOrder, generation))
        {
            return;
        }

        if (receipt is null)
        {
            ClearReceiptPreview();
            SetOrderDetailsUnavailable();
            return;
        }

        var receiptPreviewSettings = await LoadPreviewSettingsAsync(cancellationToken);
        if (!IsCurrentSelectedReceiptLoad(selectedOrder, generation))
        {
            return;
        }

        SelectedReceipt = receipt;
        ReceiptLines.ReplaceWith(receipt.Lines);
        Payments.ReplaceWith(receipt.Payments);
        ReceiptPreviewRows.ReplaceWith(BuildPreviewRows(
            receipt,
            receiptPreviewSettings));
        PreviewSubtotal = receipt.TotalAmount;
        PreviewDiscount = receipt.DiscountAmount;
        PreviewTotal = receipt.ActualAmount;
        PreviewOrderId = receipt.TransactionIdDisplay;
        PreviewSoldAt = receipt.SoldAt.ToLocalTime().ToString(
            "MMM dd, yyyy HH:mm",
            CurrentDisplayCulture);
    }

    private async Task<ReceiptDetails?> GetSuspendedReceiptAsync(Guid orderGuid, CancellationToken cancellationToken)
    {
        if (_suspendedOrderService is null)
        {
            return null;
        }

        var details = await _suspendedOrderService.GetOrderAsync(orderGuid, cancellationToken);
        return details is null ? null : CreateSuspendedReceipt(details);
    }

    private bool CanRecallSelected()
    {
        return CanRecallOrder(SelectedOrder);
    }

    private bool CanRecallOrder(HistoryOrderListItem? order)
    {
        return order?.CanRecall == true;
    }

    private bool CanContinueInstallmentPayment(HistoryOrderListItem? order)
    {
        return _continueInstallmentPaymentAsync is not null &&
            order?.InstallmentOrder is not null &&
            order.CanContinueInstallmentPayment;
    }

    private bool CanConfirmInstallmentPickup(HistoryOrderListItem? order)
    {
        return Session.IsOnline &&
            !_isInstallmentRecoveryStateUnknown &&
            order?.InstallmentOrder is not null &&
            !_lockedInstallmentGuids.Contains(order.InstallmentOrder.OrderId) &&
            order.CanConfirmInstallmentPickup;
    }

    private bool CanReprintSelected()
    {
        if (SelectedOrder is not { } selectedOrder || selectedOrder.Source != SelectedSource)
        {
            return false;
        }

        return selectedOrder switch
        {
            { IsSuspendedOrder: false, Source: TransactionHistorySource.LocalOrders } => true,
            { IsSuspendedOrder: false, Source: TransactionHistorySource.RemoteOrders } remoteOrder =>
                SelectedReceipt?.OrderGuid == remoteOrder.OrderGuid,
            { IsSuspendedOrder: false, Source: TransactionHistorySource.InstallmentOrders } installmentOrder =>
                SelectedReceipt?.OrderGuid == installmentOrder.OrderGuid,
            _ => false
        };
    }

    private async Task RecallSelectedAsync()
    {
        await RecallOrderAsync(SelectedOrder);
    }

    private async Task RecallOrderAsync(HistoryOrderListItem? order)
    {
        var orderSnapshot = order;
        using var authorization = await AuthorizeAsync(Permissions.PosTerminal.History.Recall, "recall-order");
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        if (orderSnapshot?.IsHeldOrder == true)
        {
            await RecallHeldOrderCoreAsync(orderSnapshot);
            return;
        }

        if (!CanRecallOrder(orderSnapshot) || _suspendedOrderService is null)
        {
            return;
        }

        var correlation = OperationAuditEvents.CreateCorrelation();
        var recallCompleted = false;
        try
        {
            var recalledOrder = await _suspendedOrderService.RecallOrderAsync(orderSnapshot!.OrderGuid);
            OperationAuditEvents.RecordCartChange(
                _operationAuditLogger,
                OperationAuditTypes.OrderRecall,
                Session,
                new OperationAuditCartSnapshot(0m, 0m, 0m, []),
                OperationAuditEvents.CaptureSuspendedOrder(recalledOrder),
                reasonCode: "SUSPENDED_ORDER",
                orderGuid: orderSnapshot.OrderGuid.ToString("D"),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            recallCompleted = true;
            if (_onSuspendedOrderRecalledAsync is not null)
            {
                await _onSuspendedOrderRecalledAsync();
            }

            await LoadAsync();
        }
        catch (Exception ex)
        {
            if (!recallCompleted)
            {
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.OrderRecall,
                    "Failed",
                    Session,
                    reasonCode: "SUSPENDED_ORDER",
                    safeMessage: ex.GetType().Name,
                    orderGuid: orderSnapshot?.OrderGuid.ToString("D"),
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
            }

            ConsoleLog.WriteError(
                "OperationAudit",
                $"order recall failed error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            StatusMessage = ex.Message;
        }
    }

    /// <summary>
    /// 共享挂单取单路由：服务端 Pending 且在线 -> ISharedHeldOrderCoordinator 远端取单；
    /// 本地待发布/已发布副本 -> 原设备本地/离线 recall；无发布状态的本地挂单 ->
    /// 既有本地挂单召回。成功统一走 _onSuspendedOrderRecalledAsync 刷新购物车并返回 POS。
    /// </summary>
    private async Task RecallHeldOrderCoreAsync(HistoryOrderListItem order)
    {
        var correlation = OperationAuditEvents.CreateCorrelation();
        var recallAuditRecorded = false;
        try
        {
            if (order.CanRemoteRecall && Session.IsOnline)
            {
                if (_sharedHeldOrderCoordinator is null)
                {
                    throw new InvalidOperationException(T("history.held.unavailable"));
                }

                var takeResult = await _sharedHeldOrderCoordinator.TakeRemoteHoldAsync(
                    order.OrderGuid,
                    Session);
                RecordSharedHeldOrderRecall(order, takeResult, "SHARED_HELD_ORDER", correlation);
                recallAuditRecorded = true;
            }
            else if (order.CanOfflineRecall)
            {
                if (_sharedHeldOrderCoordinator is null)
                {
                    throw new InvalidOperationException(T("history.held.unavailable"));
                }

                var takeResult = await _sharedHeldOrderCoordinator.RecallLocalPublicationAsync(
                    order.OrderGuid,
                    Session);
                RecordSharedHeldOrderRecall(order, takeResult, "SHARED_HELD_ORDER_OFFLINE", correlation);
                recallAuditRecorded = true;
            }
            else if (order.CanLegacyRecall)
            {
                if (_suspendedOrderService is null)
                {
                    return;
                }

                var recalledOrder = await _suspendedOrderService.RecallOrderAsync(order.OrderGuid);
                OperationAuditEvents.RecordCartChange(
                    _operationAuditLogger,
                    OperationAuditTypes.OrderRecall,
                    Session,
                    new OperationAuditCartSnapshot(0m, 0m, 0m, []),
                    OperationAuditEvents.CaptureSuspendedOrder(recalledOrder),
                    reasonCode: "SUSPENDED_ORDER",
                    orderGuid: order.OrderGuid.ToString("D"),
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
                recallAuditRecorded = true;
            }
            else
            {
                return;
            }

            if (_onSuspendedOrderRecalledAsync is not null)
            {
                await _onSuspendedOrderRecalledAsync();
            }
        }
        catch (Exception ex)
        {
            // 此入口未接收调用方取消令牌；取消异常只能来自内部超时，必须转为可见失败状态。
            // 取单已记审计后，刷新购物车的回调异常不能再把同一次取单记成失败。
            if (!recallAuditRecorded)
            {
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.OrderRecall,
                    "Failed",
                    Session,
                    reasonCode: "SHARED_HELD_ORDER",
                    safeMessage: ex.GetType().Name,
                    orderGuid: order.OrderGuid.ToString("D"),
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
            }
            ConsoleLog.WriteError(
                "OperationAudit",
                $"held order recall failed error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            StatusMessage = ex.Message;
        }
    }

    private bool CanDeleteHeldOrder(HistoryOrderListItem? order)
    {
        return order?.CanDeleteHeldOrder == true;
    }

    /// <summary>
    /// 与 iPad 对齐的两阶段删除：先在本地事务中阻断发布；若已经或可能发布到服务端，
    /// 再在线取消；最后才把本地挂单标记为 Canceled。任一步失败都保留暂存状态供重试。
    /// </summary>
    private async Task DeleteHeldOrderAsync(HistoryOrderListItem? order)
    {
        if (!CanDeleteHeldOrder(order))
        {
            return;
        }

        var candidate = order!;

        using var authorization = await AuthorizeAsync(
            Permissions.PosTerminal.History.Recall,
            "delete-held-order");
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        var confirmationDialogService = _confirmationDialogService;
        if (confirmationDialogService is null ||
            !await ShowConfirmationDialogAsync(confirmationDialogService.ConfirmHeldOrderCancellationAsync))
        {
            return;
        }

        if (_sharedHeldOrderRepository is null)
        {
            StatusMessage = T("history.held.unavailable");
            return;
        }

        var correlation = OperationAuditEvents.CreateCorrelation();
        var deleteStaged = false;
        try
        {
            var timestamp = _timeProvider
                .GetUtcNow()
                .ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            var staged = await _sharedHeldOrderRepository.TryStageDeletePendingAsync(
                candidate.OrderGuid,
                Session.StoreCode,
                Session.DeviceCode,
                timestamp,
                CancellationToken.None);
            if (staged is null)
            {
                throw new InvalidOperationException(T("history.held.deleteFailed"));
            }
            deleteStaged = true;

            if (staged.RemoteCancellationRequired)
            {
                if (!Session.IsOnline || _sharedHeldOrderApiClient is null)
                {
                    throw new InvalidOperationException(T("history.held.deleteOnlineRequired"));
                }

                var cancelled = await CancelRemoteHeldOrderWithNotFoundCompensationAsync(
                    candidate.OrderGuid,
                    CancellationToken.None);
                if (cancelled.HoldGuid != candidate.OrderGuid ||
                    cancelled.Status != HeldServerStatus.Cancelled)
                {
                    throw new InvalidOperationException(T("history.held.deleteFailed"));
                }
            }

            if (!await _sharedHeldOrderRepository.TryCompleteDeletePendingAsync(
                    candidate.OrderGuid,
                    Session.StoreCode,
                    Session.DeviceCode,
                    timestamp,
                    CancellationToken.None))
            {
                throw new InvalidOperationException(T("history.held.deleteFailed"));
            }

            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.OrderCancel,
                "Succeeded",
                Session,
                reasonCode: "SHARED_HELD_ORDER",
                orderGuid: candidate.OrderGuid.ToString("D"),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            await LoadAsync();
            StatusMessage = T("history.held.deleted");
        }
        catch (Exception ex)
        {
            // 删除流程固定使用 CancellationToken.None；HTTP 超时也必须保留暂存状态并允许重试。
            var failureMessage = ex.Message;
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.OrderCancel,
                "Failed",
                Session,
                reasonCode: "SHARED_HELD_ORDER",
                safeMessage: ex.GetType().Name,
                orderGuid: candidate?.OrderGuid.ToString("D"),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            if (deleteStaged)
            {
                // 暂存已把 publication 置为 Blocked；立即重读，禁止失败窗口内继续取回。
                await LoadAsync();
            }
            StatusMessage = failureMessage;
        }
    }

    private async Task<SharedHeldOrderCancelResponse> CancelRemoteHeldOrderWithNotFoundCompensationAsync(
        Guid holdGuid,
        CancellationToken cancellationToken)
    {
        var apiClient = _sharedHeldOrderApiClient
            ?? throw new InvalidOperationException(T("history.held.unavailable"));
        var repository = _sharedHeldOrderRepository
            ?? throw new InvalidOperationException(T("history.held.unavailable"));
        try
        {
            return await apiClient.CancelAsync(holdGuid, cancellationToken);
        }
        catch (SharedHeldOrderApiException exception) when (
            exception.StatusCode == HttpStatusCode.NotFound ||
            string.Equals(
                exception.ErrorCode,
                "SHARED_HELD_ORDER_NOT_FOUND",
                StringComparison.Ordinal))
        {
            var payload = await repository.GetPublicationPayloadAsync(
                holdGuid,
                cancellationToken);
            if (payload is null)
            {
                throw;
            }

            // 发布可能尚未到达服务端或响应丢失。用同一 HoldGuid/幂等键补建
            // 权威事实，再取消为稳定终态；迟到 publish 只能重放该 Cancelled 行。
            var published = await apiClient.PublishAsync(
                new SharedHeldOrderPublishRequest(
                    holdGuid,
                    Session.StoreCode,
                    Session.DeviceCode,
                    SharedHeldOrderContractMapper.ToContract(payload, payload.Version),
                    holdGuid.ToString("D")),
                cancellationToken);
            if (published.HoldGuid != holdGuid)
            {
                throw new InvalidOperationException(T("history.held.deleteFailed"));
            }
            if (published.Status == HeldServerStatus.Cancelled)
            {
                return new SharedHeldOrderCancelResponse(
                    holdGuid,
                    HeldServerStatus.Cancelled,
                    published.Revision,
                    published.CreatedAtUtc,
                    AlreadyCancelled: true);
            }
            if (published.Status != HeldServerStatus.Pending)
            {
                throw new InvalidOperationException(T("history.held.deleteFailed"));
            }

            return await apiClient.CancelAsync(holdGuid, cancellationToken);
        }
    }

    private bool CanShareHeldOrder(HistoryOrderListItem? order)
    {
        return order?.CanShare == true && order.Share.IsBusy == false;
    }

    /// <summary>
    /// 显式一次性共享：先幂等持久化请求时间（本机真实 Pending 且未请求），成功后立即
    /// 调用后台 worker 发布一轮；单轮失败只记状态，hosted service 会继续重试。
    /// </summary>
    private async Task ShareHeldOrderAsync(HistoryOrderListItem? order)
    {
        var candidate = order;
        if (!CanShareHeldOrder(candidate) || _sharedHeldOrderRepository is null)
        {
            return;
        }

        if (candidate is null)
        {
            return;
        }

        using var authorization = await AuthorizeAsync(
            Permissions.PosTerminal.History.Recall,
            "share-held-order");
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        candidate.Share.IsBusy = true;
        ShareHeldOrderCommand.NotifyCanExecuteChanged();
        try
        {
            var requestedAtIso = _timeProvider
                .GetUtcNow()
                .ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            var result = await _sharedHeldOrderRepository.TryRequestShareAsync(
                candidate.OrderGuid,
                Session.StoreCode,
                Session.DeviceCode,
                requestedAtIso,
                CancellationToken.None);
            if (result is SharedHeldOrderShareRequestResult.Requested
                or SharedHeldOrderShareRequestResult.AlreadyRequested)
            {
                // 先把本行立即切到“待共享”；发布网络调用在后台运行，不延长按钮 busy。
                MarkShareRequested(candidate);
                LastSharePublicationTask = RunShareWorkerAndRefreshAsync();
            }
            else
            {
                StatusMessage = T("history.held.shareIneligible");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            candidate.Share.IsBusy = false;
            ShareHeldOrderCommand.NotifyCanExecuteChanged();
        }
    }

    private void MarkShareRequested(HistoryOrderListItem candidate)
    {
        var index = Orders
            .Select((row, rowIndex) => (row, rowIndex))
            .FirstOrDefault(pair => pair.row.OrderGuid == candidate.OrderGuid)
            .rowIndex;
        if (index < 0 || index >= Orders.Count || Orders[index].OrderGuid != candidate.OrderGuid)
        {
            return;
        }

        var updated = Orders[index] with
        {
            CanShare = false,
            ShareStatusLabel = T("history.held.sharePending")
        };
        _suppressSelectedOrderLoad = true;
        Orders[index] = updated;
        if (SelectedOrder?.OrderGuid == candidate.OrderGuid)
        {
            SelectedOrder = updated;
        }
        _suppressSelectedOrderLoad = false;
        ShareHeldOrderCommand.NotifyCanExecuteChanged();
    }

    internal Task? LastSharePublicationTask { get; private set; }

    private async Task RunShareWorkerAndRefreshAsync()
    {
        try
        {
            if (_sharedHeldOrderPublicationWorker is not null)
            {
                await _sharedHeldOrderPublicationWorker.RunOnceAsync(
                    Session.StoreCode,
                    Session.DeviceCode,
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // 请求已持久化：worker 单轮失败不影响共享意图，hosted service 会重试。
            StatusMessage = ex.Message;
        }

        if (!_disposed && SelectedSource == TransactionHistorySource.HeldOrders)
        {
            await LoadAsync();
        }
    }

    private bool CanForceReleaseOrder(HistoryOrderListItem? order)
    {
        return order?.CanForceRelease == true && !IsForceReleaseReasonPromptOpen;
    }

    private void RequestForceRelease(HistoryOrderListItem? order)
    {
        if (!CanForceReleaseOrder(order))
        {
            return;
        }

        ForceReleaseCandidate = order;
        ForceReleaseReason = string.Empty;
        StatusMessage = string.Empty;
        IsForceReleaseReasonPromptOpen = true;
    }

    private bool CanConfirmForceRelease()
    {
        return IsForceReleaseReasonPromptOpen &&
            ForceReleaseCandidate?.CanForceRelease == true &&
            !string.IsNullOrWhiteSpace(ForceReleaseReason);
    }

    private bool CanCancelForceRelease()
    {
        return IsForceReleaseReasonPromptOpen;
    }

    private void CancelForceRelease()
    {
        IsForceReleaseReasonPromptOpen = false;
        ForceReleaseCandidate = null;
        ForceReleaseReason = string.Empty;
    }

    partial void OnForceReleaseReasonChanged(string value)
    {
        ConfirmForceReleaseCommand?.NotifyCanExecuteChanged();
    }

    partial void OnIsForceReleaseReasonPromptOpenChanged(bool value)
    {
        ForceReleaseHeldOrderCommand?.NotifyCanExecuteChanged();
        ConfirmForceReleaseCommand?.NotifyCanExecuteChanged();
        CancelForceReleaseCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 主管强制释放：复用 History.Recall 授权；原因必须非空（且不超过 500 字符），
    /// 校验通过后调用 coordinator 的 durable 方法（服务端 force-release 成功后才
    /// 清理本地 cart binding/fence/claim；失败保留可重试状态）。
    /// </summary>
    private async Task ConfirmForceReleaseAsync()
    {
        var candidate = ForceReleaseCandidate;
        if (candidate?.HeldClaimId is not Guid claimGuid)
        {
            CancelForceRelease();
            return;
        }

        var reason = ForceReleaseReason?.Trim() ?? string.Empty;
        if (reason.Length == 0)
        {
            StatusMessage = T("history.held.forceReleaseReasonRequired");
            return;
        }

        if (reason.Length > 500)
        {
            StatusMessage = T("history.held.forceReleaseReasonTooLong");
            return;
        }

        IsForceReleaseReasonPromptOpen = false;
        ForceReleaseCandidate = null;

        if (_sharedHeldOrderCoordinator is null)
        {
            StatusMessage = T("history.held.unavailable");
            ForceReleaseReason = string.Empty;
            return;
        }

        using var authorization = await AuthorizeAsync(
            Permissions.PosTerminal.History.Recall,
            "force-release-held-order");
        if (authorization is null)
        {
            ForceReleaseReason = string.Empty;
            return;
        }
        using var authorizationActivation = authorization.Activate();

        var correlation = OperationAuditEvents.CreateCorrelation();
        var releaseAuditRecorded = false;
        try
        {
            await _sharedHeldOrderCoordinator.ForceReleaseAsync(
                candidate.OrderGuid,
                claimGuid,
                reason,
                Session,
                CancellationToken.None);
            // 强制释放把被其他设备取走的挂单退回挂单池，按“挂单”记录并保留主管填写的原因。
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.OrderHold,
                "Succeeded",
                Session,
                reasonCode: "FORCE_RELEASE",
                safeMessage: reason,
                orderGuid: candidate.OrderGuid.ToString("D"),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            releaseAuditRecorded = true;
            await LoadAsync();
            // 刷新失败时保留具体错误；刷新成功后再显示强制释放结果，避免成功提示被 LoadAsync 清空。
            if (string.IsNullOrWhiteSpace(StatusMessage))
            {
                StatusMessage = T("history.held.forceReleased");
            }
        }
        catch (Exception ex)
        {
            // 强制释放使用 CancellationToken.None；内部 HTTP 超时应留在可重试状态，不能逃逸到 Dispatcher。
            if (!releaseAuditRecorded)
            {
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.OrderHold,
                    "Failed",
                    Session,
                    reasonCode: "FORCE_RELEASE",
                    safeMessage: $"{ex.GetType().Name}: {reason}",
                    orderGuid: candidate.OrderGuid.ToString("D"),
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
            }

            StatusMessage = ex.Message;
        }
        finally
        {
            ForceReleaseReason = string.Empty;
        }
    }

    /// <summary>
    /// 共享挂单取单审计：取单已在本地/服务端落定，但购物车恢复失败时记为失败，便于追查“取走却没进购物车”的挂单。
    /// 成功时与本地挂单取单口径一致，按“空购物车 → 挂单金额”记录金额变化（列表行只有整单金额，无商品明细）。
    /// </summary>
    private void RecordSharedHeldOrderRecall(
        HistoryOrderListItem order,
        SharedHeldOrderTakeResult takeResult,
        string reasonCode,
        (string CorrelationId, string TraceId) correlation)
    {
        if (!takeResult.RestoredToCart)
        {
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.OrderRecall,
                "Failed",
                Session,
                reasonCode: reasonCode,
                safeMessage: "CART_RESTORE_FAILED",
                orderGuid: order.OrderGuid.ToString("D"),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            return;
        }

        OperationAuditEvents.RecordCartChange(
            _operationAuditLogger,
            OperationAuditTypes.OrderRecall,
            Session,
            new OperationAuditCartSnapshot(0m, 0m, 0m, []),
            new OperationAuditCartSnapshot(order.TotalAmount, order.DiscountAmount, order.ActualAmount, []),
            reasonCode: reasonCode,
            orderGuid: order.OrderGuid.ToString("D"),
            correlationId: correlation.CorrelationId,
            traceId: correlation.TraceId);
    }

    private async Task<LocalInstallmentOrder?> LoadInstallmentPreviewDetailsAsync(Guid installmentGuid, CancellationToken cancellationToken)
    {
        return await _installmentOrderService.GetOrderDetailsAsync(Session, installmentGuid, cancellationToken);
    }

    private IReadOnlyList<ReceiptPreviewRow> BuildInstallmentPreviewRows(HistoryOrderListItem order, LocalInstallmentOrder? details)
    {
        var summary = order.InstallmentOrder;
        var totalAmount = details?.TotalAmount ?? summary?.TotalAmount ?? order.TotalAmount;
        var paidAmount = details?.PaidAmount ?? summary?.PaidAmount ?? 0m;
        var outstandingAmount = details?.BalanceAmount ?? summary?.OutstandingAmount ?? order.ActualAmount;
        var customerName = details?.CustomerName ?? summary?.CustomerName ?? order.CashierName;
        var customerPhone = details?.CustomerPhone ?? summary?.CustomerPhone ?? order.CustomerPhone;
        var status = summary?.Status ?? order.StatusLabel;

        var rows = new List<ReceiptPreviewRow>
        {
            new(ReceiptPreviewRowKind.Text, "===== TAX INVOICE =====", ReceiptPrintAlignment.Center, true),
            new(ReceiptPreviewRowKind.Text, FitPreviewColumns(T("installment.center.column.orderNumber"), order.DisplayOrderId), ReceiptPrintAlignment.Left, true),
            new(ReceiptPreviewRowKind.Text, $"{T("Customer")}: {customerName}"),
            new(ReceiptPreviewRowKind.Text, $"{T("Phone")}: {customerPhone}"),
            new(ReceiptPreviewRowKind.Separator, new string('-', 42))
        };

        if (details?.Lines.Count > 0)
        {
            foreach (var line in details.Lines)
            {
                rows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, FitPreviewColumns(line.DisplayName, FormatMoney(line.ActualAmount))));
                rows.Add(new ReceiptPreviewRow(
                    ReceiptPreviewRowKind.Text,
                    $"  {line.Quantity.ToString("0.##", CultureInfo.InvariantCulture)} x {FormatMoney(line.UnitPrice)}"));
            }

            rows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Separator, new string('-', 42)));
        }

        rows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, FitPreviewColumns(T("Total"), FormatMoney(totalAmount)), ReceiptPrintAlignment.Left, true));
        rows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, FitPreviewColumns(T("history.installment.paid"), FormatMoney(paidAmount)), ReceiptPrintAlignment.Left, true));
        rows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, FitPreviewColumns(T("payment.installment.outstanding"), FormatMoney(outstandingAmount)), ReceiptPrintAlignment.Left, true));
        rows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, FitPreviewColumns(T("common.status"), status)));

        var recordedPayments = details?.Payments
            .Where(payment => payment.Status == InstallmentPaymentStatus.Recorded)
            .ToList();
        if (recordedPayments is { Count: > 0 })
        {
            rows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Separator, new string('-', 42)));
            rows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, "Payments", ReceiptPrintAlignment.Center, true));
            foreach (var payment in recordedPayments)
            {
                rows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, FitPreviewColumns(GetPaymentMethodLabel(payment.Method), FormatMoney(payment.Amount))));
            }
        }

        var orderGuid = order.OrderGuid.ToString("D");
        // 分期本地快照缺失时仍保留完整订单号二维码，确保历史预览可直接扫码退货。
        rows.Add(new ReceiptPreviewRow(
            ReceiptPreviewRowKind.QrCode,
            $"QR {orderGuid}",
            ReceiptPrintAlignment.Center)
        {
            QrCodeValue = orderGuid
        });

        return rows;
    }

    private async Task ContinueInstallmentPaymentAsync(HistoryOrderListItem? order)
    {
        if (!CanContinueInstallmentPayment(order) || _continueInstallmentPaymentAsync is null)
        {
            return;
        }

        await _continueInstallmentPaymentAsync(order!.InstallmentOrder!);
    }

    private async Task ConfirmInstallmentPickupAsync(HistoryOrderListItem? order)
    {
        if (!CanConfirmInstallmentPickup(order))
        {
            return;
        }

        var orderSnapshot = order;
        using var authorization = await AuthorizeAsync(Permissions.PosTerminal.Installments.ConfirmPickup, "confirm-installment-pickup");
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        InstallmentOrderActionResult result;
        var installmentGuid = orderSnapshot!.InstallmentOrder!.OrderId;
        try
        {
            // 中文注释：历史页提货入口复用分期中心同一接口，成功后刷新列表和右侧预览状态。
            result = await _installmentOrderService.ConfirmPickupAsync(installmentGuid, Session);
        }
        catch (OperationCanceledException ex)
        {
            RecordInstallmentPickupAudit(installmentGuid, "Failed", ex.GetType().Name);
            LockInstallmentPickup(orderSnapshot!.InstallmentOrder!.OrderId);
            if (ReferenceEquals(SelectedOrder, orderSnapshot))
            {
                StatusMessage = "分期提货确认超时，结果可能已提交，请刷新后核对，勿重复操作。";
            }
            return;
        }
        catch (Exception ex)
        {
            RecordInstallmentPickupAudit(installmentGuid, "Failed", ex.GetType().Name);
            if (ReferenceEquals(SelectedOrder, orderSnapshot))
            {
                StatusMessage = ex.Message;
            }
            return;
        }

        RecordInstallmentPickupAudit(
            installmentGuid,
            result.Succeeded ? "Succeeded" : "Failed",
            result.Succeeded ? null : result.Message);
        StatusMessage = result.Message;
        if (result.RequiresReview)
        {
            // 服务层已将未知结果持久化；本页立即同步锁，防止刷新前再次提交。
            LockInstallmentPickup(orderSnapshot!.InstallmentOrder!.OrderId);
        }
        if (result.Succeeded)
        {
            var message = result.Message;
            await LoadAsync();
            if (string.IsNullOrWhiteSpace(StatusMessage))
            {
                StatusMessage = message;
            }
        }
    }

    /// <summary>
    /// 分期提货把订单推进到终态（货物交给顾客），与分期中心入口使用同一事件类型和原因码。
    /// </summary>
    private void RecordInstallmentPickupAudit(Guid installmentGuid, string outcome, string? safeMessage)
    {
        OperationAuditEvents.RecordAction(
            _operationAuditLogger,
            OperationAuditTypes.InstallmentPickupConfirm,
            outcome,
            Session,
            reasonCode: "PICKUP",
            safeMessage: safeMessage,
            orderGuid: installmentGuid.ToString("D"));
    }

    private void LockInstallmentPickup(Guid installmentGuid)
    {
        _lockedInstallmentGuids.Add(installmentGuid);
        ConfirmInstallmentPickupCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsConfirmInstallmentPickupVisible));
    }

    private async Task ReprintSelectedAsync()
    {
        var selectedOrder = SelectedOrder;
        if (selectedOrder is null || !CanReprintSelected())
        {
            return;
        }

        using var authorization = await AuthorizeAsync(Permissions.PosTerminal.History.Reprint, "reprint-selected");
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        // 授权等待期间可能切换订单；补打只能继续处理最初已校验的同一张订单。
        if (SelectedOrder?.OrderGuid != selectedOrder.OrderGuid ||
            SelectedOrder.Source != selectedOrder.Source ||
            SelectedSource != selectedOrder.Source ||
            !CanReprintSelected())
        {
            return;
        }

        ReprintRequested?.Invoke(this, EventArgs.Empty);
    }

    private Task<ViewModelAuthorizationGrant?> AuthorizeAsync(string permissionCode, string action) =>
        ViewModelOperationAuthorization.AuthorizeAsync(
            _operationAuthorizationService,
            TryRequirePermission,
            permissionCode,
            "transaction-history",
            action,
            Session);

    private bool TryRequirePermission(string permissionCode)
    {
        if ((!_enforcePermissions && _cashierSessionContext.CurrentSession is null && Session.CashierSession is null) ||
            _cashierSessionContext.RequirePermission(permissionCode, out var message))
        {
            return true;
        }

        var operationType = permissionCode switch
        {
            Permissions.PosTerminal.History.Recall => OperationAuditTypes.OrderRecall,
            Permissions.PosTerminal.History.Reprint => OperationAuditTypes.ReceiptReprint,
            _ => null
        };
        if (operationType is not null)
        {
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                operationType,
                "Denied",
                Session,
                reasonCode: "PERMISSION_DENIED",
                safeMessage: message,
                orderGuid: SelectedOrder?.OrderGuid.ToString("D"));
        }

        StatusMessage = message;
        return false;
    }

    private ReceiptDetails CreateSuspendedReceipt(SuspendedOrder order)
    {
        return new ReceiptDetails(
            order.SuspendedOrderGuid,
            order.StoreCode,
            order.DeviceCode,
            order.CashierName,
            order.SuspendedAt,
            order.TotalAmount,
            order.DiscountAmount,
            order.ActualAmount,
            order.Lines.Select(line => new ReceiptPreviewLine(
                line.DisplayName,
                line.LookupCode,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.ActualAmount)
            {
                ProductCode = line.ProductCode,
                ItemNumber = line.ItemNumber,
                ProductImage = line.ProductImage
            }).ToList(),
            [],
            // 挂单尚未完成付款，必须显式覆盖通用收据默认的 Paid 状态。
            StatusText: $"*** {T("history.payment.suspended")} ***");
    }

    private void ReturnToPos()
    {
        _returnToPos?.Invoke();
    }

    [RelayCommand]
    private void OpenReceiptPreview(HistoryOrderListItem? order)
    {
        if (order is null)
        {
            return;
        }

        if (!ReferenceEquals(SelectedOrder, order))
        {
            IsReceiptPreviewLoading = true;
            SelectedOrder = order;
        }
        else if (ReceiptPreviewRows.Count == 0 && !IsReceiptPreviewLoading)
        {
            _ = LoadSelectedReceiptSafelyAsync(order);
        }

        IsOrderDetailsOpen = false;
        IsReceiptPreviewOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanOpenOrderDetails))]
    private void OpenOrderDetails(HistoryOrderListItem? order)
    {
        if (!CanOpenOrderDetails(order))
        {
            return;
        }

        IsReceiptPreviewOpen = false;
        if (!ReferenceEquals(SelectedOrder, order))
        {
            IsReceiptPreviewLoading = true;
            SelectedOrder = order;
        }
        else if (SelectedReceipt is null && !IsReceiptPreviewLoading)
        {
            _ = LoadSelectedReceiptSafelyAsync(order);
        }

        IsOrderDetailsOpen = true;
    }

    private static bool CanOpenOrderDetails(HistoryOrderListItem? order)
    {
        return order?.CanViewOrderDetails == true;
    }

    [RelayCommand]
    private void CloseOrderDetails()
    {
        IsOrderDetailsOpen = false;
    }

    [RelayCommand(CanExecute = nameof(CanRetryOrderDetails))]
    private void RetryOrderDetails()
    {
        if (SelectedOrder is not { CanViewOrderDetails: true } order || IsReceiptPreviewLoading)
        {
            return;
        }

        _ = LoadSelectedReceiptSafelyAsync(order);
    }

    private bool CanRetryOrderDetails()
    {
        return IsOrderDetailsOpen &&
            SelectedOrder?.CanViewOrderDetails == true &&
            !IsReceiptPreviewLoading;
    }

    [RelayCommand]
    private void CloseReceiptPreview()
    {
        IsReceiptPreviewOpen = false;
    }

    private bool CanReturnToPos()
    {
        return _returnToPos is not null;
    }

    private void ClearReceiptPreview()
    {
        _selectedInstallmentDetails = null;
        SelectedReceipt = null;
        ReceiptLines.Clear();
        Payments.Clear();
        ReceiptPreviewRows.Clear();
        _orderDetailLines.Clear();
        HasOrderDetailsPayments = false;
        IsOrderDetailsEmpty = false;
        OrderDetailsErrorMessage = string.Empty;
        PreviewSubtotal = 0m;
        PreviewDiscount = 0m;
        PreviewTotal = 0m;
        PreviewOrderId = "-";
        PreviewSoldAt = "-";
    }

    private void SetOrderDetailsUnavailable()
    {
        IsOrderDetailsEmpty = false;
        OrderDetailsErrorMessage = TOrFallback(
            "history.orderDetailsUnavailable",
            "Order details are unavailable. Please try again.");
    }

    private void RefreshOrderDetailLines(ReceiptDetails? receipt)
    {
        _orderDetailLines.Clear();
        HasOrderDetailsPayments = receipt?.Payments.Count > 0;
        IsOrderDetailsEmpty = receipt is not null && receipt.Lines.Count == 0;
        if (receipt is null)
        {
            return;
        }

        OrderDetailsErrorMessage = string.Empty;
        foreach (var line in receipt.Lines)
        {
            _orderDetailLines.Add(new HistoryOrderDetailLine(
                line.DisplayName,
                line.ProductCode,
                line.ItemNumber,
                line.LookupCode,
                ResolveProductImage(receipt.StoreCode, line),
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.ActualAmount));
        }
    }

    private string? ResolveProductImage(string storeCode, ReceiptPreviewLine line)
    {
        if (!string.IsNullOrWhiteSpace(line.ProductImage))
        {
            return line.ProductImage.Trim();
        }

        if (_localSellableItemIndex is null || string.IsNullOrWhiteSpace(storeCode))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(line.LookupCode))
        {
            var exactImage = SelectCatalogImage(
                _localSellableItemIndex.FindExactMatches(storeCode, line.LookupCode)
                    .Concat(_localSellableItemIndex.FindMetadataExactMatches(storeCode, line.LookupCode))
                    .Distinct(),
                line);
            if (exactImage is not null)
            {
                return exactImage;
            }
        }

        foreach (var metadata in new[] { line.ProductCode, line.ItemNumber }
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(value => value!.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var metadataImage = SelectCatalogImage(
                _localSellableItemIndex.FindMetadataExactMatches(storeCode, metadata),
                line);
            if (metadataImage is not null)
            {
                return metadataImage;
            }
        }

        return null;
    }

    private static string? SelectCatalogImage(
        IEnumerable<SellableItemDto> candidates,
        ReceiptPreviewLine line)
    {
        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ProductImage))
            .FirstOrDefault(candidate => CatalogIdentityMatches(candidate, line))
            ?.ProductImage
            ?.Trim();
    }

    private static bool CatalogIdentityMatches(SellableItemDto candidate, ReceiptPreviewLine line)
    {
        var hasProductCode = !string.IsNullOrWhiteSpace(line.ProductCode);
        var hasItemNumber = !string.IsNullOrWhiteSpace(line.ItemNumber);
        if (hasProductCode || hasItemNumber)
        {
            return (hasProductCode && CodesEqual(candidate.ProductCode, line.ProductCode)) ||
                (hasItemNumber && CodesEqual(candidate.ItemNumber, line.ItemNumber));
        }

        return CodesEqual(candidate.LookupCode, line.LookupCode) ||
            CodesEqual(candidate.Barcode, line.LookupCode);
    }

    private static bool CodesEqual(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void NotifyOrderDetailsProperties()
    {
        OnPropertyChanged(nameof(OrderDetailsOrderId));
        OnPropertyChanged(nameof(OrderDetailsSourceLabel));
        OnPropertyChanged(nameof(OrderDetailsStatusLabel));
        OnPropertyChanged(nameof(OrderDetailsSoldAt));
        OnPropertyChanged(nameof(OrderDetailsStoreCode));
        OnPropertyChanged(nameof(OrderDetailsDeviceCode));
        OnPropertyChanged(nameof(OrderDetailsCashierName));
        OnPropertyChanged(nameof(HasOrderDetailsCustomer));
        OnPropertyChanged(nameof(OrderDetailsCustomerName));
        OnPropertyChanged(nameof(OrderDetailsCustomerPhone));
        OnPropertyChanged(nameof(HasOrderDetailsTenderedAmount));
        OnPropertyChanged(nameof(OrderDetailsTenderedAmount));
        OnPropertyChanged(nameof(HasOrderDetailsChangeAmount));
        OnPropertyChanged(nameof(OrderDetailsChangeAmount));
        OnPropertyChanged(nameof(HasOrderDetailsInstallmentAmounts));
        OnPropertyChanged(nameof(OrderDetailsPaidAmount));
        OnPropertyChanged(nameof(OrderDetailsOutstandingAmount));
        OnPropertyChanged(nameof(IsOrderDetailsFinancialContentVisible));
    }

    private static string ValueOrDash(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "-";
    }

    private static string? NormalizeKeyword(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private string? SelectedTerminalDeviceCode => SelectedTerminalOption?.DeviceCode;

    private void SetSelectedSource(TransactionHistorySource source)
    {
        if (SelectedSource == source)
        {
            return;
        }

        SelectedSourceOption = SourceOptions.First(option => option.Source == source);
    }

    private void RefreshSourceOptions(TransactionHistorySource selectedSource)
    {
        _suppressSourceAutoLoad = true;
        SourceOptions.Clear();
        SourceOptions.Add(new HistorySourceOption(TransactionHistorySource.LocalOrders, T("history.source.local")));
        SourceOptions.Add(new HistorySourceOption(TransactionHistorySource.RemoteOrders, T("history.source.online")));
        SourceOptions.Add(new HistorySourceOption(TransactionHistorySource.InstallmentOrders, T("history.source.installments")));
        SourceOptions.Add(new HistorySourceOption(TransactionHistorySource.HeldOrders, T("history.source.held")));
        SelectedSourceOption = SourceOptions.First(option => option.Source == selectedSource);
        _suppressSourceAutoLoad = false;
    }

    private void RefreshTerminalOptions(bool selectAllTerminals)
    {
        var currentDeviceCode = Session.DeviceCode.Trim();
        TerminalOptions.Clear();
        var allTerminals = new TerminalFilterOption(null, T("history.allTerminals"));
        TerminalOptions.Add(allTerminals);

        TerminalFilterOption selected = allTerminals;
        if (!string.IsNullOrWhiteSpace(currentDeviceCode))
        {
            var currentTerminal = new TerminalFilterOption(currentDeviceCode, currentDeviceCode);
            TerminalOptions.Add(currentTerminal);
            selected = selectAllTerminals ? allTerminals : currentTerminal;
        }

        SelectedTerminalOption = selected;
        TerminalFilterText = selected.DeviceCode ?? T("history.allTerminals");
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RefreshSourceOptions(SelectedSource);
        RefreshTerminalOptions(SelectedTerminalOption?.DeviceCode is null);
        LocalizeSuspendedRows();
        OnPropertyChanged(nameof(CurrentUiLanguage));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(SearchHintText));
        OnPropertyChanged(nameof(ReceiptPreviewLabel));
        OnPropertyChanged(nameof(ReprintLabel));
        OnPropertyChanged(nameof(RefundLabel));
        OnPropertyChanged(nameof(IsInstallmentSourceSelected));
        OnPropertyChanged(nameof(IsHeldSourceSelected));
        OnPropertyChanged(nameof(IsContinueInstallmentPaymentVisible));
        OnPropertyChanged(nameof(HeldOrdersSourceLabel));
        OnPropertyChanged(nameof(DeleteHeldOrderLabel));
        OnPropertyChanged(nameof(ShareHeldOrderLabel));
        OnPropertyChanged(nameof(ShareStatusHeaderLabel));
        OnPropertyChanged(nameof(HeldLocalScopeLabel));
        OnPropertyChanged(nameof(HeldOtherScopeLabel));
        OnPropertyChanged(nameof(IsTerminalFilterVisible));
        OnPropertyChanged(nameof(IsHeldScopeSelectorVisible));
        OnPropertyChanged(nameof(ForceReleaseLabel));
        OnPropertyChanged(nameof(ForceReleaseHeaderLabel));
        OnPropertyChanged(nameof(ForceReleaseReasonLabel));
        OnPropertyChanged(nameof(ForceReleaseConfirmLabel));
        OnPropertyChanged(nameof(ForceReleaseCancelLabel));
        NotifyOrderDetailsProperties();
    }

    private void LocalizeSuspendedRows()
    {
        if (Orders.Count == 0)
        {
            return;
        }

        var selectedOrderGuid = SelectedOrder?.OrderGuid;
        var displayCulture = CurrentDisplayCulture;
        Orders.ReplaceWith(Orders.Select(order => order with
        {
            DisplayCulture = displayCulture,
            PaymentSummary = order.InstallmentOrder is { } installmentOrder
                ? FormatMoney(installmentOrder.PaidAmount)
                : order.IsSuspendedOrder || order.IsHeldOrder
                    ? T("history.payment.suspended")
                    : order.PaymentSummary,
            StatusLabel = order.InstallmentOrder is { } installmentStatus
                ? LocalizeInstallmentStatus(installmentStatus.Status)
                : order.IsHeldOrder
                    ? BuildHeldStatusLabel(order.HeldBadgeKind, order.HeldStatusDetail)
                    : order.IsSuspendedOrder ? T("history.status.pendingRecall") : order.StatusLabel
        }).ToList());
        SelectedOrder = selectedOrderGuid is null
            ? Orders.FirstOrDefault()
            : Orders.FirstOrDefault(order => order.OrderGuid == selectedOrderGuid.Value);
    }

    private string LocalizeInstallmentStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return string.Empty;
        }

        var normalized = status.Trim();
        return normalized switch
        {
            "待补款" or nameof(InstallmentStatus.Active) =>
                TOrFallback("history.installment.status.paymentDue", "Payment due"),
            "待提货" or nameof(InstallmentStatus.PaidOff) =>
                TOrFallback("history.installment.status.readyForPickup", "Ready for pickup"),
            "已提货" or nameof(InstallmentStatus.PickedUp) =>
                TOrFallback("history.installment.status.pickedUp", "Picked up"),
            "已作废" => TOrFallback("history.installment.status.voided", "Voided"),
            "已取消" or nameof(InstallmentStatus.Cancelled) =>
                TOrFallback("history.installment.status.cancelled", "Cancelled"),
            _ => normalized
        };
    }

    private string TOrFallback(string key, string fallback)
    {
        var localized = T(key);
        return string.Equals(localized, key, StringComparison.Ordinal) ? fallback : localized;
    }

    private string T(string key)
    {
        if (_localization?.T(key) is { } localized && !localized.StartsWith("[[", StringComparison.Ordinal))
        {
            return localized;
        }

        return key switch
        {
            "TransactionHistory" => "Transaction History",
            "success.receiptPreview" => "Receipt Preview",
            "history.reprint" => "Reprint",
            "history.detailsLoadTimeout" => "Order details load timed out. Please try again.",
            "history.reuploadCompleted" => "Reupload completed: {0} succeeded, {1} failed.",
            "history.reuploadRangeEmpty" => "No eligible local orders were found for the selected date range.",
            "history.reuploadRangeCancelled" => "Date-range reupload cancelled.",
            "history.reuploadRangeProgress" => "Reuploading {0} orders in {1} batch(es)...",
            "history.reuploadRangeCompleted" => "Date-range reupload completed: {0} attempted, {1} succeeded, {2} failed.",
            "history.reuploadRangeInterrupted" => "Date-range reupload stopped while the server address was switching: {0} attempted, {1} succeeded, {2} did not complete in the batches already started. Later batches were not started; queued orders will continue uploading after the switch.",
            "history.reuploadRangeFailed" => "Date-range reupload stopped: {0}",
            "history.refund" => "Refund",
            "history.search" => "Search order, cashier, or terminal...",
            "history.allTerminals" => "All Terminals",
            "history.source.local" => "Local",
            "history.source.online" => "Online",
            "history.source.installments" => "Installments",
            "history.source.held" => "Held",
            "history.held.localHold" => "Local hold",
            "history.held.pendingPublish" => "Pending publish",
            "history.held.published" => "Published",
            "history.held.remotePending" => "Remote recall",
            "history.held.claimPrepared" => "Preparing on this device",
            "history.held.claimActive" => "Active on this device",
            "history.held.claimedByOther" => "Claimed by another terminal",
            "history.held.completed" => "Completed",
            "history.held.blocked" => "Blocked",
            "history.held.forceRelease" => "Force release",
            "history.held.forceReleaseReason" => "Force release reason",
            "history.held.forceReleaseConfirm" => "Confirm",
            "history.held.forceReleaseCancel" => "Cancel",
            "history.held.forceReleaseReasonRequired" => "A force-release reason is required.",
            "history.held.forceReleaseReasonTooLong" => "The force-release reason must not exceed 500 characters.",
            "history.held.forceReleased" => "Held order force-released.",
            "history.held.delete" => "Delete",
            "history.held.localScope" => "Local",
            "history.held.otherScope" => "Other",
            "history.held.share" => "Share",
            "history.held.sharePending" => "Awaiting share",
            "history.held.shared" => "Shared",
            "history.held.cannotShare" => "Cannot share",
            "history.held.shareStatus" => "Share status",
            "history.held.shareIneligible" => "This held order cannot be shared.",
            "history.held.deleteConfirmTitle" => "Delete held sale?",
            "history.held.deleteConfirmMessage" => "This permanently removes the local hold. A shared hold is cancelled online first.",
            "history.held.deleteConfirmAction" => "Delete held sale",
            "history.held.deleted" => "Held sale deleted.",
            "history.held.deleteFailed" => "The held sale was not deleted.",
            "history.held.deleteOnlineRequired" => "This shared held sale must be cancelled while online.",
            "history.held.deletePending" => "Deletion is pending; choose Delete to retry.",
            "history.held.remoteUnavailable" => "Remote held orders unavailable: {0}",
            "history.held.unavailable" => "Shared held orders are not configured on this terminal.",
            "Customer" => "Customer",
            "Phone" => "Phone",
            "Total" => "Total",
            "common.status" => "Status",
            "payment.installment.outstanding" => "Outstanding",
            "history.installment.paid" => "Paid",
            "history.installment.continuePayment" => "Continue payment",
            "history.payment.suspended" => "Suspended",
            "history.status.pendingRecall" => "Pending recall",
            "installment.center.column.orderNumber" => "Order No.",
            "payment.method.cash" => "Cash",
            "payment.method.card" => "Credit/Debit Card",
            "payment.method.voucher" => "Voucher",
            _ => key
        };
    }

    private CultureInfo CurrentDisplayCulture =>
        _localization?.CurrentCulture ??
        CultureInfo.GetCultureInfo(LocalizationService.DefaultCultureName);

    private string GetPaymentMethodLabel(PaymentMethodKind method)
    {
        return method switch
        {
            PaymentMethodKind.Cash => T("payment.method.cash"),
            PaymentMethodKind.Card => T("payment.method.card"),
            PaymentMethodKind.Voucher => T("payment.method.voucher"),
            _ => method.ToString()
        };
    }

    private static string FormatMoney(decimal amount)
    {
        return string.Create(CultureInfo.InvariantCulture, $"${amount:0.00}");
    }

    private static string FitPreviewColumns(string left, string right)
    {
        const int lineWidth = 42;
        left = left.Length > 24 ? left[..24] : left;
        right = right.Length > 16 ? right[..16] : right;
        return left + new string(' ', Math.Max(1, lineWidth - left.Length - right.Length)) + right;
    }

    private static DateTimeOffset? ParseDateFrom(DateTime? value)
    {
        return value is null ? null : new DateTimeOffset(value.Value.Date);
    }

    private static DateTimeOffset? ParseDateTo(DateTime? value)
    {
        return value is null ? null : new DateTimeOffset(value.Value.Date.AddDays(1).AddTicks(-1));
    }

    private async Task<ReceiptPrinterSettings> LoadPreviewSettingsAsync(CancellationToken cancellationToken)
    {
        if (_receiptPrinterSettingsStore is null)
        {
            return ReceiptPrinterSettings.Default;
        }

        try
        {
            return await _receiptPrinterSettingsStore.LoadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReceiptPrinterSettings.Default;
        }
    }

    private IReadOnlyList<ReceiptPreviewRow> BuildPreviewRows(ReceiptDetails receipt, ReceiptPrinterSettings settings)
    {
        try
        {
            return _receiptTextFormatter.Build(receipt, settings, receipt.SoldAt).PreviewRows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                return new ReceiptTextFormatter().Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt).PreviewRows;
            }
            catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
            {
                return [];
            }
        }
    }

    /// <summary>
    /// 界面变为可见时由视图代码调用：挂单源立即刷新并启动 10 秒自动刷新。
    /// </summary>
    public void OnScreenShown()
    {
        if (_disposed)
        {
            return;
        }

        _isScreenVisible = true;
        UpdateHeldAutoRefresh();
        if (SelectedSource == TransactionHistorySource.HeldOrders)
        {
            ResetHeldScopeToLocal();
            LastHeldAutoRefreshTask = RefreshHeldOrdersSilentlyAsync();
        }
    }

    /// <summary>界面隐藏时停表，避免后台空转。</summary>
    public void OnScreenHidden()
    {
        _isScreenVisible = false;
        CancelHeldLoad();
        IsHeldRemoteRefreshing = false;
        UpdateHeldAutoRefresh();
    }

    private void UpdateHeldAutoRefresh()
    {
        var shouldRun = !_disposed &&
            _isScreenVisible &&
            SelectedSource == TransactionHistorySource.HeldOrders;
        if (shouldRun)
        {
            if (_heldAutoRefreshTimer is null)
            {
                _heldAutoRefreshTimer = _timeProvider.CreateTimer(
                    OnHeldAutoRefreshTick,
                    null,
                    HeldAutoRefreshInterval,
                    HeldAutoRefreshInterval);
            }

            return;
        }

        StopHeldAutoRefresh();
    }

    private void StopHeldAutoRefresh()
    {
        _heldAutoRefreshTimer?.Dispose();
        _heldAutoRefreshTimer = null;
    }

    private void OnHeldAutoRefreshTick(object? state)
    {
        if (_disposed || !_isScreenVisible || SelectedSource != TransactionHistorySource.HeldOrders)
        {
            return;
        }

        // 生产环境 Timer 回调在线程池线程，需回到 Dispatcher 再刷新 UI 集合；
        // 测试环境没有 Application，直接在线程上执行。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => LastHeldAutoRefreshTask = RefreshHeldOrdersSilentlyAsync());
            return;
        }

        LastHeldAutoRefreshTask = RefreshHeldOrdersSilentlyAsync();
    }

    /// <summary>仅测试使用：最近一次自动刷新任务，便于等待 tick 完成。</summary>
    internal Task? LastHeldAutoRefreshTask { get; private set; }

    private async Task RefreshHeldOrdersSilentlyAsync()
    {
        if (_disposed ||
            !_isScreenVisible ||
            SelectedSource != TransactionHistorySource.HeldOrders)
        {
            return;
        }

        await LoadHeldOrdersAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelHeldLoad();
        IsHeldRemoteRefreshing = false;
        StopHeldAutoRefresh();
        if (_localization is not null)
        {
            _localization.CultureChanged -= OnCultureChanged;
        }

        _rawScannerService?.Unsubscribe(PageId);
        ReprintRequested = null;
    }
}
