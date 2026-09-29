using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Hbpos.Client.Wpf.Services;

public sealed record AppUpdateUnattendedInstallOptions(bool IsEnabled, TimeOnly WindowStart, TimeOnly WindowEnd)
{
    public const string WindowConfigurationKey = "AppUpdate:UnattendedInstallWindow";

    public static readonly TimeOnly DefaultWindowStart = new(1, 0);

    public static readonly TimeOnly DefaultWindowEnd = new(7, 0);

    public static AppUpdateUnattendedInstallOptions Default { get; } = new(true, DefaultWindowStart, DefaultWindowEnd);

    public static AppUpdateUnattendedInstallOptions Disabled { get; } = new(false, DefaultWindowStart, DefaultWindowEnd);

    // 中文注释：按本机时间判断，窗口含开始不含结束；支持跨零点的窗口（例如 23:00-05:00）。
    public bool IsWithinWindow(TimeOnly localTime)
    {
        if (!IsEnabled)
        {
            return false;
        }

        return WindowStart < WindowEnd
            ? localTime >= WindowStart && localTime < WindowEnd
            : localTime >= WindowStart || localTime < WindowEnd;
    }

    public static AppUpdateUnattendedInstallOptions FromConfiguration(IConfiguration configuration, bool isDebugBuild)
    {
        var raw = configuration[WindowConfigurationKey]?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            // 中文注释：Debug 构建默认关闭，避免开发机开着调试版过夜时被装上正式安装包；需要调试时显式配置窗口即可。
            return isDebugBuild ? Disabled : Default;
        }

        if (string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase))
        {
            return Disabled;
        }

        return TryParseWindow(raw, out var start, out var end)
            ? new AppUpdateUnattendedInstallOptions(true, start, end)
            : isDebugBuild ? Disabled : Default;
    }

    private static bool TryParseWindow(string raw, out TimeOnly start, out TimeOnly end)
    {
        start = default;
        end = default;
        var parts = raw.Split('-', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 &&
            TimeOnly.TryParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out start) &&
            TimeOnly.TryParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out end) &&
            start != end;
    }
}

public interface IAppUpdateElevationProbe
{
    // 中文注释：安装包需要管理员权限；返回无人值守时能否提权而不弹 UAC 确认框。
    bool CanElevateWithoutPrompt(out string reason);
}

public sealed class WindowsAppUpdateElevationProbe : IAppUpdateElevationProbe
{
    private const int TokenElevationTypeInformationClass = 18;
    private const int TokenElevationTypeLimited = 3;
    private const string UacPolicyKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public bool CanElevateWithoutPrompt(out string reason)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                // 中文注释：进程已持有完整管理员令牌（已提权或关闭了 UAC），子进程直接继承，不会弹框。
                reason = "administrator-token";
                return true;
            }

            if (GetElevationType(identity) != TokenElevationTypeLimited)
            {
                reason = "standard-user";
                return false;
            }

            // 中文注释：管理员账号的受限令牌：只有“不提示直接提升”(0) 才能无人值守，缺省值 5 会弹同意框。
            var consentPromptBehavior = ReadConsentPromptBehaviorAdmin();
            if (consentPromptBehavior == 0)
            {
                reason = "administrator-elevates-without-prompt";
                return true;
            }

            reason = $"uac-prompt consentPromptBehaviorAdmin={consentPromptBehavior?.ToString(CultureInfo.InvariantCulture) ?? "default"}";
            return false;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or Win32Exception)
        {
            reason = $"probe-failed {ex.GetType().Name}";
            return false;
        }
    }

    private static int GetElevationType(WindowsIdentity identity)
    {
        return GetTokenInformation(
            identity.AccessToken,
            TokenElevationTypeInformationClass,
            out var elevationType,
            sizeof(int),
            out _)
            ? elevationType
            : 0;
    }

    private static int? ReadConsentPromptBehaviorAdmin()
    {
        using var key = Registry.LocalMachine.OpenSubKey(UacPolicyKeyPath);
        return key?.GetValue("ConsentPromptBehaviorAdmin") is int value ? value : null;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        out int tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}

// 中文注释：夜间无人值守安装：窗口内（默认本机 01:00-07:00）只要有更新且收银机空闲，就直接拉起静默安装器，
// 不管当前是否显示强更遮罩、是否有收银员登录。白天的提示与确认流程不受影响。
public sealed class AppUpdateUnattendedInstallScheduler(
    IAppUpdateCoordinator coordinator,
    IAppUpdateInstallSafetyGuard safetyGuard,
    IAppUpdateElevationProbe elevationProbe,
    AppUpdateUnattendedInstallOptions options,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null) : IDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    // 中文注释：没有更新或检查、下载失败后，同一晚隔 30 分钟再试，避免整夜每 5 分钟请求一次更新中心。
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);

    private const int RequiredIdleTicks = 2;
    private const int MaxInstallFailuresPerWindow = 3;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync =
        delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
    private readonly CancellationTokenSource _stopping = new();
    private int _started;
    private int _idleTicks;
    private int _installFailures;
    private bool _windowBlocked;
    private DateTimeOffset? _nextAttemptUtc;

    internal Task LoopTask { get; private set; } = Task.CompletedTask;

    // 中文注释：必须在 UI 线程启动；循环在 UI 同步上下文里恢复，协调器与安全守卫读取的界面状态不会跨线程。
    public void Start()
    {
        if (!options.IsEnabled ||
            _stopping.IsCancellationRequested ||
            Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        LoopTask = RunAsync(_stopping.Token);
    }

    public void Stop()
    {
        if (!_stopping.IsCancellationRequested)
        {
            _stopping.Cancel();
        }
    }

    public void Dispose()
    {
        Stop();
        _stopping.Dispose();
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            var localNow = _timeProvider.GetLocalNow();
            if (!options.IsWithinWindow(TimeOnly.FromDateTime(localNow.DateTime)))
            {
                ResetWindowState();
                return;
            }

            if (_windowBlocked)
            {
                return;
            }

            // 中文注释：连续两轮（至少 5 分钟）都没有进行中的交易才算空闲，给深夜仍在营业的门店留出收尾时间。
            if (!safetyGuard.CanInstallUpdate(out _, out _))
            {
                _idleTicks = 0;
                return;
            }

            if (++_idleTicks < RequiredIdleTicks)
            {
                return;
            }

            var utcNow = _timeProvider.GetUtcNow();
            if (_nextAttemptUtc is { } nextAttemptUtc && utcNow < nextAttemptUtc)
            {
                return;
            }

            if (!elevationProbe.CanElevateWithoutPrompt(out var reason))
            {
                // 中文注释：安装包需要管理员权限，会弹 UAC 时无人确认会让 POS 卡在拉起安装器这一步，当晚跳过、留给白天手动安装。
                _windowBlocked = true;
                ConsoleLog.WriteError("AppUpdate", $"unattended app update install skipped: elevation would prompt reason={reason}");
                return;
            }

            var result = await coordinator.InstallUpdateUnattendedAsync(cancellationToken);
            HandleResult(result, utcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 中文注释：单轮异常只记录日志，下一轮继续，不能终止循环或影响收银。
            _nextAttemptUtc = _timeProvider.GetUtcNow() + RetryInterval;
            ConsoleLog.WriteError(
                "AppUpdate",
                $"unattended app update install failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
    }

    private void HandleResult(AppUpdateCoordinatorResult result, DateTimeOffset utcNow)
    {
        switch (result.Status)
        {
            case AppUpdateCoordinatorStatus.Installed:
                ConsoleLog.Write("AppUpdate", "unattended app update installer launched; POS is exiting for the update");
                return;
            case AppUpdateCoordinatorStatus.AlreadyRunning:
                // 中文注释：其他检查或下载正在进行，下一轮再试。
                return;
            case AppUpdateCoordinatorStatus.UnattendedInstallSkipped
                when result.ErrorCode == AppUpdateCoordinator.UnattendedActiveTransactionErrorCode:
                // 中文注释：拉起前一刻有人开始交易，重新累计空闲。
                _idleTicks = 0;
                return;
            case AppUpdateCoordinatorStatus.UnattendedInstallSkipped:
                _windowBlocked = true;
                ConsoleLog.WriteError(
                    "AppUpdate",
                    $"unattended app update install skipped errorCode={result.ErrorCode ?? "<null>"} errorMessage={result.ErrorMessage ?? "<null>"}");
                return;
            case AppUpdateCoordinatorStatus.InstallFailed:
                _installFailures++;
                _windowBlocked = _installFailures >= MaxInstallFailuresPerWindow;
                ConsoleLog.WriteError(
                    "AppUpdate",
                    $"unattended app update installer launch failed attempt={_installFailures} detail={FormatDetail(result)}");
                return;
            case AppUpdateCoordinatorStatus.CheckFailed
                or AppUpdateCoordinatorStatus.PolicyFailed
                or AppUpdateCoordinatorStatus.DownloadFailed:
                _nextAttemptUtc = utcNow + RetryInterval;
                ConsoleLog.WriteError(
                    "AppUpdate",
                    $"unattended app update check status={result.Status} errorCode={result.ErrorCode ?? "<null>"} detail={FormatDetail(result)}");
                return;
            default:
                _nextAttemptUtc = utcNow + RetryInterval;
                return;
        }
    }

    private void ResetWindowState()
    {
        _idleTicks = 0;
        _installFailures = 0;
        _windowBlocked = false;
        _nextAttemptUtc = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _delayAsync(TickInterval, cancellationToken);
                await TickAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 中文注释：窗口关闭时正常结束循环。
        }
    }

    private static string FormatDetail(AppUpdateCoordinatorResult result)
    {
        return result.ErrorMessage ??
            (result.StatusArgs.Length > 0 ? Convert.ToString(result.StatusArgs[0], CultureInfo.InvariantCulture) : null) ??
            "<null>";
    }
}
