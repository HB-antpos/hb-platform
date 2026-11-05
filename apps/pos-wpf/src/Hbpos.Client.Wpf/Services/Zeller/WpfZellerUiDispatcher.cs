using System.Windows.Threading;

namespace Hbpos.Client.Wpf.Services.Zeller;

/// <summary>
/// <see cref="IZellerUiDispatcher"/> 的 WPF 实现：把动作送到主窗口所在的界面线程执行。
/// Zeller SDK 的窗口和 WebView2 都必须在界面线程上创建和调用（WPF 内技术验证已确认可用）。
/// </summary>
public sealed class WpfZellerUiDispatcher : IZellerUiDispatcher
{
    private readonly Func<Dispatcher?> _dispatcherProvider;

    public WpfZellerUiDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcherProvider = () => dispatcher;
    }

    /// <summary>
    /// 到每次调用时才取调度器（服务容器里用它：容器可能在没有 WPF 应用的环境里被解析，例如测试）。
    /// 取不到时这次调用失败，网关把它记成「调用没有进入 SDK」。
    /// </summary>
    public WpfZellerUiDispatcher(Func<Dispatcher?> dispatcherProvider)
    {
        _dispatcherProvider = dispatcherProvider ?? throw new ArgumentNullException(nameof(dispatcherProvider));
    }

    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = _dispatcherProvider();
        if (dispatcher is null)
        {
            return Task.FromException<T>(new InvalidOperationException("No WPF dispatcher is available for the Zeller SDK."));
        }

        if (dispatcher.CheckAccess())
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

        if (dispatcher.HasShutdownStarted)
        {
            return Task.FromException<T>(new InvalidOperationException("The WPF dispatcher is shutting down."));
        }

        return dispatcher.InvokeAsync(action).Task;
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
