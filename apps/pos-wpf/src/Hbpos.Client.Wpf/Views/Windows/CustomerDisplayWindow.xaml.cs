using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Hbpos.Client.Wpf.Views.Windows;

public partial class CustomerDisplayWindow : Window
{
    private const int WmExitSizeMove = 0x0232;

    public CustomerDisplayWindow()
    {
        InitializeComponent();
    }

    /// <summary>收银员点了标题栏关闭按钮；程序退出、断开第二屏等系统关闭不算，用于决定是否记住「已关闭」。</summary>
    public bool IsClosedByUser { get; private set; }

    /// <summary>收银员拖动或缩放窗口结束（WM_EXITSIZEMOVE）；程序代码设置位置大小不会触发。</summary>
    public event EventHandler? MoveOrResizeCompleted;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmExitSizeMove)
        {
            MoveOrResizeCompleted?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 普通模式下双击标题栏请求切到客显全屏；由外壳走与主窗口客显按钮相同的权限校验与模式同步。
    /// </summary>
    public event EventHandler? FullscreenRequested;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // 关键逻辑：不再直接最大化（只铺到工作区、仍留标题栏且模式不同步），改为请求真正的全屏模式。
            FullscreenRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        IsClosedByUser = true;
        Close();
    }

    public void SetTitleBarVisible(bool isVisible)
    {
        TitleBar.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        TitleBarRow.Height = isVisible ? new GridLength(44) : new GridLength(0);
        ResizeMode = isVisible ? ResizeMode.CanResize : ResizeMode.NoResize;
        RefreshContentLayout();
    }

    public void RefreshContentLayout()
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateLayout();
            CustomerDisplayContent.RefreshPromotionLayout();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
