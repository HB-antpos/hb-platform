using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using Hbpos.Client.Wpf;

namespace Hbpos.Client.Tests;

/// <summary>
/// 收银员登录弹窗的屏幕数字键盘：给 PasswordBox.Password 赋值会把光标重置到开头，
/// 点完屏幕键盘后再扫码/敲键会插到最前面；点键后必须把光标移回末尾。
/// </summary>
public sealed class CashierLoginOverlayCaretTests
{
    [Fact]
    public void PasswordBox_exposes_the_internal_select_method_used_to_move_the_caret()
    {
        // 反射依赖框架内部的 Select(int, int)；框架升级后它若消失，这里先失败，而不是线上悄悄退回「光标跑到开头」。
        var method = typeof(PasswordBox).GetMethod(
            "Select",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(int), typeof(int)],
            modifiers: null);

        Assert.NotNull(method);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("5550001234567")]
    public async Task MoveCaretToEnd_succeeds_for_any_input_length(string password)
    {
        await RunOnStaDispatcherAsync(() =>
        {
            var passwordBox = new PasswordBox { Password = password };

            Assert.True(MainWindow.MoveCaretToEnd(passwordBox));
            // 只移动光标，不得改动已输入的内容。
            Assert.Equal(password, passwordBox.Password);
        });
    }

    [Fact]
    public void Keyboard_button_click_moves_the_caret_after_replacing_the_password()
    {
        var source = ReadMainWindowSource();
        var start = source.IndexOf("private void CashierLoginOverlayKeyboardButtonClick(", StringComparison.Ordinal);
        var end = source.IndexOf("internal static string ApplyCashierBarcodeKeyboardInput(", start, StringComparison.Ordinal);
        var method = source[start..end];

        var assign = method.IndexOf("CashierLoginOverlayPasswordBox.Password = nextValue;", StringComparison.Ordinal);
        var moveCaret = method.IndexOf("MoveCaretToEnd(CashierLoginOverlayPasswordBox);", StringComparison.Ordinal);

        // 赋值之后必须移动光标：顺序反了（或漏掉）光标仍会停在开头。
        Assert.True(assign >= 0, "屏幕键盘仍应通过整串赋值更新 PasswordBox。");
        Assert.True(moveCaret > assign, "赋值 Password 之后必须调用 MoveCaretToEnd。");
    }

    private static string ReadMainWindowSource()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                var path = Path.Combine(
                    current.FullName,
                    "apps",
                    "pos-wpf",
                    "src",
                    "Hbpos.Client.Wpf",
                    "MainWindow.xaml.cs");
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate MainWindow.xaml.cs.");
    }

    private static async Task RunOnStaDispatcherAsync(Action action)
    {
        var dispatcherReady = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcherReady.TrySetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                dispatcherReady.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Hbpos.Client.Tests.CashierLoginOverlayCaretDispatcher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var dispatcher = await dispatcherReady.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        try
        {
            await dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
        }
        finally
        {
            if (!dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            Assert.True(thread.Join(AsyncTestWaitSupport.DefaultTimeout), "WPF Dispatcher thread did not shut down.");
        }
    }
}
