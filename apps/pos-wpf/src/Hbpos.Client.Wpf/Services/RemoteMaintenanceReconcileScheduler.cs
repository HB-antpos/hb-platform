namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 决定“什么时候”做远程维护服务器对账。
/// 对账要向中心取期望配置，而网关要求已登录收银员的硬件绑定票据，开机后停在登录界面时请求只会被拒绝；
/// 紧急授权会话更会因 403 被客户端清掉。所以这里不在固定时间硬发请求，而是等到出现可用的
/// 普通收银员票据后再触发——此时刚登录、还没开始收银，提权弹窗打断的代价最小。
/// </summary>
internal sealed class RemoteMaintenanceReconcileScheduler(
    IRemoteMaintenanceReconciler reconciler,
    ICashierSessionContext sessionContext,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // 开机后先让出收银界面的启动高峰。
    internal TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(30);

    // 轮询内存里的会话状态，成本可以忽略；登录事件没有现成的订阅点。
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    // 暂缓（离线、中心维护、闸门被占用）后的重试间隔，避免频繁打中心。
    internal TimeSpan RetryInterval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 一直运行到出现“已有结论”的对账结果，或应用退出。
    /// 只有 <see cref="RemoteMaintenanceReconcileResult.Deferred"/> 会继续等待重试，其余结果都视为本次运行已处理完。
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(StartupDelay, _timeProvider, cancellationToken);
        var nextAttemptAt = DateTimeOffset.MinValue;
        while (true)
        {
            // 取消统一表现为抛 OperationCanceledException：若只用 while 条件判断，取消恰好发生在
            // Task.Delay 正常结束之后、下一次判断之前时，会无声无息地正常返回，调用方无法区分“已处理完”与“被取消”。
            cancellationToken.ThrowIfCancellationRequested();
            var now = _timeProvider.GetUtcNow();
            if (now >= nextAttemptAt && HasUsableCashierTicket(now))
            {
                var result = await reconciler.ReconcileAsync(cancellationToken);
                ConsoleLog.Write("RemoteMaintenance", $"startup reconcile result={result}");
                if (result != RemoteMaintenanceReconcileResult.Deferred)
                {
                    return;
                }

                nextAttemptAt = _timeProvider.GetUtcNow() + RetryInterval;
            }

            await Task.Delay(PollInterval, _timeProvider, cancellationToken);
        }
    }

    // 必须是普通收银员会话且票据未过期；紧急授权会话没有可用票据，请求只会 403 并把它清掉。
    private bool HasUsableCashierTicket(DateTimeOffset now) =>
        sessionContext.CurrentSession is { IsEmergencyOverride: false, AuthorizationToken: { Length: > 0 } } session &&
        session.AuthorizationExpiresAtUtc > now;
}
