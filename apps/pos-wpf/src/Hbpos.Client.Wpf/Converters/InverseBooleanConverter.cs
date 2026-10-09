using System.Globalization;
using System.Windows.Data;

namespace Hbpos.Client.Wpf.Converters;

/// <summary>
/// 布尔取反（双向）。用于把单个布尔开关拆成一对分段单选按钮，例如“正式 / 测试”环境。
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is not true;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is not true;
    }
}
