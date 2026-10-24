using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Controls;
using Hbpos.Client.Wpf.Views.Screens;

namespace Hbpos.Client.Tests;

/// <summary>
/// 设置界面「首屏」视口测试底座：1366×768 屏幕、带 40px 任务栏、主窗口标题栏 54 + 状态栏 42 之后，
/// SettingsView 实际只有 1366×632。设计目标是每个分类页的功能按钮都在这个视口内可见、无需下拉。
///
/// 阶段 A（框架）只断言框架本身：导航六项完整可见、顶栏 52、状态条 28、六个页面容器并列且互斥显示、Footer 槽存在。
/// 阶段 B 各页迁移完成后，用 <see cref="AssertFooterAndButtonsVisible"/> 与 <see cref="AssertBodyDoesNotScroll"/>
/// 给本页补详细断言（底栏所有按钮可见且在视口内、正文 ScrollableHeight==0）。
/// 这些用例是 WPF 运行时测试，只能在 Windows CI 跑；macOS 上只验证能编译。
/// </summary>
[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class SettingsFirstScreenViewportTests(PaymentViewRuntimeStaTestHost host)
{
    /// <summary>首屏视口宽度（逻辑像素）。</summary>
    public const double ViewportWidth = 1366;

    /// <summary>首屏视口高度（逻辑像素）：768 - 40 任务栏 - 54 标题栏 - 42 状态栏。</summary>
    public const double ViewportHeight = 632;

    private const double TopBarHeight = 52;
    private const double StatusBarHeight = 28;
    private const double NavButtonHeight = 48;
    private const double Tolerance = 1;

    // 六个分类页面在 XAML 里的并列顺序与 SettingsCategory 枚举顺序一致。
    private const int PageCount = 6;

    [Fact]
    public Task Data_maintenance_first_screen_skeleton() =>
        RunSkeletonAsync(SettingsCategory.DataMaintenance, vm => vm.SelectDataMaintenanceCommand);

    [Fact]
    public Task Payment_terminal_first_screen_skeleton() =>
        RunSkeletonAsync(SettingsCategory.PaymentTerminal, vm => vm.SelectPaymentTerminalCommand);

    [Fact]
    public Task Receipt_printer_first_screen_skeleton() =>
        RunSkeletonAsync(SettingsCategory.ReceiptPrinter, vm => vm.SelectReceiptPrinterCommand);

    [Fact]
    public Task Device_registration_first_screen_skeleton() =>
        RunSkeletonAsync(SettingsCategory.DeviceRegistration, vm => vm.SelectDeviceRegistrationCommand);

    [Fact]
    public Task Remote_maintenance_first_screen_skeleton() =>
        RunSkeletonAsync(SettingsCategory.RemoteMaintenance, vm => vm.SelectRemoteMaintenanceCommand);

    [Fact]
    public Task Mobile_app_download_first_screen_skeleton() =>
        RunSkeletonAsync(SettingsCategory.MobileAppDownload, vm => vm.SelectMobileAppDownloadCommand);

    /// <summary>中英文各跑一遍（英文导航标签更长，要换行而不是撑高或截断）。</summary>
    private Task RunSkeletonAsync(SettingsCategory category, Func<SettingsViewModel, IRelayCommand> select) =>
        host.RunAsync(async _ =>
        {
            foreach (var culture in new[] { "en-US", "zh-CN" })
            {
                var (vm, view, localization) = await CreateAsync(culture, select);
                try
                {
                    PaymentViewRuntimeStaTestHost.Realize(view, ViewportWidth, ViewportHeight);
                    Assert.Equal(category, vm.SelectedCategory);

                    AssertFrameLayout(view);
                    AssertNavigationFullyVisible(view, vm);

                    var pages = PaymentViewRuntimeStaTestHost.FindVisualDescendants<SettingsPageHost>(view).ToList();
                    Assert.Equal(PageCount, pages.Count);
                    for (var index = 0; index < pages.Count; index++)
                    {
                        // 六个页面并列放在同一个单元格里，只有当前分类的页面可见。
                        var expected = index == (int)category ? Visibility.Visible : Visibility.Collapsed;
                        Assert.Equal(expected, pages[index].Visibility);
                    }

                    var page = pages[(int)category];
                    // 框架阶段：Footer 槽存在（可为空）；阶段 B 迁移完成后改为 requireFooter: true 并补页内断言。
                    Assert.NotNull(typeof(SettingsPageHost).GetProperty(nameof(SettingsPageHost.Footer)));
                    AssertFooterAndButtonsVisible(page, view, requireFooter: false);
                    // 页面卡片自身必须落在视口内（顶栏之下、状态条之上）。
                    AssertInsideViewport(view, page, nameof(SettingsPageHost));
                }
                finally
                {
                    view.DataContext = null;
                    localization.SetCulture("en-US");
                }
            }
        });

    // ===================================================================================
    // 可复用断言（阶段 B 的各页测试直接调用）
    // ===================================================================================

    /// <summary>
    /// 断言页面容器的 Footer 内所有「可见」按钮都在视口内：自身及祖先 Visibility 均为 Visible（离屏测试没有
    /// PresentationSource，<see cref="UIElement.IsVisible"/> 恒为 false，所以用逐级 Visibility + 实际尺寸代替），
    /// 且边界落在 [0, ViewportWidth] × [0, ViewportHeight] 内。返回这些按钮，调用方可再断言个数与命令。
    /// Footer 为空且 <paramref name="requireFooter"/> 为 false 时直接返回空列表。
    /// </summary>
    public static IReadOnlyList<Button> AssertFooterAndButtonsVisible(
        SettingsPageHost page,
        FrameworkElement view,
        bool requireFooter = true)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(view);

        if (page.Footer is not FrameworkElement footer)
        {
            Assert.False(requireFooter, "该页面应有固定底栏（SettingsPageHost.Footer），但 Footer 为空。");
            return [];
        }

        Assert.True(page.HasFooter);
        AssertInsideViewport(view, footer, "Footer");

        var buttons = new List<Button>();
        if (footer is Button self)
        {
            buttons.Add(self);
        }

        buttons.AddRange(PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(footer));
        // 按钮自身被绑定折叠（例如「保存」只在没开自动保存时出现）属于页面逻辑，不在此断言；
        // 凡是可见的按钮，必须在视口内。
        var visible = buttons.Where(button => IsEffectivelyVisible(button)).ToList();
        foreach (var button in visible)
        {
            AssertInsideViewport(view, button, $"Footer button {button.Command?.GetType().Name ?? button.Name}");
        }

        return visible;
    }

    /// <summary>
    /// 断言正文放得下、不需要滚动：兜底 ScrollViewer 的 ScrollableHeight 为 0
    /// （设计目标是正文不出现滚动条；动态列表用固定高度内部滚动区，不算在这里）。
    /// </summary>
    public static void AssertBodyDoesNotScroll(SettingsPageHost page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var scrollViewer = page.BodyScrollViewer;
        Assert.NotNull(scrollViewer);
        Assert.True(
            scrollViewer!.ScrollableHeight <= 0.5,
            $"正文超出视口 {scrollViewer.ScrollableHeight:0.#}px，需要滚动才能看全（视口高 {scrollViewer.ViewportHeight:0.#}）。");
    }

    /// <summary>元素自身及所有可视祖先都是 Visible，且已有实际尺寸。</summary>
    public static bool IsEffectivelyVisible(UIElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return element is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 };
    }

    /// <summary>元素的边界必须落在首屏视口内（容差 1px）。</summary>
    public static void AssertInsideViewport(FrameworkElement view, FrameworkElement element, string description)
    {
        Assert.True(IsEffectivelyVisible(element), $"{description} 不可见或没有布局尺寸。");
        var bounds = element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));
        Assert.True(
            bounds.Left >= -Tolerance &&
            bounds.Top >= -Tolerance &&
            bounds.Right <= ViewportWidth + Tolerance &&
            bounds.Bottom <= ViewportHeight + Tolerance,
            $"{description} 超出 {ViewportWidth}×{ViewportHeight} 视口：{bounds}");
    }

    // ===================================================================================
    // 框架断言
    // ===================================================================================

    private static void AssertFrameLayout(SettingsView view)
    {
        // 根 Grid 三行：顶栏 52 / 内容 * / 状态条 28。
        var root = Assert.IsType<Grid>(view.Content);
        Assert.Equal(TopBarHeight, root.RowDefinitions[0].ActualHeight, 0.5);
        Assert.Equal(StatusBarHeight, root.RowDefinitions[2].ActualHeight, 0.5);
        Assert.Equal(ViewportHeight - TopBarHeight - StatusBarHeight, root.RowDefinitions[1].ActualHeight, 0.5);
    }

    private static void AssertNavigationFullyVisible(SettingsView view, SettingsViewModel vm)
    {
        var commands = new object[]
        {
            vm.SelectDataMaintenanceCommand,
            vm.SelectPaymentTerminalCommand,
            vm.SelectReceiptPrinterCommand,
            vm.SelectDeviceRegistrationCommand,
            vm.SelectRemoteMaintenanceCommand,
            vm.SelectMobileAppDownloadCommand
        };

        foreach (var command in commands)
        {
            var button = Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(view)
                .Where(candidate => ReferenceEquals(candidate.Command, command)));
            // 导航项固定 48 高，完整落在视口内。
            Assert.Equal(NavButtonHeight, button.ActualHeight, 0.5);
            AssertInsideViewport(view, button, "导航项");

            // 英文长标签只允许在项内换行（两行放得下），不能被截断：文字块高度不超过按钮内高度。
            var label = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(button)
                .First(text => text.TextWrapping == TextWrapping.Wrap);
            Assert.True(
                label.ActualHeight <= button.ActualHeight - 2 + Tolerance,
                $"导航文字 \"{label.Text}\" 高度 {label.ActualHeight:0.#} 放不进 {button.ActualHeight}px 高的导航项。");
        }
    }

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
}
