using System.Windows;
using System.Windows.Threading;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using MaterialDesignColors;
using MaterialDesignThemes.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hbpos.Client.Wpf;

internal sealed record StartupFailurePresentation(string Title, string Message);

public partial class App : Application
{
    private const int OfflineShutdownTimeoutSeconds = 1;
    private const int HostShutdownTimeoutSeconds = 2;
    private const int ShutdownPreparationTimeoutSeconds = 3;

    // 主窗口首帧最多等这么久（从 Show 之前开始计时，含首次布局）；超时也关闭启动页，避免它一直盖在主窗口上。
    private static readonly TimeSpan MainWindowFirstFrameTimeout = TimeSpan.FromSeconds(5);

    // 启动失败时先等启动页关掉再弹错误框，最多等这么久。
    private static readonly TimeSpan StartupSplashCloseTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 原 App.xaml 里 BundledTheme 之后的三个合并字典，顺序必须保持：色板在 POS 主题之前，
    /// ColorThemeService 会在顶层合并字典里按键找到色板并整体替换。
    /// 这里只存字符串：pack 协议要等 WPF 初始化后才注册，类型初始化时构造 Uri 会抛"端口无效"。
    /// </summary>
    internal static readonly IReadOnlyList<string> DeferredResourceDictionarySources =
    [
        "pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign3.Defaults.xaml",
        "pack://application:,,,/Hbpos.Client.Wpf;component/Themes/Palettes/Default.xaml",
        "pack://application:,,,/Hbpos.Client.Wpf;component/Themes/PosTheme.xaml"
    ];

    private IHost? _host;
    private SingleInstanceStartupLease? _startupLease;
    private StartupSplashHost? _startupSplash;
    private StartupProgressTracker? _startupProgress;
    private StartupTimingProfile? _startupProfile;
    private bool _applicationResourcesLoaded;
    private bool _startupGateReleaseScheduled;
    private bool _globalExceptionObserversRegistered;

    protected override async void OnStartup(StartupEventArgs e)
    {
        WindowsShellIdentityService.ApplyProcessIdentity();

        var startupOptions = AppStartupOptions.FromArgs(e.Args);
        var startupGuard = new SingleInstanceStartupGuard();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            // 保留 Dispatcher 上下文：运行中 Mutex 必须由后续负责释放 lease 的 UI 线程获得。
            var startupResult = await startupGuard.TryAcquireAsync(startupOptions.PreviewMode);
            if (!startupResult.CanStart)
            {
                Shutdown();
                return;
            }

            _startupLease = startupResult.Lease;
            if (!startupOptions.PreviewMode)
            {
                BeginStartupExperience(startupOptions);
            }

            // 阶段一：服务容器、本地数据库与后台服务。启动页在独立线程上刷新，这里占用 UI 线程也不会卡住进度。
            _host = Host.CreateDefaultBuilder(e.Args)
                .ConfigureServices(services =>
                {
                    services.AddHbposClientServices(startupOptions, _startupProgress, _startupSplash);
                })
                .Build();
            var applicationLogOptions = _host.Services.GetRequiredService<ApplicationLogOptions>();
            ConsoleLog.ConfigureCenterDefaults(applicationLogOptions.ToDefaults());
            ConsoleLog.ConfigureCenterSink(_host.Services.GetRequiredService<IApplicationLogSink>());
            RegisterGlobalExceptionObservers();

            await _host.StartAsync();
            RegisterShutdownSteps(_host);
            var localization = _host.Services.GetRequiredService<ILocalizationService>();
            LocalizationResourceProvider.Instance.Configure(localization);
            ButtonFeedbackRouter.Register(_host.Services.GetRequiredService<IUserFeedbackService>());

            // 阶段二：主题资源与主窗口；主窗口内部继续上报"检查更新 / 验证设备 / 加载商品"。
            _startupProgress?.Enter(StartupPhase.Interface);
            EnsureApplicationResourcesLoaded();
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            await mainWindow.InitializeForStartupAsync();
            if (!mainWindow.IsStartupBlockedByAppUpdate)
            {
                _host.Services.GetRequiredService<LocalSchemaService>().SignalReady();
            }

            mainWindow.StartupCompleted += (_, _) => ScheduleStartupGateReleaseAfterClickGuardDelay();

            // 阶段三：先显示主窗口并等首帧画完，再让启动页淡出。启动页置顶，淡出前一直盖在上面，
            // 不会像原来那样先关启动页、再等主窗口首次布局，中间露出桌面。
            _startupProgress?.Enter(StartupPhase.Display);
            // 有启动页时必须在 Show 之前订阅首帧事件，避免错过。
            var contentRendered = _startupSplash is null ? null : WaitForContentRenderedAsync(mainWindow);
            MainWindow = mainWindow;
            mainWindow.Show();
            // 主窗口句柄会在 Show 前为扫码初始化提前创建；Show 后再刷新一次，确保任务栏按钮拿到正确图标。
            WindowsShellIdentityService.ApplyWindowIdentity(mainWindow);
            WindowsShellIdentityService.ApplyWindowIcon(mainWindow);
            if (mainWindow.IsStartupBlockedByAppUpdate)
            {
                await WaitForFirstFrameAsync(contentRendered);
                // 被更新闸门阻断时的耗时不代表正常启动，只记日志、不写入本机档案。
                FinishStartupExperience(recordTimings: false, fade: true);
                // 已显示阻断窗口后释放启动闸门；运行中互斥仍会保护单实例。
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                ScheduleStartupGateReleaseAfterClickGuardDelay();
                base.OnStartup(e);
                return;
            }

            mainWindow.ActivateForScannerInput();
            await WaitForFirstFrameAsync(contentRendered);
            FinishStartupExperience(recordTimings: true, fade: true);
            mainWindow.ContinueStartupAfterShown();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            ScheduleStartupGateReleaseAfterClickGuardDelay();

            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            // 启动入口是 async void，异常不能继续抛回调度器，避免未观察异常导致进程崩溃。
            ConsoleLog.WriteError("Startup", $"startup failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
            // 启动页置顶且在另一个线程上，必须先关掉，否则错误提示框会被它盖住。
            await DismissStartupExperienceAsync();
            if (_host is not null)
            {
                DisposeHostWithinTimeout(
                    _host,
                    TimeSpan.FromSeconds(HostShutdownTimeoutSeconds),
                    "startup-host-dispose");

                _host = null;
            }

            var presentation = CreateStartupFailurePresentation(ex, StartupText);
            if (presentation is not null && !startupOptions.PreviewMode)
            {
                try
                {
                    MessageBox.Show(
                        presentation.Message,
                        presentation.Title,
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch (Exception promptException)
                {
                    ConsoleLog.WriteError(
                        "Startup",
                        $"startup error prompt failed error={promptException.GetType().Name}",
                        exception: promptException);
                }
            }

            ResetGlobalLogging();
            _startupLease?.Dispose();
            _startupLease = null;
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var host = _host;
        IAppShutdownCoordinator? shutdownCoordinator = null;
        if (host is not null)
        {
            try
            {
                shutdownCoordinator = host.Services.GetService<IAppShutdownCoordinator>();
                _ = shutdownCoordinator?.GetOrStartRemainingBudget();
            }
            catch (Exception ex)
            {
                LogShutdownCleanupFailure("budget-start", ex);
            }
        }

        try
        {
            try
            {
                FinishStartupExperience();
            }
            catch (Exception ex)
            {
                LogShutdownCleanupFailure("startup-experience", ex);
            }

            _host = null;
            if (host is not null)
            {
                try
                {
                    _ = WaitForShutdownPreparation(
                        shutdownCoordinator,
                        shutdownCoordinator?.GetOrStartRemainingBudget() ??
                        TimeSpan.FromSeconds(ShutdownPreparationTimeoutSeconds));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    LogShutdownCleanupFailure("prepare", ex);
                }

                DisposeHostWithinTimeout(
                    host,
                    shutdownCoordinator?.GetOrStartRemainingBudget() ?? TimeSpan.Zero,
                    "host-dispose");
            }

            try
            {
                _startupLease?.Dispose();
            }
            catch (Exception ex)
            {
                LogShutdownCleanupFailure("startup-lease-dispose", ex);
            }
            finally
            {
                _startupLease = null;
            }

            // 正常关闭由协调器中的 file-log-stop 步骤收尾；仅在 Host 尚未建立或协调器不可用时执行兜底。
            if (host is null || shutdownCoordinator is null)
            {
                using var stopLogTimeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(ShutdownPreparationTimeoutSeconds));
                try
                {
                    ConsoleLog.StopFileLogAsync(stopLogTimeout.Token).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    LogShutdownCleanupFailure("file-log-stop", ex);
                }
            }
        }
        finally
        {
            ResetGlobalLogging();
            base.OnExit(e);
        }
    }

    private static void LogShutdownCleanupFailure(string stage, Exception ex)
    {
        ConsoleLog.WriteError(
            "Shutdown",
            $"shutdown cleanup failed stage={stage} error={ex.GetType().Name} message={ex.Message}",
            exception: ex);
    }

    internal static bool WaitForShutdownPreparation(
        IAppShutdownCoordinator? coordinator,
        TimeSpan timeout)
    {
        if (coordinator is null)
        {
            return false;
        }

        var remainingBudget = coordinator.GetOrStartRemainingBudget();
        var waitBudget = timeout < remainingBudget ? timeout : remainingBudget;
        if (waitBudget <= TimeSpan.Zero)
        {
            return coordinator.IsPrepared;
        }

        using var cancellation = new CancellationTokenSource(waitBudget);
        try
        {
            // 系统关机可能忽略 Window.Closing 的 Cancel；这里等待同一个幂等任务，避免与 Dispose 竞态。
            coordinator
                .PrepareAsync(cancellation.Token)
                .WaitAsync(waitBudget)
                .GetAwaiter()
                .GetResult();

            var detachedFailureBudget = coordinator.GetOrStartRemainingBudget();
            if (detachedFailureBudget > TimeSpan.Zero)
            {
                // 中文注释：单步超时不阻塞后续清理；OnExit 使用剩余共享预算观察迟到的致命异常。
                coordinator
                    .ObserveDetachedFailuresAsync(detachedFailureBudget, cancellation.Token)
                    .GetAwaiter()
                    .GetResult();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            LogShutdownCleanupFailure("prepare-wait", ex);
        }

        return coordinator.IsPrepared;
    }

    internal static bool DisposeHostWithinTimeout(
        IDisposable host,
        TimeSpan timeout,
        string stage)
    {
        if (timeout <= TimeSpan.Zero)
        {
            ConsoleLog.WriteError(
                "Shutdown",
                $"shutdown cleanup timed out stage={stage} timeoutMs={Math.Max(0, timeout.TotalMilliseconds):0}");
            return false;
        }

        var disposeTask = Task.Run(host.Dispose);
        try
        {
            disposeTask.WaitAsync(timeout).GetAwaiter().GetResult();
            return true;
        }
        catch (TimeoutException)
        {
            _ = disposeTask.ContinueWith(
                static completedTask => _ = completedTask.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            ConsoleLog.WriteError(
                "Shutdown",
                $"shutdown cleanup timed out stage={stage} timeoutMs={Math.Max(0, timeout.TotalMilliseconds):0}");
            return false;
        }
        catch (Exception ex)
        {
            LogShutdownCleanupFailure(stage, ex);
            return false;
        }
    }

    private static void RegisterShutdownSteps(IHost host)
    {
        var coordinator = host.Services.GetRequiredService<IAppShutdownCoordinator>();
        coordinator.RegisterStep(
            "runtime-offline",
            order: 100,
            TimeSpan.FromSeconds(OfflineShutdownTimeoutSeconds),
            async cancellationToken =>
            {
                var mainViewModel = host.Services.GetService<MainViewModel>();
                if (mainViewModel is not null)
                {
                    await mainViewModel.ReportOfflineForShutdownAsync(cancellationToken);
                }
            });
        coordinator.RegisterStep(
            "host-stop",
            order: 200,
            TimeSpan.FromSeconds(HostShutdownTimeoutSeconds),
            cancellationToken => host.StopAsync(cancellationToken));
        coordinator.RegisterStep(
            "file-log-stop",
            order: 300,
            TimeSpan.FromSeconds(HostShutdownTimeoutSeconds),
            cancellationToken => ConsoleLog.StopFileLogAsync(cancellationToken));
    }

    private void RegisterGlobalExceptionObservers()
    {
        if (_globalExceptionObserversRegistered)
        {
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _globalExceptionObserversRegistered = true;
    }

    private void ResetGlobalLogging()
    {
        if (_globalExceptionObserversRegistered)
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            _globalExceptionObserversRegistered = false;
        }

        ConsoleLog.ConfigureCenterSink(null);
        ConsoleLog.ConfigureCenterDefaults(ApplicationLogDefaults.Default);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _ = sender;
        // 这里只观察并记录，不设置 Handled，保留 WPF 原有崩溃语义。
        ConsoleLog.WriteError(
            "UnhandledException",
            $"dispatcher unhandled exception type={e.Exception.GetType().Name}",
            exception: e.Exception);
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _ = sender;
        if (e.ExceptionObject is Exception exception)
        {
            ConsoleLog.WriteError(
                "UnhandledException",
                $"app domain unhandled exception type={exception.GetType().Name} terminating={e.IsTerminating}",
                exception: exception);
            return;
        }

        ConsoleLog.WriteError(
            "UnhandledException",
            $"app domain unhandled non-exception object terminating={e.IsTerminating}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _ = sender;
        // 不调用 SetObserved，日志观察器不能改变 TaskScheduler 的既有异常策略。
        ConsoleLog.WriteError(
            "UnhandledException",
            "unobserved task exception",
            exception: e.Exception);
    }

    private void ScheduleStartupGateReleaseAfterClickGuardDelay()
    {
        if (_startupGateReleaseScheduled)
        {
            return;
        }

        _startupGateReleaseScheduled = true;
        _ = ReleaseStartupGateAfterClickGuardDelayAsync();
    }

    private async Task ReleaseStartupGateAfterClickGuardDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _startupLease?.ReleaseStartupGate();
    }

    /// <summary>
    /// 在服务容器建立前启动启动页：读取本机启动档案（上次各阶段耗时、界面语言、门店），
    /// 用它给进度条分配权重，并按上次的语言直接显示文案。
    /// </summary>
    private void BeginStartupExperience(AppStartupOptions startupOptions)
    {
        try
        {
            // 启动页在宿主构建前显示，这里直接读取程序集版本，不走依赖注入。
            var version = new AppVersionProvider().CurrentVersion;
            var notice = TryRecordLaunchVersion(version);
            var profile = StartupTimingProfile.Load(StartupTimingProfile.DefaultFilePath);
            LocalizationService.TryGetSupportedCulture(startupOptions.InitialCulture ?? profile.CultureName, out var culture);
            var tracker = new StartupProgressTracker(profile.GetExpectedDurations());
            tracker.Enter(StartupPhase.Services);
            var storeLabel = profile.StoreLabel;
            _startupProfile = profile;
            _startupProgress = tracker;
            _startupSplash = StartupSplashHost.Start(tracker, () =>
            {
                var state = new StartupProgressState(key => LocalizationService.Translate(key, culture), culture, storeLabel);
                state.SetVersion(version, notice);
                return state;
            });
        }
        catch (Exception ex)
        {
            // 启动页只是辅助体验，任何意外都不能挡住收银启动。
            ConsoleLog.WriteError("Startup", $"startup splash unavailable error={ex.GetType().Name} message={ex.Message}", exception: ex);
            _startupSplash = null;
        }
    }

    /// <summary>
    /// 有启动页时等主窗口第一帧真正画出（启动页随后淡出）；没有启动页（Preview）时保持原来的"Show 之后让出一次渲染"。
    /// </summary>
    private Task WaitForFirstFrameAsync(Task? contentRendered)
    {
        return contentRendered ?? Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render).Task;
    }

    /// <summary>订阅主窗口首帧事件；必须在 Show 之前调用，超时兜底防止启动页一直不关。</summary>
    private static Task WaitForContentRenderedAsync(Window window)
    {
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnContentRendered(object? sender, EventArgs args) => rendered.TrySetResult();
        window.ContentRendered += OnContentRendered;
        return WaitCoreAsync();

        async Task WaitCoreAsync()
        {
            try
            {
                await rendered.Task.WaitAsync(MainWindowFirstFrameTimeout);
            }
            catch (TimeoutException)
            {
                ConsoleLog.Write(
                    "Startup",
                    $"main window first frame wait timed out timeoutMs={MainWindowFirstFrameTimeout.TotalMilliseconds:0}");
            }
            finally
            {
                window.ContentRendered -= OnContentRendered;
            }
        }
    }

    /// <summary>
    /// 结束启动体验：进度落到 100%、输出各阶段耗时，正常启动时写入本机档案，最后关闭启动页。可重复调用。
    /// </summary>
    private void FinishStartupExperience(bool recordTimings = false, bool fade = false)
    {
        var tracker = _startupProgress;
        var splash = _startupSplash;
        _startupProgress = null;
        _startupSplash = null;
        if (tracker is not null)
        {
            tracker.Complete();
            RecordStartupTimings(tracker, recordTimings);
        }

        if (splash is not null)
        {
            _ = ObserveStartupSplashCloseAsync(splash.CloseAsync(fade));
        }
    }

    private async Task DismissStartupExperienceAsync()
    {
        var splash = _startupSplash;
        _startupSplash = null;
        _startupProgress = null;
        if (splash is null)
        {
            return;
        }

        try
        {
            await splash.CloseAsync(fade: false).WaitAsync(StartupSplashCloseTimeout);
        }
        catch (TimeoutException)
        {
            ConsoleLog.Write("Startup", "startup splash close timed out before showing startup error");
        }
    }

    private void RecordStartupTimings(StartupProgressTracker tracker, bool persist)
    {
        var measured = tracker.GetMeasuredDurations();
        var phases = string.Join(
            ' ',
            measured.OrderBy(pair => pair.Key).Select(pair =>
                $"{pair.Key.ToString().ToLowerInvariant()}Ms={pair.Value.TotalMilliseconds:0}"));
        ConsoleLog.Write(
            "Startup",
            $"startup timings totalMs={tracker.GetSnapshot().Elapsed.TotalMilliseconds:0} recorded={persist} {phases}");

        var profile = _startupProfile;
        if (!persist || profile is null || _host is null)
        {
            return;
        }

        var localization = _host.Services.GetService<ILocalizationService>();
        profile.RecordRun(measured);
        profile.RememberCulture(localization?.CurrentCulture.Name);
        // 只有设备已授权、真正进入收银（走过"加载商品"阶段）时会话里才是真实门店；
        // 停在设备注册页时会话仍是默认占位值，此时清空标签，下次启动页显示通用副标题。
        var session = tracker.HasEntered(StartupPhase.Catalog)
            ? _host.Services.GetService<MainViewModel>()?.Session
            : null;
        profile.RememberStoreLabel(StartupTimingProfile.FormatStoreLabel(session?.StoreName, session?.DeviceCode));
        SaveStartupProfileInBackground(profile);
        if (localization is not null)
        {
            // 之后在设置里切换语言也同步到档案，下次启动页直接用新语言显示。
            localization.CultureChanged += (_, _) =>
            {
                profile.RememberCulture(localization.CurrentCulture.Name);
                SaveStartupProfileInBackground(profile);
            };
        }
    }

    private static void SaveStartupProfileInBackground(StartupTimingProfile profile)
    {
        // 档案只影响下次启动的进度节奏，写文件放到线程池，不占用刚显示出来的收银界面。
        _ = Task.Run(() =>
        {
            if (!profile.TrySave())
            {
                ConsoleLog.Write("Startup", "startup profile save skipped");
            }
        });
    }

    private static async Task ObserveStartupSplashCloseAsync(Task closeTask)
    {
        try
        {
            await closeTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError("Startup", $"startup splash close failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
        }
    }

    private void EnsureApplicationResourcesLoaded()
    {
        if (_applicationResourcesLoaded)
        {
            return;
        }

        _applicationResourcesLoaded = true;
        var merged = Resources.MergedDictionaries;
        merged.Add(new BundledTheme
        {
            BaseTheme = BaseTheme.Light,
            PrimaryColor = PrimaryColor.Blue,
            SecondaryColor = SecondaryColor.Amber
        });
        foreach (var source in DeferredResourceDictionarySources)
        {
            merged.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Absolute) });
        }
    }

    internal static StartupFailurePresentation? CreateStartupFailurePresentation(
        Exception exception,
        Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(localize);

        return exception is ApiBaseAddressConfigurationException
            ? new StartupFailurePresentation(
                localize("startup.error.apiBaseAddress.title"),
                localize("startup.error.apiBaseAddress.message"))
            : null;
    }

    private static AppLaunchVersionNotice? TryRecordLaunchVersion(string version)
    {
        try
        {
            return AppLaunchVersionTracker.CreateDefault().RecordLaunch(version);
        }
        catch (Exception ex)
        {
            // 版本提示只是辅助信息，任何意外都不能挡住收银启动。
            ConsoleLog.WriteError("Startup", $"launch version tracking failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
            return null;
        }
    }

    private static string StartupText(string key) => LocalizationResourceProvider.Instance[key];
}
