namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 决定“什么时候”把预先下载好的 RustDesk 构建升级上去：与 WPF 夜间无人值守安装用同一个窗口
/// （<see cref="AppUpdateUnattendedInstallOptions"/>，默认本机 18:00–次日 08:00，周四 20:00 起），
/// 并要求连续两轮（至少 5 分钟）收银空闲（<see cref="IAppUpdateInstallSafetyGuard"/>：购物车为空、没有付款在进行）。
/// RustDesk 升级会断开正在进行的远程连接，所以只在营业时段外做；升级本身不影响收银。
/// 当天日结保存后、收银员已登出时（<see cref="IPostDailyCloseMaintenanceWindow"/>）也算营业时段外，不必等到 18:00。
/// 必须在 UI 线程启动：安全守卫读取的是界面状态。全程静默，失败只记日志。
/// </summary>
public sealed class RemoteMaintenanceUpgradeScheduler(
    IRemoteMaintenanceUpgrader upgrader,
    IAppUpdateInstallSafetyGuard safetyGuard,
    AppUpdateUnattendedInstallOptions windowOptions,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
    IPostDailyCloseMaintenanceWindow? postDailyCloseWindow = null) : IDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    // 升级失败后隔 30 分钟再试；同一构建累计失败次数由升级服务按上限截停。
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);

    private const int RequiredIdleTicks = 2;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync =
        delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
    private readonly CancellationTokenSource _stopping = new();
    private int _started;
    private int _idleTicks;
    private DateTimeOffset? _nextAttemptUtc;

    internal Task LoopTask { get; private set; } = Task.CompletedTask;

    public void Start()
    {
        if (!windowOptions.IsEnabled ||
            _stopping.IsCancellationRequested ||
            Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        LoopTask = RunAsync(_stopping.Token);
    }

    public void Stop()
    {
        if (!_stopping.IsCancellationRequested)
        {
            _stopping.Cancel();
        }
    }

    public void Dispose()
    {
        Stop();
        _stopping.Dispose();
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!windowOptions.IsWithinWindow(_timeProvider.GetLocalNow().DateTime) &&
                postDailyCloseWindow?.IsOpen != true)
            {
                _idleTicks = 0;
                _nextAttemptUtc = null;
                return;
            }

            if (!safetyGuard.CanInstallUpdate(out _, out _))
            {
                _idleTicks = 0;
                return;
            }

            if (++_idleTicks < RequiredIdleTicks)
            {
                return;
            }

            var utcNow = _timeProvider.GetUtcNow();
            if (_nextAttemptUtc is { } nextAttemptUtc && utcNow < nextAttemptUtc)
            {
                return;
            }

            var result = await upgrader.ApplyPendingUpgradeAsync(cancellationToken);
            if (result is RemoteMaintenanceUpgradeResult.Failed)
            {
                _nextAttemptUtc = utcNow + RetryInterval;
            }

            if (result is not RemoteMaintenanceUpgradeResult.NotApplicable)
            {
                ConsoleLog.Write("RemoteMaintenance", $"night rustdesk upgrade result={result}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 单轮异常只记录日志，下一轮继续，不能终止循环或影响收银。
            _nextAttemptUtc = _timeProvider.GetUtcNow() + RetryInterval;
            ConsoleLog.WriteWarning("RemoteMaintenance", $"night rustdesk upgrade aborted error={ex.GetType().Name}", exception: ex);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _delayAsync(TickInterval, cancellationToken);
                await TickAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 关闭窗口时正常结束循环。
        }
    }
}
