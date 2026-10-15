using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.ViewModels;

public partial class PaymentViewModel
{
    private IPaymentMethodSettingsService? _paymentMethodSettingsService;
    private PaymentMethodSettings _paymentMethodSettings = new();
    private bool _paymentMethodSettingsReady = true;
    private int _paymentMethodSettingsLoadVersion;
    private System.Windows.Threading.Dispatcher? _paymentMethodSettingsDispatcher;

    public bool IsIntegratedCardPaymentVisible => !_paymentMethodSettings.UseManualCard;
    public bool IsManualCardPaymentVisible => _paymentMethodSettings.UseManualCard;
    public bool IsVoucherPaymentVisible => _paymentMethodSettings.VoucherEnabled;
    // 付款页左侧订单商品列表是否显示（设置页「付款页显示订单列表」，默认显示）；只影响布局，不影响金额与支付流程。
    public bool IsOrderListVisible => _paymentMethodSettings.ShowOrderList;

    private void InitializePaymentMethodSettings(IPaymentMethodSettingsService? service)
    {
        _paymentMethodSettingsService = service;
        // 仅 WPF 调度上下文需要切回 UI，普通异步调用不延迟设置的生效时机。
        _paymentMethodSettingsDispatcher = SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext
            ? System.Windows.Threading.Dispatcher.CurrentDispatcher : null;
        if (service is null) return;
        _paymentMethodSettingsReady = false;
        service.Changed += OnPaymentMethodSettingsChanged;
        _ = RefreshPaymentMethodSettingsAsync();
    }

    public async Task RefreshPaymentMethodSettingsAsync()
    {
        if (_paymentMethodSettingsService is null || _disposed) return;
        var version = ++_paymentMethodSettingsLoadVersion;
        try
        {
            await _paymentMethodSettingsService.LoadAsync();
            if (_disposed || version != _paymentMethodSettingsLoadVersion) return;
            // 读取 Current，避免等待期间较新的保存被旧返回值覆盖。
            _paymentMethodSettingsReady = true;
            ApplyPaymentMethodSettings();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (_disposed || version != _paymentMethodSettingsLoadVersion) return;
            // 读取失败仅禁止新增收款，不解锁未知结果，也不丢弃已确认待保存的款项。
            _paymentMethodSettingsReady = false;
            if (!IsManualCardSavePending && !_cardSession.HasUnknownResult)
                SetStatus("payment.settings.loadFailed");
            OnPropertyChanged(nameof(IsPaymentInteractionEnabled));
            NotifyPaymentCommandStates();
        }
    }

    private async Task RefreshPaymentSettingsForEntryAsync()
    {
        await RefreshPaymentMethodSettingsAsync();
        if (!_disposed) await RefreshLinklyCloudTerminalsAsync();
    }

    private void OnPaymentMethodSettingsChanged(object? sender, EventArgs e)
    {
        if (_paymentMethodSettingsDispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(ApplyPaymentMethodSettings));
            return;
        }
        ApplyPaymentMethodSettings();
    }

    private void ApplyPaymentMethodSettings()
    {
        if (_disposed || _paymentMethodSettingsService is null) return;
        _paymentMethodSettings = _paymentMethodSettingsService.Current;
        if (!IsPaymentInteractionLocked && !_cardSession.IsActive && !IsManualCardSavePending)
        {
            if (!IsManualCardPaymentVisible && IsManualCardDialogOpen) CancelManualCard();
            if (!IsVoucherPaymentVisible && IsVoucherEntryDialogOpen) CancelVoucherEntry();
            if (!IsPaymentMethodEnabled(SelectedPaymentMethod)) SelectedPaymentMethod = PaymentMethodKind.Cash;
        }
        // 设置只改变后续入口，已开始的交易和恢复/保存身份保持原状。
        OnPropertyChanged(nameof(IsIntegratedCardPaymentVisible));
        OnPropertyChanged(nameof(IsManualCardPaymentVisible));
        OnPropertyChanged(nameof(IsVoucherPaymentVisible));
        OnPropertyChanged(nameof(IsOrderListVisible));
        OnPropertyChanged(nameof(IsVoucherCodeEntryVisible));
        OnPropertyChanged(nameof(IsPaymentInteractionEnabled));
        OnPropertyChanged(nameof(IsLinklyCloudTerminalSelectorVisible));
        OnPropertyChanged(nameof(CanSwitchLinklyCloudTerminal));
        NotifyPaymentCommandStates();
    }

    private bool IsPaymentMethodEnabled(PaymentMethodKind method) => _paymentMethodSettingsReady && method switch
    {
        PaymentMethodKind.Card => IsIntegratedCardPaymentVisible,
        PaymentMethodKind.Voucher => IsVoucherPaymentVisible,
        _ => true
    };

    private bool EnsurePaymentMethodEnabled(PaymentMethodKind method)
    {
        if (IsPaymentMethodEnabled(method)) return true;
        SetStatus(_paymentMethodSettingsReady ? "payment.settings.methodDisabled" : "payment.settings.loadFailed");
        return false;
    }
}
