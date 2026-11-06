using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Views.Controls;
using Hbpos.Contracts.AppUpdates;

namespace Hbpos.Client.Tests;

// 中文注释：底部「最新版本」按钮的真实界面：空闲时一直呼吸，交易中停下，检查期间换成进度。
[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class FooterUpdateButtonRuntimeTests(PaymentViewRuntimeStaTestHost host)
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task Idle_button_shows_the_target_version_and_keeps_pulsing(string culture) =>
        host.RunAsync(_ =>
        {
            ConfigureCulture(culture);
            var state = CreateState();
            var control = new FooterUpdateButton { DataContext = state };
            Realize(control);

            var button = Find<Button>(control, "FooterLatestVersionButton");
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.Same(state.UpdateFromFooterCommand, button.Command);
            Assert.Contains("1.0.108", ButtonText(button));

            // 中文注释：呼吸动画是叠加层的透明度动画，空闲时应处于动画状态。
            Assert.True(Overlay(button).HasAnimatedProperties, "空闲时应一直播放呼吸动画");
            return Task.CompletedTask;
        });

    [Fact]
    public Task Pulse_stops_during_a_sale_and_resumes_when_the_sale_ends() =>
        host.RunAsync(async _ =>
        {
            ConfigureCulture("en-US");
            var state = CreateState();
            var control = new FooterUpdateButton { DataContext = state };
            Realize(control);
            var overlay = Overlay(Find<Button>(control, "FooterLatestVersionButton"));

            // 中文注释：呼吸动画改的是叠加层透明度：播放时会离开 0，停下后回到 0 且保持不动。
            await Task.Delay(350);
            Assert.True(overlay.Opacity > 0, "空闲时应一直播放呼吸动画");

            state.IsTransactionInProgress = true;
            PaymentViewRuntimeStaTestHost.Realize(control, 600, 60);
            await Task.Delay(350);
            Assert.Equal(0, overlay.Opacity);

            state.IsTransactionInProgress = false;
            PaymentViewRuntimeStaTestHost.Realize(control, 600, 60);
            await Task.Delay(350);
            Assert.True(overlay.Opacity > 0, "交易结束后应恢复呼吸动画");
        });

    [Fact]
    public Task Button_is_replaced_by_progress_while_the_command_runs() =>
        host.RunAsync(async _ =>
        {
            ConfigureCulture("en-US");
            var state = CreateState();
            var release = new TaskCompletionSource<AppUpdateCoordinatorResult>();
            state.ConfigureFooterRecheck(() => release.Task);
            var control = new FooterUpdateButton { DataContext = state };
            Realize(control);
            var button = Find<Button>(control, "FooterLatestVersionButton");
            var busy = Find<Border>(control, "FooterLatestVersionBusy");
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.Equal(Visibility.Collapsed, busy.Visibility);

            var click = state.UpdateFromFooterCommand.ExecuteAsync(null);
            PaymentViewRuntimeStaTestHost.Realize(control, 600, 60);
            Assert.Equal(Visibility.Collapsed, button.Visibility);
            Assert.Equal(Visibility.Visible, busy.Visibility);

            release.SetResult(AppUpdateCoordinatorResult.NoUpdate());
            await click;
            PaymentViewRuntimeStaTestHost.Realize(control, 600, 60);
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.Equal(Visibility.Collapsed, busy.Visibility);
        });

    private static AppUpdateState CreateState()
    {
        var state = new AppUpdateState();
        state.InitializeCurrentVersion("1.0.102");
        state.ApplyVersionCheck(new AppUpdateCheckResponse
        {
            UpdateAvailable = true,
            ForceUpdate = false,
            CurrentVersion = "1.0.102",
            TargetVersion = "1.0.108"
        });
        state.ConfigureFooterRecheck(() => Task.FromResult(AppUpdateCoordinatorResult.NoUpdate()));
        return state;
    }

    private static void Realize(FrameworkElement control) =>
        PaymentViewRuntimeStaTestHost.Realize(control, 600, 60);

    private static void ConfigureCulture(string culture)
    {
        var localization = new LocalizationService();
        localization.SetCulture(culture);
        LocalizationResourceProvider.Instance.Configure(localization);
    }

    private static Border Overlay(Button button)
    {
        button.ApplyTemplate();
        return (Border)button.Template.FindName("PulseOverlay", button);
    }

    private static string ButtonText(Button button) =>
        string.Concat(PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(button)
            .Select(text => string.Concat(text.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text))));

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<T>(root)
            .Where(element => AutomationProperties.GetAutomationId(element) == id));
}
