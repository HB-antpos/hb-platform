using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hbpos.Client.Wpf.Views.Controls;

/// <summary>
/// 设置页底部固定操作条里的水平排布面板（放进 <see cref="SettingsPageHost.Footer"/>）。
/// 约定：
/// 1. 子元素从左到右按文档顺序排列，相邻的「可见」子元素之间固定间距 <see cref="Spacing"/>（默认 10）；折叠的子元素不占间距。
/// 2. 子元素设 <c>SettingsFooterBar.IsFill="True"</c> 时占满左右两组之间的剩余宽度（通常给状态文字，配合 TextTrimming）。
/// 3. 子元素设 <c>SettingsFooterBar.IsRight="True"</c> 时靠右排列（右组内保持文档顺序，整体贴右边缘）。
/// 4. 危险操作之间用 <c>&lt;Border Style="{StaticResource SettingsFooterDividerStyle}" /&gt;</c> 的 1px 竖分隔线隔开，
///    分隔线本身也是一个子元素，所以左右各自占一个 Spacing。
/// 所有子元素在面板高度内垂直居中。
/// </summary>
public sealed class SettingsFooterBar : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(SettingsFooterBar),
        new FrameworkPropertyMetadata(
            10d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty IsRightProperty = DependencyProperty.RegisterAttached(
        "IsRight",
        typeof(bool),
        typeof(SettingsFooterBar),
        new FrameworkPropertyMetadata(false, OnChildLayoutPropertyChanged));

    public static readonly DependencyProperty IsFillProperty = DependencyProperty.RegisterAttached(
        "IsFill",
        typeof(bool),
        typeof(SettingsFooterBar),
        new FrameworkPropertyMetadata(false, OnChildLayoutPropertyChanged));

    /// <summary>相邻可见子元素之间的水平间距。</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static bool GetIsRight(DependencyObject element) => (bool)element.GetValue(IsRightProperty);

    public static void SetIsRight(DependencyObject element, bool value) => element.SetValue(IsRightProperty, value);

    public static bool GetIsFill(DependencyObject element) => (bool)element.GetValue(IsFillProperty);

    public static void SetIsFill(DependencyObject element, bool value) => element.SetValue(IsFillProperty, value);

    private static void OnChildLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 子元素的靠右 / 填充标记变化后，父面板需要重新测量排布。
        if (VisualTreeHelper.GetParent(d) is SettingsFooterBar parent)
        {
            parent.InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToList();
        var spacing = Math.Max(0d, Spacing);
        var fixedWidth = 0d;
        var height = 0d;

        // 先量非填充子元素（宽度不限，取自然宽度）。
        foreach (var child in visible.Where(child => !GetIsFill(child)))
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            fixedWidth += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var gaps = Math.Max(0, visible.Count - 1) * spacing;
        var fillWidth = 0d;
        var fillChildren = visible.Where(GetIsFill).ToList();
        foreach (var child in fillChildren)
        {
            // 填充子元素拿到剩余宽度；可用宽度无限时取自然宽度。
            var remaining = double.IsPositiveInfinity(availableSize.Width)
                ? double.PositiveInfinity
                : Math.Max(0d, availableSize.Width - fixedWidth - gaps);
            child.Measure(new Size(remaining, availableSize.Height));
            fillWidth += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(fixedWidth + fillWidth + gaps, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToList();
        var spacing = Math.Max(0d, Spacing);
        var gaps = Math.Max(0, visible.Count - 1) * spacing;
        var fixedWidth = visible.Where(child => !GetIsFill(child)).Sum(child => child.DesiredSize.Width);
        var hasFill = visible.Any(GetIsFill);
        var fillWidth = hasFill ? Math.Max(0d, finalSize.Width - fixedWidth - gaps) : 0d;
        // 多个填充子元素平分剩余宽度。
        var fillShare = hasFill ? fillWidth / visible.Count(GetIsFill) : 0d;

        double WidthOf(UIElement child) => GetIsFill(child) ? fillShare : child.DesiredSize.Width;

        var left = visible.Where(child => !GetIsRight(child)).ToList();
        var right = visible.Where(GetIsRight).ToList();

        var x = 0d;
        foreach (var child in left)
        {
            ArrangeChild(child, x, WidthOf(child), finalSize.Height);
            x += WidthOf(child) + spacing;
        }

        // 右组整体贴右边缘：先算右组总宽，再从其左端起排。
        var rightWidth = right.Sum(WidthOf) + Math.Max(0, right.Count - 1) * spacing;
        x = Math.Max(0d, finalSize.Width - rightWidth);
        foreach (var child in right)
        {
            ArrangeChild(child, x, WidthOf(child), finalSize.Height);
            x += WidthOf(child) + spacing;
        }

        return finalSize;
    }

    private static void ArrangeChild(UIElement child, double x, double width, double panelHeight)
    {
        var height = Math.Min(child.DesiredSize.Height, panelHeight);
        child.Arrange(new Rect(x, (panelHeight - height) / 2d, width, height));
    }
}
