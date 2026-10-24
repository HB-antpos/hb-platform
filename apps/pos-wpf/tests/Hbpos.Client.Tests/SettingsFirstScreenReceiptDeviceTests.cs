using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.NetworkDiagnostics;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Controls;
using Hbpos.Client.Wpf.Views.Screens;

namespace Hbpos.Client.Tests;

/// <summary>
/// 设置界面首屏重设计（阶段 B）：「小票打印机」三个分页与「设备注册」页。
/// 目标：1366×632 视口内正文不出现滚动条、固定底栏的功能按钮全部可见；小票预览是独立右栏且内部可滚动；
/// 设备注册的网络检测在固定高度的卡内滚动，诊断展开态与英文文案同样不超视口。
/// 运行时用例（<see cref="SettingsFirstScreenReceiptDeviceRuntimeTests"/>）是 WPF 测试，只能在 Windows CI 跑；
/// 本文件其余用例是 XAML / ViewModel 契约，任何平台都能跑。
/// </summary>
public sealed class SettingsFirstScreenReceiptDeviceContractTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Only_the_settings_page_opts_in_to_the_settings_layout_of_the_shared_server_panel()
    {
        // 登录页（MainWindow）与设备注册页（DeviceRegistrationView）继续用面板原布局：不能设置 IsSettingsLayout。
        foreach (var relative in new[] { "MainWindow.xaml", Path.Combine("Views", "Screens", "DeviceRegistrationView.xaml") })
        {
            var panel = Assert.Single(LoadXaml(relative).Descendants(), IsServerPanel);
            Assert.Null(panel.Attribute("IsSettingsLayout"));
        }

        var settingsPanel = Assert.Single(LoadXaml(Path.Combine("Views", "Screens", "SettingsView.xaml")).Descendants(), IsServerPanel);
        Assert.Equal("True", (string?)settingsPanel.Attribute("IsSettingsLayout"));
        Assert.Equal("{Binding ApiServerSettings}", (string?)settingsPanel.Attribute("DataContext"));
    }

    [Fact]
    public void The_shared_server_panel_keeps_its_original_buttons_and_hides_them_through_the_switch()
    {
        var panel = LoadXaml(Path.Combine("Views", "Controls", "ApiServerSettingsPanel.xaml"));

        // 原布局里的按钮与自动化标识一个不少（登录页 / 设备注册页仍靠它们）。
        foreach (var token in new[]
                 {
                     "TestConnectionCommand", "SaveCommand", "Network.DiagnoseCommand", "NetworkDiagnoseButton",
                     "NetworkFixButton", "NetworkFixConfirmButton", "NetworkUndoButton", "NetworkCopyReportButton"
                 })
        {
            Assert.Contains(
                panel.Descendants().SelectMany(element => element.Attributes()),
                attribute => attribute.Value.Contains(token, StringComparison.Ordinal));
        }

        // 设置页布局那一份（IsSettingsLayout 为真才显示）里不含任何操作按钮命令，也不含网络检测。
        // 设置页布局 Border 的特征：自己的 Style 默认折叠（直接子 Setter Visibility=Collapsed），触发器里才变可见。
        var settingsLayout = Assert.Single(
            panel.Root!.Elements(Presentation + "Grid").Single().Elements(Presentation + "Border"),
            border => border.Element(Presentation + "Border.Style")?
                .Element(Presentation + "Style")?
                .Elements(Presentation + "Setter")
                .Any(setter => (string?)setter.Attribute("Property") == "Visibility" &&
                               (string?)setter.Attribute("Value") == "Collapsed") == true);
        var commands = settingsLayout.Descendants(Presentation + "Button")
            .Select(button => (string?)button.Attribute("Command"))
            .ToList();
        Assert.Equal(
            ["{Binding UseDevelopmentAddressCommand}", "{Binding UseReleaseAddressCommand}"],
            commands);
        Assert.DoesNotContain(
            settingsLayout.Descendants().SelectMany(element => element.Attributes()),
            attribute => attribute.Value.Contains("Network.", StringComparison.Ordinal) ||
                         attribute.Value.Contains("HasNetworkDiagnostics", StringComparison.Ordinal));
    }

    [Fact]
    public void Device_registration_footer_hosts_the_commands_moved_out_of_the_shared_panel()
    {
        var page = DevicePage();
        var footer = Assert.Single(page.Elements(), element => element.Name.LocalName == "SettingsPageHost.Footer");
        var commands = footer.Descendants(Presentation + "Button")
            .Select(button => (string?)button.Attribute("Command"))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var command in new[]
                 {
                     "{Binding ApiServerSettings.TestConnectionCommand}",
                     "{Binding ApiServerSettings.SaveCommand}",
                     "{Binding ApiServerSettings.Network.DiagnoseCommand}",
                     "{Binding ApiServerSettings.Network.CopyReportCommand}",
                     "{Binding ReregisterDeviceCommand}"
                 })
        {
            Assert.Contains(command, commands);
        }

        // 动态长文案的修复 / 确认 / 还原按钮是底栏例外：留在网络检测卡里，且自动化标识不变。
        var diagnostics = LoadXaml(Path.Combine("Views", "Controls", "NetworkDiagnosticsPanel.xaml"));
        foreach (var id in new[] { "NetworkFixButton", "NetworkFixConfirmButton", "NetworkUndoButton" })
        {
            Assert.Contains(
                diagnostics.Descendants(Presentation + "Button"),
                button => (string?)button.Attribute("AutomationProperties.AutomationId") == id);
        }

        // 三栏列宽与底栏三区列宽一致（按钮落在各自栏的正下方）。
        static string[] Widths(XElement grid) => grid.Element(Presentation + "Grid.ColumnDefinitions")!
            .Elements().Select(column => (string)column.Attribute("Width")!).ToArray();
        var bodyGrid = page.Elements(Presentation + "Grid").Single();
        var footerGrid = footer.Elements(Presentation + "Grid").Single();
        Assert.Equal(Widths(bodyGrid), Widths(footerGrid));
    }

    [Fact]
    public void Receipt_preview_is_a_separate_right_column_outside_the_page_host_with_a_scrollable_body_and_no_buttons()
    {
        var host = ReceiptHost();
        var root = host.Parent!;
        Assert.Equal("Grid", root.Name.LocalName);
        var columns = root.Element(Presentation + "Grid.ColumnDefinitions")!.Elements()
            .Select(column => (string)column.Attribute("Width")!)
            .ToArray();
        Assert.Equal(["*", "12", "296"], columns);

        // 预览是页面根的第 3 列，不在 SettingsPageHost 里；内部有纵向触屏滚动，且不含任何功能按钮。
        var preview = Assert.Single(root.Elements(Presentation + "Border"), border => (string?)border.Attribute("Grid.Column") == "2");
        Assert.DoesNotContain(preview.Descendants(), element => element.Name.LocalName == "SettingsPageHost");
        Assert.Empty(preview.Descendants(Presentation + "Button"));
        var scroll = Assert.Single(preview.Descendants(Presentation + "ScrollViewer"));
        Assert.Equal("VerticalOnly", (string?)scroll.Attribute("PanningMode"));
        Assert.Equal("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
        Assert.Contains(preview.Descendants(Presentation + "TextBlock"), text => (string?)text.Attribute("Text") == "{Binding ReceiptReturnPolicyText}");
    }

    [Fact]
    public void Receipt_sync_and_managed_hint_are_shared_by_the_store_and_terms_sub_pages()
    {
        var host = ReceiptHost();
        var subTabs = Assert.Single(host.Elements(), element => element.Name.LocalName == "SettingsPageHost.SubTabs");
        var footer = Assert.Single(host.Elements(), element => element.Name.LocalName == "SettingsPageHost.Footer");

        // 总部下发锁提示在二级页签条右侧，门店信息与小票条款共用同一处（一份提示文字）；硬件分页不显示（样式触发器按分页折叠）。
        Assert.Single(
            subTabs.Descendants(Presentation + "TextBlock"),
            text => ((string?)text.Attribute("Text"))?.Contains("ReceiptProfileManagedHintText", StringComparison.Ordinal) == true);
        var root = host.Parent!;
        var hintStyle = Assert.Single(
            root.Descendants(Presentation + "Style"),
            style => (string?)style.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == "ReceiptManagedHintBorderStyle");
        Assert.Contains(
            hintStyle.Descendants(Presentation + "Condition"),
            condition => (string?)condition.Attribute("Binding") == "{Binding IsReceiptHardwareSubTabSelected}" &&
                         (string?)condition.Attribute("Value") == "False");

        // 底栏左侧状态文字绑定测试打印 / 同步结果；空闲时显示「修改后自动保存」。
        Assert.Contains(
            footer.Descendants(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "{Binding ReceiptPrinterTestStatusMessage}");
        Assert.Contains(
            footer.Descendants(Presentation + "TextBlock"),
            text => ((string?)text.Attribute("Text"))?.Contains("settings.autosave.hint", StringComparison.Ordinal) == true);
        // 连接卡里不再有状态消息（已搬到底栏）。
        Assert.Single(
            root.Descendants(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "{Binding ReceiptPrinterTestStatusMessage}");
    }

    [Fact]
    public void Printer_port_advanced_row_has_a_fixed_height_and_keeps_the_input_on_the_same_row()
    {
        var host = ReceiptHost();
        var port = Assert.Single(
            host.Descendants(Presentation + "TextBox"),
            textBox => ((string?)textBox.Attribute("Text"))?.Contains("{Binding ReceiptPrinterPortText,", StringComparison.Ordinal) == true);
        // 输入框与展开开关在同一个固定 56 高的行里，展开只切换输入框可见性，不增加高度。
        var row = port.Ancestors(Presentation + "Border").First();
        Assert.Equal("56", (string?)row.Attribute("Height"));
        Assert.Contains(row.Descendants(Presentation + "ToggleButton"), toggle =>
            (string?)toggle.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "ReceiptPrinterPortAdvancedToggle");
        Assert.Contains("ReceiptPrinterPortAdvancedToggle", (string?)port.Attribute("Visibility"), StringComparison.Ordinal);
        Assert.DoesNotContain(host.Descendants(), element => element.Name.LocalName == "Expander");
    }

    [Fact]
    public void Entry_status_is_split_into_preferred_current_and_fallback_for_the_two_row_display()
    {
        var selector = new ApiTransportSelector();
        selector.SetPreferredMode("aws");
        var viewModel = CreateEntryViewModel("https://pos.hotbargain.vip/pos-api/", selector);

        viewModel.Load();

        Assert.True(viewModel.HasEntryStatus);
        Assert.Equal("AWS Sydney (pos-syd.hotbargain.vip)", viewModel.PreferredEntryText);
        Assert.Equal("AWS Sydney (pos-syd.hotbargain.vip)", viewModel.CurrentEntryText);
        Assert.False(viewModel.IsEntryFallback);

        // 首选入口连不上、已回退：实际入口变化并出现「回退中」，原整句 EntryStatusText 同步保留（登录页仍用它）。
        selector.ReportConnectFailure(
            new Uri("https://pos.hotbargain.vip/pos-api/"),
            new Uri("https://pos-syd.hotbargain.vip/pos-api/"));
        viewModel.RefreshEntryStatus();

        Assert.Equal("AWS Sydney (pos-syd.hotbargain.vip)", viewModel.PreferredEntryText);
        Assert.Equal("EdgeOne (pos.hotbargain.vip)", viewModel.CurrentEntryText);
        Assert.True(viewModel.IsEntryFallback);
        Assert.Contains("falling back", viewModel.EntryStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_status_parts_are_empty_outside_the_entry_group_or_without_a_selector()
    {
        var local = CreateEntryViewModel("http://localhost:5159/", new ApiTransportSelector());
        local.Load();
        Assert.False(local.HasEntryStatus);
        Assert.Equal(string.Empty, local.PreferredEntryText);
        Assert.Equal(string.Empty, local.CurrentEntryText);
        Assert.False(local.IsEntryFallback);

        var noSelector = CreateEntryViewModel("https://pos.hotbargain.vip/pos-api/", selector: null);
        noSelector.Load();
        Assert.False(noSelector.HasEntryStatus);
        Assert.Equal(string.Empty, noSelector.PreferredEntryText);
    }

    [Fact]
    public void New_entry_status_copy_exists_in_both_languages()
    {
        var resources = Path.Combine(WpfRoot(), "Resources");
        foreach (var file in new[] { "SettingsStrings.resx", "SettingsStrings.zh-CN.resx" })
        {
            var values = XDocument.Load(Path.Combine(resources, file))
                .Descendants("data")
                .ToDictionary(element => (string)element.Attribute("name")!, element => element.Element("value")?.Value);
            foreach (var key in new[]
                     {
                         "settings.serverAddress.entry.preferred",
                         "settings.serverAddress.entry.current",
                         "settings.serverAddress.entry.fallbackChip"
                     })
            {
                Assert.True(values.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text), $"{file} 缺少 {key}");
            }
        }
    }

    private static bool IsServerPanel(XElement element) =>
        element.Name.LocalName == "ApiServerSettingsPanel" &&
        (string?)element.Attribute("DataContext") == "{Binding ApiServerSettings}";

    private static XElement ReceiptHost() => Assert.Single(
        LoadXaml(Path.Combine("Views", "Screens", "SettingsView.xaml")).Descendants(),
        element => element.Name.LocalName == "SettingsPageHost" &&
                   ((string?)element.Attribute("Visibility"))?.Contains("IsReceiptPrinterSelected", StringComparison.Ordinal) == true);

    private static XElement DevicePage() => Assert.Single(
        LoadXaml(Path.Combine("Views", "Screens", "SettingsView.xaml")).Descendants(),
        element => element.Name.LocalName == "SettingsPageHost" &&
                   ((string?)element.Attribute("Visibility"))?.Contains("IsDeviceRegistrationSelected", StringComparison.Ordinal) == true);

    private static ApiServerSettingsViewModel CreateEntryViewModel(string currentAddress, ApiTransportSelector? selector)
    {
        var service = new ApiServerSettingsService(new HttpClient(), () => currentAddress, _ => { });
        var localization = new LocalizationService();
        localization.SetCulture("en-US");
        return new ApiServerSettingsViewModel(service, localization, transportSelector: selector);
    }

    private static XDocument LoadXaml(string relative) => XDocument.Load(Path.Combine(WpfRoot(), relative));

    private static string WpfRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "pos-wpf", "src", "Hbpos.Client.Wpf");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to find the WPF project directory.");
    }
}

/// <summary>
/// 运行时视口断言（1366×632）：小票三个分页 + 设备注册（初始 / 发现问题 / 修复前确认 / 修复后还原），中英文各一遍。
/// 复用 <see cref="SettingsFirstScreenViewportTests"/> 的底栏、正文滚动与视口断言。
/// </summary>
[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class SettingsFirstScreenReceiptDeviceRuntimeTests(PaymentViewRuntimeStaTestHost host)
{
    private const double ViewportWidth = SettingsFirstScreenViewportTests.ViewportWidth;
    private const double ViewportHeight = SettingsFirstScreenViewportTests.ViewportHeight;
    private const int ReceiptPageIndex = 2;
    private const int DevicePageIndex = 3;
    private static readonly string[] Cultures = ["zh-CN", "en-US"];

    // ===================================================================================
    // 小票打印机
    // ===================================================================================

    [Fact]
    public Task Receipt_hardware_sub_page_fits_with_footer_buttons_visible() =>
        RunReceiptAsync(SubPage.Hardware, (vm, view, page) =>
        {
            // 打印机与选项：没开自动保存时底栏只有「保存小票打印机」；「立即同步」只属于门店信息 / 条款分页。
            var buttons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view);
            AssertButtons(buttons, vm.SaveReceiptPrinterCommand);
            // 三个开关与端口「高级」行（固定 56 高）都在页内。
            var toggle = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(view.FindName("PrintBankReceiptTextToggle"));
            SettingsFirstScreenViewportTests.AssertInsideViewport(view, toggle, "打印选项开关");
            foreach (var name in new[] { "NoSaleCashDrawerRescanToggle", "ReturnsRescanToggle" })
            {
                var rescan = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(view.FindName(name));
                SettingsFirstScreenViewportTests.AssertInsideViewport(view, rescan, name);
            }

            AssertPortAdvancedRowKeepsItsHeight(vm, view);
        });

    [Fact]
    public Task Receipt_store_sub_page_fits_with_managed_hint_and_sync_in_footer() =>
        RunReceiptAsync(SubPage.Store, (vm, view, page) =>
        {
            var buttons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view);
            AssertButtons(buttons, vm.LoadReceiptProfileCommand, vm.SaveReceiptPrinterCommand);
            AssertManagedHintVisibleInSubTabs(page, view);
        });

    [Fact]
    public Task Receipt_terms_sub_page_fits_with_the_same_managed_hint_and_sync() =>
        RunReceiptAsync(SubPage.Terms, (vm, view, page) =>
        {
            var buttons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view);
            AssertButtons(buttons, vm.LoadReceiptProfileCommand, vm.SaveReceiptPrinterCommand);
            AssertManagedHintVisibleInSubTabs(page, view);
            // 三栏文本框都在页内（高度固定，不靠 * 行拉伸）。
            foreach (var textBox in PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBox>(view)
                         .Where(SettingsFirstScreenViewportTests.IsEffectivelyVisible))
            {
                SettingsFirstScreenViewportTests.AssertInsideViewport(view, textBox, "条款文本框");
            }
        });

    [Fact]
    public Task Receipt_preview_is_a_full_height_scrollable_right_column_shared_by_all_sub_pages() => host.RunAsync(async _ =>
    {
        foreach (var culture in Cultures)
        {
            foreach (var subPage in new[] { SubPage.Hardware, SubPage.Store, SubPage.Terms })
            {
                var (vm, view, localization) = await CreateAsync(culture, v => v.SelectReceiptPrinterCommand);
                try
                {
                    SelectSubPage(vm, subPage);
                    // 最长数据：地址多行 + 500 字退换货政策，预览纸张比预览区高，必须在预览栏内部滚动。
                    vm.ReceiptStoreAddressText = string.Join("\n", Enumerable.Repeat("Shop 12, 34 Church St, Parramatta", 8));
                    vm.ReceiptReturnPolicyText = new string('x', 500);
                    PaymentViewRuntimeStaTestHost.Realize(view, ViewportWidth, ViewportHeight);

                    var page = ReceiptPage(view);
                    var root = (FrameworkElement)VisualTreeHelper.GetParent(page);
                    var preview = PaymentViewRuntimeStaTestHost.FindVisualDescendants<ScrollViewer>(root)
                        .Single(scroll => !IsInside(scroll, page));
                    var card = Ancestor<Border>(preview, border => Math.Abs(border.ActualWidth - 296) < 1);
                    // 预览卡宽 296、占满整页高度（与页面容器同高），三个分页都一样；按钮一个都不放在里面。
                    Assert.Equal(296, card.ActualWidth, 0.5);
                    Assert.Equal(page.ActualHeight, card.ActualHeight, 1);
                    Assert.Empty(PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(card));
                    // Windows CI 实测：296 宽、整页高的预览栏装得下这份最长数据，所以不一定出现滚动——这是好事。
                    // 契约改为「预览栏自带纵向触屏滚动兜底」：可见性 Auto、纵向平移；内容真超出时才出现滚动条，页面正文不受影响。
                    Assert.Equal(ScrollBarVisibility.Auto, preview.VerticalScrollBarVisibility);
                    Assert.Equal(PanningMode.VerticalOnly, preview.PanningMode);
                    Assert.True(preview.ScrollableHeight >= 0, "预览栏滚动范围不能为负。");
                    SettingsFirstScreenViewportTests.AssertInsideViewport(view, card, "预览栏");
                    // 预览内容再长，页面正文也不能被撑出滚动条。
                    SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
                }
                finally
                {
                    Reset(view, localization);
                }
            }
        }
    });

    [Fact]
    public Task Receipt_footer_status_shows_the_idle_hint_until_a_test_or_sync_message_arrives() => host.RunAsync(async _ =>
    {
        foreach (var culture in Cultures)
        {
            var (vm, view, localization) = await CreateAsync(culture, v => v.SelectReceiptPrinterCommand);
            try
            {
                PaymentViewRuntimeStaTestHost.Realize(view, ViewportWidth, ViewportHeight);
                var footer = (FrameworkElement)ReceiptPage(view).Footer!;
                var idle = localization.T("settings.autosave.hint");
                Assert.Contains(VisibleTexts(footer), text => text == idle);

                // 三个分页通用：消息出现后取代空闲提示，并且不超出底栏（单行省略）。
                vm.ReceiptPrinterTestStatusMessage = new string('m', 300);
                PaymentViewRuntimeStaTestHost.Realize(view, ViewportWidth, ViewportHeight);
                Assert.DoesNotContain(VisibleTexts(footer), text => text == idle);
                var message = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(footer)
                    .Single(text => text.Text == vm.ReceiptPrinterTestStatusMessage);
                SettingsFirstScreenViewportTests.AssertInsideViewport(view, message, "底栏状态文字");
                SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(ReceiptPage(view), view);
            }
            finally
            {
                Reset(view, localization);
            }
        }
    });

    // ===================================================================================
    // 设备注册
    // ===================================================================================

    [Fact]
    public Task Device_registration_initial_state_fits_with_all_footer_buttons_visible() =>
        RunDeviceAsync(DeviceState.Initial);

    [Fact]
    public Task Device_registration_fallback_entry_and_restart_warning_fit() =>
        RunDeviceAsync(DeviceState.EntryFallbackAndRestart);

    [Fact]
    public Task Device_registration_diagnosis_with_finding_and_fix_fits() =>
        RunDeviceAsync(DeviceState.DiagnosisFinding);

    [Fact]
    public Task Device_registration_fix_confirmation_fits() =>
        RunDeviceAsync(DeviceState.FixConfirming);

    [Fact]
    public Task Device_registration_after_fix_with_result_and_undo_fits() =>
        RunDeviceAsync(DeviceState.FixApplied);

    private Task RunDeviceAsync(DeviceState state) => host.RunAsync(async _ =>
    {
        foreach (var culture in Cultures)
        {
            var (vm, view, localization) = await CreateAsync(culture, v => v.SelectDeviceRegistrationCommand, withNetwork: true);
            try
            {
                ConfigureDevice(vm, state);
                PaymentViewRuntimeStaTestHost.Realize(view, ViewportWidth, ViewportHeight);
                Assert.Equal(SettingsCategory.DeviceRegistration, vm.SelectedCategory);

                var page = PageAt(view, DevicePageIndex);
                var network = vm.ApiServerSettings.Network!;
                var hasResult = state != DeviceState.Initial && state != DeviceState.EntryFallbackAndRestart;

                // 底栏：测试连接 / 保存地址 ｜ 检测网络（+ 复制报告，仅有结果时）｜ 更换分店重新注册，全部在视口内且互不重叠。
                var buttons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view);
                var expected = new List<object>
                {
                    vm.ApiServerSettings.TestConnectionCommand,
                    vm.ApiServerSettings.SaveCommand,
                    network.DiagnoseCommand
                };
                if (hasResult)
                {
                    expected.Add(network.CopyReportCommand);
                }

                expected.Add(vm.ReregisterDeviceCommand);
                AssertButtons(buttons, expected.ToArray());
                AssertNoOverlap(view, buttons);

                // 三栏卡片（API 服务器 / 网络检测 / 更换分店）都落在页内，正文不滚动。
                SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
                var settingsPanel = PaymentViewRuntimeStaTestHost.FindVisualDescendants<ApiServerSettingsPanel>(page).Single();
                var diagnostics = PaymentViewRuntimeStaTestHost.FindVisualDescendants<NetworkDiagnosticsPanel>(page).Single();
                SettingsFirstScreenViewportTests.AssertInsideViewport(view, settingsPanel, "API 服务器栏");
                SettingsFirstScreenViewportTests.AssertInsideViewport(view, diagnostics, "网络检测栏");

                // 面板的设置页布局：内置的测试 / 保存与整块网络检测不可见（底栏承载同一批命令）。
                Assert.True(settingsPanel.IsSettingsLayout);
                Assert.DoesNotContain(
                    PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(settingsPanel)
                        .Where(SettingsFirstScreenViewportTests.IsEffectivelyVisible),
                    button => ReferenceEquals(button.Command, vm.ApiServerSettings.TestConnectionCommand) ||
                              ReferenceEquals(button.Command, vm.ApiServerSettings.SaveCommand) ||
                              ReferenceEquals(button.Command, network.DiagnoseCommand));

                // 步骤区：固定高度内部滚动，不含按钮；诊断展开后仍留足可见高度。
                var steps = PaymentViewRuntimeStaTestHost.FindVisualDescendants<ScrollViewer>(diagnostics).Single();
                if (hasResult)
                {
                    Assert.True(
                        steps.ViewportHeight >= 56 - 1,
                        $"步骤区可见高度 {steps.ViewportHeight:0.#} 小于 56。");
                    Assert.Empty(PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(steps));
                    if (state == DeviceState.DiagnosisFinding)
                    {
                        Assert.True(steps.ScrollableHeight > 0, "9 个步骤应在步骤区内部滚动。");
                    }
                }

                // 诊断里的一键修复 / 确认 / 还原按钮留在卡内固定状态区，也必须在视口内。
                foreach (var button in PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(diagnostics)
                             .Where(SettingsFirstScreenViewportTests.IsEffectivelyVisible))
                {
                    SettingsFirstScreenViewportTests.AssertInsideViewport(view, button, "诊断状态区按钮");
                }

                if (state == DeviceState.EntryFallbackAndRestart)
                {
                    // 入口状态固定在 API 服务器卡内：两行 + 「回退中」芯片，整块在卡内。
                    Assert.True(vm.ApiServerSettings.IsEntryFallback);
                    var chip = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(settingsPanel)
                        .Single(text => text.Text == localization.T("settings.serverAddress.entry.fallbackChip") &&
                                        SettingsFirstScreenViewportTests.IsEffectivelyVisible(text));
                    SettingsFirstScreenViewportTests.AssertInsideViewport(view, chip, "回退中芯片");
                }
            }
            finally
            {
                Reset(view, localization);
            }
        }
    });

    private static void ConfigureDevice(SettingsViewModel vm, DeviceState state)
    {
        var api = vm.ApiServerSettings;
        var network = api.Network!;
        if (state == DeviceState.EntryFallbackAndRestart)
        {
            api.Load();
            api.RestartRequired = true;
            api.StatusMessage = new string('s', 120);
            Assert.True(api.IsEntryFallback);
            return;
        }

        if (state == DeviceState.Initial)
        {
            return;
        }

        // 诊断结果：9 个步骤（含一条很长详情的慢步骤）+ 结论（长文案）+ 一键修复。
        network.HasResult = true;
        network.SummaryText = "3.4 s";
        network.Steps.Add(new DiagnosticStepItem("Local network", "Gateway 192.168.1.1", "12 ms", "ok"));
        network.Steps.Add(new DiagnosticStepItem("Link to router", "0% loss", "3 ms", "ok"));
        network.Steps.Add(new DiagnosticStepItem("System DNS", "Lookup of pos.hotbargain.vip is very slow on this machine because the DNS server takes several seconds to answer", "4210 ms", "slow"));
        network.Steps.Add(new DiagnosticStepItem("Public DNS (1.1.1.1 / 8.8.8.8)", "Lookup OK", "38 ms", "ok"));
        network.Steps.Add(new DiagnosticStepItem("IPv4 connection", "Port 443", "120 ms", "ok"));
        network.Steps.Add(new DiagnosticStepItem("IPv6 connection", "Skipped", "Skipped", "skipped"));
        network.Steps.Add(new DiagnosticStepItem("Proxy", "No proxy in use", "0 ms", "ok"));
        network.Steps.Add(new DiagnosticStepItem("Secure connection and server reply", "POS server replied", "310 ms", "ok"));
        network.Steps.Add(new DiagnosticStepItem("Clock", "1 s from server", "1 s", "ok"));
        network.FindingTitle = "This machine's DNS server is too slow";
        network.FindingMessage = string.Concat(Enumerable.Repeat("Looking up the server name is much slower than public DNS. ", 4));
        network.FindingSeverity = "warn";
        network.HasFix = true;
        network.FixButtonText = "Fix DNS (use 1.1.1.1 and 8.8.8.8)";

        if (state == DeviceState.FixConfirming)
        {
            network.PlanTarget = "DNS on Ethernet (Intel I219-V)";
            network.PlanBefore = "Automatic (192.168.1.1)";
            network.PlanAfter = "1.1.1.1, 8.8.8.8";
            network.PlanNote = string.Concat(Enumerable.Repeat("This changes a setting for the current Windows user only. ", 4));
            network.IsConfirming = true;
        }
        else if (state == DeviceState.FixApplied)
        {
            network.FindingTitle = "No problems found";
            network.FindingMessage = string.Empty;
            network.FindingSeverity = "ok";
            network.HasFix = false;
            network.ResultMessage = string.Concat(Enumerable.Repeat("Changed. The check passes now: the POS can reach the server. ", 4));
            network.ResultSeverity = "ok";
            network.AppliedSummary = "DNS on Ethernet: automatic → 1.1.1.1, 8.8.8.8  (2026-10-24 10:00)";
            network.CopyResultMessage = "Report copied.";
        }
    }

    // ===================================================================================
    // 公共
    // ===================================================================================

    private enum SubPage
    {
        Hardware,
        Store,
        Terms
    }

    private enum DeviceState
    {
        Initial,
        EntryFallbackAndRestart,
        DiagnosisFinding,
        FixConfirming,
        FixApplied
    }

    private Task RunReceiptAsync(SubPage subPage, Action<SettingsViewModel, SettingsView, SettingsPageHost> assert) =>
        host.RunAsync(async _ =>
        {
            foreach (var culture in Cultures)
            {
                var (vm, view, localization) = await CreateAsync(culture, v => v.SelectReceiptPrinterCommand);
                try
                {
                    SelectSubPage(vm, subPage);
                    // 总部已下发（只读 + 锁提示 + 「立即同步」文案）、保存状态文字出现，是最拥挤的情形。
                    vm.ReceiptProfileVersion = 12;
                    vm.ReceiptPrinterTestStatusMessage = "Already up to date (version 12)";
                    PaymentViewRuntimeStaTestHost.Realize(view, ViewportWidth, ViewportHeight);
                    Assert.Equal(SettingsCategory.ReceiptPrinter, vm.SelectedCategory);

                    var page = ReceiptPage(view);
                    SettingsFirstScreenViewportTests.AssertInsideViewport(view, page, nameof(SettingsPageHost));
                    SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
                    assert(vm, view, page);
                }
                finally
                {
                    Reset(view, localization);
                }
            }
        });

    private static void SelectSubPage(SettingsViewModel vm, SubPage subPage)
    {
        switch (subPage)
        {
            case SubPage.Hardware:
                vm.IsReceiptHardwareSubTabSelected = true;
                break;
            case SubPage.Store:
                vm.IsReceiptStoreSubTabSelected = true;
                break;
            default:
                vm.IsReceiptTermsSubTabSelected = true;
                break;
        }
    }

    private static SettingsPageHost ReceiptPage(SettingsView view) => PageAt(view, ReceiptPageIndex);

    private static SettingsPageHost PageAt(SettingsView view, int index)
    {
        var pages = PaymentViewRuntimeStaTestHost.FindVisualDescendants<SettingsPageHost>(view).ToList();
        Assert.Equal(6, pages.Count);
        return pages[index];
    }

    /// <summary>总部下发提示在二级页签条里可见（门店信息、小票条款两个分页都有），且在视口内。</summary>
    private static void AssertManagedHintVisibleInSubTabs(SettingsPageHost page, SettingsView view)
    {
        var subTabs = Assert.IsAssignableFrom<FrameworkElement>(page.SubTabs);
        var hint = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(subTabs)
            .Single(text => text.Text.Length > 0 && text.Text == ((SettingsViewModel)view.DataContext).ReceiptProfileManagedHintText);
        SettingsFirstScreenViewportTests.AssertInsideViewport(view, hint, "总部下发提示");
    }

    private static void AssertPortAdvancedRowKeepsItsHeight(SettingsViewModel vm, SettingsView view)
    {
        var toggle = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(view.FindName("ReceiptPrinterPortAdvancedToggle"));
        var row = Ancestor<Border>(toggle, border => Math.Abs(border.ActualHeight - 56) < 0.5);
        var collapsedHeight = row.ActualHeight;
        var input = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBox>(row).Single();
        Assert.False(SettingsFirstScreenViewportTests.IsEffectivelyVisible(input));

        // 展开：输入框出现在同一行，行高不变，页面正文仍不滚动。
        toggle.IsChecked = true;
        PaymentViewRuntimeStaTestHost.Realize(view, ViewportWidth, ViewportHeight);
        Assert.Equal(collapsedHeight, row.ActualHeight, 0.5);
        SettingsFirstScreenViewportTests.AssertInsideViewport(view, input, "端口输入框");
        SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(ReceiptPage(view));
        Assert.NotNull(vm);
    }

    private static void AssertButtons(IReadOnlyList<Button> buttons, params object[] commands)
    {
        Assert.Equal(commands.Length, buttons.Count);
        foreach (var command in commands)
        {
            Assert.Single(buttons, button => ReferenceEquals(button.Command, command));
        }
    }

    /// <summary>底栏按钮互不重叠（落在各自的区里）。</summary>
    private static void AssertNoOverlap(FrameworkElement view, IReadOnlyList<Button> buttons)
    {
        var rects = buttons
            .Select(button => button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize)))
            .ToList();
        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var overlap = Rect.Intersect(rects[i], rects[j]);
                Assert.True(
                    overlap.IsEmpty || overlap.Width < 1 || overlap.Height < 1,
                    $"底栏按钮 {i} 与 {j} 重叠：{rects[i]} / {rects[j]}");
            }
        }
    }

    private static IEnumerable<string> VisibleTexts(DependencyObject root) =>
        PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(root)
            .Where(SettingsFirstScreenViewportTests.IsEffectivelyVisible)
            .Select(text => text.Text);

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container))
            {
                return true;
            }
        }

        return false;
    }

    private static T Ancestor<T>(DependencyObject element, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match && predicate(match))
            {
                return match;
            }
        }

        throw new InvalidOperationException($"找不到满足条件的 {typeof(T).Name} 祖先。");
    }

    private static void Reset(SettingsView view, LocalizationService localization)
    {
        view.DataContext = null;
        localization.SetCulture("en-US");
    }

    private static async Task<(SettingsViewModel Vm, SettingsView View, LocalizationService Localization)> CreateAsync(
        string culture,
        Func<SettingsViewModel, IRelayCommand> select,
        bool withNetwork = false)
    {
        var localization = new LocalizationService();
        localization.SetCulture(culture);
        LocalizationResourceProvider.Instance.Configure(localization);
        var setup = DispatchProxy.Create<ICardTerminalSetupService, SettingsPaymentMethodsViewRuntimeTests.UnavailableTerminalSetup>();
        var http = new HttpClient();
        // 入口状态用真实的选择器：首选 AWS、已回退到 EdgeOne，与设计稿的「回退中」态一致。
        var selector = new ApiTransportSelector();
        selector.SetPreferredMode("aws");
        selector.ReportConnectFailure(
            new Uri("https://pos.hotbargain.vip/pos-api/"),
            new Uri("https://pos-syd.hotbargain.vip/pos-api/"));
        var apiSettings = new ApiServerSettingsViewModel(
            new ApiServerSettingsService(http, () => "https://pos.hotbargain.vip/pos-api/", _ => throw new NotSupportedException()),
            localization,
            networkDiagnostics: withNetwork ? new UnusedDiagnostics() : null,
            networkFixes: withNetwork ? new UnusedFixes() : null,
            transportSelector: selector);
        var vm = new SettingsViewModel(setup, localization, apiServerSettings: apiSettings);
        await vm.LoadAsync();
        var command = select(vm);
        if (command is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(null);
        }
        else
        {
            command.Execute(null);
        }

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

    /// <summary>界面测试只关心布局：诊断结果由测试直接写进 ViewModel，服务本身不会被调用。</summary>
    private sealed class UnusedDiagnostics : INetworkDiagnosticsService
    {
        public Task<NetworkDiagnosisReport> RunAsync(Uri apiBaseAddress, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedFixes : INetworkFixService
    {
        public NetworkFixPlan? Plan(NetworkFixKind kind) => null;

        public Task<NetworkFixResult> ApplyAsync(NetworkFixPlan plan, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public NetworkFixRecord? GetApplied(NetworkFixKind kind) => null;

        public Task<NetworkFixResult> UndoAsync(NetworkFixKind kind, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
