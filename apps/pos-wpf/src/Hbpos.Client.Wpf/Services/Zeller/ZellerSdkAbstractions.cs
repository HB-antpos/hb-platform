namespace Hbpos.Client.Wpf.Services.Zeller;

// 中文注释：SDK 的最小抽象。真实实现只是薄薄一层，把 Zeller.PaymentsSdk 的类型转换成我们自己的类型，
// 不做任何排队、超时或重试；这些都在 ZellerTerminalGateway 里，用假实现就能完整测试。

/// <summary>创建一个 SDK 的 Terminal 实例。网关在重建时会释放旧实例、再向工厂要一个新的。</summary>
public interface IZellerSdkTerminalFactory
{
    IZellerSdkTerminal Create();
}

/// <summary>
/// 一个 SDK 的 Terminal 实例。所有成员都只能在界面线程上调用，而且同一时刻只能有一个调用在进行，
/// 这两点由网关保证，实现不需要再处理。
/// </summary>
public interface IZellerSdkTerminal : IDisposable
{
    IZellerSdkOperation<ZellerPairingResult> Initialise();

    IZellerSdkOperation<ZellerPairingResult> Setup();

    /// <summary>扣款。实现必须使用无人值守模式，让电脑上不弹 Zeller 窗口。</summary>
    IZellerSdkOperation<ZellerCallResult> Purchase(ZellerPurchaseRequest request);

    IZellerSdkOperation<ZellerCallResult> Refund(ZellerRefundRequest request);

    IZellerSdkOperation<ZellerQueryResult> GetTransactions(string reference);
}

/// <summary>一次已经发给 SDK 的调用。</summary>
public interface IZellerSdkOperation<TResult>
{
    /// <summary>调用的结果。SDK 可能永远不让它完成，所以网关不会无限期等待。</summary>
    Task<TResult> Completion { get; }

    /// <summary>请求 SDK 取消这次调用。网关只在这次调用进行期间调用它。</summary>
    void Cancel();

    /// <summary>登记事件回调。回调可能在任意线程上触发。</summary>
    void OnEvent(Action<ZellerTerminalEvent> handler);
}

/// <summary>把动作送到界面线程执行。SDK 的窗口和 WebView 都在界面线程上。</summary>
public interface IZellerUiDispatcher
{
    Task<T> InvokeAsync<T>(Func<T> action);

    Task InvokeAsync(Action action);
}
