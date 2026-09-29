using System.Globalization;
using System.Windows.Threading;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class SettingsViewModel
{
    private const string CatalogSyncTimeFormat = "yyyy-MM-dd HH:mm";

    private readonly ICatalogSyncStatusService? _catalogSyncStatusService;
    private Dispatcher? _catalogSyncStatusDispatcher;

    private CatalogSyncStatus CurrentCatalogSyncStatus =>
        _catalogSyncStatusService?.GetStatus(Session.StoreCode) ?? CatalogSyncStatus.Empty;

    public bool IsCatalogSyncing => CurrentCatalogSyncStatus.IsSyncing;

    public bool IsCatalogSyncFailed => CurrentCatalogSyncStatus is { IsSyncing: false, IsLastAttemptFailed: true };

    public bool IsCatalogSyncSucceeded => CurrentCatalogSyncStatus is
    {
        IsSyncing: false,
        IsLastAttemptFailed: false,
        LastSucceededAt: not null
    };

    public string CatalogSyncTimeLabelText => T(IsCatalogSyncFailed
        ? "settings.section.dataDownload.lastSuccessfulSync"
        : "settings.section.dataDownload.lastSync");

    public string CatalogSyncTimeText => CurrentCatalogSyncStatus.LastSucceededAt is { } succeededAt
        ? FormatCatalogSyncTime(succeededAt)
        : T("settings.section.dataDownload.neverSynced");

    public string CatalogSyncFailureText
    {
        get
        {
            var status = CurrentCatalogSyncStatus;
            return IsCatalogSyncFailed && status.LastFailedAt is { } failedAt
                ? Format(
                    "settings.section.dataDownload.failedDetail",
                    FormatCatalogSyncTime(failedAt),
                    status.LastFailureMessage ?? string.Empty)
                : string.Empty;
        }
    }

    private void InitializeCatalogSyncStatus()
    {
        if (_catalogSyncStatusService is null)
        {
            return;
        }

        // 后台同步完成事件可能来自线程池，只有 WPF 调度上下文才需要切回 UI 刷新绑定。
        _catalogSyncStatusDispatcher = SynchronizationContext.Current is DispatcherSynchronizationContext
            ? Dispatcher.CurrentDispatcher
            : null;
        _catalogSyncStatusService.Changed += OnCatalogSyncStatusChanged;
    }

    private void ReleaseCatalogSyncStatus()
    {
        if (_catalogSyncStatusService is not null)
        {
            _catalogSyncStatusService.Changed -= OnCatalogSyncStatusChanged;
        }
    }

    private async Task LoadCatalogSyncStatusAsync()
    {
        if (_catalogSyncStatusService is null)
        {
            return;
        }

        try
        {
            await _catalogSyncStatusService.LoadAsync(Session.StoreCode);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 同步时间只用于展示，读取失败时保留“尚未同步”，不影响设置页其他内容。
            ConsoleLog.Write("CatalogSync", $"load last sync time failed store={Session.StoreCode} error={ex.Message}");
        }

        RaiseCatalogSyncStatusProperties();
    }

    private void OnCatalogSyncStatusChanged(object? sender, EventArgs e)
    {
        if (_catalogSyncStatusDispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(RaiseCatalogSyncStatusProperties));
            return;
        }

        RaiseCatalogSyncStatusProperties();
    }

    private void RaiseCatalogSyncStatusProperties()
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(IsCatalogSyncing));
        OnPropertyChanged(nameof(IsCatalogSyncFailed));
        OnPropertyChanged(nameof(IsCatalogSyncSucceeded));
        OnPropertyChanged(nameof(CatalogSyncTimeLabelText));
        OnPropertyChanged(nameof(CatalogSyncTimeText));
        OnPropertyChanged(nameof(CatalogSyncFailureText));
    }

    private static string FormatCatalogSyncTime(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString(CatalogSyncTimeFormat, CultureInfo.InvariantCulture);
    }
}
