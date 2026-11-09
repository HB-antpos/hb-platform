using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Diagnostics;

namespace BlazorApp.Api.Services.Background;

/// <summary>由专用分布式租约串行的格式 2 折扣日快照；请求线程不创建工作项，也不读取成交源。</summary>
public sealed class BatchProductSalesDiscountWorker(
    IServiceScopeFactory scopes,
    IOptions<ScheduledTaskOptions> options,
    ILogger<BatchProductSalesDiscountWorker> logger) : BackgroundService
{
    private const string GlobalLeaseTaskType = nameof(BatchProductSalesDiscountWorker);
    private const string GlobalLeaseScope = "daily-format-2";
    internal static readonly TimeSpan GlobalLeaseDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(4);
    internal static readonly TimeSpan DayExecutionLimit = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan TerminalPersistenceLimit = TimeSpan.FromSeconds(15);
    // 来源预算覆盖前后两次 capture 与事实聚合；为发布留余量，且单日硬截止仍短于日期租约。
    internal static readonly TimeSpan SourceReadLimit = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan BackfillPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = IdlePollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { interval = await RunOnceAsync(stoppingToken) ? BackfillPollInterval : IdlePollInterval; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "商品折扣日快照后台任务暂不可用"); }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task<bool> RunOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        // 折扣 worker 由 daily-format-2 全局租约串行，不能被旧实例的通用 scheduler 选主长期阻断。
        if (!await services.GetRequiredService<ScheduledTaskRuntimeControlService>().IsLeaseManagedWorkerEnabledAsync()) return false;
        var db = services.GetRequiredService<SqlSugarContext>().Db;
        var store = new BatchProductSalesDiscountDailyStore(db);
        if (!store.SchemaReady)
        {
            logger.LogWarning("折扣日快照表尚未完成部署，后台任务跳过；请先运行版本化 schema migration");
            return false;
        }
        var leases = services.GetRequiredService<ScheduledTaskLeaseService>();
        var global = await leases.TryAcquireAsync(GlobalLeaseTaskType, GlobalLeaseScope, GlobalLeaseDuration);
        if (!global.Acquired || string.IsNullOrWhiteSpace(global.Lease?.LeaseToken)) return false;
        var globalToken = global.Lease.LeaseToken;
        var success = false;
        try
        {
            var today = SalesStatisticsBusinessDate.GetBusinessDate(DateTimeOffset.UtcNow);
            // SQL Server 单个 IN 参数列表有限制；五年仍明显高于默认一年且保持一次缺口扫描可执行。
            var historicalYears = Math.Clamp(options.Value.DiscountSnapshotHistoricalYears, 1, 5);
            var coverage = BuildCoverageDays(today, historicalYears);
            var coverageStart = coverage[0];
            var coverageEnd = coverage[^1];
            var recentDays = Math.Clamp(options.Value.DiscountSnapshotRecentDays, 1, 14);
            var preferred = Enumerable.Range(0, recentDays).Select(offset => today.AddDays(-offset)).ToArray();
            var deadline = DateTime.UtcNow.Add(RunBudget);
            // 单轮预算只限制领取新日期；已领取日期使用自己的完整预算，避免写入到一半被截断。
            await store.EnsureQueuedAsync(coverage, DateTime.UtcNow, stoppingToken);
            var recentCheckInterval = TimeSpan.FromMinutes(Math.Max(1, options.Value.DiscountSnapshotRecentCheckMinutes));
            var batchSize = Math.Clamp(options.Value.DiscountSnapshotBatchSize, 1, 48);
            for (var completed = 0; completed < batchSize && DateTime.UtcNow < deadline; completed++)
            {
                stoppingToken.ThrowIfCancellationRequested();
                await leases.EnsureActiveAsync(GlobalLeaseTaskType, GlobalLeaseScope, globalToken, GlobalLeaseDuration, "折扣日快照持久队列");
                var claim = await store.ClaimNextAsync(DateTime.UtcNow, preferred, coverageStart, coverageEnd,
                    recentCheckInterval, stoppingToken);
                if (claim == null) break;
                await ExecuteClaimAsync(leases, globalToken, claim, stoppingToken);
            }
            success = true;
            return await store.HasDueBackfillAsync(coverageStart, coverageEnd, DateTime.UtcNow);
        }
        finally
        {
            // 全局租约收尾不能继承计算 scope 的取消状态，服务停止时也要有界地归还执行权。
            await PersistTerminalAsync(async (terminalServices, _) =>
                await terminalServices.GetRequiredService<ScheduledTaskLeaseService>()
                    .CompleteAsync(GlobalLeaseTaskType, GlobalLeaseScope, globalToken, success));
        }
    }

    private async Task ExecuteClaimAsync(ScheduledTaskLeaseService leases, string globalToken, BatchProductSalesDiscountDailyStore.ClaimedDay claim,
        CancellationToken stoppingToken)
    {
        // 调度/全局租约和每个日期的成交读取使用不同的 SqlSugar context，取消不跨日期传播。
        using var computationScope = scopes.CreateScope();
        var services = computationScope.ServiceProvider;
        var computationDb = services.GetRequiredService<SqlSugarContext>().Db;
        var store = new BatchProductSalesDiscountDailyStore(computationDb);
        using var dayBudget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        dayBudget.CancelAfter(DayExecutionLimit);
        var dayToken = dayBudget.Token;
        using var sourceBudget = CancellationTokenSource.CreateLinkedTokenSource(dayToken);
        computationDb.Ado.CancellationToken = dayToken;
        var day = claim.State.Date.Date;
        var elapsed = Stopwatch.StartNew();
        var stage = "核对日统计";
        try
        {
            await ValidateOwnershipAsync(store, leases, globalToken, claim, dayToken);
            if (SalesStatisticsHBSalesHistoryWindow.IsFrozen(day))
            {
                // 已发布的快照继续有效；未完成的日期无法再计算，推迟 30 天避免反复领取。
                if (claim.State.Status == "Fresh")
                {
                    await store.RecordCheckedAsync(claim, claim.State.StatisticsVersion, claim.State.SourceVersion,
                        DateTime.UtcNow, dayToken);
                }
                else
                {
                    await store.WaitForCanonicalRefreshAsync(claim, DateTime.UtcNow, dayToken,
                        "HBSales 已断开，历史折扣快照不再计算", DateTime.UtcNow.AddDays(30));
                }
                return;
            }

            var canonical = new BatchProductSalesStatisticReader(services.GetRequiredService<SqlSugarContext>().Db);
            var canonicalStatus = await canonical.StatusAsync(day, day, dayToken);
            if (!canonicalStatus.IsFresh)
            {
                var canonicalState = await store.ReadCanonicalStateAsync(day, dayToken);
                var decision = BatchProductSalesDiscountDailyStore.DecideCanonicalReconciliation(
                    claim.State.ReconcileRequested, canonicalState.Status, canonicalState.CheckedAtUtc, DateTime.UtcNow);
                if (decision.Request)
                {
                    await RequestCanonicalReconciliationAsync(services, day, dayToken);
                    await store.WaitForCanonicalRefreshAsync(claim, DateTime.UtcNow, dayToken);
                    return;
                }
                // 已请求的重算仍失败时进入退避：保留 canonical 的失败原因，便于在折扣状态中直接看到卡点。
                var backoffDiagnostic = string.Equals(canonicalState.Status, SalesStatisticRefreshStatus.Failed,
                        StringComparison.OrdinalIgnoreCase)
                    ? $"日统计重算失败，{decision.NextAttemptAtUtc:yyyy-MM-dd HH:mm} UTC 后再请求重算"
                    : null;
                await store.WaitForCanonicalRefreshAsync(claim, DateTime.UtcNow, dayToken, backoffDiagnostic,
                    decision.NextAttemptAtUtc);
                return;
            }
            await store.BeginComputationAsync(claim, DateTime.UtcNow, dayToken);
            var statisticsBefore = await store.ReadStatisticsVersionAsync(day, dayToken);
            var source = new BatchProductSalesDiscountSnapshotSourceReader(
                services.GetRequiredService<SqlSugarContext>().Db,
                services.GetRequiredService<POSMSqlSugarContext>().Db,
                services.GetRequiredService<HBSalesRecordSqlSugarContext>().Db,
                services.GetRequiredService<ILogger<BatchProductSalesDiscountSnapshotSourceReader>>());
            sourceBudget.CancelAfter(SourceReadLimit);
            // 同一个准备结果既用于变更检查，也用于聚合，完整来源扫描保持前后各一次。
            stage = "读取成交源";
            var prepared = await source.CapturePreparedAsync(day, sourceBudget.Token);
            var sourceBefore = prepared.SourceVersion;
            if (claim.State.Status == "Fresh" && !claim.State.ReconcileRequested
                && claim.State.RuleVersion == BatchProductSalesDiscountDailyStore.RuleVersion
                && claim.State.StatisticsVersion == statisticsBefore && claim.State.SourceVersion == sourceBefore)
            {
                await store.RecordCheckedAsync(claim, statisticsBefore, sourceBefore, DateTime.UtcNow, dayToken);
                return;
            }
            await ValidateOwnershipAsync(store, leases, globalToken, claim, dayToken);
            var readResult = await source.ReadPreparedDayAsync(prepared, sourceBudget.Token);
            sourceBudget.CancelAfter(Timeout.InfiniteTimeSpan);
            var sourceRows = readResult.Rows;
            stage = "核对折扣汇总";
            var statisticRows = await store.ReadDailyStatisticsAsync(day, dayToken);
            var sourceAfter = readResult.SourceVersion;
            var statisticsAfter = await store.ReadStatisticsVersionAsync(day, dayToken);
            if (sourceAfter != sourceBefore || statisticsAfter != statisticsBefore)
            {
                await PersistFailureAsync(claim, "读取期间成交源或销售日统计发生变化，等待下次围栏重试");
                return;
            }
            var payload = BuildPayload(day, statisticRows, sourceRows, out var mismatches);
            if (mismatches.Count > 0)
            {
                var mismatchDiagnostic = FormatMismatchDiagnostic(mismatches);
                logger.LogWarning("商品折扣日快照聚合不一致: {Date}, {@Mismatches}", day, mismatches);
                if (claim.State.ReconcileRequested)
                {
                    // 已请求的 canonical 已发布但数值仍不一致，记为真正计算失败；下次退避重试可重新入队。
                    await PersistFailureAsync(claim, mismatchDiagnostic, preserveExistingReconcileRequested: false);
                    return;
                }
                // 上面已处理 ReconcileRequested=true；此处首次发现不一致，总是请求一次 canonical 重算。
                await RequestCanonicalReconciliationAsync(services, day, dayToken);
                await store.WaitForCanonicalRefreshAsync(claim, DateTime.UtcNow, dayToken, mismatchDiagnostic);
                return;
            }
            await ValidateOwnershipAsync(store, leases, globalToken, claim, dayToken);
            stage = "发布日快照";
            await store.PublishAsync(claim, statisticsAfter, sourceAfter, payload, DateTime.UtcNow, dayToken);
            logger.LogInformation("商品折扣日快照已发布: {Date}, Products={ProductCount}, ElapsedMs={ElapsedMs}",
                day, payload.Count, elapsed.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // 原始异常先落日志；SQL Server 取消也可能包装成 SqlException，而非 OperationCanceledException。
            logger.LogWarning(ex, "商品折扣日快照失败: {Date}, Attempt={Attempts}, Stage={Stage}, ElapsedMs={ElapsedMs}",
                day, claim.State.Attempts, stage, elapsed.ElapsedMilliseconds);
            var reason = stoppingToken.IsCancellationRequested ? "服务停止，等待重试"
                : dayBudget.IsCancellationRequested ? $"单日处理超时，阶段：{stage}"
                : sourceBudget.IsCancellationRequested ? "来源读取超时，等待重试"
                : ex.Message;
            await PersistFailureAsync(claim, reason);
            stoppingToken.ThrowIfCancellationRequested();
        }
        finally
        {
            computationDb.Ado.RemoveCancellationToken();
        }
    }

    private Task PersistFailureAsync(BatchProductSalesDiscountDailyStore.ClaimedDay claim, string reason,
        bool preserveExistingReconcileRequested = true) =>
        PersistTerminalAsync((services, token) =>
            new BatchProductSalesDiscountDailyStore(services.GetRequiredService<SqlSugarContext>().Db)
                .FinishFailureAsync(claim, reason, DateTime.UtcNow, false, token, preserveExistingReconcileRequested));

    private async Task PersistTerminalAsync(Func<IServiceProvider, CancellationToken, Task> persist)
    {
        using var terminalScope = scopes.CreateScope();
        using var deadline = new CancellationTokenSource(TerminalPersistenceLimit);
        var db = terminalScope.ServiceProvider.GetRequiredService<SqlSugarContext>().Db;
        db.Ado.CancellationToken = deadline.Token;
        try { await persist(terminalScope.ServiceProvider, deadline.Token); }
        catch (Exception ex)
        {
            // 数据库不可用时保留原始计算异常，现有租约 TTL 仍负责最终恢复。
            logger.LogError(ex, "折扣日快照终态持久化失败，将由租约到期恢复");
        }
        finally { db.Ado.RemoveCancellationToken(); }
    }

    internal sealed record BatchProductSalesDiscountMismatch(string ProductCode, string StoreCode,
        decimal ExpectedQuantity, decimal SourceQuantity, decimal ExpectedAmount, decimal SourceAmount);

    internal static Dictionary<string, List<BatchProductSalesAggregateRow>> BuildPayload(DateTime day,
        IReadOnlyCollection<BatchProductSalesAggregateRow> statistics, IReadOnlyCollection<BatchProductSalesAggregateRow> source,
        out IReadOnlyList<BatchProductSalesDiscountMismatch> mismatches)
    {
        var expected = statistics.GroupBy(x => x.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        var actual = source.GroupBy(x => x.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, List<BatchProductSalesAggregateRow>>(StringComparer.OrdinalIgnoreCase);
        var diagnosticRows = new List<BatchProductSalesDiscountMismatch>(capacity: 5);
        foreach (var product in expected.Keys.Union(actual.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(product => product, StringComparer.OrdinalIgnoreCase))
        {
            var statisticRows = expected.GetValueOrDefault(product) ?? [];
            var sourceRows = actual.GetValueOrDefault(product) ?? [];
            if (!BatchProductSalesStatisticReader.TotalsMatch(statisticRows, sourceRows))
            {
                diagnosticRows.AddRange(BuildMismatchDiagnostics(product, statisticRows, sourceRows,
                    remaining: 5 - diagnosticRows.Count));
                continue;
            }
            result[product] = BatchProductSalesStatisticReader.PrepareSnapshot(statisticRows, sourceRows)
                .Where(x => x.Date.Date == day).ToList();
        }
        mismatches = diagnosticRows;
        return result;
    }

    private static IReadOnlyList<BatchProductSalesDiscountMismatch> BuildMismatchDiagnostics(string product,
        IEnumerable<BatchProductSalesAggregateRow> statistics, IEnumerable<BatchProductSalesAggregateRow> source, int remaining)
    {
        static Dictionary<(DateTime Date, string Store), (decimal Quantity, decimal Amount)> Totals(
            IEnumerable<BatchProductSalesAggregateRow> rows) => rows
                .GroupBy(row => (row.Date.Date, row.BranchCode.ToUpperInvariant()))
                .ToDictionary(group => group.Key, group => (group.Sum(row => row.Quantity), group.Sum(row => row.SalesAmount)));

        var expected = Totals(statistics);
        var actual = Totals(source);
        return expected.Keys.Union(actual.Keys).OrderBy(key => key.Date).ThenBy(key => key.Store, StringComparer.Ordinal)
            .Select(key => (Key: key, Expected: expected.GetValueOrDefault(key), Source: actual.GetValueOrDefault(key)))
            // 与 TotalsMatch 完全相同：数量严格相等，成交金额按日统计存储精度四位小数比对。
            .Where(row => row.Expected.Quantity != row.Source.Quantity
                || row.Expected.Amount != Math.Round(row.Source.Amount, 4, MidpointRounding.AwayFromZero))
            .Take(Math.Max(0, remaining))
            .Select(row => new BatchProductSalesDiscountMismatch(product.Trim().ToUpperInvariant(), row.Key.Store,
                row.Expected.Quantity, row.Source.Quantity, row.Expected.Amount, row.Source.Amount))
            .ToList();
    }

    private static string FormatMismatchDiagnostic(IReadOnlyList<BatchProductSalesDiscountMismatch> mismatches)
    {
        const int maxLength = 2000;
        var value = JsonSerializer.Serialize(new { Type = "ProductStoreDailyTotalsMismatch", Mismatches = mismatches });
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    /// <summary>覆盖范围按日历年计算，避免闰年把两年回填截短为 730 或 731 天。</summary>
    internal static IReadOnlyList<DateTime> BuildCoverageDays(DateTime today, int historicalYears)
    {
        var start = today.Date.AddYears(-historicalYears);
        return Enumerable.Range(0, (today.Date - start).Days + 1).Select(offset => start.AddDays(offset)).ToArray();
    }

    private static async Task ValidateOwnershipAsync(BatchProductSalesDiscountDailyStore store,
        ScheduledTaskLeaseService leases, string globalToken, BatchProductSalesDiscountDailyStore.ClaimedDay claim,
        CancellationToken token)
    {
        await leases.EnsureActiveAsync(GlobalLeaseTaskType, GlobalLeaseScope, globalToken, GlobalLeaseDuration, "折扣日快照持久队列");
        await store.EnsureOwnershipAsync(claim, DateTime.UtcNow, token);
    }

    /// <summary>既有持久队列会把 Fresh 日期显式重置为排队重算；折扣 worker 不直接写 canonical 日统计。</summary>
    private static Task RequestCanonicalReconciliationAsync(IServiceProvider services, DateTime day, CancellationToken token) =>
        services.GetRequiredService<IProductStoreDailyStatisticQueueService>()
            .EnqueueAsync([day], "batch-product-sales-discount-reconcile", maxConcurrency: 1, cancellationToken: token);
}
