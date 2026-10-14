using System.Collections.ObjectModel;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class InstallmentCreateViewModel : ObservableObject, IDisposable
{
    private const decimal MinimumInstallmentTotalAmount = 50m;
    private const decimal MinimumDownPaymentAmount = 20m;

    private readonly IInstallmentOrderService _installmentOrderService;
    private readonly Func<InstallmentOrderSummary, Task> _onCreatedAsync;
    private readonly Action _backToCenter;
    private readonly ILocalizationService? _localization;
    private readonly ICashierSessionContext _cashierSessionContext;
    private readonly bool _enforcePermissions;
    private readonly IOperationAuditLogger? _operationAuditLogger;
    private readonly IOperationAuthorizationService? _operationAuthorizationService;
    private string? _statusResourceKey;
    private string _statusFallback = string.Empty;
    private object[] _statusResourceArgs = [];
    private Guid _createInstallmentGuid = Guid.NewGuid();
    private Guid _createPaymentGuid = Guid.NewGuid();
    private string _createIdempotencyKey = Guid.NewGuid().ToString("N");
    private bool _disposed;

    [ObservableProperty]
    private PosSessionState _session;

    [ObservableProperty]
    private PosCartServiceSnapshot? _cartSnapshot;

    [ObservableProperty]
    private string _customerName = string.Empty;

    [ObservableProperty]
    private string _customerPhone = string.Empty;

    [ObservableProperty]
    private decimal _downPaymentAmount;

    [ObservableProperty]
    private PaymentMethodKind _downPaymentMethod = PaymentMethodKind.Cash;

    [ObservableProperty]
    private string _downPaymentReference = string.Empty;

    [ObservableProperty]
    private string _voucherReservationToken = string.Empty;

    [ObservableProperty]
    private string _note = string.Empty;

    [ObservableProperty]
    private bool _isSubmitting;

    [ObservableProperty]
    private bool _isCreateRecoveryRequired;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public InstallmentCreateViewModel(
        IInstallmentOrderService installmentOrderService,
        PosSessionState session,
        Func<InstallmentOrderSummary, Task> onCreatedAsync,
        Action backToCenter,
        ILocalizationService? localization = null,
        ICashierSessionContext? cashierSessionContext = null,
        bool enforcePermissionsWhenNoCashier = false,
        IOperationAuditLogger? operationAuditLogger = null,
        IOperationAuthorizationService? operationAuthorizationService = null)
    {
        _installmentOrderService = installmentOrderService;
        _session = session;
        _onCreatedAsync = onCreatedAsync;
        _backToCenter = backToCenter;
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
            _localization.CultureChanged += OnCultureChanged;
        }

        SubmitCommand = new AsyncRelayCommand(SubmitAsync, CanSubmit);
        BackToCenterCommand = new RelayCommand(BackToCenter);
        RefreshPaymentMethodOptions();
        SetStatusResource("installment.create.status.ready", "Complete the customer, down payment, and lay-by details.");
    }

    public ObservableCollection<PosCartLineServiceSnapshot> CartLines { get; } = [];

    public IAsyncRelayCommand SubmitCommand { get; }

    public IRelayCommand BackToCenterCommand { get; }

    public ObservableCollection<InstallmentPaymentMethodOption> PaymentMethodOptions { get; } = [];

    public string PageTitleText => T("installment.create.title", "Create Lay-by");

    public string BackToCenterText => T("installment.create.action.back", "Back to Lay-by Center");

    public string SubmitText => T("installment.create.action.submit", "Create Lay-by Order");

    public string OfflineNoticeText => T("installment.create.offline", "Lay-by orders cannot be created while offline.");

    public string CustomerSectionText => T("installment.create.section.customer", "Customer Information");

    public string CartSectionText => T("installment.create.section.cart", "Cart Details");

    public string PaymentSectionText => T("installment.create.section.payment", "Down Payment");

    public string OrderTotalLabelText => T("installment.create.order.total", "Order Total");

    public string FinancedAmountText => string.Format(GetCulture(), T("installment.create.order.financed", "Financed {0:C2}"), FinancedAmount);

    public string DownPaymentMethodText => PaymentMethodOptions
        .FirstOrDefault(option => option.Method == DownPaymentMethod)?.DisplayName ?? DownPaymentMethod.ToString();

    public bool IsOffline => !Session.IsOnline;

    public bool IsVoucherPaymentSelected => DownPaymentMethod == PaymentMethodKind.Voucher;

    public decimal GoodsAmount => CartSnapshot?.TotalAmount ?? 0m;

    public decimal DiscountAmount => CartSnapshot?.DiscountAmount ?? 0m;

    public decimal TotalAmount => CartSnapshot?.ActualAmount ?? 0m;

    public decimal FinancedAmount => Math.Max(0m, TotalAmount - DownPaymentAmount);

    public string DownPaymentStatusText => BuildDownPaymentStatusText();

    public bool IsSubmitEnabled => CanSubmit();

    partial void OnSessionChanged(PosSessionState value)
    {
        if (value.CashierSession is not null)
        {
            _cashierSessionContext.SetCurrent(value.CashierSession);
        }

        RaiseActionStateChanged();
    }

    partial void OnCartSnapshotChanged(PosCartServiceSnapshot? value)
    {
        CartLines.ReplaceWith(value?.Lines ?? []);
        if (value is not null && DownPaymentAmount > value.ActualAmount)
        {
            DownPaymentAmount = value.ActualAmount;
        }

        RaiseAmountStateChanged();
        RaiseActionStateChanged();
    }

    partial void OnDownPaymentAmountChanged(decimal value)
    {
        if (value < 0m)
        {
            DownPaymentAmount = 0m;
            return;
        }

        if (TotalAmount > 0m && value > TotalAmount)
        {
            DownPaymentAmount = TotalAmount;
            return;
        }

        RaiseAmountStateChanged();
        RaiseActionStateChanged();
    }

    partial void OnDownPaymentMethodChanged(PaymentMethodKind value)
    {
        if (value != PaymentMethodKind.Voucher)
        {
            VoucherReservationToken = string.Empty;
        }

        OnPropertyChanged(nameof(IsVoucherPaymentSelected));
        OnPropertyChanged(nameof(DownPaymentMethodText));
        RaiseActionStateChanged();
    }

    partial void OnDownPaymentReferenceChanged(string value)
    {
        RaiseActionStateChanged();
    }

    partial void OnVoucherReservationTokenChanged(string value)
    {
        RaiseActionStateChanged();
    }

    partial void OnCustomerNameChanged(string value)
    {
        RaiseActionStateChanged();
    }

    partial void OnCustomerPhoneChanged(string value)
    {
        RaiseActionStateChanged();
    }

    partial void OnIsSubmittingChanged(bool value)
    {
        RaiseActionStateChanged();
    }

    partial void OnIsCreateRecoveryRequiredChanged(bool value)
    {
        RaiseActionStateChanged();
    }

    public void Prepare(PosSessionState session, PosCartServiceSnapshot? cartSnapshot)
    {
        Session = session;
        CartSnapshot = cartSnapshot;
        DownPaymentMethod = PaymentMethodKind.Cash;
        DownPaymentReference = string.Empty;
        VoucherReservationToken = string.Empty;
        DownPaymentAmount = CalculateDefaultDownPayment(cartSnapshot?.ActualAmount ?? 0m);
        CustomerName = string.Empty;
        CustomerPhone = string.Empty;
        Note = string.Empty;
        if (cartSnapshot is null)
        {
            SetStatusResource("installment.create.status.missingCart", "There is no current order available for lay-by creation.");
        }
        else
        {
            SetStatusResource("installment.create.status.ready", "Complete the customer, down payment, and lay-by details.");
        }

        RaiseAmountStateChanged();
        RaiseActionStateChanged();
    }

    private async Task SubmitAsync()
    {
        using var authorization = await ViewModelOperationAuthorization.AuthorizeAsync(
            _operationAuthorizationService,
            TryRequirePermission,
            Permissions.PosTerminal.Installments.Create,
            "installment-create",
            "submit",
            Session);
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        var cartSnapshot = CartSnapshot;
        if (cartSnapshot is null)
        {
            SetStatusResource("installment.create.status.missingCart", "There is no current order available for lay-by creation.");
            return;
        }

        IsSubmitting = true;
        var auditBefore = CaptureAuditSnapshot(cartSnapshot);
        try
        {
            // ViewModel 只收集 UI 输入，请求对象由客户端服务统一落地并提交。
            var request = new InstallmentOrderCreateRequest(
                Session,
                cartSnapshot,
                CustomerName.Trim(),
                CustomerPhone.Trim(),
                DownPaymentAmount,
                new InstallmentPaymentDraft(
                    _createPaymentGuid,
                    DownPaymentMethod,
                    DownPaymentAmount,
                    NormalizeOptional(DownPaymentReference),
                    NormalizeOptional(VoucherReservationToken),
                    IdempotencyKey: _createIdempotencyKey),
                Note.Trim(),
                _createInstallmentGuid);
            var result = await _installmentOrderService.CreateOrderAsync(request);
            SetLiteralStatus(result.Message);
            if (result.Succeeded && result.Order is not null)
            {
                OperationAuditEvents.RecordCartChange(
                    _operationAuditLogger,
                    OperationAuditTypes.SaleComplete,
                    Session,
                    auditBefore,
                    new OperationAuditCartSnapshot(0m, 0m, 0m, []),
                    reasonCode: "INSTALLMENT_CREATE",
                    paymentMethod: $"Installment+{DownPaymentMethod}",
                    paymentAmount: DownPaymentAmount,
                    orderGuid: result.Order.OrderId.ToString("D"));

                await _onCreatedAsync(result.Order);
                ResetCreateIdentity();
            }
            else
            {
                if (result.RequiresReview)
                {
                    // 结果未知时保留原金融身份并锁定页面，恢复流程只能沿用原 GUID 和幂等键。
                    IsCreateRecoveryRequired = true;
                }

                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.SaleComplete,
                    "Failed",
                    Session,
                    auditBefore,
                    reasonCode: "INSTALLMENT_CREATE",
                    safeMessage: result.Message,
                    paymentMethod: $"Installment+{DownPaymentMethod}",
                    paymentAmount: DownPaymentAmount);
            }
        }
        catch (Exception ex)
        {
            var correlation = OperationAuditEvents.CreateCorrelation();
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.SaleComplete,
                "Failed",
                Session,
                auditBefore,
                reasonCode: "INSTALLMENT_CREATE_EXCEPTION",
                safeMessage: ex.GetType().Name,
                paymentMethod: $"Installment+{DownPaymentMethod}",
                paymentAmount: DownPaymentAmount,
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            ConsoleLog.WriteError(
                "InstallmentAudit",
                $"installment create failed error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            SetLiteralStatus(ex.Message);
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private bool CanSubmit()
    {
        return !IsSubmitting &&
            !IsCreateRecoveryRequired &&
            !IsOffline &&
            CartSnapshot is not null &&
            CartSnapshot.Lines.Count > 0 &&
            IsValidInstallmentTotal() &&
            !string.IsNullOrWhiteSpace(CustomerName) &&
            !string.IsNullOrWhiteSpace(CustomerPhone) &&
            IsValidDownPayment() &&
            (DownPaymentMethod != PaymentMethodKind.Voucher ||
             (!string.IsNullOrWhiteSpace(DownPaymentReference) && !string.IsNullOrWhiteSpace(VoucherReservationToken)));
    }

    private bool IsValidInstallmentTotal()
    {
        return TotalAmount >= MinimumInstallmentTotalAmount;
    }

    private bool IsValidDownPayment()
    {
        if (DownPaymentAmount <= 0m || DownPaymentAmount > TotalAmount)
        {
            return false;
        }

        return DownPaymentAmount >= MinimumDownPaymentAmount;
    }

    private void BackToCenter()
    {
        _backToCenter();
    }

    private void ResetCreateIdentity()
    {
        _createInstallmentGuid = Guid.NewGuid();
        _createPaymentGuid = Guid.NewGuid();
        _createIdempotencyKey = Guid.NewGuid().ToString("N");
        IsCreateRecoveryRequired = false;
    }

    private void RaiseAmountStateChanged()
    {
        OnPropertyChanged(nameof(GoodsAmount));
        OnPropertyChanged(nameof(DiscountAmount));
        OnPropertyChanged(nameof(TotalAmount));
        OnPropertyChanged(nameof(FinancedAmount));
        OnPropertyChanged(nameof(FinancedAmountText));
        OnPropertyChanged(nameof(DownPaymentMethodText));
        OnPropertyChanged(nameof(DownPaymentStatusText));
    }

    private void RaiseActionStateChanged()
    {
        SubmitCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(IsSubmitEnabled));
        OnPropertyChanged(nameof(IsVoucherPaymentSelected));
        OnPropertyChanged(nameof(DownPaymentMethodText));
        OnPropertyChanged(nameof(DownPaymentStatusText));
    }

    private void RaiseLocalizedProperties()
    {
        RefreshPaymentMethodOptions();
        if (_statusResourceKey is not null)
        {
            StatusMessage = FormatResource(_statusResourceKey, _statusFallback, _statusResourceArgs);
        }

        OnPropertyChanged(nameof(PageTitleText));
        OnPropertyChanged(nameof(BackToCenterText));
        OnPropertyChanged(nameof(SubmitText));
        OnPropertyChanged(nameof(OfflineNoticeText));
        OnPropertyChanged(nameof(CustomerSectionText));
        OnPropertyChanged(nameof(CartSectionText));
        OnPropertyChanged(nameof(PaymentSectionText));
        OnPropertyChanged(nameof(OrderTotalLabelText));
        OnPropertyChanged(nameof(FinancedAmountText));
        OnPropertyChanged(nameof(DownPaymentMethodText));
        OnPropertyChanged(nameof(DownPaymentStatusText));
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RaiseLocalizedProperties();
    }

    private string BuildDownPaymentStatusText()
    {
        if (IsOffline)
        {
            return T("installment.create.payment.status.offline", "Offline mode cannot submit a down payment.");
        }

        if (!IsValidInstallmentTotal())
        {
            // 中文说明：分期只服务达到最低金额门槛的订单，小额订单引导用户走普通支付。
            return T("installment.create.status.totalBelowMinimum", "Lay-by order total must be at least $50. Use regular payment for orders below $50.");
        }

        if (DownPaymentAmount < MinimumDownPaymentAmount)
        {
            // 中文说明：首付门槛先于支付方式提示展示，避免按钮禁用但用户不知道原因。
            return T("installment.create.payment.status.downPaymentBelowMinimum", "First payment must be at least $20.");
        }

        return DownPaymentMethod switch
        {
            PaymentMethodKind.Cash => T("installment.create.payment.status.cash", "Cash down payment does not require an additional reference."),
            PaymentMethodKind.Card => string.IsNullOrWhiteSpace(DownPaymentReference)
                ? T("installment.create.payment.status.card.empty", "Card down payment may include a transaction reference for reconciliation.")
                : T("installment.create.payment.status.card.ready", "Card down payment reference is ready."),
            PaymentMethodKind.Voucher when string.IsNullOrWhiteSpace(DownPaymentReference) =>
                T("installment.create.payment.status.voucher.missingCode", "Enter a voucher code."),
            PaymentMethodKind.Voucher when string.IsNullOrWhiteSpace(VoucherReservationToken) =>
                T("installment.create.payment.status.voucher.missingToken", "Enter the voucher reservation token."),
            PaymentMethodKind.Voucher =>
                T("installment.create.payment.status.voucher.ready", "Voucher code and reservation token are ready."),
            _ => string.Empty
        };
    }

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

    private bool TryRequirePermission(string permissionCode)
    {
        if ((!_enforcePermissions && _cashierSessionContext.CurrentSession is null && Session.CashierSession is null) ||
            _cashierSessionContext.RequirePermission(permissionCode, out var message))
        {
            return true;
        }

        if (permissionCode == Permissions.PosTerminal.Installments.Create)
        {
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.SaleComplete,
                "Denied",
                Session,
                CartSnapshot is null ? null : CaptureAuditSnapshot(CartSnapshot),
                reasonCode: "PERMISSION_DENIED",
                safeMessage: message,
                paymentMethod: $"Installment+{DownPaymentMethod}",
                paymentAmount: DownPaymentAmount);
        }

        SetLiteralStatus(message);
        return false;
    }

    private static OperationAuditCartSnapshot CaptureAuditSnapshot(PosCartServiceSnapshot snapshot)
    {
        var lines = snapshot.Lines.Select((line, index) => new OperationAuditCartLineSnapshot(
            $"{line.ProductCode}|{line.ReferenceCode}|{line.LookupCode}|Sale|{index}",
            line.ProductCode,
            line.ItemNumber,
            line.ReferenceCode,
            line.LookupCode,
            line.DisplayName,
            "Sale",
            decimal.Round(line.Quantity, 3, MidpointRounding.AwayFromZero),
            decimal.Round(line.UnitPrice, 2, MidpointRounding.AwayFromZero),
            decimal.Round(line.DiscountAmount, 2, MidpointRounding.AwayFromZero),
            decimal.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero),
            decimal.Round(line.ActualAmount, 2, MidpointRounding.AwayFromZero))).ToList();
        return new OperationAuditCartSnapshot(
            decimal.Round(snapshot.TotalAmount, 2, MidpointRounding.AwayFromZero),
            decimal.Round(snapshot.DiscountAmount, 2, MidpointRounding.AwayFromZero),
            decimal.Round(snapshot.ActualAmount, 2, MidpointRounding.AwayFromZero),
            lines);
    }

    private string FormatResource(string key, string fallback, object[] args)
    {
        var format = T(key, fallback);
        return args.Length == 0 ? format : string.Format(GetCulture(), format, args);
    }

    private static decimal CalculateDefaultDownPayment(decimal orderAmount)
    {
        if (orderAmount <= 0m)
        {
            return 0m;
        }

        return orderAmount < MinimumDownPaymentAmount ? orderAmount : MinimumDownPaymentAmount;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_localization is not null)
        {
            _localization.CultureChanged -= OnCultureChanged;
        }
    }
}

public sealed record InstallmentPaymentMethodOption(PaymentMethodKind Method, string DisplayName);
