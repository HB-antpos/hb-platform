using System.Windows;
using System.Windows.Threading;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hbpos.Client.Wpf;

internal sealed record StartupFailurePresentation(string Title, string Message);

public partial class App : Application
{
    private const int SplashShownPercent = 10;
    private const int HostBuiltPercent = 30;
    private const int HostStartedPercent = 50;
    private const int MainWindowPreparingPercent = 65;
    private const int MainWindowInitializedPercent = 85;
    private const int StartupCompletedPercent = 100;
    private const int OfflineShutdownTimeoutSeconds = 1;
    private const int HostShutdownTimeoutSeconds = 2;
    private const int ShutdownPreparationTimeoutSeconds = 3;

    private IHost? _host;
    private SingleInstanceStartupLease? _startupLease;
    private StartupSplashWindow? _startupSplashWindow;
    private StartupProgressState? _startupProgressState;
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
                _startupProgressState = new StartupProgressState();
                ApplyStartupVersion(_startupProgressState);
                _startupProgressState.SetStage(SplashShownPercent, StartupText("startup.stage.starting"));
                _startupSplashWindow = new StartupSplashWindow(_startupProgressState);
                _startupSplashWindow.Show();
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
            }

            _host = Host.CreateDefaultBuilder(e.Args)
                .ConfigureServices(services =>
                {
                    services.AddHbposClientServices(startupOptions);
                })
                .Build();
            var applicationLogOptions = _host.Services.GetRequiredService<ApplicationLogOptions>();
            ConsoleLog.ConfigureCenterDefaults(applicationLogOptions.ToDefaults());
            ConsoleLog.ConfigureCenterSink(_host.Services.GetRequiredService<IApplicationLogSink>());
            RegisterGlobalExceptionObservers();
            _startupProgressState?.SetStage(HostBuiltPercent, StartupText("startup.stage.initializingServices"));

            await _host.StartAsync();
            RegisterShutdownSteps(_host);
            var localization = _host.Services.GetRequiredService<ILocalizationService>();
            LocalizationResourceProvider.Instance.Configure(localization);
            _startupProgressState?.LocalizeUpdateNotice(localization.T, localization.CurrentCulture);
            ButtonFeedbackRouter.Register(_host.Services.GetRequiredService<IUserFeedbackService>());
            _startupProgressState?.SetStage(HostStartedPercent, localization.T("startup.stage.startingLocalComponents"));

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            _startupProgressState?.SetStage(MainWindowPreparingPercent, localization.T("startup.stage.loadingProducts"));
            await mainWindow.InitializeForStartupAsync();
            if (!mainWindow.IsStartupBlockedByAppUpdate)
            {
                _host.Services.GetRequiredService<LocalSchemaService>().SignalReady();
            }

            mainWindow.StartupCompleted += (_, _) => ScheduleStartupGateReleaseAfterClickGuardDelay();
            _startupProgressState?.SetStage(MainWindowInitializedPercent, localization.T("startup.stage.preparingMainWindow"));
            MainWindow = mainWindow;
            FinishStartupExperience();
            mainWindow.Show();
            // 主窗口句柄会在 Show 前为扫码初始化提前创建；Show 后再刷新一次，确保任务栏按钮拿到正确图标。
            WindowsShellIdentityService.ApplyWindowIdentity(mainWindow);
            WindowsShellIdentityService.ApplyWindowIcon(mainWindow);
            if (mainWindow.IsStartupBlockedByAppUpdate)
            {
                // 已显示阻断窗口后释放启动闸门；运行中互斥仍会保护单实例。
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                ScheduleStartupGateReleaseAfterClickGuardDelay();
                base.OnStartup(e);
                return;
            }

            mainWindow.ActivateForScannerInput();
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
            mainWindow.ContinueStartupAfterShown();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            ScheduleStartupGateReleaseAfterClickGuardDelay();

            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            // 启动入口是 async void，异常不能继续抛回调度器，避免未观察异常导致进程崩溃。
            ConsoleLog.WriteError("Startup", $"startup failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
            DismissStartupExperience();
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

    private void FinishStartupExperience()
    {
        if (_startupSplashWindow is null)
        {
            return;
        }

        _startupProgressState?.SetStage(StartupCompletedPercent, StartupText("startup.stage.completed"));
        DismissStartupExperience();
    }

    private void DismissStartupExperience()
    {
        if (_startupSplashWindow is null)
        {
            return;
        }

        _startupSplashWindow.Close();
        _startupSplashWindow = null;
        _startupProgressState = null;
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

    private static void ApplyStartupVersion(StartupProgressState state)
    {
        // 启动页在宿主构建前显示，这里直接读取程序集版本，不走依赖注入。
        var version = new AppVersionProvider().CurrentVersion;
        AppLaunchVersionNotice? notice = null;
        try
        {
            notice = AppLaunchVersionTracker.CreateDefault().RecordLaunch(version);
        }
        catch (Exception ex)
        {
            // 版本提示只是辅助信息，任何意外都不能挡住收银启动。
            ConsoleLog.WriteError("Startup", $"launch version tracking failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
        }

        state.SetVersion(version, notice, StartupText, LocalizationResourceProvider.Instance.CurrentCulture);
    }

    private static string StartupText(string key) => LocalizationResourceProvider.Instance[key];
}
