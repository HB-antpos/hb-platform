using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Controls;
using Hbpos.Client.Wpf.Views.Screens;
using Hbpos.RemoteMaintenance.Setup;
using System.Xml.Linq;

namespace Hbpos.Client.Tests;

/// <summary>
/// 设置界面首屏重设计（阶段 B，G1 组）：数据维护 / 远程维护 / 员工 App 下载 三页。
/// 目标：1366×632 视口下正文不滚动、功能按钮全部可见（中英文各一遍）；
/// 数据维护按钮留在各自说明卡内（无底栏）、远程维护与员工 App 下载的页面级操作在固定底栏。
/// 运行时用例（<see cref="PaymentViewRuntimeStaTestHost"/>）只能在 Windows CI 跑；
/// 文末的 XAML 契约用例与资源键用例是纯逻辑，macOS 也能跑。
/// </summary>
[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class SettingsFirstScreenDataRemoteAppTests(PaymentViewRuntimeStaTestHost host)
{
    private const double Width = SettingsFirstScreenViewportTests.ViewportWidth;
    private const double Height = SettingsFirstScreenViewportTests.ViewportHeight;

    // ===================================================================================
    // 数据维护
    // ===================================================================================

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task Data_maintenance_keeps_all_buttons_inside_their_cards_without_scrolling(string culture) =>
        host.RunAsync(async _ =>
        {
            await using var harness = await HarnessAsync.CreateAsync(culture, vm => vm.SelectDataMaintenanceCommand);
            var (vm, view) = (harness.Vm, harness.View);
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            var page = PageOf(view, SettingsCategory.DataMaintenance);

            // 内容放得下，本页不设固定底栏，正文不滚动。
            Assert.False(page.HasFooter);
            SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
            // 内容区目标 ≤ 502px（外层内边距纵向 24）。
            Assert.True(page.BodyScrollViewer!.ExtentHeight - 24 <= 502 + 0.5, $"数据维护内容高 {page.BodyScrollViewer.ExtentHeight - 24:0.#}px 超过 502px 预算。");

            // 5 个按钮（Debug 才有第 5 个）：可见、在视口内、满足触屏最小高度；下载主按钮 48 高。
            var commands = new List<object> { vm.DownloadCatalogCommand, vm.ResetCatalogCommand, vm.CheckForAppUpdateCommand, vm.RestartComputerCommand };
            if (vm.IsDebugTestSalesDataResetVisible)
            {
                commands.Add(vm.ResetTestSalesDataCommand);
            }

            foreach (var command in commands)
            {
                var button = ButtonOf(page, command);
                SettingsFirstScreenViewportTests.AssertInsideViewport(view, button, $"数据维护按钮 {button.Name}");
                Assert.True(button.IsEnabled, "空闲时按钮应可用。");
                Assert.True(button.ActualHeight >= 44 - 0.5, $"按钮高 {button.ActualHeight:0.#} < 44。");
            }

            Assert.Equal(48, ButtonOf(page, vm.DownloadCatalogCommand).ActualHeight, 0.5);

            // 日结后提醒重启开关也在首屏内。
            var toggle = Assert.IsType<ToggleButton>(view.FindName("RestartPromptSettingToggle"));
            SettingsFirstScreenViewportTests.AssertInsideViewport(view, toggle, "日结后提醒重启开关");
        });

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task Data_maintenance_sync_states_keep_card_height_and_stay_inside_viewport(string culture) =>
        host.RunAsync(async _ =>
        {
            await using var harness = await HarnessAsync.CreateAsync(culture, vm => vm.SelectDataMaintenanceCommand);
            var (vm, view, sync) = (harness.Vm, harness.View, harness.SyncStatus);
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            var page = PageOf(view, SettingsCategory.DataMaintenance);
            var panel = Assert.IsType<Border>(view.FindName("CatalogSyncStatusPanel"));
            var baseline = panel.ActualHeight;
            Assert.True(baseline > 0);

            // 同步中：不定进度条出现在固定状态区，卡高不变。
            sync.MarkStarted("S01");
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            Assert.True(vm.IsCatalogSyncing);
            Assert.Equal(baseline, panel.ActualHeight, 0.5);
            SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);

            // 同步失败：很长的失败原因最多两行，卡高不变、仍不出现滚动条。
            sync.MarkFailed("S01", string.Concat(Enumerable.Repeat("无法连接商品目录服务器，请检查网络后重试。 Unable to reach the catalog server. ", 4)));
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            Assert.True(vm.IsCatalogSyncFailed);
            Assert.Equal(baseline, panel.ActualHeight, 0.5);
            SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
            var failure = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(panel)
                .First(text => text.Text == vm.CatalogSyncFailureText);
            Assert.True(failure.ActualHeight <= 28 + 0.5, $"失败原因高 {failure.ActualHeight:0.#}px，超过两行（28px）。");

            // 同步成功：回到已同步，卡高仍不变。
            await sync.MarkSucceededAsync("S01");
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            Assert.True(vm.IsCatalogSyncSucceeded);
            Assert.Equal(baseline, panel.ActualHeight, 0.5);
            SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
        });

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task Data_maintenance_buttons_are_all_disabled_while_busy(string culture) =>
        host.RunAsync(async _ =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var harness = await HarnessAsync.CreateAsync(
                culture,
                vm => vm.SelectDataMaintenanceCommand,
                downloadCatalog: async token =>
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                });
            var (vm, view) = (harness.Vm, harness.View);
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            var page = PageOf(view, SettingsCategory.DataMaintenance);

            var execution = vm.DownloadCatalogCommand.ExecuteAsync(null);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            harness.SyncStatus.MarkStarted("S01");
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            try
            {
                Assert.True(vm.IsBusy);
                var commands = new List<object> { vm.DownloadCatalogCommand, vm.ResetCatalogCommand, vm.CheckForAppUpdateCommand, vm.RestartComputerCommand };
                if (vm.IsDebugTestSalesDataResetVisible)
                {
                    commands.Add(vm.ResetTestSalesDataCommand);
                }

                foreach (var command in commands)
                {
                    Assert.False(ButtonOf(page, command).IsEnabled, "忙碌时数据维护按钮应全部禁用。");
                }

                SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
            }
            finally
            {
                release.TrySetResult();
                await execution;
            }
        });

    // ===================================================================================
    // 远程维护
    // ===================================================================================

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task Remote_maintenance_has_one_footer_button_and_a_fixed_76px_status_zone(string culture) =>
        host.RunAsync(async _ =>
        {
            var remote = new FakeRemoteMaintenanceService();
            await using var harness = await HarnessAsync.CreateAsync(culture, vm => vm.SelectRemoteMaintenanceCommand, remote: remote);
            var (vm, view) = (harness.Vm, harness.View);
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            var page = PageOf(view, SettingsCategory.RemoteMaintenance);
            var zone = Assert.IsType<Border>(view.FindName("RemoteMaintenanceStatusZone"));

            // 空闲：底栏只有一个主按钮（48 高），状态区固定 76。
            var footerButtons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view);
            var install = Assert.Single(footerButtons);
            Assert.Same(vm.InstallRemoteMaintenanceCommand, install.Command);
            Assert.Equal(48, install.ActualHeight, 0.5);
            Assert.Equal(76, zone.ActualHeight, 0.5);
            SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);

            // 安装中：进度条 + 阶段文案落在状态区里，按钮禁用，页面不跳高。
            var release = new TaskCompletionSource<RemoteMaintenanceProvisionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            remote.InstallHandler = progress =>
            {
                progress?.Report(RemoteMaintenanceStage.DownloadedInstalling);
                return release.Task;
            };
            var execution = vm.InstallRemoteMaintenanceCommand.ExecuteAsync(null);
            await PaymentViewRuntimeStaTestHost.WaitUntilAsync(() => vm.IsRemoteMaintenanceInstalling, "远程维护安装没有进入进行中状态。");
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            try
            {
                Assert.False(install.IsEnabled);
                var bar = PaymentViewRuntimeStaTestHost.FindVisualDescendants<ProgressBar>(zone).Single();
                Assert.True(SettingsFirstScreenViewportTests.IsEffectivelyVisible(bar), "安装中进度条应显示在状态区内。");
                var text = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(zone)
                    .First(candidate => candidate.Text == vm.RemoteMaintenanceProgressText);
                AssertInside(zone, text);
                Assert.Equal(76, zone.ActualHeight, 0.5);
                SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
            }
            finally
            {
                // 失败：最长的一条错误文案（POS 缺少安装组件）两行内放得下，状态区仍是 76。
                release.TrySetResult(new RemoteMaintenanceProvisionResult(
                    false,
                    "settings.remoteMaintenance.result.componentsMissing",
                    new RemoteMaintenanceStatus(false, string.Empty, string.Empty, "notInstalled")));
                await execution;
            }

            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            var failure = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(zone)
                .First(candidate => candidate.Text == vm.RemoteMaintenanceProgressText && candidate.Text.Length > 0);
            AssertInside(zone, failure);
            Assert.Equal(76, zone.ActualHeight, 0.5);
            Assert.True(install.IsEnabled, "安装结束后主按钮应恢复可用。");
            SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);
            SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view);
        });

    // ===================================================================================
    // 员工 App 下载
    // ===================================================================================

    public static TheoryData<string, string> MobileVariants() => new()
    {
        { "zh-CN", "normal" },
        { "en-US", "normal" },
        { "zh-CN", "offline" },
        { "en-US", "offline" },
        { "zh-CN", "androidNotPublished" },
        { "en-US", "androidNotPublished" }
    };

    [Theory]
    [MemberData(nameof(MobileVariants))]
    public Task Mobile_app_download_shows_big_qr_cards_with_refresh_in_the_footer(string culture, string variant) =>
        host.RunAsync(async _ =>
        {
            var mobile = new FakeMobileAppDownloadService(variant);
            await using var harness = await HarnessAsync.CreateAsync(culture, vm => vm.SelectMobileAppDownloadCommand, mobile: mobile);
            var (vm, view) = (harness.Vm, harness.View);
            PaymentViewRuntimeStaTestHost.Realize(view, Width, Height);
            var page = PageOf(view, SettingsCategory.MobileAppDownload);
            var app = vm.MobileAppDownload;

            SettingsFirstScreenViewportTests.AssertBodyDoesNotScroll(page);

            // 底栏：「刷新」+ 状态文字；复制链接不在底栏。
            var footerButtons = SettingsFirstScreenViewportTests.AssertFooterAndButtonsVisible(page, view);
            var refresh = Assert.Single(footerButtons);
            Assert.Same(app.RefreshCommand, refresh.Command);
            Assert.True(refresh.ActualHeight >= 44 - 0.5);
            var footer = Assert.IsAssignableFrom<FrameworkElement>(page.Footer);
            Assert.Contains(
                PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(footer),
                text => text.Text == app.StatusText && !string.IsNullOrEmpty(text.Text));

            // 两个「复制链接」都在卡内、在首屏内。
            var copyButtons = PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(page)
                .Where(button => ReferenceEquals(button.Command, app.CopyLinkCommand)
                                 && !IsDescendantOf(button, footer))
                .ToList();
            Assert.Equal(2, copyButtons.Count);
            foreach (var copy in copyButtons)
            {
                SettingsFirstScreenViewportTests.AssertInsideViewport(view, copy, "复制链接");
                Assert.True(copy.ActualHeight >= 44 - 0.5);
            }

            var enlargeButtons = PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(page)
                .Where(button => ReferenceEquals(button.Command, app.EnlargeCommand)
                                 && SettingsFirstScreenViewportTests.IsEffectivelyVisible(button))
                .ToList();
            Assert.Equal(variant == "androidNotPublished" ? 1 : 2, enlargeButtons.Count);
            foreach (var qr in enlargeButtons)
            {
                // 二维码外框 240×240，二维码本体最大 222px（整数倍模块取整），四周留白底边。
                Assert.Equal(240, qr.ActualWidth, 0.5);
                Assert.Equal(240, qr.ActualHeight, 0.5);
                SettingsFirstScreenViewportTests.AssertInsideViewport(view, qr, "二维码");
                var image = PaymentViewRuntimeStaTestHost.FindVisualDescendants<Image>(qr).Single();
                Assert.InRange(image.ActualWidth, 150, 222 + 0.5);
                Assert.Equal(image.ActualWidth, image.ActualHeight, 0.5);
            }

            if (variant == "androidNotPublished")
            {
                // 占位框与二维码同尺寸同位置，里面的「重试」也在首屏内。
                var retry = PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(page)
                    .Where(button => ReferenceEquals(button.Command, app.RefreshCommand)
                                     && !IsDescendantOf(button, footer)
                                     && SettingsFirstScreenViewportTests.IsEffectivelyVisible(button))
                    .ToList();
                var retryButton = Assert.Single(retry);
                SettingsFirstScreenViewportTests.AssertInsideViewport(view, retryButton, "重试");
                Assert.True(retryButton.ActualHeight >= 44 - 0.5);
            }

            if (variant == "offline")
            {
                Assert.True(app.IsOfflineWarning);
            }
        });

    // ===================================================================================
    // 纯逻辑：XAML 契约与文案键（macOS 也能跑）
    // ===================================================================================

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Data_maintenance_page_has_no_footer_and_every_button_lives_in_its_card()
    {
        var page = PageElement("IsDataMaintenanceSelected");
        Assert.DoesNotContain(page.Elements(), element => element.Name.LocalName == "SettingsPageHost.Footer");

        var commands = new[]
        {
            "DownloadCatalogCommand", "ResetCatalogCommand", "CheckForAppUpdateCommand", "ResetTestSalesDataCommand", "RestartComputerCommand"
        };
        foreach (var command in commands)
        {
            var button = Assert.Single(page.Descendants(Presentation + "Button"), element =>
                (string?)element.Attribute("Command") == "{Binding " + command + "}");
            // 每个按钮都在一张说明卡（Border，样式名含 DataMaintenance...CardStyle）里。
            Assert.Contains(button.Ancestors(Presentation + "Border"), border =>
                ((string?)border.Attribute("Style") ?? string.Empty).StartsWith("{StaticResource DataMaintenance", StringComparison.Ordinal)
                && ((string?)border.Attribute("Style") ?? string.Empty).Contains("CardStyle", StringComparison.Ordinal));
            // 按钮高度不低于触屏最小 44：显式 Height 或所用样式里的 Height。
            var height = (string?)button.Attribute("Height");
            if (height is not null)
            {
                Assert.True(double.Parse(height) >= 44);
            }
            else
            {
                var styleKey = ((string)button.Attribute("Style")!).Replace("{StaticResource ", string.Empty).TrimEnd('}');
                var style = Assert.Single(page.Descendants(Presentation + "Style"), element => (string?)element.Attribute(Xaml + "Key") == styleKey);
                var heightSetter = Assert.Single(style.Elements(Presentation + "Setter"), setter => (string?)setter.Attribute("Property") == "Height");
                Assert.True(double.Parse((string)heightSetter.Attribute("Value")!) >= 44);
            }
        }
    }

    [Fact]
    public void Remote_maintenance_footer_holds_only_the_install_button_and_status_zone_is_76px()
    {
        var page = PageElement("IsRemoteMaintenanceSelected");
        var footer = Assert.Single(page.Elements(), element => element.Name.LocalName == "SettingsPageHost.Footer");
        var footerButton = Assert.Single(footer.Descendants(Presentation + "Button"));
        Assert.Equal("{Binding InstallRemoteMaintenanceCommand}", (string?)footerButton.Attribute("Command"));
        Assert.Equal("48", (string?)footerButton.Attribute("Height"));
        Assert.Contains("RemoteMaintenanceActionText", footerButton.Descendants(Presentation + "TextBlock").Select(text => (string?)text.Attribute("Text")).Single());
        // 页面里只有这一个按钮（进度 / 错误文案不放按钮）。
        Assert.Single(page.Descendants(Presentation + "Button"));

        var zone = Assert.Single(page.Descendants(Presentation + "Border"), element =>
            (string?)element.Attribute(Xaml + "Name") == "RemoteMaintenanceStatusZone");
        Assert.Equal("76", (string?)zone.Attribute("Height"));
        var bindings = zone.Descendants().SelectMany(element => element.Attributes()).Select(attribute => attribute.Value).ToArray();
        Assert.Contains("{Binding RemoteMaintenanceProgressText}", bindings);
        Assert.Contains("{Binding IsRemoteMaintenanceInstalling, Converter={StaticResource BoolToVis}}", bindings);
        Assert.Contains(zone.Descendants(Presentation + "TextBlock"), text =>
            (string?)text.Attribute("AutomationProperties.LiveSetting") == "Polite");

        // 安装说明拆成 ① ② ③ 三步 + 开头一句 + 结尾一句；旧的整段 installHint 不再被页面引用。
        var xaml = File.ReadAllText(SettingsViewPath());
        foreach (var key in new[] { "intro", "step1", "step2", "step3", "keepOpen" })
        {
            Assert.Contains("{loc:Loc settings.remoteMaintenance.installHint." + key + "}", xaml);
        }

        Assert.DoesNotContain("{loc:Loc settings.remoteMaintenance.installHint}", xaml);
    }

    [Fact]
    public void Mobile_app_page_has_222px_QR_in_a_240px_white_frame_and_only_refresh_in_the_footer()
    {
        var page = PageElement("IsMobileAppDownloadSelected");
        var footer = Assert.Single(page.Elements(), element => element.Name.LocalName == "SettingsPageHost.Footer");
        var footerButton = Assert.Single(footer.Descendants(Presentation + "Button"));
        Assert.Equal("{Binding RefreshCommand}", (string?)footerButton.Attribute("Command"));
        // 状态文字（联网状态 / 复制反馈）与进度条在底栏，绑定到 MobileAppDownload 子视图模型。
        var footerBar = Assert.Single(footer.Elements());
        Assert.Equal("{Binding MobileAppDownload}", (string?)footerBar.Attribute("DataContext"));
        var footerBindings = footer.Descendants().SelectMany(element => element.Attributes()).Select(attribute => attribute.Value).ToArray();
        Assert.Contains("{Binding StatusText}", footerBindings);
        Assert.Contains("{Binding CopyFeedbackText}", footerBindings);

        // 复制链接留在卡内：两个，且都不在底栏。
        var copyButtons = page.Descendants(Presentation + "Button")
            .Where(element => (string?)element.Attribute("Command") == "{Binding CopyLinkCommand}")
            .ToArray();
        Assert.Equal(2, copyButtons.Length);
        Assert.All(copyButtons, button => Assert.DoesNotContain(button.Ancestors(), ancestor => ReferenceEquals(ancestor, footer)));

        // 二维码按 222px 渲染；按钮外框 240×240，白底背板走资源（主题测试禁止直接写颜色名）。
        foreach (var payload in new[] { "IosQrPayload", "AndroidQrPayload" })
        {
            var image = Assert.Single(page.Descendants(Presentation + "Image"), element =>
                ((string?)element.Attribute("Source") ?? string.Empty).Contains(payload, StringComparison.Ordinal));
            Assert.Contains("ConverterParameter=222", (string?)image.Attribute("Source"), StringComparison.Ordinal);
        }

        var qrStyle = Assert.Single(page.Descendants(Presentation + "Style"), element =>
            (string?)element.Attribute(Xaml + "Key") == "MobileAppQrButtonStyle");
        string Setter(string name) => (string)Assert.Single(qrStyle.Elements(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == name).Attribute("Value")!;
        Assert.Equal("240", Setter("Width"));
        Assert.Equal("240", Setter("Height"));
        Assert.Equal("8", Setter("Padding"));
        Assert.Equal("{StaticResource MobileAppQrBackdropBrush}", Setter("Background"));
    }

    [Fact]
    public void Remote_maintenance_install_steps_exist_in_both_languages()
    {
        var keys = new[]
        {
            "settings.remoteMaintenance.installHint.intro",
            "settings.remoteMaintenance.installHint.step1",
            "settings.remoteMaintenance.installHint.step2",
            "settings.remoteMaintenance.installHint.step3",
            "settings.remoteMaintenance.installHint.keepOpen"
        };
        var texts = new Dictionary<string, Dictionary<string, string>>();
        foreach (var resource in new[] { "SettingsStrings.resx", "SettingsStrings.zh-CN.resx" })
        {
            var values = XDocument.Load(Path.Combine(WpfSourceDirectory(), "Resources", resource))
                .Descendants("data")
                .ToDictionary(element => (string)element.Attribute("name")!, element => (string?)element.Element("value") ?? string.Empty);
            foreach (var key in keys)
            {
                Assert.True(values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value), $"{resource} 缺少 {key}");
            }

            // 原整段键保留，别处若仍引用不会失效。
            Assert.True(values.ContainsKey("settings.remoteMaintenance.installHint"));
            texts[resource] = values;
        }

        foreach (var key in keys)
        {
            Assert.NotEqual(texts["SettingsStrings.resx"][key], texts["SettingsStrings.zh-CN.resx"][key]);
        }
    }

    // ===================================================================================
    // 辅助
    // ===================================================================================

    private static SettingsPageHost PageOf(SettingsView view, SettingsCategory category)
    {
        var pages = PaymentViewRuntimeStaTestHost.FindVisualDescendants<SettingsPageHost>(view).ToList();
        var page = pages[(int)category];
        Assert.Equal(Visibility.Visible, page.Visibility);
        return page;
    }

    private static Button ButtonOf(DependencyObject root, object command) =>
        Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(root)
            .Where(candidate => ReferenceEquals(candidate.Command, command)));

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = System.Windows.Media.VisualTreeHelper.GetParent(element);
             current is not null;
             current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>子元素的边界必须落在容器内（容差 1px）。</summary>
    private static void AssertInside(FrameworkElement container, FrameworkElement child)
    {
        var bounds = child.TransformToAncestor(container).TransformBounds(new Rect(child.RenderSize));
        Assert.True(
            bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= container.ActualWidth + 1 && bounds.Bottom <= container.ActualHeight + 1,
            $"元素 {bounds} 超出容器 {container.ActualWidth:0.#}×{container.ActualHeight:0.#}。");
    }

    private static XElement PageElement(string visibilityBindingProperty) =>
        Assert.Single(
            XDocument.Load(SettingsViewPath()).Descendants().Where(element =>
                element.Name.LocalName == "SettingsPageHost" &&
                ((string?)element.Attribute("Visibility") ?? string.Empty).Contains(visibilityBindingProperty, StringComparison.Ordinal)));

    private static string SettingsViewPath() =>
        Path.Combine(WpfSourceDirectory(), "Views", "Screens", "SettingsView.xaml");

    private static string WpfSourceDirectory() =>
        Path.Combine(FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "apps", "pos-wpf")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录。");
    }

    /// <summary>创建带真实 SettingsView 的测试环境；离开时还原语言并解绑 DataContext。</summary>
    private sealed class HarnessAsync : IAsyncDisposable
    {
        private readonly LocalizationService _localization;
        private readonly HttpClient _http;

        private HarnessAsync(SettingsViewModel vm, SettingsView view, LocalizationService localization, HttpClient http, CatalogSyncStatusService syncStatus)
        {
            Vm = vm;
            View = view;
            SyncStatus = syncStatus;
            _localization = localization;
            _http = http;
        }

        public SettingsViewModel Vm { get; }

        public SettingsView View { get; }

        public CatalogSyncStatusService SyncStatus { get; }

        public static async Task<HarnessAsync> CreateAsync(
            string culture,
            Func<SettingsViewModel, IRelayCommand> select,
            Func<CancellationToken, Task>? downloadCatalog = null,
            IRemoteMaintenanceService? remote = null,
            IMobileAppDownloadService? mobile = null)
        {
            var localization = new LocalizationService();
            localization.SetCulture(culture);
            LocalizationResourceProvider.Instance.Configure(localization);
            var setup = DispatchProxy.Create<ICardTerminalSetupService, SettingsPaymentMethodsViewRuntimeTests.UnavailableTerminalSetup>();
            var http = new HttpClient();
            var apiSettings = new ApiServerSettingsViewModel(
                new ApiServerSettingsService(http, () => "https://example.test/", _ => throw new NotSupportedException()),
                localization);
            var syncStatus = new CatalogSyncStatusService(
                new CatalogSyncStatusServiceTests.InMemoryAppSettingsRepository(),
                new CatalogSyncStatusServiceTests.MutableTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 12, 0, TimeSpan.Zero)));
            var session = new PosSessionState("HB POS", "S01", "Store", "D01", string.Empty, string.Empty, true, 0);
            // 五个数据维护命令各给一个桩（只要非 null 命令就可用，测试不会真的执行除下载外的任何一个）。
            var vm = new SettingsViewModel(
                setup,
                localization,
                downloadCatalogAsync: downloadCatalog ?? (_ => Task.CompletedTask),
                resetCatalogAsync: _ => Task.CompletedTask,
                resetTestSalesDataAsync: _ => Task.CompletedTask,
                confirmResetTestSalesDataAsync: () => Task.FromResult(false),
                checkForAppUpdateAsync: _ => Task.FromResult<AppUpdateCoordinatorResult>(null!),
                apiServerSettings: apiSettings,
                session: session,
                remoteMaintenanceService: remote,
                catalogSyncStatusService: syncStatus,
                mobileAppDownloadService: mobile,
                systemRestartSettingsStore: new FakeRestartSettingsStore(),
                restartCoordinatorProvider: () => null);
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
            return new HarnessAsync(vm, view, localization, http, syncStatus);
        }

        public ValueTask DisposeAsync()
        {
            View.DataContext = null;
            Vm.Dispose();
            _localization.SetCulture("en-US");
            _http.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRestartSettingsStore : ISystemRestartSettingsStore
    {
        public Task<bool> IsPromptEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task SetPromptEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DateOnly?> GetLastPromptBusinessDateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateOnly?>(null);

        public Task MarkPromptedAsync(DateOnly businessDate, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DateTimeOffset?> GetScheduledRestartAtAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task SetScheduledRestartAtAsync(DateTimeOffset restartAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearScheduledRestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeRemoteMaintenanceService : IRemoteMaintenanceService
    {
        public Func<IProgress<RemoteMaintenanceStage>?, Task<RemoteMaintenanceProvisionResult>>? InstallHandler { get; set; }

        public Task<RemoteMaintenanceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteMaintenanceStatus(false, string.Empty, string.Empty, "notInstalled"));

        public Task<RemoteMaintenanceProvisionResult> InstallAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default,
            IProgress<RemoteMaintenanceStage>? progress = null) =>
            InstallHandler is not null
                ? InstallHandler(progress)
                : Task.FromResult(new RemoteMaintenanceProvisionResult(
                    true,
                    "settings.remoteMaintenance.result.configured",
                    new RemoteMaintenanceStatus(true, "id", string.Empty, "running")));
    }

    private sealed class FakeMobileAppDownloadService(string variant) : IMobileAppDownloadService
    {
        private static readonly MobileAppInstallInfo Local = new(
            "https://apps.apple.com/au/app/hbgroup/id6786073002",
            null,
            "https://hotbargain.vip/api/mobile-app-builds/android-latest/download?profile=production",
            MobileAppAndroidAvailability.Unknown,
            null,
            null,
            null,
            false);

        public MobileAppInstallInfo GetLocalLinks() => Local;

        public Task<MobileAppInstallInfo> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(variant switch
            {
                "offline" => Local,
                "androidNotPublished" => Local with
                {
                    IosVersion = "1.0.10",
                    AndroidAvailability = MobileAppAndroidAvailability.NotPublished,
                    MetadataLoaded = true
                },
                _ => Local with
                {
                    IosVersion = "1.0.10",
                    AndroidAvailability = MobileAppAndroidAvailability.Available,
                    AndroidVersion = "1.0.12",
                    AndroidBuild = "79",
                    AndroidSizeBytes = 62L * 1024 * 1024,
                    MetadataLoaded = true
                }
            });
    }
}
