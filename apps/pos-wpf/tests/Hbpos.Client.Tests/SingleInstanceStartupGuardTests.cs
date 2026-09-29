using Hbpos.Client.Wpf.Services;
using System.Threading;
using System.Windows.Threading;

namespace Hbpos.Client.Tests;

public sealed class SingleInstanceStartupGuardTests
{
    [Fact]
    public void Selector_only_returns_same_executable_path_and_excludes_current_process()
    {
        var provider = new FakeProcessProvider(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe", []);
        var currentProcess = new FakeRunningProcess(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe");
        var sameExecutable = new FakeRunningProcess(11, @"C:\HBPOS\Hbpos.Client.Wpf.exe");
        var differentExecutable = new FakeRunningProcess(12, @"C:\Other\Hbpos.Client.Wpf.exe");
        var inaccessibleExecutable = new FakeRunningProcess(13, null);

        var processes = new IRunningProcess[]
        {
            currentProcess,
            sameExecutable,
            differentExecutable,
            inaccessibleExecutable
        };

        var result = SingleInstanceProcessSelector.FindReplaceableProcesses(provider, processes);

        var process = Assert.Single(result);
        Assert.Equal(11, process.Id);
    }

    [Fact]
    public async Task TryAcquire_returns_startup_in_progress_when_startup_gate_is_held()
    {
        var options = CreateOptions();
        using var startupGate = new Semaphore(1, 1, options.StartupGateName);
        Assert.True(startupGate.WaitOne(TimeSpan.Zero));
        var process = new FakeRunningProcess(11, @"C:\HBPOS\Hbpos.Client.Wpf.exe");
        var provider = new FakeProcessProvider(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe", [process]);
        var guard = new SingleInstanceStartupGuard(provider, options);

        try
        {
            var result = await guard.TryAcquireAsync(previewMode: false);

            Assert.False(result.CanStart);
            Assert.Equal(SingleInstanceStartupStatus.AnotherStartupInProgress, result.Status);
            Assert.Equal(0, provider.GetSiblingProcessesCallCount);
            Assert.Equal(0, process.CloseMainWindowCallCount);
            Assert.False(process.KillCalled);
        }
        finally
        {
            startupGate.Release();
        }
    }

    [Fact]
    public async Task TryAcquire_kills_existing_process_when_graceful_close_does_not_exit()
    {
        var options = CreateOptions();
        var process = new FakeRunningProcess(11, @"C:\HBPOS\Hbpos.Client.Wpf.exe")
        {
            CloseMainWindowResult = false,
            WaitForExitResult = false
        };
        var provider = new FakeProcessProvider(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe", [process]);
        var guard = new SingleInstanceStartupGuard(provider, options);

        using var lease = (await guard.TryAcquireAsync(previewMode: false)).Lease;

        Assert.NotNull(lease);
        Assert.Equal(1, process.CloseMainWindowCallCount);
        Assert.True(process.KillCalled);
        Assert.True(process.KillEntireProcessTree);
    }

    [Fact]
    public async Task TryAcquire_does_not_kill_when_graceful_close_exits()
    {
        var options = CreateOptions();
        var process = new FakeRunningProcess(11, @"C:\HBPOS\Hbpos.Client.Wpf.exe")
        {
            CloseMainWindowResult = true,
            WaitForExitResult = true
        };
        var provider = new FakeProcessProvider(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe", [process]);
        var guard = new SingleInstanceStartupGuard(provider, options);

        using var lease = (await guard.TryAcquireAsync(previewMode: false)).Lease;

        Assert.NotNull(lease);
        Assert.Equal(1, process.CloseMainWindowCallCount);
        Assert.False(process.KillCalled);
    }

    [Fact]
    public async Task TryAcquire_returns_acquired_when_running_mutex_was_abandoned_and_lease_release_allows_reacquire()
    {
        // Thread.Join 返回时 OS 线程可能尚未退出完毕，互斥体还没被标记为遗弃；
        // 等待预算只作防挂死兜底，让守卫轮询到遗弃发生，而不是 1ms 后误判为超时。
        var options = CreateOptions() with { RunningInstanceWaitTimeout = AsyncTestWaitSupport.DefaultTimeout };
        var process = new FakeRunningProcess(11, @"C:\HBPOS\Hbpos.Client.Wpf.exe");
        var provider = new FakeProcessProvider(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe", [process]);
        var guard = new SingleInstanceStartupGuard(provider, options);

        // 让另一个线程持有真实命名互斥体并在退出前不释放所有权，模拟 Windows 的 abandoned mutex。
        using var threadStarted = new ManualResetEventSlim();
        Mutex? abandonedOwner = null;
        var abandoningThread = new Thread(() =>
        {
            abandonedOwner = new Mutex(true, options.RunningInstanceMutexName);
            threadStarted.Set();
        });
        abandoningThread.Start();
        threadStarted.Wait();
        abandoningThread.Join();

        var result = await guard.TryAcquireAsync(previewMode: false);

        Assert.Equal(SingleInstanceStartupStatus.Acquired, result.Status);
        Assert.True(result.CanStart);
        Assert.NotNull(result.Lease);

        // lease 释放后，同一命名互斥体可再次获得（所有权已归还）。
        result.Lease!.Dispose();
        using var reacquired = new Mutex(false, options.RunningInstanceMutexName);
        Assert.True(reacquired.WaitOne(TimeSpan.FromSeconds(5)));
        reacquired.ReleaseMutex();
        abandonedOwner!.Dispose();
    }

    [Fact]
    public Task TryAcquireAsync_yields_while_waiting_for_graceful_process_exit()
    {
        return RunOnStaDispatcherAsync(async () =>
        {
            var options = CreateOptions();
            var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exitCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = new FakeRunningProcess(11, @"C:\HBPOS\Hbpos.Client.Wpf.exe")
            {
                CloseMainWindowResult = true,
                WaitForExitAsyncHandler = _ =>
                {
                    waitStarted.TrySetResult();
                    return exitCompleted.Task;
                }
            };
            var provider = new FakeProcessProvider(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe", [process]);
            var guard = new SingleInstanceStartupGuard(provider, options);

            var acquireTask = guard.TryAcquireAsync(previewMode: false);
            await waitStarted.Task.WaitUntilCompletedAsync(() => $"acquireTask={acquireTask.Status}");

            // 退出 TCS 只有下面这行能完成，所以"仍未完成"是确定性的，不是时序窗口。
            Assert.False(acquireTask.IsCompleted, $"进程未退出前 TryAcquireAsync 不应完成：status={acquireTask.Status}");
            exitCompleted.SetResult(true);
            // 续体要经 ThreadPool → Dispatcher 两跳调度才回到 STA 线程，CI 负载高时耗时不可控，使用共享预算。
            var result = await acquireTask.WaitUntilCompletedAsync(() => $"killCalled={process.KillCalled}");
            using var lease = result.Lease;
            Assert.True(lease is not null, $"应成功获得租约，实际 status={result.Status}");
            Assert.False(process.KillCalled);
        });
    }

    [Fact]
    public async Task TryAcquireAsync_releases_startup_gate_when_process_wait_faults()
    {
        var options = CreateOptions();
        var process = new FakeRunningProcess(11, @"C:\HBPOS\Hbpos.Client.Wpf.exe")
        {
            CloseMainWindowResult = true,
            WaitForExitAsyncHandler = _ => Task.FromException<bool>(new InvalidOperationException("wait failed"))
        };
        var provider = new FakeProcessProvider(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe", [process]);
        var guard = new SingleInstanceStartupGuard(provider, options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.TryAcquireAsync(previewMode: false));

        using var reacquiredGate = new Semaphore(1, 1, options.StartupGateName);
        Assert.True(reacquiredGate.WaitOne(TimeSpan.Zero));
        reacquiredGate.Release();
    }

    [Fact]
    public async Task TryAcquireAsync_releases_startup_gate_when_running_mutex_times_out()
    {
        var options = CreateOptions();
        using var ownerReady = new ManualResetEventSlim();
        using var releaseOwner = new ManualResetEventSlim();
        Exception? ownerFailure = null;
        var ownerThread = new Thread(() =>
        {
            try
            {
                using var ownedMutex = new Mutex(false, options.RunningInstanceMutexName);
                ownedMutex.WaitOne();
                ownerReady.Set();
                releaseOwner.Wait();
                ownedMutex.ReleaseMutex();
            }
            catch (Exception ex)
            {
                ownerFailure = ex;
                ownerReady.Set();
            }
        });
        ownerThread.Start();
        ownerReady.Wait();

        try
        {
            Assert.Null(ownerFailure);
            var provider = new FakeProcessProvider(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe", []);
            var guard = new SingleInstanceStartupGuard(provider, options);

            var result = await guard.TryAcquireAsync(previewMode: false);

            Assert.Equal(SingleInstanceStartupStatus.ExistingInstanceCouldNotBeStopped, result.Status);
            Assert.Null(result.Lease);
            using var reacquiredGate = new Semaphore(1, 1, options.StartupGateName);
            Assert.True(reacquiredGate.WaitOne(TimeSpan.Zero));
            reacquiredGate.Release();
        }
        finally
        {
            releaseOwner.Set();
            Assert.True(ownerThread.Join(AsyncTestWaitSupport.DefaultTimeout), "Mutex owner thread did not shut down.");
        }

        Assert.Null(ownerFailure);
    }

    private static SingleInstanceStartupGuardOptions CreateOptions()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new SingleInstanceStartupGuardOptions(
            $@"Local\Hbpos.Client.Wpf.Test.StartupGate.{suffix}",
            $@"Local\Hbpos.Client.Wpf.Test.SingleInstance.{suffix}",
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));
    }

    private static async Task RunOnStaDispatcherAsync(Func<Task> action)
    {
        var dispatcherReady = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcherReady.TrySetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                dispatcherReady.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Hbpos.Client.Tests.SingleInstanceDispatcher"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var dispatcher = await dispatcherReady.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        try
        {
            var operation = dispatcher.InvokeAsync(action, DispatcherPriority.Normal);
            await await operation.Task;
        }
        finally
        {
            if (!dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            Assert.True(thread.Join(AsyncTestWaitSupport.DefaultTimeout), "WPF Dispatcher thread did not shut down.");
        }
    }

    private sealed class FakeProcessProvider : IRunningProcessProvider
    {
        private readonly IReadOnlyList<IRunningProcess> _processes;

        public FakeProcessProvider(int currentProcessId, string? currentExecutablePath, IReadOnlyList<IRunningProcess> processes)
        {
            CurrentProcessId = currentProcessId;
            CurrentExecutablePath = currentExecutablePath;
            _processes = processes;
        }

        public int CurrentProcessId { get; }

        public string? CurrentExecutablePath { get; }

        public int GetSiblingProcessesCallCount { get; private set; }

        public ProcessSnapshot GetSiblingProcesses()
        {
            GetSiblingProcessesCallCount++;
            return new ProcessSnapshot(_processes);
        }
    }

    private sealed class FakeRunningProcess : IRunningProcess
    {
        public FakeRunningProcess(int id, string? executablePath)
        {
            Id = id;
            ExecutablePath = executablePath;
        }

        public int Id { get; }

        public string? ExecutablePath { get; }

        public bool HasExited { get; set; }

        public bool CloseMainWindowResult { get; init; }

        public bool WaitForExitResult { get; init; }

        public Func<TimeSpan, Task<bool>>? WaitForExitAsyncHandler { get; init; }

        public int CloseMainWindowCallCount { get; private set; }

        public bool KillCalled { get; private set; }

        public bool KillEntireProcessTree { get; private set; }

        public bool CloseMainWindow()
        {
            CloseMainWindowCallCount++;
            return CloseMainWindowResult;
        }

        public async Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            var result = WaitForExitAsyncHandler is null
                ? WaitForExitResult
                : await WaitForExitAsyncHandler(timeout);
            HasExited = result;
            return result;
        }

        public void Kill(bool entireProcessTree)
        {
            KillCalled = true;
            KillEntireProcessTree = entireProcessTree;
            HasExited = true;
        }

        public void Dispose()
        {
        }
    }
}
