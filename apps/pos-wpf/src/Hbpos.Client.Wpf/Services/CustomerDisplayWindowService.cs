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

    void Prewarm(CustomerDisplayViewModel viewModel)
    {
    }

    CustomerDisplayWindowResult Open(CustomerDisplayViewModel viewModel, Window? owner);

    CustomerDisplayWindowResult Toggle(CustomerDisplayViewModel viewModel, Window? owner);

    CustomerDisplayWindowResult SetMode(CustomerDisplayWindowMode mode, CustomerDisplayViewModel viewModel, Window? owner);
}

public sealed class CustomerDisplayWindowService : ICustomerDisplayWindowService
{
    public const string OpenedStatusKey = OpenedFullscreenStatusKey;
    public const string OpenedNormalStatusKey = "customerDisplay.window.openedNormal";
    public const string OpenedFullscreenStatusKey = "customerDisplay.window.openedFullscreen";
    public const string ClosedStatusKey = "customerDisplay.window.closed";
    public const string NoSecondDisplayStatusKey = "customerDisplay.window.noSecondDisplay";

    private readonly IDisplayTopologyService _displayTopology;
    private CustomerDisplayWindow? _window;
    private CustomerDisplayWindowMode _mode = CustomerDisplayWindowMode.Closed;

    public CustomerDisplayWindowService(IDisplayTopologyService displayTopology)
    {
        _displayTopology = displayTopology;
    }

    public bool IsOpen => _window?.IsVisible == true && _mode != CustomerDisplayWindowMode.Closed;

    public CustomerDisplayWindowMode Mode => _mode;

    public event EventHandler? Closed;

    public event EventHandler? FullscreenRequested;

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

        if (plan.CenterAfterPlacement)
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
            _window = null;
        }

        _mode = CustomerDisplayWindowMode.Closed;
        Closed?.Invoke(this, EventArgs.Empty);
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

    // 只有带标题栏的普通模式能双击；全屏已无标题栏，关闭状态下的迟到事件直接忽略。
    internal static bool ShouldForwardFullscreenRequest(CustomerDisplayWindowMode mode) =>
        mode == CustomerDisplayWindowMode.Normal;
}
