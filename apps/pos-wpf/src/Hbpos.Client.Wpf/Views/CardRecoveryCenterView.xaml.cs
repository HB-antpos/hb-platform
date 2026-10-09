using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf.Views;

public partial class CardRecoveryCenterView : UserControl
{
    // 自动检查倒计时只在页面可见时运行；离开恢复中心即停止，不在后台查询。
    private readonly DispatcherTimer _autoCheckTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public CardRecoveryCenterView()
    {
        InitializeComponent();
        _autoCheckTimer.Tick += AutoCheckTimer_OnTick;
    }

    private async void CardRecoveryCenterView_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CardRecoveryCenterViewModel viewModel)
        {
            return;
        }

        try
        {
            await viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            // async void 是 WPF Loaded 桥接点，最后一道保护避免页面加载异常终止客户端。
            ConsoleLog.WriteError(
                "CardRecoveryCenter",
                "Failed to load card recovery center.",
                exception: ex);
        }

        _autoCheckTimer.Start();
    }

    private void CardRecoveryCenterView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _autoCheckTimer.Stop();
    }

    private async void AutoCheckTimer_OnTick(object? sender, EventArgs e)
    {
        if (DataContext is not CardRecoveryCenterViewModel viewModel)
        {
            return;
        }

        try
        {
            await viewModel.OnAutoCheckTickAsync();
        }
        catch (Exception ex)
        {
            // 自动检查失败只记录；收银员仍可点“立即检查”手动重试。
            ConsoleLog.WriteError(
                "CardRecoveryCenter",
                "Automatic recovery check failed.",
                exception: ex);
        }
    }

    private void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string text } || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text.Trim());
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            // 剪贴板被其他程序占用时忽略，不影响恢复操作。
            ConsoleLog.WriteError("CardRecoveryCenter", "Copy to clipboard failed.", exception: ex);
        }
    }
}
