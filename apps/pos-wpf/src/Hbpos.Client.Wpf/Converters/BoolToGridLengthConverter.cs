using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Hbpos.Client.Wpf.Converters;

/// <summary>
/// 布尔值转 <see cref="GridLength"/>：true 取 <see cref="TrueValue"/>，false 取 <see cref="FalseValue"/>。
/// 用于 ColumnDefinition / RowDefinition 的宽高随开关切换（例如付款页隐藏订单列表时左列收窄）。
/// 源值缺失（DataContext 尚未就绪）时按 true 处理，即保持默认布局。
/// </summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public GridLength TrueValue { get; set; } = new(1, GridUnitType.Star);

    public GridLength FalseValue { get; set; } = new(1, GridUnitType.Star);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 只有明确为 false 才用 FalseValue，其余（含 null / UnsetValue）一律走默认的 TrueValue。
        return value is false ? FalseValue : TrueValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
