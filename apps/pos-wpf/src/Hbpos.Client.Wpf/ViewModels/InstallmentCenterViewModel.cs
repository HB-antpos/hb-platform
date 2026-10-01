using System.Collections.ObjectModel;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class InstallmentCenterViewModel : ObservableObject, IDisposable
{
    private readonly IInstallmentOrderService _installmentOrderService;
    private readonly Func<PosCartServiceSnapshot?, Task> _showCreateAsync;
    private readonly Action _backToPayment;
    private readonly ILocalizationService? _localization;
    private readonly ICashierSessionContext _cashierSessionContext;
    private readonly bool _enforcePermissions;
    private readonly IOperationAuditLogger? _operationAuditLogger;
    private readonly IOperationAuthorizationService? _operationAuthorizationService;
    private EventHandler? _onCultureChanged;
    private string? _statusResourceKey;
    private string _statusFallback = string.Empty;
    private object[] _statusResourceArgs = [];
    private Guid _repaymentPaymentGuid = Guid.NewGuid();
    private string? _repaymentIdempotencyKey;
    private readonly HashSet<Guid> _lockedInstallmentGuids = [];
    private bool _isRecoveryStateUnknown;
    private Guid? _refundStepsInstallmentGuid;

    [ObservableProperty] private PosSessionState _session;
    [ObservableProperty] private PosCartServiceSnapshot? _cartSnapshot;
    [ObservableProperty] private InstallmentOrderSummary? _selectedOrder;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private decimal _repaymentAmount;
    [ObservableProperty] private PaymentMethodKind _repaymentMethod = PaymentMethodKind.Cash;
    [ObservableProperty] private string _repaymentReference = string.Empty;
    [ObservableProperty] private string _repaymentVoucherToken = string.Empty;
    [ObservableProperty] private string _voidReason = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private LocalInstallmentRefundStep? _selectedRefundStep;
    [ObservableProperty] private InstallmentRefundSupervisorDecision _supervisorRefundDecision = InstallmentRefundSupervisorDecision.ContinueWaiting;
    [ObservableProperty] private string _supervisorRefundReason = string.Empty;
    [ObservableProperty] private string _supervisorRefundEvidence = string.Empty;
    [ObservableProperty] private string _supervisorRefundReference = string.Empty;

    public InstallmentCenterViewModel(
        IInstallmentOrderService installmentOrderService,
        PosSessionState session,
        Func<PosCartServiceSnapshot?, Task> showCreateAsync,
        Action backToPayment,
        ILocalizationService? localization = null,
        ICardTerminalClient? cardTerminalClient = null,
        ICashierSessionContext? cashierSessionContext = null,
        bool enforcePermissionsWhenNoCashier = false,
        IOperationAuditLogger? operationAuditLogger = null,
        IOperationAuthorizationService? operationAuthorizationService = null)
    {
        _installmentOrderService = installmentOrderService;
        _session = session;
        _showCreateAsync = showCreateAsync;
        _backToPayment = backToPayment;
        _localization = localization;
        _cashierSessionContext = cashierSessionContext ?? new CashierSessionContext();
        _enforcePermissions = enforcePermissionsWhenNoCashier;
        _operationAuditLogger = operationAuditLogger;
        _operationAuthorizationService = operationAuthorizationService;
        if (session.CashierSession is not null)
        {
            _cashierSessionContext.SetCurrent(session.CashierSession);
        }

        if (_localization is not null)
        {
            _onCultureChanged = OnCultureChanged;
            _localization.CultureChanged += _onCultureChanged;
        }

        LoadCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy);
        RecoveryCommand = new AsyncRelayCommand(RecoverAsync, () => !IsBusy && Session.IsOnline);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsBusy);
        CreateInstallmentCommand = new AsyncRelayCommand(CreateInstallmentAsync, CanCreateInstallment);
        AddRepaymentCommand = new AsyncRelayCommand(AddRepaymentAsync, CanAddRepayment);
        CancelWithRefundCommand = new AsyncRelayCommand(CancelWithRefundAsync, CanCancelWithRefund);
        VoidCancelCommand = new AsyncRelayCommand(VoidCancelAsync, CanVoidCancel);
        ConfirmPickupCommand = new AsyncRelayCommand(ConfirmPickupAsync, CanConfirmPickup);
        SupervisorResolveRefundCommand = new AsyncRelayCommand(ResolveRefundBySupervisorAsync, CanResolveRefundBySupervisor);
        BackToPaymentCommand = new RelayCommand(_backToPayment);

        RefreshPaymentMethodOptions();
        SetStatusResource("installment.center.status.ready", "Select an installment order to create or process.");
    }

    public ObservableCollection<InstallmentOrderSummary> Orders { get; } = [];
    public ObservableCollection<InstallmentPaymentMethodOption> PaymentMethodOptions { get; } = [];
    public ObservableCollection<LocalInstallmentRefundStep> RefundStepsForReview { get; } = [];
    public IReadOnlyList<InstallmentRefundSupervisorDecision> SupervisorRefundDecisions { get; } =
        [
            InstallmentRefundSupervisorDecision.ConfirmRefunded,
            InstallmentRefundSupervisorDecision.ConfirmNotRefunded,
            InstallmentRefundSupervisorDecision.ContinueWaiting
        ];

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand RecoveryCommand { get; }
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand CreateInstallmentCommand { get; }
    public IAsyncRelayCommand AddRepaymentCommand { get; }
    public IAsyncRelayCommand CancelWithRefundCommand { get; }
    public IAsyncRelayCommand VoidCancelCommand { get; }
    public IAsyncRelayCommand ConfirmPickupCommand { get; }
    public IAsyncRelayCommand SupervisorResolveRefundCommand { get; }
    public IRelayCommand BackToPaymentCommand { get; }

    public string PageTitleText => T("installment.center.title", "Installment Center");
    public string CurrentOrderSummaryText => CartSnapshot is null
        ? T("installment.center.currentOrder.none", "There is no current order available for a new installment.")
        : string.Format(GetCulture(), T("installment.center.currentOrder.amount", "Current order amount {0:C2}. A new installment can be created."), CartSnapshot.ActualAmount);
    public string CreateInstallmentText => T("installment.center.action.create", "Create Installment");
    public string AddRepaymentText => T("installment.center.action.repay", "Add Repayment");
    public string CancelWithRefundText => T("installment.center.action.cancel", "Cancel and Refund");
    public string VoidCancelText => T("installment.center.action.void", "Void");
    public string ConfirmPickupText => T("installment.center.action.confirmPickup", "Confirm Pickup");
    public string LoadText => T("common.load", "Load");
    public string SearchButtonText => T("installment.center.action.search", "Search");
    public string SearchTextLabel => T("installment.center.search", "Search order no., name, or phone");
    public string BackToPaymentText => T("installment.center.action.backToPayment", "Back to Payment");
    public string OfflineNoticeText => T("installment.center.offline", "Offline mode only supports local cached installment orders.");
    public string SelectedOrderNumberText => SelectedOrder?.OrderNumber ?? T("installment.center.selected.none", "No installment selected");
    public string SelectedOrderCustomerText => SelectedOrder?.CustomerName ?? T("installment.center.selected.customer.empty", "Select an installment order on the left");
    public string SelectedOrderOutstandingText => SelectedOrder is null
        ? T("installment.center.selected.outstanding.empty", "Outstanding -")
        : string.Format(GetCulture(), T("installment.center.selected.outstanding", "Outstanding {0:C2}"), SelectedOrder.OutstandingAmount);
    public bool IsOffline => !Session.IsOnline;
    public bool HasOrders => Orders.Count > 0;
    public bool IsCreateEnabled => CanCreateInstallment();
    public bool IsAddRepaymentEnabled => CanAddRepayment();
    public bool IsCancelWithRefundEnabled => CanCancelWithRefund();
    public bool IsVoidCancelEnabled => CanVoidCancel();
    public bool IsConfirmPickupEnabled => CanConfirmPickup();
    public bool IsSelectedOrderLocked => SelectedOrder is not null &&
        (_isRecoveryStateUnknown || _lockedInstallmentGuids.Contains(SelectedOrder.OrderId));
    public bool HasRefundStepsForReview => RefundStepsForReview.Count > 0 && IsRefundSupervisorAuthorized;
    public bool IsSupervisorResolutionEnabled => CanResolveRefundBySupervisor();

    private bool IsRefundSupervisorAuthorized =>
        (!_enforcePermissions && _cashierSessionContext.CurrentSession is null && Session.CashierSession is null) ||
        _cashierSessionContext.RequirePermission(Permissions.PosTerminal.Installments.Cancel, out _);

    partial void OnSelectedOrderChanged(InstallmentOrderSummary? value)
    {
        RepaymentAmount = value?.OutstandingAmount ?? 0m;
        ResetRepaymentOperation();
        // 切换订单时立即撤销旧退款步骤的归属，等待新订单查询完成前禁止主管操作。
        _refundStepsInstallmentGuid = null;
        RefundStepsForReview.Clear();
        SelectedRefundStep = null;
        OnPropertyChanged(nameof(HasRefundStepsForReview));
        OnPropertyChanged(nameof(SelectedOrderNumberText));
        OnPropertyChanged(nameof(SelectedOrderCustomerText));
        OnPropertyChanged(nameof(SelectedOrderOutstandingText));
        OnPropertyChanged(nameof(IsSelectedOrderLocked));
        RaiseSelectionStateChanged();
        _ = RefreshSupervisorRefundStepsSafelyAsync(value?.OrderId);
    }

    partial void OnIsBusyChanged(bool value)
    {
        LoadCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        RaiseSelectionStateChanged();
    }

    partial void OnRepaymentAmountChanged(decimal value) { ResetRepaymentOperation(); RaiseSelectionStateChanged(); }
    partial void OnRepaymentMethodChanged(PaymentMethodKind value) { ResetRepaymentOperation(); RaiseSelectionStateChanged(); }
    partial void OnRepaymentReferenceChanged(string value) { ResetRepaymentOperation(); RaiseSelectionStateChanged(); }
    partial void OnRepaymentVoucherTokenChanged(string value) { ResetRepaymentOperation(); RaiseSelectionStateChanged(); }
    partial void OnSelectedRefundStepChanged(LocalInstallmentRefundStep? value) => RaiseSelectionStateChanged();
    partial void OnSessionChanged(PosSessionState value)
    {
        if (value.CashierSession is not null)
        {
            _cashierSessionContext.SetCurrent(value.CashierSession);
        }

        OnPropertyChanged(nameof(HasRefundStepsForReview));
    }

    public async Task LoadAsync()
    {
        var recoveryWarning = await RecoverPendingOperationsAsync();
        await LoadCoreAsync(
            () => _installmentOrderService.GetOrdersAsync(Session),
            "installment.center.status.loaded",
            "Loaded {0} installment orders.",
            recoveryWarning);
    }
    public async Task SearchAsync() => await LoadCoreAsync(() => _installmentOrderService.SearchAsync(Session, SearchText), "installment.center.status.searched", "Found {0} installment orders.");

    public async Task RecoverAsync()
    {
        var recoveryWarning = await RecoverPendingOperationsAsync();
        await LoadCoreAsync(
            () => _installmentOrderService.GetOrdersAsync(Session),
            "installment.center.status.loaded",
            "Loaded {0} installment orders.",
            recoveryWarning);
    }

    public void Prepare(PosSessionState session, PosCartServiceSnapshot? cartSnapshot)
    {
        Session = session;
        CartSnapshot = cartSnapshot;
        OnPropertyChanged(nameof(CurrentOrderSummaryText));
        RaiseSelectionStateChanged();
    }

    public void AppendOrUpdateOrder(InstallmentOrderSummary order)
    {
        var existing = Orders.FirstOrDefault(item => item.OrderId == order.OrderId);
        if (existing is null)
        {
            Orders.Insert(0, order);
        }
        else
        {
            Orders[Orders.IndexOf(existing)] = order;
        }

        SelectedOrder = order;
        OnPropertyChanged(nameof(HasOrders));
    }

    private async Task<bool> LoadCoreAsync(
        Func<Task<IReadOnlyList<InstallmentOrderSummary>>> loader,
        string loadedFormatKey,
        string loadedFormatFallback,
        string? actionMessage = null)
    {
        IsBusy = true;
        try
        {
            var orders = await loader();
            Orders.ReplaceWith(orders);
            SelectedOrder = Orders.FirstOrDefault();
            if (actionMessage is not null)
            {
                SetLiteralStatus(actionMessage);
            }
            else if (orders.Count == 0)
            {
                SetStatusResource("installment.center.status.empty", "There are no installment orders.");
            }
            else
            {
                SetStatusResource(loadedFormatKey, loadedFormatFallback, orders.Count);
            }

            OnPropertyChanged(nameof(HasOrders));
            return true;
        }
        catch (Exception ex)
        {
            if (actionMessage is null)
            {
                SetLiteralStatus(ex.Message);
            }
            else
            {
                SetLiteralStatus(string.Format(GetCulture(), T("installment.center.status.refreshFailed", "{0} (refresh failed: {1})"), actionMessage, ex.Message));
            }

            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CreateInstallmentAsync()
    {
        using var authorization = await AuthorizeAsync(Permissions.PosTerminal.Installments.Create, "create");
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        if (_isRecoveryStateUnknown)
        {
            SetLiteralStatus("分期恢复状态未知，创建入口已锁定；请刷新核对后重试。");
            return;
        }

        await _showCreateAsync(CartSnapshot);
    }
    private bool CanCreateInstallment()
    {
        // 中文说明：创建入口只要求当前有可分期订单；提交阶段会继续校验在线状态并给出明确提示。
        return !IsBusy && !_isRecoveryStateUnknown && CartSnapshot is { ActualAmount: > 0m };
    }

    private async Task AddRepaymentAsync()
    {
        var selectedOrder = SelectedOrder;
        if (selectedOrder is null || !CanAddRepayment())
        {
            return;
        }

        var paymentGuid = _repaymentPaymentGuid;
        var method = RepaymentMethod;
        var amount = RepaymentAmount;
        var reference = RepaymentReference;
        var voucherToken = RepaymentVoucherToken;
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Installments.AddRepayment, "add-repayment");
        if (permissionGrant is null)
        {
            return;
        }
        using var authorizationActivation = permissionGrant.Activate();

        // 授权期间可能切换订单或修改补款草稿；任何漂移都必须重新发起，避免写入错误订单。
        if (SelectedOrder?.OrderId != selectedOrder.OrderId ||
            IsSelectedOrderLocked ||
            !selectedOrder.CanAddRepayment ||
            _repaymentPaymentGuid != paymentGuid ||
            RepaymentMethod != method ||
            RepaymentAmount != amount ||
            !string.Equals(RepaymentReference, reference, StringComparison.Ordinal) ||
            !string.Equals(RepaymentVoucherToken, voucherToken, StringComparison.Ordinal) ||
            !CanAddRepayment())
        {
            return;
        }

        var orderId = selectedOrder.OrderId;
        var payment = new InstallmentPaymentDraft(
            paymentGuid,
            method,
            amount,
            Normalize(reference),
            Normalize(voucherToken),
            IdempotencyKey: _repaymentIdempotencyKey ??= $"{orderId:D}:repayment:{paymentGuid:D}");

        var result = await RunOrderActionAsync(
            () => _installmentOrderService.AddRepaymentAsync(new InstallmentOrderRepaymentRequest(orderId, Session, payment)),
            OperationAuditTypes.InstallmentRepaymentComplete,
            "REPAYMENT",
            payment.Method.ToString(),
            payment.Amount,
            orderId);
        if (!result.RequiresReview)
        {
            ResetRepaymentOperation();
        }
    }

    private bool CanAddRepayment() => !IsBusy &&
        !IsOffline &&
        !IsSelectedOrderLocked &&
        SelectedOrder is { CanAddRepayment: true } &&
        RepaymentAmount > 0m &&
        RepaymentAmount <= SelectedOrder.OutstandingAmount &&
        (RepaymentMethod != PaymentMethodKind.Voucher || (!string.IsNullOrWhiteSpace(RepaymentReference) && !string.IsNullOrWhiteSpace(RepaymentVoucherToken)));
    private async Task CancelWithRefundAsync()
    {
        var selectedOrder = SelectedOrder;
        if (selectedOrder is null || !CanCancelWithRefund())
        {
            return;
        }

        using var authorization = await AuthorizeAsync(Permissions.PosTerminal.Installments.Cancel, "cancel-with-refund");
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        // 授权返回后再次确认原订单仍被选中且未被恢复锁定。
        if (SelectedOrder?.OrderId == selectedOrder.OrderId &&
            !IsSelectedOrderLocked &&
            selectedOrder.CanCancelWithRefund)
        {
            var orderId = selectedOrder.OrderId;
            await RunOrderActionAsync(
                () => _installmentOrderService.CancelWithRefundAsync(orderId, Session),
                OperationAuditTypes.InstallmentRepaymentCancel,
                "CANCEL_WITH_REFUND",
                orderGuid: orderId);
        }
    }

    private bool CanCancelWithRefund() => !IsBusy && !IsOffline && !IsSelectedOrderLocked && SelectedOrder is { CanCancelWithRefund: true };
    private async Task VoidCancelAsync()
    {
        var selectedOrder = SelectedOrder;
        if (selectedOrder is null)
        {
            return;
        }

        using var authorization = await AuthorizeAsync(Permissions.PosTerminal.Installments.Cancel, "void-cancel");
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        // 授权期间可能切换订单；只有原订单仍被选中且锁状态明确时才允许写入。
        if (SelectedOrder?.OrderId == selectedOrder.OrderId &&
            !IsSelectedOrderLocked &&
            selectedOrder.CanVoidCancel)
        {
            var orderId = selectedOrder.OrderId;
            await RunOrderActionAsync(
                () => _installmentOrderService.VoidCancelAsync(orderId, Session, VoidReason),
                OperationAuditTypes.InstallmentRepaymentCancel,
                "VOID",
                orderGuid: orderId);
        }
    }

    private bool CanVoidCancel() => !IsBusy && !IsOffline && !IsSelectedOrderLocked && SelectedOrder is { CanVoidCancel: true };
    private async Task ConfirmPickupAsync()
    {
        var selectedOrder = SelectedOrder;
        if (selectedOrder is null)
        {
            return;
        }

        using var authorization = await AuthorizeAsync(Permissions.PosTerminal.Installments.ConfirmPickup, "confirm-pickup");
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        // 提货同样必须在授权完成后重新确认选择和锁状态，避免未知金融操作期间推进终态。
        if (SelectedOrder?.OrderId == selectedOrder.OrderId &&
            !IsSelectedOrderLocked &&
            selectedOrder.CanConfirmPickup)
        {
            var result = await RunOrderActionAsync(
                () => _installmentOrderService.ConfirmPickupAsync(selectedOrder.OrderId, Session),
                OperationAuditTypes.InstallmentPickupConfirm,
                "PICKUP",
                orderGuid: selectedOrder.OrderId);
            if (result.RequiresReview)
            {
                // durable operation 已落盘；同步内存锁，避免等待下一次加载期间再次点击。
                _lockedInstallmentGuids.Add(selectedOrder.OrderId);
                OnPropertyChanged(nameof(IsSelectedOrderLocked));
                RaiseSelectionStateChanged();
            }
        }
    }

    private bool CanConfirmPickup() => !IsBusy && !IsOffline && !IsSelectedOrderLocked && SelectedOrder is { CanConfirmPickup: true };

    private async Task ResolveRefundBySupervisorAsync()
    {
        var selectedOrder = SelectedOrder;
        var selectedRefundStep = SelectedRefundStep;
        if (selectedOrder is null ||
            selectedRefundStep is null ||
            _refundStepsInstallmentGuid != selectedOrder.OrderId)
        {
            return;
        }

        using var authorization = await AuthorizeAsync(Permissions.PosTerminal.Installments.Cancel, "supervisor-refund-resolution");
        if (authorization is null)
        {
            return;
        }

        using var authorizationActivation = authorization.Activate();
        // 主管授权期间若选择、步骤归属或锁状态发生变化，旧步骤不得作用到新订单。
        if (SelectedOrder?.OrderId != selectedOrder.OrderId ||
            SelectedRefundStep?.RefundStepGuid != selectedRefundStep.RefundStepGuid ||
            _refundStepsInstallmentGuid != selectedOrder.OrderId ||
            !IsSelectedOrderLocked)
        {
            return;
        }

        var authorizer = OperationAuthorizationScope.CurrentAuthorizingSession ?? Session.CashierSession;
        var decision = SupervisorRefundDecision;
        var resolution = new InstallmentRefundSupervisorResolution(
            decision,
            authorizer?.CashierId ?? Session.CashierId,
            Normalize(SupervisorRefundReason) ?? string.Empty,
            Normalize(SupervisorRefundEvidence),
            Normalize(SupervisorRefundReference),
            authorizer?.UserGuid ?? Session.CashierSession?.UserGuid,
            authorizer?.CashierName ?? Session.CashierName);
        var resolutionSaved = false;

        try
        {
            IsBusy = true;
            var resolved = await _installmentOrderService.ResolveRefundStepAsync(selectedRefundStep.RefundStepGuid, resolution);
            resolutionSaved = resolved;
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.InstallmentRepaymentCancel,
                resolved ? "SupervisorResolved" : "SupervisorResolutionRejected",
                Session,
                reasonCode: decision.ToString(),
                safeMessage: $"reason={resolution.Reason}; evidence={resolution.Evidence ?? "-"}; refundReference={resolution.RefundReference ?? "-"}",
                orderGuid: selectedOrder.OrderId.ToString("D"));
            if (!resolved)
            {
                SetLiteralStatus("该退款步骤正在处理或已结案，保持锁定。 ");
                return;
            }

            if (decision != InstallmentRefundSupervisorDecision.ContinueWaiting)
            {
                var result = await _installmentOrderService.ResumeCancelAfterSupervisorAsync(selectedRefundStep.OperationGuid, selectedOrder.OrderNumber, Session);
                SetLiteralStatus(result.Message);
                if (result.Succeeded && result.Order is not null)
                {
                    AppendOrUpdateOrder(result.Order);
                }
            }
            else
            {
                SetLiteralStatus("已记录继续等待，退款保持锁定。 ");
            }
        }
        catch (ArgumentException exception)
        {
            SetLiteralStatus(exception.Message);
        }
        catch (OperationCanceledException exception)
        {
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.InstallmentRepaymentCancel,
                resolutionSaved ? "SupervisorRecoveryFailed" : "SupervisorResolutionUnknown",
                Session,
                reasonCode: decision.ToString(),
                safeMessage: exception.GetType().Name,
                orderGuid: selectedOrder.OrderId.ToString("D"));
            SetLiteralStatus(resolutionSaved
                ? "主管裁决已保存但恢复查询超时，退款保持锁定，请刷新核对。"
                : "主管裁决请求超时，结果未知，退款保持锁定，请刷新核对。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.InstallmentRepaymentCancel,
                "SupervisorResolutionFailed",
                Session,
                reasonCode: decision.ToString(),
                safeMessage: exception.GetType().Name,
                orderGuid: selectedOrder.OrderId.ToString("D"));
            SetLiteralStatus("主管结案未保存，退款保持锁定。 ");
        }
        finally
        {
            IsBusy = false;
            await RefreshSupervisorRefundStepsSafelyAsync(selectedOrder.OrderId);
            await RefreshLockedInstallmentsSafelyAsync();
        }
    }

    private bool CanResolveRefundBySupervisor() =>
        !IsBusy &&
        !IsOffline &&
        IsRefundSupervisorAuthorized &&
        IsSelectedOrderLocked &&
        SelectedOrder is not null &&
        SelectedRefundStep is not null &&
        _refundStepsInstallmentGuid == SelectedOrder.OrderId;

    private bool TryRequirePermission(string permissionCode)
    {
        if ((!_enforcePermissions && _cashierSessionContext.CurrentSession is null && Session.CashierSession is null) ||
            _cashierSessionContext.RequirePermission(permissionCode, out var message))
        {
            return true;
        }

        var operationType = permissionCode switch
        {
            Permissions.PosTerminal.Installments.AddRepayment => OperationAuditTypes.InstallmentRepaymentComplete,
            Permissions.PosTerminal.Installments.Cancel => OperationAuditTypes.InstallmentRepaymentCancel,
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
                orderGuid: SelectedOrder?.OrderId.ToString("D"));
        }

        SetLiteralStatus(message);
        return false;
    }

    private Task<ViewModelAuthorizationGrant?> AuthorizeAsync(string permissionCode, string action) =>
        ViewModelOperationAuthorization.AuthorizeAsync(
            _operationAuthorizationService,
            TryRequirePermission,
            permissionCode,
            "installment-center",
            action,
            Session);

    private async Task<InstallmentOrderActionResult> RunOrderActionAsync(
        Func<Task<InstallmentOrderActionResult>> action,
        string? operationType = null,
        string? reasonCode = null,
        string? paymentMethod = null,
        decimal? paymentAmount = null,
        Guid? orderGuid = null)
    {
        IsBusy = true;
        try
        {
            var result = await action();
            if (operationType is not null)
            {
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    operationType,
                    result.Succeeded ? "Succeeded" : "Failed",
                    Session,
                    reasonCode: reasonCode,
                    safeMessage: result.Succeeded ? null : result.Message,
                    paymentMethod: paymentMethod,
                    paymentAmount: paymentAmount,
                    orderGuid: orderGuid?.ToString("D"));
            }

            SetLiteralStatus(result.Message);
            if (result.Succeeded)
            {
                await LoadCoreAsync(() => _installmentOrderService.SearchAsync(Session, SearchText), "installment.center.status.searched", "Found {0} installment orders.", result.Message);
            }

            return result;
        }
        catch (Exception ex)
        {
            if (operationType is not null)
            {
                var correlation = OperationAuditEvents.CreateCorrelation();
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    operationType,
                    "Failed",
                    Session,
                    reasonCode: reasonCode,
                    safeMessage: ex.GetType().Name,
                    paymentMethod: paymentMethod,
                    paymentAmount: paymentAmount,
                    orderGuid: orderGuid?.ToString("D"),
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
                ConsoleLog.WriteError(
                    "InstallmentAudit",
                    $"installment operation failed operation={operationType} error={ex.GetType().Name}",
                    new ApplicationLogContext(TraceId: correlation.TraceId),
                    ex);
            }

            SetLiteralStatus(ex.Message);
            return new InstallmentOrderActionResult(false, ex.Message, RequiresReview: ex is OperationCanceledException);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<string?> RecoverPendingOperationsAsync()
    {
        if (!Session.IsOnline)
        {
            return null;
        }

        try
        {
            var recovered = await _installmentOrderService.RecoverPendingOperationsAsync(Session);
            var lockedInstallments = await _installmentOrderService.GetLockedInstallmentGuidsAsync(Session);
            _isRecoveryStateUnknown = false;
            _lockedInstallmentGuids.Clear();
            _lockedInstallmentGuids.UnionWith(lockedInstallments);
            OnPropertyChanged(nameof(IsSelectedOrderLocked));
            RaiseSelectionStateChanged();
            await RefreshSupervisorRefundStepsSafelyAsync(SelectedOrder?.OrderId);
            var locked = recovered.Count(item => item.State == LocalInstallmentOperationState.ResultUnknown);
            if (locked > 0)
            {
                SetLiteralStatus($"{locked} 个分期支付结果未知，已锁定，请勿重复收款。");
            }
        }
        catch (OperationCanceledException)
        {
            // 恢复请求超时不代表远端操作未完成；优先刷新锁，刷新失败时保留现有锁集合。
            _isRecoveryStateUnknown = true;
            await RefreshLockedInstallmentsSafelyAsync();
            const string timeoutStatus = "分期恢复超时，操作保持锁定，请勿重复收款；请刷新核对。";
            SetLiteralStatus(timeoutStatus);
            return timeoutStatus;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _isRecoveryStateUnknown = true;
            await RefreshLockedInstallmentsSafelyAsync();
            var failureStatus = $"分期恢复失败：{ex.GetType().Name}。操作保持锁定，请勿重复收款；请刷新核对。";
            SetLiteralStatus(failureStatus);
            return failureStatus;
        }

        return null;
    }

    private async Task RefreshSupervisorRefundStepsSafelyAsync(Guid? installmentGuid)
    {
        if (SelectedOrder?.OrderId != installmentGuid)
        {
            return;
        }

        try
        {
            var steps = installmentGuid is null
                ? []
                : await _installmentOrderService.GetRefundStepsForReviewAsync(installmentGuid.Value);
            if (SelectedOrder?.OrderId != installmentGuid)
            {
                return;
            }

            RefundStepsForReview.ReplaceWith(steps);
            _refundStepsInstallmentGuid = installmentGuid;
            SelectedRefundStep = RefundStepsForReview.FirstOrDefault();
            OnPropertyChanged(nameof(HasRefundStepsForReview));
            RaiseSelectionStateChanged();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (SelectedOrder?.OrderId != installmentGuid)
            {
                return;
            }

            RefundStepsForReview.Clear();
            _refundStepsInstallmentGuid = null;
            SelectedRefundStep = null;
            OnPropertyChanged(nameof(HasRefundStepsForReview));
            ConsoleLog.WriteError("InstallmentSupervisor", "failed to load refund review steps", null, exception);
        }
        catch (OperationCanceledException exception)
        {
            // 超时时保留已有复核步骤，避免把仍需主管核对的退款误显示为已清空。
            ConsoleLog.WriteError("InstallmentSupervisor", "timed out while loading refund review steps", null, exception);
        }
    }

    private async Task<bool> RefreshLockedInstallmentsSafelyAsync()
    {
        try
        {
            var lockedInstallments = await _installmentOrderService.GetLockedInstallmentGuidsAsync(Session);
            _lockedInstallmentGuids.Clear();
            _lockedInstallmentGuids.UnionWith(lockedInstallments);
            OnPropertyChanged(nameof(IsSelectedOrderLocked));
            RaiseSelectionStateChanged();
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 锁状态无法读取时必须 fail-closed，防止未知支付/退款重新开放操作入口。
            _isRecoveryStateUnknown = true;
            OnPropertyChanged(nameof(IsSelectedOrderLocked));
            RaiseSelectionStateChanged();
            ConsoleLog.WriteError("InstallmentSupervisor", "failed to refresh installment locks", null, exception);
            return false;
        }
        catch (OperationCanceledException exception)
        {
            // 首次加载可能没有可保留的锁集合，因此同时设置全局未知状态并关闭操作入口。
            _isRecoveryStateUnknown = true;
            OnPropertyChanged(nameof(IsSelectedOrderLocked));
            RaiseSelectionStateChanged();
            ConsoleLog.WriteError("InstallmentSupervisor", "timed out while refreshing installment locks", null, exception);
            return false;
        }
    }

    private void ResetRepaymentOperation()
    {
        _repaymentPaymentGuid = Guid.NewGuid();
        _repaymentIdempotencyKey = null;
    }

    private void RaiseSelectionStateChanged()
    {
        CreateInstallmentCommand.NotifyCanExecuteChanged();
        AddRepaymentCommand.NotifyCanExecuteChanged();
        CancelWithRefundCommand.NotifyCanExecuteChanged();
        VoidCancelCommand.NotifyCanExecuteChanged();
        ConfirmPickupCommand.NotifyCanExecuteChanged();
        RecoveryCommand.NotifyCanExecuteChanged();
        SupervisorResolveRefundCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsCreateEnabled));
        OnPropertyChanged(nameof(IsAddRepaymentEnabled));
        OnPropertyChanged(nameof(IsCancelWithRefundEnabled));
        OnPropertyChanged(nameof(IsVoidCancelEnabled));
        OnPropertyChanged(nameof(IsConfirmPickupEnabled));
        OnPropertyChanged(nameof(IsSupervisorResolutionEnabled));
        OnPropertyChanged(nameof(IsOffline));
    }

    private void RaiseLocalizedProperties()
    {
        RefreshPaymentMethodOptions();
        if (_statusResourceKey is not null)
        {
            StatusMessage = FormatResource(_statusResourceKey, _statusFallback, _statusResourceArgs);
        }

        OnPropertyChanged(nameof(PageTitleText));
        OnPropertyChanged(nameof(CurrentOrderSummaryText));
        OnPropertyChanged(nameof(CreateInstallmentText));
        OnPropertyChanged(nameof(AddRepaymentText));
        OnPropertyChanged(nameof(CancelWithRefundText));
        OnPropertyChanged(nameof(VoidCancelText));
        OnPropertyChanged(nameof(ConfirmPickupText));
        OnPropertyChanged(nameof(LoadText));
        OnPropertyChanged(nameof(SearchButtonText));
        OnPropertyChanged(nameof(SearchTextLabel));
        OnPropertyChanged(nameof(BackToPaymentText));
        OnPropertyChanged(nameof(OfflineNoticeText));
        OnPropertyChanged(nameof(SelectedOrderNumberText));
        OnPropertyChanged(nameof(SelectedOrderCustomerText));
        OnPropertyChanged(nameof(SelectedOrderOutstandingText));
    }

    private void OnCultureChanged(object? sender, EventArgs args) => RaiseLocalizedProperties();

    private void RefreshPaymentMethodOptions()
    {
        PaymentMethodOptions.Clear();
        PaymentMethodOptions.Add(new InstallmentPaymentMethodOption(PaymentMethodKind.Cash, T("payment.method.cash", "Cash")));
        PaymentMethodOptions.Add(new InstallmentPaymentMethodOption(PaymentMethodKind.Card, T("payment.method.card", "Credit/Debit Card")));
        PaymentMethodOptions.Add(new InstallmentPaymentMethodOption(PaymentMethodKind.Voucher, T("payment.method.voucher", "Voucher")));
        OnPropertyChanged(nameof(PaymentMethodOptions));
    }

    private void SetStatusResource(string key, string fallback, params object[] args)
    {
        _statusResourceKey = key;
        _statusFallback = fallback;
        _statusResourceArgs = args;
        StatusMessage = FormatResource(key, fallback, args);
    }

    private void SetLiteralStatus(string value)
    {
        _statusResourceKey = null;
        _statusFallback = string.Empty;
        _statusResourceArgs = [];
        StatusMessage = value;
    }

    private string FormatResource(string key, string fallback, object[] args)
    {
        var format = T(key, fallback);
        return args.Length == 0 ? format : string.Format(GetCulture(), format, args);
    }

    private string T(string key, string fallback)
    {
        var value = _localization?.T(key);
        return IsMissingLocalizedValue(key, value) ? fallback : value!;
    }

    private static bool IsMissingLocalizedValue(string key, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ||
            value == key ||
            (value.StartsWith("[[", StringComparison.Ordinal) && value.EndsWith("]]", StringComparison.Ordinal));
    }

    private IFormatProvider GetCulture() => _localization?.CurrentCulture ?? System.Globalization.CultureInfo.CurrentCulture;
    private static string? Normalize(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        var onCultureChanged = Interlocked.Exchange(ref _onCultureChanged, null);
        if (_localization is not null && onCultureChanged is not null)
        {
            _localization.CultureChanged -= onCultureChanged;
        }
    }
}
