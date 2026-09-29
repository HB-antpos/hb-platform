using System.Windows;
using System.Windows.Media;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 小屏整体等比缩放：宿主窗口小于设计基准时，给元素加 LayoutTransform，页面仍按基准尺寸排版再整体缩小；
/// 窗口不小于基准时保持 1:1，不放大。
/// </summary>
public static class AdaptiveUiScale
{
    // 设计基准取 1280×720：1024×768 的 15.6" 屏缩到 0.8 后，元素物理尺寸与常见 15.6" 1366×768 屏基本一致。
    public const double DesignWidth = 1280d;
    public const double DesignHeight = 720d;

    // 窗口最小尺寸 = 基准 × 该比例：用户拖动窗口时不能再小，避免文字和触控目标过小。
    public const double MinimumScale = 0.75d;

    // 屏幕本身放不下 0.75 倍基准时（如 1920×1080@200%、1366×768@150% 扣掉任务栏后不足 540 高），
    // 窗口会被压到工作区大小，此时继续缩小内容以保证完整显示，只在极端小尺寸下才停在该下限。
    public const double ScreenLimitedMinimumScale = 0.5d;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(AdaptiveUiScale),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty AttachmentProperty = DependencyProperty.RegisterAttached(
        "Attachment",
        typeof(WindowAttachment),
        typeof(AdaptiveUiScale),
        new PropertyMetadata(null));

    public static void SetIsEnabled(FrameworkElement element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(FrameworkElement element) => (bool)element.GetValue(IsEnabledProperty);

    internal static double Calculate(double availableWidth, double availableHeight)
    {
        if (!double.IsFinite(availableWidth)
            || !double.IsFinite(availableHeight)
            || availableWidth <= 0d
            || availableHeight <= 0d)
        {
            return 1d;
        }

        var scale = Math.Min(availableWidth / DesignWidth, availableHeight / DesignHeight);
        if (scale >= 1d)
        {
            return 1d;
        }

        // 向下取两位小数：缩放后的逻辑尺寸不低于设计基准，拖动窗口时比例也不会连续抖动。
        // 关键逻辑：不再用 0.75 兜底。原来窗口被屏幕压到 540 高以下时内容仍按 540 排版，
        // 底部超出窗口被裁掉，正好落在任务栏位置，看起来像被任务栏挡住。
        var rounded = Math.Floor((scale * 100d) + 1e-9) / 100d;
        return Math.Max(ScreenLimitedMinimumScale, rounded);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnElementLoaded;
        element.Unloaded -= OnElementUnloaded;
        if ((bool)e.NewValue)
        {
            element.Loaded += OnElementLoaded;
            element.Unloaded += OnElementUnloaded;
            if (element.IsLoaded)
            {
                Attach(element);
            }

            return;
        }

        Detach(element);
        element.ClearValue(FrameworkElement.LayoutTransformProperty);
        element.ClearValue(TextOptions.TextRenderingModeProperty);
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Attach(element);
        }
    }

    private static void OnElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Detach(element);
        }
    }

    private static void Attach(FrameworkElement element)
    {
        var window = Window.GetWindow(element);
        if (window is null)
        {
            return;
        }

        if (element.GetValue(AttachmentProperty) is WindowAttachment existing)
        {
            if (ReferenceEquals(existing.Window, window))
            {
                existing.Apply();
                return;
            }

            existing.Dispose();
        }

        var attachment = new WindowAttachment(element, window);
        element.SetValue(AttachmentProperty, attachment);
        attachment.Apply();
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.GetValue(AttachmentProperty) is WindowAttachment attachment)
        {
            attachment.Dispose();
            element.ClearValue(AttachmentProperty);
        }
    }

    private sealed class WindowAttachment : IDisposable
    {
        private readonly FrameworkElement _element;

        public WindowAttachment(FrameworkElement element, Window window)
        {
            _element = element;
            Window = window;
            // 关键逻辑：按宿主窗口尺寸而不是元素自身尺寸计算，改 LayoutTransform 不会反过来改变窗口尺寸，避免布局回环。
            Window.SizeChanged += OnWindowSizeChanged;
        }

        public Window Window { get; }

        public void Apply()
        {
            var scale = Calculate(Window.ActualWidth, Window.ActualHeight);
            if (scale >= 1d)
            {
                if (_element.ReadLocalValue(FrameworkElement.LayoutTransformProperty) != DependencyProperty.UnsetValue)
                {
                    _element.ClearValue(FrameworkElement.LayoutTransformProperty);
                    _element.ClearValue(TextOptions.TextRenderingModeProperty);
                }

                return;
            }

            if (_element.LayoutTransform is ScaleTransform current
                && current.ScaleX == scale
                && current.ScaleY == scale)
            {
                return;
            }

            _element.LayoutTransform = new ScaleTransform(scale, scale);
            // 缩放后字形落在非整像素位置，ClearType 子像素渲染会在竖笔上出现白色细线；改用灰度抗锯齿。
            TextOptions.SetTextRenderingMode(_element, TextRenderingMode.Grayscale);
        }

        public void Dispose()
        {
            Window.SizeChanged -= OnWindowSizeChanged;
        }

        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            Apply();
        }
    }
}
