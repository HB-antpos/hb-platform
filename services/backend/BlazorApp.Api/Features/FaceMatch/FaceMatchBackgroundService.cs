namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>
/// 人脸比对后台任务调度（BackgroundService）：每个节拍（默认 60 秒）检查哪些任务到点，到点的任务各开独立 DI 作用域执行，
/// 每个任务单独 try/catch，一个失败不影响其他。时间来自 <see cref="TimeProvider"/>，默认安全：
/// 没有管理凭据时不发任何腾讯云请求，没有门店启用时不做读取类与保留清理类工作（见 <see cref="FaceMatchMaintenance"/>）。
/// 命中提示发布每个节拍都跑（只是一条廉价的索引查询）；其余任务按各自间隔。
/// 分时客流投影（任务⑧）只在主库内换算计数桶，不依赖腾讯云凭据与「有门店启用」，功能没开启时也会把已有计数桶投影出来。
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
    public static readonly TimeSpan TrafficHourlyInterval = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DateTimeOffset> _nextRun = new(StringComparer.Ordinal);

    // 分时客流投影的进程内状态：历史补齐每个进程只做一次；迁移未执行的 Warning 只记一次，表出现后重置。
    private bool _trafficHourlyCaughtUp;
    private bool _trafficHourlyMissingLogged;

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
        // ⑧ 分时客流投影：不调用腾讯云、不需要管理凭据（功能没开启时也要把已有计数桶投影出来，免得报表缺数据）。
        await RunIfDueAsync("traffic-hourly", TrafficHourlyInterval, now, async scope =>
            await ProjectTrafficHourlyAsync(scope.GetRequiredService<FaceTrafficHourlyProjector>(), cancellationToken), cancellationToken);
    }

    /// <summary>
    /// 任务⑧：把最近 3 个门店本地日的 UTC 计数桶重算成「门店 + 本地日期 + 本地小时」分时行。
    /// 最近没有计数桶时只是一条廉价查询；迁移未执行（表不存在）时记一次 Warning 后跳过，不让任务失败；
    /// 进程启动后第一次成功投影之后补齐一次历史（最早的计数桶早于最早的分时行时）。
    /// </summary>
    private async Task ProjectTrafficHourlyAsync(FaceTrafficHourlyProjector projector, CancellationToken cancellationToken)
    {
        var recent = await projector.ProjectRecentAsync(cancellationToken);
        if (recent.SchemaMissing)
        {
            if (!_trafficHourlyMissingLogged)
            {
                _trafficHourlyMissingLogged = true;
                logger.LogWarning("分时客流投影已跳过：FaceTrafficHourly 表不存在，请先带 --schema=migrate 执行迁移 20261016.001-face-traffic-hourly");
            }

            return;
        }

        _trafficHourlyMissingLogged = false;
        if (!_trafficHourlyCaughtUp)
        {
            var history = await projector.BackfillMissingHistoryAsync(cancellationToken);
            if (!history.SchemaMissing)
            {
                _trafficHourlyCaughtUp = true;
                if (history.RowsChanged > 0)
                {
                    logger.LogInformation(
                        "分时客流历史补齐完成: Stores={Stores}, Inserted={Inserted}, Updated={Updated}, Deleted={Deleted}",
                        history.StoresProcessed, history.RowsInserted, history.RowsUpdated, history.RowsDeleted);
                }
            }
        }

        if (recent.RowsChanged > 0)
        {
            logger.LogDebug(
                "分时客流投影: Stores={Stores}, Inserted={Inserted}, Updated={Updated}, Deleted={Deleted}",
                recent.StoresProcessed, recent.RowsInserted, recent.RowsUpdated, recent.RowsDeleted);
        }
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
