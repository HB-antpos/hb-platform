using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Contracts.AppUpdates;

namespace Hbpos.Client.Wpf.Services;

public sealed partial class AppUpdateState : ObservableObject
{
    private Func<Task<ProcessLaunchResult>>? _installAsync;
    private Func<Task>? _retryAsync;
    private Action? _exitApplication;
    private string? _declinedOptionalTargetVersion;
#if DEBUG
    private Func<Task>? _continueStartupAfterDebugDismissAsync;
#endif

    public AppUpdateState()
    {
        InstallUpdateCommand = new AsyncRelayCommand(InstallAsync, CanInstall);
        DismissOptionalUpdateCommand = new RelayCommand(ClearOptionalUpdate);
        DismissForceUpdateForDebugCommand = new AsyncRelayCommand(
            DismissForceUpdateForDebugAsync,
            () => CanDismissForceUpdateForDebug);
        RetryForceUpdateCommand = new AsyncRelayCommand(RetryForceUpdateAsync, CanRetryForceUpdate);
        ExitApplicationCommand = new RelayCommand(ExitApplication, CanExitApplication);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsForceUpdateBlocking))]
    [NotifyPropertyChangedFor(nameof(CanDismissForceUpdateForDebug))]
    private bool _isForceUpdateRequired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsForceUpdateBlocking))]
    [NotifyPropertyChangedFor(nameof(CanDismissForceUpdateForDebug))]
    private bool _isForceUpdatePendingInstall;

    [ObservableProperty]
    private bool _isForceUpdateError;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstallerReady))]
    private string? _installerPath;

    private string _currentVersion = string.Empty;

    private bool _hasDifferentTargetVersion;

    private bool _isRollbackTarget;

    [ObservableProperty]
    private string? _targetVersion;

    [ObservableProperty]
    private string? _releaseNotes;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isOptionalUpdateReady;

    [ObservableProperty]
    private int _downloadProgressPercent;

    [ObservableProperty]
    private string _downloadProgressText = string.Empty;

    [ObservableProperty]
    private bool _hasDownloadProgress;

    public string StatusKey { get; private set; } = string.Empty;

    public object[] StatusArgs { get; private set; } = [];

    public bool IsForceUpdateBlocking => IsForceUpdateRequired && !IsForceUpdatePendingInstall;

    public bool CanDismissForceUpdateForDebug
    {
        get
        {
#if DEBUG
            return IsForceUpdateRequired;
#else
            return false;
#endif
        }
    }

    public bool IsInstallerReady => !string.IsNullOrWhiteSpace(InstallerPath);

    // 中文注释：已有下载中、强更（含待安装与错误态）时后台检查不能介入，避免覆盖当前更新提示。
    public bool IsUpdateFlowActive => IsDownloading || IsForceUpdateRequired;

    public string CurrentVersion
    {
        get => _currentVersion;
        private set => SetProperty(ref _currentVersion, value);
    }

    public bool HasDifferentTargetVersion
    {
        get => _hasDifferentTargetVersion;
        private set => SetProperty(ref _hasDifferentTargetVersion, value);
    }

    public bool IsRollbackTarget
    {
        get => _isRollbackTarget;
        private set => SetProperty(ref _isRollbackTarget, value);
    }

    public IAsyncRelayCommand InstallUpdateCommand { get; }

    public IRelayCommand DismissOptionalUpdateCommand { get; }

    public IAsyncRelayCommand DismissForceUpdateForDebugCommand { get; }

    public IAsyncRelayCommand RetryForceUpdateCommand { get; }

    public IRelayCommand ExitApplicationCommand { get; }

    public void InitializeCurrentVersion(string currentVersion)
    {
        CurrentVersion = AppVersionProvider.NormalizeVersionText(currentVersion);
        ClearVersionCheckResult();
    }

    // 中文注释：底部「最新版本/目标版本」也是更新提示，只能在安装包下载完成后调用。
    public void ApplyVersionCheck(AppUpdateCheckResponse update)
    {
        var targetVersion = AppVersionProvider.NormalizeVersionText(update.TargetVersion);
        TargetVersion = targetVersion;
        HasDifferentTargetVersion = update.UpdateAvailable &&
            !string.IsNullOrWhiteSpace(targetVersion) &&
            !string.Equals(CurrentVersion, targetVersion, StringComparison.OrdinalIgnoreCase);
        IsRollbackTarget = HasDifferentTargetVersion && update.IsRollback;
    }

    public void ClearVersionCheckResult()
    {
        HasDifferentTargetVersion = false;
        IsRollbackTarget = false;
    }

    // 中文注释：本次运行内记住收银员拒绝或关闭过的可选版本，后台定时检查不再反复提示同一版本；重启后清空。
    public void MarkOptionalUpdateDeclined(string? targetVersion)
    {
        var normalized = AppVersionProvider.NormalizeVersionText(targetVersion);
        _declinedOptionalTargetVersion = string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public bool IsOptionalUpdateDeclined(string? targetVersion)
    {
        return _declinedOptionalTargetVersion is not null &&
            string.Equals(
                _declinedOptionalTargetVersion,
                AppVersionProvider.NormalizeVersionText(targetVersion),
                StringComparison.OrdinalIgnoreCase);
    }

    public bool IsOptionalUpdateReadyFor(string? targetVersion)
    {
        return IsOptionalUpdateReady &&
            IsInstallerReady &&
            string.Equals(
                AppVersionProvider.NormalizeVersionText(TargetVersion),
                AppVersionProvider.NormalizeVersionText(targetVersion),
                StringComparison.OrdinalIgnoreCase);
    }

    public void SetStatus(string statusKey, params object[] args)
    {
        StatusKey = statusKey;
        StatusArgs = args;
        StatusMessage = FormatStatus(statusKey, args);
    }

    public void ShowForceUpdateDownloading(AppUpdateCheckResponse update)
    {
        // 中文注释：强更下载期间不阻断收银流程，安装包就绪后再切到强更遮罩。
        IsForceUpdateRequired = false;
        IsForceUpdatePendingInstall = false;
        IsForceUpdateError = false;
        IsOptionalUpdateReady = false;
        IsDownloading = true;
        InstallerPath = null;
        TargetVersion = update.TargetVersion;
        ReleaseNotes = update.ReleaseNotes;
        _installAsync = null;
        _retryAsync = null;
        _exitApplication = null;
        // 中文注释：新安装包下载完成前底部不能出现目标版本号。
        ClearVersionCheckResult();
        ClearDownloadProgress();
        SetStatus("appUpdate.force.downloading");
        NotifyCommandStates();
    }

    public void ShowForceUpdateReady(
        AppUpdateCheckResponse update,
        string installerPath,
        Func<Task<ProcessLaunchResult>> installAsync,
        string statusKey,
        params object[] statusArgs)
    {
        IsForceUpdateRequired = true;
        IsForceUpdatePendingInstall = false;
        IsForceUpdateError = false;
        IsOptionalUpdateReady = false;
        IsDownloading = false;
        InstallerPath = installerPath;
        TargetVersion = update.TargetVersion;
        ReleaseNotes = update.ReleaseNotes;
        _installAsync = installAsync;
        _retryAsync = null;
        _exitApplication = null;
        SetStatus(statusKey, statusArgs);
        NotifyCommandStates();
    }

    public void ShowForceUpdatePendingInstall(
        AppUpdateCheckResponse update,
        string installerPath,
        Func<Task<ProcessLaunchResult>> installAsync,
        string statusKey,
        params object[] statusArgs)
    {
        IsForceUpdateRequired = true;
        IsForceUpdatePendingInstall = true;
        IsForceUpdateError = false;
        IsOptionalUpdateReady = false;
        IsDownloading = false;
        InstallerPath = installerPath;
        TargetVersion = update.TargetVersion;
        ReleaseNotes = update.ReleaseNotes;
        _installAsync = installAsync;
        _retryAsync = null;
        _exitApplication = null;
        ClearDownloadProgress();
        SetStatus(statusKey, statusArgs);
        NotifyCommandStates();
    }

    public void ShowForceUpdateError(
        AppUpdateCheckResponse update,
        string message,
        Func<Task> retryAsync,
        Action exitApplication)
    {
        IsForceUpdateRequired = true;
        IsForceUpdatePendingInstall = false;
        IsForceUpdateError = true;
        IsOptionalUpdateReady = false;
        IsDownloading = false;
        InstallerPath = null;
        TargetVersion = update.TargetVersion;
        ReleaseNotes = update.ReleaseNotes;
        _installAsync = null;
        _retryAsync = retryAsync;
        _exitApplication = exitApplication;
        ClearDownloadProgress();
        SetStatus("appUpdate.force.downloadFailed", message);
        NotifyCommandStates();
    }

    public void ClearForceUpdateDownload()
    {
        // 中文注释：强更安装包没下载成功时回到无提示状态，不能留下阻断遮罩或底部新版本号。
        IsForceUpdateRequired = false;
        IsForceUpdatePendingInstall = false;
        IsForceUpdateError = false;
        IsOptionalUpdateReady = false;
        IsDownloading = false;
        InstallerPath = null;
        TargetVersion = null;
        ReleaseNotes = null;
        _installAsync = null;
        _retryAsync = null;
        _exitApplication = null;
        ClearVersionCheckResult();
        ClearDownloadProgress();
        StatusKey = string.Empty;
        StatusArgs = [];
        StatusMessage = string.Empty;
        NotifyCommandStates();
    }

    public void ShowOptionalUpdateReady(
        AppUpdateCheckResponse update,
        string installerPath,
        Func<Task<ProcessLaunchResult>> installAsync)
    {
        IsForceUpdateRequired = false;
        IsForceUpdatePendingInstall = false;
        IsForceUpdateError = false;
        IsOptionalUpdateReady = true;
        IsDownloading = false;
        InstallerPath = installerPath;
        TargetVersion = update.TargetVersion;
        ReleaseNotes = update.ReleaseNotes;
        _installAsync = installAsync;
        _retryAsync = null;
        _exitApplication = null;
        SetStatus("appUpdate.optional.ready");
        NotifyCommandStates();
    }

    public void UpdateDownloadProgress(AppUpdateDownloadProgress progress)
    {
        HasDownloadProgress = true;
        DownloadProgressPercent = progress.Percent;
        DownloadProgressText = $"{progress.DownloadedBytes} / {progress.TotalBytes} bytes ({progress.Percent}%)";
    }

    public void ApplyInstallFailure(ProcessLaunchResult result)
    {
        if (result.Success)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.StatusKey))
        {
            SetStatus(result.StatusKey, result.StatusArgs ?? []);
        }
        else
        {
            SetStatus("appUpdate.install.failed", result.ErrorMessage ?? string.Empty);
        }

        NotifyCommandStates();
    }

    // 中文注释：收银员在交易结束后的安装确认框里选择“稍后安装”时，与点右下角关闭按钮等效。
    public void DismissOptionalUpdate()
    {
        ClearOptionalUpdate();
    }

    public void ClearOptionalUpdateAfterSuccessfulInstall()
    {
        if (IsForceUpdateRequired)
        {
            return;
        }

        // 可选更新安装器已成功交给系统后，清掉重试入口，避免用户再次点击重复拉起安装器。
        IsOptionalUpdateReady = false;
        InstallerPath = null;
        _installAsync = null;
        ClearDownloadProgress();
        NotifyCommandStates();
    }

    private async Task InstallAsync()
    {
        if (_installAsync is not null)
        {
            var result = await _installAsync();
            // 中文注释：可选更新首次自动拉起失败后会保留重试入口；一旦用户重试成功，需要在这里统一清掉入口状态。
            if (result.Success && !IsForceUpdateRequired)
            {
                ClearOptionalUpdateAfterSuccessfulInstall();
                return;
            }

            if (IsForceUpdateError)
            {
                // 中文注释：强更安装器启动失败时，coordinator 已切到可恢复错误态；这里不能再用通用安装失败状态覆盖 retry/exit 文案。
                return;
            }

            ApplyInstallFailure(result);
        }
    }

    private bool CanInstall()
    {
        return _installAsync is not null && !IsDownloading;
    }

    private void ClearOptionalUpdate()
    {
        if (IsForceUpdateRequired)
        {
            return;
        }

        MarkOptionalUpdateDeclined(TargetVersion);
        IsOptionalUpdateReady = false;
        InstallerPath = null;
        _installAsync = null;
        ClearDownloadProgress();
        NotifyCommandStates();
    }

    public void ConfigureDebugForceUpdateDismissed(Func<Task> continueStartupAsync)
    {
#if DEBUG
        _continueStartupAfterDebugDismissAsync = continueStartupAsync;
#endif
    }

    private async Task DismissForceUpdateForDebugAsync()
    {
#if DEBUG
        if (!IsForceUpdateRequired)
        {
            return;
        }

        // 中文注释：仅调试构建允许绕过强制升级，Release 构建在属性与命令两层均保持禁用。
        IsForceUpdateRequired = false;
        IsForceUpdatePendingInstall = false;
        IsForceUpdateError = false;
        IsOptionalUpdateReady = false;
        IsDownloading = false;
        InstallerPath = null;
        TargetVersion = null;
        ReleaseNotes = null;
        _installAsync = null;
        _retryAsync = null;
        _exitApplication = null;
        ClearVersionCheckResult();
        ClearDownloadProgress();
        StatusKey = string.Empty;
        StatusArgs = [];
        StatusMessage = string.Empty;
        NotifyCommandStates();

        if (_continueStartupAfterDebugDismissAsync is not null)
        {
            await _continueStartupAfterDebugDismissAsync();
        }
#else
        await Task.CompletedTask;
#endif
    }

    private async Task RetryForceUpdateAsync()
    {
        if (_retryAsync is not null)
        {
            await _retryAsync();
        }
    }

    private bool CanRetryForceUpdate()
    {
        return IsForceUpdateError && !IsDownloading && _retryAsync is not null;
    }

    private void ExitApplication()
    {
        _exitApplication?.Invoke();
    }

    private bool CanExitApplication()
    {
        return IsForceUpdateError && !IsDownloading && _exitApplication is not null;
    }

    private void ClearDownloadProgress()
    {
        HasDownloadProgress = false;
        DownloadProgressPercent = 0;
        DownloadProgressText = string.Empty;
    }

    private void NotifyCommandStates()
    {
        InstallUpdateCommand.NotifyCanExecuteChanged();
        DismissForceUpdateForDebugCommand.NotifyCanExecuteChanged();
        RetryForceUpdateCommand.NotifyCanExecuteChanged();
        ExitApplicationCommand.NotifyCanExecuteChanged();
    }

    private static string FormatStatus(string statusKey, params object[] args)
    {
        var template = LocalizationResourceProvider.Instance[statusKey];
        return args.Length == 0
            ? template
            : string.Format(LocalizationResourceProvider.Instance.CurrentCulture, template, args);
    }
}
