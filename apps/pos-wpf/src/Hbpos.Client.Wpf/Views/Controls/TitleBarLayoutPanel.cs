using System.Windows;
using System.Windows.Controls;

namespace Hbpos.Client.Wpf.Views.Controls;

/// <summary>
/// 标题栏布局：按子元素的 HorizontalAlignment 分为左、中、右三组。左右两组贴边；
/// 中间标题优先按整栏居中，居中会碰到两侧控件时向空闲一侧平移，仍放不下才压缩宽度（配合 TextTrimming 省略）。
/// 小屏（1024×768 缩放后逻辑宽 1280）上英文长标题不再与右侧门店、收银员信息重叠。
/// </summary>
public sealed class TitleBarLayoutPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(TitleBarLayoutPanel),
        new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var sideWidth = 0d;
        var height = 0d;
        foreach (UIElement child in InternalChildren)
        {
            if (IsCenter(child))
            {
                continue;
            }

            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            sideWidth += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        // 标题只能用两侧控件之外的空间，超出时由 TextTrimming 省略。
        var centerAvailable = double.IsPositiveInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0d, availableSize.Width - sideWidth - (2 * Spacing));
        var centerWidth = 0d;
        foreach (UIElement child in InternalChildren)
        {
            if (!IsCenter(child))
            {
                continue;
            }

            child.Measure(new Size(centerAvailable, availableSize.Height));
            centerWidth = Math.Max(centerWidth, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var width = sideWidth + centerWidth + (centerWidth > 0d ? 2 * Spacing : 0d);
        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var leftEdge = 0d;
        var rightEdge = finalSize.Width;
        foreach (UIElement child in InternalChildren)
        {
            var alignment = GetAlignment(child);
            if (alignment == HorizontalAlignment.Left)
            {
                child.Arrange(new Rect(leftEdge, 0d, child.DesiredSize.Width, finalSize.Height));
                leftEdge += child.DesiredSize.Width;
            }
            else if (alignment == HorizontalAlignment.Right)
            {
                rightEdge -= child.DesiredSize.Width;
                child.Arrange(new Rect(rightEdge, 0d, child.DesiredSize.Width, finalSize.Height));
            }
        }

        foreach (UIElement child in InternalChildren)
        {
            if (!IsCenter(child))
            {
                continue;
            }

            var (x, width) = PlaceCenter(finalSize.Width, leftEdge, rightEdge, child.DesiredSize.Width, Spacing);
            child.Arrange(new Rect(x, 0d, width, finalSize.Height));
        }

        return finalSize;
    }

    internal static (double X, double Width) PlaceCenter(
        double totalWidth,
        double leftEdge,
        double rightEdge,
        double desiredWidth,
        double spacing)
    {
        var minX = leftEdge + spacing;
        var maxRight = rightEdge - spacing;
        var width = Math.Max(0d, Math.Min(desiredWidth, maxRight - minX));
        var x = (totalWidth - width) / 2d;
        x = Math.Min(x, maxRight - width);
        x = Math.Max(x, minX);
        return (x, width);
    }

    private static bool IsCenter(UIElement child) =>
        GetAlignment(child) is not (HorizontalAlignment.Left or HorizontalAlignment.Right);

    private static HorizontalAlignment GetAlignment(UIElement child) =>
        child is FrameworkElement element ? element.HorizontalAlignment : HorizontalAlignment.Center;
}
