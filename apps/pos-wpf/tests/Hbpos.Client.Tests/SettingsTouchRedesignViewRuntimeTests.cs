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
/// 支付设置与小票打印机页触屏改版的运行时契约：主要操作按钮 ≥52px（顶栏「测试打印」为 44px）、环境分段按钮与布尔开关双向同步、
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
            // 支付终端页拆成三个分页，进入时停在「付款方式」；Square 设置在 Square 分页。
            vm.IsSquareSubTabSelected = true;
            PaymentViewRuntimeStaTestHost.Realize(view, Width, FullHeight);
            var radios = Segment(view, "SquareEnvironmentSegment");
            Assert.Equal(2, radios.Count);
            Assert.All(radios, radio => Assert.True(radio.ActualHeight >= 44, $"segment height {radio.ActualHeight}"));
            Assert.False(vm.IsSquareSandbox);
            Assert.True(radios[0].IsChecked);

            radios[1].IsChecked = true;
            PaymentViewRuntimeStaTestHost.Realize(view, Width, FullHeight);
            Assert.True(vm.IsSquareSandbox);
            Assert.False(radios[0].IsChecked);

            Assert.IsType<ListBox>(view.FindName("SquareLocationList"));
            Assert.IsType<ListBox>(view.FindName("SquareDeviceList"));
            AssertTouchButton(view, vm.SaveSquareCommand, 44);
            AssertTouchButton(view, vm.LoadLocationsCommand, 44);
            AssertTouchButton(view, vm.LoadDevicesCommand, 44);
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
            var restore = AssertTouchButton(view, vm.UseRecommendedLinklyAddressCommand, 44);
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
            // 「测试打印」在顶栏（44 高），三个分页都能点；整页保存按钮只在没开自动保存时出现（这里未开启）。
            AssertTouchButton(view, vm.TestReceiptPrinterCommand, 44);
            AssertTouchButton(view, vm.SaveReceiptPrinterCommand, 56);
            var toggle = Assert.IsType<ToggleButton>(view.FindName("PrintBankReceiptTextToggle"));
            Assert.True(toggle.ActualHeight >= 48);
            // 「再次扫码确认」卡片里的两个开关（无交易开钱箱、退货）同样要满足触控高度，且默认关闭。
            foreach (var name in new[] { "NoSaleCashDrawerRescanToggle", "ReturnsRescanToggle" })
            {
                var rescanToggle = Assert.IsType<ToggleButton>(view.FindName(name));
                Assert.True(rescanToggle.ActualHeight >= 48, $"{name} 触控高度不足：{rescanToggle.ActualHeight:0.##}。");
                Assert.False(rescanToggle.IsChecked);
            }
            Save(view, $"receipt-{culture}");

            // 「从分店资料载入 / 立即同步」在「门店信息」分页。
            vm.IsReceiptStoreSubTabSelected = true;
            PaymentViewRuntimeStaTestHost.Realize(view, Width, FullHeight);
            AssertTouchButton(view, vm.LoadReceiptProfileCommand, 44);
            Assert.Equal(Visibility.Collapsed, PrintBankReceiptCardVisibility(toggle));
            AssertTouchButton(view, vm.TestReceiptPrinterCommand, 44);
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
        var view = new SettingsView();
        // 先隔离单选按钮分组再设置 DataContext，避免与其它测试遗留的同名分组互相清除勾选。
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

    // 切到其它分页后，「打印机与选项」分页的卡片整块折叠（开关本身的 Visibility 不变，看最近的折叠祖先）。
    private static Visibility PrintBankReceiptCardVisibility(DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: Visibility.Collapsed })
            {
                return Visibility.Collapsed;
            }
        }

        return Visibility.Visible;
    }

    private static List<RadioButton> Segment(DependencyObject root, string groupName) =>
        PaymentViewRuntimeStaTestHost.FindVisualDescendants<RadioButton>(root)
            .Where(radio => radio.GroupName is { } name && name.StartsWith(groupName + "#", StringComparison.Ordinal))
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
