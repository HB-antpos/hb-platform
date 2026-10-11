using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.Converters;

public sealed class ReceiptQrCodeImageSourceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string payload || string.IsNullOrWhiteSpace(payload))
        {
            return DependencyProperty.UnsetValue;
        }

        // ConverterParameter 可指定二维码画布像素（例如放大扫码用更大的尺寸）；不给就沿用 260px。
        var containerPixels = parameter switch
        {
            int pixels when pixels > 0 => pixels,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pixels) && pixels > 0 => pixels,
            _ => AttendanceQrPngRenderer.ContainerPixels
        };

        try
        {
            var rendered = AttendanceQrPngRenderer.Render(payload, containerPixels);
            using var stream = new MemoryStream(rendered.PngBytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return DependencyProperty.UnsetValue;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
