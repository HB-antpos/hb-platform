using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.ViewModels.Settings;
using Hbpos.Client.Wpf.Views.Controls;
using Hbpos.Client.Wpf.Views.Screens;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Tests;

/// <summary>
/// 设置 → 支付终端 → ANZ Linkly 首屏重设计（1366×632 视口）：三种连接模式各自的底栏按钮全部可见、正文不出现滚动条，
/// 后端异步 3 线路 + 展开配对确认步（最大内容）也不超视口，中英文各跑一遍；以及聚焦线路在 SettingsViewModel 上的接线。
/// 运行时用例只能在 Windows CI 跑（macOS 上只验证能编译）；聚焦线路的纯逻辑见 <see cref="LinklyLineFocusTrackerTests"/>（可在 net9 临时宿主跑）。
/// </summary>
[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class SettingsFirstScreenLinklyTests(PaymentViewRuntimeStaTestHost host)
{
    private static readonly string[] Cultures = ["en-US", "zh-CN"];

    // ── 本地 IP ──

    [Fact]
    public Task Local_ip_footer_buttons_are_all_visible_and_body_does_not_scroll() => host.RunAsync(async _ =>
    {
        foreach (var culture in Cultures)
        {
            foreach (var sandbox in new[] { false, true })
            {
                var (vm, view, localization) = await CreateAsync(culture);
                try
                {
                    vm.IsLinklySubTabSelected = true;
                    vm.SelectedLinklyMode = LinklySettingsMode.LocalIp;
                    vm.IsLinklySandbox = sandbox;
                    PaymentViewRuntimeStaTestHost.Realize(view, SettingsFirstScreenViewportTests.ViewportWidth, SettingsFirstScreenViewportTests.ViewportHeight);

                    var page = PaymentPage(view);
                    var buttons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view, requireFooter: true);
                    SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);

                    // 测试 Linkly（沙盒本地 IP 时文案为「测试虚拟刷卡机」）/ 登录银行网络 / 启用 Linkly。
                    Assert.Equal(3, buttons.Count);
                    var test = Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.TestLinklyCommand));
                    Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.LogonLinklyCommand));
                    Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.SaveLinklyCommand));
                    Assert.Equal(vm.LinklyTestActionText, Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(test)).Text);
                    Assert.Equal(sandbox, test.Content is StackPanel panel &&
                        panel.Children.OfType<TextBlock>().Any(text => text.Text == localization.T("settings.linkly.localIp.testVirtualTerminal")));

                    AssertCommonParts(view, vm, page);
                }
                finally
                {
                    view.DataContext = null;
                    localization.SetCulture("en-US");
                }
            }
        }
    });

    // ── Cloud 直连同步 ──

    [Fact]
    public Task Cloud_direct_footer_buttons_are_all_visible_and_body_does_not_scroll() => host.RunAsync(async _ =>
    {
        foreach (var culture in Cultures)
        {
            var (vm, view, localization) = await CreateAsync(culture);
            try
            {
                vm.IsLinklySubTabSelected = true;
                vm.SelectedLinklyMode = LinklySettingsMode.CloudDirectSync;
                PaymentViewRuntimeStaTestHost.Realize(view, SettingsFirstScreenViewportTests.ViewportWidth, SettingsFirstScreenViewportTests.ViewportHeight);

                var page = PaymentPage(view);
                var buttons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view, requireFooter: true);
                SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);

                // 保存测试账号 / 取消 / 配对 Cloud / 测试 Linkly / 启用 Linkly。
                Assert.Equal(5, buttons.Count);
                Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.CancelLinklyCloudPairingCommand));
                Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.TestLinklyCommand));
                Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.SaveLinklyCommand));
                Assert.Equal(2, buttons.Count(button => button.Command is null));
                // 保存测试账号（无 Command、靠 Click 处理）在底栏最左，其它操作靠右。
                var leftmost = buttons.OrderBy(button => button.TranslatePoint(new Point(), view).X).First();
                Assert.Null(leftmost.Command);

                // 密码框仍是 code-behind 读取的那一个。
                Assert.IsType<PasswordBox>(view.FindName("LinklyCloudPasswordBox"));
                AssertCommonParts(view, vm, page);
            }
            finally
            {
                view.DataContext = null;
                localization.SetCulture("en-US");
            }
        }
    });

    // ── Cloud 后端异步（master-detail）──

    [Fact]
    public Task Cloud_backend_first_screen_fits_with_three_lines_and_pairing_confirmation() => host.RunAsync(async _ =>
    {
        foreach (var culture in Cultures)
        {
            var (vm, view, localization) = await CreateAsync(culture);
            try
            {
                vm.IsLinklySubTabSelected = true;
                vm.SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync;
                // 切到后端模式会触发一次线路目录刷新（测试里没有服务，会失败并清空目录）：等它结束后再放入测试线路。
                await PaymentViewRuntimeStaTestHost.WaitUntilAsync(() => !vm.IsBusy, "等待线路目录刷新结束超时。");
                PaymentViewRuntimeStaTestHost.PumpDispatcher();
                var rows = AddLines(vm, localization);

                PaymentViewRuntimeStaTestHost.Realize(view, SettingsFirstScreenViewportTests.ViewportWidth, SettingsFirstScreenViewportTests.ViewportHeight);
                var page = PaymentPage(view);

                // 常态：聚焦第 1 条（付款线路），底栏 7 个按钮全部可见，正文不滚动。
                var buttons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view, requireFooter: true);
                SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
                Assert.Same(rows[0], vm.FocusedLinklyCloudLine);
                AssertBackendFooter(buttons, vm, rows[0]);
                AssertCommonParts(view, vm, page);

                // 最大内容：聚焦第 3 条并展开配对确认步（含处理中提示），仍不超视口。
                vm.FocusedLinklyCloudLine = rows[2];
                rows[2].IsExpanded = true;
                rows[2].IsEnteringCode = true;
                rows[2].PairCode = "482913";
                PaymentViewRuntimeStaTestHost.Realize(view, SettingsFirstScreenViewportTests.ViewportWidth, SettingsFirstScreenViewportTests.ViewportHeight);
                rows[2].BeginConfirmation(vm.SelectedLinklyEnvironment);
                rows[2].IsOperationBusy = true;
                PaymentViewRuntimeStaTestHost.Realize(view, SettingsFirstScreenViewportTests.ViewportWidth, SettingsFirstScreenViewportTests.ViewportHeight);

                buttons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view, requireFooter: true);
                SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
                AssertBackendFooter(buttons, vm, rows[2]);
                var confirm = Assert.Single(
                    PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(view)
                        .Where(button => AutomationProperties.GetAutomationId(button) == "LinklyLineConfirmPairing"));
                SettingsFirstScreenViewportTests.AssertInsideViewport(view, confirm, "确认配对按钮");
                Assert.Same(rows[2].ConfirmCommand, confirm.Command);
                AssertCommonParts(view, vm, page);
            }
            finally
            {
                view.DataContext = null;
                localization.SetCulture("en-US");
            }
        }
    });

    // ── 聚焦线路在 SettingsViewModel 上的接线 ──

    [Fact]
    public Task Focused_line_switch_clears_pairing_and_does_not_change_the_payment_line() => host.RunAsync(async _ =>
    {
        var (vm, view, localization) = await CreateAsync("zh-CN");
        try
        {
            var rows = AddLines(vm, localization);
            // 默认聚焦当前付款线路（第 1 条）。
            Assert.Same(rows[0], vm.FocusedLinklyCloudLine);

            vm.FocusedLinklyCloudLine = rows[1];
            Assert.Same(rows[1], vm.FocusedLinklyCloudLine);
            rows[1].OpenForCodeEntry();
            rows[1].PairCode = "123456";

            // 切换聚焦前先关闭配对面板，配对码不会带到别的线路。
            vm.FocusedLinklyCloudLine = rows[2];
            Assert.Same(rows[2], vm.FocusedLinklyCloudLine);
            Assert.False(rows[1].IsExpanded);
            Assert.Empty(rows[1].PairCode);
            // 只是「查看」，付款线路不变。
            Assert.True(rows[0].IsSelected);
            Assert.False(rows[2].IsSelected);

            // 线路被整体重建后仍按终端 Id 回到同一条线路。
            var focusedId = rows[2].Terminal.TerminalId;
            var rebuilt = rows.Select(row => row.Terminal).Reverse()
                .Select(terminal => new LinklyCloudLineItem(
                    terminal, localization.T, () => false, _ => { }, _ => { }, _ => { }, _ => { },
                    _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask))
                .ToArray();
            vm.LinklyCloudLines.Clear();
            Assert.Null(vm.FocusedLinklyCloudLine);
            foreach (var row in rebuilt)
            {
                vm.LinklyCloudLines.Add(row);
            }

            Assert.Equal(focusedId, vm.FocusedLinklyCloudLine!.Terminal.TerminalId);
        }
        finally
        {
            view.DataContext = null;
            localization.SetCulture("en-US");
        }
    });

    [Fact]
    public Task Focused_line_change_notifies_property_changed() => host.RunAsync(async _ =>
    {
        var (vm, view, localization) = await CreateAsync("zh-CN");
        try
        {
            var notifications = new List<string?>();
            vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            var rows = AddLines(vm, localization);
            Assert.Contains(nameof(SettingsViewModel.FocusedLinklyCloudLine), notifications);

            notifications.Clear();
            vm.FocusedLinklyCloudLine = rows[2];
            Assert.Contains(nameof(SettingsViewModel.FocusedLinklyCloudLine), notifications);

            // 空值（ListBox 在列表重建时回写）被忽略，不改变聚焦。
            vm.FocusedLinklyCloudLine = null;
            Assert.Same(rows[2], vm.FocusedLinklyCloudLine);
        }
        finally
        {
            view.DataContext = null;
            localization.SetCulture("en-US");
        }
    });

    // ── 公共断言 ──

    /// <summary>三种模式共用的部分：3 行模式列表（⚙ ▲ ▼ ≥44）、底部状态条、二级页签右侧的环境分段都在视口内。</summary>
    private static void AssertCommonParts(SettingsView view, SettingsViewModel vm, SettingsPageHost page)
    {
        var modeList = Assert.Single(
            PaymentViewRuntimeStaTestHost.FindVisualDescendants<ItemsControl>(view)
                .Where(item => AutomationProperties.GetAutomationId(item) == "LinklyModePriorityList"));
        Assert.Equal(3, modeList.Items.Count);

        // 每行 3 个图标按钮（⚙ ▲ ▼）+ 1 个整行点击按钮；图标按钮 44×44，命令沿用原来的三条。
        var rowButtons = PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(modeList).ToList();
        Assert.Equal(12, rowButtons.Count);
        Assert.Equal(6, rowButtons.Count(button => ReferenceEquals(button.Command, vm.SelectLinklyPriorityModeCommand)));
        Assert.Equal(3, rowButtons.Count(button => ReferenceEquals(button.Command, vm.MoveLinklyPriorityUpCommand)));
        Assert.Equal(3, rowButtons.Count(button => ReferenceEquals(button.Command, vm.MoveLinklyPriorityDownCommand)));
        foreach (var button in rowButtons.Where(button => button.Width is 44))
        {
            Assert.True(button.ActualHeight >= 44 && button.ActualWidth >= 44, $"模式行图标按钮过小：{button.ActualWidth}×{button.ActualHeight}");
            SettingsFirstScreenViewportTests.AssertInsideViewport(view, button, "模式行图标按钮");
        }

        var status = Assert.Single(
            PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(view)
                .Where(text => AutomationProperties.GetAutomationId(text) == "LinklyTestStatusMessage"));
        SettingsFirstScreenViewportTests.AssertInsideViewport(view, status, "状态条");

        // 环境分段在二级页签条右侧（一份，两个按钮 ≥44 高），随当前分页显示。
        Assert.True(page.HasSubTabs);
        var segment = PaymentViewRuntimeStaTestHost.FindVisualDescendants<RadioButton>(view)
            .Where(radio => radio.GroupName is { } name && name.StartsWith("LinklyEnvironmentSegment", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, segment.Count);
        Assert.All(segment, radio =>
        {
            Assert.True(radio.ActualHeight >= 44, $"环境分段高度 {radio.ActualHeight}");
            SettingsFirstScreenViewportTests.AssertInsideViewport(view, radio, "环境分段");
        });
        Assert.Equal(vm.IsLinklySandbox, segment[1].IsChecked);
    }

    /// <summary>后端异步底栏：重新配对/收起、检测连接、使用此线路、变更绑定、解除绑定、测试交易状态、启用 Linkly。</summary>
    private static void AssertBackendFooter(IReadOnlyList<Button> buttons, SettingsViewModel vm, LinklyCloudLineItem focused)
    {
        Assert.Equal(7, buttons.Count);
        Assert.Single(buttons, button => ReferenceEquals(button.Command, focused.TogglePairingCommand));
        Assert.Single(buttons, button => ReferenceEquals(button.Command, focused.TestConnectionCommand));
        Assert.Single(buttons, button => ReferenceEquals(button.Command, focused.SelectCommand));
        var change = Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.ChangeLinklyCloudTerminalAssignmentCommand));
        var unassign = Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.UnassignLinklyCloudTerminalCommand));
        // 变更 / 解除绑定都作用于聚焦线路的管理项。
        Assert.Same(focused.ManagementItem, change.CommandParameter);
        Assert.Same(focused.ManagementItem, unassign.CommandParameter);
        Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.TestLinklyTransactionStatusCommand));
        Assert.Single(buttons, button => ReferenceEquals(button.Command, vm.SaveLinklyCommand));
        Assert.All(buttons, button => Assert.True(button.ActualHeight >= 44, $"底栏按钮高度 {button.ActualHeight}"));
    }

    private static SettingsPageHost PaymentPage(SettingsView view)
    {
        var pages = PaymentViewRuntimeStaTestHost.FindVisualDescendants<SettingsPageHost>(view).ToList();
        var page = pages[(int)SettingsCategory.PaymentTerminal];
        Assert.Equal(Visibility.Visible, page.Visibility);
        return page;
    }

    /// <summary>放入 3 条线路：第 1 条是当前付款线路且已绑定本机，第 2、3 条未绑定；第 2 条名称很长。</summary>
    private static List<LinklyCloudLineItem> AddLines(SettingsViewModel vm, LocalizationService localization)
    {
        var rows = new List<LinklyCloudLineItem>();
        for (var i = 1; i <= 3; i++)
        {
            var terminal = new LinklyCloudTerminalSummary(
                Guid.NewGuid(), i, i == 2 ? "Customer service / 客服收银台 B" : $"Checkout {i}", "Ready", false, true, null, null,
                i == 1 ? "POS-1" : null, i, $"v-{i}");
            var devices = new[]
            {
                new LinklyCloudAssignableDevice("POS-1", "WPF", true, i == 1 ? terminal.TerminalId : null, i),
                new LinklyCloudAssignableDevice("IPAD-2", "iPad", true, null, i)
            };
            var managementItem = new LinklyCloudTerminalManagementItem(terminal, devices, devices[0], localize: localization.T);
            var row = new LinklyCloudLineItem(
                terminal, localization.T, () => false, _ => { }, _ => { }, _ => { }, _ => { },
                _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, managementItem, supportsManagement: true);
            row.IsSelected = i == 1;
            rows.Add(row);
            vm.LinklyCloudLines.Add(row);
        }

        return rows;
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
        var vm = new SettingsViewModel(setup, localization, apiServerSettings: apiSettings);
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
