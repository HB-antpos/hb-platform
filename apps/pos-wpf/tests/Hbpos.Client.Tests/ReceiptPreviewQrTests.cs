using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Hbpos.Client.Wpf.Converters;
using ZXing;
using ZXing.Common;

namespace Hbpos.Client.Tests;

public sealed class ReceiptPreviewQrTests
{
    [Fact]
    public void Converter_renders_frozen_qr_that_decodes_to_original_value()
    {
        var value = Guid.Parse("11111111-2222-3333-4444-555555555555").ToString("D");
        var converter = new ReceiptQrCodeImageSourceConverter();

        var image = Assert.IsType<BitmapImage>(
            converter.Convert(value, typeof(BitmapSource), null, CultureInfo.InvariantCulture));

        Assert.True(image.IsFrozen);
        Assert.Equal(value, DecodeQr(image));
    }

    [Fact]
    public void Converter_returns_safe_fallback_for_blank_or_unrenderable_value()
    {
        var converter = new ReceiptQrCodeImageSourceConverter();

        Assert.Same(
            DependencyProperty.UnsetValue,
            converter.Convert(" ", typeof(BitmapSource), null, CultureInfo.InvariantCulture));
        Assert.Same(
            DependencyProperty.UnsetValue,
            converter.Convert(new string('A', 10000), typeof(BitmapSource), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Order_receipt_preview_surfaces_use_shared_qr_template()
    {
        var wpfRoot = Path.Combine(FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf");
        var theme = XDocument.Load(Path.Combine(wpfRoot, "Themes", "PosTheme.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var template = Assert.Single(theme.Descendants(presentation + "DataTemplate").Where(element =>
            (string?)element.Attribute(x + "Key") == "ReceiptPreviewRowTemplate"));
        var image = Assert.Single(template.Descendants(presentation + "Image"));
        Assert.Contains("QrCodeValue", (string?)image.Attribute("Source"), StringComparison.Ordinal);
        Assert.Equal("260", (string?)image.Attribute("Width"));
        Assert.Equal("260", (string?)image.Attribute("Height"));
        Assert.Equal("None", (string?)image.Attribute("Stretch"));
        Assert.Equal("NearestNeighbor", (string?)image.Attribute("RenderOptions.BitmapScalingMode"));
        Assert.Null((string?)image.Attribute("AutomationProperties.Name"));
        Assert.Contains(
            template.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding Text}");

        AssertSharedTemplate(
            XDocument.Load(Path.Combine(wpfRoot, "Views", "Screens", "TransactionHistoryView.xaml")),
            presentation);
        AssertSharedTemplate(
            XDocument.Load(Path.Combine(wpfRoot, "MainWindow.xaml")),
            presentation);
    }

    private static void AssertSharedTemplate(XDocument document, XNamespace presentation)
    {
        var receiptPreview = Assert.Single(document.Descendants(presentation + "ItemsControl").Where(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding ReceiptPreviewRows}"));
        Assert.Equal(
            "{StaticResource ReceiptPreviewRowTemplate}",
            (string?)receiptPreview.Attribute("ItemTemplate"));
        Assert.Empty(receiptPreview.Elements(presentation + "ItemsControl.ItemTemplate"));
    }

    private static string? DecodeQr(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true }
        }.Decode(new RGBLuminanceSource(
            pixels,
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            RGBLuminanceSource.BitmapFormat.BGRA32))?.Text;
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "apps", "pos-wpf")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
