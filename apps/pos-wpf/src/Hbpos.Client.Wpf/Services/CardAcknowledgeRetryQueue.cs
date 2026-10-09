namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 实时 acknowledge 失败后的进程内后台重试。
/// 订单/失败结果已经耐久落库后，Linkly 会话的 ack 一旦失败（网络抖动、API 重启、端点切换），
/// 服务端会话会停在“终态未确认”，下一笔刷卡就会被服务端闸门拦住；这里按退避间隔自动补发，
/// 重试用尽或进程重启后，仍由启动自动恢复和异常中心“恢复”兜底。
/// </summary>
internal sealed class CardAcknowledgeRetryQueue
{
    private static readonly TimeSpan[] DefaultDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5)
    ];

    private readonly Func<Guid, CancellationToken, Task<bool>> _acknowledgeAsync;
    private readonly IReadOnlyList<TimeSpan> _delays;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Dictionary<Guid, Task> _running = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <param name="acknowledgeAsync">返回 true 表示已确认或无事可做，false 表示失败需要继续重试。</param>
    public CardAcknowledgeRetryQueue(
        Func<Guid, CancellationToken, Task<bool>> acknowledgeAsync,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        IReadOnlyList<TimeSpan>? delays = null)
    {
        _acknowledgeAsync = acknowledgeAsync;
        _delayAsync = delayAsync ?? Task.Delay;
        _delays = delays ?? DefaultDelays;
    }

    /// <summary>同一个 attempt 同时只保留一条重试链；链结束后才可以再次调度。</summary>
    public void Schedule(Guid attemptGuid)
    {
        lock (_gate)
        {
            if (_running.ContainsKey(attemptGuid))
            {
                return;
            }

            _running[attemptGuid] = Task.Run(() => RunAsync(attemptGuid));
        }
    }

    /// <summary>等待当前所有重试链结束（测试和进程退出前使用）。</summary>
    public Task WhenIdleAsync()
    {
        Task[] running;
        lock (_gate)
        {
            running = _running.Values.ToArray();
        }

        return Task.WhenAll(running);
    }

    public void Shutdown() => _shutdown.Cancel();

    private async Task RunAsync(Guid attemptGuid)
    {
        try
        {
            foreach (var delay in _delays)
            {
                await _delayAsync(delay, _shutdown.Token);
                try
                {
                    if (await _acknowledgeAsync(attemptGuid, _shutdown.Token))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    ConsoleLog.Write(
                        "CardRecovery",
                        $"acknowledge retry attempt failed attemptGuid={attemptGuid} error={ex.GetType().Name}");
                }
            }

            ConsoleLog.Write(
                "CardRecovery",
                $"acknowledge retries exhausted attemptGuid={attemptGuid}; startup recovery or the recovery center will finish it");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // 进程关闭：放弃本轮，下次启动由自动恢复补发。
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(attemptGuid);
            }
        }
    }
}
