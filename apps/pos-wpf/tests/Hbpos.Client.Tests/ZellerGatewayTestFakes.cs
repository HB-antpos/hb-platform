using Hbpos.Client.Wpf.Services.Zeller;

namespace Hbpos.Client.Tests;

/// <summary>
/// 假的 Zeller SDK：按测试的指令完成、失败或永远不完成调用，并记录同一时刻有几个调用在 SDK 里。
/// 真实 SDK 0.1.0 在同一时刻收到第二个调用时会丢掉第一个的结果，所以 MaxConcurrentCalls 必须始终是 1。
/// </summary>
internal sealed class FakeZellerSdk : IZellerSdkTerminalFactory
{
    private readonly object _lock = new();
    private readonly List<FakeZellerTerminal> _terminals = [];
    private int _active;

    public Exception? CreateFailure { get; set; }

    /// <summary>设置后，查询会在发起后自动完成，用来跑并发压力测试。</summary>
    public Func<string, ZellerQueryResult>? QueryResponder { get; set; }

    /// <summary>设置后，扣款会在发起后自动完成。</summary>
    public Func<ZellerPurchaseRequest, ZellerCallResult>? PurchaseResponder { get; set; }

    public int MaxConcurrentCalls { get; private set; }

    public IReadOnlyList<FakeZellerTerminal> Terminals
    {
        get
        {
            lock (_lock)
            {
                return [.. _terminals];
            }
        }
    }

    public FakeZellerTerminal Current => Terminals[^1];

    public IZellerSdkTerminal Create()
    {
        if (CreateFailure is { } failure)
        {
            throw failure;
        }

        lock (_lock)
        {
            var terminal = new FakeZellerTerminal(this, _terminals.Count + 1);
            _terminals.Add(terminal);
            return terminal;
        }
    }

    internal void CallStarted()
    {
        lock (_lock)
        {
            _active++;
            MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, _active);
        }
    }

    internal void CallEnded()
    {
        lock (_lock)
        {
            _active--;
        }
    }
}

internal sealed class FakeZellerTerminal(FakeZellerSdk sdk, int number) : IZellerSdkTerminal
{
    private readonly object _lock = new();
    private readonly List<IFakeZellerOperation> _all = [];

    public int Number { get; } = number;

    public bool Disposed { get; private set; }

    public Exception? PurchaseThrows { get; set; }

    public SafeList<FakeZellerOperation<ZellerCallResult>> Purchases { get; } = new();

    public SafeList<FakeZellerOperation<ZellerCallResult>> Refunds { get; } = new();

    public SafeList<FakeZellerOperation<ZellerQueryResult>> Queries { get; } = new();

    public SafeList<string> QueryReferences { get; } = new();

    public SafeList<FakeZellerOperation<ZellerPairingResult>> Initialises { get; } = new();

    public SafeList<FakeZellerOperation<ZellerPairingResult>> Setups { get; } = new();

    public IZellerSdkOperation<ZellerPairingResult> Initialise() => Track(Initialises, new FakeZellerOperation<ZellerPairingResult>(sdk));

    public IZellerSdkOperation<ZellerPairingResult> Setup() => Track(Setups, new FakeZellerOperation<ZellerPairingResult>(sdk));

    public IZellerSdkOperation<ZellerCallResult> Purchase(ZellerPurchaseRequest request)
    {
        if (PurchaseThrows is { } failure)
        {
            throw failure;
        }

        var operation = Track(Purchases, new FakeZellerOperation<ZellerCallResult>(sdk));
        if (sdk.PurchaseResponder is { } responder)
        {
            CompleteSoon(operation, responder(request));
        }

        return operation;
    }

    public IZellerSdkOperation<ZellerCallResult> Refund(ZellerRefundRequest request) =>
        Track(Refunds, new FakeZellerOperation<ZellerCallResult>(sdk));

    public IZellerSdkOperation<ZellerQueryResult> GetTransactions(string reference)
    {
        QueryReferences.Add(reference);
        var operation = Track(Queries, new FakeZellerOperation<ZellerQueryResult>(sdk));
        if (sdk.QueryResponder is { } responder)
        {
            CompleteSoon(operation, responder(reference));
        }

        return operation;
    }

    public void Dispose()
    {
        Disposed = true;
        IFakeZellerOperation[] pending;
        lock (_lock)
        {
            pending = [.. _all];
        }

        // 中文注释：作废的 Terminal 上挂着的调用不再占着 SDK。
        foreach (var operation in pending)
        {
            operation.End();
        }
    }

    private T Track<T>(SafeList<T> list, T operation) where T : IFakeZellerOperation
    {
        lock (_lock)
        {
            _all.Add(operation);
        }

        list.Add(operation);
        return operation;
    }

    private static void CompleteSoon<T>(FakeZellerOperation<T> operation, T value) =>
        _ = Task.Run(async () =>
        {
            await Task.Delay(1);
            operation.Complete(value);
        });
}

internal interface IFakeZellerOperation
{
    void End();
}

internal sealed class FakeZellerOperation<T> : IZellerSdkOperation<T>, IFakeZellerOperation
{
    private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FakeZellerSdk _sdk;
    private Action<ZellerTerminalEvent>? _handler;
    private int _ended;
    private int _cancelCount;

    public FakeZellerOperation(FakeZellerSdk sdk)
    {
        _sdk = sdk;
        sdk.CallStarted();
    }

    public Task<T> Completion => _completion.Task;

    public int CancelCount => Volatile.Read(ref _cancelCount);

    /// <summary>设置后，收到 cancel() 时执行，用来模拟 SDK 正常响应取消。</summary>
    public Action? OnCancel { get; set; }

    public bool HasEventHandler => _handler is not null;

    public void Cancel()
    {
        Interlocked.Increment(ref _cancelCount);
        OnCancel?.Invoke();
    }

    public void OnEvent(Action<ZellerTerminalEvent> handler) => _handler = handler;

    public void Emit(ZellerTerminalEventKind kind) => _handler?.Invoke(new ZellerTerminalEvent(kind, kind.ToString()));

    public void Complete(T value)
    {
        // 中文注释：先登记「已离开 SDK」再完成，网关被唤醒后发起的下一个调用不会被误算成并发。
        End();
        _completion.TrySetResult(value);
    }

    public void Fail(Exception exception)
    {
        End();
        _completion.TrySetException(exception);
    }

    public void End()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            _sdk.CallEnded();
        }
    }
}

/// <summary>在调用线程上立即执行，模拟已经在界面线程上的调度器。</summary>
internal sealed class ImmediateZellerDispatcher : IZellerUiDispatcher
{
    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        try
        {
            return Task.FromResult(action());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    public Task InvokeAsync(Action action)
    {
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }
}

/// <summary>同步调用的 IProgress：Progress&lt;T&gt; 会切到捕获的上下文，测试里不好等待。</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal sealed class SafeList<T>
{
    private readonly object _lock = new();
    private readonly List<T> _items = [];

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public T this[int index]
    {
        get
        {
            lock (_lock)
            {
                return _items[index];
            }
        }
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            _items.Add(item);
        }
    }

    public T[] ToArray()
    {
        lock (_lock)
        {
            return [.. _items];
        }
    }
}
