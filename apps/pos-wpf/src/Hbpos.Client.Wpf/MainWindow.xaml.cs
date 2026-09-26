using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf;

public partial class MainWindow : Window
{
    private const int DisplayChangeMessageId = 0x007E;
    private const int RawInputMessageId = 0x00FF;
    private const string MainWindowModeSettingKey = "Shell:MainWindowMode";
    internal const string FullscreenWindowModeValue = "Fullscreen";
    internal const string NormalCenteredWindowModeValue = "NormalCentered";
    private const double NormalWindowWidth = 1366;
    private const double NormalWindowHeight = 768;

    internal delegate IntPtr WindowMessageProcessor(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled);

    private readonly MainViewModel _viewModel;
    private readonly AppStartupOptions _startupOptions;
    private readonly IRawScannerService _rawScannerService;
    private readonly IDisplayTopologyService _displayTopologyService;
    private readonly IUiPriorityCoordinator _uiPriorityCoordinator;
    private readonly IAppUpdateCoordinator _appUpdateCoordinator;
    private readonly ILocalAppSettingsRepository _localAppSettingsRepository;
    private readonly IAppShutdownCoordinator _appShutdownCoordinator;
    private readonly IColorThemeService? _colorThemeService;
    private readonly AppUpdateBackgroundCheckScheduler? _appUpdateBackgroundCheckScheduler;
    private HwndSource? _hwndSource;
    private Task? _startupInitializationTask;
    private Task _windowModeSaveTask = Task.CompletedTask;
    private readonly KeyboardScannerFallbackBuffer _keyboardScannerFallback = new();
    private bool _postShowStartupStarted;
    private bool _windowModeRestored;
    private bool _isApplyingWindowMode;
    private bool _isDisplayTopologyCheckPending;
    private bool _isWaitingForWindowModeSaveBeforeClose;
    private bool _isClosingAfterWindowModeSave;
    private WindowState _lastNonMinimizedWindowState = WindowState.Maximized;

    public bool IsStartupBlockedByAppUpdate { get; private set; }

    public event EventHandler? StartupCompleted;

    public MainWindow(
        MainViewModel viewModel,
        AppStartupOptions startupOptions,
        IRawScannerService rawScannerService,
        IDisplayTopologyService displayTopologyService,
        IUiPriorityCoordinator uiPriorityCoordinator,
        IAppUpdateCoordinator appUpdateCoordinator,
        ILocalAppSettingsRepository localAppSettingsRepository,
        IAppShutdownCoordinator? appShutdownCoordinator = null,
        IColorThemeService? colorThemeService = null,
        ColorThemeSwitcherViewModel? colorThemeSwitcher = null,
        AppUpdateBackgroundCheckScheduler? appUpdateBackgroundCheckScheduler = null)
    {
        _viewModel = viewModel;
        _startupOptions = startupOptions;
        _rawScannerService = rawScannerService;
        _displayTopologyService = displayTopologyService;
        _uiPriorityCoordinator = uiPriorityCoordinator;
        _appUpdateCoordinator = appUpdateCoordinator;
        _localAppSettingsRepository = localAppSettingsRepository;
        _appShutdownCoordinator = appShutdownCoordinator ?? new AppShutdownCoordinator();
        _colorThemeService = colorThemeService;
        _appUpdateBackgroundCheckScheduler = appUpdateBackgroundCheckScheduler;
#if DEBUG
        _viewModel.AppUpdate.ConfigureDebugForceUpdateDismissed(ResumeStartupAfterDebugUpdateDismissalAsync);
#endif
        DataContext = _viewModel;
        InitializeComponent();
        if (colorThemeSwitcher is null)
        {
            ColorThemeSwitcher.Visibility = Visibility.Collapsed;
        }
        else
        {
            ColorThemeSwitcher.DataContext = colorThemeSwitcher;
        }
        SourceInitialized += MainWindowSourceInitialized;
        Loaded += MainWindowLoaded;
        PreviewKeyDown += MainWindowPreviewKeyDown;
        PreviewMouseDown += MainWindowUserInput;
        PreviewMouseMove += MainWindowUserInput;
        PreviewMouseWheel += MainWindowUserInput;
        PreviewTouchDown += MainWindowUserInput;
        StateChanged += MainWindowStateChanged;
        Closing += MainWindowClosing;
        Closed += MainWindowClosed;
    }

    private void CashierLoginOverlayPasswordBoxPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            _viewModel.CashierBarcodeInput = passwordBox.Password;
        }
    }

    private void CashierLoginOverlayPasswordBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        ExecuteCashierLoginCommandFromOverlay();
    }

    private void CashierLoginOverlayKeyboardButtonClick(object sender, RoutedEventArgs e)
    {
        var key = (sender as Button)?.Tag as string;
        var nextValue = ApplyCashierBarcodeKeyboardInput(CashierLoginOverlayPasswordBox.Password, key);

        // 关键逻辑：屏幕键盘只服务登录遮罩，扫码枪仍由同一个 PasswordBox 接收输入。
        CashierLoginOverlayPasswordBox.Password = nextValue;
        _viewModel.CashierBarcodeInput = nextValue;
        CashierLoginOverlayPasswordBox.Focus();
        Keyboard.Focus(CashierLoginOverlayPasswordBox);
    }

    internal static string ApplyCashierBarcodeKeyboardInput(string current, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return current;
        }

        if (string.Equals(key, "Clear", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (string.Equals(key, "Back", StringComparison.OrdinalIgnoreCase))
        {
            return current.Length == 0
                ? string.Empty
                : current[..^1];
        }

        return key.Length == 1 && char.IsDigit(key[0])
            ? current + key
            : current;
    }

    private void CashierLoginOverlayButtonClick(object sender, RoutedEventArgs e)
    {
        ClearCashierLoginOverlayInputAfterLogin();
    }

    private void ExecuteCashierLoginCommandFromOverlay()
    {
        var command = _viewModel.LoginCashierCommand;
        if (!command.CanExecute(null))
        {
            return;
        }

        command.Execute(null);
        ClearCashierLoginOverlayInputAfterLogin();
    }

    private void ClearCashierLoginOverlayInputAfterLogin()
    {
        // 关键逻辑：PasswordBox 不能普通绑定，登录命令清空 VM 后还要同步清掉屏幕上的敏感输入。
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(CashierLoginOverlayPasswordBox.Clear));
    }

    private void CashierLoginOverlayIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        FocusCashierLoginOverlayInput();
    }

    private void CashierLoginOverlayIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is UIElement { IsVisible: true })
        {
            // 服务器热切换会暂时禁用整个窗口；解冻后必须重新夺回扫码焦点。
            FocusCashierLoginOverlayInput();
        }
    }

    private void FocusCashierLoginOverlayInput()
    {
        // 关键逻辑：遮盖打开或重新启用后把焦点交给扫码输入框，扫码枪可直接录入收银员条码。
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!CashierLoginOverlayPasswordBox.IsVisible || !CashierLoginOverlayPasswordBox.IsEnabled)
                {
                    return;
                }

                CashierLoginOverlayPasswordBox.Focus();
                Keyboard.Focus(CashierLoginOverlayPasswordBox);
            }));
    }

    private void OperationAuthorizationOverlayIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // 关键逻辑：遮罩开关都清空两路未完成输入，授权条码不得泄漏到下一页面。
        _keyboardScannerFallback.Clear();
        _rawScannerService.ClearPendingInput();
        if (e.NewValue is not true)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                OperationAuthorizationCancelButton.Focus();
                Keyboard.Focus(OperationAuthorizationCancelButton);
            }));
    }

    private async void MainWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindowLoaded;
        try
        {
            await InitializeForStartupAsync();
        }
        catch (Exception ex)
        {
            // Loaded 事件是 async void，初始化失败时记录并关闭窗口，避免异常脱离任务链。
            ConsoleLog.WriteError("Startup", $"main window initialization failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
            _viewModel.StatusMessage = ex.Message;
            Close();
        }
    }

    public Task InitializeForStartupAsync()
    {
        _startupInitializationTask ??= InitializeForStartupCoreAsync();
        return _startupInitializationTask;
    }

    private async Task InitializeForStartupCoreAsync()
    {
        await RestoreWindowModeAsync();
        if (_colorThemeService is not null)
        {
            // 在主窗口显示前应用本机保存的配色，避免先闪一下默认配色。
            await _colorThemeService.InitializeAsync();
        }

        var updateResult = await RunStartupAppUpdateCheckAsync();
        IsStartupBlockedByAppUpdate = !ShouldContinueStartupAfterAppUpdateCheck(updateResult);
        if (IsStartupBlockedByAppUpdate)
        {
            // 更新闸门未放行前不初始化 scanner 和主 VM，避免旧版本继续进入收银流程。
            return;
        }

        await CompleteStartupInitializationAsync();
    }

    private async Task CompleteStartupInitializationAsync()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        await _rawScannerService.InitializeAsync();
        _rawScannerService.Start(hwnd);
        await _viewModel.InitializeAsync(_startupOptions);
        if (!_startupOptions.PreviewMode)
        {
            // 中文注释：启动闸门放行后才开始运行期后台检查；Preview 与真实更新链完全隔离。
            _appUpdateBackgroundCheckScheduler?.Start();
        }

        StartupCompleted?.Invoke(this, EventArgs.Empty);
    }

#if DEBUG
    private async Task ResumeStartupAfterDebugUpdateDismissalAsync()
    {
        if (!IsStartupBlockedByAppUpdate)
        {
            return;
        }

        IsStartupBlockedByAppUpdate = false;
        await CompleteStartupInitializationAsync();
        ActivateForScannerInput();
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
        ContinueStartupAfterShown();
    }
#endif

    public void ContinueStartupAfterShown()
    {
        if (_postShowStartupStarted)
        {
            return;
        }

        _postShowStartupStarted = true;
        _ = ContinueStartupAfterShownCoreAsync();
    }

    public void ActivateForScannerInput()
    {
        if (WindowState == WindowState.Minimized)
        {
            ApplyWindowMode(_lastNonMinimizedWindowState, persist: false);
        }

        var wasTopmost = Topmost;
        Topmost = true;
        Activate();
        Focus();
        Topmost = wasTopmost;
    }

    private async Task ContinueStartupAfterShownCoreAsync()
    {
        try
        {
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(300);
            await _viewModel.ContinueStartupAfterShownAsync(_startupOptions, this);
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = ex.Message;
        }
    }

    private async Task<AppUpdateCoordinatorResult> RunStartupAppUpdateCheckAsync()
    {
        return await RunStartupAppUpdateCheckCoreAsync(
            _startupOptions.PreviewMode,
            () => _appUpdateCoordinator.CheckForUpdatesAtStartupAsync(),
            ReportStartupAppUpdateException,
            ReportStartupAppUpdateFailure);
    }

    private void ReportStartupAppUpdateException(Exception ex)
    {
        ConsoleLog.WriteError("Startup", $"app update startup check failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
    }

    private static void ReportStartupAppUpdateFailure(AppUpdateCoordinatorResult result)
    {
        ConsoleLog.WriteError(
            "Startup",
            $"app update startup check allowed status={result.Status} errorCode={result.ErrorCode ?? "<null>"} errorMessage={result.ErrorMessage ?? "<null>"}");
    }

    internal static async Task<AppUpdateCoordinatorResult> RunStartupAppUpdateCheckCoreAsync(
        bool previewMode,
        Func<Task<AppUpdateCoordinatorResult>> checkAsync,
        Action<Exception> reportException,
        Action<AppUpdateCoordinatorResult>? reportFailure = null)
    {
        if (previewMode)
        {
            // Preview 必须与真实更新链完全隔离，避免测试下载或启动生产安装包。
            return AppUpdateCoordinatorResult.NoUpdate();
        }

        try
        {
            var result = await checkAsync();
            if (result.Status is AppUpdateCoordinatorStatus.CheckFailed or AppUpdateCoordinatorStatus.PolicyFailed)
            {
                // 启动自动检查不可用时只记录一次并静默放行；强制更新状态仍由启动闸门阻断。
                reportFailure?.Invoke(result);
            }

            return result;
        }
        catch (Exception ex)
        {
            reportException(ex);
            return AppUpdateCoordinatorResult.CheckFailed();
        }
    }

    internal static bool ShouldContinueStartupAfterAppUpdateCheck(AppUpdateCoordinatorResult result)
    {
        return result.Status switch
        {
            AppUpdateCoordinatorStatus.NoUpdate
                or AppUpdateCoordinatorStatus.OptionalDeclined
                or AppUpdateCoordinatorStatus.OptionalReady
                or AppUpdateCoordinatorStatus.InstallFailed
                or AppUpdateCoordinatorStatus.CheckFailed
                or AppUpdateCoordinatorStatus.PolicyFailed
                // 安装包没下载成功时不显示任何更新提示，也不能卡住启动；下次启动再下载。
                or AppUpdateCoordinatorStatus.DownloadFailed
                // 安装包还没下载时先放行收银，启动后在后台下载；强更下好后空闲即阻断、交易中则等交易结束。
                or AppUpdateCoordinatorStatus.DownloadDeferred => true,
            _ => false
        };
    }

    private void MainWindowSourceInitialized(object? sender, EventArgs e)
    {
        _displayTopologyService.AttachWorkAreaConstraint(this);
        WindowsShellIdentityService.ApplyWindowIdentity(this);
        WindowsShellIdentityService.ApplyWindowIcon(this);
        _hwndSource = (HwndSource?)PresentationSource.FromVisual(this);
        _hwndSource?.AddHook(MainWindowMessageHook);
    }

    private void MainWindowClosed(object? sender, EventArgs e)
    {
        PreviewKeyDown -= MainWindowPreviewKeyDown;
        PreviewMouseDown -= MainWindowUserInput;
        PreviewMouseMove -= MainWindowUserInput;
        PreviewMouseWheel -= MainWindowUserInput;
        PreviewTouchDown -= MainWindowUserInput;
        StateChanged -= MainWindowStateChanged;
        Closing -= MainWindowClosing;
        _hwndSource?.RemoveHook(MainWindowMessageHook);
    }

    private async void MainWindowClosing(object? sender, CancelEventArgs e)
    {
        _ = _appShutdownCoordinator.GetOrStartRemainingBudget();
        if (_isClosingAfterWindowModeSave)
        {
            return;
        }

        if (_isWaitingForWindowModeSaveBeforeClose)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _isWaitingForWindowModeSaveBeforeClose = true;
        IsEnabled = false;
        _viewModel.BeginShutdown();
        _appUpdateBackgroundCheckScheduler?.Stop();
        try
        {
            _rawScannerService.Stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "Shutdown",
                $"external input stop failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }

        try
        {
            await WaitForClosePreparationAsync(
                () => _windowModeSaveTask,
                _appShutdownCoordinator);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "Shutdown",
                $"main window close preparation failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
        finally
        {
            _isWaitingForWindowModeSaveBeforeClose = false;
            _isClosingAfterWindowModeSave = true;
            Close();
        }
    }

    private IntPtr MainWindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        QueueDisplayTopologyCheck(msg);
        return ProcessRawScannerWindowMessage(
            _viewModel.AppUpdate.IsForceUpdateBlocking,
            _viewModel.ConfirmationDialog.IsOpen,
            msg,
            hwnd,
            wParam,
            lParam,
            _rawScannerService.ProcessWindowMessage,
            ref handled);
    }

    private void QueueDisplayTopologyCheck(int messageId)
    {
        if (!ShouldQueueDisplayTopologyCheck(messageId, _isDisplayTopologyCheckPending))
        {
            return;
        }

        _isDisplayTopologyCheckPending = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _isDisplayTopologyCheckPending = false;
                if (!IsVisible)
                {
                    return;
                }

                try
                {
                    // 关键逻辑：退出 Win32 消息回调后再读取拓扑，避免断屏过程中同步关闭 WPF 窗口造成重入。
                    var displayCount = _displayTopologyService.GetDisplays().Count;
                    if (!ShouldCloseCustomerDisplayAfterTopologyChange(
                            _viewModel.IsCustomerDisplayOpen,
                            displayCount))
                    {
                        return;
                    }

                    ConsoleLog.Write(
                        "CustomerDisplay",
                        $"display topology changed displayCount={displayCount} action=close reason=no-secondary-display");
                    _viewModel.SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Closed, this);
                    ActivateForScannerInput();
                }
                catch (Exception ex)
                {
                    ConsoleLog.WriteError(
                        "CustomerDisplay",
                        $"display topology reconciliation failed error={ex.GetType().Name} message={ex.Message}",
                        exception: ex);
                }
            }));
    }

    internal static bool ShouldQueueDisplayTopologyCheck(int messageId, bool isCheckPending)
    {
        return messageId == DisplayChangeMessageId && !isCheckPending;
    }

    internal static bool ShouldCloseCustomerDisplayAfterTopologyChange(
        bool isCustomerDisplayOpen,
        int displayCount)
    {
        return isCustomerDisplayOpen && displayCount < 2;
    }

    private void MainWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _uiPriorityCoordinator.NotifyUserInput();
        var isOperationAuthorizationOpen = _viewModel.OperationAuthorization?.IsPromptOpen == true;
        if (isOperationAuthorizationOpen)
        {
            if (e.Key == Key.Escape)
            {
                _keyboardScannerFallback.Clear();
                _rawScannerService.ClearPendingInput();
                _viewModel.OperationAuthorization?.Cancel();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Tab)
            {
                return;
            }
        }

        var isForceUpdateBlocking = _viewModel.AppUpdate.IsForceUpdateBlocking;
        var isConfirmationDialogOpen = _viewModel.ConfirmationDialog.IsOpen;
        if (IsKeyboardScannerFallbackBlocked(isForceUpdateBlocking, isConfirmationDialogOpen))
        {
            _keyboardScannerFallback.Clear();
            if (!isForceUpdateBlocking && isConfirmationDialogOpen && IsAltF4(e))
            {
                // 关键逻辑：普通确认不改变 Alt+F4 的窗口关闭语义，强制更新遮罩仍吞掉全部按键。
                return;
            }

            if (ShouldConsumeBlockedKeyboardInput(isForceUpdateBlocking, isConfirmationDialogOpen, e.Key))
            {
                e.Handled = true;
            }

            return;
        }

        var timestamp = DateTimeOffset.Now;
        var result = _keyboardScannerFallback.Process(e.Key, timestamp, e.ImeProcessedKey);
        if (result is null)
        {
            // 授权遮罩打开时吞掉普通按键；仅 Tab 用于焦点导航，Enter 只用于完成扫码。
            e.Handled = isOperationAuthorizationOpen;
            return;
        }

        if (_rawScannerService is IScannerInputDeduplicator deduplicator &&
            !deduplicator.TryAcceptScanDelivery(result, "keyboard-fallback", timestamp))
        {
            // 同一把 HID 扫码枪可能同时产生 Raw Input 与普通键盘事件，只允许其中一路提交。
            e.Handled = true;
            return;
        }

        if (_viewModel.TryProcessKeyboardScannerInput(result))
        {
            e.Handled = true;
        }
    }

    private void MainWindowUserInput(object? sender, InputEventArgs e)
    {
        _uiPriorityCoordinator.NotifyUserInput();
    }

    private bool IsKeyboardScannerFallbackBlocked(bool isForceUpdateBlocking, bool isConfirmationDialogOpen)
    {
        var focusedElement = Keyboard.FocusedElement;
        return ShouldBlockKeyboardScannerFallback(
            isForceUpdateBlocking,
            isConfirmationDialogOpen,
            IsTextInputElement(focusedElement),
            IsFocusedElementVisible(focusedElement));
    }

    internal static bool ShouldBlockKeyboardScannerFallback(
        bool isForceUpdateBlocking,
        bool isConfirmationDialogOpen,
        bool isTextInputFocused,
        bool isFocusedElementVisible)
    {
        return isForceUpdateBlocking || isConfirmationDialogOpen ||
            ShouldBlockKeyboardScannerFallback(isTextInputFocused, isFocusedElementVisible);
    }

    internal static bool ShouldBlockKeyboardScannerFallback(
        bool isTextInputFocused,
        bool isFocusedElementVisible)
    {
        return isTextInputFocused && isFocusedElementVisible;
    }

    internal static bool ShouldBlockRawScannerWindowMessage(
        bool isForceUpdateBlocking,
        bool isConfirmationDialogOpen,
        int messageId)
    {
        return (isForceUpdateBlocking || isConfirmationDialogOpen) && messageId == RawInputMessageId;
    }

    internal static IntPtr ProcessRawScannerWindowMessage(
        bool isForceUpdateBlocking,
        bool isConfirmationDialogOpen,
        int messageId,
        IntPtr hwnd,
        IntPtr wParam,
        IntPtr lParam,
        WindowMessageProcessor processWindowMessage,
        ref bool handled)
    {
        if (ShouldBlockRawScannerWindowMessage(isForceUpdateBlocking, isConfirmationDialogOpen, messageId))
        {
            // 关键逻辑：全局遮罩打开时吞掉 WM_INPUT，避免 raw scanner 绕过界面门禁修改订单。
            handled = true;
            return IntPtr.Zero;
        }

        return processWindowMessage(hwnd, messageId, wParam, lParam, ref handled);
    }

    internal static bool ShouldConsumeBlockedKeyboardInput(
        bool isForceUpdateBlocking,
        bool isConfirmationDialogOpen,
        Key key)
    {
        if (isForceUpdateBlocking)
        {
            return true;
        }

        return isConfirmationDialogOpen && key is not Key.Escape and not Key.Tab and not Key.Enter;
    }

    private static bool IsAltF4(KeyEventArgs e)
    {
        return e.SystemKey == Key.F4 ||
            e.Key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
    }

    private void ConfirmationDialogIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        // 关键逻辑：确认遮罩打开时同时清空键盘兜底与 Raw Input 残留，避免超时派发修改购物车。
        _keyboardScannerFallback.Clear();
        _rawScannerService.ClearPendingInput();

        // 关键逻辑：遮罩出现后默认聚焦取消按钮，Enter 默认走安全分支，Tab 仍可在两按钮间循环。
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                ConfirmationCancelButton.Focus();
                Keyboard.Focus(ConfirmationCancelButton);
            }));
    }

    private static bool IsTextInputElement(object? focusedElement)
    {
        return focusedElement is TextBoxBase or PasswordBox or ComboBox;
    }

    private static bool IsFocusedElementVisible(object? focusedElement)
    {
        return focusedElement is not UIElement uiElement ||
            uiElement.IsVisible &&
            PresentationSource.FromDependencyObject(uiElement) is not null;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void ToggleWindowState()
    {
        var targetState = _lastNonMinimizedWindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        ApplyWindowMode(targetState, persist: true);
    }

    private async Task RestoreWindowModeAsync()
    {
        var state = await LoadWindowStateAsync(
            _localAppSettingsRepository,
            ex => ConsoleLog.WriteError(
                "Startup",
                $"main window mode restore failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex));
        ApplyWindowMode(state, persist: false);
        _windowModeRestored = true;
    }

    private void MainWindowStateChanged(object? sender, EventArgs e)
    {
        if (!_windowModeRestored || _isApplyingWindowMode || WindowState == WindowState.Minimized)
        {
            return;
        }

        _lastNonMinimizedWindowState = WindowState;
        if (WindowState == WindowState.Normal)
        {
            CenterNormalWindow();
        }

        QueueWindowModePersistence(WindowState);
    }

    private void ApplyWindowMode(WindowState state, bool persist)
    {
        _isApplyingWindowMode = true;
        try
        {
            WindowState = state;
            _lastNonMinimizedWindowState = state;
            if (state == WindowState.Normal)
            {
                CenterNormalWindow();
            }
        }
        finally
        {
            _isApplyingWindowMode = false;
        }

        if (persist && _windowModeRestored)
        {
            QueueWindowModePersistence(state);
        }
    }

    private void CenterNormalWindow()
    {
        Width = Math.Max(MinWidth, Math.Min(NormalWindowWidth, SystemParameters.WorkArea.Width));
        Height = Math.Max(MinHeight, Math.Min(NormalWindowHeight, SystemParameters.WorkArea.Height));

        var position = StartupSplashWindowPlacement.CenterInWorkArea(
            SystemParameters.WorkArea,
            Width,
            Height);
        Left = position.X;
        Top = position.Y;
    }

    private void QueueWindowModePersistence(WindowState state)
    {
        var mode = GetPersistedWindowMode(state);
        if (mode is null)
        {
            return;
        }

        // 同一 UI 线程按触发顺序排队，避免快速切换时较早的异步写入覆盖最终状态。
        _windowModeSaveTask = PersistWindowModeAfterAsync(
            _windowModeSaveTask,
            _localAppSettingsRepository,
            mode,
            ex => ConsoleLog.WriteError(
                "WindowState",
                $"main window mode save failed mode={mode} error={ex.GetType().Name} message={ex.Message}",
                exception: ex));
    }

    internal static async Task<WindowState> LoadWindowStateAsync(
        ILocalAppSettingsRepository settingsRepository,
        Action<Exception> reportException)
    {
        try
        {
            var savedMode = await settingsRepository
                .GetValueAsync(MainWindowModeSettingKey)
                .ConfigureAwait(false);
            return ResolveWindowState(savedMode);
        }
        catch (Exception ex)
        {
            // 窗口偏好损坏不应阻断收银启动。
            reportException(ex);
            return WindowState.Maximized;
        }
    }

    internal static async Task PersistWindowModeAfterAsync(
        Task previousSave,
        ILocalAppSettingsRepository settingsRepository,
        string mode,
        Action<Exception> reportException)
    {
        try
        {
            await previousSave.ConfigureAwait(false);
            await settingsRepository
                .SetValueAsync(MainWindowModeSettingKey, mode)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            reportException(ex);
        }
    }

    internal static async Task WaitForPendingWindowModeSaveAsync(Func<Task> getPendingSave)
    {
        while (true)
        {
            var pendingSave = getPendingSave();
            await pendingSave;
            if (ReferenceEquals(pendingSave, getPendingSave()))
            {
                return;
            }
        }
    }

    internal static async Task WaitForClosePreparationAsync(
        Func<Task> getPendingWindowModeSave,
        IAppShutdownCoordinator shutdownCoordinator)
    {
        var windowSaveTask = WaitForPendingWindowModeSaveAsync(getPendingWindowModeSave);
        var remainingBudget = shutdownCoordinator.GetOrStartRemainingBudget();
        var windowSaveBudget = remainingBudget < TimeSpan.FromMilliseconds(250)
            ? remainingBudget
            : TimeSpan.FromMilliseconds(250);
        try
        {
            if (windowSaveBudget > TimeSpan.Zero)
            {
                await windowSaveTask.WaitAsync(windowSaveBudget);
            }
        }
        catch (TimeoutException)
        {
            _ = windowSaveTask.ContinueWith(
                static completedTask => _ = completedTask.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            ConsoleLog.WriteError(
                "Shutdown",
                $"window state did not finish within {windowSaveBudget.TotalMilliseconds:F0}ms; continuing bounded shutdown");
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError(
                "Shutdown",
                $"window close preparation failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }

        await shutdownCoordinator.PrepareAsync();
    }

    internal static WindowState ResolveWindowState(string? savedMode)
    {
        return string.Equals(savedMode, NormalCenteredWindowModeValue, StringComparison.OrdinalIgnoreCase)
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    internal static string? GetPersistedWindowMode(WindowState state)
    {
        return state switch
        {
            WindowState.Maximized => FullscreenWindowModeValue,
            WindowState.Normal => NormalCenteredWindowModeValue,
            _ => null
        };
    }
}

internal sealed class KeyboardScannerFallbackBuffer
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMilliseconds(120);
    private const int MinBarcodeLength = 3;
    private readonly System.Text.StringBuilder _buffer = new();
    private DateTimeOffset _lastInputAt = DateTimeOffset.MinValue;

    public string? Process(Key key, DateTimeOffset timestamp, Key imeProcessedKey = Key.None)
    {
        if (key == Key.ImeProcessed && imeProcessedKey != Key.None)
        {
            // 中文输入法会把扫码枪字母键标记为 ImeProcessed，必须还原物理按键后再进入扫码缓冲。
            key = imeProcessedKey;
        }

        if (key == Key.Enter)
        {
            return Complete();
        }

        if (!TryMapCharacter(key, out var character))
        {
            Clear();
            return null;
        }

        if (_buffer.Length > 0 && timestamp - _lastInputAt > ScanTimeout)
        {
            _buffer.Clear();
        }

        _buffer.Append(character);
        _lastInputAt = timestamp;
        return null;
    }

    public void Clear()
    {
        _buffer.Clear();
        _lastInputAt = DateTimeOffset.MinValue;
    }

    private string? Complete()
    {
        var barcode = _buffer.ToString();
        Clear();
        return barcode.Length >= MinBarcodeLength ? barcode : null;
    }

    private static bool TryMapCharacter(Key key, out char character)
    {
        if (key >= Key.D0 && key <= Key.D9)
        {
            character = (char)('0' + (key - Key.D0));
            return true;
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            character = (char)('0' + (key - Key.NumPad0));
            return true;
        }

        if (key >= Key.A && key <= Key.Z)
        {
            character = (char)('A' + (key - Key.A));
            return true;
        }

        character = key switch
        {
            Key.OemMinus or Key.Subtract => '-',
            Key.OemPlus or Key.Add => '+',
            Key.OemPeriod or Key.Decimal => '.',
            Key.OemComma => ',',
            Key.Space => ' ',
            _ => '\0'
        };

        return character != '\0';
    }
}
