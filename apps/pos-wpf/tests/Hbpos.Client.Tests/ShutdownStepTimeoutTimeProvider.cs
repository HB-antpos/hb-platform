namespace Hbpos.Client.Tests;

/// <summary>
/// 退出协调器专用虚拟时钟：时间不前进（总预算永不耗尽），只记下指定时长的步骤超时计时器，
/// 由测试显式触发；其余计时器（后续步骤超时、迟到异常观察预算）永不触发。
/// </summary>
/// <remarks>
/// 用墙钟总预算断言"步骤超时后迟到致命异常仍能传播"时，超时续体要排线程池；
/// wpf-inno-smoke-build 单进程全量运行时线程池被前序测试占满，续体会被拖到约 1 秒后，
/// 总预算先耗尽而误报。改用本时钟后，断言只依赖事件顺序，墙钟仅作防挂死保险。
/// </remarks>
internal sealed class ShutdownStepTimeoutTimeProvider(TimeSpan stepTimeout) : TimeProvider
{
    private readonly DateTimeOffset _utcNow = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
    private readonly TaskCompletionSource<ManualTimer> _stepTimeoutArmed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(callback, state);
        if (dueTime == stepTimeout)
        {
            _stepTimeoutArmed.TrySetResult(timer);
        }

        return timer;
    }

    /// <summary>等协调器挂上步骤超时计时器后再触发，避免先推进、后建计时器导致永不超时。</summary>
    public async Task FireStepTimeoutAsync()
    {
        var timer = await _stepTimeoutArmed.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        timer.Fire();
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private int _disposed;

        public void Fire()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                callback(state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Volatile.Write(ref _disposed, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
