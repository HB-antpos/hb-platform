using Zeller.PaymentsSdk.Models;
using Zeller.PaymentsSdk.Utils;

namespace Hbpos.Client.Wpf.Services.Zeller.Sdk;

/// <summary>
/// 把 SDK 的 <see cref="OperationHandle{TResponse}"/> 包成 <see cref="IZellerSdkOperation{TResult}"/>，并把响应转换成我们的类型。
/// </summary>
/// <remarks>
/// 中文注释：OperationHandle 没有公开构造函数，测试造不出来，所以这里只依赖三个委托；
/// <see cref="From"/> 是唯一碰 OperationHandle 的地方，只做转接。
/// 结果任务在构造时就开始等待：网关在界面线程上发起调用并立即构造这个对象，不会漏掉很快返回的结果。
/// </remarks>
internal sealed class ZellerSdkOperation<TResponse, TResult> : IZellerSdkOperation<TResult>
{
    private readonly Action _cancel;
    private readonly Action<Action<EventResponseValueData>> _subscribe;

    public ZellerSdkOperation(
        Task<TResponse> response,
        Action cancel,
        Action<Action<EventResponseValueData>> subscribe,
        Func<TResponse, TResult> map)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(map);
        _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
        _subscribe = subscribe ?? throw new ArgumentNullException(nameof(subscribe));
        Completion = MapAsync(response, map);
    }

    public Task<TResult> Completion { get; }

    public void Cancel() => _cancel();

    public void OnEvent(Action<ZellerTerminalEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _subscribe(data => handler(ZellerSdkMapper.ToEvent(data)));
    }

    public static ZellerSdkOperation<TResponse, TResult> From(OperationHandle<TResponse> handle, Func<TResponse, TResult> map)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return new ZellerSdkOperation<TResponse, TResult>(AwaitAsync(handle), handle.cancel, handle.onEvent, map);
    }

    private static async Task<TResponse> AwaitAsync(OperationHandle<TResponse> handle) => await handle.ConfigureAwait(false);

    // 中文注释：SDK 抛出的异常原样传出去，网关会把它记成 Faulted（结果未知，按 reference 核实）。
    private static async Task<TResult> MapAsync(Task<TResponse> response, Func<TResponse, TResult> map) =>
        map(await response.ConfigureAwait(false));
}
