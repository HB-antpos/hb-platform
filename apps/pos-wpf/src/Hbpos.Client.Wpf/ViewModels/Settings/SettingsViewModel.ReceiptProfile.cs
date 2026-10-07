using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>
/// 设置页中「总部下发的小票资料」相关状态：本机已应用过下发（版本 &gt; 0）后六个资料字段只读，
/// 载入按钮改为「立即同步」。下发版本的读写与校验都在服务层，这里只负责界面状态。
/// </summary>
public sealed partial class SettingsViewModel
{
    private readonly IReceiptProfileSyncService? _receiptProfileSyncService;
    private Dispatcher? _receiptProfileSyncDispatcher;

    /// <summary>本机已应用的总部下发版本，0 表示从未应用过下发（资料可手工编辑）。</summary>
    [ObservableProperty]
    private int _receiptProfileVersion;

    public bool IsReceiptProfileManaged => ReceiptProfileVersion > 0;

    public string ReceiptProfileLoadButtonText => T(IsReceiptProfileManaged
        ? "settings.receiptPrinter.syncNow"
        : "settings.receiptPrinter.loadFromStore");

    public string ReceiptProfileManagedHintText => T("settings.receiptPrinter.profileManaged");

    partial void OnReceiptProfileVersionChanged(int value)
    {
        OnPropertyChanged(nameof(IsReceiptProfileManaged));
        OnPropertyChanged(nameof(ReceiptProfileLoadButtonText));
        OnPropertyChanged(nameof(ReceiptProfileManagedHintText));
    }

    private void InitializeReceiptProfileSync()
    {
        if (_receiptProfileSyncService is null)
        {
            return;
        }

        // 后台同步写入完成的事件来自线程池；只有 WPF 调度上下文才需要切回 UI 线程刷新绑定。
        _receiptProfileSyncDispatcher = SynchronizationContext.Current is DispatcherSynchronizationContext
            ? Dispatcher.CurrentDispatcher
            : null;
        _receiptProfileSyncService.ProfileApplied += OnReceiptProfileApplied;
    }

    private void ReleaseReceiptProfileSync()
    {
        if (_receiptProfileSyncService is not null)
        {
            _receiptProfileSyncService.ProfileApplied -= OnReceiptProfileApplied;
        }
    }

    private void OnReceiptProfileApplied(object? sender, ReceiptProfileAppliedEventArgs e)
    {
        // 设置页正开着时后台恰好写入了新版本：刷新六个字段和只读状态，避免界面停在旧内容上。
        if (_receiptProfileSyncDispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => _ = ReloadReceiptProfileFromStoreAsync()));
            return;
        }

        _ = ReloadReceiptProfileFromStoreAsync();
    }

    /// <summary>
    /// 本机已应用总部下发（只读）时的「立即同步」：立刻跑一轮同步并显示结果。
    /// 同步服务保证同一时刻只有一个同步在飞，后台同步进行中时直接复用它的结果。
    /// </summary>
    private async Task SyncReceiptProfileNowAsync()
    {
        if (_receiptProfileSyncService is null)
        {
            ReceiptPrinterTestStatusMessage = "Store receipt profile is not configured.";
            return;
        }

        try
        {
            var result = await _receiptProfileSyncService.SyncNowAsync();
            var (key, args) = ReceiptProfileSyncStatusText.Describe(result);
            ReceiptPrinterTestStatusMessage = Format(key, args);
            if (result.Outcome == ReceiptProfileSyncOutcome.Applied)
            {
                await ReloadReceiptProfileFromStoreAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 同步服务内部已把失败转成结果；这里兜底本机异常，不改动任何字段。
            ReceiptPrinterTestStatusMessage = Format(ReceiptProfileSyncStatusText.FailedKey, ex.Message);
        }
    }

    private async Task ReloadReceiptProfileFromStoreAsync()
    {
        if (_receiptPrinterSettingsStore is null || _disposed)
        {
            return;
        }

        try
        {
            var settings = await _receiptPrinterSettingsStore.LoadAsync();
            if (_disposed)
            {
                return;
            }

            ApplyReceiptProfileFields(settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.Write("ReceiptProfile", $"reload receipt settings after sync failed error={ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 只刷新六个资料字段和下发版本；端口、银行收据开关等硬件设置可能有未保存的草稿，不能被覆盖。
    /// </summary>
    private void ApplyReceiptProfileFields(ReceiptPrinterSettings settings)
    {
        ReceiptBrandNameText = settings.BrandName;
        ReceiptStoreNameText = settings.StoreName;
        ReceiptStoreAddressText = settings.StoreAddress;
        ReceiptStorePhoneText = settings.StorePhone;
        ReceiptAbnText = settings.Abn;
        ReceiptReturnPolicyText = settings.ReturnPolicy;
        ReceiptProfileVersion = settings.ProfileVersion;
    }
}
