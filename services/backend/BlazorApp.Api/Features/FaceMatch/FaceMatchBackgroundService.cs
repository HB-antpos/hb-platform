namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>
/// 人脸比对后台任务调度（BackgroundService）：每个节拍（默认 60 秒）检查哪些任务到点，到点的任务各开独立 DI 作用域执行，
/// 每个任务单独 try/catch，一个失败不影响其他。时间来自 <see cref="TimeProvider"/>，默认安全：
/// 没有管理凭据时不发任何腾讯云请求，没有门店启用时不做读取类与保留清理类工作（见 <see cref="FaceMatchMaintenance"/>）。
/// 命中提示发布每个节拍都跑（只是一条廉价的索引查询）；其余任务按各自间隔。
/// </summary>
public sealed class FaceMatchBackgroundService(
    IServiceScopeFactory scopeFactory,
    IFaceMatchSettingsProvider settingsProvider,
    TimeProvider timeProvider,
    ILogger<FaceMatchBackgroundService> logger
) : BackgroundService
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan VisitGroupInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan HousekeepingInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan OrphanSweepInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan IntegrityInterval = TimeSpan.FromHours(24);

    private readonly Dictionary<string, DateTimeOffset> _nextRun = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后稍等，避开启动迁移与缓存预热的高峰。
        if (!await DelayAsync(StartupDelay, stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "人脸比对后台任务节拍失败");
            }

            var seconds = Math.Max(10, settingsProvider.Options.BackgroundTickSeconds);
            if (!await DelayAsync(TimeSpan.FromSeconds(seconds), stoppingToken))
            {
                break;
            }
        }
    }

    /// <summary>执行一个节拍：把到点的任务各跑一遍。公开给测试直接驱动（配合假的 TimeProvider）。</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (!settingsProvider.Options.BackgroundEnabled)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        await RunIfDueAsync("hit-notify", TimeSpan.Zero, now, async scope =>
            await scope.GetRequiredService<FaceHitNotifier>().PublishPendingAsync(cancellationToken), cancellationToken);
        await RunIfDueAsync("visit-groups", VisitGroupInterval, now, async scope =>
            await scope.GetRequiredService<FaceMatchMaintenance>().CleanupVisitGroupsAsync(cancellationToken), cancellationToken);
        await RunIfDueAsync("housekeeping", HousekeepingInterval, now, async scope =>
        {
            var maintenance = scope.GetRequiredService<FaceMatchMaintenance>();
            await maintenance.ProcessUnwelcomeEntriesAsync(cancellationToken);
            await maintenance.PublishExpiryRemindersAsync(cancellationToken);
            await maintenance.DeleteExpiredTaskImagesAsync(cancellationToken);
            await maintenance.RetryEnrollmentDeletionsAsync(cancellationToken);
            await maintenance.InvalidateStaleCrossBorderReviewsAsync(cancellationToken);
        }, cancellationToken);
        await RunIfDueAsync("capture-retention", RetentionInterval, now, async scope =>
            await scope.GetRequiredService<FaceMatchMaintenance>().CleanupCaptureRecordsAsync(cancellationToken), cancellationToken);
        await RunIfDueAsync("orphan-sweep", OrphanSweepInterval, now, async scope =>
        {
            var maintenance = scope.GetRequiredService<FaceMatchMaintenance>();
            if (await maintenance.AnyStoreEnabledAsync(cancellationToken))
            {
                await maintenance.SweepOrphanVisitGroupsAsync(cancellationToken);
            }
        }, cancellationToken);
        await RunIfDueAsync("staff-integrity", IntegrityInterval, now, async scope =>
        {
            var maintenance = scope.GetRequiredService<FaceMatchMaintenance>();
            if (await maintenance.AnyStoreEnabledAsync(cancellationToken))
            {
                await maintenance.CheckStaffLibraryIntegrityAsync(cancellationToken);
            }
        }, cancellationToken);
    }

    private async Task RunIfDueAsync(
        string name, TimeSpan interval, DateTimeOffset now, Func<IServiceProvider, Task> job, CancellationToken cancellationToken)
    {
        // 首次出现时安排在「现在 + 间隔」之后（命中提示间隔为 0，立即跑）；这样刚部署时不会一次性把所有重任务都跑一遍。
        if (!_nextRun.TryGetValue(name, out var due))
        {
            due = now + interval;
            _nextRun[name] = due;
        }

        if (now < due)
        {
            return;
        }

        _nextRun[name] = now + interval;
        try
        {
            // 每个任务独立作用域（独立的数据库连接 / 事务边界）。
            using var scope = scopeFactory.CreateScope();
            await job(scope.ServiceProvider);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "人脸比对后台任务失败: Job={Job}", name);
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
