using System.Net.Http;
using System.Text.Json;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Contracts.AppUpdates;

namespace Hbpos.Client.Wpf.Services;

public enum AppUpdateCoordinatorStatus
{
    NoUpdate,
    AlreadyRunning,
    CheckFailed,
    PolicyFailed,
    OptionalDeclined,
    OptionalReady,
    ForceReady,
    ForcePendingInstall,
    DownloadFailed,
    InstallFailed,
    Installed,
    DownloadDeferred
}

public sealed record AppUpdateCoordinatorResult(
    AppUpdateCoordinatorStatus Status,
    string StatusKey,
    object[] StatusArgs)
{
    public static AppUpdateCoordinatorResult NoUpdate() => FromStatus(AppUpdateCoordinatorStatus.NoUpdate);

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public static AppUpdateCoordinatorResult CheckFailed(
        string? errorCode = null,
        string? errorMessage = null)
    {
        return FromStatus(AppUpdateCoordinatorStatus.CheckFailed) with
        {
            ErrorCode = NormalizeDetail(errorCode),
            ErrorMessage = NormalizeDetail(errorMessage)
        };
    }

    public static AppUpdateCoordinatorResult PolicyFailed(
        string? errorCode = null,
        string? errorMessage = null)
    {
        return FromStatus(AppUpdateCoordinatorStatus.PolicyFailed) with
        {
            ErrorCode = NormalizeDetail(errorCode),
            ErrorMessage = NormalizeDetail(errorMessage)
        };
    }

    public static AppUpdateCoordinatorResult FromStatus(
        AppUpdateCoordinatorStatus status,
        params object[] args)
    {
        return new AppUpdateCoordinatorResult(status, StatusKeyFor(status), args);
    }

    private static string StatusKeyFor(AppUpdateCoordinatorStatus status)
    {
        return status switch
        {
            AppUpdateCoordinatorStatus.NoUpdate => "settings.status.appUpdateLatest",
            AppUpdateCoordinatorStatus.AlreadyRunning => "settings.status.appUpdateAlreadyRunning",
            AppUpdateCoordinatorStatus.CheckFailed => "settings.status.appUpdateCheckFailed",
            AppUpdateCoordinatorStatus.PolicyFailed => "settings.status.appUpdatePolicyFailed",
            AppUpdateCoordinatorStatus.OptionalDeclined => "settings.status.appUpdateOptionalDeclined",
            AppUpdateCoordinatorStatus.OptionalReady => "settings.status.appUpdateReady",
            AppUpdateCoordinatorStatus.ForceReady => "settings.status.appUpdateForceReady",
            AppUpdateCoordinatorStatus.ForcePendingInstall => "settings.status.appUpdateForceReady",
            AppUpdateCoordinatorStatus.DownloadFailed => "settings.status.appUpdateDownloadFailed",
            AppUpdateCoordinatorStatus.InstallFailed => "settings.status.appUpdateInstallFailed",
            AppUpdateCoordinatorStatus.Installed => "settings.status.appUpdateInstalling",
            AppUpdateCoordinatorStatus.DownloadDeferred => "settings.status.appUpdateDownloading",
            _ => "settings.status.appUpdateCheckFailed"
        };
    }

    private static string? NormalizeDetail(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public interface IAppUpdateCoordinator
{
    Task<AppUpdateCoordinatorResult> CheckForUpdatesAsync(
        bool manual,
        CancellationToken cancellationToken = default);

    // 中文注释：启动闸门专用检查：安装包已在本地时和以前一样当场处理；还没下载时返回 DownloadDeferred 放行启动，下载转到后台继续。
    Task<AppUpdateCoordinatorResult> CheckForUpdatesAtStartupAsync(
        CancellationToken cancellationToken = default);

    // 中文注释：运行期间的后台定时检查，只做非阻断提示：不弹确认框、不弹强更遮罩，也不写设置页状态文字。
    Task<AppUpdateCoordinatorResult> CheckForUpdatesInBackgroundAsync(
        CancellationToken cancellationToken = default);
}

public sealed class AppUpdateCoordinator(
    IAppVersionProvider versionProvider,
    IAppUpdateApiClient apiClient,
    IAppUpdateDownloadService downloadService,
    IAppUpdateInstallerLauncher installerLauncher,
    IAppUpdateInstallSafetyGuard installSafetyGuard,
    IAppUpdatePromptService promptService,
    AppUpdateState state,
    IApplicationExitService exitService,
    IAppUpdateChannelProvider channelProvider,
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null) : IAppUpdateCoordinator, IDisposable
{
    private const string ActiveTransactionStatusKey = "appUpdate.install.activeTransaction";
    private const string BackgroundForcePendingStatusKey = "appUpdate.force.backgroundReady";
    private const int RequiredSafeObservations = 2;
    private static readonly TimeSpan TransactionWatchInterval = TimeSpan.FromSeconds(2);
    private static readonly StringComparison ErrorCodeComparison = StringComparison.OrdinalIgnoreCase;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync =
        delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
    private CancellationTokenSource? _transactionWatch;

    internal Task DeferredStartupDownloadTask { get; private set; } = Task.CompletedTask;

    internal Task TransactionWatchTask { get; private set; } = Task.CompletedTask;

    public async Task<AppUpdateCoordinatorResult> CheckForUpdatesAtStartupAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.AlreadyRunning);
        }

        var releaseGate = true;
        try
        {
            var (update, terminalResult) = await FetchUpdateAsync(manual: false, background: false, cancellationToken);
            if (terminalResult is not null)
            {
                return terminalResult;
            }

            if (await downloadService.TryGetVerifiedCachedInstallerAsync(update!, cancellationToken) is not null)
            {
                // 中文注释：安装包已在本地（后台检查或上次启动下好的）时和以前一样在启动闸门里处理：强更直接阻断、可选更新弹确认框。
                return await HandleUpdateAsync(update!, manual: false, background: false, cancellationToken);
            }

            // 中文注释：安装包还没下载时不让启动等下载：先放行收银，下载在启动后继续；闸门信号量交给后台下载持有，避免与手动/定时检查重叠。
            releaseGate = false;
            DeferredStartupDownloadTask = ContinueStartupDownloadInBackgroundAsync(update!);
            return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.DownloadDeferred);
        }
        finally
        {
            if (releaseGate)
            {
                _gate.Release();
            }
        }
    }

    public void Dispose()
    {
        try
        {
            // 中文注释：Host 释放可能发生在后台线程，此时交易监视可能刚在 UI 线程结束并释放了自己的令牌源。
            _lifetime.Cancel();
            _transactionWatch?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task<AppUpdateCoordinatorResult> CheckForUpdatesAsync(
        bool manual,
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.AlreadyRunning);
        }

        try
        {
            return await CheckForUpdatesCoreAsync(manual, background: false, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppUpdateCoordinatorResult> CheckForUpdatesInBackgroundAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.AlreadyRunning);
        }

        try
        {
            if (state.IsUpdateFlowActive)
            {
                // 中文注释：强更待安装、强更错误态或下载中时不重复检查，避免后台流程覆盖收银员正在看的提示。
                return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.AlreadyRunning);
            }

            return await CheckForUpdatesCoreAsync(manual: false, background: true, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AppUpdateCoordinatorResult> CheckForUpdatesCoreAsync(
        bool manual,
        bool background,
        CancellationToken cancellationToken)
    {
        var (update, terminalResult) = await FetchUpdateAsync(manual, background, cancellationToken);
        return terminalResult ?? await HandleUpdateAsync(update!, manual, background, cancellationToken);
    }

    private Task<AppUpdateCoordinatorResult> HandleUpdateAsync(
        AppUpdateCheckResponse update,
        bool manual,
        bool background,
        CancellationToken cancellationToken)
    {
        return update.ForceUpdate
            ? HandleForceUpdateAsync(update, manual, background, cancellationToken)
            : HandleOptionalUpdateAsync(update, manual, background, cancellationToken);
    }

    // 中文注释：只负责查询更新中心；失败、无更新时直接给出最终结果，有可用更新时返回更新合同交给后续下载/提示流程。
    private async Task<(AppUpdateCheckResponse? Update, AppUpdateCoordinatorResult? TerminalResult)> FetchUpdateAsync(
        bool manual,
        bool background,
        CancellationToken cancellationToken)
    {
        AppUpdateCheckResponse update;
        try
        {
            update = await apiClient.CheckAsync(
                new AppUpdateCheckRequest
                {
                    CurrentVersion = versionProvider.CurrentVersion,
                    Channel = channelProvider.CurrentChannel
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            if (!background)
            {
                // 中文注释：后台检查的网络抖动不能清掉底部已显示的新版本号，只有前台检查失败才重置。
                state.ClearVersionCheckResult();
            }

            if (manual)
            {
                state.SetStatus("settings.status.appUpdateCheckFailed");
            }

            return (null, AppUpdateCoordinatorResult.CheckFailed());
        }

        if (update.CheckFailed)
        {
            if (!background)
            {
                state.ClearVersionCheckResult();
            }

            if (IsPolicyCheckFailure(update))
            {
                if (manual)
                {
                    state.SetStatus("settings.status.appUpdatePolicyFailed");
                }

                return (null, AppUpdateCoordinatorResult.PolicyFailed(update.ErrorCode, update.ErrorMessage));
            }

            // 中文注释：本地 API / 更新中心的 HTTP、空响应、合同校验失败都属于“检查失败”，必须保留给启动重试逻辑识别。
            if (manual)
            {
                state.SetStatus("settings.status.appUpdateCheckFailed");
            }

            return (null, AppUpdateCoordinatorResult.CheckFailed(update.ErrorCode, update.ErrorMessage));
        }

        if (!update.UpdateAvailable)
        {
            state.ClearVersionCheckResult();
            if (manual)
            {
                state.SetStatus("settings.status.appUpdateLatest");
            }

            return (null, AppUpdateCoordinatorResult.NoUpdate());
        }

        return (update, null);
    }

    private async Task ContinueStartupDownloadInBackgroundAsync(AppUpdateCheckResponse update)
    {
        try
        {
            // 中文注释：先让出执行权，保证启动流程先拿到 DownloadDeferred 继续初始化，再在 UI 线程上继续下载与提示。
            await Task.Yield();
            // 中文注释：强更下载完后空闲即弹阻断遮罩、交易中则等交易结束；可选更新只显示右下角就绪提示，不在收银中途弹模态框。
            var result = await HandleUpdateAsync(
                update,
                manual: false,
                background: !update.ForceUpdate,
                _lifetime.Token);
            if (result.Status == AppUpdateCoordinatorStatus.DownloadFailed)
            {
                ConsoleLog.WriteError(
                    "AppUpdate",
                    $"deferred startup app update download failed errorMessage={(result.StatusArgs.Length > 0 ? result.StatusArgs[0] : "<null>")}");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError(
                "AppUpdate",
                $"deferred startup app update failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AppUpdateCoordinatorResult> HandleOptionalUpdateAsync(
        AppUpdateCheckResponse update,
        bool manual,
        bool background,
        CancellationToken cancellationToken)
    {
        if (background && state.IsOptionalUpdateReadyFor(update.TargetVersion))
        {
            // 中文注释：同一版本的就绪提示还在，后台检查保持原提示与状态文字，不重复下载或重置。
            return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.OptionalReady);
        }

        if (background && state.IsOptionalUpdateDeclined(update.TargetVersion))
        {
            // 中文注释：收银员本次运行已拒绝或关闭过该可选版本，后台检查不再提示；手动检查和下次启动仍会询问。
            return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.OptionalDeclined);
        }

        var progress = new AppUpdateProgressSink(state.UpdateDownloadProgress);
        var download = await downloadService.DownloadAsync(update, progress, cancellationToken);
        if (!download.Success || string.IsNullOrWhiteSpace(download.FilePath))
        {
            state.ClearVersionCheckResult();
            if (manual)
            {
                state.SetStatus("settings.status.appUpdateDownloadFailed", download.ErrorMessage ?? string.Empty);
            }

            return AppUpdateCoordinatorResult.FromStatus(
                AppUpdateCoordinatorStatus.DownloadFailed,
                download.ErrorMessage ?? string.Empty);
        }

        // 中文注释：所有更新提示（含底部新版本号）只在安装包下载完成后出现；确认弹窗只负责决定是否立即拉起已下载的安装器。
        state.ApplyVersionCheck(update);

        string installerPath = download.FilePath;

        // 中文注释：可选更新安装动作晚于检查流程，不能捕获检查流程的取消令牌。
        async Task<ProcessLaunchResult> InstallOptionalAsync()
        {
            var launchResult = await installerLauncher.LaunchAsync(installerPath, update, CancellationToken.None);
            if (ShouldDeferUntilTransactionCompletes(launchResult))
            {
                // 中文注释：收银员已点过安装但被当前交易挡住，交易结束后自动弹出安装确认框，不用再去找右下角按钮。
                WatchForTransactionEnd(PromptOptionalInstallAfterTransactionAsync);
            }

            return launchResult;
        }

        async Task<bool> PromptOptionalInstallAfterTransactionAsync()
        {
            if (!state.IsOptionalUpdateReadyFor(update.TargetVersion) ||
                !string.Equals(state.InstallerPath, installerPath, StringComparison.OrdinalIgnoreCase))
            {
                // 中文注释：就绪提示已被关闭或被新版本替换，本次自动续装作废。
                return true;
            }

            if (!await promptService.ConfirmOptionalDownloadAndInstallAsync(update, _lifetime.Token))
            {
                state.DismissOptionalUpdate();
                return true;
            }

            var launchResult = await InstallOptionalAsync();
            state.ApplyInstallFailure(launchResult);
            if (launchResult.Success)
            {
                state.ClearOptionalUpdateAfterSuccessfulInstall();
            }

            return true;
        }

        Func<Task<ProcessLaunchResult>> installAsync = InstallOptionalAsync;
        if (background)
        {
            // 中文注释：后台检查随时可能落在扫码或结算中，不弹模态确认框，只显示右下角可关闭的就绪提示，由收银员空闲时点击安装。
            state.ShowOptionalUpdateReady(update, installerPath, installAsync);
            return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.OptionalReady);
        }

        if (!await promptService.ConfirmOptionalDownloadAndInstallAsync(update, cancellationToken))
        {
            state.MarkOptionalUpdateDeclined(update.TargetVersion);
            if (manual)
            {
                state.SetStatus("settings.status.appUpdateOptionalDeclined");
            }

            return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.OptionalDeclined);
        }

        state.ShowOptionalUpdateReady(update, installerPath, installAsync);
        var launchResult = await installAsync();
        state.ApplyInstallFailure(launchResult);
        if (launchResult.Success)
        {
            state.ClearOptionalUpdateAfterSuccessfulInstall();
        }

        return launchResult.Success
            ? AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.Installed)
            : AppUpdateCoordinatorResult.FromStatus(
                AppUpdateCoordinatorStatus.InstallFailed,
                ResolveInstallFailureMessage(launchResult));
    }

    private async Task<AppUpdateCoordinatorResult> HandleForceUpdateAsync(
        AppUpdateCheckResponse update,
        bool manual,
        bool background,
        CancellationToken cancellationToken)
    {
        // 中文注释：强制更新也先后台下载，只有安装包就绪后才进入阻断或待安装状态。
        return await DownloadForceUpdateAsync(update, manual, background, cancellationToken);
    }

    private async Task<AppUpdateCoordinatorResult> DownloadForceUpdateAsync(
        AppUpdateCheckResponse update,
        bool manual,
        bool background,
        CancellationToken cancellationToken)
    {
        // 中文注释：下载阶段保持非阻断，让用户能继续完成或取消当前交易。
        state.ShowForceUpdateDownloading(update);
        var progress = new AppUpdateProgressSink(state.UpdateDownloadProgress);
        var download = await downloadService.DownloadAsync(update, progress, cancellationToken);
        if (!download.Success || string.IsNullOrWhiteSpace(download.FilePath))
        {
            // 中文注释：没有安装包就不能出现任何更新提示；收银照常，下次启动或设置页手动检查时再下载。
            state.ClearForceUpdateDownload();
            if (manual)
            {
                state.SetStatus("settings.status.appUpdateDownloadFailed", download.ErrorMessage ?? string.Empty);
            }

            return AppUpdateCoordinatorResult.FromStatus(
                AppUpdateCoordinatorStatus.DownloadFailed,
                download.ErrorMessage ?? string.Empty);
        }

        state.ApplyVersionCheck(update);
        string installerPath = download.FilePath;

        void ShowPendingUntilTransactionEnds(string statusKey, object[] statusArgs)
        {
            state.ShowForceUpdatePendingInstall(
                update,
                installerPath,
                ResumeForceInstallAsync,
                statusKey,
                statusArgs);
            WatchForTransactionEnd(() => Task.FromResult(TryEscalatePendingForceUpdate()));
        }

        bool TryEscalatePendingForceUpdate()
        {
            if (!state.IsForceUpdatePendingInstall ||
                !string.Equals(state.InstallerPath, installerPath, StringComparison.OrdinalIgnoreCase))
            {
                // 中文注释：待安装状态已被安装、调试跳过或新一轮检查替换，本次自动续装作废。
                return true;
            }

            // 中文注释：交易结束后只切到强更阻断遮罩，由收银员点“安装更新”再关闭程序，避免小票打印等收尾被打断。
            return TryShowForceUpdateReady(update, installerPath, LaunchInstallerAsync, out _, out _);
        }

        async Task<ProcessLaunchResult> LaunchInstallerAsync()
        {
            var launchResult = await installerLauncher.LaunchAsync(installerPath, update, CancellationToken.None);
            if (ShouldDeferUntilTransactionCompletes(launchResult))
            {
                ShowPendingUntilTransactionEnds(launchResult.StatusKey!, launchResult.StatusArgs ?? []);
            }
            else if (!launchResult.Success)
            {
                // 中文注释：安装器已经下载完成，普通启动失败要切到可恢复错误态，避免强更遮罩只剩安装按钮。
                state.ShowForceUpdateError(
                    update,
                    launchResult.ErrorMessage ?? LocalizationResourceProvider.Instance["appUpdate.force.downloadFailedDefault"],
                    ResumeForceInstallAsync,
                    exitService.Exit);
            }

            return launchResult;
        }

        async Task<ProcessLaunchResult> ResumeForceInstallAsync()
        {
            if (!TryShowForceUpdateReady(update, installerPath, LaunchInstallerAsync, out var statusKey, out var statusArgs))
            {
                ShowPendingUntilTransactionEnds(statusKey, statusArgs);
                return ProcessLaunchResult.Fail(null, statusKey, statusArgs);
            }

            return await LaunchInstallerAsync();
        }

        if (background)
        {
            // 中文注释：后台检查的时机不可控，安全守卫只认购物车和付款页，覆盖不到交班、退货等其他进行中的操作，
            // 所以这里不直接弹阻断遮罩，只显示右下角待安装提示；收银员点击后再过安全守卫，下次启动仍由启动闸门强制安装。
            object[] backgroundStatusArgs = [update.TargetVersion];
            state.ShowForceUpdatePendingInstall(
                update,
                installerPath,
                ResumeForceInstallAsync,
                BackgroundForcePendingStatusKey,
                backgroundStatusArgs);
            return new AppUpdateCoordinatorResult(
                AppUpdateCoordinatorStatus.ForcePendingInstall,
                BackgroundForcePendingStatusKey,
                backgroundStatusArgs);
        }

        if (!TryShowForceUpdateReady(update, installerPath, LaunchInstallerAsync, out var blockedStatusKey, out var blockedStatusArgs))
        {
            ShowPendingUntilTransactionEnds(blockedStatusKey, blockedStatusArgs);
            return new AppUpdateCoordinatorResult(
                AppUpdateCoordinatorStatus.ForcePendingInstall,
                blockedStatusKey,
                blockedStatusArgs);
        }

        return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.ForceReady);
    }

    private bool TryShowForceUpdateReady(
        AppUpdateCheckResponse update,
        string installerPath,
        Func<Task<ProcessLaunchResult>> installAsync,
        out string statusKey,
        out object[] statusArgs)
    {
        if (!installSafetyGuard.CanInstallUpdate(out statusKey, out statusArgs))
        {
            return false;
        }

        state.ShowForceUpdateReady(
            update,
            installerPath,
            installAsync,
            ResolveForceReadyStatusKey(update),
            ResolveForceReadyStatusArgs(update));
        return true;
    }

    // 中文注释：只保留一个交易结束监视；新的延后安装会替换旧的。调用方都在 UI 线程，循环也在 UI 同步上下文里恢复。
    private void WatchForTransactionEnd(Func<Task<bool>> onSafeToInstall)
    {
        _transactionWatch?.Cancel();
        var watch = new CancellationTokenSource();
        _transactionWatch = watch;
        TransactionWatchTask = RunTransactionWatchAsync(onSafeToInstall, watch);
    }

    private async Task RunTransactionWatchAsync(
        Func<Task<bool>> onSafeToInstall,
        CancellationTokenSource watch)
    {
        var safeObservations = 0;
        try
        {
            while (true)
            {
                await _delayAsync(TransactionWatchInterval, watch.Token);
                if (!installSafetyGuard.CanInstallUpdate(out _, out _))
                {
                    safeObservations = 0;
                    continue;
                }

                // 中文注释：连续两次确认空闲才继续，给刚结束交易的小票打印、开钱箱等收尾留出时间。
                if (++safeObservations < RequiredSafeObservations)
                {
                    continue;
                }

                if (await onSafeToInstall())
                {
                    return;
                }

                safeObservations = 0;
            }
        }
        catch (OperationCanceledException) when (watch.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError(
                "AppUpdate",
                $"app update transaction watch failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
        finally
        {
            if (ReferenceEquals(_transactionWatch, watch))
            {
                _transactionWatch = null;
            }

            watch.Dispose();
        }
    }

    private static bool ShouldDeferUntilTransactionCompletes(ProcessLaunchResult result)
    {
        return !result.Success &&
            string.Equals(result.StatusKey, ActiveTransactionStatusKey, StringComparison.Ordinal);
    }

    private static string ResolveInstallFailureMessage(ProcessLaunchResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            return result.ErrorMessage;
        }

        if (!string.IsNullOrWhiteSpace(result.StatusKey))
        {
            var template = LocalizationResourceProvider.Instance[result.StatusKey];
            return (result.StatusArgs?.Length ?? 0) == 0
                ? template
                : string.Format(
                    LocalizationResourceProvider.Instance.CurrentCulture,
                    template,
                    result.StatusArgs!);
        }

        return string.Empty;
    }

    private static ProcessLaunchResult ToProcessLaunchResult(AppUpdateCoordinatorResult result)
    {
        // 中文注释：pending 恢复流程必须保留下载/策略失败语义，不能把内部失败包装成安装成功。
        return result.Status is AppUpdateCoordinatorStatus.ForceReady or AppUpdateCoordinatorStatus.Installed
            ? ProcessLaunchResult.Succeeded()
            : ProcessLaunchResult.Fail(null, result.StatusKey, result.StatusArgs);
    }

    private static bool IsPolicyCheckFailure(AppUpdateCheckResponse update)
    {
        var errorCode = update.ErrorCode?.Trim();
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return false;
        }

        return !errorCode.StartsWith("LOCAL_APP_UPDATE_", ErrorCodeComparison) &&
            !errorCode.StartsWith("APP_UPDATE_CENTER_", ErrorCodeComparison) &&
            !string.Equals(errorCode, "INVALID_UPDATE_CONTRACT", ErrorCodeComparison);
    }

    private static string ResolveForceReadyStatusKey(AppUpdateCheckResponse update)
    {
        if (update.IsRollback)
        {
            return "appUpdate.force.rollbackReady";
        }

        return string.IsNullOrWhiteSpace(update.MinimumSupportedVersion)
            ? "appUpdate.force.ready"
            : "appUpdate.force.minimumRequired";
    }

    private static object[] ResolveForceReadyStatusArgs(AppUpdateCheckResponse update)
    {
        if (update.IsRollback)
        {
            return [update.TargetVersion];
        }

        return string.IsNullOrWhiteSpace(update.MinimumSupportedVersion)
            ? []
            : [update.MinimumSupportedVersion];
    }

    private sealed class AppUpdateProgressSink(Action<AppUpdateDownloadProgress> report) : IProgress<AppUpdateDownloadProgress>
    {
        public void Report(AppUpdateDownloadProgress value)
        {
            report(value);
        }
    }
}
