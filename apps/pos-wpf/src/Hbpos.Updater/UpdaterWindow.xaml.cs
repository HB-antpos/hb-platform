using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Hbpos.Updater;

public partial class UpdaterWindow : Window
{
    private readonly UpdaterViewModel _viewModel;

    internal UpdaterWindow(UpdaterViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        CenterOnPrimaryScreen();
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        Closing += OnClosing;
    }

    private void CenterOnPrimaryScreen()
    {
        // 与启动页一致固定在主屏工作区居中，不跟随鼠标跑到副屏。
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.CanClose)
        {
            e.Cancel = true;
        }
    }
}
