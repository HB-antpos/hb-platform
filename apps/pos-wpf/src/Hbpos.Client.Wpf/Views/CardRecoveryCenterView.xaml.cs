using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf.Views;

public partial class CardRecoveryCenterView : UserControl
{
    // 自动检查倒计时只在页面可见时运行；离开恢复中心即停止，不在后台查询。
    private readonly DispatcherTimer _autoCheckTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // 窄于该宽度（如 1024×768 的老收银屏）进入紧凑模式：左列进度与字段收起，让右侧操作区独占宽度，
    // 三个结果按钮仍留在首屏内；被收起的信息可从头部入口的弹层查看。
    internal const double CompactWidthThreshold = 1180;

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact),
        typeof(bool),
        typeof(CardRecoveryCenterView),
        new PropertyMetadata(false));

    public CardRecoveryCenterView()
    {
        InitializeComponent();
        _autoCheckTimer.Tick += AutoCheckTimer_OnTick;
        SizeChanged += (_, e) => IsCompact = e.NewSize.Width < CompactWidthThreshold;
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        private set => SetValue(IsCompactProperty, value);
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

    // 人工核实区常驻后不再有“打开面板”动作：主管一开始填写（输入框获得焦点），
    // 就沿用原先的约定暂停自动检查，避免查询结果把正在核对的交易刷新掉。
    private void ManualResolution_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is CardRecoveryCenterViewModel { CanShowSupervisorResolution: true } viewModel)
        {
            viewModel.IsManualExpanded = true;
        }
    }

    // 次要信息弹层：入口按钮的 Tag 决定先显示哪一页（商品 / 交易详情 / 状态记录）。
    private void OpenDetailSheet_OnClick(object sender, RoutedEventArgs e)
    {
        var tab = (sender as FrameworkElement)?.Tag as string;
        SheetTabProducts.IsChecked = tab == "products";
        SheetTabDetails.IsChecked = tab == "details";
        SheetTabHistory.IsChecked = tab == "history";
        DetailSheetOverlay.Visibility = Visibility.Visible;
    }

    private void DetailSheetClose_OnClick(object sender, RoutedEventArgs e) =>
        DetailSheetOverlay.Visibility = Visibility.Collapsed;

    private void DetailSheetScrim_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        DetailSheetOverlay.Visibility = Visibility.Collapsed;

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
