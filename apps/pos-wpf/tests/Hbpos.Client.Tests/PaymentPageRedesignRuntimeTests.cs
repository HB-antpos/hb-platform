using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Screens;

namespace Hbpos.Client.Tests;

[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class PaymentPageRedesignRuntimeTests(PaymentViewRuntimeStaTestHost host)
{
    [Theory]
    [InlineData(1366, 688, "en-US", false)]
    [InlineData(1366, 688, "zh-CN", false)]
    [InlineData(1024, 768, "en-US", false)]
    [InlineData(1024, 768, "zh-CN", false)]
    [InlineData(1366, 688, "en-US", true)]
    [InlineData(1366, 688, "zh-CN", true)]
    [InlineData(1024, 768, "en-US", true)]
    [InlineData(1024, 768, "zh-CN", true)]
    public Task Payment_workspace_keeps_touch_controls_in_bounds_and_card_modes_in_one_slot(
        int width, int height, string culture, bool manualAndVoucher) => host.RunAsync(async _ =>
    {
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(new ScriptedLinklyCloudApi(false));
        var localization = new LocalizationService();
        localization.SetCulture(culture);
        LocalizationResourceProvider.Instance.Configure(localization);
        var settings = new MutablePaymentMethodSettingsService(new PaymentMethodSettings(
            UseManualCard: manualAndVoucher, VoucherEnabled: manualAndVoucher));
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
            PaymentViewRuntimeStaTestHost.Realize(view, width, height);
            var cash = Find<Button>(view, "AddCashTenderButton");
            var card = Find<Button>(view, "AddCardTenderButton");
            var manual = Find<Button>(view, "ManualCardPaymentButton");
            var voucher = Find<Button>(view, "AddVoucherTenderButton");
            var confirm = Find<Button>(view, "ConfirmPaymentButton");
            var keypad = Find<FrameworkElement>(view, "PaymentNumberKeypad");
            var quickCash = Find<ItemsControl>(view, "PaymentQuickCashAmounts");
            Assert.Equal(manualAndVoucher ? Visibility.Collapsed : Visibility.Visible, card.Visibility);
            Assert.Equal(manualAndVoucher ? Visibility.Visible : Visibility.Collapsed, manual.Visibility);
            Assert.Equal(manualAndVoucher ? Visibility.Visible : Visibility.Collapsed, voucher.Visibility);
            var activeCard = manualAndVoucher ? manual : card;
            AssertInBounds(view, cash, width, height, 48);
            AssertInBounds(view, activeCard, width, height, 48);
            AssertInBounds(view, confirm, width, height, 48);
            AssertInBounds(view, quickCash, width, height, 48);
            Assert.Equal(Bounds(view, cash).Top, Bounds(view, activeCard).Top, 1);
            Assert.True(Bounds(view, cash).Right < Bounds(view, activeCard).Left);
            Assert.True(Bounds(view, confirm).Left > Bounds(view, keypad).Right,
                "完成付款应固定在右侧付款面板底部。");
            Assert.False(confirm.IsEnabled);
            var quantityHeader = Assert.Single(PaymentViewRuntimeStaTestHost
                .FindVisualDescendants<DataGridColumnHeader>(view)
                .Where(header => Equals(header.Content, localization.T("Quantity"))));
            var headerText = new FormattedText(localization.T("Quantity"),
                System.Globalization.CultureInfo.GetCultureInfo(culture), FlowDirection.LeftToRight,
                new Typeface(quantityHeader.FontFamily, quantityHeader.FontStyle,
                    quantityHeader.FontWeight, quantityHeader.FontStretch),
                quantityHeader.FontSize, Brushes.Black, 1);
            Assert.True(quantityHeader.ActualWidth >= headerText.Width +
                quantityHeader.Padding.Left + quantityHeader.Padding.Right,
                "数量表头必须完整显示，不能缩略成 Q.. 或 数.。");
            if (manualAndVoucher)
            {
                AssertInBounds(view, voucher, width, height, 48);
                Assert.True(Bounds(view, voucher).Top > Bounds(view, cash).Bottom);
                Assert.Equal(Bounds(view, cash).Left, Bounds(view, voucher).Left, 1);
                Assert.Equal(Bounds(view, activeCard).Right, Bounds(view, voucher).Right, 1);
            }

            var keys = PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(keypad).ToArray();
            Assert.Equal(12, keys.Length);
            foreach (var key in keys) AssertInBounds(view, key, width, height, 48);
            var notes = PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(quickCash).ToArray();
            Assert.Equal(5, notes.Length);
            foreach (var note in notes)
            {
                AssertInBounds(view, note, width, height, 48);
                Assert.Equal(Bounds(view, notes[0]).Top, Bounds(view, note).Top, 1);
            }
            var backToPos = Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<Button>(view)
                .Where(button => System.Windows.Data.BindingOperations
                    .GetBinding(button, ButtonBase.CommandProperty)?.Path?.Path == "BackToPosCommand"));
            var visibleInstallmentToggle = PaymentViewRuntimeStaTestHost.FindVisualDescendants<ToggleButton>(view)
                .Where(toggle => AutomationProperties.GetAutomationId(toggle) == "InstallmentPaymentToggle" && toggle.IsVisible);
            // 检查实际文字布局，防止英文卡片标签、清除键或放大后的返回/分期文字因固定宽度被裁切。
            foreach (var control in keys.Concat(notes).Concat([cash, activeCard, confirm, backToPos])
                         .Cast<FrameworkElement>()
                         .Concat(visibleInstallmentToggle))
            {
                foreach (var label in PaymentViewRuntimeStaTestHost.FindVisualDescendants<TextBlock>(control))
                {
                    var textBounds = Bounds(control, label);
                    Assert.True(textBounds.Left >= -1 && textBounds.Top >= -1 &&
                        textBounds.Right <= control.ActualWidth + 1 &&
                        textBounds.Bottom <= control.ActualHeight + 1,
                        $"{label.Text} exceeds its touch target: {textBounds}");
                }
            }

            SaveScreenshot(view, width, height, culture, manualAndVoucher);
            // 新布局仍使用原数字输入命令，不会在输入金额时触发终端或记账。
            Assert.Single(keys.Where(key => Equals(key.CommandParameter, "1"))).Command.Execute("1");
            Assert.Equal("1", vm.TenderAmountText);
            Assert.Single(keys.Where(key => Equals(key.CommandParameter, "Clear"))).Command.Execute("Clear");
            Assert.True(string.IsNullOrEmpty(vm.TenderAmountText));
            Assert.Empty(vm.PaymentTenders);
            Assert.Equal(0, fixture.CloudApi.SendCount);
        }
        finally
        {
            view.DataContext = null;
            localization.SetCulture("en-US");
        }
    });

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<T>(root)
            .Where(element => AutomationProperties.GetAutomationId(element) == id));

    private static Rect Bounds(FrameworkElement root, FrameworkElement element) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    private static void AssertInBounds(FrameworkElement root, FrameworkElement element,
        int width, int height, double minHeight)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement ui) Assert.Equal(Visibility.Visible, ui.Visibility);
        var bounds = Bounds(root, element);
        Assert.True(bounds.Left >= 0 && bounds.Top >= 0 &&
            bounds.Right <= width + 1 && bounds.Bottom <= height + 1,
            $"{AutomationProperties.GetAutomationId(element)} outside viewport: {bounds}");
        Assert.True(element.ActualHeight >= minHeight, $"Touch target height: {element.ActualHeight}");
    }

    private static void SaveScreenshot(FrameworkElement view, int width, int height,
        string culture, bool manualAndVoucher)
    {
        var output = Environment.GetEnvironmentVariable("HBPOS_PAYMENT_PAGE_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var mode = manualAndVoucher ? "manual-voucher" : "default";
        using var stream = File.Create(Path.Combine(output, $"payment-{width}-{height}-{culture}-{mode}.png"));
        encoder.Save(stream);
    }
}
