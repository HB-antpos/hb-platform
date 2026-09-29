using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Xml.Linq;
using Hbpos.Client.Wpf.Views.Controls;

namespace Hbpos.Client.Tests;

[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class TitleBarLayoutPanelTests
{
    [Theory]
    // 1366 宽：标题放得下，保持整栏居中。
    [InlineData(1342d, 102d, 750d, 84d, 629d, 84d)]
    // 1024×768 缩放后逻辑宽 1280：英文长标题居中会压到右侧控件，向左平移。
    [InlineData(1256d, 102d, 692d, 171d, 513d, 171d)]
    // 两侧之间放不下：贴左侧控件并压缩宽度。
    [InlineData(400d, 100d, 200d, 171d, 108d, 84d)]
    // 两侧已经重叠：标题宽度为 0。
    [InlineData(300d, 200d, 150d, 84d, 208d, 0d)]
    public void Title_stays_centered_until_it_would_touch_a_side_group(
        double totalWidth,
        double leftEdge,
        double rightEdge,
        double desiredWidth,
        double expectedX,
        double expectedWidth)
    {
        var (x, width) = TitleBarLayoutPanel.PlaceCenter(totalWidth, leftEdge, rightEdge, desiredWidth, spacing: 8d);

        Assert.Equal(expectedX, x, precision: 6);
        Assert.Equal(expectedWidth, width, precision: 6);
    }

    [Fact]
    public async Task Panel_arranges_side_groups_at_the_edges_and_shifts_a_long_title_away_from_the_right_group()
    {
        await RunOnStaDispatcherAsync(() =>
        {
            var left = new Border { Width = 102d, Height = 32d, HorizontalAlignment = HorizontalAlignment.Left };
            var title = new Border { Width = 171d, Height = 28d, HorizontalAlignment = HorizontalAlignment.Center };
            var right = new Border { Width = 564d, Height = 34d, HorizontalAlignment = HorizontalAlignment.Right };
            var panel = new TitleBarLayoutPanel { Children = { left, title, right } };

            panel.Measure(new Size(1256d, 54d));
            panel.Arrange(new Rect(0d, 0d, 1256d, 54d));

            Assert.Equal(0d, LayoutInformation.GetLayoutSlot(left).X);
            Assert.Equal(692d, LayoutInformation.GetLayoutSlot(right).X);
            var titleSlot = LayoutInformation.GetLayoutSlot(title);
            Assert.Equal(513d, titleSlot.X, precision: 6);
            Assert.True(titleSlot.Right <= LayoutInformation.GetLayoutSlot(right).X - panel.Spacing);
        });
    }

    [Fact]
    public void Main_window_title_bar_uses_the_layout_panel_and_trims_long_titles()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace controls = "clr-namespace:Hbpos.Client.Wpf.Views.Controls";

        var titleBar = Assert.Single(document.Descendants(controls + "TitleBarLayoutPanel"));
        var title = Assert.Single(titleBar.Elements(presentation + "TextBlock"));
        Assert.Equal("{Binding ActivePageTitleText}", (string?)title.Attribute("Text"));
        Assert.Equal("CharacterEllipsis", (string?)title.Attribute("TextTrimming"));
        Assert.Equal(
            ["Left", "Center", "Right"],
            titleBar.Elements().Select(element => (string?)element.Attribute("HorizontalAlignment")));
    }

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
            Name = "Hbpos.Client.Tests.TitleBarLayoutPanelDispatcher",
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
