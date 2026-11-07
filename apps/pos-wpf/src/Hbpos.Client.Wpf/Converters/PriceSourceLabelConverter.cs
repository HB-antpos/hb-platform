using System.Globalization;
using System.Windows.Data;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.Converters;

/// <summary>
/// 把目录的价格来源标签（set / store-retail 等原始值）转成本地化的"主条码 / 套装条码"等文字。
/// 未识别的标签原样显示。映射规则见 <see cref="PriceSourceLabelKeys"/>。
/// </summary>
public sealed class PriceSourceLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var label = value as string;
        var key = PriceSourceLabelKeys.ResolveKey(label);
        return key is null
            ? label ?? string.Empty
            : LocalizationResourceProvider.Instance[key];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
