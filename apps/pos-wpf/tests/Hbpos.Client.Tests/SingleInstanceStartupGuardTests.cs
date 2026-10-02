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
    public Task TryAcquire_returns_acquired_when_running_mutex_was_abandoned_and_lease_release_allows_reacquire()
    {
        // 互斥体所有权归属线程：守卫获取与 lease 释放必须在同一线程，生产上由 WPF Dispatcher 保证。
        // 原先直接在 xUnit 同步上下文里 await，续体会被投到任意线程池线程：守卫可能在线程 A 上接管遗弃的
        // 所有权、却在线程 B 上 Dispose；A 退出时互斥体被再次遗弃，最后的获取偶发 AbandonedMutexException
        // （CI 运行 36971189562、36943671269）。因此与生产一致，在单线程 Dispatcher 上执行获取与释放。
        return RunOnStaDispatcherAsync(async () =>
        {
            // 等待预算只作防挂死兜底，让守卫轮询到遗弃发生。
            var options = CreateOptions() with { RunningInstanceWaitTimeout = AsyncTestWaitSupport.DefaultTimeout };
            var process = new FakeRunningProcess(11, @"C:\HBPOS\Hbpos.Client.Wpf.exe");
            var provider = new FakeProcessProvider(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe", [process]);
            var guard = new SingleInstanceStartupGuard(provider, options);

            // 另一个线程持有真实命名互斥体，收到信号后不释放所有权直接退出，模拟 Windows 的 abandoned mutex。
            using var ownerReady = new ManualResetEventSlim();
            using var exitOwner = new ManualResetEventSlim();
            Mutex? abandonedOwner = null;
            var abandoningThread = new Thread(() =>
            {
                abandonedOwner = new Mutex(true, options.RunningInstanceMutexName);
                ownerReady.Set();
                exitOwner.Wait();
            });
            abandoningThread.Start();
            ownerReady.Wait();

            try
            {
                var acquireTask = guard.TryAcquireAsync(previewMode: false);

                // 所有者仍存活，首轮零等待必然失败，守卫进入 await 轮询：这正是依赖线程亲和的路径，
                // 不再取决于 Thread.Join 返回时 OS 线程是否已退出完毕。
                Assert.False(acquireTask.IsCompleted, $"所有者退出前守卫不应获得互斥体：status={acquireTask.Status}");
                exitOwner.Set();
                Assert.True(abandoningThread.Join(AsyncTestWaitSupport.DefaultTimeout), "Abandoning thread did not exit.");

                var result = await acquireTask.WaitUntilCompletedAsync();

                Assert.Equal(SingleInstanceStartupStatus.Acquired, result.Status);
                Assert.True(result.CanStart);
                Assert.NotNull(result.Lease);

                result.Lease!.Dispose();

                // 必须换线程验证：互斥体可重入，原所有者线程自己再次获取总会成功，证明不了所有权已归还。
                // 另一线程能立即正常获得（而非遗弃），说明守卫接管了遗弃的所有权、且 lease 正常释放。
                Assert.Equal(MutexAcquireOutcome.Acquired, TryAcquireOnSeparateThread(options.RunningInstanceMutexName));
            }
            finally
            {
                exitOwner.Set();
                abandoningThread.Join(AsyncTestWaitSupport.DefaultTimeout);
                abandonedOwner?.Dispose();
            }
        });
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

    private enum MutexAcquireOutcome
    {
        Acquired,
        Abandoned,
        HeldByAnotherThread
    }

    // 在独立线程上零等待获取并立即释放；调用前 lease 已同步释放，所以零等待的结果是确定的。
    private static MutexAcquireOutcome TryAcquireOnSeparateThread(string mutexName)
    {
        var outcome = MutexAcquireOutcome.HeldByAnotherThread;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(false, mutexName);
                try
                {
                    if (!mutex.WaitOne(TimeSpan.Zero))
                    {
                        return;
                    }

                    outcome = MutexAcquireOutcome.Acquired;
                }
                catch (AbandonedMutexException)
                {
                    // 遗弃同样把所有权交给了本线程，记下结果后照常释放。
                    outcome = MutexAcquireOutcome.Abandoned;
                }

                mutex.ReleaseMutex();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        Assert.True(thread.Join(AsyncTestWaitSupport.DefaultTimeout), "Mutex reacquire thread did not finish.");
        Assert.Null(failure);
        return outcome;
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
