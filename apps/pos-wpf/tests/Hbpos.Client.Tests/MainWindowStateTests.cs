using System.Windows;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

[Collection(ShutdownTimingTestCollection.Name)]
public sealed class MainWindowStateTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData(MainWindow.FullscreenWindowModeValue)]
    public void ResolveWindowState_defaults_to_fullscreen(string? savedMode)
    {
        Assert.Equal(WindowState.Maximized, MainWindow.ResolveWindowState(savedMode));
    }

    [Theory]
    [InlineData(MainWindow.NormalCenteredWindowModeValue)]
    [InlineData("normalcentered")]
    public void ResolveWindowState_restores_normal_centered_mode(string savedMode)
    {
        Assert.Equal(WindowState.Normal, MainWindow.ResolveWindowState(savedMode));
    }

    [Theory]
    [InlineData(WindowState.Maximized, MainWindow.FullscreenWindowModeValue)]
    [InlineData(WindowState.Normal, MainWindow.NormalCenteredWindowModeValue)]
    [InlineData(WindowState.Minimized, null)]
    public void GetPersistedWindowMode_ignores_minimized(
        WindowState state,
        string? expectedMode)
    {
        Assert.Equal(expectedMode, MainWindow.GetPersistedWindowMode(state));
    }

    [Fact]
    public void Display_diagnostics_shows_high_dpi_screen_content_now_fits_above_the_taskbar()
    {
        // 1920×1080 @200%，Win11 任务栏 48 DIP = 96 设备像素：工作区只剩 960×492 DIP。
        var display = new DisplayBounds(IntPtr.Zero, 0, 0, 1920, 1080, 0, 0, 1920, 984);

        var message = MainWindow.BuildDisplayDiagnosticsMessage(display, 2d, 960d, 492d, WindowState.Maximized);

        Assert.Contains("monitor=1920x1080 workArea=1920x984 taskbarReserved=0x96", message, StringComparison.Ordinal);
        Assert.Contains("dpiScale=2 windowDip=960x492 windowState=Maximized", message, StringComparison.Ordinal);
        Assert.Contains("contentScale=0.68 contentLogical=1412x724 contentClipped=False", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_diagnostics_reveals_auto_hidden_taskbar_and_unknown_display()
    {
        var autoHidden = new DisplayBounds(IntPtr.Zero, 0, 0, 1366, 768, 0, 0, 1366, 768);

        var autoHiddenMessage = MainWindow.BuildDisplayDiagnosticsMessage(autoHidden, 1d, 1366d, 768d, WindowState.Maximized);
        var unknownMessage = MainWindow.BuildDisplayDiagnosticsMessage(null, 1.25d, 1092.8d, 576d, WindowState.Normal);

        Assert.Contains("taskbarReserved=0x0", autoHiddenMessage, StringComparison.Ordinal);
        Assert.Contains("contentScale=1 contentLogical=1366x768 contentClipped=False", autoHiddenMessage, StringComparison.Ordinal);
        Assert.Contains("monitor=<unknown> workArea=<unknown> taskbarReserved=<unknown>", unknownMessage, StringComparison.Ordinal);
        Assert.Contains("dpiScale=1.25 windowDip=1092.8x576 windowState=Normal", unknownMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadWindowStateAsync_falls_back_to_fullscreen_when_local_settings_fail()
    {
        var repository = new RecordingSettingsRepository { ExceptionToThrow = new InvalidOperationException("broken") };
        Exception? reportedException = null;

        var state = await MainWindow.LoadWindowStateAsync(repository, ex => reportedException = ex);

        Assert.Equal(WindowState.Maximized, state);
        Assert.Same(repository.ExceptionToThrow, reportedException);
    }

    [Fact]
    public async Task PersistWindowModeAfterAsync_waits_for_previous_save_and_swallows_write_failure()
    {
        var previousSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new RecordingSettingsRepository { ExceptionToThrow = new InvalidOperationException("broken") };
        Exception? reportedException = null;

        var saveTask = MainWindow.PersistWindowModeAfterAsync(
            previousSave.Task,
            repository,
            MainWindow.NormalCenteredWindowModeValue,
            ex => reportedException = ex);

        Assert.Equal(0, repository.SetCallCount);

        previousSave.SetResult();
        await saveTask;

        Assert.Equal(1, repository.SetCallCount);
        Assert.Same(repository.ExceptionToThrow, reportedException);
    }

    [Fact]
    public async Task WaitForPendingWindowModeSaveAsync_waits_for_latest_queued_save()
    {
        var firstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestSave = firstSave.Task;
        var waitTask = MainWindow.WaitForPendingWindowModeSaveAsync(() => latestSave);

        var secondSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        latestSave = secondSave.Task;
        firstSave.SetResult();

        Assert.False(waitTask.IsCompleted);

        secondSave.SetResult();
        await waitTask;
    }

    [Fact]
    public async Task WaitForClosePreparationAsync_waits_for_window_save_before_shutdown_steps()
    {
        var windowSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var coordinator = new AppShutdownCoordinator();
        coordinator.RegisterStep(
            "host",
            100,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                calls.Add("host");
                return Task.CompletedTask;
            });

        var waitTask = MainWindow.WaitForClosePreparationAsync(() => windowSave.Task, coordinator);

        Assert.False(coordinator.IsPreparationStarted);
        Assert.Empty(calls);

        windowSave.SetResult();
        await waitTask;

        Assert.Equal(["host"], calls);
        Assert.True(coordinator.IsPrepared);
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task WaitForClosePreparationAsync_propagates_fatal_shutdown_exception_instance(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal window shutdown")
            : new StackOverflowException("fatal window shutdown");
        var coordinator = new AppShutdownCoordinator();
        coordinator.RegisterStep(
            "fatal",
            100,
            TimeSpan.FromSeconds(1),
            _ => throw fatal);

        var thrown = await Record.ExceptionAsync(() =>
            MainWindow.WaitForClosePreparationAsync(() => Task.CompletedTask, coordinator));

        Assert.Same(fatal, thrown);
    }

    [Fact]
    public async Task WaitForClosePreparationAsync_does_not_block_shutdown_on_stuck_window_save()
    {
        var stuckSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new AppShutdownCoordinator();
        var shutdownStepCalled = false;
        coordinator.RegisterStep(
            "host",
            100,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                shutdownStepCalled = true;
                return Task.CompletedTask;
            });

        try
        {
            await MainWindow.WaitForClosePreparationAsync(
                () => stuckSave.Task,
                coordinator).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            Assert.True(shutdownStepCalled);
            Assert.True(coordinator.IsPrepared);
        }
        finally
        {
            stuckSave.TrySetResult();
        }
    }

    [Fact]
    public async Task WaitForClosePreparationAsync_logs_the_actual_window_save_budget()
    {
        var stuckSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new FixedBudgetShutdownCoordinator(TimeSpan.FromMilliseconds(25));
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void CaptureLine(string line) => lines.Enqueue(line);

        ConsoleLog.LineWritten += CaptureLine;
        try
        {
            await MainWindow.WaitForClosePreparationAsync(
                () => stuckSave.Task,
                coordinator).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            Assert.Contains(lines, line =>
                line.Contains(
                    "window state did not finish within 25ms",
                    StringComparison.Ordinal));
            Assert.True(coordinator.PrepareCalled);
        }
        finally
        {
            ConsoleLog.LineWritten -= CaptureLine;
            stuckSave.TrySetResult();
        }
    }

    [Fact]
    public void MainWindow_close_owns_deadline_first_and_closed_does_not_repeat_external_cleanup()
    {
        var source = ReadMainWindowSource();
        var closingBody = Slice(source, "private async void MainWindowClosing", "private IntPtr MainWindowMessageHook");
        var closedBody = Slice(source, "private void MainWindowClosed", "private async void MainWindowClosing");
        var firstClosingStatement = closingBody[(closingBody.IndexOf('{') + 1)..]
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .First(line => line is not "}");

        Assert.Equal(
            "_ = _appShutdownCoordinator.GetOrStartRemainingBudget();",
            firstClosingStatement);
        Assert.True(
            closingBody.IndexOf("_viewModel.BeginShutdown();", StringComparison.Ordinal) <
            closingBody.IndexOf("_rawScannerService.Stop();", StringComparison.Ordinal));
        Assert.True(
            closingBody.IndexOf("_rawScannerService.Stop();", StringComparison.Ordinal) <
            closingBody.IndexOf("await WaitForClosePreparationAsync", StringComparison.Ordinal));
        Assert.DoesNotContain("StopExternalInputForShutdownAsync", closingBody, StringComparison.Ordinal);
        Assert.Contains(
            "catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)",
            closingBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_rawScannerService.Stop()", closedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_viewModel.Dispose()", closedBody, StringComparison.Ordinal);
    }

    private static string ReadMainWindowSource()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                var path = Path.Combine(
                    current.FullName,
                    "apps",
                    "pos-wpf",
                    "src",
                    "Hbpos.Client.Wpf",
                    "MainWindow.xaml.cs");
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate MainWindow.xaml.cs.");
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private sealed class RecordingSettingsRepository : ILocalAppSettingsRepository
    {
        public Exception? ExceptionToThrow { get; init; }

        public int SetCallCount { get; private set; }

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            return ExceptionToThrow is null
                ? Task.FromResult<string?>(null)
                : Task.FromException<string?>(ExceptionToThrow);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            SetCallCount++;
            return ExceptionToThrow is null
                ? Task.CompletedTask
                : Task.FromException(ExceptionToThrow);
        }

        public Task SetValuesAsync(
            IReadOnlyDictionary<string, string> values,
            CancellationToken cancellationToken = default)
        {
            SetCallCount++;
            return ExceptionToThrow is null
                ? Task.CompletedTask
                : Task.FromException(ExceptionToThrow);
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FixedBudgetShutdownCoordinator(TimeSpan budget) : IAppShutdownCoordinator
    {
        public bool IsPreparationStarted => PrepareCalled;

        public bool IsPrepared => PrepareCalled;

        public bool PrepareCalled { get; private set; }

        public TimeSpan GetOrStartRemainingBudget() => budget;

        public void RegisterStep(
            string name,
            int order,
            TimeSpan timeout,
            Func<CancellationToken, Task> executeAsync)
        {
        }

        public Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            PrepareCalled = true;
            return Task.CompletedTask;
        }
    }
}
