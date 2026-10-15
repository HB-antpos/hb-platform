using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Hbpos.Client.Wpf.Converters;

/// <summary>
/// 把倒计时剩余比例（0~1）换成圆环 Ellipse 的 StrokeDashArray：用「一段实线 + 足够长的空白」画出圆弧。
/// StrokeDashArray 的单位是笔画粗细的倍数，所以周长要除以粗细；参数 ConverterParameter 形如 "直径,粗细"，默认 132,10。
/// </summary>
public sealed class RestartCountdownDashConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var progress = value is double number ? Math.Clamp(number, 0d, 1d) : 0d;
        var (diameter, thickness) = ParseParameter(parameter);

        // Ellipse 的描边居中落在几何边缘，实际圆的直径 = 元素尺寸 - 粗细。
        var circumferenceInThickness = Math.PI * (diameter - thickness) / thickness;
        return new DoubleCollection { circumferenceInThickness * progress, circumferenceInThickness + 1d };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static (double Diameter, double Thickness) ParseParameter(object parameter)
    {
        if (parameter is string text)
        {
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var diameter) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var thickness) &&
                diameter > thickness &&
                thickness > 0)
            {
                return (diameter, thickness);
            }
        }

        return (132d, 10d);
    }
}
