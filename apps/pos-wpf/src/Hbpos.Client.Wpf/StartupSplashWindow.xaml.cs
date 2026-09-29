using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hbpos.Client.Wpf;

public partial class StartupSplashWindow : Window
{
    private static readonly Duration FadeOutDuration = new(TimeSpan.FromMilliseconds(160));

    public StartupSplashWindow(StartupProgressState progressState)
    {
        // 启动页在独立线程上运行：资源查找到窗口为止直接转到系统主题，
        // 不读取主线程上的 Application.Resources（主线程可能正在合并主题字典）。
        InheritanceBehavior = InheritanceBehavior.SkipToThemeNext;
        InitializeComponent();
        DataContext = progressState;
        CenterOnPrimaryScreen();
        Loaded += (_, _) => StartSheenIfAnimationsEnabled();
    }

    /// <summary>淡出后关闭；主窗口已在下方完成首帧，淡出期间两者叠加，不会露出桌面。</summary>
    public void FadeOutAndClose()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }

        var fade = new DoubleAnimation(0, FadeOutDuration) { FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void StartSheenIfAnimationsEnabled()
    {
        // 系统关闭了"窗口内动画"（含远程桌面的精简模式）时不播放高光，进度条仍按真实进度推进。
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        try
        {
            StartupProgressBar.ApplyTemplate();
            if (StartupProgressBar.Template?.FindName("PART_Indicator", StartupProgressBar) is not Border indicator ||
                indicator.Background is not LinearGradientBrush brush)
            {
                return;
            }

            if (brush.IsFrozen)
            {
                // 模板里的画刷可能被冻结共享，克隆一份再动画，避免"无法对冻结对象做动画"。
                brush = brush.Clone();
                indicator.Background = brush;
            }

            if (brush.Transform is not TranslateTransform sheen)
            {
                return;
            }

            var sweep = new DoubleAnimation(-120, StartupProgressBar.ActualWidth + 120, new Duration(TimeSpan.FromSeconds(1.6)))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            // 高光是纯装饰，30 帧足够；降低分层窗口的重绘频率，少占低配收银机的 CPU。
            Timeline.SetDesiredFrameRate(sweep, 30);
            sheen.BeginAnimation(TranslateTransform.XProperty, sweep);
        }
        catch (InvalidOperationException)
        {
            // 高光失败只影响装饰效果，不能让启动页线程因此退出。
        }
    }

    private void CenterOnPrimaryScreen()
    {
        // 启动页必须固定在主屏工作区居中，避免 CenterScreen 跟随鼠标跑到副屏。
        var position = StartupSplashWindowPlacement.CenterInWorkArea(SystemParameters.WorkArea, Width, Height);
        Left = position.X;
        Top = position.Y;
    }
}

internal static class StartupSplashWindowPlacement
{
    public static Point CenterInWorkArea(Rect workArea, double windowWidth, double windowHeight)
    {
        var safeWidth = NormalizeLength(windowWidth);
        var safeHeight = NormalizeLength(windowHeight);

        // 当窗口大于主屏工作区时贴住工作区左上角，避免被放到屏幕外。
        var leftOffset = Math.Max(0, (workArea.Width - safeWidth) / 2);
        var topOffset = Math.Max(0, (workArea.Height - safeHeight) / 2);

        return new Point(workArea.Left + leftOffset, workArea.Top + topOffset);
    }

    private static double NormalizeLength(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < 0
            ? 0
            : value;
    }
}
