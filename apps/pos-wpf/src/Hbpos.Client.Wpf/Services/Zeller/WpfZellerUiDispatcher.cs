using System.Windows.Threading;

namespace Hbpos.Client.Wpf.Services.Zeller;

/// <summary>
/// <see cref="IZellerUiDispatcher"/> 的 WPF 实现：把动作送到主窗口所在的界面线程执行。
/// Zeller SDK 的窗口和 WebView2 都必须在界面线程上创建和调用（WPF 内技术验证已确认可用）。
/// </summary>
public sealed class WpfZellerUiDispatcher : IZellerUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfZellerUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.CheckAccess())
        {
            // 已经在界面线程上就直接执行，不再排一次队。
            try
            {
                return Task.FromResult(action());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        if (_dispatcher.HasShutdownStarted)
        {
            return Task.FromException<T>(new InvalidOperationException("The WPF dispatcher is shutting down."));
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync(() =>
        {
            action();
            return true;
        });
    }
}
