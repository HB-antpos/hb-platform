using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Hbpos.Client.Tests;

/// <summary>
/// 为全部 WPF 运行时测试共享一个 STA Dispatcher 和一个产品资源 Application。
/// </summary>
public sealed class PaymentViewRuntimeStaTestHost : IAsyncLifetime
{
    private readonly TaskCompletionSource<Dispatcher> _dispatcherReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private Application? _application;

    public async Task InitializeAsync()
    {
        _thread = new Thread(RunDispatcher)
        {
            IsBackground = true,
            Name = "Hbpos.Client.Tests.SharedWpfDispatcher"
        };
        try
        {
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            _dispatcher = await _dispatcherReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var operation = _dispatcher.InvokeAsync(
                static () => CreateTestApplication(),
                DispatcherPriority.Normal);
            _application = await operation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // 中文注释：资源字典或 STA 启动失败时也必须回收已启动的 Dispatcher，避免后续测试永久占用线程。
            try
            {
                await StopDispatcherAsync();
            }
            catch
            {
                // 保留初始化异常作为测试失败原因；StopDispatcherAsync 已执行有限时长的退出尝试。
            }

            throw;
        }
    }

    public Task DisposeAsync() => StopDispatcherAsync();

    private async Task StopDispatcherAsync()
    {
        var dispatcher = _dispatcher;
        var thread = _thread;
        if (thread is null)
        {
            return;
        }

        if (dispatcher is null)
        {
            if (!thread.Join(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("WPF 运行时测试的共享 Dispatcher 线程未能退出。");
            }

            _thread = null;
            return;
        }

        try
        {
            Exception? shutdownException = null;
            if (!dispatcher.HasShutdownStarted)
            {
                if (_application is not null)
                {
                    try
                    {
                        var shutdown = dispatcher.InvokeAsync(
                            () =>
                            {
                                if (!dispatcher.HasShutdownStarted)
                                {
                                    _application?.Shutdown();
                                }
                            },
                            DispatcherPriority.Send);
                        await shutdown.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch (Exception ex)
                    {
                        // 中文注释：Application.Shutdown 失败时仍需请求 Dispatcher 退出，避免清理异常留下后台 STA。
                        shutdownException = ex;
                    }
                }

                if (!dispatcher.HasShutdownStarted)
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }
            }

            if (!thread.Join(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("WPF 运行时测试的共享 Dispatcher 线程未能退出。");
            }

            if (shutdownException is not null)
            {
                ExceptionDispatchInfo.Capture(shutdownException).Throw();
            }
        }
        finally
        {
            if (!thread.IsAlive)
            {
                _application = null;
                _dispatcher = null;
                _thread = null;
            }
        }
    }

    public async Task RunAsync(Func<Application, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("WPF 测试 Dispatcher 尚未初始化。");
        var application = _application ?? throw new InvalidOperationException("WPF 测试 Application 尚未初始化。");
        var operation = dispatcher.InvokeAsync(() => test(application), DispatcherPriority.Normal);
        await operation.Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));
    }

    private void RunDispatcher()
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _dispatcher = dispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            _dispatcherReady.TrySetResult(dispatcher);
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _dispatcherReady.TrySetException(ex);
        }
    }

    public static void Realize(FrameworkElement view, double width = 1366, double height = 768)
    {
        view.ApplyTemplate();
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        PumpDispatcher();
    }

    public static async Task WaitUntilAsync(
        Func<bool> condition,
        string failureMessage,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var stopwatch = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (stopwatch.Elapsed >= limit)
            {
                throw new TimeoutException(failureMessage);
            }

            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(10);
        }
    }

    public static void PumpDispatcher()
    {
        Dispatcher.CurrentDispatcher.Invoke(
            static () => { },
            DispatcherPriority.ApplicationIdle);
    }

    public static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static Application CreateTestApplication()
    {
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign3.Defaults.xaml",
                UriKind.Absolute)
        });
        // 与 App.xaml 一致：先加载默认色板，PosTheme 与各页面通过 DynamicResource 引用色板颜色。
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/Hbpos.Client.Wpf;component/Themes/Palettes/Default.xaml",
                UriKind.Absolute)
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/Hbpos.Client.Wpf;component/Themes/PosTheme.xaml",
                UriKind.Absolute)
        });
        return application;
    }
}
