using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace Hbpos.Client.Wpf.Services;

public sealed record DisplayBounds(
    IntPtr Handle,
    int MonitorLeft,
    int MonitorTop,
    int MonitorWidth,
    int MonitorHeight,
    int WorkAreaLeft,
    int WorkAreaTop,
    int WorkAreaWidth,
    int WorkAreaHeight)
{
    public MonitorIdentity Identity => new(MonitorLeft, MonitorTop, MonitorWidth, MonitorHeight);
}

internal readonly record struct WindowSizeLimits(
    double MinWidth,
    double MinHeight,
    double MaxWidth,
    double MaxHeight);

public interface IDisplayTopologyService
{
    IReadOnlyList<DisplayBounds> GetDisplays();

    DisplayBounds? FindDisplayAwayFrom(Window owner);

    void AttachWorkAreaConstraint(Window window);

    void FitToDisplayWorkArea(Window window, DisplayBounds display);

    void FitToDisplayBounds(Window window, DisplayBounds display);

    /// <summary>窗口当前所在的显示器；窗口句柄未创建时返回 null。</summary>
    DisplayBounds? GetDisplayForWindow(Window window) => null;
}

public sealed class DisplayTopologyService : IDisplayTopologyService
{
    private const uint MonitorDefaultToNearest = 2;
    private const int WmGetMinMaxInfo = 0x0024;

    // 按整块显示器放置的窗口（客显全屏）：拖拽/定位上限放宽到显示器边界，否则会被截到工作区、露出任务栏。
    private static readonly DependencyProperty UsesFullMonitorBoundsProperty = DependencyProperty.RegisterAttached(
        "UsesFullMonitorBounds",
        typeof(bool),
        typeof(DisplayTopologyService),
        new PropertyMetadata(false));

    public IReadOnlyList<DisplayBounds> GetDisplays()
    {
        return EnumerateDisplays();
    }

    public DisplayBounds? FindDisplayAwayFrom(Window owner)
    {
        var ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
        var ownerMonitor = MonitorFromWindow(ownerHandle, MonitorDefaultToNearest);

        return EnumerateDisplays()
            .FirstOrDefault(display => display.Handle != ownerMonitor);
    }

    public void AttachWorkAreaConstraint(Window window)
    {
        // 关键逻辑：句柄已建好就直接挂钩子，不能再订阅 SourceInitialized。
        // 主窗口为扫码初始化在 Show 之前 EnsureHandle，WPF 此时先触发 SourceInitialized、Show 时才设置 RootVisual，
        // 本方法正是在该事件里被调用：FromVisual 为 null，若在触发中的事件里再订阅它，新订阅永远不会被调用，
        // 工作区限制（最大化尺寸、高缩放屏下调最小尺寸）因此从未生效。
        if (TryGetHwndSource(window) is { } existingSource)
        {
            AttachHook(window, existingSource);
            return;
        }

        window.SourceInitialized += (_, _) =>
        {
            if (TryGetHwndSource(window) is { } source)
            {
                AttachHook(window, source);
            }
        };
    }

    public void FitToDisplayWorkArea(Window window, DisplayBounds display)
    {
        window.ClearValue(UsesFullMonitorBoundsProperty);
        ApplyBounds(window, display.WorkAreaLeft, display.WorkAreaTop, display.WorkAreaWidth, display.WorkAreaHeight);
    }

    public void FitToDisplayBounds(Window window, DisplayBounds display)
    {
        // 先标记再定尺寸：定尺寸时系统会查询 WM_GETMINMAXINFO。
        window.SetValue(UsesFullMonitorBoundsProperty, true);
        ApplyBounds(window, display.MonitorLeft, display.MonitorTop, display.MonitorWidth, display.MonitorHeight);
    }

    internal static bool UsesFullMonitorBounds(Window window) => (bool)window.GetValue(UsesFullMonitorBoundsProperty);

    public DisplayBounds? GetDisplayForWindow(Window window) => FindDisplayForWindow(window);

    /// <summary>把设备像素矩形换成该窗口的 WPF 逻辑单位（DIP）矩形。</summary>
    internal static System.Windows.Rect ToDipRect(Window window, int left, int top, int width, int height)
    {
        // 类内有同名的 Win32 RECT 结构体，这里显式用 WPF 的 Rect。
        var topLeft = FromDevice(window, left, top);
        var bottomRight = FromDevice(window, left + width, top + height);
        return new System.Windows.Rect(topLeft, bottomRight);
    }

    /// <summary>
    /// 按指定显示器的工作区重设窗口尺寸上下限：窗口挪到另一块屏时用，
    /// 否则上限仍停留在原屏（窗口创建时只按当时所在的屏设过一次）。
    /// </summary>
    internal static void ApplyWorkAreaLimit(Window window, DisplayBounds display)
    {
        var workArea = ToDipRect(window, display.WorkAreaLeft, display.WorkAreaTop, display.WorkAreaWidth, display.WorkAreaHeight);
        var limits = ResolveSizeLimits(window.MinWidth, window.MinHeight, workArea.Width, workArea.Height);
        ApplySizeLimits(window, limits);
        if (window.Width > limits.MaxWidth)
        {
            window.Width = limits.MaxWidth;
        }

        if (window.Height > limits.MaxHeight)
        {
            window.Height = limits.MaxHeight;
        }
    }

    /// <summary>取窗口当前所在显示器的整屏与工作区（设备像素）；窗口句柄未创建或查询失败时返回 null。</summary>
    internal static DisplayBounds? FindDisplayForWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return null;
        }

        var monitorArea = monitorInfo.Monitor;
        var workArea = monitorInfo.WorkArea;
        return new DisplayBounds(
            monitor,
            monitorArea.Left,
            monitorArea.Top,
            monitorArea.Right - monitorArea.Left,
            monitorArea.Bottom - monitorArea.Top,
            workArea.Left,
            workArea.Top,
            workArea.Right - workArea.Left,
            workArea.Bottom - workArea.Top);
    }

    internal static (int Width, int Height) ResolveMaxTrackSize(
        int monitorWidth,
        int monitorHeight,
        int workAreaWidth,
        int workAreaHeight,
        bool usesFullMonitorBounds)
    {
        // 最大化尺寸始终是工作区；只有整屏放置的窗口允许拖拽/定位到显示器边界（盖住任务栏）。
        return usesFullMonitorBounds
            ? (monitorWidth, monitorHeight)
            : (workAreaWidth, workAreaHeight);
    }

    private static IReadOnlyList<DisplayBounds> EnumerateDisplays()
    {
        var displays = new List<DisplayBounds>();
        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (monitor, _, _, _) =>
            {
                var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    var workArea = monitorInfo.WorkArea;
                    var monitorArea = monitorInfo.Monitor;
                    displays.Add(new DisplayBounds(
                        monitor,
                        monitorArea.Left,
                        monitorArea.Top,
                        monitorArea.Right - monitorArea.Left,
                        monitorArea.Bottom - monitorArea.Top,
                        workArea.Left,
                        workArea.Top,
                        workArea.Right - workArea.Left,
                        workArea.Bottom - workArea.Top));
                }

                return true;
            },
            IntPtr.Zero);

        return displays;
    }

    private static void AttachHook(Window window, HwndSource source)
    {
        source.AddHook(WindowMessageHook);
        ApplyCurrentWorkAreaLimit(window);
    }

    private static IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitorArea = monitorInfo.Monitor;
        var workArea = monitorInfo.WorkArea;

        minMaxInfo.MaxPosition.X = workArea.Left - monitorArea.Left;
        minMaxInfo.MaxPosition.Y = workArea.Top - monitorArea.Top;
        minMaxInfo.MaxSize.X = workArea.Right - workArea.Left;
        minMaxInfo.MaxSize.Y = workArea.Bottom - workArea.Top;
        var usesFullMonitorBounds = HwndSource.FromHwnd(hwnd)?.RootVisual is Window window
            && UsesFullMonitorBounds(window);
        var maxTrackSize = ResolveMaxTrackSize(
            monitorArea.Right - monitorArea.Left,
            monitorArea.Bottom - monitorArea.Top,
            minMaxInfo.MaxSize.X,
            minMaxInfo.MaxSize.Y,
            usesFullMonitorBounds);
        minMaxInfo.MaxTrackSize.X = maxTrackSize.Width;
        minMaxInfo.MaxTrackSize.Y = maxTrackSize.Height;

        Marshal.StructureToPtr(minMaxInfo, lParam, false);
        // 关键逻辑：不标记 handled，让 Window 自带的 WM_GETMINMAXINFO 处理接着执行（后挂的钩子先执行）。
        // 它会在本钩子给出的上限内再套用 MinWidth/MinHeight/MaxWidth/MaxHeight 并记下系统限制供布局使用；
        // 若在这里截断，拖动窗口边框就不再受最小尺寸约束，窗口可被拖到比内容小、内容被裁。
        return IntPtr.Zero;
    }

    private static void ApplyCurrentWorkAreaLimit(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var workArea = monitorInfo.WorkArea;
        var topLeft = FromDevice(window, workArea.Left, workArea.Top);
        var bottomRight = FromDevice(window, workArea.Right, workArea.Bottom);
        var limits = ResolveSizeLimits(
            window.MinWidth,
            window.MinHeight,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);

        ApplySizeLimits(window, limits);
        if (window.Width > limits.MaxWidth)
        {
            window.Width = limits.MaxWidth;
        }

        if (window.Height > limits.MaxHeight)
        {
            window.Height = limits.MaxHeight;
        }
    }

    internal static WindowSizeLimits ResolveSizeLimits(
        double minWidth,
        double minHeight,
        double availableWidth,
        double availableHeight)
    {
        // 关键逻辑：屏幕优先。最小尺寸超过所在屏幕时下调最小尺寸，而不是把窗口撑出屏幕（如 1024×768 屏）。
        return new WindowSizeLimits(
            Math.Min(minWidth, availableWidth),
            Math.Min(minHeight, availableHeight),
            availableWidth,
            availableHeight);
    }

    private static void ApplySizeLimits(Window window, WindowSizeLimits limits)
    {
        window.MinWidth = limits.MinWidth;
        window.MinHeight = limits.MinHeight;
        window.MaxWidth = limits.MaxWidth;
        window.MaxHeight = limits.MaxHeight;
    }

    private static Point FromDevice(Window source, int x, int y)
    {
        // 句柄已建好但尚未 Show 时 FromVisual 为 null，按句柄取 HwndSource 才能拿到正确的 DPI 换算；
        // 否则会把设备像素当逻辑单位，125%/150% 缩放下工作区被算大，最小尺寸不会下调。
        var transform = TryGetHwndSource(source)?.CompositionTarget?.TransformFromDevice;
        return transform?.Transform(new Point(x, y)) ?? new Point(x, y);
    }

    /// <summary>
    /// 取窗口的 HwndSource：优先 FromVisual；句柄已由 EnsureHandle 建好、RootVisual 还没设置时按句柄取。
    /// 句柄未创建时返回 null。
    /// </summary>
    private static HwndSource? TryGetHwndSource(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            return source;
        }

        var handle = new WindowInteropHelper(window).Handle;
        return handle == IntPtr.Zero ? null : HwndSource.FromHwnd(handle);
    }

    private static void ApplyBounds(Window window, int left, int top, int width, int height)
    {
        var topLeft = FromDevice(window, left, top);
        var bottomRight = FromDevice(window, left + width, top + height);
        var limits = ResolveSizeLimits(
            window.MinWidth,
            window.MinHeight,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);

        window.Left = topLeft.X;
        window.Top = topLeft.Y;
        ApplySizeLimits(window, limits);
        window.Width = limits.MaxWidth;
        window.Height = limits.MaxHeight;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointL Reserved;
        public PointL MaxSize;
        public PointL MaxPosition;
        public PointL MinTrackSize;
        public PointL MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
