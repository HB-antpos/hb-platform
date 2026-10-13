using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Background;

/// <summary>
/// 门店价格更新合并推送（N3-a）：仓库调价产生的价格更新任务，按门店合并成一条通知发给该店持有
/// <c>StoreProducts.PriceUpdates</c> 的人。
/// <para>
/// 为什么放在后台 worker 而不是任务服务里：
/// 1. 通知发布器自带事务，<b>必须在任何业务事务之外调用</b>（嵌套会让外层回滚失效）；任务服务挂在审计收口的业务事务上，
///    热路径里发布会踩这条硬约束，所以这里完全不碰 <c>StorePriceUpdateTaskService</c>；
/// 2. 仓库一次批量调价会给同一门店生成大量任务，需要一个合并窗口，避免一天收到几十条。
/// </para>
/// <para>
/// 判断“新增 / 再次变更”不用时间水位：对账 worker 每 5 分钟整行重写所有 Pending 任务（UpdatedAtUtc 不可靠），
/// InitiatedAtUtc 取自提交前的时间，长事务可能晚于它提交。改用“任务 + 变更次数”：
/// Pending 且没有 <see cref="StorePriceUpdateNotice"/> 行，或该行的 ChangeCount 小于任务当前值，即为未通知。
/// </para>
/// 默认开启；首次启用时先做一次静默基线（不发通知），避免上线瞬间对积压任务轰炸。
/// </summary>
public sealed class StorePriceUpdateNotificationWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<StorePriceUpdateNotificationWorker> logger
) : BackgroundService
{
    internal const string ConfigSection = "StorePriceUpdateNotifications";
    internal const int DefaultIntervalMinutes = 5;
    internal const int DefaultMergeWindowMinutes = 30;

    /// <summary>通知创建人（写入 AppNotification.CreatedBy）。</summary>
    internal const string SystemCreatedBy = "system:store-price-updates";

    internal const string NotificationTitle = "Price updates for your store";

    /// <summary>已非 Pending 任务的 Notice 行保留天数，超过后清理。</summary>
    internal const int NoticeRetentionDays = 30;

    /// <summary>每轮最多清理的 Notice 行数。</summary>
    internal const int CleanupBatchLimit = 5000;

    // 批量写库的分块：每行 3 个参数，300 行 = 900 个参数，低于 SQL Server 的 2100 与旧版 SQLite 的 999 上限。
    private const int InsertChunkSize = 300;

    // IN (...) 列表的分块：单条语句最多 1000 个参数。
    private const int InListChunkSize = 1000;

    /// <summary>启动后的等待时间，避开启动迁移与缓存预热的高峰；测试可缩短。</summary>
    internal TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue($"{ConfigSection}:Enabled", true))
        {
            logger.LogInformation("门店价格更新合并推送已关闭（{Section}:Enabled=false）", ConfigSection);
            return;
        }

        if (!await DelayAsync(StartupDelay, stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(timeProvider.GetUtcNow().UtcDateTime, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 整轮失败（例如数据库暂时不可用）只记日志，下一轮重试；迁移未执行时表不存在也会落在这里。
                logger.LogError(ex, "门店价格更新合并推送失败，下一轮重试");
            }

            var intervalMinutes = Math.Max(
                1,
                configuration.GetValue($"{ConfigSection}:IntervalMinutes", DefaultIntervalMinutes)
            );
            if (!await DelayAsync(TimeSpan.FromMinutes(intervalMinutes), stoppingToken))
            {
                break;
            }
        }
    }

    /// <summary>
    /// 跑一轮：首次启用基线 → 找出未通知任务并按门店合并发布 → 清理过期 Notice。
    /// 时间由调用方传入（测试可冻结）；每轮开新的 DI 作用域，发布全程不在任何业务事务内。
    /// </summary>
    internal async Task RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqlSugarContext>().Db;
        var publisher = scope.ServiceProvider.GetRequiredService<INotificationPublisher>();

        if (await TryWriteBaselineAsync(db, nowUtc, cancellationToken))
        {
            return;
        }

        var mergeWindow = TimeSpan.FromMinutes(
            Math.Max(0, configuration.GetValue($"{ConfigSection}:MergeWindowMinutes", DefaultMergeWindowMinutes))
        );
        var unnotified = await QueryUnnotifiedAsync(db, cancellationToken);
        var published = 0;
        var waiting = 0;
        var storeGroups = unnotified
            .GroupBy(task => task.StoreCode, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in storeGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 窗口按“该店最早一条未通知任务”算：最早的一条等够了，窗口内后来的任务一并带上。
            if (group.Min(task => task.InitiatedAtUtc) > nowUtc - mergeWindow)
            {
                waiting++;
                continue;
            }

            try
            {
                await PublishStoreAsync(db, publisher, group.Key, group.ToList(), nowUtc, cancellationToken);
                published++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // 单店失败只记日志、不写 Notice，下一轮按同样内容（同一幂等键）重试，不影响其他门店。
                logger.LogWarning(ex, "门店价格更新通知发布失败，下一轮重试: Store={StoreCode}", group.Key);
            }
        }

        if (published > 0)
        {
            logger.LogInformation(
                "门店价格更新合并推送完成: 已发布门店={Published}, 等待合并窗口门店={Waiting}",
                published,
                waiting
            );
        }
        else if (waiting > 0)
        {
            // 只在等窗口时每 5 分钟一条 Information 太吵，降为 Debug。
            logger.LogDebug("门店价格更新合并推送本轮无发布，等待合并窗口的门店={Waiting}", waiting);
        }

        try
        {
            var removed = await CleanupAsync(db, nowUtc, cancellationToken);
            if (removed > 0)
            {
                logger.LogInformation("已清理过期的价格更新已通知记录 {Count} 条", removed);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 清理失败不影响已完成的发布，下一轮再清。
            logger.LogWarning(ex, "清理价格更新已通知记录失败，下一轮重试");
        }
    }

    /// <summary>
    /// 首次启用基线：Notice 表为空时，把当前所有 Pending 任务静默写成已通知，本轮不发布，返回 true。
    /// 同时写入 TaskId=0 的标记行，保证表不再为空——否则“启用时恰好没有 Pending 任务”或“30 天后清理光了”，
    /// 之后出现的第一批真实任务会被误当作基线吞掉。基线在同一事务内完成（此处不调用发布器，不违反“发布在事务外”）。
    /// </summary>
    private async Task<bool> TryWriteBaselineAsync(ISqlSugarClient db, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (await db.Queryable<StorePriceUpdateNotice>().AnyAsync())
        {
            return false;
        }

        var pending = await db.Queryable<StorePriceUpdateTask>()
            .Where(task => task.Status == StorePriceUpdateTaskStatuses.Pending)
            .Select(task => new UnnotifiedTask { Id = task.Id, ChangeCount = task.ChangeCount })
            .ToListAsync(cancellationToken);

        var rows = pending
            .Select(task => new StorePriceUpdateNotice
            {
                TaskId = task.Id,
                ChangeCount = task.ChangeCount,
                NotifiedAtUtc = nowUtc,
            })
            .Append(new StorePriceUpdateNotice
            {
                TaskId = StorePriceUpdateNotice.BaselineMarkerTaskId,
                ChangeCount = 0,
                NotifiedAtUtc = nowUtc,
            })
            .ToList();

        await WriteNoticesAsync(db, rows, replaceExisting: false, cancellationToken);
        logger.LogInformation(
            "门店价格更新合并推送首次启用，已静默基线 {Count} 条待处理任务（不发通知）",
            pending.Count
        );
        return true;
    }

    /// <summary>Pending 且没有 Notice 行、或 Notice.ChangeCount 小于任务当前值的任务。</summary>
    private static async Task<List<UnnotifiedTask>> QueryUnnotifiedAsync(ISqlSugarClient db, CancellationToken cancellationToken)
    {
        return await db.Queryable<StorePriceUpdateTask, StorePriceUpdateNotice>(
                (task, notice) => new JoinQueryInfos(JoinType.Left, task.Id == notice.TaskId)
            )
            .Where((task, notice) =>
                task.Status == StorePriceUpdateTaskStatuses.Pending
                && SqlFunc.IsNull(notice.ChangeCount, -1) < task.ChangeCount)
            .Select((task, notice) => new UnnotifiedTask
            {
                Id = task.Id,
                StoreCode = task.StoreCode,
                Kind = task.Kind,
                InitiatedAtUtc = task.InitiatedAtUtc,
                ChangeCount = task.ChangeCount,
            })
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// 把一家店的全部未通知任务合并成一条通知发布，成功（含幂等命中、没有接收人）后再写 Notice。
    /// 先发布后写 Notice，两步都在事务之外：发布成功但写 Notice 失败时，下一轮同内容得到同一幂等键，发布器返回 Deduplicated，不会重复发。
    /// </summary>
    private async Task PublishStoreAsync(
        ISqlSugarClient db,
        INotificationPublisher publisher,
        string storeCode,
        IReadOnlyList<UnnotifiedTask> tasks,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var labelOnlyCount = tasks.Count(task => task.Kind == StorePriceUpdateTaskKinds.LabelOnly);
        var priceUpdateCount = tasks.Count - labelOnlyCount;

        var result = await publisher.PublishAsync(
            new NotificationPublishRequest
            {
                Type = AppNotificationTypes.StorePriceUpdates,
                Title = NotificationTitle,
                Body = BuildBody(priceUpdateCount, labelOnlyCount),
                Audience = NotificationAudience.ForStore(storeCode, Permissions.StoreProducts.PriceUpdates),
                StoreCode = storeCode,
                // 只放标识与数量，不放商品、价格等明细。
                Data = new Dictionary<string, string>
                {
                    ["storeCode"] = storeCode,
                    ["priceUpdateCount"] = priceUpdateCount.ToString(CultureInfo.InvariantCulture),
                    ["labelOnlyCount"] = labelOnlyCount.ToString(CultureInfo.InvariantCulture),
                },
                DedupeKey = BuildDedupeKey(storeCode, tasks.Select(task => (task.Id, task.ChangeCount))),
                CreatedBy = SystemCreatedBy,
            },
            cancellationToken
        );

        if (result.NotificationId == null && !result.Deduplicated)
        {
            // 没有接收人也要写 Notice：否则每一轮都会对同一批任务重算、重查接收人。之后有变更（ChangeCount 变大）或新任务才会再次触发。
            logger.LogInformation(
                "门店价格更新通知没有接收人，已记为已通知: Store={StoreCode}, Tasks={Count}",
                storeCode,
                tasks.Count
            );
        }

        await WriteNoticesAsync(
            db,
            tasks.Select(task => new StorePriceUpdateNotice
            {
                TaskId = task.Id,
                // 用查询时的变更次数而不是重新读取：发布期间若任务又被改，下一轮会因 Notice 偏小而再次通知。
                ChangeCount = task.ChangeCount,
                NotifiedAtUtc = nowUtc,
            }).ToList(),
            replaceExisting: true,
            cancellationToken
        );
    }

    /// <summary>
    /// 写入 Notice 行。replaceExisting=true 时先删掉同 TaskId 的旧行再插入（等价 upsert，且按批量语句执行）。
    /// 整体放在一个事务里，保证“删旧插新”不会留下半截；此处不调用发布器，不违反“发布在事务外”。
    /// </summary>
    private static async Task WriteNoticesAsync(
        ISqlSugarClient db,
        IReadOnlyList<StorePriceUpdateNotice> rows,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var transaction = await db.Ado.UseTranAsync(async () =>
        {
            if (replaceExisting)
            {
                foreach (var idChunk in rows.Select(row => row.TaskId).Chunk(InListChunkSize))
                {
                    await db.Deleteable<StorePriceUpdateNotice>()
                        .Where(notice => idChunk.Contains(notice.TaskId))
                        .ExecuteCommandAsync(cancellationToken);
                }
            }

            foreach (var chunk in rows.Chunk(InsertChunkSize))
            {
                await db.Insertable(chunk.ToList()).ExecuteCommandAsync(cancellationToken);
            }
        });
        if (!transaction.IsSuccess)
        {
            throw transaction.ErrorException ?? new InvalidOperationException("保存价格更新已通知记录失败");
        }
    }

    /// <summary>
    /// 清理“对应任务已不是 Pending（含任务行已被过期清理）且通知时间早于 30 天”的 Notice 行，每轮最多 5000 行。
    /// 仍是 Pending 的任务无论多久都保留，否则会被当作新任务再通知一次；TaskId=0 的基线标记永不清理。
    /// 用左连接 + IsNull 而不是子查询，SQL Server 与 SQLite 都能跑。
    /// </summary>
    internal static async Task<int> CleanupAsync(ISqlSugarClient db, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var cutoff = nowUtc.AddDays(-NoticeRetentionDays);
        var expiredIds = await db.Queryable<StorePriceUpdateNotice, StorePriceUpdateTask>(
                (notice, task) => new JoinQueryInfos(JoinType.Left, notice.TaskId == task.Id)
            )
            .Where((notice, task) =>
                notice.TaskId > StorePriceUpdateNotice.BaselineMarkerTaskId
                && notice.NotifiedAtUtc < cutoff
                && SqlFunc.IsNull(task.Status, string.Empty) != StorePriceUpdateTaskStatuses.Pending)
            .OrderBy((notice, task) => notice.NotifiedAtUtc)
            .Select((notice, task) => notice.TaskId)
            .Take(CleanupBatchLimit)
            .ToListAsync(cancellationToken);

        var removed = 0;
        foreach (var idChunk in expiredIds.Chunk(InListChunkSize))
        {
            removed += await db.Deleteable<StorePriceUpdateNotice>()
                .Where(notice => idChunk.Contains(notice.TaskId))
                .ExecuteCommandAsync(cancellationToken);
        }

        return removed;
    }

    /// <summary>
    /// 幂等键：<c>store-price-updates:{storeCode}:{SHA-256 前 16 位十六进制}</c>。
    /// 哈希输入是把本批任务按 "id:changeCount" 字符串（序号比较）排序后用逗号拼接——内容确定则键确定，
    /// 崩溃重试天然幂等；任一任务的变更次数变大就会得到新键，撤回后同键永不再发的约定不受影响。
    /// </summary>
    internal static string BuildDedupeKey(string storeCode, IEnumerable<(long Id, int ChangeCount)> tasks)
    {
        var parts = tasks
            .Select(task => string.Create(CultureInfo.InvariantCulture, $"{task.Id}:{task.ChangeCount}"))
            .OrderBy(part => part, StringComparer.Ordinal);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", parts)));
        return $"store-price-updates:{storeCode}:{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}";
    }

    /// <summary>
    /// 英文正文，只写数量；数量为 0 的部分省略措辞。
    /// 例：<c>3 products need a price change and 2 need a new shelf label. Open Price Updates to review.</c>
    /// </summary>
    internal static string BuildBody(int priceUpdateCount, int labelOnlyCount)
    {
        const string callToAction = "Open Price Updates to review.";
        var price = priceUpdateCount.ToString(CultureInfo.InvariantCulture);
        var label = labelOnlyCount.ToString(CultureInfo.InvariantCulture);

        if (priceUpdateCount > 0 && labelOnlyCount > 0)
        {
            var priceClause = priceUpdateCount == 1
                ? $"{price} product needs a price change"
                : $"{price} products need a price change";
            var labelClause = labelOnlyCount == 1
                ? $"{label} needs a new shelf label"
                : $"{label} need a new shelf label";
            return $"{priceClause} and {labelClause}. {callToAction}";
        }

        if (priceUpdateCount > 0)
        {
            return priceUpdateCount == 1
                ? $"{price} product needs a price change. {callToAction}"
                : $"{price} products need a price change. {callToAction}";
        }

        return labelOnlyCount == 1
            ? $"{label} product needs a new shelf label. {callToAction}"
            : $"{label} products need a new shelf label. {callToAction}";
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
            // 停机信号：由调用方退出循环。
            return false;
        }
    }

    /// <summary>未通知任务的投影行（只取判断与统计需要的列）。</summary>
    internal sealed class UnnotifiedTask
    {
        public long Id { get; set; }

        public string StoreCode { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public DateTime InitiatedAtUtc { get; set; }

        public int ChangeCount { get; set; }
    }
}
