using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Views.Screens;

namespace Hbpos.Client.Tests;

[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class PosTerminalButtonFontRuntimeTests(PaymentViewRuntimeStaTestHost host)
{
    [Theory]
    [InlineData(1080, 720, "en-US")]
    [InlineData(1080, 720, "zh-CN")]
    [InlineData(1366, 768, "en-US")]
    [InlineData(1366, 768, "zh-CN")]
    public Task Main_page_button_text_renders_at_enlarged_size_and_fits(int width, int windowHeight, string culture) =>
        host.RunAsync(_ =>
        {
            var localization = new LocalizationService();
            localization.SetCulture(culture);
            LocalizationResourceProvider.Instance.Configure(localization);
            var view = new PosTerminalView
            {
                // 付款与加入按钮文字来自 ViewModel；这里只提供文案，验证放大后仍放得下。
                DataContext = new ButtonTextProbe(
                    LocalizationResourceProvider.Instance["pos.terminal.payNow"],
                    LocalizationResourceProvider.Instance["pos.terminal.search.action"])
            };
            var layoutSize = new Size(width, windowHeight - 54 - 42);
            PaymentViewRuntimeStaTestHost.Realize(view, layoutSize.Width, layoutSize.Height);
            PaymentViewRuntimeStaTestHost.Realize(view, layoutSize.Width, layoutSize.Height);

            var keypad = Assert.IsType<UniformGrid>(view.FindName("CashierKeypad"));
            foreach (var key in keypad.Children.OfType<ButtonBase>())
            {
                var parameter = (key as Button)?.CommandParameter as string;
                var expected = parameter switch
                {
                    "Clear" or "QuickHalf" or "QuickNinetyNine" => 18d,
                    null => 16d,
                    _ => 28d
                };
                AssertButtonText(key, expected);
            }

            var discountButtons = ButtonsWithCommand(view, "ApplyQuickDiscountPercentCommand");
            Assert.Equal(5, discountButtons.Count);
            discountButtons.ForEach(button => AssertButtonText(button, 18));

            foreach (var command in new[]
                     {
                         "ModifySelectedLineQuantityCommand",
                         "ModifySelectedLinePriceCommand",
                         "ApplySelectedLineDiscountAmountCommand",
                         "ApplySelectedLineDiscountPercentCommand"
                     })
            {
                AssertButtonText(Assert.Single(ButtonsWithCommand(view, command)), 16);
            }

            AssertButtonText(Assert.Single(ButtonsWithCommand(view, "AddOpenItemCommand")), 18);
            AssertButtonText(Assert.Single(ButtonsWithCommand(view, "OpenPaymentCommand")), 22);
            AssertButtonText(Assert.Single(ButtonsWithCommand(view, "ScanCommand")), 16);

            var sidebarLabelStyle = (Style)view.FindResource("PosSidebarActionLabelStyle");
            var sidebarLabels = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(view)
                .Where(text => ReferenceEquals(text.Style, sidebarLabelStyle))
                .ToList();
            Assert.Equal(10, sidebarLabels.Count);
            foreach (var label in sidebarLabels)
            {
                Assert.Equal(13, label.FontSize);
                var tile = FindAncestor<ButtonBase>(label);
                AssertInside(FindAncestor<StackPanel>(label), tile);
                // 两行英文标签必须完整显示，不能被 MaxHeight 截掉第二行。
                Assert.True(
                    MeasureWrappedHeight(label) <= label.MaxHeight + 0.5,
                    $"{label.Text} 在 {width}×{windowHeight} {culture} 下超出 {label.MaxHeight} 高。");
            }

            return Task.CompletedTask;
        });

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public Task Touch_keyboard_keys_render_enlarged_text_inside_each_key(string culture) =>
        host.RunAsync(_ =>
        {
            var localization = new LocalizationService();
            localization.SetCulture(culture);
            LocalizationResourceProvider.Instance.Configure(localization);
            var view = new PosTerminalView
            {
                DataContext = new ButtonTextProbe(
                    LocalizationResourceProvider.Instance["pos.terminal.payNow"],
                    LocalizationResourceProvider.Instance["pos.terminal.search.action"])
            };
            var popup = Assert.IsType<Popup>(view.FindName("TouchKeyboardPopup"));
            var keyboard = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
            keyboard.DataContext = view.DataContext;
            PaymentViewRuntimeStaTestHost.Realize(keyboard, 620, 400);

            var keys = PaymentViewRuntimeStaTestHost.FindVisualDescendants<ButtonBase>(keyboard).ToList();
            Assert.Equal(41, keys.Count);
            foreach (var key in keys)
            {
                var parameter = (key as Button)?.CommandParameter as string;
                var expected = parameter == "Enter" ? 16d : 20d;
                AssertButtonText(key, expected);
            }

            return Task.CompletedTask;
        });

    private static void AssertButtonText(ButtonBase button, double expectedFontSize)
    {
        var texts = PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(button)
            .Where(text => !string.IsNullOrEmpty(text.Text))
            .ToList();
        Assert.NotEmpty(texts);
        foreach (var text in texts)
        {
            Assert.True(
                Math.Abs(text.FontSize - expectedFontSize) < 0.01,
                $"「{text.Text}」实际显示 {text.FontSize}，应为 {expectedFontSize}。");
            AssertInside(text, button);
        }
    }

    private static List<ButtonBase> ButtonsWithCommand(FrameworkElement view, string commandPath)
    {
        return PaymentViewRuntimeStaTestHost.FindVisualDescendants<ButtonBase>(view)
            .Where(button =>
                System.Windows.Data.BindingOperations.GetBinding(button, ButtonBase.CommandProperty)?.Path?.Path == commandPath)
            .ToList();
    }

    private static T FindAncestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(element);
        while (current is not null and not T)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        return Assert.IsAssignableFrom<T>(current);
    }

    private static void AssertInside(FrameworkElement element, FrameworkElement container)
    {
        var bounds = element.TransformToAncestor(container).TransformBounds(new Rect(element.RenderSize));
        var containerBounds = new Rect(container.RenderSize);
        containerBounds.Inflate(0.5, 0.5);
        Assert.True(
            containerBounds.Contains(bounds),
            $"「{(element as TextBlock)?.Text ?? element.Name}」{bounds} 超出按钮 {containerBounds}。");
    }

    private static double MeasureWrappedHeight(TextBlock label)
    {
        var formatted = new FormattedText(
            label.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
            label.FontSize,
            Brushes.Black,
            1.0)
        {
            MaxTextWidth = Math.Min(label.MaxWidth, label.ActualWidth > 0 ? label.ActualWidth : label.MaxWidth),
            LineHeight = double.IsNaN(label.LineHeight) ? 0 : label.LineHeight
        };
        return formatted.Height;
    }

    private sealed record ButtonTextProbe(string PayNowText, string SearchButtonText);
}
