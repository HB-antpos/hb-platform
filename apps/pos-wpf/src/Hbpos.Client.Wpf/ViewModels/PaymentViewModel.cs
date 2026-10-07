using System.Collections.ObjectModel;
using System.Globalization;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.ViewModels;

public partial class PaymentViewModel : ObservableObject, IDisposable
{
    private const decimal MinimumInstallmentTotalAmount = 50m;
    private const decimal MinimumInstallmentFirstPaymentAmount = 20m;

    private readonly PosCartService _cart;
    private readonly ICashPaymentWorkflowService _workflowService;
    private readonly IInstallmentOrderService _installmentOrderService;
    private readonly ILocalizationService? _localization;
    private readonly ICashierSessionContext _cashierSessionContext;
    private readonly bool _enforcePermissions;
    private PaymentNavigationActions _navigationActions;
    private readonly IOperationAuditLogger? _operationAuditLogger;
    private readonly IOperationAuthorizationService? _operationAuthorizationService;
    private readonly ICardTerminalSetupService? _cardTerminalSetupService;
    private Guid? _persistedLinklyCloudTerminalId;
    private bool _isLinklyCloudBackendTerminalMode;

    internal PaymentNavigationActions NavigationActions => _navigationActions;
    private readonly PaymentTenderController _tenderController = new();
    private readonly CardPaymentSession _cardSession;
    private readonly ILinklyFallbackPromptCoordinator? _linklyFallbackPromptCoordinator;

    private static readonly Dictionary<PaymentMethodKind, IPaymentMethodStrategy> PaymentStrategies = new()
    {
        [PaymentMethodKind.Cash] = CashStrategy.Instance,
        [PaymentMethodKind.Card] = CardStrategy.Instance,
        [PaymentMethodKind.Voucher] = VoucherStrategy.Instance
    };
    private string _statusKey = "payment.status.ready";
    private string? _statusTextOverride;
    private Guid? _pendingVoucherUploadOrderGuid;
    private decimal _pendingVoucherTenderedAmount;
    private decimal _pendingVoucherChangeAmount;
    private int _paymentEntryVersion;
    private decimal _workflowRemainingAmount;
    private InstallmentOrderSummary? _installmentRepaymentOrder;
    private string? _installmentDraftFingerprint;
    private Guid? _installmentDraftPaymentGuid;
    private string? _installmentDraftCardIdempotencyKey;
    private bool _requiresAlternativeRefundMethod;
    private long? _alternativeRefundCartRevision;
    private long _cartTransactionEpoch;
    private long? _unknownCardResultCartEpoch;
    private bool _cartWasEmpty;
    private int _shutdownStarted;
    private bool _disposed;

    private sealed record RecoveredPaymentProjectionSnapshot(
        bool RequiresAlternativeRefundMethod,
        long? AlternativeRefundCartRevision,
        IReadOnlyList<PaymentTender> Tenders,
        string StatusKey,
        string? StatusTextOverride,
        string TenderAmountText,
        string VoucherCodeText,
        string VoucherEntryText,
        bool IsVoucherEntryDialogOpen,
        PaymentMethodKind SelectedPaymentMethod,
        PaymentEntryMode PaymentMode,
        decimal TotalTendered,
        decimal RemainingAmount,
        decimal ChangeDue,
        decimal WorkflowRemainingAmount);

    [ObservableProperty]
    private PosSessionState _session;

    [ObservableProperty]
    private PaymentMethodKind _selectedPaymentMethod = PaymentMethodKind.Cash;

    [ObservableProperty]
    private string _tenderAmountText = string.Empty;

    [ObservableProperty]
    private string _voucherCodeText = string.Empty;

    [ObservableProperty]
    private string _voucherEntryText = string.Empty;

    [ObservableProperty]
    private bool _isVoucherEntryDialogOpen;

    [ObservableProperty]
    private decimal _changeDue;

    [ObservableProperty]
    private decimal _remainingAmount;

    [ObservableProperty]
    private decimal _totalTendered;

    [ObservableProperty]
    private int _pendingSyncCount;

    [ObservableProperty]
    private bool _isCardPaymentInProgress;

    [ObservableProperty]
    private bool _isPaymentInteractionLocked;

    [ObservableProperty]
    private bool _isLinklyCloudTerminalRefreshing;

    [ObservableProperty]
    private LinklyCloudTerminalSummary? _selectedLinklyCloudTerminal;

    [ObservableProperty]
    private string _linklyCloudTerminalStatusText = string.Empty;

    [ObservableProperty]
    private PaymentEntryMode _paymentMode;

    [ObservableProperty]
    private CardPaymentErrorOverlayViewModel? _cardPaymentErrorOverlay;

    [ObservableProperty]
    private bool _isInstallmentPaymentEnabled;

    [ObservableProperty]
    private bool _isInstallmentSwitchLocked;

    [ObservableProperty]
    private string _installmentCustomerName = string.Empty;

    [ObservableProperty]
    private string _installmentCustomerPhone = string.Empty;

    [ObservableProperty]
    private bool _isInstallmentCustomerDialogOpen;

    [ObservableProperty]
    private string _installmentCustomerDraftName = string.Empty;

    [ObservableProperty]
    private string _installmentCustomerDraftPhone = string.Empty;

    [ObservableProperty]
    private string _installmentCustomerEditTarget = "Name";

    public PaymentViewModel(
        PosCartService cart,
        CashCheckoutService checkout,
        ILocalOrderRepository orderRepository,
        ISyncQueueRepository syncQueueRepository,
        PosSessionState session,
        ILocalizationService? localization = null,
        Action? onBackToPos = null,
        Action? onShowInstallmentCenter = null,
        Func<Task<bool>>? recoverPreviousCardTransactionAsync = null,
        ILinklyFallbackPromptCoordinator? linklyFallbackPromptCoordinator = null,
        ICashierSessionContext? cashierSessionContext = null,
        bool enforcePermissionsWhenNoCashier = false,
        IInstallmentOrderService? installmentOrderService = null,
        Func<InstallmentOrderSummary, Task>? onInstallmentOrderCreatedAsync = null,
        Func<Task<bool>>? confirmInstallmentFullFirstPaymentAsync = null,
        IOperationAuditLogger? operationAuditLogger = null,
        IOperationAuthorizationService? operationAuthorizationService = null,
        Func<CardPaymentHandoffRequest, Task<CardPaymentHandoffCandidate?>>? prepareCardPaymentHandoffAsync = null,
        Func<CardPaymentHandoffCandidate, CardPaymentHandoffRequest, Task<bool>>? handoffCardPaymentAsync = null,
        Action? openCardRecoveryCenter = null,
        ICardTerminalSetupService? cardTerminalSetupService = null,
        IPaymentMethodSettingsService? paymentMethodSettingsService = null)
        : this(
            cart,
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueueRepository),
            session,
            localization,
            onBackToPos,
            onShowInstallmentCenter,
            recoverPreviousCardTransactionAsync,
            linklyFallbackPromptCoordinator,
            cashierSessionContext,
            enforcePermissionsWhenNoCashier,
            installmentOrderService,
            onInstallmentOrderCreatedAsync,
            confirmInstallmentFullFirstPaymentAsync,
            operationAuditLogger,
            operationAuthorizationService,
            prepareCardPaymentHandoffAsync,
            handoffCardPaymentAsync,
            openCardRecoveryCenter,
            cardTerminalSetupService,
            paymentMethodSettingsService)
    {
    }

    public PaymentViewModel(
        PosCartService cart,
        ICashPaymentWorkflowService workflowService,
        PosSessionState session,
        ILocalizationService? localization = null,
        Action? onBackToPos = null,
        Action? onShowInstallmentCenter = null,
        Func<Task<bool>>? recoverPreviousCardTransactionAsync = null,
        ILinklyFallbackPromptCoordinator? linklyFallbackPromptCoordinator = null,
        ICashierSessionContext? cashierSessionContext = null,
        bool enforcePermissionsWhenNoCashier = false,
        IInstallmentOrderService? installmentOrderService = null,
        Func<InstallmentOrderSummary, Task>? onInstallmentOrderCreatedAsync = null,
        Func<Task<bool>>? confirmInstallmentFullFirstPaymentAsync = null,
        IOperationAuditLogger? operationAuditLogger = null,
        IOperationAuthorizationService? operationAuthorizationService = null,
        Func<CardPaymentHandoffRequest, Task<CardPaymentHandoffCandidate?>>? prepareCardPaymentHandoffAsync = null,
        Func<CardPaymentHandoffCandidate, CardPaymentHandoffRequest, Task<bool>>? handoffCardPaymentAsync = null,
        Action? openCardRecoveryCenter = null,
        ICardTerminalSetupService? cardTerminalSetupService = null,
        IPaymentMethodSettingsService? paymentMethodSettingsService = null)
    {
        _cart = cart;
        _cartWasEmpty = cart.IsEmpty;
        _cart.CartChanged += OnCartTransactionChanged;
        _workflowService = workflowService;
        _installmentOrderService = installmentOrderService ?? NoopInstallmentOrderService.Instance;
        _session = session;
        _localization = localization;
        _cashierSessionContext = cashierSessionContext ?? new CashierSessionContext();
        _enforcePermissions = enforcePermissionsWhenNoCashier;
        _operationAuditLogger = operationAuditLogger;
        _operationAuthorizationService = operationAuthorizationService;
        _cardTerminalSetupService = cardTerminalSetupService;
        if (session.CashierSession is not null)
        {
            _cashierSessionContext.SetCurrent(session.CashierSession);
        }

        _navigationActions = PaymentNavigationActions.FromLegacyCallbacks(
            onBackToPos,
            onShowInstallmentCenter,
            recoverPreviousCardTransactionAsync,
            onInstallmentOrderCreatedAsync,
            confirmInstallmentFullFirstPaymentAsync,
            prepareCardPaymentHandoffAsync,
            handoffCardPaymentAsync,
            openCardRecoveryCenter);
        _linklyFallbackPromptCoordinator = linklyFallbackPromptCoordinator;
        _cardSession = new CardPaymentSession(this);
        _linklyFallbackPromptCoordinator?.SetPromptHandler(_cardSession.ConfirmLinklyFallbackAsync);
        if (_localization is not null)
        {
            _localization.CultureChanged += OnCultureChanged;
        }

        NumberInputCommand = new RelayCommand<string>(AppendTenderAmount, _ => IsPaymentInteractionEnabled);
        QuickCashCommand = new AsyncRelayCommand<QuickCashOption>(ApplyQuickCashAsync, CanApplyQuickCash);
        SelectCashCommand = new AsyncRelayCommand(() => AddTenderByMethodAsync(PaymentMethodKind.Cash), () => CanAddTender(PaymentMethodKind.Cash, allowDefaultAmount: true));
        SelectCardCommand = new AsyncRelayCommand(
            () => AddTenderByMethodAsync(PaymentMethodKind.Card),
            () => CanAddTender(PaymentMethodKind.Card, allowDefaultAmount: true));
        SelectVoucherCommand = new AsyncRelayCommand(() => AddTenderByMethodAsync(PaymentMethodKind.Voucher), () => CanAddTender(PaymentMethodKind.Voucher, allowDefaultAmount: true));
        OpenVoucherEntryCommand = new AsyncRelayCommand(OpenVoucherEntryAsync, CanOpenVoucherEntry);
        VoucherEntryKeyCommand = new RelayCommand<string>(ApplyVoucherEntryKey, _ => CanUseVoucherEntryDialog());
        ConfirmVoucherEntryCommand = new AsyncRelayCommand(ConfirmVoucherEntryAsync, CanConfirmVoucherEntry);
        CancelVoucherEntryCommand = new RelayCommand(CancelVoucherEntry, () => IsVoucherEntryDialogOpen);
        OpenInstallmentCustomerDialogCommand = new RelayCommand(OpenInstallmentCustomerDialog, CanOpenInstallmentCustomerDialog);
        SelectInstallmentCustomerFieldCommand = new RelayCommand<string>(SelectInstallmentCustomerField, _ => IsInstallmentCustomerDialogOpen);
        InstallmentCustomerKeyCommand = new RelayCommand<string>(ApplyInstallmentCustomerKey, _ => CanUseInstallmentCustomerDialog());
        ConfirmInstallmentCustomerDialogCommand = new RelayCommand(ConfirmInstallmentCustomerDialog, CanUseInstallmentCustomerDialog);
        CancelInstallmentCustomerDialogCommand = new RelayCommand(CancelInstallmentCustomerDialog, () => IsInstallmentCustomerDialogOpen);
        RemoveTenderCommand = new AsyncRelayCommand<PaymentTender?>(RemoveTenderAsync, CanRemoveTender);
        ConfirmPaymentCommand = new AsyncRelayCommand(ConfirmPaymentAsync, CanConfirmPayment);
        CancelCommand = new RelayCommand(CancelPayment, CanCancelPayment);
        BackToPosCommand = new RelayCommand(BackToPos, CanBackToPos);
        ShowInstallmentCenterCommand = new AsyncRelayCommand(ShowInstallmentCenterAsync, CanShowInstallmentCenter);
        OpenCardRecoveryCenterCommand = new AsyncRelayCommand(
            _cardSession.OpenRecoveryCenterAsync,
            _cardSession.CanOpenRecoveryCenter);
        CloseCardPaymentErrorOverlayCommand = new RelayCommand(CloseCardPaymentErrorOverlay);
        CardPaymentErrorPrimaryActionCommand = new AsyncRelayCommand(
            ExecuteCardPaymentErrorPrimaryActionAsync,
            CanExecuteCardPaymentErrorPrimaryAction);

        InitializeManualCardCommands();
        InitializePaymentMethodSettings(paymentMethodSettingsService);
        RefreshCart();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_paymentMethodSettingsService is not null)
            _paymentMethodSettingsService.Changed -= OnPaymentMethodSettingsChanged;
        BeginShutdown();
        if (_localization is not null)
        {
            _localization.CultureChanged -= OnCultureChanged;
        }

        _cart.CartChanged -= OnCartTransactionChanged;
        _cardSession.Dispose();
    }

    public Task BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return _cardSession.CancelForShutdownAsync();
        }

        // 关闭窗口期间只取消仍在等待的终端调用，保留 attempt 与恢复锁给下次启动核对。
        IsPaymentInteractionLocked = true;
        var cancellationTask = _cardSession.CancelForShutdownAsync();
        try
        {
            NotifyPaymentCommandStates();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 中文注释：关机命令通知是 best-effort，不能阻止主 shutdown step 观察终端取消任务。
            ConsoleLog.WriteError(
                "Shutdown",
                $"payment shutdown command notification failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }

        return cancellationTask;
    }

    public async Task RefreshLinklyCloudTerminalsAsync()
    {
        if (!IsIntegratedCardPaymentVisible || !_paymentMethodSettingsReady ||
            _cardTerminalSetupService is null || IsLinklyCloudTerminalRefreshing)
        {
            return;
        }

        IsLinklyCloudTerminalRefreshing = true;
        // 首次进入付款页时尚不知道服务端是否 Active；先阻止卡支付，配置与目录均确认后再放行。
        _isLinklyCloudBackendTerminalMode = true;
        RaiseLinklyCloudTerminalProperties();
        try
        {
            var configuration = await _cardTerminalSetupService.LoadConfigurationAsync();
            var isCloudBackendConfigured =
                CardTerminalSettings.NormalizeLinklyConnectionMode(configuration.LinklyConnectionMode) ==
                LinklyConnectionMode.CloudBackendAsync;
            if (!isCloudBackendConfigured)
            {
                ClearLinklyCloudTerminalDirectory();
                return;
            }

            var directory = await _cardTerminalSetupService.ListLinklyCloudBackendTerminalsAsync(
                configuration.Environment);
            if (!string.Equals(directory.Mode, "Active", StringComparison.OrdinalIgnoreCase))
            {
                // Legacy/Draft 继续使用既有共享凭据流程；只有 Active 才启用一对一终端选择。
                ClearLinklyCloudTerminalDirectory();
                return;
            }

            LinklyCloudTerminals.Clear();
            foreach (var terminal in directory.Terminals
                         .Where(item => item.IsReady)
                         .OrderBy(item => item.LaneNo)
                         .ThenBy(item => item.DisplayName))
            {
                LinklyCloudTerminals.Add(terminal);
            }

            _persistedLinklyCloudTerminalId = directory.SelectedTerminalId;
            LinklyCloudSelectionRevision = directory.SelectionRevision;
            SelectedLinklyCloudTerminal = directory.SelectedTerminalId is Guid selectedTerminalId
                ? LinklyCloudTerminals.FirstOrDefault(item => item.TerminalId == selectedTerminalId)
                : null;

            // 单台 Ready 终端无需额外点击；仍通过版本化 API 持久化为本 POS 的选择。
            if (SelectedLinklyCloudTerminal is null &&
                LinklyCloudTerminals.Count == 1 &&
                LinklyCloudTerminals[0] is { } onlyTerminal)
            {
                var selection = await _cardTerminalSetupService.SelectLinklyCloudBackendTerminalAsync(
                    configuration.Environment,
                    onlyTerminal.TerminalId,
                    directory.SelectionRevision);
                ApplyLinklyCloudSelection(onlyTerminal, selection.Revision);
            }

            LinklyCloudTerminalStatusText = LinklyCloudTerminals.Count == 0
                ? TerminalText("payment.linklyTerminal.noneReady", "No ready Linkly Cloud terminal is available.")
                : SelectedLinklyCloudTerminalText;
            RaiseLinklyCloudTerminalProperties();
        }
        catch (OperationCanceledException ex)
        {
            LinklyCloudTerminalStatusText = GetLinklyTerminalErrorText(
                ex,
                "payment.linklyTerminal.refreshFailed",
                "Unable to refresh Linkly Cloud terminals. Try Refresh again.");
            ConsoleLog.WriteError(
                "Payment",
                $"load linkly cloud terminal directory canceled error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
        catch (Exception ex) when (
            ex is not OutOfMemoryException &&
            ex is not StackOverflowException)
        {
            LinklyCloudTerminalStatusText = GetLinklyTerminalErrorText(
                ex,
                "payment.linklyTerminal.refreshFailed",
                "Unable to refresh Linkly Cloud terminals. Try Refresh again.");
            ConsoleLog.WriteError(
                "Payment",
                $"load linkly cloud terminal directory failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
        finally
        {
            IsLinklyCloudTerminalRefreshing = false;
            RaiseLinklyCloudTerminalProperties();
        }
    }

    public async Task SelectLinklyCloudTerminalAsync(LinklyCloudTerminalSummary? terminal)
    {
        if (_cardTerminalSetupService is null || terminal is null || !_isLinklyCloudBackendTerminalMode)
        {
            return;
        }

        if (terminal.TerminalId == _persistedLinklyCloudTerminalId)
        {
            SelectedLinklyCloudTerminal = terminal;
            LinklyCloudTerminalStatusText = SelectedLinklyCloudTerminalText;
            return;
        }

        if (!CanSwitchLinklyCloudTerminal || !terminal.IsReady)
        {
            RestorePersistedLinklyCloudTerminal();
            LinklyCloudTerminalStatusText = TerminalText(
                "payment.linklyTerminal.switchBlocked",
                "The terminal cannot be switched while a card payment is active or unresolved.");
            return;
        }

        IsLinklyCloudTerminalRefreshing = true;
        try
        {
            var configuration = await _cardTerminalSetupService.LoadConfigurationAsync();
            var selection = await _cardTerminalSetupService.SelectLinklyCloudBackendTerminalAsync(
                configuration.Environment,
                terminal.TerminalId,
                LinklyCloudSelectionRevision);
            ApplyLinklyCloudSelection(terminal, selection.Revision);
            LinklyCloudTerminalStatusText = string.Format(
                CultureInfo.CurrentCulture,
                TerminalText("payment.linklyTerminal.selected", "Payments will use {0}."),
                terminal.DisplayName);
        }
        catch (OperationCanceledException ex)
        {
            RestorePersistedLinklyCloudTerminal();
            LinklyCloudTerminalStatusText = GetLinklyTerminalErrorText(
                ex,
                "payment.linklyTerminal.refreshFailed",
                "Unable to refresh Linkly Cloud terminals. Try Refresh again.");
            ConsoleLog.WriteError(
                "Payment",
                $"select linkly cloud terminal canceled error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
        catch (Exception ex) when (
            ex is not OutOfMemoryException &&
            ex is not StackOverflowException)
        {
            RestorePersistedLinklyCloudTerminal();
            LinklyCloudTerminalStatusText = GetLinklyTerminalErrorText(
                ex,
                "payment.linklyTerminal.refreshFailed",
                "Unable to refresh Linkly Cloud terminals. Try Refresh again.");
            ConsoleLog.WriteError(
                "Payment",
                $"select linkly cloud terminal failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
        finally
        {
            IsLinklyCloudTerminalRefreshing = false;
            RaiseLinklyCloudTerminalProperties();
        }
    }

    private void ApplyLinklyCloudSelection(LinklyCloudTerminalSummary terminal, long revision)
    {
        _persistedLinklyCloudTerminalId = terminal.TerminalId;
        LinklyCloudSelectionRevision = revision;
        SelectedLinklyCloudTerminal = terminal;
        RaiseLinklyCloudTerminalProperties();
    }

    private void RestorePersistedLinklyCloudTerminal()
    {
        SelectedLinklyCloudTerminal = _persistedLinklyCloudTerminalId is Guid terminalId
            ? LinklyCloudTerminals.FirstOrDefault(item => item.TerminalId == terminalId)
            : null;
    }

    private void ClearLinklyCloudTerminalDirectory()
    {
        _isLinklyCloudBackendTerminalMode = false;
        LinklyCloudTerminals.Clear();
        SelectedLinklyCloudTerminal = null;
        _persistedLinklyCloudTerminalId = null;
        LinklyCloudSelectionRevision = null;
        LinklyCloudTerminalStatusText = string.Empty;
        RaiseLinklyCloudTerminalProperties();
    }

    private void RaiseLinklyCloudTerminalProperties()
    {
        OnPropertyChanged(nameof(LinklyCloudSelectionRevision));
        OnPropertyChanged(nameof(IsLinklyCloudTerminalSelectorVisible));
        OnPropertyChanged(nameof(CanSwitchLinklyCloudTerminal));
        OnPropertyChanged(nameof(SelectedLinklyCloudTerminalText));
        NotifyPaymentCommandStates();
    }

    private string TerminalText(string key, string fallback)
    {
        var translated = _localization?.T(key);
        return string.IsNullOrWhiteSpace(translated) || string.Equals(translated, key, StringComparison.Ordinal)
            ? fallback
            : translated;
    }

    private string GetLinklyTerminalErrorText(Exception exception, string key, string fallback)
    {
        return string.IsNullOrWhiteSpace(exception.Message)
            ? TerminalText(key, fallback)
            : exception.Message;
    }

    internal bool IsShuttingDown => Volatile.Read(ref _shutdownStarted) == 1;

    public ObservableCollection<CartLine> CartLines { get; } = [];

    public ObservableCollection<PaymentTender> PaymentTenders { get; } = [];

    public ObservableCollection<LinklyCloudTerminalSummary> LinklyCloudTerminals { get; } = [];

    public long? LinklyCloudSelectionRevision { get; private set; }

    public bool IsLinklyCloudTerminalSelectorVisible =>
        IsIntegratedCardPaymentVisible && _isLinklyCloudBackendTerminalMode;

    public bool CanSwitchLinklyCloudTerminal =>
        IsLinklyCloudTerminalSelectorVisible &&
        LinklyCloudTerminals.Count > 1 &&
        !IsLinklyCloudTerminalRefreshing &&
        !IsCardPaymentInProgress &&
        IsPaymentInteractionEnabled;

    public string SelectedLinklyCloudTerminalText => SelectedLinklyCloudTerminal is null
        ? TerminalText("payment.linklyTerminal.none", "No terminal selected")
        : string.Format(
            CultureInfo.CurrentCulture,
            TerminalText("payment.linklyTerminal.display", "Lane {0} · {1}"),
            SelectedLinklyCloudTerminal.LaneNo,
            SelectedLinklyCloudTerminal.DisplayName);

    public IReadOnlyList<QuickCashOption> QuickCashAmounts => BuildQuickCashAmounts();

    public IRelayCommand<string> NumberInputCommand { get; }

    public IAsyncRelayCommand<QuickCashOption> QuickCashCommand { get; }

    public IAsyncRelayCommand SelectCashCommand { get; }

    public IAsyncRelayCommand SelectCardCommand { get; }

    public IAsyncRelayCommand SelectVoucherCommand { get; }

    public IAsyncRelayCommand OpenVoucherEntryCommand { get; }

    public IRelayCommand<string> VoucherEntryKeyCommand { get; }

    public IAsyncRelayCommand ConfirmVoucherEntryCommand { get; }

    public IRelayCommand CancelVoucherEntryCommand { get; }

    public IRelayCommand OpenInstallmentCustomerDialogCommand { get; }

    public IRelayCommand<string> SelectInstallmentCustomerFieldCommand { get; }

    public IRelayCommand<string> InstallmentCustomerKeyCommand { get; }

    public IRelayCommand ConfirmInstallmentCustomerDialogCommand { get; }

    public IRelayCommand CancelInstallmentCustomerDialogCommand { get; }

    public IAsyncRelayCommand<PaymentTender?> RemoveTenderCommand { get; }

    public IAsyncRelayCommand ConfirmPaymentCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand BackToPosCommand { get; }

    public IRelayCommand ShowInstallmentCenterCommand { get; }

    public IAsyncRelayCommand OpenCardRecoveryCenterCommand { get; }

    public IRelayCommand CloseCardPaymentErrorOverlayCommand { get; }

    public IAsyncRelayCommand CardPaymentErrorPrimaryActionCommand { get; }

    public event EventHandler<PaymentCompletedEventArgs>? PaymentCompleted;

    public string ScreenTitleText => T(GetScreenTitleKey());

    public string OrderSummaryText => T("payment.orderSummary");

    public string CurrentTenderTextLabel => T("payment.currentTender");

    public string AmountTenderedTextLabel => CurrentTenderTextLabel;

    public string RemainingAmountText => T(GetRemainingAmountKey());

    public string ChangeDueText => T("payment.changeDue");

    public string QuickCashText => T("payment.quickCash");

    public string PaymentMethodText => T("payment.method");

    public string AppliedTendersText => T("payment.appliedTenders");

    public string ConfirmPaymentText => T(GetConfirmPaymentKey());

    public string NoTendersText => T(GetNoTendersKey());

    public string CashMethodText => T(GetMethodTextKey(PaymentMethodKind.Cash));

    public string CardMethodText => T(GetMethodTextKey(PaymentMethodKind.Card));

    public string VoucherMethodText => T(GetMethodTextKey(PaymentMethodKind.Voucher));

    public string InstallmentMethodText => T("payment.method.installment");

    public string CancelText => T("common.cancel");

    public string StatusMessage => _statusTextOverride ?? T(_statusKey);

    public bool IsPaymentInteractionEnabled => _paymentMethodSettingsReady && !IsPaymentInteractionLocked && !_cardSession.HasUnknownResult && !IsManualCardDialogOpen;

    // 中文注释：恢复入口必须独立于错误遮罩；订单锁定时即使遮罩已关闭也要能继续核对已批准卡款。
    public bool IsCardPaymentRecoveryRequired => _cardSession.HasUnknownResult;

    // 普通支付状态隐藏取消入口，避免将取消误用为返回收银页。
    public bool IsCancelPaymentVisible => IsCardPaymentInProgress || _cardSession.IsAwaitingLateResult;

    public decimal TotalAmount => _cart.TotalAmount;

    public decimal DiscountAmount => _cart.DiscountAmount;

    public decimal ActualAmount => _cart.ActualAmount;

    public bool IsRefundMode => PaymentMode == PaymentEntryMode.Refund;

    public bool IsZeroSettlementMode => PaymentMode == PaymentEntryMode.ZeroSettlement;

    public bool IsPaymentMode => PaymentMode == PaymentEntryMode.Payment;

    public bool IsTenderEntryVisible => !IsZeroSettlementMode;

    public bool IsPaymentMethodSelectionVisible => !IsZeroSettlementMode;

    public bool IsQuickCashVisible => IsPaymentMode && IsCashSelected;

    public string AmountTenderedText
    {
        get => TenderAmountText;
        set => TenderAmountText = value;
    }

    public bool IsCashSelected => SelectedPaymentMethod == PaymentMethodKind.Cash;

    public bool IsCardSelected => SelectedPaymentMethod == PaymentMethodKind.Card;

    public bool IsVoucherSelected => SelectedPaymentMethod == PaymentMethodKind.Voucher;

    public bool IsVoucherCodeEntryVisible => IsVoucherPaymentVisible && IsVoucherSelected && !IsRefundMode;

    public bool IsInstallmentEntryVisible => IsPaymentMode;

    public bool IsInstallmentCustomerSectionVisible => IsInstallmentPaymentEnabled;

    public bool CanEditInstallmentCustomer => IsInstallmentPaymentEnabled && _installmentRepaymentOrder is null;

    public string InstallmentCustomerNameDisplay => string.IsNullOrWhiteSpace(InstallmentCustomerName)
        ? T("payment.installment.customerMissingName")
        : InstallmentCustomerName;

    public string InstallmentCustomerPhoneDisplay => string.IsNullOrWhiteSpace(InstallmentCustomerPhone)
        ? T("payment.installment.customerMissingPhone")
        : InstallmentCustomerPhone;

    public bool IsInstallmentCustomerNameDraftActive => InstallmentCustomerEditTarget.Equals("Name", StringComparison.OrdinalIgnoreCase);

    public bool IsInstallmentCustomerPhoneDraftActive => InstallmentCustomerEditTarget.Equals("Phone", StringComparison.OrdinalIgnoreCase);

    public string InstallmentOrderInfoText => _installmentRepaymentOrder is null
        ? string.Empty
        : string.Format(
            _localization?.CurrentCulture ?? CultureInfo.GetCultureInfo(LocalizationService.DefaultCultureName),
            "{0} | {1} | {2} | {3}: {4:C2}",
            _installmentRepaymentOrder.OrderNumber,
            _installmentRepaymentOrder.CustomerName,
            _installmentRepaymentOrder.CustomerPhone,
            T("payment.installment.outstanding"),
            _installmentRepaymentOrder.OutstandingAmount);

    public bool IsConfirmPaymentVisible => CanConfirmPayment();

    partial void OnTenderAmountTextChanged(string value)
    {
        RecalculateTenderSummary();
        NotifyPaymentCommandStates();
    }

    partial void OnVoucherCodeTextChanged(string value)
    {
        NotifyPaymentCommandStates();
    }

    partial void OnIsInstallmentPaymentEnabledChanged(bool value)
    {
        if (IsInstallmentSwitchLocked && !value)
        {
            // 中文注释：历史分期补款必须锁定在分期模式，避免误切回普通付款。
            IsInstallmentPaymentEnabled = true;
            return;
        }

        if (!value)
        {
            _installmentRepaymentOrder = null;
            IsInstallmentSwitchLocked = false;
            InstallmentCustomerName = string.Empty;
            InstallmentCustomerPhone = string.Empty;
            InstallmentCustomerDraftName = string.Empty;
            InstallmentCustomerDraftPhone = string.Empty;
            IsInstallmentCustomerDialogOpen = false;
        }

        OnPropertyChanged(nameof(IsInstallmentCustomerSectionVisible));
        OnPropertyChanged(nameof(CanEditInstallmentCustomer));
        OnPropertyChanged(nameof(InstallmentOrderInfoText));
        OpenInstallmentCustomerDialogCommand.NotifyCanExecuteChanged();
        RecalculateTenderSummary();
        NotifyPaymentCommandStates();
    }

    partial void OnIsInstallmentSwitchLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditInstallmentCustomer));
        OpenInstallmentCustomerDialogCommand.NotifyCanExecuteChanged();
    }

    partial void OnInstallmentCustomerNameChanged(string value)
    {
        OnPropertyChanged(nameof(InstallmentCustomerNameDisplay));
        NotifyPaymentCommandStates();
    }

    partial void OnInstallmentCustomerPhoneChanged(string value)
    {
        OnPropertyChanged(nameof(InstallmentCustomerPhoneDisplay));
        NotifyPaymentCommandStates();
    }

    partial void OnIsInstallmentCustomerDialogOpenChanged(bool value)
    {
        SelectInstallmentCustomerFieldCommand.NotifyCanExecuteChanged();
        InstallmentCustomerKeyCommand.NotifyCanExecuteChanged();
        ConfirmInstallmentCustomerDialogCommand.NotifyCanExecuteChanged();
        CancelInstallmentCustomerDialogCommand.NotifyCanExecuteChanged();
    }

    partial void OnInstallmentCustomerEditTargetChanged(string value)
    {
        OnPropertyChanged(nameof(IsInstallmentCustomerNameDraftActive));
        OnPropertyChanged(nameof(IsInstallmentCustomerPhoneDraftActive));
    }

    partial void OnVoucherEntryTextChanged(string value)
    {
        VoucherEntryKeyCommand.NotifyCanExecuteChanged();
        ConfirmVoucherEntryCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsVoucherEntryDialogOpenChanged(bool value)
    {
        VoucherEntryKeyCommand.NotifyCanExecuteChanged();
        ConfirmVoucherEntryCommand.NotifyCanExecuteChanged();
        CancelVoucherEntryCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPaymentMethodChanged(PaymentMethodKind value)
    {
        SelectCashCommand.NotifyCanExecuteChanged();
        SelectCardCommand.NotifyCanExecuteChanged();
        SelectVoucherCommand.NotifyCanExecuteChanged();
        OpenVoucherEntryCommand.NotifyCanExecuteChanged();
        QuickCashCommand.NotifyCanExecuteChanged();
        ShowInstallmentCenterCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsCashSelected));
        OnPropertyChanged(nameof(IsCardSelected));
        OnPropertyChanged(nameof(IsVoucherSelected));
        OnPropertyChanged(nameof(IsVoucherCodeEntryVisible));
        OnPropertyChanged(nameof(QuickCashAmounts));
        OnPropertyChanged(nameof(IsQuickCashVisible));
    }

    partial void OnIsCardPaymentInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSwitchLinklyCloudTerminal));
        NotifyPaymentCommandStates();
    }

    partial void OnIsPaymentInteractionLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPaymentInteractionEnabled));
        OnPropertyChanged(nameof(CanSwitchLinklyCloudTerminal));
        NotifyPaymentCommandStates();
    }

    partial void OnIsLinklyCloudTerminalRefreshingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSwitchLinklyCloudTerminal));
        NotifyPaymentCommandStates();
    }

    partial void OnSelectedLinklyCloudTerminalChanged(LinklyCloudTerminalSummary? value)
    {
        OnPropertyChanged(nameof(SelectedLinklyCloudTerminalText));
    }

    partial void OnSessionChanged(PosSessionState value)
    {
        if (value.CashierSession is not null)
        {
            _cashierSessionContext.SetCurrent(value.CashierSession);
        }

        PendingSyncCount = value.PendingSyncCount;
        NotifyPaymentCommandStates();
    }

    partial void OnPaymentModeChanged(PaymentEntryMode value)
    {
        RaiseLocalizedProperties();
        OnPropertyChanged(nameof(IsRefundMode));
        OnPropertyChanged(nameof(IsZeroSettlementMode));
        OnPropertyChanged(nameof(IsPaymentMode));
        OnPropertyChanged(nameof(IsTenderEntryVisible));
        OnPropertyChanged(nameof(IsPaymentMethodSelectionVisible));
        OnPropertyChanged(nameof(IsQuickCashVisible));
        OnPropertyChanged(nameof(IsVoucherCodeEntryVisible));
        OnPropertyChanged(nameof(IsInstallmentEntryVisible));
        OpenVoucherEntryCommand.NotifyCanExecuteChanged();
        OpenInstallmentCustomerDialogCommand.NotifyCanExecuteChanged();
        ShowInstallmentCenterCommand.NotifyCanExecuteChanged();
    }

    public void PrepareForEntry(PosSessionState session)
    {
        // 已人工确认但尚未落单时，页面重入不能清掉付款证据。
        if (IsManualCardSavePending) return;
        IsManualCardDialogOpen = false;
        IsManualCardSuccessChecked = false;
        // 先封死上一付款代际并取消旧卡任务；Session setter 或其通知失败时，迟到结果也不能污染恢复页。
        _paymentEntryVersion++;
        _cardSession.ResetManualCancellationState();
        _cardSession.Cancel();
        _cardSession.DetachCanceledActiveCardPayment();
        Session = session;
        _pendingVoucherUploadOrderGuid = null;
        _pendingVoucherTenderedAmount = 0m;
        _pendingVoucherChangeAmount = 0m;
        if (_cardSession.HasUnknownResult &&
            _unknownCardResultCartEpoch is long protectedEpoch &&
            protectedEpoch != _cartTransactionEpoch)
        {
            // 只有购物车确实经过清空边界进入下一笔交易，才允许释放上一单的未知结果锁。
            _cardSession.SetResultUnknownRecoveryRequired(false);
        }

        IsCardPaymentInProgress = false;
        IsPaymentInteractionLocked = IsShuttingDown || _cardSession.HasUnknownResult;
        if (_requiresAlternativeRefundMethod &&
            _cart.RecoveryOwnerAttemptGuid is null &&
            _alternativeRefundCartRevision is long protectedRevision &&
            protectedRevision != _cart.Revision)
        {
            // 上一笔替代退款策略只绑定当时的恢复购物车；清空后装入的新订单不得继承禁卡状态。
            _requiresAlternativeRefundMethod = false;
            _alternativeRefundCartRevision = null;
        }

        _installmentRepaymentOrder = null;
        IsInstallmentSwitchLocked = false;
        IsInstallmentPaymentEnabled = false;
        InstallmentCustomerName = string.Empty;
        InstallmentCustomerPhone = string.Empty;
        InstallmentCustomerDraftName = string.Empty;
        InstallmentCustomerDraftPhone = string.Empty;
        IsInstallmentCustomerDialogOpen = false;
        PaymentTenders.Clear();
        VoucherCodeText = string.Empty;
        VoucherEntryText = string.Empty;
        IsVoucherEntryDialogOpen = false;
        TenderAmountText = string.Empty;
        if (!_cardSession.HasUnknownResult)
        {
            _statusKey = _requiresAlternativeRefundMethod
                ? "payment.refund.status.alternativeMethodRequired"
                : GetReadyStatusKey();
            _statusTextOverride = null;
        }

        SelectedPaymentMethod = PaymentMethodKind.Cash;
        RefreshCart();
        if (!_cardSession.HasUnknownResult && !HasBlockingCartIssue())
        {
            SetStatus(_requiresAlternativeRefundMethod
                ? "payment.refund.status.alternativeMethodRequired"
                : GetReadyStatusKey());
            if (!_requiresAlternativeRefundMethod && GetRequiredVoucherRefundAmount() > 0m)
            {
                // 退货里含代金券买的商品：进入退款页就提示至少多少须退代金券，现金/刷卡按钮按上限置灰。
                SetVoucherFundedRefundRequiredStatus();
            }
        }

        OnPropertyChanged(nameof(StatusMessage));
        _ = RefreshPaymentSettingsForEntryAsync();
    }

    private void OnCartTransactionChanged(object? sender, EventArgs e)
    {
        var isEmpty = _cart.IsEmpty;
        if (isEmpty && !_cartWasEmpty)
        {
            _cartTransactionEpoch++;
        }

        _cartWasEmpty = isEmpty;
    }

    internal void OnCardResultUnknownRecoveryChanged(bool required)
    {
        OnPropertyChanged(nameof(IsCardPaymentRecoveryRequired));
        OnPropertyChanged(nameof(CanSwitchLinklyCloudTerminal));
        if (required)
        {
            _unknownCardResultCartEpoch ??= _cartTransactionEpoch;
            return;
        }

        _unknownCardResultCartEpoch = null;
    }

    public void PrepareForInstallmentRepayment(PosSessionState session, InstallmentOrderSummary order)
    {
        PrepareForEntry(session);

        // 中文注释：历史页续付直接进入普通支付页，但目标金额来自原分期单余额。
        _installmentRepaymentOrder = order;
        InstallmentCustomerName = order.CustomerName;
        InstallmentCustomerPhone = order.CustomerPhone;
        IsInstallmentPaymentEnabled = true;
        IsInstallmentSwitchLocked = true;
        PaymentMode = CalculatePaymentMode();
        RecalculateTenderSummary();
        SetStatus(GetReadyStatusKey());
        OnPropertyChanged(nameof(CanEditInstallmentCustomer));
        OnPropertyChanged(nameof(InstallmentOrderInfoText));
        NotifyPaymentCommandStates();
    }

    public void RefreshCart()
    {
        if (_cart.IsEmpty)
        {
            // 非卡退款限制属于当前恢复购物车；只有购物车真正清空后才可释放，页面往返不能绕过。
            _requiresAlternativeRefundMethod = false;
            _alternativeRefundCartRevision = null;
        }

        PaymentMode = CalculatePaymentMode();
        CartLines.ReplaceWith(_cart.Lines);
        OnPropertyChanged(nameof(TotalAmount));
        OnPropertyChanged(nameof(DiscountAmount));
        OnPropertyChanged(nameof(ActualAmount));
        RecalculateTenderSummary();
        RefreshCartValidationStatus();
        NotifyPaymentCommandStates();
    }

    /// <summary>
    /// 原子投影恢复草稿的退款策略、完整 tender 快照和状态消息。
    /// </summary>
    /// <remarks>
    /// 先物化并校验外部快照，再开始改变付款页。普通通知失败时尽力恢复旧投影并把原异常继续抛给 Presenter，
    /// 由 Presenter 负责保持恢复锁；OOM/StackOverflowException 不在这里拦截。
    /// </remarks>
    internal bool TryApplyRecoveredPaymentProjection(
        bool requiresAlternativeRefundMethod,
        IReadOnlyList<PaymentTender>? tenders,
        string? statusMessage)
    {
        var materializedTenders = MaterializeRecoveredPaymentTenders(tenders);
        var previous = new RecoveredPaymentProjectionSnapshot(
            _requiresAlternativeRefundMethod,
            _alternativeRefundCartRevision,
            PaymentTenders.ToArray(),
            _statusKey,
            _statusTextOverride,
            TenderAmountText,
            VoucherCodeText,
            VoucherEntryText,
            IsVoucherEntryDialogOpen,
            SelectedPaymentMethod,
            PaymentMode,
            TotalTendered,
            RemainingAmount,
            ChangeDue,
            _workflowRemainingAmount);

        try
        {
            _requiresAlternativeRefundMethod = requiresAlternativeRefundMethod;
            PaymentTenders.Clear();
            foreach (var tender in materializedTenders)
            {
                PaymentTenders.Add(tender);
            }

            TenderAmountText = string.Empty;
            VoucherCodeText = string.Empty;
            VoucherEntryText = string.Empty;
            IsVoucherEntryDialogOpen = false;

            // 中文注释：恢复已批准 tender 只回填付款页状态，剩余金额仍由收银员补齐后走现有完成订单流程。
            RefreshCart();
            // 空购物车刷新会释放旧的退款限制；恢复结果本身仍必须保留服务端要求的策略。
            _requiresAlternativeRefundMethod = requiresAlternativeRefundMethod;
            _alternativeRefundCartRevision = requiresAlternativeRefundMethod
                ? _cart.Revision
                : null;
            SetStatus(
                materializedTenders.Count > 0
                    ? "payment.status.cardTenderAdded"
                    : requiresAlternativeRefundMethod
                        ? "payment.refund.status.alternativeMethodRequired"
                        : GetReadyStatusKey(),
                statusMessage);
            NotifyPaymentCommandStates();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            RestoreRecoveredPaymentProjectionBestEffort(previous);
            throw;
        }
    }

    internal void RestoreRecoveredPaymentTenders(IReadOnlyList<PaymentTender> tenders, string? statusMessage)
    {
        _ = TryApplyRecoveredPaymentProjection(
            _requiresAlternativeRefundMethod,
            tenders,
            statusMessage);
    }

    private static IReadOnlyList<PaymentTender> MaterializeRecoveredPaymentTenders(
        IReadOnlyList<PaymentTender>? tenders)
    {
        if (tenders is null || tenders.Count == 0)
        {
            return [];
        }

        var materialized = new List<PaymentTender>(tenders.Count);
        for (var index = 0; index < tenders.Count; index++)
        {
            var tender = tenders[index];
            ArgumentNullException.ThrowIfNull(tender, nameof(tenders));
            if (!Enum.IsDefined(tender.Method))
            {
                throw new ArgumentOutOfRangeException(nameof(tenders), tender.Method, "The recovered tender method is invalid.");
            }

            materialized.Add(tender);
        }

        return materialized;
    }

    private void RestoreRecoveredPaymentProjectionBestEffort(RecoveredPaymentProjectionSnapshot snapshot)
    {
        _requiresAlternativeRefundMethod = snapshot.RequiresAlternativeRefundMethod;
        _alternativeRefundCartRevision = snapshot.AlternativeRefundCartRevision;

        TryBestEffortProjectionAction(() => PaymentTenders.Clear());
        foreach (var tender in snapshot.Tenders)
        {
            TryBestEffortProjectionAction(() => PaymentTenders.Add(tender));
        }

        // 中文注释：回滚直接恢复 backing field，避免失败订阅者再次覆盖原异常；通知本身仍逐项尽力发送。
#pragma warning disable MVVMTK0034 // 原子投影失败后的 best-effort 回滚必须绕过可能再次抛错的生成 setter。
        _tenderAmountText = snapshot.TenderAmountText;
        _voucherCodeText = snapshot.VoucherCodeText;
        _voucherEntryText = snapshot.VoucherEntryText;
        _isVoucherEntryDialogOpen = snapshot.IsVoucherEntryDialogOpen;
        _selectedPaymentMethod = snapshot.SelectedPaymentMethod;
        _paymentMode = snapshot.PaymentMode;
        _totalTendered = snapshot.TotalTendered;
        _remainingAmount = snapshot.RemainingAmount;
        _changeDue = snapshot.ChangeDue;
#pragma warning restore MVVMTK0034
        _workflowRemainingAmount = snapshot.WorkflowRemainingAmount;
        _statusKey = snapshot.StatusKey;
        _statusTextOverride = snapshot.StatusTextOverride;

        TryNotifyRecoveredProjectionProperty(nameof(TenderAmountText));
        TryNotifyRecoveredProjectionProperty(nameof(VoucherCodeText));
        TryNotifyRecoveredProjectionProperty(nameof(VoucherEntryText));
        TryNotifyRecoveredProjectionProperty(nameof(IsVoucherEntryDialogOpen));
        TryNotifyRecoveredProjectionProperty(nameof(SelectedPaymentMethod));
        TryNotifyRecoveredProjectionProperty(nameof(PaymentMode));
        TryNotifyRecoveredProjectionProperty(nameof(IsRefundMode));
        TryNotifyRecoveredProjectionProperty(nameof(IsZeroSettlementMode));
        TryNotifyRecoveredProjectionProperty(nameof(IsPaymentMode));
        TryNotifyRecoveredProjectionProperty(nameof(TotalTendered));
        TryNotifyRecoveredProjectionProperty(nameof(RemainingAmount));
        TryNotifyRecoveredProjectionProperty(nameof(ChangeDue));
        TryNotifyRecoveredProjectionProperty(nameof(StatusMessage));
        TryBestEffortProjectionAction(NotifyPaymentCommandStates);
    }

    private void TryNotifyRecoveredProjectionProperty(string propertyName)
    {
        TryBestEffortProjectionAction(() => OnPropertyChanged(propertyName));
    }

    private static void TryBestEffortProjectionAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 回滚通知是 best-effort；OOM/StackOverflowException 仍由过滤器外传播。
        }
    }

    internal bool LastCardPaymentHandoffHadWarning { get; private set; }

    internal bool CompleteCardPaymentHandoff()
    {
        // 安全移交只清理当前活动订单和付款页投影；持久化 attempt、草稿、tender 与终端证据由恢复中心继续持有。
        LastCardPaymentHandoffHadWarning = false;

        void RunPostHandoffAction(string stage, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // 中文注释：集合和 WPF 通知可能在 backing state 已更新后抛错；继续完成可验证的核心 handoff。
                LastCardPaymentHandoffHadWarning = true;
                try
                {
                    ConsoleLog.WriteError(
                        "CardPayment",
                        $"card payment handoff UI action failed stage={stage} error={ex.GetType().Name}",
                        exception: ex);
                }
                catch (Exception logException) when (
                    logException is not OutOfMemoryException and not StackOverflowException)
                {
                    // best-effort 日志订阅者失败不能中断已经提交的付款交接。
                }
            }
        }

        _paymentEntryVersion++;
        _pendingVoucherUploadOrderGuid = null;
        _pendingVoucherTenderedAmount = 0m;
        _pendingVoucherChangeAmount = 0m;
        _requiresAlternativeRefundMethod = false;
        _alternativeRefundCartRevision = null;
        RunPostHandoffAction("clear-tenders", PaymentTenders.Clear);
        RunPostHandoffAction("clear-voucher-code", () => VoucherCodeText = string.Empty);
        RunPostHandoffAction("clear-voucher-entry", () => VoucherEntryText = string.Empty);
        RunPostHandoffAction("close-voucher-dialog", () => IsVoucherEntryDialogOpen = false);
        RunPostHandoffAction("clear-tender-amount", () => TenderAmountText = string.Empty);
        RunPostHandoffAction("select-cash", () => SelectedPaymentMethod = PaymentMethodKind.Cash);
        var activeOverlay = CardPaymentErrorOverlay;
        if (activeOverlay is not null)
        {
            RunPostHandoffAction("close-error-overlay", () => activeOverlay.IsOpen = false);
        }

        RunPostHandoffAction("clear-error-overlay", () => CardPaymentErrorOverlay = null);
        RunPostHandoffAction("clear-cart", _cart.Clear);

        if (PaymentTenders.Count != 0 || !_cart.IsEmpty)
        {
            // 核心集合未清空时 handoff 尚未完成，保留原 recovery identity 与交互锁供重试。
            RunPostHandoffAction("retain-lock", () => IsPaymentInteractionLocked = true);
            return false;
        }

        // 只有核心购物车与 tender 均已确认清空后，才释放当前页的未知结果身份。
        _cardSession.SetResultUnknownRecoveryRequired(false);
        RunPostHandoffAction("refresh-cart", RefreshCart);
        RunPostHandoffAction("unlock-payment", () => IsPaymentInteractionLocked = IsShuttingDown);
        RunPostHandoffAction("ready-status", () => SetStatus(GetReadyStatusKey()));
        RunPostHandoffAction("notify-commands", NotifyPaymentCommandStates);
        RunPostHandoffAction("navigate-pos", () => _navigationActions.BackToPos?.Invoke());
        return true;
    }

    internal CardPaymentHandoffRequest CreateCardPaymentHandoffRequest() =>
        new(
            Session,
            _cart.CreateSnapshot(),
            PaymentTenders.ToArray(),
            ActualAmount,
            _cardSession.RecoveryAttemptKey,
            _cardSession.RecoveryOrderGuid);

    internal void ConfigureCardPaymentHandoff(
        Func<CardPaymentHandoffRequest, Task<CardPaymentHandoffCandidate?>> prepareAsync,
        Func<CardPaymentHandoffCandidate, CardPaymentHandoffRequest, Task<bool>> handoffAsync,
        Action? openRecoveryCenter = null)
    {
        _navigationActions = _navigationActions with
        {
            PrepareCardPaymentHandoffAsync = prepareAsync,
            HandoffCardPaymentAsync = handoffAsync,
            OpenCardRecoveryCenter = openRecoveryCenter
        };
        NotifyPaymentCommandStates();
    }

    private void AppendTenderAmount(string? value)
    {
        if (!IsPaymentInteractionEnabled || _cardSession.IsActive)
        {
            return;
        }

        if (value == "Back")
        {
            TenderAmountText = TenderAmountText.Length > 0 ? TenderAmountText[..^1] : string.Empty;
            return;
        }

        if (value == "Clear")
        {
            TenderAmountText = string.Empty;
            return;
        }

        if (value == "." && TenderAmountText.Contains('.', StringComparison.Ordinal))
        {
            return;
        }

        TenderAmountText += value;
    }

    private async Task ApplyQuickCashAsync(QuickCashOption? option)
    {
        await _tenderController.ApplyQuickCashAsync(
            option,
            amountText => TenderAmountText = amountText,
            AddTenderByMethodAsync);
    }

    private Task OpenVoucherEntryAsync()
    {
        if (!CanOpenVoucherEntry())
        {
            return Task.CompletedTask;
        }

        // 中文注释：退款的代金券由系统在结算阶段新发（见 CashPaymentWorkflowService.IssuePendingRefundVouchersAsync），
        // 没有可扫描/输入的券号；销售用的扫码弹窗在退款时输入会被丢弃，所以直接按剩余应退金额加一笔退券付款项。
        if (IsRefundMode)
        {
            return AddTenderByMethodAsync(PaymentMethodKind.Voucher);
        }

        SelectedPaymentMethod = PaymentMethodKind.Voucher;
        VoucherEntryText = VoucherCodeText;
        IsVoucherEntryDialogOpen = true;
        return Task.CompletedTask;
    }

    private bool CanOpenVoucherEntry()
    {
        return CanAddTender(PaymentMethodKind.Voucher, allowDefaultAmount: true);
    }

    private void ApplyVoucherEntryKey(string? key)
    {
        if (!CanUseVoucherEntryDialog() || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (key.Equals("Back", StringComparison.OrdinalIgnoreCase))
        {
            VoucherEntryText = VoucherEntryText.Length > 0 ? VoucherEntryText[..^1] : string.Empty;
            return;
        }

        if (key.Equals("Clear", StringComparison.OrdinalIgnoreCase))
        {
            VoucherEntryText = string.Empty;
            return;
        }

        if (key.Length == 1 && (char.IsLetterOrDigit(key[0]) || key[0] == '-'))
        {
            VoucherEntryText += key.ToUpperInvariant();
        }
    }

    private async Task ConfirmVoucherEntryAsync()
    {
        if (!IsVoucherEntryDialogOpen)
        {
            return;
        }

        var tenderCountBeforeConfirm = PaymentTenders.Count;
        VoucherCodeText = VoucherEntryText.Trim();

        // 中文注释：确认时复用现有代金券付款命令，空码、重复付款和后端失败都沿用原状态提示。
        await SelectVoucherCommand.ExecuteAsync(null);
        if (PaymentTenders.Count <= tenderCountBeforeConfirm)
        {
            return;
        }

        VoucherEntryText = string.Empty;
        IsVoucherEntryDialogOpen = false;
    }

    private bool CanConfirmVoucherEntry()
    {
        return CanUseVoucherEntryDialog();
    }

    /// <summary>
    /// 代金券弹窗打开但焦点不在券号框（例如刚点过屏幕键盘按钮）时，键盘通道收到的扫码由主窗口转交到这里：
    /// 与扫码直接进输入框一致，只填入券号（按输入框规则转大写），仍由收银员点「确认」添加付款。
    /// </summary>
    internal bool TryAcceptScannedVoucherCode(string barcode)
    {
        var code = barcode.Trim();
        if (code.Length == 0 || !CanUseVoucherEntryDialog())
        {
            return false;
        }

        VoucherEntryText = code.ToUpperInvariant();
        return true;
    }

    /// <summary>付款页当前可以先打开代金券弹窗再扫券码（销售模式且启用了代金券）。</summary>
    internal bool CanScanVoucherAfterOpeningEntry => IsVoucherPaymentVisible && !IsRefundMode;

    private bool CanUseVoucherEntryDialog()
    {
        return IsVoucherPaymentVisible && IsVoucherEntryDialogOpen && IsPaymentInteractionEnabled;
    }

    private void CancelVoucherEntry()
    {
        VoucherEntryText = string.Empty;
        IsVoucherEntryDialogOpen = false;
    }

    private void OpenInstallmentCustomerDialog()
    {
        if (!CanOpenInstallmentCustomerDialog())
        {
            return;
        }

        InstallmentCustomerDraftName = InstallmentCustomerName;
        InstallmentCustomerDraftPhone = InstallmentCustomerPhone;
        InstallmentCustomerEditTarget = "Name";
        IsInstallmentCustomerDialogOpen = true;
    }

    private bool CanOpenInstallmentCustomerDialog()
    {
        return CanEditInstallmentCustomer && IsPaymentInteractionEnabled;
    }

    private void SelectInstallmentCustomerField(string? target)
    {
        if (!IsInstallmentCustomerDialogOpen || string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        if (target.Equals("Phone", StringComparison.OrdinalIgnoreCase))
        {
            InstallmentCustomerEditTarget = "Phone";
            return;
        }

        InstallmentCustomerEditTarget = "Name";
    }

    private void ApplyInstallmentCustomerKey(string? key)
    {
        if (!CanUseInstallmentCustomerDialog() || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (key.Equals("Back", StringComparison.OrdinalIgnoreCase))
        {
            UpdateActiveInstallmentCustomerDraft(value => value.Length > 0 ? value[..^1] : string.Empty);
            return;
        }

        if (key.Equals("Clear", StringComparison.OrdinalIgnoreCase))
        {
            UpdateActiveInstallmentCustomerDraft(_ => string.Empty);
            return;
        }

        if (key.Equals("Space", StringComparison.OrdinalIgnoreCase) && IsInstallmentCustomerNameDraftActive)
        {
            InstallmentCustomerDraftName += " ";
            return;
        }

        if (key.Length != 1)
        {
            return;
        }

        var character = key[0];
        if (IsInstallmentCustomerNameDraftActive && char.IsLetterOrDigit(character))
        {
            InstallmentCustomerDraftName += char.ToUpperInvariant(character);
            return;
        }

        if (IsInstallmentCustomerPhoneDraftActive &&
            (char.IsDigit(character) || character is '+' or '-'))
        {
            InstallmentCustomerDraftPhone += character;
        }
    }

    private bool CanUseInstallmentCustomerDialog()
    {
        return IsInstallmentCustomerDialogOpen && CanEditInstallmentCustomer && IsPaymentInteractionEnabled;
    }

    private void ConfirmInstallmentCustomerDialog()
    {
        if (!IsInstallmentCustomerDialogOpen)
        {
            return;
        }

        InstallmentCustomerName = InstallmentCustomerDraftName.Trim();
        InstallmentCustomerPhone = InstallmentCustomerDraftPhone.Trim();
        IsInstallmentCustomerDialogOpen = false;
    }

    private void CancelInstallmentCustomerDialog()
    {
        InstallmentCustomerDraftName = string.Empty;
        InstallmentCustomerDraftPhone = string.Empty;
        IsInstallmentCustomerDialogOpen = false;
    }

    private void UpdateActiveInstallmentCustomerDraft(Func<string, string> update)
    {
        if (IsInstallmentCustomerPhoneDraftActive)
        {
            InstallmentCustomerDraftPhone = update(InstallmentCustomerDraftPhone);
            return;
        }

        InstallmentCustomerDraftName = update(InstallmentCustomerDraftName);
    }

    private async Task AddTenderByMethodAsync(PaymentMethodKind method)
    {
        if (!EnsurePaymentMethodEnabled(method)) return;
        if (method == PaymentMethodKind.Card && _requiresAlternativeRefundMethod)
        {
            // Square 恢复退款只能改用其他退款方式；即使命令被程序化调用也不能再次进入卡工作流。
            SetStatus("payment.refund.status.alternativeMethodRequired");
            NotifyPaymentCommandStates();
            return;
        }

        if (IsInstallmentPaymentEnabled && PaymentTenders.Count > 0)
        {
            SetStatus("payment.installment.status.singleTenderOnly");
            NotifyPaymentCommandStates();
            return;
        }

        if (!TryApplyAddTenderPlan(
                _tenderController.CreateAddTenderPlan(BuildAddTenderRequest(method)),
                out var amountText,
                out var referenceText))
        {
            return;
        }

        if (IsInstallmentPaymentEnabled &&
            _workflowService.TryParseTenderedAmount(amountText, out var plannedAmount) &&
            !IsInstallmentTenderAmountAllowed(method, plannedAmount, GetPaymentTargetAmount()))
        {
            SetStatus("payment.installment.status.invalidAmount");
            NotifyPaymentCommandStates();
            return;
        }

        if (TrySetRegularPaymentTenderOrderStatus(method, amountText))
        {
            return;
        }

        if (IsRefundMode &&
            _workflowService.TryParseTenderedAmount(amountText, out var plannedRefundAmount) &&
            !IsNonVoucherRefundAmountAllowed(method, plannedRefundAmount))
        {
            SetVoucherFundedRefundRequiredStatus();
            NotifyPaymentCommandStates();
            return;
        }

        var attemptedPaymentAmount = ResolveAttemptedPaymentAmount(amountText);
        using var tenderPermissionGrant = await AuthorizeAsync(
            GetTenderPermission(method),
            $"add-tender-{method.ToString().ToLowerInvariant()}",
            method,
            attemptedPaymentAmount);
        if (tenderPermissionGrant is null)
        {
            return;
        }

        var willAutoCompleteCardPayment = method == PaymentMethodKind.Card &&
            !IsInstallmentPaymentEnabled &&
            (IsPaymentMode || IsRefundMode) &&
            _workflowService.TryParseTenderedAmount(amountText, out var plannedCardAmount) &&
            plannedCardAmount >= RemainingAmount;
        using var confirmPermissionGrant = willAutoCompleteCardPayment
            ? await AuthorizeAsync(
                Permissions.PosTerminal.Payment.Confirm,
                "confirm-payment",
                method,
                attemptedPaymentAmount)
            : null;
        if (willAutoCompleteCardPayment && confirmPermissionGrant is null)
        {
            return;
        }

        // 授权期间可能保存新设置，真正收款前再次检查，防止执行已隐藏的付款方式。
        if (!EnsurePaymentMethodEnabled(method)) return;

        // 中文注释：卡终端阶段只激活收卡授权；确认付款授权到真正完成订单前才单独激活。
        using var tenderAuthorizationActivation = tenderPermissionGrant.Activate();

        PaymentTenderAttemptResult result;
        CancellationTokenSource? cardPaymentCts = null;
        var cardPaymentWasManuallyCancelled = false;
        var isCard = method == PaymentMethodKind.Card;
        var deferInstallmentRepaymentProvider = IsInstallmentRepaymentMode;
        var paymentEntryVersion = _paymentEntryVersion;
        try
        {
            if (isCard && !deferInstallmentRepaymentProvider)
            {
                cardPaymentCts = _cardSession.BeginCardPayment();
            }

            if (IsRefundMode && isCard)
            {
                ConsoleLog.Write(
                    "CardRefund",
                    $"payment view add card refund amountText={LogValue(amountText)} originalReference={LogValue(referenceText)} " +
                    $"tenders={PaymentTenders.Count} capacities={_cart.ReturnPaymentCapacities.Count}");
            }

            result = deferInstallmentRepaymentProvider
                ? CreateDeferredInstallmentRepaymentTender(method, amountText, referenceText)
                : await _workflowService.AddTenderAsync(
                    method,
                    Session,
                    GetPaymentTargetAmount(),
                    PaymentTenders.ToList(),
                    amountText,
                    referenceText,
                    isCard ? cardPaymentCts?.Token ?? CancellationToken.None : CancellationToken.None,
                    isCard ? _cart.CreateSnapshot() : null);
            if (isCard && cardPaymentCts?.IsCancellationRequested == true)
            {
                cardPaymentWasManuallyCancelled = _cardSession.IsManualCancellation(cardPaymentCts);
            }
        }
        catch (OperationCanceledException ex) when (isCard)
        {
            if (!IsCurrentPaymentEntry(paymentEntryVersion))
            {
                return;
            }

            var wasManuallyCancelled = _cardSession.IsManualCancellation(cardPaymentCts);
            if (!wasManuallyCancelled)
            {
                var correlation = OperationAuditEvents.CreateCorrelation();
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.PaymentTenderAdd,
                    "Failed",
                    Session,
                    OperationAuditEvents.CaptureCart(_cart.Lines),
                    reasonCode: "CARD_TIMEOUT",
                    safeMessage: ex.GetType().Name,
                    paymentMethod: PaymentMethodKind.Card.ToString(),
                    paymentAmount: attemptedPaymentAmount,
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
                ConsoleLog.WriteError(
                    "OperationAudit",
                    $"card tender cancelled without manual request error={ex.GetType().Name}",
                    new ApplicationLogContext(TraceId: correlation.TraceId),
                    ex);
            }

            _cardSession.HandleOperationCanceledException(cardPaymentCts, paymentEntryVersion);
            await ReleaseVoucherTendersAfterCardFailureAsync();
            return;
        }
        // 中文注释：卡终端致命异常绕过普通失败处理，确保原异常实例继续向上传播。
        catch (Exception ex) when (
            isCard &&
            ex is not OperationCanceledException &&
            ex is not OutOfMemoryException &&
            ex is not StackOverflowException)
        {
            if (!IsCurrentPaymentEntry(paymentEntryVersion))
            {
                return;
            }

            var correlation = OperationAuditEvents.CreateCorrelation();
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.PaymentTenderAdd,
                "Failed",
                Session,
                OperationAuditEvents.CaptureCart(_cart.Lines),
                reasonCode: "CARD_EXCEPTION",
                safeMessage: ex.GetType().Name,
                paymentMethod: PaymentMethodKind.Card.ToString(),
                paymentAmount: attemptedPaymentAmount,
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            ConsoleLog.WriteError(
                "OperationAudit",
                $"card tender failed error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            _cardSession.HandleUnexpectedException(ex, paymentEntryVersion);
            await ReleaseVoucherTendersAfterCardFailureAsync();
            return;
        }
        catch (OperationCanceledException ex)
        {
            var correlation = OperationAuditEvents.CreateCorrelation();
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.PaymentTenderAdd,
                "Failed",
                Session,
                OperationAuditEvents.CaptureCart(_cart.Lines),
                reasonCode: "TENDER_CANCELLED",
                safeMessage: ex.GetType().Name,
                paymentMethod: method.ToString(),
                paymentAmount: attemptedPaymentAmount,
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            ConsoleLog.WriteError(
                "OperationAudit",
                $"tender cancelled unexpectedly method={method} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            SetStatus("payment.status.tenderFailed");
            NotifyPaymentCommandStates();
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var correlation = OperationAuditEvents.CreateCorrelation();
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.PaymentTenderAdd,
                "Failed",
                Session,
                OperationAuditEvents.CaptureCart(_cart.Lines),
                reasonCode: "TENDER_EXCEPTION",
                safeMessage: ex.GetType().Name,
                paymentMethod: method.ToString(),
                paymentAmount: attemptedPaymentAmount,
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            // 服务端 4xx（如券不可用、余额不足）是收银员可见并可重试的业务拒绝，记 Warning；其余异常仍为 Error。
            var tenderFailureContext = new ApplicationLogContext(
                TraceId: correlation.TraceId,
                StatusCode: ex is CatalogApiException { StatusCode: { } tenderStatus } ? (int)tenderStatus : null,
                Properties: new Dictionary<string, object?>
                {
                    ["errorCode"] = (ex as CatalogApiException)?.ErrorCode
                });
            if (ex is CatalogApiException { StatusCode: { } clientStatus } && (int)clientStatus is >= 400 and < 500)
            {
                ConsoleLog.WriteWarning(
                    "OperationAudit",
                    $"tender rejected method={method} error={ex.GetType().Name} http={(int)clientStatus}",
                    tenderFailureContext,
                    ex);
            }
            else
            {
                ConsoleLog.WriteError(
                    "OperationAudit",
                    $"tender failed method={method} error={ex.GetType().Name}",
                    tenderFailureContext,
                    ex);
            }
            SetStatus("payment.status.tenderFailed");
            NotifyPaymentCommandStates();
            return;
        }
        finally
        {
            if (isCard && !deferInstallmentRepaymentProvider)
            {
                _cardSession.EndCardPayment(cardPaymentCts);
            }
        }

        if (!IsCurrentPaymentEntry(paymentEntryVersion))
        {
            return;
        }

        if (isCard &&
            cardPaymentCts?.IsCancellationRequested == true &&
            (!result.Succeeded || result.Tender is null))
        {
            await _cardSession.TryHandleCancelledResultAsync(result, cardPaymentCts, cardPaymentWasManuallyCancelled);
            if (result.CardResult?.RequiresRecovery != true)
            {
                await ReleaseVoucherTendersAfterCardFailureAsync();
            }
            return;
        }

        if (!result.Succeeded || result.Tender is null)
        {
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.PaymentTenderAdd,
                "Failed",
                Session,
                OperationAuditEvents.CaptureCart(_cart.Lines),
                reasonCode: result.StatusKey,
                safeMessage: result.StatusMessage,
                paymentMethod: method.ToString(),
                paymentAmount: attemptedPaymentAmount);
            if (IsRefundMode && isCard)
            {
                ConsoleLog.Write(
                    "CardRefund",
                    $"payment view card refund tender failed statusKey={result.StatusKey} message={LogValue(result.StatusMessage)}");
            }

            if (isCard)
            {
                var resultUnknown = result.CardResult?.RequiresRecovery == true;
                await _cardSession.TryHandleFailedResultAsync(result);
                if (!resultUnknown)
                {
                    await ReleaseVoucherTendersAfterCardFailureAsync();
                }

                return;
            }

            SetStatus(result.StatusKey, result.StatusMessage);
            NotifyPaymentCommandStates();
            return;
        }

        if (isCard)
        {
            _cardSession.ResetManualCancellationState();
            _cardSession.SetResultUnknownRecoveryRequired(false);
        }

        PaymentTenders.Add(result.Tender);
        OperationAuditEvents.RecordAction(
            _operationAuditLogger,
            OperationAuditTypes.PaymentTenderAdd,
            "Succeeded",
            Session,
            OperationAuditEvents.CaptureCart(_cart.Lines),
            reasonCode: IsRefundMode ? "REFUND" : "SALE",
            paymentMethod: result.Tender.Method.ToString(),
            paymentAmount: result.Tender.Amount);
        if (method == PaymentMethodKind.Voucher)
        {
            VoucherCodeText = string.Empty;
        }

        TenderAmountText = string.Empty;
        RecalculateTenderSummary();
        SetStatus(result.StatusKey, result.StatusMessage);
        NotifyPaymentCommandStates();
        if (isCard &&
            !IsInstallmentPaymentEnabled &&
            !cardPaymentWasManuallyCancelled &&
            IsSettlementComplete() &&
            (IsPaymentMode || IsRefundMode))
        {
            tenderAuthorizationActivation.Dispose();
            IDisposable? confirmAuthorizationActivation;
            try
            {
                confirmAuthorizationActivation = confirmPermissionGrant?.Activate();
            }
            catch (ObjectDisposedException)
            {
                // 中文注释：长时间卡交易期间授权可能被全局撤销；完成订单前必须重新确认，不能绕过权限。
                using var refreshedConfirmPermissionGrant = await AuthorizeAsync(
                    Permissions.PosTerminal.Payment.Confirm,
                    "confirm-payment",
                    method,
                    attemptedPaymentAmount);
                if (refreshedConfirmPermissionGrant is null)
                {
                    return;
                }

                using var refreshedConfirmAuthorizationActivation = refreshedConfirmPermissionGrant.Activate();
                await CompletePaymentFromTendersAsync();
                return;
            }

            using (confirmAuthorizationActivation)
            {
                await CompletePaymentFromTendersAsync();
            }
        }
    }

    private bool TrySetRegularPaymentTenderOrderStatus(PaymentMethodKind method, string? amountText)
    {
        if (!IsPaymentMode)
        {
            return false;
        }

        if (method == PaymentMethodKind.Voucher &&
            PaymentTenders.Any(tender => tender.Method == PaymentMethodKind.Cash))
        {
            SetStatus("payment.status.paymentMethodOrder");
            NotifyPaymentCommandStates();
            return true;
        }

        if (!IsInstallmentPaymentEnabled &&
            method == PaymentMethodKind.Card &&
            _workflowService.TryParseTenderedAmount(amountText, out var tenderAmount) &&
            tenderAmount > 0m &&
            // 已收够（含现金超收）时剩余应收为 0，不能再追加卡笔；
            // 未收够时仍要求卡是足额的最后一笔。
            (IsSettlementComplete() || tenderAmount < RemainingAmount))
        {
            SetStatus("payment.status.cardMustBeFinalTender");
            NotifyPaymentCommandStates();
            return true;
        }

        // 中文注释：恢复出的 partial card tender 只允许补齐差额，是否结清仍由完成付款校验兜底。
        return false;
    }

    private async Task<bool> ReleaseVoucherTendersAfterCardFailureAsync()
    {
        if (!IsPaymentMode)
        {
            return true;
        }

        var voucherTenders = PaymentTenders
            .Where(tender => tender.Method == PaymentMethodKind.Voucher)
            .ToList();
        if (voucherTenders.Count == 0)
        {
            return true;
        }

        foreach (var tender in voucherTenders)
        {
            bool released;
            try
            {
                released = await _workflowService.ReleaseVoucherTenderAsync(tender, Session);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                var correlation = OperationAuditEvents.CreateCorrelation();
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.PaymentTenderRemove,
                    "Failed",
                    Session,
                    OperationAuditEvents.CaptureCart(_cart.Lines),
                    reasonCode: "CARD_FAILURE_RELEASE_EXCEPTION",
                    safeMessage: ex.GetType().Name,
                    paymentMethod: tender.Method.ToString(),
                    paymentAmount: tender.Amount,
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
                ConsoleLog.WriteError(
                    "OperationAudit",
                    $"automatic voucher release failed error={ex.GetType().Name}",
                    new ApplicationLogContext(TraceId: correlation.TraceId),
                    ex);
                throw;
            }

            if (!released)
            {
                SetStatus("payment.status.voucherReleaseFailed");
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.PaymentTenderRemove,
                    "Failed",
                    Session,
                    OperationAuditEvents.CaptureCart(_cart.Lines),
                    reasonCode: "CARD_FAILURE_RELEASE_FAILED",
                    safeMessage: StatusMessage,
                    paymentMethod: tender.Method.ToString(),
                    paymentAmount: tender.Amount);
                NotifyPaymentCommandStates();
                return false;
            }

            PaymentTenders.Remove(tender);
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.PaymentTenderRemove,
                "Succeeded",
                Session,
                OperationAuditEvents.CaptureCart(_cart.Lines),
                reasonCode: "CARD_FAILURE_AUTO_RELEASE",
                paymentMethod: tender.Method.ToString(),
                paymentAmount: tender.Amount);
        }

        RecalculateTenderSummary();
        TenderAmountText = string.Empty;
        SetStatus("payment.status.voucherReleased");
        NotifyPaymentCommandStates();
        return true;
    }

    private async Task RemoveTenderAsync(PaymentTender? tender)
    {
        var tenderSnapshot = tender;
        using var permissionGrant = await AuthorizeAsync(
            Permissions.PosTerminal.Payment.RemoveTender,
            "remove-tender",
            tenderSnapshot?.Method,
            tenderSnapshot?.Amount);
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        tender = tenderSnapshot;
        if (tender is null)
        {
            return;
        }

        if (_pendingVoucherUploadOrderGuid is not null)
        {
            SetStatus("payment.status.retryVoucherUpload");
            return;
        }

        if (IsRecoveredCardAttemptTender(tender))
        {
            // PaymentTenders 中的银行卡项都已经过终端批准，不能像现金 tender 一样删除后重新刷卡。
            return;
        }

        if (IsPaymentMode && tender.Method == PaymentMethodKind.Voucher)
        {
            bool released;
            try
            {
                released = await _workflowService.ReleaseVoucherTenderAsync(tender, Session);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                var correlation = OperationAuditEvents.CreateCorrelation();
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.PaymentTenderRemove,
                    "Failed",
                    Session,
                    OperationAuditEvents.CaptureCart(_cart.Lines),
                    reasonCode: "VOUCHER_RELEASE_EXCEPTION",
                    safeMessage: ex.GetType().Name,
                    paymentMethod: tender.Method.ToString(),
                    paymentAmount: tender.Amount,
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
                ConsoleLog.WriteError(
                    "OperationAudit",
                    $"manual voucher release failed error={ex.GetType().Name}",
                    new ApplicationLogContext(TraceId: correlation.TraceId),
                    ex);
                throw;
            }

            if (!released)
            {
                SetStatus("payment.status.voucherReleaseFailed");
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.PaymentTenderRemove,
                    "Failed",
                    Session,
                    OperationAuditEvents.CaptureCart(_cart.Lines),
                    reasonCode: "VOUCHER_RELEASE_FAILED",
                    safeMessage: StatusMessage,
                    paymentMethod: tender.Method.ToString(),
                    paymentAmount: tender.Amount);
                NotifyPaymentCommandStates();
                return;
            }

            RemoveTenderFromList(tender, "payment.status.voucherReleased");
            RecordTenderRemoved(tender);
            return;
        }

        RemoveTenderFromList(tender, "payment.status.tenderRemoved");
        RecordTenderRemoved(tender);
    }

    private void RecordTenderRemoved(PaymentTender tender)
    {
        OperationAuditEvents.RecordAction(
            _operationAuditLogger,
            OperationAuditTypes.PaymentTenderRemove,
            "Succeeded",
            Session,
            OperationAuditEvents.CaptureCart(_cart.Lines),
            reasonCode: IsRefundMode ? "REFUND" : "SALE",
            paymentMethod: tender.Method.ToString(),
            paymentAmount: tender.Amount);
    }

    private void RemoveTenderFromList(PaymentTender tender, string statusKey)
    {
        PaymentTenders.Remove(tender);
        RecalculateTenderSummary();
        TenderAmountText = string.Empty;
        SetStatus(statusKey);
        NotifyPaymentCommandStates();
    }

    private bool CanRemoveTender(PaymentTender? tender)
    {
        return tender is not null &&
            IsPaymentInteractionEnabled &&
            !_cardSession.IsActive &&
            _pendingVoucherUploadOrderGuid is null &&
            !IsRecoveredCardAttemptTender(tender);
    }

    private static bool IsRecoveredCardAttemptTender(PaymentTender tender)
    {
        return tender.Method == PaymentMethodKind.Card;
    }

    private async Task ConfirmPaymentAsync()
    {
        if (IsManualCardDialogOpen) return;
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Payment.Confirm, "confirm-payment");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        if (IsPaymentInteractionLocked)
        {
            return;
        }

        if (TrySetBlockingCartIssueStatus())
        {
            ConfirmPaymentCommand.NotifyCanExecuteChanged();
            return;
        }

        if (_pendingVoucherUploadOrderGuid is not null)
        {
            await RetryPendingVoucherUploadAsync();
            return;
        }

        if (IsInstallmentPaymentEnabled)
        {
            await ConfirmInstallmentPaymentAsync();
            return;
        }

        if (TrySetOfflineVoucherRefundTenderStatus())
        {
            return;
        }

        // 兜底：恢复出的付款明细等路径若让现金/刷卡挤占了代金券应退部分，完成前必须拦下。
        if (TrySetVoucherFundedRefundShortfallStatus())
        {
            return;
        }

        if (!IsZeroSettlementMode && PaymentTenders.Count == 0)
        {
            SetStatus(GetNoTendersStatusKey());
            return;
        }

        if (!IsSettlementComplete())
        {
            SetStatus(GetIncompleteSettlementStatusKey());
            return;
        }

        await CompletePaymentFromTendersAsync();
    }

    private async Task CompletePaymentFromTendersAsync()
    {
        if (IsPaymentInteractionLocked)
        {
            return;
        }

        // 中文注释：自动完成和人工确认共用此入口，首次等待前同步上锁，防止同一订单并发结算。
        IsPaymentInteractionLocked = true;
        var preservePaymentInteractionLock = false;
        try
        {
            await CompletePaymentFromTendersCoreAsync();
        }
        catch (Exception ex) when (ex is OutOfMemoryException or StackOverflowException)
        {
            // 中文注释：致命异常必须原样传播，同时保留页面锁，避免异常后继续操作当前付款。
            preservePaymentInteractionLock = true;
            throw;
        }
        finally
        {
            // 结算期间若发现已批准卡款无法安全落盘，finally 不能覆盖待恢复锁。
            IsPaymentInteractionLocked =
                preservePaymentInteractionLock ||
                IsManualCardSavePending ||
                IsShuttingDown ||
                _cardSession.HasUnknownResult;
        }
    }

    internal void SetCurrentCardRecoveryRequired(bool required, string? statusMessage = null)
    {
        _cardSession.SetResultUnknownRecoveryRequired(required);
        IsPaymentInteractionLocked = required || IsShuttingDown || _cardSession.IsActive;
        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            SetStatus(required ? "payment.card.resultUnknown" : GetReadyStatusKey(), statusMessage);
        }

        NotifyPaymentCommandStates();
    }

    internal void SetAlternativeRefundMethodRequired(bool required)
    {
        _requiresAlternativeRefundMethod = required;
        _alternativeRefundCartRevision = required ? _cart.Revision : null;
        if (required)
        {
            SetStatus("payment.refund.status.alternativeMethodRequired");
        }

        NotifyPaymentCommandStates();
    }

    private async Task CompletePaymentFromTendersCoreAsync()
    {
        if (TrySetOfflineVoucherRefundTenderStatus())
        {
            return;
        }

        var before = OperationAuditEvents.CaptureCart(_cart.Lines);
        var tenderSnapshot = PaymentTenders.ToList();
        var isRefundMode = IsRefundMode;
        var operationType = isRefundMode
            ? OperationAuditTypes.ReturnRefundComplete
            : OperationAuditTypes.SaleComplete;
        var cashTenderedAmount = tenderSnapshot
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount);
        CashPaymentWorkflowResult result;
        try
        {
            result = await _workflowService.CompletePaymentAsync(
                _cart,
                Session,
                tenderSnapshot,
                cashTenderedAmount);
        }
        catch (CardPaymentPersistenceUnknownException ex)
        {
            if (tenderSnapshot.Any(tender => ManualCardPaymentReference.IsManual(tender.Reference)))
            {
                // 手动款只能重试保存同一订单，不能转到 Linkly/Square 查询或让收银员再次刷卡。
                IsManualCardSavePending = true;
                IsPaymentInteractionLocked = true;
                SetStatus("payment.manualCard.saveFailed");
                return;
            }
            _cardSession.SetPersistenceRecoveryOrder(ex.OrderGuid);
            // 中文注释：先保留付款锁与恢复入口，诊断写入失败不能丢失已批准卡款的待恢复状态。
            SetCurrentCardRecoveryRequired(true, ex.Message);
            try
            {
                var correlation = OperationAuditEvents.CreateCorrelation();
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    operationType,
                    "RequiresReview",
                    Session,
                    before,
                    reasonCode: "CARD_PERSISTENCE_UNKNOWN",
                    safeMessage: ex.InnerException?.GetType().Name ?? ex.GetType().Name,
                    paymentMethod: FormatPaymentMethods(tenderSnapshot),
                    paymentAmount: tenderSnapshot.Sum(tender => tender.Amount),
                    orderGuid: ex.OrderGuid.ToString("D"),
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
                ConsoleLog.WriteError(
                    "OperationAudit",
                    $"approved card order persistence unknown operation={operationType} error={ex.InnerException?.GetType().Name ?? ex.GetType().Name}",
                    new ApplicationLogContext(TraceId: correlation.TraceId),
                    ex);
            }
            catch (Exception logException) when (logException is not OutOfMemoryException and not StackOverflowException)
            {
                // 诊断失败仍保留交易、金额及冻结订单身份，用户可从原页面重试恢复。
            }
            return;
        }
        catch (PaymentUploadFailedException ex)
        {
            var correlation = OperationAuditEvents.CreateCorrelation();
            // 付款未完成时购物车没有变化，但失败审计仍必须保留完整商品和有符号金额快照。
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                operationType,
                "Failed",
                Session,
                before,
                reasonCode: "UPLOAD_FAILED",
                safeMessage: ex.GetType().Name,
                paymentMethod: FormatPaymentMethods(tenderSnapshot),
                paymentAmount: ex.TenderedAmount,
                orderGuid: ex.OrderGuid.ToString("D"),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            ConsoleLog.WriteError(
                "OperationAudit",
                $"payment completion upload failed operation={operationType} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            _pendingVoucherUploadOrderGuid = ex.OrderGuid;
            _pendingVoucherTenderedAmount = ex.TenderedAmount;
            _pendingVoucherChangeAmount = ex.ChangeAmount;
            SetStatus("payment.status.uploadFailed", ex.Message);
            NotifyPaymentCommandStates();
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var correlation = OperationAuditEvents.CreateCorrelation();
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                operationType,
                "Failed",
                Session,
                before,
                reasonCode: "TECHNICAL_FAILURE",
                safeMessage: ex.GetType().Name,
                paymentMethod: FormatPaymentMethods(tenderSnapshot),
                paymentAmount: tenderSnapshot.Sum(tender => tender.Amount),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            ConsoleLog.WriteError(
                "OperationAudit",
                $"payment completion failed operation={operationType} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            SetStatus("payment.status.paymentFailed");
            NotifyPaymentCommandStates();
            return;
        }

        OperationAuditEvents.RecordCartChange(
            _operationAuditLogger,
            operationType,
            Session,
            before,
            OperationAuditEvents.CaptureCart(_cart.Lines),
            reasonCode: isRefundMode ? "REFUND" : "SALE",
            paymentMethod: FormatPaymentMethods(tenderSnapshot),
            paymentAmount: result.TenderedAmount,
            orderGuid: result.Order.OrderGuid.ToString("D"));
        CompleteSuccessfulPayment(result);
    }

    private async Task ConfirmInstallmentPaymentAsync()
    {
        if (!ValidateInstallmentConfirmation(out var tender))
        {
            NotifyPaymentCommandStates();
            return;
        }

        // 分期续付的 tender 尚未扣款，首次提交仍须遵守开关；已提交的幂等操作保留原身份核对结果。
        if (IsInstallmentRepaymentMode &&
            (_installmentDraftPaymentGuid is null || !string.Equals(_installmentDraftFingerprint,
                CreateInstallmentPaymentFingerprint(GetInstallmentAppliedTender(tender)), StringComparison.Ordinal)) &&
            !EnsurePaymentMethodEnabled(tender.Method)) return;

        IsPaymentInteractionLocked = true;
        var preservePaymentInteractionLock = false;
        var auditCart = OperationAuditEvents.CaptureCart(_cart.Lines);
        try
        {
            var appliedTender = GetInstallmentAppliedTender(tender);
            if (_installmentRepaymentOrder is { } repaymentOrder)
            {
                InstallmentOrderActionResult repaymentResult;
                try
                {
                    repaymentResult = await _installmentOrderService.AddRepaymentAsync(
                        new InstallmentOrderRepaymentRequest(
                            repaymentOrder.OrderId,
                            Session,
                            CreateInstallmentPaymentDraft(appliedTender)));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    var correlation = OperationAuditEvents.CreateCorrelation();
                    OperationAuditEvents.RecordAction(
                        _operationAuditLogger,
                        OperationAuditTypes.InstallmentRepaymentComplete,
                        "Failed",
                        Session,
                        auditCart,
                        reasonCode: "REPAYMENT",
                        safeMessage: ex.GetType().Name,
                        paymentMethod: appliedTender.Method.ToString(),
                        paymentAmount: appliedTender.Amount,
                        orderGuid: repaymentOrder.OrderId.ToString("D"),
                        correlationId: correlation.CorrelationId,
                        traceId: correlation.TraceId);
                    ConsoleLog.WriteError(
                        "InstallmentAudit",
                        $"installment repayment submission failed error={ex.GetType().Name}",
                        new ApplicationLogContext(TraceId: correlation.TraceId),
                        ex);
                    throw;
                }

                if (!repaymentResult.Succeeded)
                {
                    OperationAuditEvents.RecordAction(
                        _operationAuditLogger,
                        OperationAuditTypes.InstallmentRepaymentComplete,
                        "Failed",
                        Session,
                        auditCart,
                        reasonCode: "REPAYMENT",
                        safeMessage: repaymentResult.Message,
                        paymentMethod: appliedTender.Method.ToString(),
                        paymentAmount: appliedTender.Amount,
                        orderGuid: repaymentOrder.OrderId.ToString("D"));
                    if (!repaymentResult.RequiresReview)
                    {
                        // 明确拒绝/claim busy 没有不确定 provider 副作用，可丢弃本次草稿并让下一次生成新的 operation。
                        ResetInstallmentPaymentDraft();
                        PaymentTenders.Clear();
                        RecalculateTenderSummary();
                    }
                    SetStatus("payment.installment.status.actionFailed", repaymentResult.Message);
                    return;
                }

                var completedOrder = repaymentResult.Order ?? repaymentOrder;
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.InstallmentRepaymentComplete,
                    "Succeeded",
                    Session,
                    OperationAuditEvents.CaptureCart(_cart.Lines),
                    reasonCode: "REPAYMENT",
                    paymentMethod: appliedTender.Method.ToString(),
                    paymentAmount: appliedTender.Amount,
                    orderGuid: completedOrder.OrderId.ToString("D"));
                ResetInstallmentPaymentDraft();
                PaymentTenders.Clear();
                _installmentRepaymentOrder = completedOrder;
                TenderAmountText = string.Empty;
                RecalculateTenderSummary();
                SetStatus("payment.installment.status.repaymentRecorded", repaymentResult.Message);
                OnPropertyChanged(nameof(InstallmentOrderInfoText));
                if (_navigationActions.InstallmentOrderCreatedAsync is not null)
                {
                    // 分期补录成功后复用外层自动打印和回 POS 流程，避免收银员继续停在已完成的补录页。
                    _installmentRepaymentOrder = null;
                    IsInstallmentSwitchLocked = false;
                    IsInstallmentPaymentEnabled = false;
                    InstallmentCustomerName = string.Empty;
                    InstallmentCustomerPhone = string.Empty;
                    await _navigationActions.InstallmentOrderCreatedAsync(completedOrder);
                }

                return;
            }

            var cartSnapshot = CreateInstallmentCartSnapshot();
            if (appliedTender.Amount >= GetPaymentTargetAmount() &&
                _navigationActions.ConfirmInstallmentFullFirstPaymentAsync is { } confirmAsync &&
                !await confirmAsync())
            {
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.InstallmentRepaymentCancel,
                    "Succeeded",
                    Session,
                    auditCart,
                    reasonCode: "FULL_FIRST_PAYMENT_CANCELLED",
                    paymentMethod: appliedTender.Method.ToString(),
                    paymentAmount: appliedTender.Amount);
                // 中文注释：首付已可结清整单时先二次确认；确认后仍按分期创建，取消则保留当前收款状态。
                SetStatus("payment.installment.status.fullFirstPaymentCancelled");
                return;
            }

            InstallmentOrderCreateResult createResult;
            try
            {
                createResult = await _installmentOrderService.CreateOrderAsync(
                    new InstallmentOrderCreateRequest(
                        Session,
                        cartSnapshot,
                        InstallmentCustomerName.Trim(),
                        InstallmentCustomerPhone.Trim(),
                        appliedTender.Amount,
                        CreateInstallmentPaymentDraft(appliedTender),
                        string.Empty));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                var correlation = OperationAuditEvents.CreateCorrelation();
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.SaleComplete,
                    "Failed",
                    Session,
                    auditCart,
                    reasonCode: "INSTALLMENT_CREATE",
                    safeMessage: ex.GetType().Name,
                    paymentMethod: $"Installment+{appliedTender.Method}",
                    paymentAmount: appliedTender.Amount,
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
                ConsoleLog.WriteError(
                    "InstallmentAudit",
                    $"installment order submission failed error={ex.GetType().Name}",
                    new ApplicationLogContext(TraceId: correlation.TraceId),
                    ex);
                throw;
            }

            if (!createResult.Succeeded)
            {
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.SaleComplete,
                    "Failed",
                    Session,
                    auditCart,
                    reasonCode: "INSTALLMENT_CREATE",
                    safeMessage: createResult.Message,
                    paymentMethod: $"Installment+{appliedTender.Method}",
                    paymentAmount: appliedTender.Amount);
                SetStatus("payment.installment.status.actionFailed", createResult.Message);
                return;
            }

            OperationAuditEvents.RecordCartChange(
                _operationAuditLogger,
                OperationAuditTypes.SaleComplete,
                Session,
                auditCart,
                new OperationAuditCartSnapshot(0m, 0m, 0m, []),
                reasonCode: "INSTALLMENT_CREATE",
                paymentMethod: $"Installment+{appliedTender.Method}",
                paymentAmount: appliedTender.Amount,
                orderGuid: createResult.Order?.OrderId.ToString("D"));
            ResetInstallmentPaymentDraft();
            PaymentTenders.Clear();
            _cart.Clear();
            TenderAmountText = string.Empty;
            if (_navigationActions.InstallmentOrderCreatedAsync is not null && createResult.Order is { } createdOrder)
            {
                // 分期首单由外层统一打印小票并回到 POS，付款页不再停留在补款模式。
                _installmentRepaymentOrder = null;
                IsInstallmentSwitchLocked = false;
                IsInstallmentPaymentEnabled = false;
                InstallmentCustomerName = string.Empty;
                InstallmentCustomerPhone = string.Empty;
                RefreshCart();
                SetStatus("payment.installment.status.created", createResult.Message);
                OnPropertyChanged(nameof(InstallmentOrderInfoText));
                await _navigationActions.InstallmentOrderCreatedAsync(createdOrder);
                return;
            }

            if (createResult.Order is { CanAddRepayment: true } nextOrder)
            {
                // 中文注释：首付成功后转为原分期单补款模式，后续付款都写回同一张分期单。
                _installmentRepaymentOrder = nextOrder;
                InstallmentCustomerName = nextOrder.CustomerName;
                InstallmentCustomerPhone = nextOrder.CustomerPhone;
                IsInstallmentPaymentEnabled = true;
                IsInstallmentSwitchLocked = true;
            }
            else
            {
                _installmentRepaymentOrder = null;
                IsInstallmentSwitchLocked = false;
                IsInstallmentPaymentEnabled = false;
                InstallmentCustomerName = string.Empty;
                InstallmentCustomerPhone = string.Empty;
            }

            RefreshCart();
            SetStatus("payment.installment.status.created", createResult.Message);
            OnPropertyChanged(nameof(InstallmentOrderInfoText));
        }
        catch (Exception ex) when (ex is OutOfMemoryException or StackOverflowException)
        {
            // 中文注释：分期提交的致命异常必须原样传播，并保持付款页锁定，避免继续操作金融状态未确认的订单。
            preservePaymentInteractionLock = true;
            throw;
        }
        finally
        {
            if (!preservePaymentInteractionLock)
            {
                IsPaymentInteractionLocked = false;
                NotifyPaymentCommandStates();
            }
        }
    }

    private bool ValidateInstallmentConfirmation(out PaymentTender tender)
    {
        tender = null!;

        if (!Session.IsOnline)
        {
            SetStatus("payment.installment.status.onlineRequired");
            return false;
        }

        if (PaymentTenders.Count != 1)
        {
            SetStatus(GetNoTendersStatusKey());
            return false;
        }

        tender = PaymentTenders[0];
        var targetAmount = GetPaymentTargetAmount();
        if (!IsInstallmentTenderAmountAllowed(tender.Method, tender.Amount, targetAmount))
        {
            SetStatus("payment.installment.status.invalidAmount");
            return false;
        }

        if (_installmentRepaymentOrder is { } repaymentOrder)
        {
            if (!repaymentOrder.CanAddRepayment || repaymentOrder.OutstandingAmount <= 0m)
            {
                SetStatus("payment.installment.status.noRepaymentAllowed");
                return false;
            }

            return true;
        }

        if (_cart.IsEmpty || _cart.HasNonIntegerQuantity || _cart.HasZeroPriceLine)
        {
            TrySetBlockingCartIssueStatus();
            return false;
        }

        if (ActualAmount < MinimumInstallmentTotalAmount)
        {
            SetStatus("payment.installment.status.totalBelowMinimum");
            return false;
        }

        if (string.IsNullOrWhiteSpace(InstallmentCustomerName) ||
            string.IsNullOrWhiteSpace(InstallmentCustomerPhone))
        {
            SetStatus("payment.installment.status.customerRequired");
            return false;
        }

        if (GetInstallmentAppliedPaymentAmount(tender) < MinimumInstallmentFirstPaymentAmount)
        {
            SetStatus("payment.installment.status.firstPaymentBelowMinimum");
            return false;
        }

        return true;
    }

    private bool CanConfirmInstallmentPayment()
    {
        var tender = PaymentTenders.Count == 1 ? PaymentTenders[0] : null;
        if (!Session.IsOnline ||
            PaymentTenders.Count != 1 ||
            tender is null ||
            !IsInstallmentTenderAmountAllowed(tender.Method, tender.Amount, GetPaymentTargetAmount()))
        {
            return false;
        }

        if (_installmentRepaymentOrder is { } repaymentOrder)
        {
            return repaymentOrder.CanAddRepayment && repaymentOrder.OutstandingAmount > 0m;
        }

        return !_cart.IsEmpty &&
            !_cart.HasNonIntegerQuantity &&
            !_cart.HasZeroPriceLine &&
            ActualAmount >= MinimumInstallmentTotalAmount &&
            GetInstallmentAppliedPaymentAmount(tender) >= MinimumInstallmentFirstPaymentAmount &&
            !string.IsNullOrWhiteSpace(InstallmentCustomerName) &&
            !string.IsNullOrWhiteSpace(InstallmentCustomerPhone);
    }

    private async Task RetryPendingVoucherUploadAsync()
    {
        if (_pendingVoucherUploadOrderGuid is null)
        {
            return;
        }

        var before = OperationAuditEvents.CaptureCart(_cart.Lines);
        var tenderSnapshot = PaymentTenders.ToList();
        var isRefundMode = IsRefundMode;
        var operationType = isRefundMode
            ? OperationAuditTypes.ReturnRefundComplete
            : OperationAuditTypes.SaleComplete;
        try
        {
            var result = await _workflowService.RetryVoucherUploadAsync(
                _pendingVoucherUploadOrderGuid.Value,
                _cart,
                Session,
                _pendingVoucherTenderedAmount,
                _pendingVoucherChangeAmount);
            OperationAuditEvents.RecordCartChange(
                _operationAuditLogger,
                operationType,
                Session,
                before,
                OperationAuditEvents.CaptureCart(_cart.Lines),
                reasonCode: "UPLOAD_RETRY_SUCCEEDED",
                paymentMethod: FormatPaymentMethods(tenderSnapshot),
                paymentAmount: result.TenderedAmount,
                orderGuid: result.Order.OrderGuid.ToString("D"));
            CompleteSuccessfulPayment(result);
        }
        catch (PaymentUploadFailedException ex)
        {
            var correlation = OperationAuditEvents.CreateCorrelation();
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                operationType,
                "Failed",
                Session,
                before,
                reasonCode: "UPLOAD_RETRY_FAILED",
                safeMessage: ex.GetType().Name,
                paymentMethod: FormatPaymentMethods(tenderSnapshot),
                paymentAmount: ex.TenderedAmount,
                orderGuid: ex.OrderGuid.ToString("D"),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            ConsoleLog.WriteError(
                "OperationAudit",
                $"payment upload retry failed operation={operationType} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            _pendingVoucherUploadOrderGuid = ex.OrderGuid;
            _pendingVoucherTenderedAmount = ex.TenderedAmount;
            _pendingVoucherChangeAmount = ex.ChangeAmount;
            SetStatus("payment.status.uploadFailed", ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var correlation = OperationAuditEvents.CreateCorrelation();
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                operationType,
                "Failed",
                Session,
                before,
                reasonCode: "UPLOAD_RETRY_FAILED",
                safeMessage: ex.GetType().Name,
                paymentMethod: FormatPaymentMethods(tenderSnapshot),
                paymentAmount: _pendingVoucherTenderedAmount,
                orderGuid: _pendingVoucherUploadOrderGuid.Value.ToString("D"),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            ConsoleLog.WriteError(
                "OperationAudit",
                $"payment upload retry technical failure operation={operationType} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            SetStatus("payment.status.uploadFailed");
            NotifyPaymentCommandStates();
            return;
        }
    }

    private void CompleteSuccessfulPayment(CashPaymentWorkflowResult result)
    {
        IsManualCardSavePending = false;
        _pendingVoucherUploadOrderGuid = null;
        _pendingVoucherTenderedAmount = 0m;
        _pendingVoucherChangeAmount = 0m;
        _requiresAlternativeRefundMethod = false;
        _alternativeRefundCartRevision = null;
        PaymentTenders.Clear();
        PendingSyncCount = result.PendingSyncCount;
        Session = result.UpdatedSession;
        RefreshCart();
        SetStatus(result.HasPostCommitWarning
            ? "payment.status.completedWarning"
            : "payment.status.completed");
        // 中文注释：支付完成后的导航、同步、自动开钱箱和打印属于后台收尾，不得继承本次提权票据。
        using (OperationAuthorizationScope.Suspend())
        {
            PaymentCompleted?.Invoke(
                this,
                new PaymentCompletedEventArgs(
                    result.Order,
                    result.TenderedAmount,
                    result.ChangeAmount,
                    result.HasPostCommitWarning));
        }
    }

    private bool CanAddTender(PaymentMethodKind method, bool allowDefaultAmount)
    {
        if (!IsPaymentMethodEnabled(method)) return false;
        if (IsManualCardDialogOpen) return false;
        if (method == PaymentMethodKind.Card && _requiresAlternativeRefundMethod)
        {
            return false;
        }

        if (method == PaymentMethodKind.Card &&
            _isLinklyCloudBackendTerminalMode &&
            (IsLinklyCloudTerminalRefreshing || SelectedLinklyCloudTerminal is null))
        {
            return false;
        }

        if (IsPaymentInteractionLocked ||
            _cardSession.IsActive ||
            _cardSession.HasUnknownResult ||
            _pendingVoucherUploadOrderGuid is not null ||
            IsZeroSettlementMode ||
            (!IsInstallmentRepaymentMode && (_cart.IsEmpty || _cart.HasNonIntegerQuantity || _cart.HasZeroPriceLine)))
        {
            return false;
        }

        if (IsInstallmentPaymentEnabled && PaymentTenders.Count > 0)
        {
            return false;
        }

        if (HasTenderForMethod(method) && !(IsRefundMode && method == PaymentMethodKind.Card))
        {
            return false;
        }

        var amountText = ResolveTenderAmountText(method, allowDefaultAmount);
        if (!_workflowService.TryParseTenderedAmount(amountText, out var amount) || amount <= 0m)
        {
            return false;
        }

        var remainingAmount = PaymentStrategies[method].ResolveDefaultAmount(
            IsRefundMode,
            GetPaymentTargetAmount(),
            GetCashRemainingAmount,
            GetExternalRemainingAmount,
            GetRefundRemainingAmount);
        if (remainingAmount <= 0m)
        {
            return false;
        }

        var additionalCheck = PaymentStrategies[method].CanAddTenderAdditionalCheck(
            IsRefundMode,
            allowDefaultAmount,
            VoucherCodeText,
            GetRefundReference(method),
            remainingAmount);
        if (additionalCheck is not null)
        {
            return false;
        }

        if (IsRefundMode && !IsNonVoucherRefundAmountAllowed(method, amount))
        {
            return false;
        }

        return IsInstallmentPaymentEnabled
            ? IsInstallmentTenderAmountAllowed(method, amount, remainingAmount)
            : method == PaymentMethodKind.Cash || amount <= remainingAmount;
    }

    private bool CanConfirmPayment()
    {
        if (IsManualCardDialogOpen) return false;
        if (IsPaymentInteractionLocked || _cardSession.IsActive || _cardSession.HasUnknownResult)
        {
            return false;
        }

        if (_pendingVoucherUploadOrderGuid is not null)
        {
            return true;
        }

        if (IsInstallmentPaymentEnabled)
        {
            return CanConfirmInstallmentPayment();
        }

        return !_cart.IsEmpty &&
            !_cart.HasNonIntegerQuantity &&
            !_cart.HasZeroPriceLine &&
            (IsZeroSettlementMode || PaymentTenders.Count > 0) &&
            IsSettlementComplete() &&
            GetVoucherRefundShortfall() <= 0m;
    }

    private void RefreshCartValidationStatus()
    {
        if (TrySetBlockingCartIssueStatus())
        {
            return;
        }

        if (_statusTextOverride is null &&
            (_statusKey is "cart.status.quantityMustBeInteger" or "cart.status.zeroPriceItem" || IsModeStatusKey(_statusKey)))
        {
            SetStatus(GetReadyStatusKey());
        }
    }

    private bool TrySetBlockingCartIssueStatus()
    {
        var statusKey = GetBlockingCartIssueStatusKey();
        if (statusKey is null)
        {
            return false;
        }

        SetStatus(statusKey);
        return true;
    }

    private void RecalculateTenderSummary()
    {
        var targetAmount = GetPaymentTargetAmount();
        TotalTendered = _workflowService.CalculateTenderedAmount(PaymentTenders.ToList());
        _workflowRemainingAmount = _workflowService.CalculateRemainingAmount(targetAmount, PaymentTenders.ToList());
        // 澳币现金最小面额 0.05，顾客给整钞后超收是常态，此时剩余额为负。
        // 取绝对值会把找零显示成"还差这么多"，与同屏的找零金额自相矛盾，
        // 收银员可能据此重复收款；付款模式下已收够就按 0 收口。
        // 退款模式沿用绝对值语义（剩余额为正表示还需退给顾客）。
        RemainingAmount = IsPaymentMode
            ? Math.Max(0m, _workflowRemainingAmount)
            : Math.Abs(_workflowRemainingAmount);
        ChangeDue = IsPaymentMode &&
            PaymentTenders.Count == 0 &&
            _workflowService.TryParseTenderedAmount(TenderAmountText, out var tenderedAmount)
                ? Math.Max(0m, CashRoundingPolicy.CalculateCashChange(targetAmount, [], tenderedAmount))
                : IsPaymentMode
                    ? _workflowService.CalculateChange(PaymentTenders.ToList(), targetAmount)
                    : 0m;
        OnPropertyChanged(nameof(QuickCashAmounts));
    }

    private IReadOnlyList<QuickCashOption> BuildQuickCashAmounts()
    {
        return
        [
            new QuickCashOption(100m, "$100", "#FF2F9E6D", "White"),
            new QuickCashOption(50m, "$50", "#FFF2C94C", "#FF2E1500"),
            new QuickCashOption(20m, "$20", "#FFE45858", "White"),
            new QuickCashOption(10m, "$10", "#FF2E6BB8", "White"),
            new QuickCashOption(5m, "$5", "#FFC15AA1", "White")
        ];
    }

    private void SyncTenderAmountToRemaining(bool force = false)
    {
        if (!force && !string.IsNullOrWhiteSpace(TenderAmountText))
        {
            return;
        }

        var amount = PaymentStrategies[SelectedPaymentMethod].ResolveDefaultAmount(
            IsRefundMode,
            GetPaymentTargetAmount(),
            GetCashRemainingAmount,
            GetExternalRemainingAmount,
            GetRefundRemainingAmount);
        TenderAmountText = amount > 0m ? amount.ToString("0.00") : string.Empty;
    }

    private bool CanApplyQuickCash(QuickCashOption? option)
    {
        return option is not null && CanAddTender(PaymentMethodKind.Cash, allowDefaultAmount: true);
    }

    private string ResolveTenderAmountText(PaymentMethodKind method)
    {
        return ResolveTenderAmountText(method, allowDefaultAmount: true);
    }

    private string ResolveTenderAmountText(PaymentMethodKind method, bool allowDefaultAmount)
    {
        if (!string.IsNullOrWhiteSpace(TenderAmountText) || !allowDefaultAmount)
        {
            return TenderAmountText;
        }

        return ResolveDefaultTenderAmountText(method);
    }

    private string ResolveDefaultTenderAmountText(PaymentMethodKind method)
    {
        var amount = PaymentStrategies[method].ResolveDefaultAmount(
            IsRefundMode,
            GetPaymentTargetAmount(),
            GetCashRemainingAmount,
            GetExternalRemainingAmount,
            GetRefundRemainingAmount);
        if (method == PaymentMethodKind.Voucher && GetVoucherRefundShortfall() is var shortfall && shortfall > 0m)
        {
            // 代金券默认只退须退代金券的部分，其余留给刷卡/现金；收银员仍可手动输入全额退代金券。
            amount = Math.Min(amount, shortfall);
        }

        return amount > 0m ? amount.ToString("0.00") : string.Empty;
    }

    private decimal? ResolveAttemptedPaymentAmount(string? amountText)
    {
        if (!_workflowService.TryParseTenderedAmount(amountText, out var amount) || amount <= 0m)
        {
            return null;
        }

        // UI 输入始终为正数；退款审计必须恢复 POS 现有的有符号金额语义。
        return IsRefundMode ? -Math.Abs(amount) : Math.Abs(amount);
    }

    private decimal GetExternalRemainingAmount()
    {
        var tenderedAmount = PaymentTenders.Sum(tender => tender.Amount);
        return Math.Abs(decimal.Round(GetPaymentTargetAmount() - tenderedAmount, 2, MidpointRounding.AwayFromZero));
    }

    private bool HasTenderForMethod(PaymentMethodKind method)
    {
        return PaymentTenders.Any(tender => tender.Method == method);
    }

    private bool IsOfflineVoucherRefundUnavailable(PaymentMethodKind method)
    {
        // 退款代金券需要在线发券，离线时提前拦截，避免进入待上传状态后卡住。
        return IsRefundMode &&
            method == PaymentMethodKind.Voucher &&
            !Session.IsOnline;
    }

    private bool HasOfflineVoucherRefundTender()
    {
        return IsRefundMode &&
            !Session.IsOnline &&
            PaymentTenders.Any(IsVoucherRefundTender);
    }

    private static bool IsVoucherRefundTender(PaymentTender tender)
    {
        return tender.Method == PaymentMethodKind.Voucher && tender.Amount < 0m;
    }

    private bool TrySetOfflineVoucherRefundTenderStatus()
    {
        if (!HasOfflineVoucherRefundTender())
        {
            return false;
        }

        SetOfflineVoucherRefundUnavailableStatus();
        return true;
    }

    private void SetOfflineVoucherRefundUnavailableStatus()
    {
        SetStatus("payment.refund.status.voucherOfflineUnavailable");
        NotifyPaymentCommandStates();
    }

    private void BackToPos()
    {
        if (IsManualCardDialogOpen || IsManualCardSavePending) return;
        if (TrySetOfflineVoucherRefundTenderStatus())
        {
            return;
        }

        if (PaymentTenders.Count > 0)
        {
            SetStatus("payment.status.removeTendersBeforeBack");
            NotifyPaymentCommandStates();
            return;
        }

        _navigationActions.BackToPos?.Invoke();
    }

    private bool CanBackToPos()
    {
        return !IsManualCardDialogOpen && !IsPaymentInteractionLocked &&
            !IsCardPaymentInProgress &&
            !_cardSession.HasUnknownResult &&
            !_cardSession.IsAwaitingLateResult;
    }

    private async Task ShowInstallmentCenterAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Installments.View, "open-installment-center");
        if (permissionGrant is null)
        {
            return;
        }

        // 分期流程暂时独立于现有收款流程，只负责跳转到新骨架页面。
        using var authorizationActivation = permissionGrant.Activate();
        _navigationActions.ShowInstallmentCenter?.Invoke();
    }

    private Task<ViewModelAuthorizationGrant?> AuthorizeAsync(
        string permissionCode,
        string action,
        PaymentMethodKind? requestedPaymentMethod = null,
        decimal? requestedPaymentAmount = null) =>
        ViewModelOperationAuthorization.AuthorizeAsync(
            _operationAuthorizationService,
            code => TryRequirePermission(code, requestedPaymentMethod, requestedPaymentAmount),
            permissionCode,
            "payment",
            action,
            Session);

    private bool CanShowInstallmentCenter()
    {
        return !IsManualCardDialogOpen && IsPaymentMode &&
            !IsPaymentInteractionLocked &&
            !_cardSession.IsActive &&
            !_cardSession.HasUnknownResult &&
            !_cart.IsEmpty;
    }

    private void CancelPayment()
    {
        if (IsCardPaymentInProgress)
        {
            _cardSession.Cancel();
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.PaymentCancel,
                "Succeeded",
                Session,
                OperationAuditEvents.CaptureCart(_cart.Lines),
                reasonCode: "CARD_MANUAL_CANCEL",
                paymentMethod: PaymentMethodKind.Card.ToString());
            return;
        }
    }

    private static string? FormatPaymentMethods(IReadOnlyList<PaymentTender> tenders)
    {
        var methods = tenders
            .Select(tender => tender.Method.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return methods.Length == 0 ? null : string.Join('+', methods);
    }

    private bool CanCancelPayment()
    {
        // 中文注释：人工取消只允许中断仍在等待的终端调用；晚到结果窗口不能再次取消并丢弃已批准款项。
        return IsCardPaymentInProgress;
    }

    internal bool IsCurrentPaymentEntry(int paymentEntryVersion)
    {
        return paymentEntryVersion == _paymentEntryVersion;
    }

    private PaymentEntryMode CalculatePaymentMode()
    {
        if (IsInstallmentRepaymentMode)
        {
            return PaymentEntryMode.Payment;
        }

        if (ActualAmount < 0m)
        {
            return PaymentEntryMode.Refund;
        }

        if (ActualAmount == 0m)
        {
            return PaymentEntryMode.ZeroSettlement;
        }

        return PaymentEntryMode.Payment;
    }

    private decimal GetCashRemainingAmount()
    {
        return IsRefundMode
            ? new CashRoundingPolicy().NormalizeCashTender(GetRefundRemainingAmount(PaymentMethodKind.Cash))
            : CashRoundingPolicy.GetCashPayableAmount(GetPaymentTargetAmount(), PaymentTenders.ToList());
    }

    private bool IsInstallmentRepaymentMode => _installmentRepaymentOrder is not null;

    private decimal GetPaymentTargetAmount()
    {
        return _installmentRepaymentOrder?.OutstandingAmount ?? ActualAmount;
    }

    private PaymentTenderAttemptResult CreateDeferredInstallmentRepaymentTender(
        PaymentMethodKind method,
        string? amountText,
        string? referenceText)
    {
        if (!_workflowService.TryParseTenderedAmount(amountText, out var amount) || amount <= 0m)
        {
            return PaymentTenderAttemptResult.Fail("payment.status.invalidAmount");
        }

        if (method == PaymentMethodKind.Voucher && string.IsNullOrWhiteSpace(referenceText))
        {
            return PaymentTenderAttemptResult.Fail("payment.status.voucherCodeRequired");
        }

        var statusKey = method switch
        {
            PaymentMethodKind.Cash => "payment.status.cashTenderAdded",
            PaymentMethodKind.Card => "payment.status.cardTenderAdded",
            PaymentMethodKind.Voucher => "payment.status.voucherTenderAdded",
            _ => "payment.status.unsupportedMethod"
        };
        if (method is not (PaymentMethodKind.Cash or PaymentMethodKind.Card or PaymentMethodKind.Voucher))
        {
            return PaymentTenderAttemptResult.Fail(statusKey);
        }

        // 中文注释：续付页这里只固定 tender 草稿；真正现金登记、礼券预占或卡终端授权必须在 durable action + 中央 claim 之后执行。
        return PaymentTenderAttemptResult.Success(
            new PaymentTender(method, amount, method == PaymentMethodKind.Voucher ? referenceText?.Trim() : null),
            statusKey);
    }

    private static bool IsInstallmentTenderAmountAllowed(PaymentMethodKind method, decimal amount, decimal targetAmount)
    {
        return targetAmount > 0m &&
            amount > 0m &&
            (method == PaymentMethodKind.Cash || amount <= targetAmount);
    }

    private PaymentTender GetInstallmentAppliedTender(PaymentTender tender)
    {
        var appliedAmount = GetInstallmentAppliedPaymentAmount(tender);
        return appliedAmount == tender.Amount
            ? tender
            : tender with { Amount = appliedAmount };
    }

    private decimal GetInstallmentAppliedPaymentAmount(PaymentTender tender)
    {
        // 中文注释：现金可以超付用于找零，但分期服务只记录真正抵扣未付金额的部分。
        return tender.Method == PaymentMethodKind.Cash
            ? Math.Min(tender.Amount, GetPaymentTargetAmount())
            : tender.Amount;
    }

    private PosCartServiceSnapshot CreateInstallmentCartSnapshot()
    {
        // 中文注释：分期创建要保存商品明细，使用和分期中心相同的轻量购物车快照结构。
        return new PosCartServiceSnapshot(
            _cart.TotalAmount,
            _cart.DiscountAmount,
            _cart.ActualAmount,
            _cart.Lines.Select(line => new PosCartLineServiceSnapshot(
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.ItemNumber,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.ActualAmount)).ToList());
    }

    private string CreateInstallmentPaymentFingerprint(PaymentTender tender)
    {
        var (reference, reservationToken) = tender.Method == PaymentMethodKind.Voucher
            ? OrderUploadService.ParseVoucherReference(tender.Reference)
            : (tender.Reference, null);

        return string.Join(
            '\u001f',
            _installmentRepaymentOrder?.OrderId.ToString("N") ?? "create",
            Session.StoreCode,
            Session.DeviceCode,
            tender.Method,
            tender.Amount.ToString(CultureInfo.InvariantCulture),
            reference ?? string.Empty,
            reservationToken ?? string.Empty,
            tender.IdempotencyKey ?? string.Empty,
            InstallmentCustomerName.Trim(),
            InstallmentCustomerPhone.Trim(),
            _cart.ActualAmount.ToString(CultureInfo.InvariantCulture));
    }

    private InstallmentPaymentDraft CreateInstallmentPaymentDraft(PaymentTender tender)
    {
        var (reference, reservationToken) = tender.Method == PaymentMethodKind.Voucher
            ? OrderUploadService.ParseVoucherReference(tender.Reference)
            : (tender.Reference, null);
        // 仅同一订单、金额和付款方式的重试可复用身份，旧草稿不能放行新的已禁用付款。
        var fingerprint = CreateInstallmentPaymentFingerprint(tender);
        if (!string.Equals(_installmentDraftFingerprint, fingerprint, StringComparison.Ordinal))
        {
            _installmentDraftFingerprint = fingerprint;
            _installmentDraftPaymentGuid = Guid.NewGuid();
            _installmentDraftCardIdempotencyKey = tender.IdempotencyKey ??
                (tender.Method == PaymentMethodKind.Card
                    ? $"preauthorized-card:{_installmentDraftPaymentGuid.Value:D}"
                    : null);
        }

        return new InstallmentPaymentDraft(
            _installmentDraftPaymentGuid!.Value,
            tender.Method,
            tender.Amount,
            reference,
            reservationToken,
            tender.CardTransactions,
            tender.IdempotencyKey ?? _installmentDraftCardIdempotencyKey);
    }

    private void ResetInstallmentPaymentDraft()
    {
        _installmentDraftFingerprint = null;
        _installmentDraftPaymentGuid = null;
        _installmentDraftCardIdempotencyKey = null;
    }

    private decimal GetRefundRemainingAmount(PaymentMethodKind method)
    {
        var remaining = PaymentStrategies[method].GetRefundRemainingAmount(
            _workflowRemainingAmount,
            _ => GetNextCardRefundCapacity()?.RemainingAmount) ?? 0m;
        // 代金券买的部分只能退代金券：现金/刷卡的默认金额不超过非代金券可退上限。
        return GetNonVoucherRefundCap(method) is { } cap ? Math.Min(remaining, cap) : remaining;
    }

    private decimal GetRequiredVoucherRefundAmount() =>
        IsRefundMode
            ? VoucherFundedRefundPolicy.GetRequiredVoucherRefundAmount(
                _cart.ReturnPaymentCapacities,
                _cart.Lines,
                GetPaymentTargetAmount())
            : 0m;

    // 只有本次退款确实含代金券应退部分时才限额；不含代金券原付款的退款保持原有行为。
    private decimal? GetNonVoucherRefundCap(PaymentMethodKind method) =>
        GetRequiredVoucherRefundAmount() is var required && required > 0m
            ? VoucherFundedRefundPolicy.GetNonVoucherRefundCap(
                method,
                GetPaymentTargetAmount(),
                required,
                PaymentTenders.ToList(),
                _workflowRemainingAmount)
            : null;

    private bool IsNonVoucherRefundAmountAllowed(PaymentMethodKind method, decimal amount) =>
        GetNonVoucherRefundCap(method) is not { } cap || Math.Abs(amount) <= cap;

    private decimal GetVoucherRefundShortfall() =>
        IsRefundMode
            ? VoucherFundedRefundPolicy.GetVoucherRefundShortfall(GetRequiredVoucherRefundAmount(), PaymentTenders.ToList())
            : 0m;

    private static string FormatRefundMoney(decimal amount) =>
        amount.ToString("C2", CultureInfo.GetCultureInfo("en-AU"));

    private void SetVoucherFundedRefundRequiredStatus()
    {
        SetStatus(
            "payment.refund.status.voucherFundedRequiresVoucher",
            string.Format(
                CultureInfo.CurrentCulture,
                T("payment.refund.status.voucherFundedRequiresVoucher"),
                FormatRefundMoney(GetRequiredVoucherRefundAmount())));
    }

    private bool TrySetVoucherFundedRefundShortfallStatus()
    {
        var shortfall = GetVoucherRefundShortfall();
        if (shortfall <= 0m)
        {
            return false;
        }

        SetStatus(
            "payment.refund.status.voucherFundedShortfall",
            string.Format(
                CultureInfo.CurrentCulture,
                T("payment.refund.status.voucherFundedShortfall"),
                FormatRefundMoney(shortfall)));
        NotifyPaymentCommandStates();
        return true;
    }

    private string? GetRefundReference(PaymentMethodKind method)
    {
        if (!IsRefundMode)
        {
            return null;
        }

        return PaymentStrategies[method].GetRefundReference(() => GetNextCardRefundCapacity()?.Reference);
    }

    private (string Reference, decimal RemainingAmount)? GetNextCardRefundCapacity()
    {
        foreach (var capacity in _cart.ReturnPaymentCapacities.Where(capacity => capacity.Method == PaymentMethodKind.Card))
        {
            var reference = ResolveOriginalCardRefundReference(capacity);
            ConsoleLog.Write(
                "CardRefund",
                $"capacity candidate originalOrder={capacity.OriginalOrderGuid?.ToString() ?? "<null>"} " +
                $"remaining={capacity.RemainingAmount:0.00} rawReference={LogValue(capacity.Reference)} " +
                $"resolvedReference={LogValue(reference)} cardTxCount={capacity.CardTransactions?.Count ?? 0} " +
                $"refundReferences={LogValue(FormatRefundReferences(capacity.CardTransactions))}");
            if (reference is null)
            {
                continue;
            }

            var existingTendered = Math.Abs(PaymentTenders
                .Where(tender => tender.Method == PaymentMethodKind.Card)
                .Where(tender => string.Equals(GetOriginalCardReference(tender.Reference), reference, StringComparison.OrdinalIgnoreCase))
                .Sum(tender => tender.Amount));
            var remainingCapacity = Math.Max(0m, capacity.RemainingAmount - existingTendered);
            var remainingReturnAmount = GetRemainingReturnAmountForCardCapacity(capacity);
            var remainingAmount = remainingReturnAmount is decimal orderLimitedAmount
                ? Math.Min(remainingCapacity, orderLimitedAmount)
                : remainingCapacity;
            ConsoleLog.Write(
                "CardRefund",
                $"capacity evaluated originalReference={LogValue(reference)} existingTendered={existingTendered:0.00} " +
                $"remainingCapacity={remainingCapacity:0.00} orderLimited={remainingReturnAmount?.ToString("0.00") ?? "<null>"} " +
                $"selectedRemaining={remainingAmount:0.00}");
            if (remainingAmount > 0m)
            {
                ConsoleLog.Write(
                    "CardRefund",
                    $"capacity selected originalReference={LogValue(reference)} remaining={remainingAmount:0.00}");
                return (reference, remainingAmount);
            }
        }

        ConsoleLog.Write("CardRefund", "no eligible card refund capacity selected");
        return null;
    }

    private decimal? GetRemainingReturnAmountForCardCapacity(OrderReturnPaymentCapacityDto capacity)
    {
        if (capacity.OriginalOrderGuid is not Guid originalOrderGuid)
        {
            return null;
        }

        var returnAmount = Math.Abs(decimal.Round(
            _cart.Lines
                .Where(line => line.IsReturnLine && line.OriginalOrderGuid == originalOrderGuid)
                .Sum(line => line.ActualAmount),
            2,
            MidpointRounding.AwayFromZero));
        if (returnAmount <= 0m)
        {
            return 0m;
        }

        var existingCardRefundsForOrder = Math.Abs(decimal.Round(
            PaymentTenders
                .Where(tender => tender.Method == PaymentMethodKind.Card)
                .Where(tender => IsCardRefundForOriginalOrder(tender.Reference, originalOrderGuid))
                .Sum(tender => tender.Amount),
            2,
            MidpointRounding.AwayFromZero));
        return Math.Max(0m, returnAmount - existingCardRefundsForOrder);
    }

    private bool IsCardRefundForOriginalOrder(string? reference, Guid originalOrderGuid)
    {
        var originalReference = GetOriginalCardReference(reference);
        if (originalReference is null)
        {
            return false;
        }

        return _cart.ReturnPaymentCapacities.Any(capacity =>
            capacity.Method == PaymentMethodKind.Card &&
            capacity.OriginalOrderGuid == originalOrderGuid &&
            string.Equals(NormalizeReference(capacity.Reference), originalReference, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetOriginalCardReference(string? reference)
    {
        return CardRefundReference.TryGetOriginalReference(reference, out var originalReference)
            ? NormalizeReference(originalReference)
            : NormalizeReference(reference);
    }

    private static string? ResolveOriginalCardRefundReference(OrderReturnPaymentCapacityDto capacity)
    {
        var reference = NormalizeReference(capacity.Reference);
        if (ManualCardPaymentReference.IsManualRefundSource(reference) ||
            capacity.CardTransactions?.Any(transaction => string.Equals(transaction.Processor, "Manual", StringComparison.OrdinalIgnoreCase)) == true)
        {
            return null;
        }
        if (reference is null || RequiresLinklyRefundReference(reference))
        {
            return BuildLinklyRefundReference(capacity.CardTransactions, reference) ?? reference;
        }

        return reference;
    }

    private static bool RequiresLinklyRefundReference(string reference)
    {
        return reference.StartsWith("ANZCLOUD:", StringComparison.OrdinalIgnoreCase) ||
            reference.StartsWith($"{LinklyBackendPaymentReference.Prefix}:", StringComparison.OrdinalIgnoreCase);
    }

    private static string? BuildLinklyRefundReference(
        IReadOnlyList<CardTransactionDto>? cardTransactions,
        string? fallbackReference)
    {
        foreach (var transaction in cardTransactions ?? [])
        {
            var refundReference = NormalizeReference(transaction.RefundReference);
            if (refundReference is null)
            {
                continue;
            }

            var txnRef = NormalizeReference(transaction.TxnRef) ??
                TryGetLinklyTxnRef(fallbackReference) ??
                "RFN";
            return $"ANZCLOUD:{txnRef}:{refundReference}";
        }

        return null;
    }

    private static string FormatRefundReferences(IReadOnlyList<CardTransactionDto>? cardTransactions)
    {
        var values = (cardTransactions ?? [])
            .Select(transaction => transaction.RefundReference)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length == 0 ? string.Empty : string.Join(',', values);
    }

    private static string? TryGetLinklyTxnRef(string? reference)
    {
        var parts = reference?.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        return parts.Length >= 2 &&
            (string.Equals(parts[0], "ANZCLOUD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parts[0], LinklyBackendPaymentReference.Prefix, StringComparison.OrdinalIgnoreCase))
                ? parts[1]
                : null;
    }

    private static string? NormalizeReference(string? reference)
    {
        return string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private bool IsSettlementComplete()
    {
        return PaymentMode switch
        {
            PaymentEntryMode.Refund => _workflowRemainingAmount >= 0m,
            PaymentEntryMode.ZeroSettlement => true,
            _ => _workflowRemainingAmount <= 0m
        };
    }

    private bool HasBlockingCartIssue()
    {
        return _cart.HasNonIntegerQuantity || _cart.HasZeroPriceLine;
    }

    private string? GetBlockingCartIssueStatusKey()
    {
        if (_cart.HasNonIntegerQuantity)
        {
            return "cart.status.quantityMustBeInteger";
        }

        if (_cart.HasZeroPriceLine)
        {
            return "cart.status.zeroPriceItem";
        }

        return null;
    }

    private string GetScreenTitleKey()
    {
        return PaymentMode switch
        {
            PaymentEntryMode.Refund => "payment.refund.title",
            PaymentEntryMode.ZeroSettlement => "payment.zeroSettlement.title",
            _ => "payment.title"
        };
    }

    private string GetMethodTextKey(PaymentMethodKind method)
    {
        return method switch
        {
            PaymentMethodKind.Cash => IsRefundMode ? "payment.method.refundCash" : "payment.method.cash",
            PaymentMethodKind.Card => IsRefundMode ? "payment.method.refundCard" : "payment.method.card",
            PaymentMethodKind.Voucher => IsRefundMode ? "payment.method.refundVoucher" : "payment.method.voucher",
            _ => "payment.method.installment"
        };
    }

    private string GetRemainingAmountKey()
    {
        return IsRefundMode ? "payment.refund.remaining" : "payment.remaining";
    }

    private string GetConfirmPaymentKey()
    {
        return PaymentMode switch
        {
            PaymentEntryMode.Refund => "payment.refund.confirm",
            PaymentEntryMode.ZeroSettlement => "payment.zeroSettlement.confirm",
            _ => "payment.confirm"
        };
    }

    private string GetNoTendersKey()
    {
        return PaymentMode switch
        {
            PaymentEntryMode.Refund => "payment.refund.noTenders",
            PaymentEntryMode.ZeroSettlement => "payment.zeroSettlement.noTenders",
            _ => "payment.noTenders"
        };
    }

    private string GetReadyStatusKey()
    {
        return PaymentMode switch
        {
            PaymentEntryMode.Refund => "payment.refund.status.ready",
            PaymentEntryMode.ZeroSettlement => "payment.zeroSettlement.status.ready",
            _ => "payment.status.ready"
        };
    }

    private string GetNoTendersStatusKey()
    {
        return IsRefundMode ? "payment.refund.status.noTendersAdded" : "payment.status.noTendersAdded";
    }

    private string GetIncompleteSettlementStatusKey()
    {
        return IsRefundMode ? "payment.refund.status.remainingBalance" : "payment.status.remainingBalance";
    }

    private bool IsModeStatusKey(string statusKey)
    {
        return statusKey == "payment.status.ready" ||
            statusKey == "payment.refund.status.ready" ||
            statusKey == "payment.zeroSettlement.status.ready";
    }

    internal void NotifyPaymentCommandStates()
    {
        OpenManualCardCommand.NotifyCanExecuteChanged();
        CancelManualCardCommand.NotifyCanExecuteChanged();
        ConfirmManualCardCommand.NotifyCanExecuteChanged();
        RetryManualCardSaveCommand.NotifyCanExecuteChanged();
        NumberInputCommand.NotifyCanExecuteChanged();
        SelectCashCommand.NotifyCanExecuteChanged();
        SelectCardCommand.NotifyCanExecuteChanged();
        SelectVoucherCommand.NotifyCanExecuteChanged();
        OpenVoucherEntryCommand.NotifyCanExecuteChanged();
        VoucherEntryKeyCommand.NotifyCanExecuteChanged();
        ConfirmVoucherEntryCommand.NotifyCanExecuteChanged();
        CancelVoucherEntryCommand.NotifyCanExecuteChanged();
        OpenInstallmentCustomerDialogCommand.NotifyCanExecuteChanged();
        SelectInstallmentCustomerFieldCommand.NotifyCanExecuteChanged();
        InstallmentCustomerKeyCommand.NotifyCanExecuteChanged();
        ConfirmInstallmentCustomerDialogCommand.NotifyCanExecuteChanged();
        CancelInstallmentCustomerDialogCommand.NotifyCanExecuteChanged();
        QuickCashCommand.NotifyCanExecuteChanged();
        RemoveTenderCommand.NotifyCanExecuteChanged();
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        BackToPosCommand.NotifyCanExecuteChanged();
        ShowInstallmentCenterCommand.NotifyCanExecuteChanged();
        OpenCardRecoveryCenterCommand.NotifyCanExecuteChanged();
        CardPaymentErrorPrimaryActionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsCancelPaymentVisible));
        OnPropertyChanged(nameof(IsConfirmPaymentVisible));
    }

    internal void SetStatus(string key, string? statusText = null)
    {
        _statusKey = key;
        _statusTextOverride = statusText;
        OnPropertyChanged(nameof(StatusMessage));
    }

    private bool TryRequirePermission(
        string permissionCode,
        PaymentMethodKind? requestedPaymentMethod = null,
        decimal? requestedPaymentAmount = null)
    {
        if ((!_enforcePermissions && _cashierSessionContext.CurrentSession is null && Session.CashierSession is null) ||
            _cashierSessionContext.RequirePermission(permissionCode, out var message))
        {
            return true;
        }

        // 中文注释：付款命令必须在执行前复核当前收银员权限。
        var (operationType, paymentMethod, paymentAmount, orderGuid) = GetDeniedAuditOperation(
            permissionCode,
            requestedPaymentMethod,
            requestedPaymentAmount);
        if (operationType is not null)
        {
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                operationType,
                "Denied",
                Session,
                OperationAuditEvents.CaptureCart(_cart.Lines),
                reasonCode: "PERMISSION_DENIED",
                safeMessage: message,
                paymentMethod: paymentMethod,
                paymentAmount: paymentAmount,
                orderGuid: orderGuid);
        }

        SetStatus("payment.status.permissionDenied", message);
        NotifyPaymentCommandStates();
        return false;
    }

    private (string? OperationType, string? PaymentMethod, decimal? PaymentAmount, string? OrderGuid) GetDeniedAuditOperation(
        string permissionCode,
        PaymentMethodKind? requestedPaymentMethod,
        decimal? requestedPaymentAmount)
    {
        if (permissionCode == Permissions.PosTerminal.Payment.TakeCash)
        {
            return (
                OperationAuditTypes.PaymentTenderAdd,
                (requestedPaymentMethod ?? PaymentMethodKind.Cash).ToString(),
                requestedPaymentAmount,
                null);
        }

        if (permissionCode == Permissions.PosTerminal.Payment.TakeCard)
        {
            return (
                OperationAuditTypes.PaymentTenderAdd,
                (requestedPaymentMethod ?? PaymentMethodKind.Card).ToString(),
                requestedPaymentAmount,
                null);
        }

        if (permissionCode == Permissions.PosTerminal.Payment.TakeVoucher)
        {
            return (
                OperationAuditTypes.PaymentTenderAdd,
                (requestedPaymentMethod ?? PaymentMethodKind.Voucher).ToString(),
                requestedPaymentAmount,
                null);
        }

        if (permissionCode == Permissions.PosTerminal.Payment.RemoveTender)
        {
            return (
                OperationAuditTypes.PaymentTenderRemove,
                requestedPaymentMethod?.ToString(),
                requestedPaymentAmount,
                null);
        }

        if (permissionCode == Permissions.PosTerminal.Payment.Confirm)
        {
            var tenders = PaymentTenders.ToList();
            var tender = tenders.FirstOrDefault();
            if (_installmentRepaymentOrder is { } repaymentOrder)
            {
                return (
                    OperationAuditTypes.InstallmentRepaymentComplete,
                    tender?.Method.ToString(),
                    tender is null ? null : GetInstallmentAppliedPaymentAmount(tender),
                    repaymentOrder.OrderId.ToString("D"));
            }

            if (IsInstallmentPaymentEnabled)
            {
                return (
                    OperationAuditTypes.SaleComplete,
                    tender is null ? "Installment" : $"Installment+{tender.Method}",
                    tender is null ? null : GetInstallmentAppliedPaymentAmount(tender),
                    null);
            }

            decimal? paymentAmount = _pendingVoucherUploadOrderGuid is not null
                ? _pendingVoucherTenderedAmount
                : tenders.Count == 0
                    ? null
                    : tenders.Sum(item => item.Amount);
            return (
                IsRefundMode ? OperationAuditTypes.ReturnRefundComplete : OperationAuditTypes.SaleComplete,
                FormatPaymentMethods(tenders),
                paymentAmount,
                _pendingVoucherUploadOrderGuid?.ToString("D"));
        }

        return (null, null, null, null);
    }

    private static string GetTenderPermission(PaymentMethodKind method)
    {
        return method switch
        {
            PaymentMethodKind.Cash => Permissions.PosTerminal.Payment.TakeCash,
            PaymentMethodKind.Card => Permissions.PosTerminal.Payment.TakeCard,
            PaymentMethodKind.Voucher => Permissions.PosTerminal.Payment.TakeVoucher,
            _ => Permissions.PosTerminal.Payment.Confirm
        };
    }

    internal string T(string key)
    {
        return _localization?.T(key) ?? key;
    }

    private static readonly string[] LocalizedPropertyNames =
    [
        nameof(ScreenTitleText),
        nameof(OrderSummaryText),
        nameof(CurrentTenderTextLabel),
        nameof(AmountTenderedTextLabel),
        nameof(RemainingAmountText),
        nameof(ChangeDueText),
        nameof(QuickCashText),
        nameof(PaymentMethodText),
        nameof(AppliedTendersText),
        nameof(ConfirmPaymentText),
        nameof(NoTendersText),
        nameof(CashMethodText),
        nameof(CardMethodText),
        nameof(VoucherMethodText),
        nameof(InstallmentMethodText),
        nameof(InstallmentCustomerNameDisplay),
        nameof(InstallmentCustomerPhoneDisplay),
        nameof(InstallmentOrderInfoText),
        nameof(CancelText)
    ];

    private void RaiseLocalizedProperties()
    {
        foreach (var name in LocalizedPropertyNames)
        {
            OnPropertyChanged(name);
        }

        OnPropertyChanged(nameof(StatusMessage));
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RaiseLocalizedProperties();
    }

    private void CloseCardPaymentErrorOverlay()
    {
        _cardSession.CloseErrorOverlay();
    }

    private bool CanExecuteCardPaymentErrorPrimaryAction()
    {
        return _cardSession.CanExecuteErrorPrimaryAction();
    }

    private async Task ExecuteCardPaymentErrorPrimaryActionAsync()
    {
        await _cardSession.ExecuteErrorPrimaryActionAsync();
    }

    private PaymentTenderAddRequest BuildAddTenderRequest(PaymentMethodKind method)
    {
        return new PaymentTenderAddRequest(
            method,
            SelectedPaymentMethod,
            IsManualCardDialogOpen || IsPaymentInteractionLocked || _cardSession.IsActive || _cardSession.HasUnknownResult,
            _pendingVoucherUploadOrderGuid is not null,
            IsRefundMode,
            IsOfflineVoucherRefundUnavailable(method),
            HasTenderForMethod(method) && !(IsRefundMode && method == PaymentMethodKind.Card),
            GetBlockingCartIssueStatusKey(),
            VoucherCodeText,
            ResolveTenderAmountText,
            ResolveDefaultTenderAmountText,
            GetRefundReference);
    }

    private bool TryApplyAddTenderPlan(
        PaymentTenderAddPlan plan,
        out string amountText,
        out string? referenceText)
    {
        if (plan.SelectedPaymentMethod is PaymentMethodKind selectedPaymentMethod)
        {
            SelectedPaymentMethod = selectedPaymentMethod;
        }

        if (!plan.ShouldProceed)
        {
            if (plan.StatusKey is not null)
            {
                // 中文注释：controller 只返回入口决策，真正的状态投影和命令刷新仍由 VM 统一处理。
                SetStatus(plan.StatusKey);
            }

            if (plan.NotifyCommandStates)
            {
                NotifyPaymentCommandStates();
            }

            amountText = string.Empty;
            referenceText = null;
            return false;
        }

        amountText = plan.AmountText ?? string.Empty;
        referenceText = plan.ReferenceText;
        return true;
    }
}

public sealed record QuickCashOption(decimal Amount, string Label, string NoteColorKey, string ForegroundColorKey);

public enum PaymentEntryMode
{
    Payment,
    Refund,
    ZeroSettlement
}
