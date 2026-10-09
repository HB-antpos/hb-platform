using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Screens;

namespace Hbpos.Client.Tests;

/// <summary>
/// 支付设置与小票打印机页触屏改版的运行时契约：主要操作按钮 ≥52px、环境分段按钮与布尔开关双向同步、
/// Linkly 本地 IP 默认使用官方推荐地址并可一键恢复。设置 HBPOS_SETTINGS_REDESIGN_SCREENSHOTS 可导出整页截图。
/// </summary>
[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class SettingsTouchRedesignViewRuntimeTests(PaymentViewRuntimeStaTestHost host)
{
    private const int Width = 1366;
    private const int FullHeight = 3400;

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public Task Square_tab_uses_touch_sized_environment_segment_and_selection_lists(string culture) => host.RunAsync(async _ =>
    {
        var (vm, view, localization) = await CreateAsync(culture, vm => vm.SelectPaymentTerminalCommand);
        try
        {
            PaymentViewRuntimeStaTestHost.Realize(view, Width, FullHeight);
            var radios = Segment(view, "SquareEnvironmentSegment");
            Assert.Equal(2, radios.Count);
            Assert.All(radios, radio => Assert.True(radio.ActualHeight >= 48, $"segment height {radio.ActualHeight}"));
            Assert.False(vm.IsSquareSandbox);
            Assert.True(radios[0].IsChecked);

            radios[1].IsChecked = true;
            PaymentViewRuntimeStaTestHost.Realize(view, Width, FullHeight);
            Assert.True(vm.IsSquareSandbox);
            Assert.False(radios[0].IsChecked);

            Assert.IsType<ListBox>(view.FindName("SquareLocationList"));
            Assert.IsType<ListBox>(view.FindName("SquareDeviceList"));
            AssertTouchButton(view, vm.SaveSquareCommand, 52);
            AssertTouchButton(view, vm.LoadLocationsCommand, 52);
            AssertTouchButton(view, vm.LoadDevicesCommand, 52);
            Save(view, $"square-{culture}");
        }
        finally
        {
            view.DataContext = null;
            localization.SetCulture("en-US");
        }
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public Task Linkly_local_ip_defaults_to_the_recommended_address_and_can_restore_it(string culture) => host.RunAsync(async _ =>
    {
        var (vm, view, localization) = await CreateAsync(culture, vm => vm.SelectPaymentTerminalCommand);
        try
        {
            PaymentViewRuntimeStaTestHost.Realize(view, Width, FullHeight);
            var tabs = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TabControl>(view).First();
            tabs.SelectedIndex = 1;
            PaymentViewRuntimeStaTestHost.Realize(view, Width, FullHeight);

            Assert.Equal("127.0.0.1", vm.LinklyHostText);
            Assert.Equal("2011", vm.LinklyPortText);
            Assert.True(vm.IsLinklyRecommendedAddress);
            Save(view, $"linkly-{culture}");

            vm.LinklyHostText = "192.168.1.58";
            vm.LinklyPortText = "2012";
            Assert.False(vm.IsLinklyRecommendedAddress);
            PaymentViewRuntimeStaTestHost.Realize(view, Width, FullHeight);
            var restore = AssertTouchButton(view, vm.UseRecommendedLinklyAddressCommand, 52);
            Assert.Equal(Visibility.Visible, restore.Visibility);

            vm.UseRecommendedLinklyAddressCommand.Execute(null);
            PaymentViewRuntimeStaTestHost.Realize(view, Width, FullHeight);
            Assert.Equal("127.0.0.1", vm.LinklyHostText);
            Assert.Equal("2011", vm.LinklyPortText);
            Assert.True(vm.IsLinklyRecommendedAddress);
            Assert.Equal(Visibility.Collapsed, restore.Visibility);
        }
        finally
        {
            view.DataContext = null;
            localization.SetCulture("en-US");
        }
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public Task Receipt_printer_page_puts_test_print_and_save_on_large_buttons(string culture) => host.RunAsync(async _ =>
    {
        var (vm, view, localization) = await CreateAsync(culture, vm => vm.SelectReceiptPrinterCommand);
        try
        {
            PaymentViewRuntimeStaTestHost.Realize(view, Width, FullHeight);
            Assert.True(vm.IsReceiptPrinterSelected);
            AssertTouchButton(view, vm.TestReceiptPrinterCommand, 56);
            AssertTouchButton(view, vm.SaveReceiptPrinterCommand, 56);
            AssertTouchButton(view, vm.LoadReceiptProfileCommand, 52);
            var toggle = Assert.IsType<ToggleButton>(view.FindName("PrintBankReceiptTextToggle"));
            Assert.True(toggle.ActualHeight >= 48);
            Save(view, $"receipt-{culture}");
        }
        finally
        {
            view.DataContext = null;
            localization.SetCulture("en-US");
        }
    });

    private static async Task<(SettingsViewModel Vm, SettingsView View, LocalizationService Localization)> CreateAsync(
        string culture,
        Func<SettingsViewModel, IRelayCommand> select)
    {
        var localization = new LocalizationService();
        localization.SetCulture(culture);
        LocalizationResourceProvider.Instance.Configure(localization);
        var setup = DispatchProxy.Create<ICardTerminalSetupService, SettingsPaymentMethodsViewRuntimeTests.UnavailableTerminalSetup>();
        var http = new HttpClient();
        var apiSettings = new ApiServerSettingsViewModel(
            new ApiServerSettingsService(http, () => "https://example.test/", _ => throw new NotSupportedException()),
            localization);
        var vm = new SettingsViewModel(setup, localization, apiServerSettings: apiSettings);
        await vm.LoadAsync();
        await ((IAsyncRelayCommand)select(vm)).ExecuteAsync(null);
        var view = new SettingsView { DataContext = vm };
        view.Resources.MergedDictionaries.Add(new MaterialDesignThemes.Wpf.BundledTheme
        {
            BaseTheme = MaterialDesignThemes.Wpf.BaseTheme.Light,
            PrimaryColor = MaterialDesignColors.PrimaryColor.Blue,
            SecondaryColor = MaterialDesignColors.SecondaryColor.Amber
        });
        return (vm, view, localization);
    }

    private static List<RadioButton> Segment(DependencyObject root, string groupName) =>
        PaymentViewRuntimeStaTestHost.FindVisualDescendants<RadioButton>(root)
            .Where(radio => radio.GroupName == groupName)
            .ToList();

    private static Button AssertTouchButton(DependencyObject root, object command, double minHeight)
    {
        var button = Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(root)
            .Where(candidate => ReferenceEquals(candidate.Command, command)));
        if (button.Visibility == Visibility.Visible)
        {
            Assert.True(button.ActualHeight >= minHeight, $"button height {button.ActualHeight} < {minHeight}");
        }

        return button;
    }

    private static void Save(FrameworkElement view, string name)
    {
        var output = Environment.GetEnvironmentVariable("HBPOS_SETTINGS_REDESIGN_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        // 外层滚动区只渲染可见部分，这里直接渲染整页内容高度。
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap(Width, FullHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, $"settings-{name}.png"));
        encoder.Save(file);
    }
}
