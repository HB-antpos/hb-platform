using System.Windows;
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
}
