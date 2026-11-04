using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Screens;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class PosTerminalSuccessCardRuntimeTests(PaymentViewRuntimeStaTestHost host)
{
    private static readonly Color CashToCustomerBackground = (Color)ColorConverter.ConvertFromString("#FBBF24");

    [Theory]
    [InlineData("cash-change", 1080, 720, "en-US")]
    [InlineData("cash-change", 1366, 768, "zh-CN")]
    [InlineData("mixed-change-warning", 1080, 720, "en-US")]
    [InlineData("mixed-change-warning", 1080, 720, "zh-CN")]
    [InlineData("mixed-change-warning", 1366, 768, "en-US")]
    [InlineData("mixed-change-warning", 1366, 768, "zh-CN")]
    [InlineData("refund-cash", 1080, 720, "zh-CN")]
    [InlineData("refund-cash", 1366, 768, "en-US")]
    [InlineData("refund-voucher", 1080, 720, "en-US")]
    [InlineData("refund-voucher", 1366, 768, "zh-CN")]
    public Task Success_card_fits_the_cart_area_and_puts_the_hero_amount_first(
        string scenario,
        int width,
        int windowHeight,
        string culture) =>
        host.RunAsync(_ =>
        {
            var localization = new LocalizationService();
            localization.SetCulture(culture);
            LocalizationResourceProvider.Instance.Configure(localization);
            var (sale, heroAmount, summaryAmount, cashToCustomer) = CreateScenario(scenario);
            var view = new PosTerminalView
            {
                DataContext = new SuccessCardProbe(
                    sale,
                    LocalizationResourceProvider.Instance["pos.terminal.payNow"],
                    LocalizationResourceProvider.Instance["pos.terminal.search.action"])
            };
            var layoutSize = new Size(width, windowHeight - 54 - 42);
            PaymentViewRuntimeStaTestHost.Realize(view, layoutSize.Width, layoutSize.Height);
            PaymentViewRuntimeStaTestHost.Realize(view, layoutSize.Width, layoutSize.Height);

            var card = Find<Border>(view, "PaymentSuccessCard");
            Assert.Equal(Visibility.Visible, card.Visibility);
            var cartGrid = Find<DataGrid>(view, "CartItemsGrid");
            var cartHost = Assert.IsAssignableFrom<FrameworkElement>(cartGrid.Parent);
            AssertInside(view, card, cartHost);

            foreach (var id in new[] { "CompletedTransactionId", "CompletedSaleTotalPaid", "CompletedSaleChangeDue", "PaymentSuccessHero" })
            {
                AssertInside(view, Find<FrameworkElement>(view, id), card);
            }

            Assert.Equal(heroAmount, Find<TextBlock>(view, "CompletedSaleChangeDue").Text);
            Assert.Equal(summaryAmount, Find<TextBlock>(view, "CompletedSaleTotalPaid").Text);
            // 中文注释：要给顾客现金（找零、退现金）统一黄底红字，主块金额必须比次要金额大得多。
            var hero = Find<Border>(view, "PaymentSuccessHero");
            Assert.Equal(cashToCustomer, ((SolidColorBrush)hero.Background).Color == CashToCustomerBackground);
            Assert.True(
                Find<TextBlock>(view, "CompletedSaleChangeDue").FontSize >= 3 * Find<TextBlock>(view, "CompletedSaleTotalPaid").FontSize,
                "主块金额要远大于应收等次要金额。");
            if (sale.HasPostCommitWarning)
            {
                AssertInside(view, Find<Border>(view, "PaymentCompletedWarning"), card);
            }

            var printButton = Find<Button>(view, "PaymentSuccessCardPrintButton");
            AssertInside(view, printButton, card);
            // 中文注释：触屏大按钮：设计高度 80，卡片缩小时实际高度也不能低于 60。
            Assert.Equal(80, printButton.ActualHeight, 1);
            Assert.True(RenderedHeight(view, printButton) >= 60, $"打印按钮实际只有 {RenderedHeight(view, printButton):0} 像素高。");
            // 点打印后焦点不能停在按钮上，否则扫码枪尾部的 Enter 没被吃掉时会再次触发打印。
            Assert.False(printButton.Focusable);
            Assert.False(printButton.IsTabStop);
            AssertInside(view, Find<Button>(view, "PaymentSuccessCardCloseButton"), card);

            SaveScreenshot(view, width, (int)layoutSize.Height, culture, scenario);
            return Task.CompletedTask;
        });

    private static (PaymentSuccessViewModel Sale, string HeroAmount, string SummaryAmount, bool CashToCustomer) CreateScenario(string scenario)
    {
        var sale = new PaymentSuccessViewModel
        {
            TransactionId = Guid.Parse("a1b2c3d4-e5f6-4711-8899-aabbccddeeff"),
            SoldAt = new DateTimeOffset(2026, 9, 26, 14, 45, 0, TimeSpan.FromHours(10))
        };
        switch (scenario)
        {
            case "cash-change":
                sale.CardSummary = PaymentSuccessCardSummary.Create(PaymentSuccessCardSummaryTests.CreateReceipt(
                    1234.56m,
                    [new ReceiptPaymentLine(PaymentMethodKind.Cash, 1234.56m, null)],
                    tendered: 1300m,
                    change: 65.44m));
                return (sale, "$65.44", "$1234.56", true);
            case "mixed-change-warning":
                // 中文注释：混合支付 + 卡信息 + 收尾告警，覆盖卡片内容最多的情况。
                sale.CardSummary = PaymentSuccessCardSummary.Create(PaymentSuccessCardSummaryTests.CreateReceipt(
                    186m,
                    [
                        new ReceiptPaymentLine(PaymentMethodKind.Card, 100m, null),
                        new ReceiptPaymentLine(PaymentMethodKind.Voucher, 20m, "VOUCHER:ABC"),
                        new ReceiptPaymentLine(PaymentMethodKind.Cash, 66m, null)
                    ],
                    tendered: 80m,
                    change: 14m));
                sale.CardPaymentSummary = "VISA **** 4242 · Approved";
                sale.IsCardPaymentSummaryVisible = true;
                sale.HasPostCommitWarning = true;
                return (sale, "$14.00", "$186.00", true);
            case "refund-cash":
                sale.CardSummary = PaymentSuccessCardSummary.Create(PaymentSuccessCardSummaryTests.CreateReceipt(
                    -35.9m,
                    [new ReceiptPaymentLine(PaymentMethodKind.Cash, -35.9m, null)],
                    lines: [new ReceiptPreviewLine("Tea", "930001", 2m, 17.95m, 0m, -35.9m)]));
                return (sale, "$35.90", "$35.90", true);
            default:
                sale.CardSummary = PaymentSuccessCardSummary.Create(PaymentSuccessCardSummaryTests.CreateReceipt(
                    -58m,
                    [new ReceiptPaymentLine(PaymentMethodKind.Voucher, -58m, "VOUCHER_REFUND:RV7D3E10F2A41B")],
                    lines: [new ReceiptPreviewLine("Tea", "930001", 3m, 19.34m, 0m, -58m)],
                    refundVoucher: new RefundVoucherReceipt("RV7D3E10F2A41B", 58m)));
                return (sale, "$58.00", "$58.00", false);
        }
    }

    private static T Find<T>(DependencyObject root, string id)
        where T : FrameworkElement =>
        Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<T>(root)
            .Where(element => AutomationProperties.GetAutomationId(element) == id));

    private static double RenderedHeight(FrameworkElement root, FrameworkElement element) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)).Height;

    private static void AssertInside(FrameworkElement root, FrameworkElement element, FrameworkElement container)
    {
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        var containerBounds = container.TransformToAncestor(root).TransformBounds(new Rect(container.RenderSize));
        containerBounds.Inflate(0.5, 0.5);
        Assert.True(
            containerBounds.Contains(bounds),
            $"{AutomationProperties.GetAutomationId(element)} {bounds} 超出 {containerBounds}。");
    }

    private static void SaveScreenshot(FrameworkElement view, int width, int height, string culture, string scenario)
    {
        var output = Environment.GetEnvironmentVariable("HBPOS_SUCCESS_CARD_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, $"success-card-{scenario}-{width}-{height}-{culture}.png"));
        encoder.Save(stream);
    }

    private sealed class SuccessCardProbe(PaymentSuccessViewModel sale, string payNowText, string searchButtonText)
    {
        public PaymentSuccessViewModel LastSale { get; } = sale;

        public bool IsLastSaleVisible => true;

        public IRelayCommand DismissLastSaleCommand { get; } = new RelayCommand(() => { });

        public string PayNowText { get; } = payNowText;

        public string SearchButtonText { get; } = searchButtonText;
    }
}
