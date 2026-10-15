using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Hbpos.Client.Wpf.Views.Controls;

/// <summary>日结后重启提示浮层；全部展示状态来自 <c>WpfRestartPromptService</c>（DataContext）。</summary>
public partial class RestartPromptOverlay : UserControl
{
    public RestartPromptOverlay()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>浮层出现时触发（主窗口借此清扫码残留）。</summary>
    public event EventHandler? Shown;

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        Shown?.Invoke(this, EventArgs.Empty);

        // 默认焦点放在安全的一侧：Enter / 误触的扫码回车只会触发「稍后 / 取消重启 / 知道了」，不会触发重启。
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                var target = SecondaryButton.IsVisible ? SecondaryButton : PrimaryButton;
                target.Focus();
                Keyboard.Focus(target);
            }));
    }
}
