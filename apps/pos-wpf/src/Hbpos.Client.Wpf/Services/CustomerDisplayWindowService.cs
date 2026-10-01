using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Windows;

namespace Hbpos.Client.Wpf.Services;

public enum CustomerDisplayWindowMode
{
    Closed,
    Normal,
    Fullscreen
}

public sealed record CustomerDisplayWindowResult(CustomerDisplayWindowMode Mode, string? StatusMessageKey)
{
    public CustomerDisplayWindowResult(bool isOpen, string? statusMessageKey)
        : this(isOpen ? CustomerDisplayWindowMode.Fullscreen : CustomerDisplayWindowMode.Closed, statusMessageKey)
    {
    }

    public bool IsOpen => Mode != CustomerDisplayWindowMode.Closed;
}

/// <summary>能被挪到指定显示器的外壳窗口（主窗口），供「主窗口与客显互换屏幕」使用。</summary>
public interface IDisplayMovableWindow
{
    /// <summary>挪到指定显示器并保持原来的最大化/普通状态，同时记住这块屏供下次启动使用。</summary>
    void MoveToDisplay(DisplayBounds display);
}

public interface ICustomerDisplayWindowService
{
    bool IsOpen { get; }

    CustomerDisplayWindowMode Mode { get; }

    event EventHandler? Closed;

    /// <summary>客显窗口在普通模式下双击标题栏，请求切到全屏。</summary>
    event EventHandler? FullscreenRequested
    {
        add { }
        remove { }
    }

    /// <summary>客显窗口上点了互换屏幕按钮（标题栏或全屏右上角），请求主窗口与客显互换屏幕。</summary>
    event EventHandler? SwapScreensRequested
    {
        add { }
        remove { }
    }

    void Prewarm(CustomerDisplayViewModel viewModel)
    {
    }

    CustomerDisplayWindowResult Open(CustomerDisplayViewModel viewModel, Window? owner);

    CustomerDisplayWindowResult Toggle(CustomerDisplayViewModel viewModel, Window? owner);

    CustomerDisplayWindowResult SetMode(CustomerDisplayWindowMode mode, CustomerDisplayViewModel viewModel, Window? owner);

    /// <summary>主窗口与客显互换所在显示器，两边各自保持原来的显示状态。</summary>
    CustomerDisplayWindowResult SwapDisplays(CustomerDisplayViewModel viewModel, Window owner) =>
        new(Mode, null);
}

public sealed class CustomerDisplayWindowService : ICustomerDisplayWindowService
{
    public const string OpenedStatusKey = OpenedFullscreenStatusKey;
    public const string OpenedNormalStatusKey = "customerDisplay.window.openedNormal";
    public const string OpenedFullscreenStatusKey = "customerDisplay.window.openedFullscreen";
    public const string ClosedStatusKey = "customerDisplay.window.closed";
    public const string NoSecondDisplayStatusKey = "customerDisplay.window.noSecondDisplay";
    public const string SwappedStatusKey = "customerDisplay.window.swapped";
    public const string SwapRequiresOpenStatusKey = "customerDisplay.window.swapRequiresOpen";
    public const string SwapUnavailableStatusKey = "customerDisplay.window.swapUnavailable";

    private readonly IDisplayTopologyService _displayTopology;
    private readonly ICustomerDisplayWindowPreferenceStore? _preferences;
    private CustomerDisplayWindow? _window;
    private CustomerDisplayWindowMode _mode = CustomerDisplayWindowMode.Closed;

    public CustomerDisplayWindowService(
        IDisplayTopologyService displayTopology,
        ICustomerDisplayWindowPreferenceStore? preferences = null)
    {
        _displayTopology = displayTopology;
        _preferences = preferences;
    }

    public bool IsOpen => _window?.IsVisible == true && _mode != CustomerDisplayWindowMode.Closed;

    public CustomerDisplayWindowMode Mode => _mode;

    public event EventHandler? Closed;

    public event EventHandler? FullscreenRequested;

    public event EventHandler? SwapScreensRequested;

    internal sealed record CustomerDisplayLayoutPlan(
        bool TitleBarVisibleDuringPlacement,
        bool CenterAfterPlacement,
        bool UseFullDisplayBoundsForPlacement,
        WindowState FinalWindowState,
        bool TitleBarVisibleAfterStateChange,
        bool Topmost);

    public void Prewarm(CustomerDisplayViewModel viewModel)
    {
        var stopwatch = Stopwatch.StartNew();
        var hadWindow = _window is not null;
        ConsoleLog.Write("CustomerDisplay", $"window prewarm start hadWindow={hadWindow} mode={_mode}");
        try
        {
            EnsureWindow(viewModel, owner: null);
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"window prewarm completed created={!hadWindow && _window is not null} visible={_window?.IsVisible == true} mode={_mode} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"window prewarm failed hadWindow={hadWindow} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    public CustomerDisplayWindowResult Open(CustomerDisplayViewModel viewModel, Window? owner)
    {
        return SetMode(CustomerDisplayWindowMode.Fullscreen, viewModel, owner);
    }

    public CustomerDisplayWindowResult Toggle(CustomerDisplayViewModel viewModel, Window? owner)
    {
        return SetMode(IsOpen ? CustomerDisplayWindowMode.Closed : CustomerDisplayWindowMode.Fullscreen, viewModel, owner);
    }

    public CustomerDisplayWindowResult SetMode(CustomerDisplayWindowMode mode, CustomerDisplayViewModel viewModel, Window? owner)
    {
        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"window set-mode start requestedMode={mode} currentMode={_mode} ownerPresent={owner is not null} windowExists={_window is not null}");

        if (mode == CustomerDisplayWindowMode.Closed)
        {
            CloseWindow();
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"window set-mode completed requestedMode={mode} resultMode={CustomerDisplayWindowMode.Closed} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new CustomerDisplayWindowResult(CustomerDisplayWindowMode.Closed, ClosedStatusKey);
        }

        if (owner is null)
        {
            CloseWindow();
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"window set-mode blocked requestedMode={mode} reason=no-owner elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new CustomerDisplayWindowResult(CustomerDisplayWindowMode.Closed, NoSecondDisplayStatusKey);
        }

        var targetDisplay = _displayTopology.FindDisplayAwayFrom(owner);
        if (targetDisplay is null)
        {
            CloseWindow();
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"window set-mode blocked requestedMode={mode} reason=no-second-display elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new CustomerDisplayWindowResult(CustomerDisplayWindowMode.Closed, NoSecondDisplayStatusKey);
        }

        ConsoleLog.Write(
            "CustomerDisplay",
            $"window set-mode target-display requestedMode={mode} left={targetDisplay.MonitorLeft} top={targetDisplay.MonitorTop} width={targetDisplay.MonitorWidth} height={targetDisplay.MonitorHeight}");
        var window = EnsureWindow(viewModel, owner);
        ApplyMode(window, owner, targetDisplay, mode);
        _mode = mode;

        stopwatch.Stop();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"window set-mode completed requestedMode={mode} resultMode={mode} visible={window.IsVisible} state={window.WindowState} elapsedMs={stopwatch.ElapsedMilliseconds}");
        return new CustomerDisplayWindowResult(mode, GetOpenedStatusKey(mode));
    }

    public CustomerDisplayWindowResult SwapDisplays(CustomerDisplayViewModel viewModel, Window owner)
    {
        if (_window is null || !IsOpen)
        {
            ConsoleLog.Write("CustomerDisplay", $"window swap blocked reason=not-open mode={_mode}");
            return new CustomerDisplayWindowResult(_mode, SwapRequiresOpenStatusKey);
        }

        _window.DataContext = viewModel;
        return SwapDisplaysCore(
            _window,
            owner,
            _mode,
            showWindow: _window.Show,
            setTitleBarVisible: _window.SetTitleBarVisible,
            refreshContentLayout: _window.RefreshContentLayout);
    }

    internal CustomerDisplayWindowResult SwapDisplaysCore(
        Window customerWindow,
        Window owner,
        CustomerDisplayWindowMode mode,
        Action showWindow,
        Action<bool> setTitleBarVisible,
        Action refreshContentLayout)
    {
        var stopwatch = Stopwatch.StartNew();
        var ownerDisplay = _displayTopology.GetDisplayForWindow(owner);
        var customerDisplay = _displayTopology.GetDisplayForWindow(customerWindow);
        if (ownerDisplay is null
            || customerDisplay is null
            || ownerDisplay.Identity == customerDisplay.Identity
            || owner is not IDisplayMovableWindow movableOwner)
        {
            ConsoleLog.Write(
                "CustomerDisplay",
                $"window swap blocked reason=displays-unresolved ownerDisplay={ownerDisplay?.Identity.Format() ?? "none"} customerDisplay={customerDisplay?.Identity.Format() ?? "none"} ownerMovable={owner is IDisplayMovableWindow}");
            return new CustomerDisplayWindowResult(mode, SwapUnavailableStatusKey);
        }

        // 关键逻辑：先把主窗口挪到客显所在屏，再按当前模式把客显铺到主窗口原来的屏；
        // 客显目标屏显式传入，不走「找主窗口以外第一块屏」，三屏以上也只在这两块屏之间互换。
        movableOwner.MoveToDisplay(customerDisplay);
        ApplyModeCore(customerWindow, owner, ownerDisplay, mode, showWindow, setTitleBarVisible, refreshContentLayout);
        stopwatch.Stop();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"window swap completed mode={mode} mainTo={customerDisplay.Identity.Format()} customerTo={ownerDisplay.Identity.Format()} elapsedMs={stopwatch.ElapsedMilliseconds}");
        return new CustomerDisplayWindowResult(mode, SwappedStatusKey);
    }

    private CustomerDisplayWindow EnsureWindow(CustomerDisplayViewModel viewModel, Window? owner)
    {
        if (_window is not null)
        {
            if (owner is not null && _window.Owner is null && !_window.IsVisible)
            {
                _window.Owner = owner;
            }

            _window.DataContext = viewModel;
            ConsoleLog.Write(
                "CustomerDisplay",
                $"window ensure reused ownerPresent={_window.Owner is not null} visible={_window.IsVisible} mode={_mode}");
            return _window;
        }

        var stopwatch = Stopwatch.StartNew();
        _window = new CustomerDisplayWindow
        {
            DataContext = viewModel,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowState = WindowState.Normal
        };
        if (owner is not null)
        {
            _window.Owner = owner;
        }

        _displayTopology.AttachWorkAreaConstraint(_window);
        _window.Closed += OnWindowClosed;
        _window.FullscreenRequested += OnWindowFullscreenRequested;
        _window.SwapScreensRequested += OnWindowSwapScreensRequested;
        _window.MoveOrResizeCompleted += OnWindowMoveOrResizeCompleted;
        stopwatch.Stop();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"window ensure created ownerPresent={owner is not null} elapsedMs={stopwatch.ElapsedMilliseconds}");
        return _window;
    }

    private void ApplyMode(CustomerDisplayWindow window, Window owner, DisplayBounds targetDisplay, CustomerDisplayWindowMode mode)
    {
        ApplyModeCore(
            window,
            owner,
            targetDisplay,
            mode,
            showWindow: window.Show,
            setTitleBarVisible: window.SetTitleBarVisible,
            refreshContentLayout: window.RefreshContentLayout);
    }

    internal void ApplyModeCore(
        Window window,
        Window owner,
        DisplayBounds targetDisplay,
        CustomerDisplayWindowMode mode,
        Action showWindow,
        Action<bool> setTitleBarVisible,
        Action refreshContentLayout)
    {
        var stopwatch = Stopwatch.StartNew();
        var plan = GetLayoutPlan(mode);
        ConsoleLog.Write(
            "CustomerDisplay",
            $"window apply-mode start mode={mode} wasVisible={window.IsVisible} targetLeft={targetDisplay.MonitorLeft} targetTop={targetDisplay.MonitorTop} targetWidth={targetDisplay.MonitorWidth} targetHeight={targetDisplay.MonitorHeight}");
        window.WindowState = WindowState.Normal;
        setTitleBarVisible(plan.TitleBarVisibleDuringPlacement);

        if (!window.IsVisible)
        {
            var showStopwatch = Stopwatch.StartNew();
            showWindow();
            showStopwatch.Stop();
            ConsoleLog.Write("CustomerDisplay", $"window show completed mode={mode} elapsedMs={showStopwatch.ElapsedMilliseconds}");
        }

        if (plan.UseFullDisplayBoundsForPlacement)
        {
            _displayTopology.FitToDisplayBounds(window, targetDisplay);
        }
        else
        {
            _displayTopology.FitToDisplayWorkArea(window, targetDisplay);
        }

        // 普通窗口优先回到收银员上次拖放的位置大小，没有记录或已不在该显示器上时才默认居中。
        if (plan.CenterAfterPlacement && !TryApplyRememberedNormalBounds(window))
        {
            CenterNormalWindow(window);
        }

        window.WindowState = plan.FinalWindowState;
        setTitleBarVisible(plan.TitleBarVisibleAfterStateChange);
        window.Topmost = plan.Topmost;
        refreshContentLayout();
        RestoreOwnerActivation(owner);
        stopwatch.Stop();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"window apply-mode completed mode={mode} state={window.WindowState} titleBarVisible={plan.TitleBarVisibleAfterStateChange} elapsedMs={stopwatch.ElapsedMilliseconds}");
    }

    internal static CustomerDisplayLayoutPlan GetLayoutPlan(CustomerDisplayWindowMode mode)
    {
        return mode switch
        {
            CustomerDisplayWindowMode.Normal => new CustomerDisplayLayoutPlan(
                TitleBarVisibleDuringPlacement: true,
                CenterAfterPlacement: true,
                UseFullDisplayBoundsForPlacement: false,
                FinalWindowState: WindowState.Normal,
                TitleBarVisibleAfterStateChange: true,
                Topmost: false),
            // 全屏使用完整显示器边界和置顶状态，保持 Normal 避免 WPF 最大化回到工作区边界。
            CustomerDisplayWindowMode.Fullscreen => new CustomerDisplayLayoutPlan(
                TitleBarVisibleDuringPlacement: true,
                CenterAfterPlacement: false,
                UseFullDisplayBoundsForPlacement: true,
                FinalWindowState: WindowState.Normal,
                TitleBarVisibleAfterStateChange: false,
                Topmost: true),
            _ => new CustomerDisplayLayoutPlan(
                TitleBarVisibleDuringPlacement: false,
                CenterAfterPlacement: false,
                UseFullDisplayBoundsForPlacement: false,
                FinalWindowState: WindowState.Normal,
                TitleBarVisibleAfterStateChange: false,
                Topmost: false)
        };
    }

    private static void RestoreOwnerActivation(Window owner)
    {
        if (!owner.IsVisible)
        {
            return;
        }

        owner.Dispatcher.BeginInvoke(() =>
        {
            if (!owner.IsVisible)
            {
                return;
            }

            if (owner.WindowState == WindowState.Minimized)
            {
                owner.WindowState = WindowState.Normal;
            }

            var wasTopmost = owner.Topmost;
            owner.Topmost = true;
            owner.Activate();
            owner.Focus();
            owner.Topmost = wasTopmost;
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>调用前窗口已铺满目标显示器工作区，因此当前位置大小即为该工作区的 DIP 边界。</summary>
    private bool TryApplyRememberedNormalBounds(Window window)
    {
        var workArea = new CustomerDisplayNormalBounds(window.Left, window.Top, window.Width, window.Height);
        var restored = CustomerDisplayWindowPreferenceStore.ResolveRestoredBounds(
            workArea,
            _preferences?.Current.NormalBounds,
            window.MinWidth,
            window.MinHeight);
        if (restored is not { } bounds)
        {
            return false;
        }

        window.Left = bounds.Left;
        window.Top = bounds.Top;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        ConsoleLog.Write(
            "CustomerDisplay",
            $"window normal bounds restored left={bounds.Left:0} top={bounds.Top:0} width={bounds.Width:0} height={bounds.Height:0}");
        return true;
    }

    private static void CenterNormalWindow(Window window)
    {
        var fullWidth = window.Width;
        var fullHeight = window.Height;
        var width = Math.Max(window.MinWidth, fullWidth * 0.78);
        var height = Math.Max(window.MinHeight, fullHeight * 0.78);

        window.Left += Math.Max(0, (fullWidth - width) / 2);
        window.Top += Math.Max(0, (fullHeight - height) / 2);
        window.Width = Math.Min(fullWidth, width);
        window.Height = Math.Min(fullHeight, height);
    }

    private void CloseWindow()
    {
        if (_window is null)
        {
            _mode = CustomerDisplayWindowMode.Closed;
            return;
        }

        _window.Close();
    }

    private static string GetOpenedStatusKey(CustomerDisplayWindowMode mode)
    {
        return mode == CustomerDisplayWindowMode.Normal
            ? OpenedNormalStatusKey
            : OpenedFullscreenStatusKey;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            _window.FullscreenRequested -= OnWindowFullscreenRequested;
            _window.SwapScreensRequested -= OnWindowSwapScreensRequested;
            _window.MoveOrResizeCompleted -= OnWindowMoveOrResizeCompleted;
            _window = null;
        }

        // 只记住收银员点关闭按钮；程序退出、断开第二屏、重新注册设备等系统关闭不改上次设置。
        if (sender is CustomerDisplayWindow { IsClosedByUser: true })
        {
            _ = _preferences?.RememberModeAsync(CustomerDisplayWindowMode.Closed);
        }

        _mode = CustomerDisplayWindowMode.Closed;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OnWindowMoveOrResizeCompleted(object? sender, EventArgs e)
    {
        if (_mode != CustomerDisplayWindowMode.Normal || sender is not Window window)
        {
            return;
        }

        var current = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        var bounds = new CustomerDisplayNormalBounds(current.Left, current.Top, current.Width, current.Height);
        ConsoleLog.Write(
            "CustomerDisplay",
            $"window normal bounds remembered left={bounds.Left:0} top={bounds.Top:0} width={bounds.Width:0} height={bounds.Height:0}");
        _ = _preferences?.RememberNormalBoundsAsync(bounds);
    }

    private void OnWindowFullscreenRequested(object? sender, EventArgs e)
    {
        OnFullscreenRequested();
    }

    internal void OnFullscreenRequested()
    {
        ConsoleLog.Write("CustomerDisplay", $"window fullscreen requested currentMode={_mode}");
        if (!ShouldForwardFullscreenRequest(_mode))
        {
            return;
        }

        FullscreenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnWindowSwapScreensRequested(object? sender, EventArgs e)
    {
        OnSwapScreensRequested();
    }

    internal void OnSwapScreensRequested()
    {
        ConsoleLog.Write("CustomerDisplay", $"window swap-screens requested currentMode={_mode}");
        if (!ShouldForwardSwapScreensRequest(_mode))
        {
            return;
        }

        SwapScreensRequested?.Invoke(this, EventArgs.Empty);
    }

    // 窗口模式和全屏都有互换按钮；关闭状态下的迟到点击直接忽略。
    internal static bool ShouldForwardSwapScreensRequest(CustomerDisplayWindowMode mode) =>
        mode != CustomerDisplayWindowMode.Closed;

    // 只有带标题栏的普通模式能双击；全屏已无标题栏，关闭状态下的迟到事件直接忽略。
    internal static bool ShouldForwardFullscreenRequest(CustomerDisplayWindowMode mode) =>
        mode == CustomerDisplayWindowMode.Normal;
}
