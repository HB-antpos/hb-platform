using BlazorApp.Api.Data;
using BlazorApp.Api.Services.Background;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services;

internal sealed record AutomaticRetryCandidate(DateTime Date, DateTime? LastCheckedAtUtc);

/// <summary>数据库批次可恢复执行；没有审计表时不自动建表，不改变统计数据。</summary>
public sealed class SalesCostBackfillWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<SalesCostBackfillWorker> logger) : BackgroundService
{
    private const int DefaultAutoRetryMaxDatesPerUtcDay = 31;
    private const int DefaultAutoRetryRecentCompletedDays = 7;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<SalesCostBackfillService>();
                if (service.SchemaReady())
                {
                    var leases = scope.ServiceProvider.GetRequiredService<ScheduledTaskLeaseService>();
                    var lease = await leases.TryAcquireAsync("SalesCostBackfillWorker", "global", TimeSpan.FromMinutes(30));
                    if (lease.Acquired && lease.Lease?.LeaseToken is { Length: > 0 } leaseToken)
                    {
                        try
                        {
                            worked = await service.RunOneAsync(stoppingToken);
                            if (!worked && configuration.GetValue<bool>("SalesStatistics:CostBackfillAutoRetryEnabled"))
                                worked = await ScheduleRetryAsync(scope.ServiceProvider.GetRequiredService<SqlSugarContext>(), service);
                        }
                        finally { await leases.CompleteAsync("SalesCostBackfillWorker", "global", leaseToken, true); }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "成本回填后台批次执行失败"); }
            try { await Task.Delay(worked ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal static DateTime? SelectAutomaticRetryDate(
        IReadOnlyCollection<DateTime> recentCandidates,
        IReadOnlyCollection<AutomaticRetryCandidate> historicalCandidates,
        int checkedToday,
        int maxDatesPerDay)
    {
        if (maxDatesPerDay <= 0 || checkedToday >= maxDatesPerDay)
            return null;

        var recent = recentCandidates
            .Select(date => date.Date)
            .Distinct()
            .OrderByDescending(date => date)
            .ToList();
        var historical = historicalCandidates
            .GroupBy(candidate => candidate.Date.Date)
            .Select(group => group.OrderBy(candidate => candidate.LastCheckedAtUtc ?? DateTime.MinValue)
                .ThenBy(candidate => candidate.Date)
                .First())
            .OrderBy(candidate => candidate.LastCheckedAtUtc ?? DateTime.MinValue)
            .ThenBy(candidate => candidate.Date)
            .ToList();

        // 每日最后一个名额优先给历史轮转；这样近期日优先，同时不会让旧日期永远饥饿。
        var reserveHistory = maxDatesPerDay >= 2 && checkedToday == maxDatesPerDay - 1;
        if (reserveHistory && historical.Count > 0)
            return historical[0].Date.Date;
        if (recent.Count > 0)
            return recent[0];
        return historical.Count > 0 ? historical[0].Date.Date : null;
    }

    private async Task<bool> ScheduleRetryAsync(SqlSugarContext context, SalesCostBackfillService service)
    {
        var ready = await context.Db.Queryable<SalesCostBackfillBatch>()
            .Where(x => x.Automatic && x.Status == "Previewed").OrderBy(x => x.CreatedAtUtc).FirstAsync();
        if (ready != null)
        {
            // 瞬时预览失败只自动补试一次；持续无来源的日期交给下一日轮次，避免热循环。
            var failedPreview = string.IsNullOrEmpty(ready.PreviewRetriedBy)
                && await context.Db.Queryable<SalesCostBackfillDay>()
                    .AnyAsync(x => x.BatchId == ready.Id && x.Status == "Failed" && x.FailedOperation == "Previewing");
            if (failedPreview && await service.RetryPreviewAsync(ready.Id, "cost-retry")) return true;
            return await service.RequestAsync(ready.Id, false, "cost-retry");
        }
        var nowUtc = DateTime.UtcNow;
        var utcDayStart = nowUtc.Date;
        var maxDatesPerDay = Math.Clamp(
            configuration.GetValue("SalesStatistics:CostBackfillAutoRetryMaxDatesPerUtcDay",
                DefaultAutoRetryMaxDatesPerUtcDay),
            1,
            31);
        var recentCompletedDays = Math.Clamp(
            configuration.GetValue("SalesStatistics:CostBackfillAutoRetryRecentCompletedDays",
                DefaultAutoRetryRecentCompletedDays),
            1,
            31);
        var automaticChecks = await context.Db.Queryable<SalesCostBackfillDay>()
            .InnerJoin<SalesCostBackfillBatch>((day, batch) => day.BatchId == batch.Id)
            .Where((day, batch) => batch.Automatic)
            .Select((day, batch) => new { day.Date, day.UpdatedAtUtc })
            .ToListAsync();
        var lastCheckedByDate = automaticChecks
            .GroupBy(check => check.Date.Date)
            .ToDictionary(group => group.Key, group => group.Max(check => check.UpdatedAtUtc));
        var checkedToday = lastCheckedByDate.Count(check => check.Value >= utcDayStart);
        if (checkedToday >= maxDatesPerDay)
            return false;

        var earliest = new DateTime(2025, 1, 1);
        var latestCompleted = SalesStatisticsBusinessDate.Today().AddDays(-1);
        var recentStart = latestCompleted.AddDays(-(recentCompletedDays - 1));
        var gapDates = await context.Db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(x => x.Date >= earliest && x.Date <= latestCompleted
                && (x.TotalCost == null || x.GrossProfit == null
                    || (x.TotalAmount > 0 && x.GrossMarginRate == null)))
            .Select(x => x.Date).Distinct().ToListAsync();
        var uncheckedDates = gapDates
            .Select(date => date.Date)
            .Distinct()
            // HBSales 断开后历史窗口内的日期无法回填，不能选中，否则每轮都会失败。
            .Where(date => !SalesStatisticsHBSalesHistoryWindow.IsFrozen(date))
            .Where(date => !lastCheckedByDate.TryGetValue(date, out var checkedAt)
                || checkedAt < utcDayStart)
            .ToList();
        var recentCandidates = uncheckedDates.Where(date => date >= recentStart).ToList();
        var historicalCandidates = uncheckedDates
            .Where(date => date < recentStart)
            .Select(date => new AutomaticRetryCandidate(
                date,
                lastCheckedByDate.TryGetValue(date, out var checkedAt) ? checkedAt : null))
            .ToList();
        var selectedDate = SelectAutomaticRetryDate(
            recentCandidates,
            historicalCandidates,
            checkedToday,
            maxDatesPerDay);
        if (!selectedDate.HasValue)
            return false;

        // 自动发现仍通过单日冻结预览和后续 Apply 执行；来源变化会在下一轮轮转重新获得机会。
        await service.PreviewAsync(selectedDate.Value, selectedDate.Value, "cost-retry", automatic: true);
        return true;
    }
}
