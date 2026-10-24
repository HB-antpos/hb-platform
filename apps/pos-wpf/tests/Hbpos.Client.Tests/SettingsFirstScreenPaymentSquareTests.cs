using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Controls;
using Hbpos.Client.Wpf.Views.Screens;

namespace Hbpos.Client.Tests;

/// <summary>
/// 设置界面首屏重设计「支付终端」页的付款方式 + Square 分页：在 1366×632 视口下，
/// 固定底栏的所有可见按钮都落在视口内、正文不出现滚动条（兜底 ScrollViewer 的 ScrollableHeight 为 0）。
/// 覆盖：付款方式（自动保存 / 显式保存 + 手动刷卡最长说明）、Square（初始空态 / 选了门店+设备+设备码+草稿的满载态 / 沙盒态），
/// 每个场景中英文各跑一遍（英文文案更长）。
/// 这些用例是 WPF 运行时测试，只能在 Windows CI 跑；macOS 上只验证能编译。
/// </summary>
[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class SettingsFirstScreenPaymentSquareTests(PaymentViewRuntimeStaTestHost host)
{
    private const double Width = SettingsFirstScreenViewportTests.ViewportWidth;
    private const double Height = SettingsFirstScreenViewportTests.ViewportHeight;

    /// <summary>支付终端页是第 2 个分类页（与 SettingsCategory 枚举顺序一致）。</summary>
    private const int PaymentTerminalPageIndex = 1;

    private static readonly string[] Cultures = ["en-US", "zh-CN"];

    [Fact]
    public Task Payment_methods_autosave_first_screen_fits_without_scrolling() => host.RunAsync(async _ =>
    {
        foreach (var culture in Cultures)
        {
            var (vm, view, localization) = await CreateAsync(culture, autoSave: true);
            try
            {
                Assert.True(vm.IsPaymentMethodsSubTabSelected);
                PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
                var page = PaymentPage(view);

                // 自动保存：底栏只有提示文字，没有任何可见按钮（「保存付款方式」折叠）。
                var footerButtons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view);
                Assert.Empty(footerButtons);
                SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
                AssertThreeSwitchesInsideViewport(view);

                // 手动刷卡开启：右侧摘要详情文案最长，仍然放得下。
                var manual = Assert.IsType<ToggleButton>(view.FindName("ManualCardSettingToggle"));
                manual.IsChecked = true;
                PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
                SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
                AssertThreeSwitchesInsideViewport(view);
            }
            finally
            {
                view.DataContext = null;
                localization.SetCulture("en-US");
            }
        }
    });

    [Fact]
    public Task Payment_methods_explicit_save_button_appears_in_footer_and_stays_inside_viewport() => host.RunAsync(async _ =>
    {
        foreach (var culture in Cultures)
        {
            var (vm, view, localization) = await CreateAsync(culture, autoSave: false);
            try
            {
                Assert.True(vm.IsExplicitSaveVisible);
                PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
                var page = PaymentPage(view);

                var footerButtons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view);
                var save = Assert.Single(footerButtons);
                Assert.Same(vm.SavePaymentMethodsCommand, save.Command);
                Assert.True(save.ActualHeight >= 48, $"保存付款方式按钮高度 {save.ActualHeight}");
                SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
                AssertThreeSwitchesInsideViewport(view);
            }
            finally
            {
                view.DataContext = null;
                localization.SetCulture("en-US");
            }
        }
    });

    [Fact]
    public Task Square_initial_state_first_screen_fits_without_scrolling() => host.RunAsync(async _ =>
    {
        foreach (var culture in Cultures)
        {
            var (vm, view, localization) = await CreateAsync(culture, autoSave: true);
            try
            {
                vm.IsSquareSubTabSelected = true;
                PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
                var page = PaymentPage(view);

                AssertSquareFooter(page, view, vm, expectDraftChip: false);
                AssertSquareBodyFits(page, view);
                AssertSquareControlsInside(view, vm);

                // 环境分段在二级页签条右侧：两个单选按钮可见、触控高度 ≥44，且在视口内。
                var radios = EnvironmentSegment(view);
                Assert.Equal(2, radios.Count);
                foreach (var radio in radios)
                {
                    Assert.True(radio.ActualHeight >= 44, $"环境分段高度 {radio.ActualHeight}");
                    SettingsFirstScreenViewportTests.AssertInsideViewport(view, radio, "环境分段");
                }

                // 门店 / 设备两个列表在空态下显示居中提示，且列表高度固定为 98（内部滚动区，不随行数撑高）。
                var locationList = Assert.IsType<ListBox>(view.FindName("SquareLocationList"));
                var deviceList = Assert.IsType<ListBox>(view.FindName("SquareDeviceList"));
                Assert.Equal(98, locationList.ActualHeight, 1);
                Assert.Equal(98, deviceList.ActualHeight, 1);
            }
            finally
            {
                view.DataContext = null;
                localization.SetCulture("en-US");
            }
        }
    });

    [Fact]
    public Task Square_fully_loaded_state_with_draft_first_screen_fits_without_scrolling() => host.RunAsync(async _ =>
    {
        foreach (var culture in Cultures)
        {
            var (vm, view, localization) = await CreateAsync(culture, autoSave: true);
            try
            {
                vm.IsSquareSubTabSelected = true;
                FillSquareFully(vm);
                Assert.True(vm.HasSquareUnsaved, "选了门店 / 设备后 Square 应处于草稿状态，底栏要出现草稿芯片。");
                PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
                var page = PaymentPage(view);

                AssertSquareFooter(page, view, vm, expectDraftChip: true);
                AssertSquareBodyFits(page, view);
                AssertSquareControlsInside(view, vm);

                // 满载：列表仍是固定高度的内部滚动区；列表行都 ≥48 高。
                var locationList = Assert.IsType<ListBox>(view.FindName("SquareLocationList"));
                var deviceList = Assert.IsType<ListBox>(view.FindName("SquareDeviceList"));
                Assert.Equal(98, locationList.ActualHeight, 1);
                Assert.Equal(98, deviceList.ActualHeight, 1);
                foreach (var item in PaymentViewRuntimeStaTestHost.FindVisualDescendants<ListBoxItem>(locationList)
                             .Concat(PaymentViewRuntimeStaTestHost.FindVisualDescendants<ListBoxItem>(deviceList)))
                {
                    Assert.True(item.ActualHeight >= 48, $"列表行高度 {item.ActualHeight}");
                }

                // 列表行内不放功能按钮。
                Assert.Empty(PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(locationList));
                Assert.Empty(PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(deviceList));
            }
            finally
            {
                view.DataContext = null;
                localization.SetCulture("en-US");
            }
        }
    });

    [Fact]
    public Task Square_sandbox_state_disables_device_codes_and_still_fits() => host.RunAsync(async _ =>
    {
        foreach (var culture in Cultures)
        {
            var (vm, view, localization) = await CreateAsync(culture, autoSave: true);
            try
            {
                vm.IsSquareSubTabSelected = true;
                FillSquareFully(vm);
                vm.IsSquareSandbox = true;
                Assert.True(vm.IsSquareDeviceCodesUnsupported);
                PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
                var page = PaymentPage(view);

                AssertSquareFooter(page, view, vm, expectDraftChip: true);
                AssertSquareBodyFits(page, view);

                // 设备码区整体禁用：创建 / 加载 / 刷新三个按钮不可点。
                foreach (var command in new object[] { vm.CreateDeviceCodeCommand, vm.LoadDeviceCodesCommand, vm.RefreshDeviceCodeStatusCommand })
                {
                    var button = Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(page)
                        .Where(candidate => ReferenceEquals(candidate.Command, command)));
                    Assert.False(button.IsEnabled, "沙盒下设备码按钮必须禁用。");
                }

                // 提示槽里显示「不支持」警告，帮助说明折叠（二选一）。
                var warning = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(page)
                    .Single(text => text.Text == vm.SquareDeviceCodesUnavailableText);
                Assert.True(SettingsFirstScreenViewportTests.IsEffectivelyVisible(warning), "沙盒下应显示不支持提示。");
                SettingsFirstScreenViewportTests.AssertInsideViewport(view, warning, "沙盒不支持提示");
                var help = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(page)
                    .Single(text => text.Text == localization.T("settings.square.deviceCodesHelp"));
                Assert.False(SettingsFirstScreenViewportTests.IsEffectivelyVisible(help), "沙盒下帮助说明应折叠，让位给警告。");
            }
            finally
            {
                view.DataContext = null;
                localization.SetCulture("en-US");
            }
        }
    });

    [Fact]
    public Task Square_environment_segment_shows_only_on_square_tab_and_stays_in_sync_with_the_view_model() => host.RunAsync(async _ =>
    {
        var (vm, view, localization) = await CreateAsync("zh-CN", autoSave: true);
        try
        {
            // 付款方式分页：右侧没有环境分段。
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            Assert.All(EnvironmentSegment(view), radio =>
                Assert.False(SettingsFirstScreenViewportTests.IsEffectivelyVisible(radio), "付款方式分页不应显示 Square 环境分段。"));

            vm.IsSquareSubTabSelected = true;
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            var radios = EnvironmentSegment(view);
            Assert.All(radios, radio => Assert.True(SettingsFirstScreenViewportTests.IsEffectivelyVisible(radio)));
            Assert.False(vm.IsSquareSandbox);
            Assert.True(radios[0].IsChecked);

            radios[1].IsChecked = true;
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            Assert.True(vm.IsSquareSandbox);
            Assert.False(radios[0].IsChecked);

            // 切到 Linkly 分页：Square 的环境分段隐藏。
            vm.IsLinklySubTabSelected = true;
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            Assert.All(EnvironmentSegment(view), radio =>
                Assert.False(SettingsFirstScreenViewportTests.IsEffectivelyVisible(radio), "Linkly 分页不应显示 Square 环境分段。"));
        }
        finally
        {
            view.DataContext = null;
            localization.SetCulture("en-US");
        }
    });

    // ===================================================================================
    // 断言与辅助
    // ===================================================================================

    private static SettingsPageHost PaymentPage(SettingsView view) =>
        PaymentViewRuntimeStaTestHost.FindVisualDescendants<SettingsPageHost>(view).ToList()[PaymentTerminalPageIndex];

    private static List<RadioButton> EnvironmentSegment(DependencyObject root) =>
        PaymentViewRuntimeStaTestHost.FindVisualDescendants<RadioButton>(root)
            .Where(radio => radio.GroupName is { } name && name.StartsWith("SquareEnvironmentSegment#", StringComparison.Ordinal))
            .ToList();

    private static void AssertThreeSwitchesInsideViewport(SettingsView view)
    {
        foreach (var name in new[] { "ManualCardSettingToggle", "VoucherSettingToggle", "OrderListSettingToggle" })
        {
            var toggle = Assert.IsType<ToggleButton>(view.FindName(name));
            SettingsFirstScreenViewportTests.AssertInsideViewport(view, toggle, name);
            Assert.True(toggle.ActualHeight >= 48, $"{name} 触控高度 {toggle.ActualHeight}");
        }
    }

    /// <summary>Square 底栏：三个按钮（直连开关 / 刷新直连状态 / 保存 Square）都可见且在视口内；草稿芯片按状态出现。</summary>
    private static void AssertSquareFooter(SettingsPageHost page, SettingsView view, SettingsViewModel vm, bool expectDraftChip)
    {
        var buttons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view);
        Assert.Equal(3, buttons.Count);
        Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.ToggleSquareDirectCommand));
        Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.RefreshSquareDirectCommand));
        var save = Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.SaveSquareCommand));
        Assert.True(save.ActualHeight >= 48, $"保存 Square 高度 {save.ActualHeight}");
        Assert.All(buttons, button => Assert.True(button.ActualHeight >= 44, $"底栏按钮高度 {button.ActualHeight}"));

        var chipText = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>((FrameworkElement)page.Footer!)
            .FirstOrDefault(text => text.Text == vm.SquareSaveStateText && !string.IsNullOrEmpty(text.Text));
        if (expectDraftChip)
        {
            Assert.NotNull(chipText);
            SettingsFirstScreenViewportTests.AssertInsideViewport(view, chipText!, "草稿芯片");
        }
        else
        {
            Assert.True(chipText is null || !SettingsFirstScreenViewportTests.IsEffectivelyVisible(chipText), "没有草稿时不应显示草稿芯片。");
        }
    }

    private static void AssertSquareBodyFits(SettingsPageHost page, SettingsView view)
    {
        SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
        // 正文内所有可见按钮（加载门店位置 / 加载设备 / 创建设备码 / 加载设备码 / 刷新状态）也都在视口内。
        foreach (var button in PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(page)
                     .Where(button => SettingsFirstScreenViewportTests.IsEffectivelyVisible(button)))
        {
            SettingsFirstScreenViewportTests.AssertInsideViewport(view, button, $"Square 按钮 {button.Command?.GetType().Name}");
        }
    }

    private static void AssertSquareControlsInside(SettingsView view, SettingsViewModel vm)
    {
        var page = PaymentPage(view);
        foreach (var command in new object[]
                 {
                     vm.LoadLocationsCommand, vm.LoadDevicesCommand, vm.CreateDeviceCodeCommand,
                     vm.LoadDeviceCodesCommand, vm.RefreshDeviceCodeStatusCommand
                 })
        {
            var button = Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(page)
                .Where(candidate => ReferenceEquals(candidate.Command, command)));
            SettingsFirstScreenViewportTests.AssertInsideViewport(view, button, "Square 正文按钮");
            Assert.True(button.ActualHeight >= 44, $"Square 正文按钮高度 {button.ActualHeight}");
        }
    }

    /// <summary>
    /// 造「满载」态：3 个门店位置、3 台设备、已生成设备码，并选中其中一项（产生草稿）。
    /// 顺序有讲究：选中门店会清空设备与设备码，所以先选门店，再灌设备，再灌设备码。
    /// </summary>
    private static void FillSquareFully(SettingsViewModel vm)
    {
        vm.SquareLocations.Add(new SquareLocationOption("LHQ7K2M9ZP41A", "Hot Bargain Bankstown"));
        vm.SquareLocations.Add(new SquareLocationOption("LB3D8T5XN06WC", "Hot Bargain Warehouse"));
        vm.SquareLocations.Add(new SquareLocationOption("L9R4YV1CE72HG", "Hot Bargain Online"));
        vm.SelectedSquareLocation = vm.SquareLocations[0];
        vm.SquareDevices.Add(new SquareDeviceOption("DV-2A81F6C3", "Terminal 1", "PAIRED", "PAIRED"));
        vm.SquareDevices.Add(new SquareDeviceOption("DV-77C0E19B", "Terminal 2", "PAIRED", "PAIRED"));
        vm.SquareDevices.Add(new SquareDeviceOption("DV-5E3A90D4", "Front iPad", "PAIRED", "PAIRED"));
        vm.SelectedSquareDevice = vm.SquareDevices[0];
        var code = new SquareDeviceCodeOption("DC-1", "Front iPad", "K4W9PQ", "UNPAIRED", "LHQ7K2M9ZP41A", null, null, null);
        vm.SquareDeviceCodes.Add(code);
        vm.SelectedSquareDeviceCode = code;
        vm.SquareDeviceCodeNameText = "Front iPad";
    }

    private static async Task<(SettingsViewModel Vm, SettingsView View, LocalizationService Localization)> CreateAsync(
        string culture,
        bool autoSave)
    {
        var localization = new LocalizationService();
        localization.SetCulture(culture);
        LocalizationResourceProvider.Instance.Configure(localization);
        var setup = DispatchProxy.Create<ICardTerminalSetupService, SettingsPaymentMethodsViewRuntimeTests.UnavailableTerminalSetup>();
        var http = new HttpClient();
        var apiSettings = new ApiServerSettingsViewModel(
            new ApiServerSettingsService(http, () => "https://example.test/", _ => throw new NotSupportedException()),
            localization);
        // autoSave=true 与生产装配一致（开关改了直接保存，Square 为显式保存 + 草稿）；false 走显式保存按钮。
        var vm = new SettingsViewModel(
            setup,
            localization,
            apiServerSettings: apiSettings,
            paymentMethodSettingsService: new MutablePaymentMethodSettingsService(),
            autoSaveSettings: autoSave,
            autoSaveDelay: autoSave ? (_, _) => Task.CompletedTask : null);
        await vm.LoadAsync();
        await ((IAsyncRelayCommand)vm.SelectPaymentTerminalCommand).ExecuteAsync(null);

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
}
