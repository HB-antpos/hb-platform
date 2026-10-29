using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Hbpos.Client.Wpf.Views.Controls;

/// <summary>收银员首次登录的配色选择窗；全部展示状态来自 <c>ColorThemeOnboardingViewModel</c>（DataContext）。</summary>
public partial class ColorThemeOnboardingOverlay : UserControl
{
    public ColorThemeOnboardingOverlay()
    {
        InitializeComponent();
        // 遮罩本身随 IsOpen 显隐（UserControl 始终可见），所以监听内层 Grid。
        OverlayRoot.IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        // 焦点放到「开始收银」：扫码枪残留的回车只会确认当前预选的配色，不会落到背后的收银界面。
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                ConfirmButton.Focus();
                Keyboard.Focus(ConfirmButton);
            }));
    }
}
