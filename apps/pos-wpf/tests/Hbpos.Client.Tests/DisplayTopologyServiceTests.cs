using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class DisplayTopologyServiceTests(PaymentViewRuntimeStaTestHost host)
{
    private static readonly DisplayBounds SecondDisplay = new(
        IntPtr.Zero,
        MonitorLeft: 0,
        MonitorTop: -1440,
        MonitorWidth: 2560,
        MonitorHeight: 1440,
        WorkAreaLeft: 0,
        WorkAreaTop: -1440,
        WorkAreaWidth: 2560,
        WorkAreaHeight: 1392);

    [Theory]
    // 客显全屏：可以盖住任务栏，上限放宽到整块显示器。
    [InlineData(true, 3840, 2160)]
    // 普通窗口与最大化：仍限制在工作区，不压到任务栏。
    [InlineData(false, 3840, 2088)]
    public void Max_track_size_follows_monitor_only_for_full_monitor_windows(
        bool usesFullMonitorBounds,
        int expectedWidth,
        int expectedHeight)
    {
        var size = DisplayTopologyService.ResolveMaxTrackSize(3840, 2160, 3840, 2088, usesFullMonitorBounds);

        Assert.Equal((expectedWidth, expectedHeight), size);
    }

    [Fact]
    public Task Fitting_to_display_bounds_marks_the_window_and_work_area_fitting_clears_it()
    {
        return host.RunAsync(_ =>
        {
            var service = new DisplayTopologyService();
            var window = new Window { Width = 1024, Height = 640, MinWidth = 800, MinHeight = 520 };

            Assert.False(DisplayTopologyService.UsesFullMonitorBounds(window));

            service.FitToDisplayBounds(window, SecondDisplay);
            Assert.True(DisplayTopologyService.UsesFullMonitorBounds(window));
            Assert.Equal(1440d, window.Height);

            service.FitToDisplayWorkArea(window, SecondDisplay);
            Assert.False(DisplayTopologyService.UsesFullMonitorBounds(window));
            Assert.Equal(1392d, window.Height);

            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task Constraint_attached_inside_source_initialized_before_show_takes_effect()
    {
        return host.RunAsync(_ =>
        {
            var service = new DisplayTopologyService();
            // 最小尺寸故意大于任何屏幕：工作区限制生效时会被下调到工作区。
            var window = new Window
            {
                Width = 640,
                Height = 480,
                MinWidth = 100_000,
                MinHeight = 100_000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            // 复现主窗口的时序：Show 之前 EnsureHandle，并在 SourceInitialized 里挂工作区限制。
            window.SourceInitialized += (_, _) => service.AttachWorkAreaConstraint(window);

            try
            {
                new WindowInteropHelper(window).EnsureHandle();

                // 前提：EnsureHandle 路径下 WPF 还没设置 RootVisual，FromVisual 拿不到 HwndSource。
                Assert.Null(PresentationSource.FromVisual(window));
                Assert.False(double.IsPositiveInfinity(window.MaxWidth));
                Assert.False(double.IsPositiveInfinity(window.MaxHeight));

                var display = DisplayTopologyService.FindDisplayForWindow(window);
                Assert.NotNull(display);
                var workArea = DisplayTopologyService.ToDipRect(
                    window,
                    display.WorkAreaLeft,
                    display.WorkAreaTop,
                    display.WorkAreaWidth,
                    display.WorkAreaHeight);
                Assert.Equal(workArea.Width, window.MaxWidth, 3);
                Assert.Equal(workArea.Height, window.MaxHeight, 3);
                Assert.Equal(workArea.Width, window.MinWidth, 3);
                Assert.Equal(workArea.Height, window.MinHeight, 3);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task Min_max_info_uses_work_area_and_keeps_window_minimum_track_size()
    {
        return host.RunAsync(_ =>
        {
            var service = new DisplayTopologyService();
            var window = new Window
            {
                Width = 400,
                Height = 300,
                MinWidth = 320,
                MinHeight = 240,
                Left = 0,
                Top = 0,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None
            };
            service.AttachWorkAreaConstraint(window);

            try
            {
                window.Show();
                var handle = new WindowInteropHelper(window).Handle;
                var display = DisplayTopologyService.FindDisplayForWindow(window);
                Assert.NotNull(display);

                var info = new TestMinMaxInfo();
                SendMessage(handle, 0x0024, IntPtr.Zero, ref info);

                // 最大化尺寸与位置：显示器工作区（设备像素，位置相对显示器左上角）。
                Assert.Equal(display.WorkAreaWidth, info.MaxSize.X);
                Assert.Equal(display.WorkAreaHeight, info.MaxSize.Y);
                Assert.Equal(display.WorkAreaLeft - display.MonitorLeft, info.MaxPosition.X);
                Assert.Equal(display.WorkAreaTop - display.MonitorTop, info.MaxPosition.Y);
                Assert.InRange(info.MaxTrackSize.X, display.WorkAreaWidth - 1, display.WorkAreaWidth);
                Assert.InRange(info.MaxTrackSize.Y, display.WorkAreaHeight - 1, display.WorkAreaHeight);

                // 钩子不截断消息：Window 自带处理仍按 MinWidth/MinHeight 设置拖动下限。
                var toDevice = HwndSource.FromHwnd(handle)!.CompositionTarget!.TransformToDevice;
                var minDevice = toDevice.Transform(new Point(window.MinWidth, window.MinHeight));
                Assert.InRange(info.MinTrackSize.X, (int)Math.Floor(minDevice.X), (int)Math.Ceiling(minDevice.X));
                Assert.InRange(info.MinTrackSize.Y, (int)Math.Floor(minDevice.Y), (int)Math.Ceiling(minDevice.Y));
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref TestMinMaxInfo lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct TestPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestMinMaxInfo
    {
        public TestPoint Reserved;
        public TestPoint MaxSize;
        public TestPoint MaxPosition;
        public TestPoint MinTrackSize;
        public TestPoint MaxTrackSize;
    }
}
