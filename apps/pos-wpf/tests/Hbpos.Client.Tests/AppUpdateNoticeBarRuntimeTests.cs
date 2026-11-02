using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Controls;
using Hbpos.Client.Wpf.Views.Screens;
using Hbpos.Contracts.AppUpdates;

namespace Hbpos.Client.Tests;

[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class AppUpdateNoticeBarRuntimeTests(PaymentViewRuntimeStaTestHost host)
{
    // 中文注释：与 MainWindow 一致：标题栏 54、状态栏 42；状态栏左侧时钟、右侧同步中心按钮，提示条占中间。
    private const double TitleBarHeight = 54;
    private const double StatusBarHeight = 42;
    private const double ClockWidth = 72;
    private const double SyncCenterButtonWidth = 260;

    [Theory]
    [InlineData(1024, 768, "zh-CN")]
    [InlineData(1024, 768, "en-US")]
    [InlineData(1366, 768, "zh-CN")]
    [InlineData(1366, 768, "en-US")]
    [InlineData(1920, 1080, "zh-CN")]
    [InlineData(1920, 1080, "en-US")]
    public Task Force_update_notice_never_covers_payment_page_buttons(int width, int height, string culture) =>
        host.RunAsync(async _ =>
        {
            await using var fixture = await PaymentFlowTestFixture.CreateAsync(new ScriptedLinklyCloudApi(false));
            ConfigureCulture(culture, out var localization);
            using var vm = new PaymentViewModel(fixture.CreateSaleCart(10.03m), fixture.Workflow,
                fixture.Session, localization);
            // 中文注释：先付一部分现金，让“移除支付”按钮出现，复现 1016 店付款页上的情形。
            vm.TenderAmountText = "5";
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.SelectCashCommand).ExecuteAsync(null);
            Assert.Single(vm.PaymentTenders);

            var payment = new PaymentView { DataContext = vm };
            var state = CreatePendingForceUpdate(transactionInProgress: true);
            var notice = new AppUpdateNoticeBar { DataContext = state };
            var shell = CreateShell(payment, notice);
            PaymentViewRuntimeStaTestHost.Realize(shell, width, height);

            var noticeBounds = Bounds(shell, notice);
            Assert.True(noticeBounds.Top >= height - StatusBarHeight - 0.5 && noticeBounds.Bottom <= height + 0.5,
                $"更新提示条必须留在状态栏内: {noticeBounds}");

            var protectedButtons = new List<FrameworkElement>
            {
                Find<Button>(payment, "ConfirmPaymentButton"),
                Assert.Single(FindByCommandPath(payment, "BackToPosCommand"))
            };
            // 中文注释：离屏布局没有窗口，IsVisible 恒为 false，按整条可视树的 Visibility 判断是否显示。
            var removeTenderButtons = FindByCommandPath(payment, "DataContext.RemoveTenderCommand")
                .Where(IsShown)
                .ToArray();
            Assert.NotEmpty(removeTenderButtons);
            protectedButtons.AddRange(removeTenderButtons);
            foreach (var button in protectedButtons)
            {
                var buttonBounds = Bounds(shell, button);
                Assert.False(noticeBounds.IntersectsWith(buttonBounds),
                    $"更新提示条 {noticeBounds} 挡住了付款页按钮 {AutomationProperties.GetAutomationId(button)} {buttonBounds}");
            }

            // 中文注释：交易中安装一定会被拒绝，提示条不给安装按钮，只保留“稍后”。
            Assert.Equal(Visibility.Collapsed, FindNamed<Button>(notice, "InstallButton").Visibility);
            Assert.Equal(Visibility.Visible, FindNamed<TextBlock>(notice, "WaitingForTransactionText").Visibility);
            AssertButtonFits(notice, FindNamed<Button>(notice, "PostponeButton"));
            SaveScreenshot(shell, width, height, $"payment-{width}-{height}-{culture}");
        });

    [Theory]
    [InlineData(1024, "zh-CN")]
    [InlineData(1024, "en-US")]
    [InlineData(1366, "en-US")]
    [InlineData(1920, "en-US")]
    public Task Idle_force_update_notice_fits_install_and_later_buttons_in_status_bar(int width, string culture) =>
        host.RunAsync(_ =>
        {
            ConfigureCulture(culture, out var _);
            var state = CreatePendingForceUpdate(transactionInProgress: false);
            var notice = new AppUpdateNoticeBar { DataContext = state };
            var shell = CreateShell(new Border(), notice);
            PaymentViewRuntimeStaTestHost.Realize(shell, width, 768);

            var install = FindNamed<Button>(notice, "InstallButton");
            var postpone = FindNamed<Button>(notice, "PostponeButton");
            Assert.Equal(Visibility.Visible, install.Visibility);
            Assert.Equal(Visibility.Visible, postpone.Visibility);
            Assert.Equal(Visibility.Collapsed, FindNamed<TextBlock>(notice, "WaitingForTransactionText").Visibility);
            AssertButtonFits(notice, install);
            AssertButtonFits(notice, postpone);
            Assert.True(Bounds(shell, postpone).Right <= width - SyncCenterButtonWidth + 0.5,
                "提示条按钮不能伸到同步中心按钮下面。");
            SaveScreenshot(shell, width, 768, $"idle-{width}-{culture}");
            return Task.CompletedTask;
        });

    // 中文注释：设置 HBPOS_UPDATE_NOTICE_SCREENSHOTS 目录时保存截图，供人工核对状态栏提示条的实际样子。
    private static void SaveScreenshot(FrameworkElement view, int width, int height, string name)
    {
        var output = Environment.GetEnvironmentVariable("HBPOS_UPDATE_NOTICE_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        Directory.CreateDirectory(output);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, $"update-notice-{name}.png"));
        encoder.Save(stream);
    }

    private static AppUpdateState CreatePendingForceUpdate(bool transactionInProgress)
    {
        var state = new AppUpdateState();
        state.InitializeCurrentVersion("1.0.96");
        state.ShowForceUpdatePendingInstall(
            new AppUpdateCheckResponse
            {
                UpdateAvailable = true,
                ForceUpdate = true,
                CurrentVersion = "1.0.96",
                TargetVersion = "1.0.97"
            },
            @"C:\Temp\hbpos.exe",
            () => Task.FromResult(ProcessLaunchResult.Succeeded()),
            "appUpdate.force.backgroundReady",
            "1.0.97");
        state.SetForceUpdatePostpone(() => Task.CompletedTask, 3, TimeSpan.FromMinutes(30));
        state.IsTransactionInProgress = transactionInProgress;
        Assert.True(state.IsUpdateNoticeVisible);
        return state;
    }

    private static Grid CreateShell(FrameworkElement page, AppUpdateNoticeBar notice)
    {
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleBarHeight) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(StatusBarHeight) });
        Grid.SetRow(page, 1);
        shell.Children.Add(page);

        var footer = new Grid { Margin = new Thickness(18, 0, 12, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ClockWidth) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SyncCenterButtonWidth - 12) });
        notice.Margin = new Thickness(14, 0, 8, 0);
        Grid.SetColumn(notice, 1);
        footer.Children.Add(notice);
        Grid.SetRow(footer, 2);
        shell.Children.Add(footer);
        return shell;
    }

    private static void ConfigureCulture(string culture, out LocalizationService localization)
    {
        localization = new LocalizationService();
        localization.SetCulture(culture);
        LocalizationResourceProvider.Instance.Configure(localization);
    }

    private static void AssertButtonFits(FrameworkElement notice, Button button)
    {
        var bounds = Bounds(notice, button);
        Assert.True(bounds.Left >= -0.5 && bounds.Right <= notice.ActualWidth + 0.5 &&
            bounds.Top >= -0.5 && bounds.Bottom <= notice.ActualHeight + 0.5,
            $"{button.Name} 超出提示条: {bounds} / {notice.ActualWidth}x{notice.ActualHeight}");
        foreach (var label in PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(button))
        {
            var textBounds = Bounds(button, label);
            Assert.True(textBounds.Left >= -1 && textBounds.Right <= button.ActualWidth + 1,
                $"{label.Text} 超出按钮: {textBounds}");
        }
    }

    private static bool IsShown(DependencyObject element)
    {
        for (var current = element; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<ButtonBase> FindByCommandPath(DependencyObject root, string path) =>
        PaymentViewRuntimeStaTestHost.FindVisualDescendants<ButtonBase>(root)
            .Where(button => BindingOperations.GetBinding(button, ButtonBase.CommandProperty)?.Path?.Path == path);

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<T>(root)
            .Where(element => AutomationProperties.GetAutomationId(element) == id));

    private static T FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<T>(root)
            .Where(element => element.Name == name));

    private static Rect Bounds(FrameworkElement root, FrameworkElement element) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
}
