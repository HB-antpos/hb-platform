using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Hbpos.Client.Wpf.Services;

public enum SystemRestartStatus
{
    /// <summary>已向 Windows 下发重启计划。</summary>
    Scheduled,

    /// <summary>系统里已经有一个重启/关机计划（退出码 1190 / 1115），视同成功，不重复下发。</summary>
    AlreadyScheduled,

    Failed
}

public sealed record SystemRestartResult(
    SystemRestartStatus Status,
    int ExitCode,
    string? Message = null)
{
    public bool Succeeded => Status != SystemRestartStatus.Failed;
}

public interface ISystemRestartService
{
    /// <summary>
    /// 先登记「下次登录自动打开 HB POS」（HKCU RunOnce，一次性），再让 Windows 在 <paramref name="delay"/> 后重启。
    /// 命令失败时撤销 RunOnce，避免留下没有对应重启的自启项。
    /// </summary>
    Task<SystemRestartResult> RestartAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public sealed record ProcessRunResult(int ExitCode, string StandardError);

/// <summary>最窄的进程执行抽象，只为让重启命令能在测试里被替换。</summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                // 关键逻辑：不走 shell，直接用绝对路径启动，避免被 PATH 里的同名程序劫持。
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };

        process.Start();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not exit within {timeout.TotalSeconds:0}s.");
        }

        return new ProcessRunResult(process.ExitCode, (await errorTask).Trim());
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程已经退出，无需处理。
        }
    }
}

/// <summary>「重启后自动回到 POS」的登记抽象：生产实现写 HKCU RunOnce，测试用替身。</summary>
public interface IRestartAutoLaunchRegistrar
{
    /// <summary>登记失败不阻止重启（只是重启后需手动打开 POS），所以返回 false 而不是抛出。</summary>
    bool TryRegister(string executablePath);

    void Unregister();
}

public sealed class RunOnceAutoLaunchRegistrar : IRestartAutoLaunchRegistrar
{
    internal const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    internal const string ValueName = "HbPosAfterRestart";

    public bool TryRegister(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunOnceKeyPath, writable: true);
            // 带引号，路径含空格（Program Files）时才能正确启动；RunOnce 执行后系统会自己删掉这个值。
            key?.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
            return key is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            ConsoleLog.WriteWarning(
                "RestartAfterClose",
                $"runonce register failed error={ex.GetType().Name} message={ex.Message}");
            return false;
        }
    }

    public void Unregister()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunOnceKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            ConsoleLog.WriteWarning(
                "RestartAfterClose",
                $"runonce unregister failed error={ex.GetType().Name} message={ex.Message}");
        }
    }
}

public sealed class SystemRestartService(
    IProcessRunner processRunner,
    IRestartAutoLaunchRegistrar autoLaunchRegistrar,
    Func<string>? shutdownExecutablePathProvider = null,
    Func<string?>? executablePathProvider = null) : ISystemRestartService
{
    // Windows 错误码：1190 = 已计划重启/关机；1115 = 系统正在关机。两者都说明机器已经在重启路上，不重复下发。
    internal const int ErrorRestartAlreadyScheduled = 1190;
    internal const int ErrorShutdownInProgress = 1115;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<string> _shutdownExecutablePath = shutdownExecutablePathProvider
        ?? (() => Path.Combine(Environment.SystemDirectory, "shutdown.exe"));

    private readonly Func<string?> _executablePath = executablePathProvider
        ?? (() => Environment.ProcessPath);

    public async Task<SystemRestartResult> RestartAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        var delaySeconds = Math.Clamp((int)Math.Ceiling(delay.TotalSeconds), 5, 3600);

        // 先登记一次性自启，再下发重启：反过来的话，重启一旦生效就来不及登记了。
        var executablePath = _executablePath();
        var registered = executablePath is not null && autoLaunchRegistrar.TryRegister(executablePath);
        if (!registered)
        {
            ConsoleLog.WriteWarning(
                "RestartAfterClose",
                "auto-launch after restart was not registered; POS must be opened manually after restart");
        }

        // /r 重启（不是关机：快速启动下关机不会清零开机时长）；/f 强制关闭其他程序，避免被某个程序卡在
        // 「应用阻止重启」界面；/d p:4:1 = 计划内的应用维护，便于事件查看器里识别。
        var arguments = $"/r /f /t {delaySeconds} /d p:4:1 /c \"HB POS scheduled restart after daily close\"";

        try
        {
            var result = await processRunner.RunAsync(_shutdownExecutablePath(), arguments, CommandTimeout, cancellationToken);
            if (result.ExitCode == 0)
            {
                return new SystemRestartResult(SystemRestartStatus.Scheduled, 0);
            }

            if (result.ExitCode is ErrorRestartAlreadyScheduled or ErrorShutdownInProgress)
            {
                return new SystemRestartResult(SystemRestartStatus.AlreadyScheduled, result.ExitCode, result.StandardError);
            }

            if (registered)
            {
                autoLaunchRegistrar.Unregister();
            }

            return new SystemRestartResult(SystemRestartStatus.Failed, result.ExitCode, result.StandardError);
        }
        catch (OperationCanceledException)
        {
            if (registered)
            {
                autoLaunchRegistrar.Unregister();
            }

            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or TimeoutException)
        {
            if (registered)
            {
                autoLaunchRegistrar.Unregister();
            }

            // Win32Exception 的 NativeErrorCode 对应 Windows 错误码（5 = 拒绝访问），保留下来方便排查。
            var code = ex is System.ComponentModel.Win32Exception win32 ? win32.NativeErrorCode : -1;
            return new SystemRestartResult(SystemRestartStatus.Failed, code, ex.Message);
        }
    }
}
