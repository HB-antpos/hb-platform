using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.Zeller;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.ViewModels.Settings;
using Hbpos.Client.Wpf.Views.Controls;
using Hbpos.Client.Wpf.Views.Screens;

namespace Hbpos.Client.Tests;

/// <summary>
/// 设置 → 支付终端 → Zeller 分页（1366×632 视口）：底栏三个按钮在视口内、正文不出现滚动条，中英文各跑一遍；
/// 同时确认整个设置视图能被真正加载（XAML 里的图标名等只有运行时才会校验）。
/// 运行时用例只能在 Windows CI 跑。
/// </summary>
[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class SettingsFirstScreenZellerTests(PaymentViewRuntimeStaTestHost host)
{
    private static readonly string[] Cultures = ["en-US", "zh-CN"];

    [Fact]
    public Task Zeller_footer_buttons_are_visible_and_body_does_not_scroll() => host.RunAsync(async _ =>
    {
        foreach (var culture in Cultures)
        {
            var (vm, view, localization) = await CreateAsync(culture);
            try
            {
                vm.IsZellerSubTabSelected = true;
                PaymentViewRuntimeStaTestHost.Realize(view, SettingsFirstScreenViewportTests.ViewportWidth, SettingsFirstScreenViewportTests.ViewportHeight);

                var page = PaymentPage(view);
                var buttons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view, requireFooter: true);
                SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);

                // 检查配对 / 配对刷卡机 / 启用 Zeller；取消配对只在配对进行中才出现。
                Assert.Equal(3, buttons.Count);
                Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.CheckZellerCommand));
                Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.PairZellerCommand));
                Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.EnableZellerCommand));

                // 状态卡和三条步骤都在正文里，文案是本地化过的（不是 [[key]]）。
                var texts = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(page).Select(text => text.Text).ToList();
                Assert.Contains(vm.ZellerStatusText, texts);
                Assert.Contains(localization.T("settings.zeller.steps.two"), texts);
                Assert.DoesNotContain(texts, text => text.Contains("[[", StringComparison.Ordinal));
            }
            finally
            {
                view.DataContext = null;
                localization.SetCulture("en-US");
            }
        }
    });

    [Fact]
    public Task Zeller_footer_shows_cancel_only_while_pairing() => host.RunAsync(async _ =>
    {
        var (vm, view, localization) = await CreateAsync("en-US");
        try
        {
            vm.IsZellerSubTabSelected = true;
            PaymentViewRuntimeStaTestHost.Realize(view, SettingsFirstScreenViewportTests.ViewportWidth, SettingsFirstScreenViewportTests.ViewportHeight);
            var page = PaymentPage(view);
            Assert.Equal(3, SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view, requireFooter: true).Count);

            vm.IsZellerPairingInProgress = true;
            PaymentViewRuntimeStaTestHost.Realize(view, SettingsFirstScreenViewportTests.ViewportWidth, SettingsFirstScreenViewportTests.ViewportHeight);

            var withCancel = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view, requireFooter: true);
            Assert.Equal(4, withCancel.Count);
            Assert.Single(withCancel, button => ReferenceEquals(button.Command, vm.CancelZellerPairingCommand));
        }
        finally
        {
            view.DataContext = null;
            localization.SetCulture("en-US");
        }
    });

    [Fact]
    public Task Other_sub_tabs_do_not_show_the_zeller_footer() => host.RunAsync(async _ =>
    {
        var (vm, view, localization) = await CreateAsync("en-US");
        try
        {
            vm.IsSquareSubTabSelected = true;
            PaymentViewRuntimeStaTestHost.Realize(view, SettingsFirstScreenViewportTests.ViewportWidth, SettingsFirstScreenViewportTests.ViewportHeight);

            var buttons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(PaymentPage(view), view, requireFooter: true);

            Assert.DoesNotContain(buttons, button => ReferenceEquals(button.Command, vm.EnableZellerCommand));
        }
        finally
        {
            view.DataContext = null;
            localization.SetCulture("en-US");
        }
    });

    private static SettingsPageHost PaymentPage(SettingsView view)
    {
        var pages = PaymentViewRuntimeStaTestHost.FindVisualDescendants<SettingsPageHost>(view).ToList();
        var page = pages[(int)SettingsCategory.PaymentTerminal];
        Assert.Equal(Visibility.Visible, page.Visibility);
        return page;
    }

    private static async Task<(SettingsViewModel Vm, SettingsView View, LocalizationService Localization)> CreateAsync(string culture)
    {
        var localization = new LocalizationService();
        localization.SetCulture(culture);
        LocalizationResourceProvider.Instance.Configure(localization);
        var setup = DispatchProxy.Create<ICardTerminalSetupService, SettingsPaymentMethodsViewRuntimeTests.UnavailableTerminalSetup>();
        var http = new HttpClient();
        var apiSettings = new ApiServerSettingsViewModel(
            new ApiServerSettingsService(http, () => "https://example.test/", _ => throw new NotSupportedException()),
            localization);
        var coordinator = new ZellerSettingsCoordinator(
            new SettingsViewModelTests.NeverAskedGateway(),
            (_, _) => Task.CompletedTask);
        var vm = new SettingsViewModel(setup, localization, apiServerSettings: apiSettings, zellerSettingsCoordinator: coordinator);
        await vm.LoadAsync();
        await ((IAsyncRelayCommand)vm.SelectPaymentTerminalCommand).ExecuteAsync(null);

        var view = new SettingsView();
        PaymentViewRuntimeStaTestHost.IsolateRadioGroups(view);
        view.DataContext = vm;
        view.Resources.MergedDictionaries.Add(new MaterialDesignThemes.Wpf.BundledTheme
        {
            BaseTheme = MaterialDesignThemes.Wpf.BaseTheme.Light,
            PrimaryColor = MaterialDesignColors.PrimaryColor.Blue,
            SecondaryColor = MaterialDesignColors.SecondaryColor.Amber
        });
        return (vm, view, localization);
    }
}
