using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Hbpos.Client.Wpf.Services;

public sealed record AppUpdateBackgroundCheckOptions(TimeSpan Interval, TimeSpan MaxJitter)
{
    public const string IntervalMinutesConfigurationKey = "AppUpdate:BackgroundCheckIntervalMinutes";

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(60);

    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan MaxJitterCap = TimeSpan.FromMinutes(10);

    public bool IsEnabled => Interval > TimeSpan.Zero;

    public static AppUpdateBackgroundCheckOptions FromConfiguration(IConfiguration configuration)
    {
        var raw = configuration[IntervalMinutesConfigurationKey]?.Trim();
        if (string.IsNullOrWhiteSpace(raw) ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return Create(DefaultInterval);
        }

        // 中文注释：0 或负数表示关闭后台检查；过短间隔抬到 5 分钟，避免门店终端集中请求更新中心。
        return minutes <= 0
            ? Create(TimeSpan.Zero)
            : Create(TimeSpan.FromMinutes(Math.Max(minutes, MinimumInterval.TotalMinutes)));
    }

    public static AppUpdateBackgroundCheckOptions Create(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return new AppUpdateBackgroundCheckOptions(TimeSpan.Zero, TimeSpan.Zero);
        }

        // 中文注释：每轮额外随机等待最多 1/5 间隔（上限 10 分钟），把同一时间启动的门店终端错开。
        var jitter = TimeSpan.FromTicks(Math.Min(interval.Ticks / 5, MaxJitterCap.Ticks));
        return new AppUpdateBackgroundCheckOptions(interval, jitter);
    }
}

// 中文注释：运行期间按固定间隔在后台检查更新；启动检查仍由 MainWindow 的启动闸门负责，这里只覆盖长时间不重启的收银机。
public sealed class AppUpdateBackgroundCheckScheduler(
    IAppUpdateCoordinator coordinator,
    AppUpdateBackgroundCheckOptions options,
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null) : IDisposable
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync =
        delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
    private readonly CancellationTokenSource _stopping = new();
    private int _started;

    internal Task LoopTask { get; private set; } = Task.CompletedTask;

    // 中文注释：必须在 UI 线程启动；循环在 UI 同步上下文里恢复，协调器更新界面状态和命令时不会跨线程。
    public void Start()
    {
        if (!options.IsEnabled ||
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

    internal async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await coordinator.CheckForUpdatesInBackgroundAsync(cancellationToken);
            if (result.Status is AppUpdateCoordinatorStatus.CheckFailed
                or AppUpdateCoordinatorStatus.PolicyFailed
                or AppUpdateCoordinatorStatus.DownloadFailed)
            {
                var detail = result.ErrorMessage ??
                    (result.StatusArgs.Length > 0 ? Convert.ToString(result.StatusArgs[0], CultureInfo.InvariantCulture) : null);
                ConsoleLog.WriteError(
                    "AppUpdate",
                    $"background app update check status={result.Status} errorCode={result.ErrorCode ?? "<null>"} errorMessage={detail ?? "<null>"}");
            }
            else if (result.Status is AppUpdateCoordinatorStatus.OptionalReady
                or AppUpdateCoordinatorStatus.ForcePendingInstall)
            {
                ConsoleLog.Write("AppUpdate", $"background app update ready status={result.Status}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 中文注释：单次后台检查异常只记录日志，下个周期继续，不能终止循环或影响收银。
            ConsoleLog.WriteError(
                "AppUpdate",
                $"background app update check failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _delayAsync(NextDelay(), cancellationToken);
                await CheckOnceAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 中文注释：窗口关闭时正常结束循环。
        }
    }

    private TimeSpan NextDelay()
    {
        return options.MaxJitter > TimeSpan.Zero
            ? options.Interval + TimeSpan.FromTicks(Random.Shared.NextInt64(options.MaxJitter.Ticks + 1))
            : options.Interval;
    }
}
