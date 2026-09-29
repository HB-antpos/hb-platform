using Hbpos.Updater;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class UpdateSessionTests
{
    private const string InstallerPath = @"C:\Updates\Hbpos.Client.Wpf-1.9.0-x64.exe";
    private const string ProgressPath = @"C:\Temp\HbposUpdater\run\installer-progress.txt";
    private const string LogPath = @"C:\Logs\install-1.9.0.log";
    private const string AppExePath = @"C:\Program Files\HB POS\Hbpos.Client.Wpf.exe";

    [Fact]
    public async Task RunAsync_waits_for_old_app_then_reports_installer_progress_and_exits_after_new_app_starts()
    {
        var harness = new SessionHarness();
        var run = harness.Session.RunAsync();

        Assert.Equal(UpdaterStage.Closing, harness.ViewModel.Stage);
        Assert.Empty(harness.System.InstallerStarts);

        harness.System.OldAppExit.TrySetResult();
        await WaitUntilAsync(() => harness.System.CurrentInstaller is not null);
        Assert.Equal(UpdaterStage.Installing, harness.ViewModel.Stage);
        Assert.Equal(ProgressPath, Assert.Single(harness.System.DeletedFiles));

        harness.System.ProgressText = "install 40";
        await harness.AdvanceUntilAsync(() => harness.ViewModel.PercentText == "40%");
        Assert.True(harness.ViewModel.IsDeterminate);
        Assert.Equal(40, harness.ViewModel.ProgressValue);

        harness.System.AppRunning = true;
        harness.System.CurrentInstaller!.Exit(0);
        await harness.AdvanceUntilAsync(() => harness.ExitRequested);

        await run;
        Assert.True(harness.ViewModel.IsCompleted);
        Assert.Equal(UpdaterStepState.Done, harness.ViewModel.InstallStep.State);
        Assert.Equal(UpdaterStepState.Active, harness.ViewModel.LaunchStep.State);
        Assert.Empty(harness.System.StartedApps);
    }

    [Fact]
    public async Task RunAsync_passes_progress_file_and_log_to_installer_arguments()
    {
        var harness = new SessionHarness();
        _ = harness.Session.RunAsync();
        harness.System.OldAppExit.TrySetResult();

        await WaitUntilAsync(() => harness.System.CurrentInstaller is not null);

        var start = Assert.Single(harness.System.InstallerStarts);
        Assert.Equal(InstallerPath, start.Path);
        Assert.Equal(
            $"/SP- /VERYSILENT /HBPOSPROGRESS=\"{ProgressPath}\" /LOG=\"{LogPath}\"",
            start.Arguments);
    }

    [Fact]
    public async Task RunAsync_omits_log_argument_when_log_directory_cannot_be_prepared()
    {
        var harness = new SessionHarness();
        harness.System.CanPrepareLog = false;
        _ = harness.Session.RunAsync();
        harness.System.OldAppExit.TrySetResult();

        await WaitUntilAsync(() => harness.System.CurrentInstaller is not null);

        Assert.DoesNotContain("/LOG=", Assert.Single(harness.System.InstallerStarts).Arguments);
    }

    [Fact]
    public async Task RunAsync_starts_installer_when_old_app_does_not_exit_before_timeout()
    {
        var harness = new SessionHarness();
        _ = harness.Session.RunAsync();

        await harness.AdvanceUntilAsync(() => harness.System.CurrentInstaller is not null);

        Assert.True(harness.Time.GetUtcNow() - harness.StartedAt >= UpdateSession.OldAppExitTimeout);
    }

    [Fact]
    public async Task RunAsync_opens_app_itself_when_installer_did_not_start_it()
    {
        var harness = new SessionHarness();
        var run = harness.Session.RunAsync();
        harness.System.OldAppExit.TrySetResult();
        await WaitUntilAsync(() => harness.System.CurrentInstaller is not null);

        harness.System.CurrentInstaller!.Exit(0);
        await harness.AdvanceUntilAsync(() => harness.ExitRequested);

        await run;
        Assert.Equal(AppExePath, Assert.Single(harness.System.StartedApps));
        Assert.Equal("Hbpos.Client.Wpf", harness.System.LastQueriedProcessName);
    }

    [Theory]
    [InlineData(4, nameof(UpdaterFailureKind.InstallerFailed))]
    [InlineData(7, nameof(UpdaterFailureKind.InstallerFailed))]
    [InlineData(2, nameof(UpdaterFailureKind.InstallerCancelled))]
    [InlineData(5, nameof(UpdaterFailureKind.InstallerCancelled))]
    [InlineData(8, nameof(UpdaterFailureKind.RestartRequired))]
    public async Task RunAsync_shows_failure_page_for_non_zero_installer_exit_code(int exitCode, string expectedKind)
    {
        var harness = new SessionHarness();
        harness.System.ExistingFiles.Add(LogPath);
        var run = harness.Session.RunAsync();
        harness.System.OldAppExit.TrySetResult();
        await WaitUntilAsync(() => harness.System.CurrentInstaller is not null);

        harness.System.CurrentInstaller!.Exit(exitCode);
        await run;

        var expected = new UpdaterViewModel(UpdaterStrings.ChineseSimplified, "1.8.3", "1.9.0");
        expected.ShowFailed(Enum.Parse<UpdaterFailureKind>(expectedKind), exitCode.ToString(), canViewLog: true);
        Assert.True(harness.ViewModel.IsFailed);
        Assert.Equal(expected.FailureMessage, harness.ViewModel.FailureMessage);
        Assert.True(harness.ViewModel.CanViewLog);
        Assert.True(harness.ViewModel.CanClose);
        Assert.False(harness.ExitRequested);
    }

    [Fact]
    public async Task RunAsync_reports_cancelled_elevation_without_exiting()
    {
        var harness = new SessionHarness();
        harness.System.NextStartException = new InstallerLaunchException("The operation was canceled by the user.", elevationCancelled: true);

        var run = harness.Session.RunAsync();
        harness.System.OldAppExit.TrySetResult();
        await run;

        Assert.True(harness.ViewModel.IsFailed);
        Assert.Equal(UpdaterStrings.ChineseSimplified.ElevationCancelledMessage, harness.ViewModel.FailureMessage);
        Assert.False(harness.ViewModel.CanViewLog);
    }

    [Fact]
    public async Task RunAsync_reports_missing_installer_without_starting_it()
    {
        var harness = new SessionHarness();
        harness.System.ExistingFiles.Remove(InstallerPath);

        var run = harness.Session.RunAsync();
        harness.System.OldAppExit.TrySetResult();
        await run;

        Assert.Empty(harness.System.InstallerStarts);
        Assert.Equal(UpdaterStrings.ChineseSimplified.InstallerMissingMessage, harness.ViewModel.FailureMessage);
    }

    [Fact]
    public async Task Retry_runs_installer_again_after_failure()
    {
        var harness = new SessionHarness();
        var run = harness.Session.RunAsync();
        harness.System.OldAppExit.TrySetResult();
        await WaitUntilAsync(() => harness.System.CurrentInstaller is not null);
        var firstInstaller = harness.System.CurrentInstaller!;
        firstInstaller.Exit(4);
        await run;

        harness.ViewModel.RetryCommand.Execute(null);

        await WaitUntilAsync(() => harness.System.InstallerStarts.Count == 2 && harness.System.CurrentInstaller != firstInstaller);
        Assert.Equal(UpdaterStage.Installing, harness.ViewModel.Stage);
        harness.System.AppRunning = true;
        harness.System.CurrentInstaller!.Exit(0);
        await harness.AdvanceUntilAsync(() => harness.ExitRequested);
        Assert.True(harness.ViewModel.IsCompleted);
    }

    [Fact]
    public async Task Open_current_version_starts_app_and_requests_exit()
    {
        var harness = new SessionHarness();
        var run = harness.Session.RunAsync();
        harness.System.OldAppExit.TrySetResult();
        await WaitUntilAsync(() => harness.System.CurrentInstaller is not null);
        harness.System.CurrentInstaller!.Exit(4);
        await run;

        harness.ViewModel.OpenCurrentVersionCommand.Execute(null);

        Assert.Equal(AppExePath, Assert.Single(harness.System.StartedApps));
        Assert.True(harness.ExitRequested);
    }

    [Fact]
    public async Task Open_current_version_keeps_window_when_app_cannot_start()
    {
        var harness = new SessionHarness();
        harness.System.StartAppResult = false;
        var run = harness.Session.RunAsync();
        harness.System.OldAppExit.TrySetResult();
        await WaitUntilAsync(() => harness.System.CurrentInstaller is not null);
        harness.System.CurrentInstaller!.Exit(4);
        await run;

        harness.ViewModel.OpenCurrentVersionCommand.Execute(null);

        Assert.False(harness.ExitRequested);
        Assert.Equal(UpdaterStrings.ChineseSimplified.OpenAppFailedMessage, harness.ViewModel.FailureMessage);
    }

    [Fact]
    public void View_log_opens_install_log()
    {
        var harness = new SessionHarness();

        harness.ViewModel.ViewLogCommand.Execute(null);

        Assert.Equal(LogPath, Assert.Single(harness.System.OpenedFiles));
    }

    [Fact]
    public async Task Unexpected_exception_lands_on_failure_page()
    {
        var harness = new SessionHarness();
        harness.System.ReadProgressException = new InvalidOperationException("boom");
        var run = harness.Session.RunAsync();
        harness.System.OldAppExit.TrySetResult();

        await run;

        Assert.True(harness.ViewModel.IsFailed);
        Assert.Contains("boom", harness.ViewModel.FailureMessage);
    }

    private sealed class SessionHarness
    {
        public SessionHarness()
        {
            System.ExistingFiles.Add(InstallerPath);
            StartedAt = Time.GetUtcNow();
            ViewModel = new UpdaterViewModel(UpdaterStrings.ChineseSimplified, "1.8.3", "1.9.0");
            Session = new UpdateSession(
                new UpdaterOptions
                {
                    InstallerPath = InstallerPath,
                    InstallerArguments = "/SP- /VERYSILENT",
                    AppExePath = AppExePath,
                    WaitProcessId = 4242,
                    FromVersion = "1.8.3",
                    ToVersion = "1.9.0",
                    Culture = "zh-CN",
                    LogPath = LogPath
                },
                System,
                Time,
                ViewModel,
                ProgressPath);
            Session.ExitRequested += (_, _) => _exitRequested = true;
        }

        private volatile bool _exitRequested;

        public FakeUpdaterSystem System { get; } = new();

        public FakeTimeProvider Time { get; } = new();

        public DateTimeOffset StartedAt { get; }

        public UpdaterViewModel ViewModel { get; }

        public UpdateSession Session { get; }

        public bool ExitRequested => _exitRequested;

        public async Task AdvanceUntilAsync(Func<bool> condition)
        {
            // 推进虚拟时间驱动会话里的轮询与等待；每步让出一下，给已完成的计时器续体调度机会。
            for (var step = 0; step < 400 && !condition(); step++)
            {
                Time.Advance(UpdateSession.PollInterval);
                await Task.Delay(1);
            }

            await WaitUntilAsync(condition);
        }
    }

    private sealed class FakeUpdaterSystem : IUpdaterSystem
    {
        private readonly object _gate = new();
        private volatile FakeInstaller? _currentInstaller;
        private volatile string? _progressText;
        private volatile bool _appRunning;

        public TaskCompletionSource OldAppExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Path, string Arguments)> InstallerStarts { get; } = [];

        public List<string> DeletedFiles { get; } = [];

        public List<string> StartedApps { get; } = [];

        public List<string> OpenedFiles { get; } = [];

        public FakeInstaller? CurrentInstaller => _currentInstaller;

        public string? ProgressText
        {
            get => _progressText;
            set => _progressText = value;
        }

        public bool AppRunning
        {
            get => _appRunning;
            set => _appRunning = value;
        }

        public bool CanPrepareLog { get; set; } = true;

        public bool StartAppResult { get; set; } = true;

        public Exception? NextStartException { get; set; }

        public Exception? ReadProgressException { get; set; }

        public string? LastQueriedProcessName { get; private set; }

        public Task WaitForProcessExitAsync(int processId, CancellationToken cancellationToken)
        {
            Assert.Equal(4242, processId);
            return OldAppExit.Task.WaitAsync(cancellationToken);
        }

        public Task<IInstallerProcess> StartInstallerAsync(string installerPath, string arguments)
        {
            lock (_gate)
            {
                InstallerStarts.Add((installerPath, arguments));
            }

            if (NextStartException is { } exception)
            {
                NextStartException = null;
                return Task.FromException<IInstallerProcess>(exception);
            }

            var installer = new FakeInstaller();
            _currentInstaller = installer;
            return Task.FromResult<IInstallerProcess>(installer);
        }

        public bool FileExists(string path) => ExistingFiles.Contains(path);

        public string? TryReadAllText(string path)
        {
            if (ReadProgressException is { } exception)
            {
                throw exception;
            }

            return string.Equals(path, ProgressPath, StringComparison.OrdinalIgnoreCase) ? ProgressText : null;
        }

        public void TryDeleteFile(string path)
        {
            lock (_gate)
            {
                DeletedFiles.Add(path);
            }
        }

        public bool TryEnsureParentDirectory(string path) => CanPrepareLog;

        public bool IsProcessRunning(string processName, int? excludedProcessId)
        {
            Assert.Equal(4242, excludedProcessId);
            LastQueriedProcessName = processName;
            return AppRunning;
        }

        public bool TryStartApp(string exePath)
        {
            lock (_gate)
            {
                StartedApps.Add(exePath);
            }

            return StartAppResult;
        }

        public bool TryOpenFile(string path)
        {
            OpenedFiles.Add(path);
            return true;
        }
    }

    private sealed class FakeInstaller : IInstallerProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExitCode { get; private set; }

        public void Exit(int exitCode)
        {
            ExitCode = exitCode;
            _exit.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

        public void Dispose()
        {
        }
    }
}
