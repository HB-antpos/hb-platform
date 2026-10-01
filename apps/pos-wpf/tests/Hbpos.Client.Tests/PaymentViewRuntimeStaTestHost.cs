using System.Diagnostics;
using System.Reflection;
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

            // 两处等待都是防挂死保险（真卡死时兜底报错，不是性能断言），统一用共享的 DefaultTimeout。
            // 进程内首次创建 Application 并合并 MaterialDesign 等字典属于 WPF 冷启动，CI 实测耗时随 xUnit 随机的集合顺序波动：
            // 排在其它串行集合之后不超过 3.2 秒，紧接 SQLite 并行阶段结束时约 10~11.8 秒，原来的 10 秒会偶发整组超时。
            _dispatcher = await _dispatcherReady.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            var operation = _dispatcher.InvokeAsync(
                static () => CreateTestApplication(),
                DispatcherPriority.Normal);
            _application = await operation.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
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
        // 以下等待同属防挂死兜底（关闭请求已发出，只等 Dispatcher 线程执行完），与初始化共用 DefaultTimeout：
        // 初始化超时后 Dispatcher 仍在执行 CreateTestApplication，要等它返回才会处理关闭请求。
        var dispatcher = _dispatcher;
        var thread = _thread;
        if (thread is null)
        {
            return;
        }

        if (dispatcher is null)
        {
            if (!thread.Join(AsyncTestWaitSupport.DefaultTimeout))
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
                        await shutdown.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
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

            if (!thread.Join(AsyncTestWaitSupport.DefaultTimeout))
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
        await operation.Task.Unwrap().WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
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
        UseClientResourceAssembly();
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

    /// <summary>
    /// 测试进程的入口程序集是 testhost，pack://application:,,,/Resources/... 这类不带程序集名的资源
    /// （窗口图标、品牌图片）默认会去 testhost 里找而失败。生产上入口程序集就是 WPF 客户端，
    /// 这里在创建 Application 前把应用资源程序集改指向客户端，并丢弃可能已按 testhost 建好的资源包装器缓存，
    /// 使宿主内所有界面测试与生产一致，测试本身不再需要各自改写进程级状态。
    /// </summary>
    private static void UseClientResourceAssembly()
    {
        var clientAssembly = typeof(Hbpos.Client.Wpf.StartupSplashWindow).Assembly;
        var resourceAssemblyField = typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WPF Application._resourceAssembly 字段不存在，无法设置测试资源程序集。");
        resourceAssemblyField.SetValue(null, clientAssembly);
        var resourceContainer = typeof(Application).Assembly.GetType("MS.Internal.AppModel.ResourceContainer")
            ?? throw new InvalidOperationException("WPF ResourceContainer 类型不存在，无法重置应用资源缓存。");
        var wrapperField = resourceContainer.GetField("_applicationResourceManagerWrapper", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WPF ResourceContainer._applicationResourceManagerWrapper 字段不存在。");
        wrapperField.SetValue(null, null);
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
    }
}
