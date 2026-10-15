using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hbpos.Client.Wpf.Views.Controls;

/// <summary>
/// 单行按权重分配宽度的面板：第 i 个子元素宽度 = 总宽 × Weights[i] / 权重之和，所有子元素等高（占满面板高度）。
/// 付款页「快捷现金」五个面额按钮用它按 1 : .956 : .911 : .867 : .82 分配宽度（$100 最宽、$5 最窄）。
/// Weights 缺项或非正数时按 1 处理；未设置 Weights 时退化为等分，行为与 UniformGrid 单行一致。
/// </summary>
public sealed class WeightedRowPanel : Panel
{
    public static readonly DependencyProperty WeightsProperty = DependencyProperty.Register(
        nameof(Weights),
        typeof(DoubleCollection),
        typeof(WeightedRowPanel),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>各子元素的宽度权重（按子元素顺序）。</summary>
    public DoubleCollection? Weights
    {
        get => (DoubleCollection?)GetValue(WeightsProperty);
        set => SetValue(WeightsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
        {
            return new Size(0d, 0d);
        }

        var weights = ResolveWeights(children.Count, out var totalWeight);
        var widthIsInfinite = double.IsPositiveInfinity(availableSize.Width);
        var width = 0d;
        var height = 0d;
        for (var index = 0; index < children.Count; index++)
        {
            // 宽度无限（放在 Auto 容器里）时让子元素自己决定宽度，整行宽度取各子元素之和。
            var slot = widthIsInfinite
                ? double.PositiveInfinity
                : availableSize.Width * weights[index] / totalWeight;
            children[index].Measure(new Size(slot, availableSize.Height));
            height = Math.Max(height, children[index].DesiredSize.Height);
            width += widthIsInfinite ? children[index].DesiredSize.Width : slot;
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
        {
            return finalSize;
        }

        var weights = ResolveWeights(children.Count, out var totalWeight);
        var left = 0d;
        for (var index = 0; index < children.Count; index++)
        {
            var slot = finalSize.Width * weights[index] / totalWeight;
            children[index].Arrange(new Rect(left, 0d, slot, finalSize.Height));
            left += slot;
        }

        return finalSize;
    }

    private double[] ResolveWeights(int count, out double totalWeight)
    {
        var configured = Weights;
        var resolved = new double[count];
        totalWeight = 0d;
        for (var index = 0; index < count; index++)
        {
            var weight = configured is not null && index < configured.Count ? configured[index] : 1d;
            // 非正数、NaN 与无穷大都当作 1，避免除零或整行塌陷。
            resolved[index] = weight > 0d && !double.IsInfinity(weight) ? weight : 1d;
            totalWeight += resolved[index];
        }

        return resolved;
    }
}
