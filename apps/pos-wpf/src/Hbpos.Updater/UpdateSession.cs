using System.Globalization;
using System.IO;

namespace Hbpos.Updater;

/// <summary>
/// 一次更新的完整流程：等旧版收银程序退出 → 运行安装器并读取进度 → 确认新版已打开后退出。
/// </summary>
internal sealed class UpdateSession
{
    internal const string ProgressFileArgumentName = "/HBPOSPROGRESS";
    internal const string DefaultAppProcessName = "Hbpos.Client.Wpf";
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan OldAppExitTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan AppStartTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan CompletedDisplayDuration = TimeSpan.FromSeconds(1.5);

    private readonly UpdaterOptions _options;
    private readonly IUpdaterSystem _system;
    private readonly TimeProvider _timeProvider;
    private readonly UpdaterViewModel _viewModel;
    private readonly string _progressFilePath;
    private bool _installing;

    public UpdateSession(
        UpdaterOptions options,
        IUpdaterSystem system,
        TimeProvider timeProvider,
        UpdaterViewModel viewModel,
        string progressFilePath)
    {
        _options = options;
        _system = system;
        _timeProvider = timeProvider;
        _viewModel = viewModel;
        _progressFilePath = progressFilePath;
        _viewModel.RetryRequested += (_, _) => _ = RunGuardedAsync(() => InstallAsync());
        _viewModel.OpenCurrentVersionRequested += (_, _) => OpenCurrentVersion();
        _viewModel.ViewLogRequested += (_, _) => ViewLog();
    }

    public event EventHandler? ExitRequested;

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return RunGuardedAsync(async () =>
        {
            await WaitForOldAppExitAsync(cancellationToken);
            await InstallAsync(cancellationToken);
        });
    }

    public void ReportUnexpectedFailure(Exception exception)
    {
        _viewModel.ShowFailed(UpdaterFailureKind.Unexpected, exception.Message, CanViewLog());
    }

    internal async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        if (_installing)
        {
            return;
        }

        _installing = true;
        try
        {
            await InstallCoreAsync(cancellationToken);
        }
        finally
        {
            _installing = false;
        }
    }

    internal string BuildInstallerArguments(bool includeLog)
    {
        var arguments = new List<string>();
        if (_options.InstallerArguments.Length > 0)
        {
            arguments.Add(_options.InstallerArguments);
        }

        // 新版安装脚本会把进度写进这个文件；旧安装包不认识该参数时会直接忽略，窗口保持不确定进度。
        arguments.Add($"{ProgressFileArgumentName}=\"{_progressFilePath}\"");
        if (includeLog && _options.LogPath is not null)
        {
            arguments.Add($"/LOG=\"{_options.LogPath}\"");
        }

        return string.Join(' ', arguments);
    }

    private async Task WaitForOldAppExitAsync(CancellationToken cancellationToken)
    {
        _viewModel.ShowClosing();
        if (_options.WaitProcessId is not { } processId)
        {
            return;
        }

        var startedAt = _timeProvider.GetTimestamp();
        var exitTask = _system.WaitForProcessExitAsync(processId, cancellationToken);
        while (!exitTask.IsCompleted)
        {
            var elapsed = _timeProvider.GetElapsedTime(startedAt);
            _viewModel.UpdateElapsed(elapsed);
            if (elapsed >= OldAppExitTimeout)
            {
                // 旧版迟迟不退出时照常安装，由安装器的 CloseApplications 负责关闭它。
                return;
            }

            await Task.WhenAny(exitTask, Task.Delay(PollInterval, _timeProvider, cancellationToken));
        }
    }

    private async Task InstallCoreAsync(CancellationToken cancellationToken)
    {
        _viewModel.ShowInstalling();
        if (!_system.FileExists(_options.InstallerPath))
        {
            ShowFailed(UpdaterFailureKind.InstallerMissing, null);
            return;
        }

        _system.TryDeleteFile(_progressFilePath);
        var includeLog = _options.LogPath is not null && _system.TryEnsureParentDirectory(_options.LogPath);
        var startedAt = _timeProvider.GetTimestamp();
        IInstallerProcess installer;
        try
        {
            installer = await _system.StartInstallerAsync(_options.InstallerPath, BuildInstallerArguments(includeLog));
        }
        catch (InstallerLaunchException ex)
        {
            ShowFailed(
                ex.ElevationCancelled ? UpdaterFailureKind.ElevationCancelled : UpdaterFailureKind.LaunchFailed,
                ex.Message);
            return;
        }

        int exitCode;
        using (installer)
        {
            var exitTask = installer.WaitForExitAsync(cancellationToken);
            while (!exitTask.IsCompleted)
            {
                ApplyInstallerProgress(startedAt);
                await Task.WhenAny(exitTask, Task.Delay(PollInterval, _timeProvider, cancellationToken));
            }

            await exitTask;
            exitCode = installer.ExitCode;
        }

        if (exitCode != 0)
        {
            ShowFailed(MapExitCode(exitCode), exitCode.ToString(CultureInfo.InvariantCulture));
            return;
        }

        await CompleteAsync(_timeProvider.GetElapsedTime(startedAt), cancellationToken);
    }

    private void ApplyInstallerProgress(long startedAt)
    {
        if (InstallerProgressSnapshot.TryParse(_system.TryReadAllText(_progressFilePath), out var progress))
        {
            _viewModel.ReportInstallerProgress(progress);
        }

        _viewModel.UpdateElapsed(_timeProvider.GetElapsedTime(startedAt));
    }

    private async Task CompleteAsync(TimeSpan installDuration, CancellationToken cancellationToken)
    {
        _viewModel.ShowCompleted(installDuration);
        var startedAt = _timeProvider.GetTimestamp();
        var processName = ResolveAppProcessName();
        // 安装脚本 [Run] 会以原登录用户打开新版；等不到时由更新窗口兜底打开，避免收银机停在桌面。
        while (!_system.IsProcessRunning(processName, _options.WaitProcessId))
        {
            if (_timeProvider.GetElapsedTime(startedAt) >= AppStartTimeout)
            {
                if (_options.AppExePath is { } appExePath)
                {
                    _system.TryStartApp(appExePath);
                }

                break;
            }

            await Task.Delay(PollInterval, _timeProvider, cancellationToken);
        }

        await Task.Delay(CompletedDisplayDuration, _timeProvider, cancellationToken);
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 更新窗口是收银员此刻唯一能看到的界面，任何意外都要落到可重试、可打开旧版的失败页。
            ReportUnexpectedFailure(ex);
        }
    }

    private void OpenCurrentVersion()
    {
        if (_options.AppExePath is { } appExePath && _system.TryStartApp(appExePath))
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _viewModel.ShowOpenAppFailed();
    }

    private void ViewLog()
    {
        if (_options.LogPath is { } logPath)
        {
            _system.TryOpenFile(logPath);
        }
    }

    private void ShowFailed(UpdaterFailureKind kind, string? detail)
    {
        _viewModel.ShowFailed(kind, detail, CanViewLog());
    }

    private bool CanViewLog()
    {
        return _options.LogPath is { } logPath && _system.FileExists(logPath);
    }

    private string ResolveAppProcessName()
    {
        var name = _options.AppExePath is null ? null : Path.GetFileNameWithoutExtension(_options.AppExePath);
        return string.IsNullOrWhiteSpace(name) ? DefaultAppProcessName : name;
    }

    private static UpdaterFailureKind MapExitCode(int exitCode)
    {
        // Inno Setup 退出码：2/5 为用户取消，8 为需要重启后才能继续。
        return exitCode switch
        {
            2 or 5 => UpdaterFailureKind.InstallerCancelled,
            8 => UpdaterFailureKind.RestartRequired,
            _ => UpdaterFailureKind.InstallerFailed
        };
    }
}
