using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class AdaptiveUiScaleTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName ScaleEnabledAttribute = XName.Get("AdaptiveUiScale.IsEnabled", "clr-namespace:Hbpos.Client.Wpf.Services");

    [Theory]
    [InlineData(1024d, 728d, 0.8d)]
    [InlineData(1024d, 720d, 0.8d)]
    [InlineData(1000d, 720d, 0.78d)]
    [InlineData(1080d, 720d, 0.84d)]
    [InlineData(1366d, 600d, 0.83d)]
    [InlineData(960d, 540d, 0.75d)]
    [InlineData(800d, 600d, 0.75d)]
    [InlineData(1280d, 720d, 1d)]
    [InlineData(1366d, 728d, 1d)]
    [InlineData(1920d, 1040d, 1d)]
    public void Scale_shrinks_only_below_the_1280_by_720_design_size(double width, double height, double expected)
    {
        Assert.Equal(expected, AdaptiveUiScale.Calculate(width, height), precision: 10);
    }

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(-1d, 720d)]
    [InlineData(double.NaN, 720d)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity)]
    public void Scale_ignores_unmeasured_or_unbounded_sizes(double width, double height)
    {
        Assert.Equal(1d, AdaptiveUiScale.Calculate(width, height));
    }

    [Fact]
    public void Scaled_logical_size_never_drops_below_the_design_size_within_supported_windows()
    {
        var minWidth = AdaptiveUiScale.DesignWidth * AdaptiveUiScale.MinimumScale;
        var minHeight = AdaptiveUiScale.DesignHeight * AdaptiveUiScale.MinimumScale;
        for (var width = minWidth; width <= 1400d; width += 7d)
        {
            for (var height = minHeight; height <= 900d; height += 5d)
            {
                var scale = AdaptiveUiScale.Calculate(width, height);
                Assert.True(width / scale >= AdaptiveUiScale.DesignWidth - 1e-6, $"width {width}x{height} scale {scale}");
                Assert.True(height / scale >= AdaptiveUiScale.DesignHeight - 1e-6, $"height {width}x{height} scale {scale}");
            }
        }
    }

    [Theory]
    [InlineData(960d, 540d, 1024d, 728d, 960d, 540d)]
    [InlineData(1080d, 720d, 1024d, 720d, 1024d, 720d)]
    [InlineData(800d, 520d, 1920d, 1040d, 800d, 520d)]
    public void Window_size_limits_never_exceed_the_display(
        double minWidth,
        double minHeight,
        double availableWidth,
        double availableHeight,
        double expectedMinWidth,
        double expectedMinHeight)
    {
        var limits = DisplayTopologyService.ResolveSizeLimits(minWidth, minHeight, availableWidth, availableHeight);

        Assert.Equal(expectedMinWidth, limits.MinWidth);
        Assert.Equal(expectedMinHeight, limits.MinHeight);
        Assert.Equal(availableWidth, limits.MaxWidth);
        Assert.Equal(availableHeight, limits.MaxHeight);
    }

    [Fact]
    public void Main_window_minimum_matches_the_smallest_scaled_design_and_enables_shell_scaling()
    {
        var window = LoadXaml("MainWindow.xaml").Root!;

        Assert.Equal(AdaptiveUiScale.DesignWidth * AdaptiveUiScale.MinimumScale, (double)window.Attribute("MinWidth")!);
        Assert.Equal(AdaptiveUiScale.DesignHeight * AdaptiveUiScale.MinimumScale, (double)window.Attribute("MinHeight")!);
        var shellGrid = Assert.Single(window.Elements(Presentation + "Grid"));
        Assert.Equal("True", (string?)shellGrid.Attribute(ScaleEnabledAttribute));
    }

    [Fact]
    public void Update_prompt_overlay_scales_with_the_main_window()
    {
        var window = LoadXaml("Views", "Windows", "AppUpdatePromptWindow.xaml").Root!;

        var overlay = Assert.Single(window.Elements(Presentation + "Grid"));
        Assert.Equal("ModalOverlay", (string?)overlay.Attribute(Xaml + "Name"));
        Assert.Equal("True", (string?)overlay.Attribute(ScaleEnabledAttribute));
    }

    [Fact]
    public async Task Hosted_element_follows_window_size_and_resets_when_disabled()
    {
        await RunOnStaDispatcherAsync(() =>
        {
            var content = new Grid();
            AdaptiveUiScale.SetIsEnabled(content, true);
            var window = new Window
            {
                Width = 1024d,
                Height = 728d,
                Left = -10_000d,
                Top = -10_000d,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Content = content
            };

            try
            {
                window.Show();
                PumpDispatcher();
                AssertScale(content, 0.8d);
                Assert.Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(content));

                window.Width = 960d;
                window.Height = 540d;
                PumpDispatcher();
                AssertScale(content, 0.75d);

                // CI 运行器屏幕只有 1024×768，系统会把更大的窗口压回屏幕尺寸；恢复 1:1 的计算已由纯函数用例覆盖，
                // 这里只在屏幕放得下 1366×768 时验证真实窗口会撤销缩放与灰度文字。
                if (SystemParameters.VirtualScreenWidth >= 1366d && SystemParameters.VirtualScreenHeight >= 768d)
                {
                    window.Width = 1366d;
                    window.Height = 768d;
                    PumpDispatcher();
                    Assert.True(content.LayoutTransform.Value.IsIdentity);
                    Assert.Equal(DependencyProperty.UnsetValue, content.ReadLocalValue(TextOptions.TextRenderingModeProperty));
                }

                AdaptiveUiScale.SetIsEnabled(content, false);
                PumpDispatcher();
                Assert.True(content.LayoutTransform.Value.IsIdentity);
                Assert.Equal(DependencyProperty.UnsetValue, content.ReadLocalValue(TextOptions.TextRenderingModeProperty));

                // 关闭后不再跟随窗口尺寸。
                window.Width = 1024d;
                window.Height = 728d;
                PumpDispatcher();
                Assert.True(content.LayoutTransform.Value.IsIdentity);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void AssertScale(FrameworkElement element, double expected)
    {
        var transform = Assert.IsType<ScaleTransform>(element.LayoutTransform);
        Assert.Equal(expected, transform.ScaleX, precision: 10);
        Assert.Equal(expected, transform.ScaleY, precision: 10);
    }

    private static XDocument LoadXaml(params string[] relativePath)
    {
        return XDocument.Load(Path.Combine(
            [FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", .. relativePath]));
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

    private static async Task RunOnStaDispatcherAsync(Action action)
    {
        var dispatcherReady = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcherReady.TrySetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                dispatcherReady.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Hbpos.Client.Tests.AdaptiveUiScaleDispatcher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var dispatcher = await dispatcherReady.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        try
        {
            await dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
        }
        finally
        {
            if (!dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            Assert.True(thread.Join(AsyncTestWaitSupport.DefaultTimeout), "WPF Dispatcher thread did not shut down.");
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, "hb-platform.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to find repository root.");
    }
}
