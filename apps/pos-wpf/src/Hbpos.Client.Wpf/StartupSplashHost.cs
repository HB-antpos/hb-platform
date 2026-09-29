using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf;

/// <summary>
/// 在独立的 STA 线程上承载启动页：主线程解析主窗口 XAML、同步读写 SQLite 时，启动页照常按帧刷新进度。
/// 对外方法都可以从任意线程调用；启动页自身出任何问题都只记日志，绝不影响收银启动。
/// </summary>
internal sealed class StartupSplashHost : IStartupScreen
{
    // 30 帧足够让进度条连续移动，又不会在低配收银机上与主线程争抢太多 CPU。
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan CrossThreadCallTimeout = TimeSpan.FromMilliseconds(500);

    private readonly StartupProgressTracker _tracker;
    private readonly Func<StartupProgressState> _createState;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dispatcher? _dispatcher;
    private StartupSplashWindow? _window;
    private StartupProgressState? _state;
    private bool _closeRequested;
    private int _suspendCount;

    private StartupSplashHost(StartupProgressTracker tracker, Func<StartupProgressState> createState)
    {
        _tracker = tracker;
        _createState = createState;
    }

    public static StartupSplashHost Start(StartupProgressTracker tracker, Func<StartupProgressState> createState)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(createState);
        var host = new StartupSplashHost(tracker, createState);
        var thread = new Thread(host.Run)
        {
            IsBackground = true,
            Name = "HBPOS startup splash"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return host;
    }

    public IDisposable SuspendForDialog()
    {
        _tracker.MarkCurrentPhaseInterrupted();
        Interlocked.Increment(ref _suspendCount);
        // 同步等启动页隐藏完成，保证对话框出现时不会被置顶的启动页盖住。
        InvokeOnSplashThread(window => window.Hide());
        return new Suspension(this);
    }

    /// <summary>请求关闭启动页，可重复调用；返回的任务在启动页线程退出后完成。</summary>
    public Task CloseAsync(bool fade)
    {
        Dispatcher? dispatcher;
        lock (_gate)
        {
            _closeRequested = true;
            dispatcher = _dispatcher;
        }

        if (dispatcher is not null)
        {
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Send, () => CloseOnSplashThread(fade));
            }
            catch (Exception ex)
            {
                ConsoleLog.WriteError("StartupSplash", $"close request failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
                _closed.TrySetResult();
            }
        }

        return _closed.Task;
    }

    private void Run()
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var state = _createState();
            var window = new StartupSplashWindow(state);
            var lastFrame = Stopwatch.GetTimestamp();
            var timer = new DispatcherTimer(
                FrameInterval,
                DispatcherPriority.Render,
                (_, _) =>
                {
                    var now = Stopwatch.GetTimestamp();
                    state.Apply(_tracker.GetSnapshot(), Stopwatch.GetElapsedTime(lastFrame, now));
                    lastFrame = now;
                },
                dispatcher);
            timer.Stop();
            window.ContentRendered += (_, _) => LogFirstFrame();
            window.Closed += (_, _) =>
            {
                timer.Stop();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            };

            bool closeRequested;
            lock (_gate)
            {
                _dispatcher = dispatcher;
                _window = window;
                _state = state;
                closeRequested = _closeRequested;
            }

            if (closeRequested)
            {
                // 主线程在启动页还没显示时就已经完成（或失败），直接退出，不再闪现。
                return;
            }

            state.Apply(_tracker.GetSnapshot(), TimeSpan.Zero);
            window.Show();
            timer.Start();
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError("StartupSplash", $"splash thread failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
        }
        finally
        {
            lock (_gate)
            {
                _dispatcher = null;
                _window = null;
                _state = null;
            }

            _closed.TrySetResult();
        }
    }

    private void CloseOnSplashThread(bool fade)
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        if (fade && window.IsVisible)
        {
            // 先把进度直接落到 100%，再淡出，避免淡出时还停在九十几。
            _state?.Apply(_tracker.GetSnapshot(), TimeSpan.FromSeconds(1));
            window.FadeOutAndClose();
            return;
        }

        window.Close();
    }

    private void ResumeAfterDialog()
    {
        if (Interlocked.Decrement(ref _suspendCount) > 0)
        {
            return;
        }

        InvokeOnSplashThread(window =>
        {
            bool closeRequested;
            lock (_gate)
            {
                closeRequested = _closeRequested;
            }

            if (!closeRequested)
            {
                window.Show();
            }
        });
    }

    private void InvokeOnSplashThread(Action<StartupSplashWindow> action)
    {
        Dispatcher? dispatcher;
        lock (_gate)
        {
            dispatcher = _dispatcher;
        }

        if (dispatcher is null)
        {
            return;
        }

        try
        {
            dispatcher.Invoke(
                () =>
                {
                    if (_window is { } window)
                    {
                        action(window);
                    }
                },
                DispatcherPriority.Send,
                CancellationToken.None,
                CrossThreadCallTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            ConsoleLog.WriteError("StartupSplash", $"cross-thread call failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
        }
    }

    private static void LogFirstFrame()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var sinceProcessStart = DateTime.Now - process.StartTime;
            ConsoleLog.Write("Startup", $"splash first frame sinceProcessStartMs={sinceProcessStart.TotalMilliseconds:0}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 进程启动时间只用于诊断，读取失败不影响启动页。
        }
    }

    private sealed class Suspension(StartupSplashHost host) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                host.ResumeAfterDialog();
            }
        }
    }
}
