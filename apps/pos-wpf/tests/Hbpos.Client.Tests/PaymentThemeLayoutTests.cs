using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Hbpos.Client.Wpf.Converters;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Controls;
using Hbpos.Client.Wpf.Views.Screens;

namespace Hbpos.Client.Tests;

/// <summary>
/// 付款页「订单列表开关」「快捷现金权重面板」「尺寸字典」相关布局测试。
/// </summary>
[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class PaymentThemeLayoutTests(PaymentViewRuntimeStaTestHost host)
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Bool_to_grid_length_converter_uses_false_value_only_for_an_explicit_false()
    {
        var converter = new BoolToGridLengthConverter
        {
            TrueValue = new GridLength(28, GridUnitType.Star),
            FalseValue = new GridLength(22, GridUnitType.Star)
        };

        Assert.Equal(new GridLength(28, GridUnitType.Star), converter.Convert(true, typeof(GridLength), null, null!));
        Assert.Equal(new GridLength(22, GridUnitType.Star), converter.Convert(false, typeof(GridLength), null, null!));
        // DataContext 尚未就绪（null / UnsetValue）时保持显示订单列表的默认布局。
        Assert.Equal(new GridLength(28, GridUnitType.Star), converter.Convert(null, typeof(GridLength), null, null!));
        Assert.Equal(
            new GridLength(28, GridUnitType.Star),
            converter.Convert(DependencyProperty.UnsetValue, typeof(GridLength), null, null!));
    }

    [Fact]
    public Task Weighted_row_panel_splits_width_by_weights_and_gives_every_child_the_full_height() => host.RunAsync(_ =>
    {
        var panel = new WeightedRowPanel { Weights = new DoubleCollection([1d, 0.956d, 0.911d, 0.867d, 0.82d]) };
        var children = Enumerable.Range(0, 5).Select(index => new Border { Background = Brushes.Transparent }).ToArray();
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        PaymentViewRuntimeStaTestHost.Realize(panel, 1000, 84);

        var totalWeight = 1d + 0.956d + 0.911d + 0.867d + 0.82d;
        var expectedWeights = new[] { 1d, 0.956d, 0.911d, 0.867d, 0.82d };
        var left = 0d;
        for (var index = 0; index < children.Length; index++)
        {
            var expectedWidth = 1000d * expectedWeights[index] / totalWeight;
            Assert.Equal(expectedWidth, children[index].ActualWidth, 1);
            Assert.Equal(left, children[index].TranslatePoint(new Point(0, 0), panel).X, 1);
            Assert.Equal(84d, children[index].ActualHeight, 1);
            left += expectedWidth;
        }

        // 面额从大到小，宽度也必须严格递减（$100 最宽、$5 最窄）。
        Assert.True(children.Zip(children.Skip(1), (wider, narrower) => wider.ActualWidth > narrower.ActualWidth).All(ok => ok));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Order_list_switch_resizes_columns_and_swaps_the_table_for_the_big_due_amount(bool showOrderList) =>
        host.RunAsync(async _ =>
        {
            await using var fixture = await PaymentFlowTestFixture.CreateAsync(new ScriptedLinklyCloudApi(false));
            var localization = new LocalizationService();
            localization.SetCulture("en-US");
            LocalizationResourceProvider.Instance.Configure(localization);
            var settings = new MutablePaymentMethodSettingsService(new PaymentMethodSettings(ShowOrderList: showOrderList));
            using var vm = new PaymentViewModel(fixture.CreateSaleCart(10.03m), fixture.Workflow,
                fixture.Session, localization, paymentMethodSettingsService: settings);
            await vm.RefreshPaymentMethodSettingsAsync();
            var view = new PaymentView { DataContext = vm };
            view.Resources.MergedDictionaries.Add(new MaterialDesignThemes.Wpf.BundledTheme
            {
                BaseTheme = MaterialDesignThemes.Wpf.BaseTheme.Light,
                PrimaryColor = MaterialDesignColors.PrimaryColor.Blue,
                SecondaryColor = MaterialDesignColors.SecondaryColor.Amber
            });
            try
            {
                PaymentViewRuntimeStaTestHost.Realize(view, 1366, 688);

                Assert.Equal(showOrderList, vm.IsOrderListVisible);
                var root = Assert.IsType<Grid>(view.FindName("PaymentLayoutRoot"));
                // 显示订单列表 28/42/30；隐藏时左列收窄为 22/44/34。
                var expected = showOrderList ? new[] { 28d, 42d, 30d } : new[] { 22d, 44d, 34d };
                for (var column = 0; column < 3; column++)
                {
                    Assert.Equal(expected[column] / 100d, root.ColumnDefinitions[column].ActualWidth / root.ActualWidth, 2);
                }

                var table = Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<DataGrid>(view)
                    .Where(grid => AutomationProperties.GetAutomationId(grid) == "PaymentCartItemsGrid"));
                Assert.Equal(showOrderList ? Visibility.Visible : Visibility.Collapsed, table.Visibility);

                // 「Amount Due」在界面里有两处：汇总区的应付行，以及隐藏列表时中央的大号应付金额；任何时候只显示其中一处。
                var dueLabels = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(view)
                    .Where(text => text.Text == localization.T("AmountDue"))
                    .ToArray();
                Assert.Equal(2, dueLabels.Length);
                // 离屏测试没有 PresentationSource，IsVisible 恒为 false；改沿视觉树检查各级 Visibility。
                var visibleDue = Assert.Single(dueLabels.Where(IsEffectivelyVisible));
                var subtotal = Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(view)
                    .Where(text => text.Text == localization.T("Subtotal")));
                var dueTop = visibleDue.TranslatePoint(new Point(0, 0), view).Y;
                var subtotalTop = subtotal.TranslatePoint(new Point(0, 0), view).Y;
                // 显示列表：应付在汇总区最下方；隐藏列表：大号应付在小计之上，小计/折扣仍保留。
                Assert.Equal(showOrderList, dueTop > subtotalTop);
                Assert.True(IsEffectivelyVisible(subtotal));
            }
            finally
            {
                view.DataContext = null;
                localization.SetCulture("en-US");
            }
        });

    private static bool IsEffectivelyVisible(UIElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible)
            {
                return false;
            }
        }

        return true;
    }

    [Fact]
    public void Quick_cash_keeps_its_automation_id_and_uses_the_weighted_panel_with_five_weights()
    {
        var document = XDocument.Load(PaymentViewPath());
        var quickCash = Assert.Single(document.Descendants(Presentation + "ItemsControl").Where(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "PaymentQuickCashAmounts"));

        var panel = Assert.Single(quickCash.Descendants().Where(element => element.Name.LocalName == "WeightedRowPanel"));
        Assert.Equal("1 0.956 0.911 0.867 0.82", (string?)panel.Attribute("Weights"));
        Assert.Equal("{Binding QuickCashAmounts}", (string?)quickCash.Attribute("ItemsSource"));
        Assert.Equal(
            "{Binding DataContext.QuickCashCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}",
            (string?)quickCash.Descendants(Presentation + "Button").Single().Attribute("Command"));
    }

    [Fact]
    public void Payment_metrics_elder_keys_are_a_subset_of_standard_and_cover_every_referenced_size_key()
    {
        var metricsFolder = Path.Combine(WpfRoot(), "Themes", "Metrics");
        var standard = MetricKeys(Path.Combine(metricsFolder, "Payment.Standard.xaml"));
        var elder = MetricKeys(Path.Combine(metricsFolder, "Payment.Elder.xaml"));
        elder.Remove("PosMetricsElderId");

        Assert.True(elder.IsSubsetOf(standard), "长者字典里的键必须都在标准字典中：" + string.Join(", ", elder.Except(standard)));

        var referenced = Regex.Matches(File.ReadAllText(PaymentViewPath()), @"\{DynamicResource (PosPay\w+)\}")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(referenced);
        Assert.True(referenced.IsSubsetOf(standard), "PaymentView 引用了标准字典里不存在的尺寸键：" + string.Join(", ", referenced.Except(standard)));
    }

    private static HashSet<string> MetricKeys(string path) =>
        XDocument.Load(path).Root!.Elements()
            .Select(element => (string?)element.Attribute(Xaml + "Key"))
            .Where(key => key is not null)
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);

    private static string PaymentViewPath() => Path.Combine(WpfRoot(), "Views", "Screens", "PaymentView.xaml");

    private static string WpfRoot() => Path.Combine(FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf");

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, "hb-platform.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to find repository root.");
    }
}
