using System.Windows;
using System.Windows.Input;

namespace Hbpos.Client.Wpf.Views.Windows;

public partial class CustomerDisplayWindow : Window
{
    public CustomerDisplayWindow()
    {
        InitializeComponent();
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
