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
    int WorkAreaHeight);

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
        if (PresentationSource.FromVisual(window) is HwndSource existingSource)
        {
            AttachHook(window, existingSource);
            return;
        }

        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
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
        handled = true;
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
        var transform = PresentationSource.FromVisual(source)?.CompositionTarget?.TransformFromDevice;
        return transform?.Transform(new Point(x, y)) ?? new Point(x, y);
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
