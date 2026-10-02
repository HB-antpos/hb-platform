using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Services;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace Hbpos.Client.Tests;

[Collection(ShutdownTimingTestCollection.Name)]
public sealed class AppShutdownCoordinatorTests
{
    [Fact]
    public void GetOrStartRemainingBudget_uses_one_deadline_without_waiting()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero));
        var coordinator = new AppShutdownCoordinator(timeProvider, TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(3), coordinator.GetOrStartRemainingBudget());
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(2), coordinator.GetOrStartRemainingBudget());
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.Zero, coordinator.GetOrStartRemainingBudget());
    }

    [Fact]
    public async Task PrepareAsync_runs_registered_steps_in_order_once()
    {
        var coordinator = new AppShutdownCoordinator();
        var calls = new List<string>();
        coordinator.RegisterStep(
            "host",
            200,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                calls.Add("host");
                return Task.CompletedTask;
            });
        coordinator.RegisterStep(
            "offline",
            100,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                calls.Add("offline");
                return Task.CompletedTask;
            });

        await Task.WhenAll(coordinator.PrepareAsync(), coordinator.PrepareAsync());

        Assert.Equal(["offline", "host"], calls);
        Assert.True(coordinator.IsPreparationStarted);
        Assert.True(coordinator.IsPrepared);
    }

    [Fact]
    public async Task PrepareAsync_continues_after_step_failure()
    {
        var coordinator = new AppShutdownCoordinator();
        var secondStepCalled = false;
        coordinator.RegisterStep(
            "offline",
            100,
            TimeSpan.FromSeconds(1),
            _ => throw new InvalidOperationException("offline failed"));
        coordinator.RegisterStep(
            "host",
            200,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                secondStepCalled = true;
                return Task.CompletedTask;
            });

        await coordinator.PrepareAsync();

        Assert.True(secondStepCalled);
        Assert.True(coordinator.IsPrepared);
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task PrepareAsync_propagates_fatal_step_exception_instance(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal shutdown step")
            : new StackOverflowException("fatal shutdown step");
        var coordinator = new AppShutdownCoordinator();
        coordinator.RegisterStep(
            "fatal",
            100,
            TimeSpan.FromSeconds(1),
            _ => throw fatal);

        var thrown = await Record.ExceptionAsync(() => coordinator.PrepareAsync());

        Assert.Same(fatal, thrown);
        Assert.True(coordinator.IsPrepared);
    }

    [Fact]
    public async Task PrepareAsync_continues_after_step_timeout()
    {
        var coordinator = new AppShutdownCoordinator();
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOutStepCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStepCalled = false;
        coordinator.RegisterStep(
            "offline",
            100,
            TimeSpan.FromMilliseconds(20),
            async _ =>
            {
                try
                {
                    await neverCompletes.Task;
                }
                finally
                {
                    timedOutStepCompleted.TrySetResult();
                }
            });
        coordinator.RegisterStep(
            "host",
            200,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                secondStepCalled = true;
                return Task.CompletedTask;
            });

        try
        {
            await coordinator.PrepareAsync().WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            Assert.True(secondStepCalled);
            Assert.True(coordinator.IsPrepared);
        }
        finally
        {
            neverCompletes.TrySetResult();
            await timedOutStepCompleted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        }
    }

    [Fact]
    public async Task PrepareAsync_logs_actual_remaining_budget_timeout()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero));
        var coordinator = new AppShutdownCoordinator(
            timeProvider,
            totalBudget: TimeSpan.FromSeconds(3));
        _ = coordinator.GetOrStartRemainingBudget();
        timeProvider.Advance(TimeSpan.FromMilliseconds(2950));
        coordinator.RegisterStep(
            "remaining-budget-log",
            100,
            TimeSpan.FromSeconds(1),
            cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();

        void CaptureLine(string line)
        {
            if (line.Contains("step=remaining-budget-log", StringComparison.Ordinal))
            {
                lines.Enqueue(line);
            }
        }

        ConsoleLog.LineWritten += CaptureLine;
        try
        {
            await coordinator.PrepareAsync().WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        }
        finally
        {
            ConsoleLog.LineWritten -= CaptureLine;
        }

        var timeoutLine = Assert.Single(lines);
        Assert.Contains("timeoutMs=50", timeoutLine, StringComparison.Ordinal);
        Assert.DoesNotContain("timeoutMs=1000", timeoutLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareAsync_bounds_synchronous_delegate_prefix_and_continues()
    {
        var coordinator = new AppShutdownCoordinator();
        var blockingStepCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStepCalled = false;
        coordinator.RegisterStep(
            "blocking",
            100,
            TimeSpan.FromMilliseconds(20),
            _ =>
            {
                try
                {
                    Thread.Sleep(100);
                    return Task.CompletedTask;
                }
                finally
                {
                    blockingStepCompleted.TrySetResult();
                }
            });
        coordinator.RegisterStep(
            "next",
            200,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                secondStepCalled = true;
                return Task.CompletedTask;
            });

        try
        {
            await coordinator.PrepareAsync().WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        }
        finally
        {
            await blockingStepCompleted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        }

        Assert.True(secondStepCalled);
        Assert.True(coordinator.IsPrepared);
    }

    [Fact]
    public async Task PrepareAsync_respects_total_budget_across_steps()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero));
        var coordinator = new AppShutdownCoordinator(timeProvider, TimeSpan.FromSeconds(3));
        var hostStepCalled = false;
        coordinator.RegisterStep(
            "offline",
            100,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                timeProvider.Advance(TimeSpan.FromSeconds(3));
                return Task.CompletedTask;
            });
        coordinator.RegisterStep(
            "host",
            200,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                hostStepCalled = true;
                return Task.CompletedTask;
            });

        await coordinator.PrepareAsync();

        Assert.True(coordinator.IsPrepared);
        Assert.False(hostStepCalled);
    }

    [Fact]
    public async Task PrepareAsync_does_not_wait_for_blocking_cancellation_callback()
    {
        var coordinator = new AppShutdownCoordinator();
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOutStepCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStepCalled = false;
        coordinator.RegisterStep(
            "blocking-cancel",
            100,
            TimeSpan.FromMilliseconds(20),
            async token =>
            {
                using var registration = token.Register(() =>
                {
                    callbackStarted.TrySetResult();
                    try
                    {
                        Thread.Sleep(100);
                    }
                    finally
                    {
                        callbackCompleted.TrySetResult();
                    }
                });
                try
                {
                    await neverCompletes.Task;
                }
                finally
                {
                    timedOutStepCompleted.TrySetResult();
                }
            });
        coordinator.RegisterStep(
            "next",
            200,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                nextStepCalled = true;
                return Task.CompletedTask;
            });

        try
        {
            await coordinator.PrepareAsync().WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            await callbackStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            Assert.True(nextStepCalled);
            Assert.True(coordinator.IsPrepared);
        }
        finally
        {
            neverCompletes.TrySetResult();
            await timedOutStepCompleted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            if (callbackStarted.Task.IsCompleted)
            {
                await callbackCompleted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            }
        }
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task App_exit_observer_propagates_fatal_from_timed_out_cancellation_callback_within_total_budget(
        string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("late fatal shutdown cancellation")
            : new StackOverflowException("late fatal shutdown cancellation");
        // 中文注释：协调器用虚拟时钟（原因见 ShutdownStepTimeoutTimeProvider），总预算不会被墙钟耗尽；
        // 步骤超时由测试在步骤已注册取消回调后显式触发。
        var stepTimeout = TimeSpan.FromMilliseconds(20);
        var timeProvider = new ShutdownStepTimeoutTimeProvider(stepTimeout);
        var coordinator = new AppShutdownCoordinator(timeProvider, AsyncTestWaitSupport.DefaultTimeout);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim(false);
        var timedOutStepCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStepCalled = false;
        coordinator.RegisterStep(
            "late-fatal-cancel",
            100,
            stepTimeout,
            async token =>
            {
                using var registration = token.Register(() =>
                {
                    callbackStarted.TrySetResult();
                    releaseCallback.Wait();
                    throw fatal;
                });
                callbackRegistered.TrySetResult();
                try
                {
                    await neverCompletes.Task;
                }
                finally
                {
                    timedOutStepCompleted.TrySetResult();
                }
            });
        coordinator.RegisterStep(
            "release-late-fatal",
            200,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                nextStepCalled = true;
                releaseCallback.Set();
                return Task.CompletedTask;
            });

        try
        {
            // 生产中 OnExit 在 UI 线程同步等待；这里用独立线程，避免测试自身再占一个线程池线程。
            var exitWait = Task.Factory.StartNew(
                () => App.WaitForShutdownPreparation(coordinator, AsyncTestWaitSupport.DefaultTimeout),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            // 先确认回调已挂在步骤 token 上，致命异常才来自"超时取消回调"而不是步骤体内的同步 Register。
            await callbackRegistered.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            await timeProvider.FireStepTimeoutAsync();

            var thrown = await Record.ExceptionAsync(() => exitWait);

            Assert.Same(fatal, thrown);
            Assert.True(nextStepCalled);
            await callbackStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        }
        finally
        {
            releaseCallback.Set();
            neverCompletes.TrySetResult();
            await timedOutStepCompleted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        }
    }

    [Fact]
    public async Task RegisterStep_rejects_late_registration()
    {
        var coordinator = new AppShutdownCoordinator();
        coordinator.RegisterStep("host", 100, TimeSpan.FromSeconds(1), _ => Task.CompletedTask);
        await coordinator.PrepareAsync();

        Assert.Throws<InvalidOperationException>(() =>
            coordinator.RegisterStep("late", 200, TimeSpan.FromSeconds(1), _ => Task.CompletedTask));
    }

    [Fact]
    public void App_OnExit_is_synchronous()
    {
        var onExit = typeof(App).GetMethod("OnExit", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(onExit);
        Assert.Null(onExit!.GetCustomAttribute<AsyncStateMachineAttribute>());
    }

    [Fact]
    public void App_OnExit_starts_shared_budget_before_startup_cleanup()
    {
        var source = ReadAppSource();
        var start = source.IndexOf("protected override void OnExit", StringComparison.Ordinal);
        var end = source.IndexOf("private static void LogShutdownCleanupFailure", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var onExitBody = source[start..end];

        var budgetStart = onExitBody.IndexOf("GetOrStartRemainingBudget()", StringComparison.Ordinal);
        var firstCleanup = onExitBody.IndexOf("FinishStartupExperience()", StringComparison.Ordinal);

        Assert.True(budgetStart >= 0);
        Assert.True(firstCleanup > budgetStart);
    }

    [Fact]
    public void App_exit_fallback_runs_shutdown_preparation_when_window_close_was_bypassed()
    {
        var coordinator = new AppShutdownCoordinator();
        var called = false;
        coordinator.RegisterStep(
            "host",
            100,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            });

        var prepared = App.WaitForShutdownPreparation(coordinator, TimeSpan.FromSeconds(1));

        Assert.True(prepared);
        Assert.True(called);
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public void App_exit_fallback_propagates_fatal_shutdown_exception_instance(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal fallback shutdown")
            : new StackOverflowException("fatal fallback shutdown");
        var coordinator = new AppShutdownCoordinator();
        coordinator.RegisterStep(
            "fatal",
            100,
            TimeSpan.FromSeconds(1),
            _ => throw fatal);

        var thrown = Record.Exception(() =>
            App.WaitForShutdownPreparation(coordinator, TimeSpan.FromSeconds(1)));

        Assert.Same(fatal, thrown);
    }

    [Fact]
    public async Task App_exit_fallback_waits_for_already_started_preparation()
    {
        var coordinator = new AppShutdownCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.RegisterStep(
            "host",
            100,
            TimeSpan.FromSeconds(1),
            async _ =>
            {
                started.TrySetResult();
                await release.Task;
            });
        var firstPreparation = coordinator.PrepareAsync();
        await started.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        var exitWait = Task.Run(() =>
            App.WaitForShutdownPreparation(coordinator, TimeSpan.FromSeconds(1)));
        release.TrySetResult();

        Assert.True(await exitWait);
        await firstPreparation;
    }

    [Fact]
    public async Task App_host_dispose_fallback_is_bounded()
    {
        var disposable = new GatedDisposable();

        try
        {
            var startedAt = Stopwatch.GetTimestamp();

            var disposed = App.DisposeHostWithinTimeout(
                disposable,
                TimeSpan.FromMilliseconds(20),
                "test");

            Assert.False(disposed);
            Assert.True(
                Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(1),
                "host dispose fallback should not block application exit");
        }
        finally
        {
            // 固定延时会受测试运行器调度影响；断言完成后再开门，确保 Dispose 必然跨过 20ms 超时边界。
            disposable.Release();
            await disposable.Completed.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        }
    }

    [Fact]
    public void App_host_dispose_does_not_start_after_budget_is_exhausted()
    {
        var disposable = new CountingDisposable();

        var disposed = App.DisposeHostWithinTimeout(
            disposable,
            TimeSpan.Zero,
            "test-zero-budget");

        Assert.False(disposed);
        Assert.Equal(0, disposable.DisposeCallCount);
    }

    private sealed class GatedDisposable : IDisposable
    {
        private readonly TaskCompletionSource _releaseGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _releaseGate.TrySetResult();

        public void Dispose()
        {
            try
            {
                _releaseGate.Task.GetAwaiter().GetResult();
            }
            finally
            {
                Completed.TrySetResult();
            }
        }
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCallCount { get; private set; }

        public void Dispose()
        {
            DisposeCallCount++;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private static string ReadAppSource()
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
                    "App.xaml.cs");
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate App.xaml.cs.");
    }
}
