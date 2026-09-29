using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Screens;

namespace Hbpos.Client.Tests;

[Collection(WpfViewLifecycleTestCollection.Name)]
public sealed class PosTerminalSuccessCardRuntimeTests(PaymentViewRuntimeStaTestHost host)
{
    [Theory]
    [InlineData(1080, 720, "en-US")]
    [InlineData(1080, 720, "zh-CN")]
    [InlineData(1366, 768, "en-US")]
    [InlineData(1366, 768, "zh-CN")]
    public Task Success_card_fits_the_cart_area_with_change_card_details_and_warning(int width, int windowHeight, string culture) =>
        host.RunAsync(_ =>
        {
            var localization = new LocalizationService();
            localization.SetCulture(culture);
            LocalizationResourceProvider.Instance.Configure(localization);
            // 同时显示现金找零、卡信息与收尾告警，覆盖卡片内容最多的情况。
            var sale = new PaymentSuccessViewModel
            {
                TransactionId = Guid.Parse("a1b2c3d4-e5f6-4711-8899-aabbccddeeff"),
                TotalAmountPaid = 1234.56m,
                SoldAt = new DateTimeOffset(2026, 9, 26, 14, 45, 0, TimeSpan.FromHours(10)),
                TenderedAmount = 1300m,
                ChangeAmount = 65.44m,
                IsCashChangeVisible = true,
                CardPaymentSummary = "VISA **** 4242 · Approved",
                IsCardPaymentSummaryVisible = true,
                HasPostCommitWarning = true
            };
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

            foreach (var id in new[] { "CompletedTransactionId", "CompletedSaleTotalPaid", "CompletedSaleChangeDue" })
            {
                AssertInside(view, Find<TextBlock>(view, id), card);
            }

            Assert.Equal("$65.44", Find<TextBlock>(view, "CompletedSaleChangeDue").Text);
            Assert.Equal("$1234.56", Find<TextBlock>(view, "CompletedSaleTotalPaid").Text);
            AssertInside(view, Find<Border>(view, "PaymentCompletedWarning"), card);
            AssertInside(view, Find<Button>(view, "PaymentSuccessCardPrintButton"), card);
            AssertInside(view, Find<Button>(view, "PaymentSuccessCardCloseButton"), card);

            SaveScreenshot(view, width, (int)layoutSize.Height, culture);
            return Task.CompletedTask;
        });

    private static T Find<T>(DependencyObject root, string id)
        where T : FrameworkElement =>
        Assert.Single(PaymentViewRuntimeStaTestHost.FindVisualDescendants<T>(root)
            .Where(element => AutomationProperties.GetAutomationId(element) == id));

    private static void AssertInside(FrameworkElement root, FrameworkElement element, FrameworkElement container)
    {
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        var containerBounds = container.TransformToAncestor(root).TransformBounds(new Rect(container.RenderSize));
        containerBounds.Inflate(0.5, 0.5);
        Assert.True(
            containerBounds.Contains(bounds),
            $"{AutomationProperties.GetAutomationId(element)} {bounds} 超出 {containerBounds}。");
    }

    private static void SaveScreenshot(FrameworkElement view, int width, int height, string culture)
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
        using var stream = File.Create(Path.Combine(output, $"success-card-{width}-{height}-{culture}.png"));
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
