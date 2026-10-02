using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf.Views.Screens;

public partial class DailyCloseView : UserControl
{
    private IInputElement? _focusBeforeCashCountWorkspace;
    private IInputElement? _focusBeforeDiscardDraftConfirmation;

    public DailyCloseView()
    {
        InitializeComponent();
    }

    private void DailyCloseCashWorkspaceOverlayIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _focusBeforeCashCountWorkspace = Keyboard.FocusedElement;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() => FocusDefaultButton(DailyCloseCashWorkspaceReturnButton)));
            return;
        }

        RestoreFocus(_focusBeforeCashCountWorkspace);
        _focusBeforeCashCountWorkspace = null;
    }

    private void DailyCloseCashWorkspaceOverlayPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not DailyCloseViewModel viewModel)
        {
            return;
        }

        // 中文注释：数字键盘已常驻工作区；只有放弃确认框打开时由它处理 Escape，否则关闭工作区并保留草稿。
        if (viewModel.IsDiscardDailyCloseDraftConfirmationOpen)
        {
            return;
        }

        if (viewModel.CloseCashCountWorkspaceCommand.CanExecute(null))
        {
            viewModel.CloseCashCountWorkspaceCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void DailyCloseDiscardDraftOverlayIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _focusBeforeDiscardDraftConfirmation = Keyboard.FocusedElement;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() => FocusDefaultButton(DailyCloseDiscardDraftCancelButton)));
            return;
        }

        RestoreFocus(_focusBeforeDiscardDraftConfirmation);
        _focusBeforeDiscardDraftConfirmation = null;
    }

    private void DailyCloseDiscardDraftOverlayPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not DailyCloseViewModel viewModel)
        {
            return;
        }

        if (viewModel.CancelDiscardDailyCloseDraftCommand.CanExecute(null))
        {
            viewModel.CancelDiscardDailyCloseDraftCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static void FocusDefaultButton(Button button)
    {
        if (!button.IsVisible || !button.IsEnabled)
        {
            return;
        }

        button.Focus();
        Keyboard.Focus(button);
    }

    private static void RestoreFocus(IInputElement? focusTarget)
    {
        if (focusTarget is UIElement { IsVisible: true, IsEnabled: true } previousFocus)
        {
            previousFocus.Focus();
        }
    }
}
