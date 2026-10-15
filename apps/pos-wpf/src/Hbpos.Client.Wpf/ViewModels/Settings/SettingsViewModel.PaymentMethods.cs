using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class SettingsViewModel
{
    private readonly IPaymentMethodSettingsService? _paymentMethodSettingsService;

    [ObservableProperty]
    private bool _useManualCard;

    [ObservableProperty]
    private bool _voucherEnabled;

    // 付款页是否在左侧列出订单商品明细；与 PaymentMethodSettings.ShowOrderList 的默认值保持一致（默认显示），
    // 这样未加载（或没有设置服务）时开关也不会误显示为「关」。
    [ObservableProperty]
    private bool _showOrderList = true;

    public IAsyncRelayCommand SavePaymentMethodsCommand { get; }

    private bool CanSavePaymentMethods() => !IsBusy && _paymentMethodSettingsService is not null;

    private async Task LoadPaymentMethodsAsync()
    {
        if (_paymentMethodSettingsService is null) return;
        var settings = await _paymentMethodSettingsService.LoadAsync();
        UseManualCard = settings.UseManualCard;
        VoucherEnabled = settings.VoucherEnabled;
        ShowOrderList = settings.ShowOrderList;
        RaiseActivePaymentProviderProperties();
    }

    private async Task SavePaymentMethodsAsync()
    {
        if (!CanSavePaymentMethods()) return;
        using var permissionGrant = await AuthorizeAsync(
            Permissions.PosTerminal.Settings.PaymentTerminal, "save-payment-methods");
        if (permissionGrant is null || !CanSavePaymentMethods()) return;
        using var authorizationActivation = permissionGrant.Activate();
        // 三个开关作为同一份本机配置一起保存（与「保存付款方式」按钮一一对应）。
        var settings = new PaymentMethodSettings(UseManualCard, VoucherEnabled, ShowOrderList);
        await RunBusyAsync(async () =>
        {
            await _paymentMethodSettingsService!.SaveAsync(settings);
            RaiseActivePaymentProviderProperties();
            SetStatus("settings.payment.methods.saved");
        }, "save payment methods");
    }
}
