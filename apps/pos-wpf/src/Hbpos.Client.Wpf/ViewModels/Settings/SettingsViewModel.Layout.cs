using CommunityToolkit.Mvvm.ComponentModel;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>支付终端页的分页：付款方式 / Square / ANZ Linkly。</summary>
public enum SettingsPaymentSubTab
{
    Methods,
    Square,
    Linkly
}

/// <summary>小票打印机页的分页：打印机与选项 / 门店信息 / 小票条款。</summary>
public enum SettingsReceiptSubTab
{
    Hardware,
    Store,
    Terms
}

/// <summary>
/// 设置页布局状态：固定页头（标题、说明、保存状态）与支付终端、小票打印机两页的分页。
/// 拆分页是为了让每页的操作按钮都落在首屏；分页只控制显隐，不改变各设置的保存方式。
/// </summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private SettingsPaymentSubTab _selectedPaymentSubTab = SettingsPaymentSubTab.Methods;

    [ObservableProperty]
    private SettingsReceiptSubTab _selectedReceiptSubTab = SettingsReceiptSubTab.Hardware;

    public string PageTitleText => SelectedCategory switch
    {
        SettingsCategory.DataMaintenance => DataMaintenanceTitleText,
        SettingsCategory.PaymentTerminal => CardTerminalTitleText,
        SettingsCategory.ReceiptPrinter => ReceiptPrinterTitleText,
        SettingsCategory.DeviceRegistration => T("settings.category.deviceRegistrationNav"),
        SettingsCategory.RemoteMaintenance => RemoteMaintenanceTitleText,
        _ => T("settings.category.mobileAppDownload")
    };

    public string PageDescriptionText => SelectedCategory switch
    {
        SettingsCategory.DataMaintenance => T("settings.page.dataMaintenance.description"),
        SettingsCategory.PaymentTerminal => T("settings.page.paymentTerminal.description"),
        SettingsCategory.ReceiptPrinter => T("settings.receiptPrinter.description"),
        SettingsCategory.DeviceRegistration => T("settings.page.deviceRegistration.description"),
        SettingsCategory.RemoteMaintenance => RemoteMaintenanceDescriptionText,
        _ => T("mobileApp.description")
    };

    /// <summary>旧的「保存付款方式 / 保存小票打印机」按钮只在没开自动保存时显示。</summary>
    public bool IsExplicitSaveVisible => !_autoSaveEnabled;

    // ── 支付终端分页（RadioButton 双向绑定：选中时切换，取消选中忽略）──

    public bool IsPaymentMethodsSubTabSelected
    {
        get => SelectedPaymentSubTab == SettingsPaymentSubTab.Methods;
        set
        {
            if (value)
            {
                SelectedPaymentSubTab = SettingsPaymentSubTab.Methods;
            }
        }
    }

    public bool IsSquareSubTabSelected
    {
        get => SelectedPaymentSubTab == SettingsPaymentSubTab.Square;
        set
        {
            if (value)
            {
                SelectedPaymentSubTab = SettingsPaymentSubTab.Square;
                SelectedProviderTabIndex = 0;
            }
        }
    }

    public bool IsLinklySubTabSelected
    {
        get => SelectedPaymentSubTab == SettingsPaymentSubTab.Linkly;
        set
        {
            if (value)
            {
                SelectedPaymentSubTab = SettingsPaymentSubTab.Linkly;
                SelectedProviderTabIndex = 1;
            }
        }
    }

    /// <summary>Square / Linkly 分页共用原来的服务商 TabControl（标签头隐藏，由页内分页切换）。</summary>
    public bool IsProviderSubTabSelected => SelectedPaymentSubTab != SettingsPaymentSubTab.Methods;

    // ── 小票打印机分页 ──

    public bool IsReceiptHardwareSubTabSelected
    {
        get => SelectedReceiptSubTab == SettingsReceiptSubTab.Hardware;
        set
        {
            if (value)
            {
                SelectedReceiptSubTab = SettingsReceiptSubTab.Hardware;
            }
        }
    }

    public bool IsReceiptStoreSubTabSelected
    {
        get => SelectedReceiptSubTab == SettingsReceiptSubTab.Store;
        set
        {
            if (value)
            {
                SelectedReceiptSubTab = SettingsReceiptSubTab.Store;
            }
        }
    }

    public bool IsReceiptTermsSubTabSelected
    {
        get => SelectedReceiptSubTab == SettingsReceiptSubTab.Terms;
        set
        {
            if (value)
            {
                SelectedReceiptSubTab = SettingsReceiptSubTab.Terms;
            }
        }
    }

    // ── 分页标签上的红点（该分区有保存失败或未生效的修改）──

    public bool HasPaymentMethodsUnsaved => IsUnsaved(PaymentMethodsSaveKey);

    public bool HasSquareUnsaved => IsUnsaved(SquareSaveKey);

    public bool HasLinklyUnsaved => IsUnsaved(LinklySaveKey);

    public bool HasReceiptHardwareUnsaved => IsUnsaved(ReceiptHardwareSaveKey);

    public bool HasReceiptStoreUnsaved => IsUnsaved(ReceiptStoreSaveKey);

    public bool HasReceiptTermsUnsaved => IsUnsaved(ReceiptTermsSaveKey);

    // 左侧导航上的未保存数量角标：数量为 0 时隐藏。
    public bool HasDataMaintenanceUnsaved => DataMaintenanceUnsavedCount > 0;

    public bool HasPaymentTerminalUnsaved => PaymentTerminalUnsavedCount > 0;

    public bool HasReceiptPrinterUnsaved => ReceiptPrinterUnsavedCount > 0;

    public bool HasDeviceRegistrationUnsaved => DeviceRegistrationUnsavedCount > 0;

    private bool IsUnsaved(string key) => _autoSave.GetState(key) is SettingsSaveState.Failed or SettingsSaveState.Draft;

    private void RaiseSubTabUnsavedProperties()
    {
        OnPropertyChanged(nameof(HasDataMaintenanceUnsaved));
        OnPropertyChanged(nameof(HasPaymentTerminalUnsaved));
        OnPropertyChanged(nameof(HasReceiptPrinterUnsaved));
        OnPropertyChanged(nameof(HasDeviceRegistrationUnsaved));
        OnPropertyChanged(nameof(HasPaymentMethodsUnsaved));
        OnPropertyChanged(nameof(HasSquareUnsaved));
        OnPropertyChanged(nameof(HasLinklyUnsaved));
        OnPropertyChanged(nameof(HasReceiptHardwareUnsaved));
        OnPropertyChanged(nameof(HasReceiptStoreUnsaved));
        OnPropertyChanged(nameof(HasReceiptTermsUnsaved));
    }

    private void RaisePageHeaderProperties()
    {
        OnPropertyChanged(nameof(PageTitleText));
        OnPropertyChanged(nameof(PageDescriptionText));
    }

    /// <summary>每次进入分类都从第一个分页开始（付款方式 / 打印机与选项）。</summary>
    private void ResetSubTabsForCategory()
    {
        SelectedPaymentSubTab = SettingsPaymentSubTab.Methods;
        SelectedReceiptSubTab = SettingsReceiptSubTab.Hardware;
    }

    partial void OnSelectedPaymentSubTabChanged(SettingsPaymentSubTab value)
    {
        OnPropertyChanged(nameof(IsPaymentMethodsSubTabSelected));
        OnPropertyChanged(nameof(IsSquareSubTabSelected));
        OnPropertyChanged(nameof(IsLinklySubTabSelected));
        OnPropertyChanged(nameof(IsProviderSubTabSelected));
    }

    partial void OnSelectedReceiptSubTabChanged(SettingsReceiptSubTab value)
    {
        OnPropertyChanged(nameof(IsReceiptHardwareSubTabSelected));
        OnPropertyChanged(nameof(IsReceiptStoreSubTabSelected));
        OnPropertyChanged(nameof(IsReceiptTermsSubTabSelected));
    }

    /// <summary>
    /// 服务商 TabControl 被直接切换（例如自动化或旧入口）时，同步到对应分页；
    /// 加载回显「当前启用的服务商」时不切页，进入分类时仍停在「付款方式」。
    /// </summary>
    partial void OnSelectedProviderTabIndexChanged(int value)
    {
        if (IsAutoSaveSuppressed)
        {
            return;
        }

        SelectedPaymentSubTab = value == 1 ? SettingsPaymentSubTab.Linkly : SettingsPaymentSubTab.Square;
    }
}
