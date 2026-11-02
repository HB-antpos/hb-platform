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
    DownloadDeferred,
    UnattendedInstallSkipped
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
            AppUpdateCoordinatorStatus.UnattendedInstallSkipped => "settings.status.appUpdateInstallFailed",
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

    // 中文注释：夜间无人值守安装：有更新（可选或强制、是否被拒绝过都一样）就下载并拉起安装，不弹任何提示。
    // agentAvailable：本机注册了管理员更新代理，可免 UAC 安装；directInstallAvailable：本机直接运行静默安装器也不会弹 UAC。
    Task<AppUpdateCoordinatorResult> InstallUpdateUnattendedAsync(
        bool agentAvailable,
        bool directInstallAvailable,
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
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
    AppUpdatePostponeOptions? postponeOptions = null) : IAppUpdateCoordinator, IDisposable
{
    public const string UnattendedInstallerNotSilentErrorCode = "UNATTENDED_INSTALLER_NOT_SILENT";
    public const string UnattendedActiveTransactionErrorCode = "UNATTENDED_ACTIVE_TRANSACTION";
    public const string UnattendedNoInstallPathErrorCode = "UNATTENDED_NO_INSTALL_PATH";
    private const string ActiveTransactionStatusKey = "appUpdate.install.activeTransaction";
    private const string BackgroundForcePendingStatusKey = "appUpdate.force.backgroundReady";
    private const int RequiredSafeObservations = 2;
    private static readonly TimeSpan TransactionWatchInterval = TimeSpan.FromSeconds(2);
    private static readonly StringComparison ErrorCodeComparison = StringComparison.OrdinalIgnoreCase;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync =
        delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
    private readonly AppUpdatePostponeOptions _postponeOptions = postponeOptions ?? AppUpdatePostponeOptions.Default;
    private CancellationTokenSource? _transactionWatch;
    // 中文注释：本次运行内同一目标版本已推迟的次数；重启后由启动闸门直接要求安装，不会被绕过。
    private string? _postponeTargetVersion;
    private int _postponeCount;

    // 中文注释：强更从哪里进入决定能否推迟、何时弹遮罩：启动闸门不可推迟；手动检查按收银员意愿立即提示；
    // 启动后台下载等自动流程只在收银主页空闲时弹遮罩；每小时后台检查只显示底部提示条。
    private enum ForceUpdateEntry
    {
        StartupGate,
        Manual,
        Automatic,
        Background
    }

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
                return await HandleUpdateAsync(update!, manual: false, background: false, cancellationToken, startupGate: true);
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

    public async Task<AppUpdateCoordinatorResult> InstallUpdateUnattendedAsync(
        bool agentAvailable,
        bool directInstallAvailable,
        CancellationToken cancellationToken = default)
    {
        if (!agentAvailable && !directInstallAvailable)
        {
            return UnattendedInstallSkipped(
                UnattendedNoInstallPathErrorCode,
                "Neither the update agent nor a prompt-free direct install is available.");
        }

        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.AlreadyRunning);
        }

        try
        {
            // 中文注释：失败时不改动界面上已有的更新提示，按后台检查处理；有无强更遮罩、收银员是否拒绝过都不影响夜间安装。
            var (update, terminalResult) = await FetchUpdateAsync(manual: false, background: true, cancellationToken);
            if (terminalResult is not null)
            {
                return terminalResult;
            }

            // 中文注释：直接运行安装器时用发布合同里的参数，非静默参数会弹安装向导等人点击，POS 已退出却装不完；
            // 走管理员更新代理时参数由代理固定为静默参数，与发布合同无关。
            var directInstallAllowed = directInstallAvailable && IsSilentInstallerArguments(update!);
            if (!agentAvailable && !directInstallAllowed)
            {
                return UnattendedInstallSkipped(
                    UnattendedInstallerNotSilentErrorCode,
                    "Installer arguments are not silent; unattended install would wait for user input.");
            }

            var download = await downloadService.DownloadAsync(update!, progress: null, cancellationToken);
            if (!download.Success || string.IsNullOrWhiteSpace(download.FilePath))
            {
                return AppUpdateCoordinatorResult.FromStatus(
                    AppUpdateCoordinatorStatus.DownloadFailed,
                    download.ErrorMessage ?? string.Empty);
            }

            // 中文注释：启动器内部会再过一次安全守卫；拉起成功后退出 POS，安装包装完会以原登录用户重新启动 POS。
            var launchResult = await installerLauncher.LaunchUnattendedAsync(
                download.FilePath,
                update!,
                directInstallAllowed ? UnattendedInstallMode.AnyInstaller : UnattendedInstallMode.AgentOnly,
                CancellationToken.None);
            if (launchResult.Success)
            {
                return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.Installed);
            }

            return ShouldDeferUntilTransactionCompletes(launchResult)
                ? UnattendedInstallSkipped(
                    UnattendedActiveTransactionErrorCode,
                    "A transaction started before the installer could be launched.")
                : AppUpdateCoordinatorResult.FromStatus(
                    AppUpdateCoordinatorStatus.InstallFailed,
                    ResolveInstallFailureMessage(launchResult));
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static bool IsSilentInstallerArguments(AppUpdateCheckResponse update)
    {
        if (!AppUpdateDownloadService.TryResolveInstallerFileName(update, out _, out var installerType, out _))
        {
            return false;
        }

        var tokens = (update.InstallerArguments ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToLowerInvariant())
            .ToArray();

        // 中文注释：MSI 认 /qn、/quiet、/passive 等 msiexec 无交互参数；Inno EXE 认 /VERYSILENT 或 /SILENT。
        return installerType == "msi"
            ? tokens.Any(token => token is "/quiet" or "/passive" || token.StartsWith("/qn", StringComparison.Ordinal) || token.StartsWith("/qb", StringComparison.Ordinal))
            : tokens.Any(token => token is "/verysilent" or "/silent");
    }

    private static AppUpdateCoordinatorResult UnattendedInstallSkipped(string errorCode, string errorMessage)
    {
        return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.UnattendedInstallSkipped, errorMessage) with
        {
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
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
        CancellationToken cancellationToken,
        bool startupGate = false)
    {
        if (!update.ForceUpdate)
        {
            return HandleOptionalUpdateAsync(update, manual, background, cancellationToken);
        }

        var entry = startupGate
            ? ForceUpdateEntry.StartupGate
            : manual
                ? ForceUpdateEntry.Manual
                : background
                    ? ForceUpdateEntry.Background
                    : ForceUpdateEntry.Automatic;
        return HandleForceUpdateAsync(update, entry, cancellationToken);
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

            // 中文注释：把失败原因带进结果（调用方仅用于日志），并单独记 Warning；检查频率低，无需节流。
            var statusCode = ex is HttpRequestException { StatusCode: { } status } ? (int)status : (int?)null;
            var errorCode = statusCode is { } code
                ? $"HTTP_{code}"
                : ex is TaskCanceledException ? "TIMEOUT" : ex.GetType().Name;
            ConsoleLog.WriteWarning(
                "AppUpdate",
                $"app update check failed errorCode={errorCode} manual={manual} background={background} error={ex.GetType().Name}",
                new ApplicationLogContext(
                    RequestPath: "api/app-update/check",
                    RequestMethod: "GET",
                    StatusCode: statusCode,
                    Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["errorCode"] = errorCode,
                        ["mode"] = manual ? "manual" : background ? "background" : "startup"
                    }),
                ex);
            return (null, AppUpdateCoordinatorResult.CheckFailed(errorCode, ex.Message));
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
            // 中文注释：后台检查随时可能落在扫码或结算中，不弹模态确认框，只显示底部状态栏可关闭的就绪提示，由收银员空闲时点击安装。
            state.ShowOptionalUpdateReady(update, installerPath, installAsync);
            ConsoleLog.Write("AppUpdate", $"optional update notice shown target={update.TargetVersion}");
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
        ForceUpdateEntry entry,
        CancellationToken cancellationToken)
    {
        // 中文注释：强制更新也先后台下载，只有安装包就绪后才进入阻断或待安装状态。
        return await DownloadForceUpdateAsync(update, entry, cancellationToken);
    }

    private async Task<AppUpdateCoordinatorResult> DownloadForceUpdateAsync(
        AppUpdateCheckResponse update,
        ForceUpdateEntry entry,
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
            if (entry == ForceUpdateEntry.Manual)
            {
                state.SetStatus("settings.status.appUpdateDownloadFailed", download.ErrorMessage ?? string.Empty);
            }

            return AppUpdateCoordinatorResult.FromStatus(
                AppUpdateCoordinatorStatus.DownloadFailed,
                download.ErrorMessage ?? string.Empty);
        }

        state.ApplyVersionCheck(update);
        string installerPath = download.FilePath;
        // 中文注释：启动闸门要保证旧版本不进入收银；回退说明当前版本有问题。这两种不允许推迟。
        var postponeAllowed = entry != ForceUpdateEntry.StartupGate && !update.IsRollback && _postponeOptions.IsEnabled;
        // 中文注释：每小时后台检查发现的强更只显示底部提示条；其他入口（或收银员点过安装、推迟过遮罩）在空闲时升级为遮罩。
        var escalating = entry != ForceUpdateEntry.Background;

        void OfferPostpone()
        {
            state.SetForceUpdatePostpone(
                PostponeAsync,
                postponeAllowed ? RemainingPostpones(update.TargetVersion) : 0,
                _postponeOptions.Interval);
        }

        void LogNotice(string kind, string? statusKey)
        {
            ConsoleLog.Write(
                "AppUpdate",
                $"force update notice shown kind={kind} entry={entry} target={update.TargetVersion} statusKey={statusKey ?? "<null>"} postponesLeft={(postponeAllowed ? RemainingPostpones(update.TargetVersion) : 0)}");
        }

        void ShowPending(string statusKey, object[] statusArgs)
        {
            state.ShowForceUpdatePendingInstall(
                update,
                installerPath,
                ResumeForceInstallAsync,
                statusKey,
                statusArgs);
            OfferPostpone();
        }

        void ShowPendingUntilTransactionEnds(string statusKey, object[] statusArgs)
        {
            escalating = true;
            ShowPending(statusKey, statusArgs);
            LogNotice("pending", statusKey);
            WatchForTransactionEnd(() => Task.FromResult(TryEscalatePendingForceUpdate()), interruptOnly: true);
        }

        bool IsCurrentPendingInstall()
        {
            return state.IsForceUpdatePendingInstall &&
                string.Equals(state.InstallerPath, installerPath, StringComparison.OrdinalIgnoreCase);
        }

        bool TryShowReady(bool interruptOnly, out string statusKey, out object[] statusArgs)
        {
            if (!TryShowForceUpdateReady(update, installerPath, LaunchInstallerAsync, interruptOnly, out statusKey, out statusArgs))
            {
                return false;
            }

            OfferPostpone();
            LogNotice("overlay", state.StatusKey);
            return true;
        }

        bool TryEscalatePendingForceUpdate()
        {
            if (!IsCurrentPendingInstall())
            {
                // 中文注释：待安装状态已被安装、调试跳过或新一轮检查替换，本次自动续装作废。
                return true;
            }

            // 中文注释：交易结束、回到收银主页空闲后只切到强更阻断遮罩，由收银员点“安装更新”再关闭程序，避免小票打印等收尾被打断。
            return TryShowReady(interruptOnly: true, out _, out _);
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
                // 中文注释：运行中安装器起不来时错误遮罩同样会挡住整个收银界面，也要能推迟。
                OfferPostpone();
                LogNotice("error", state.StatusKey);
            }

            return launchResult;
        }

        async Task<ProcessLaunchResult> ResumeForceInstallAsync()
        {
            // 中文注释：收银员主动点安装时按能否安装判断，不要求停在收银主页。
            if (!TryShowReady(interruptOnly: false, out var statusKey, out var statusArgs))
            {
                ShowPendingUntilTransactionEnds(statusKey, statusArgs);
                return ProcessLaunchResult.Fail(null, statusKey, statusArgs);
            }

            return await LaunchInstallerAsync();
        }

        Task PostponeAsync()
        {
            if (!postponeAllowed ||
                !state.IsForceUpdateRequired ||
                state.IsDownloading ||
                RemainingPostpones(update.TargetVersion) <= 0)
            {
                return Task.CompletedTask;
            }

            var fromOverlay = state.IsForceUpdateBlocking;
            var used = RecordPostpone(update.TargetVersion);
            // 中文注释：遮罩推迟后收起为底部提示条，收银员随时可以自己点安装；提示条推迟则整条先收起。
            ShowPending(BackgroundForcePendingStatusKey, [update.TargetVersion]);
            if (!fromOverlay)
            {
                state.HideForceUpdateNotice();
            }

            var escalateAfterPostpone = escalating || fromOverlay;
            escalating = escalateAfterPostpone;
            ConsoleLog.Write(
                "AppUpdate",
                $"force update postponed source={(fromOverlay ? "overlay" : "notice")} target={update.TargetVersion} count={used}/{_postponeOptions.MaxCount} minutes={(int)_postponeOptions.Interval.TotalMinutes} escalate={escalateAfterPostpone}");
            WatchForTransactionEnd(
                () => Task.FromResult(!escalateAfterPostpone || TryEscalatePendingForceUpdate()),
                interruptOnly: true,
                initialDelay: _postponeOptions.Interval,
                afterInitialDelay: () =>
                {
                    if (IsCurrentPendingInstall())
                    {
                        // 中文注释：推迟到点先把底部提示条放回来；需要升级为遮罩的再等收银主页空闲。
                        state.ShowForceUpdateNoticeAgain();
                        ConsoleLog.Write("AppUpdate", $"force update postpone expired target={update.TargetVersion} escalate={escalateAfterPostpone}");
                    }
                },
                requireSafe: escalateAfterPostpone);
            return Task.CompletedTask;
        }

        if (entry == ForceUpdateEntry.Background)
        {
            // 中文注释：后台检查的时机不可控，安全守卫只认购物车和付款页，覆盖不到交班、退货等其他进行中的操作，
            // 所以这里不直接弹阻断遮罩，只显示底部状态栏的待安装提示条；收银员点击后再过安全守卫，下次启动仍由启动闸门强制安装。
            object[] backgroundStatusArgs = [update.TargetVersion];
            ShowPending(BackgroundForcePendingStatusKey, backgroundStatusArgs);
            LogNotice("pending", BackgroundForcePendingStatusKey);
            return new AppUpdateCoordinatorResult(
                AppUpdateCoordinatorStatus.ForcePendingInstall,
                BackgroundForcePendingStatusKey,
                backgroundStatusArgs);
        }

        // 中文注释：启动闸门与手动检查当场提示；启动后的后台下载等自动流程只在收银主页空闲时才弹遮罩。
        if (!TryShowReady(entry == ForceUpdateEntry.Automatic, out var blockedStatusKey, out var blockedStatusArgs))
        {
            ShowPendingUntilTransactionEnds(blockedStatusKey, blockedStatusArgs);
            return new AppUpdateCoordinatorResult(
                AppUpdateCoordinatorStatus.ForcePendingInstall,
                blockedStatusKey,
                blockedStatusArgs);
        }

        return AppUpdateCoordinatorResult.FromStatus(AppUpdateCoordinatorStatus.ForceReady);
    }

    private int RemainingPostpones(string? targetVersion)
    {
        var used = string.Equals(
            _postponeTargetVersion,
            AppVersionProvider.NormalizeVersionText(targetVersion),
            StringComparison.OrdinalIgnoreCase)
            ? _postponeCount
            : 0;
        return Math.Max(_postponeOptions.MaxCount - used, 0);
    }

    private int RecordPostpone(string? targetVersion)
    {
        var normalized = AppVersionProvider.NormalizeVersionText(targetVersion);
        if (!string.Equals(_postponeTargetVersion, normalized, StringComparison.OrdinalIgnoreCase))
        {
            _postponeTargetVersion = normalized;
            _postponeCount = 0;
        }

        return ++_postponeCount;
    }

    private bool TryShowForceUpdateReady(
        AppUpdateCheckResponse update,
        string installerPath,
        Func<Task<ProcessLaunchResult>> installAsync,
        bool interruptOnly,
        out string statusKey,
        out object[] statusArgs)
    {
        var canShow = interruptOnly
            ? installSafetyGuard.CanInterruptForUpdatePrompt(out statusKey, out statusArgs)
            : installSafetyGuard.CanInstallUpdate(out statusKey, out statusArgs);
        if (!canShow)
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

    // 中文注释：只保留一个交易结束监视；新的延后安装或推迟会替换旧的。调用方都在 UI 线程，循环也在 UI 同步上下文里恢复。
    // interruptOnly：自动弹遮罩，按“收银主页空闲”判断；initialDelay：推迟时先等满间隔再开始观察。
    private void WatchForTransactionEnd(
        Func<Task<bool>> onSafeToInstall,
        bool interruptOnly = false,
        TimeSpan? initialDelay = null,
        Action? afterInitialDelay = null,
        bool requireSafe = true)
    {
        _transactionWatch?.Cancel();
        var watch = new CancellationTokenSource();
        _transactionWatch = watch;
        TransactionWatchTask = RunTransactionWatchAsync(
            onSafeToInstall,
            watch,
            interruptOnly,
            initialDelay,
            afterInitialDelay,
            requireSafe);
    }

    private async Task RunTransactionWatchAsync(
        Func<Task<bool>> onSafeToInstall,
        CancellationTokenSource watch,
        bool interruptOnly,
        TimeSpan? initialDelay,
        Action? afterInitialDelay,
        bool requireSafe)
    {
        var safeObservations = 0;
        try
        {
            if (initialDelay is { } delay && delay > TimeSpan.Zero)
            {
                await _delayAsync(delay, watch.Token);
                afterInitialDelay?.Invoke();
            }

            if (!requireSafe)
            {
                await onSafeToInstall();
                return;
            }

            while (true)
            {
                await _delayAsync(TransactionWatchInterval, watch.Token);
                var isSafe = interruptOnly
                    ? installSafetyGuard.CanInterruptForUpdatePrompt(out _, out _)
                    : installSafetyGuard.CanInstallUpdate(out _, out _);
                if (!isSafe)
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

        // 中文注释：后台填了最低支持版本不代表本机低于它；只有确实低于时才说“低于最低支持版本”，否则是勾选了强制更新。
        return IsBelowMinimumSupportedVersion(update)
            ? "appUpdate.force.minimumRequired"
            : "appUpdate.force.ready";
    }

    private static object[] ResolveForceReadyStatusArgs(AppUpdateCheckResponse update)
    {
        if (update.IsRollback)
        {
            return [update.TargetVersion];
        }

        return IsBelowMinimumSupportedVersion(update)
            ? [update.MinimumSupportedVersion!]
            : [];
    }

    internal static bool IsBelowMinimumSupportedVersion(AppUpdateCheckResponse update)
    {
        return TryParseComparableVersion(update.CurrentVersion, out var current) &&
            TryParseComparableVersion(update.MinimumSupportedVersion, out var minimum) &&
            current < minimum;
    }

    private static bool TryParseComparableVersion(string? value, out Version version)
    {
        var normalized = AppVersionProvider.NormalizeVersionText(value).TrimStart('v', 'V');
        var prereleaseIndex = normalized.IndexOf('-', StringComparison.Ordinal);
        if (prereleaseIndex >= 0)
        {
            normalized = normalized[..prereleaseIndex];
        }

        if (Version.TryParse(normalized, out var parsed))
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0);
        return false;
    }

    private sealed class AppUpdateProgressSink(Action<AppUpdateDownloadProgress> report) : IProgress<AppUpdateDownloadProgress>
    {
        public void Report(AppUpdateDownloadProgress value)
        {
            report(value);
        }
    }
}
