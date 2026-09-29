using System.Threading.Channels;

namespace Hbpos.Client.Tests;

// 中文注释：替代 Task.Delay 的可控等待：每次等待都进入队列，由测试逐个放行，循环类逻辑不依赖真实时间。
internal sealed class ControlledDelays
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private readonly Channel<PendingDelay> _requests = Channel.CreateUnbounded<PendingDelay>();
    private PendingDelay? _current;
    private int _requestCount;

    public int RequestCount => Volatile.Read(ref _requestCount);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        var pending = new PendingDelay(delay, cancellationToken);
        _requests.Writer.TryWrite(pending);
        return pending.Task;
    }

    public async Task<PendingDelay> NextAsync()
    {
        return await _requests.Reader.ReadAsync().AsTask().WaitAsync(WaitTimeout);
    }

    // 中文注释：放行当前等待，并等到循环发出下一个等待，保证循环在两次等待之间的工作已经执行完，测试再改条件不会与循环竞态。
    public async Task AdvanceAsync()
    {
        await CompleteCurrentAsync();
        _current = await NextAsync();
    }

    // 中文注释：放行当前等待且预期循环随后结束，不再等待下一个等待。
    public async Task AdvanceLastAsync()
    {
        await CompleteCurrentAsync();
    }

    private async Task CompleteCurrentAsync()
    {
        var current = _current ?? await NextAsync();
        _current = null;
        current.Complete();
    }
}

internal sealed class PendingDelay
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PendingDelay(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delay = delay;
        Task = _completion.Task.WaitAsync(cancellationToken);
    }

    public TimeSpan Delay { get; }

    public Task Task { get; }

    public void Complete() => _completion.TrySetResult();
}
