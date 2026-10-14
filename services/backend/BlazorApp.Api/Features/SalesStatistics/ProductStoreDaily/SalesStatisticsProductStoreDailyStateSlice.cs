using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.Background;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBSalesRecord;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;

namespace BlazorApp.Api.Services
{
    /// <summary>销售统计垂直切片：SalesStatisticsProductStoreDailyStateSlice。</summary>
    internal sealed class SalesStatisticsProductStoreDailyStateSlice : SalesStatisticsSliceBase
    {
        public SalesStatisticsProductStoreDailyStateSlice(SalesStatisticsSliceContext shared)
            : base(shared) { }

    internal static decimal? ResolveUnitCost(
        string branchCode,
        string supplierCode,
        string productCode,
        Dictionary<string, decimal?> storeCostMap,
        Dictionary<string, decimal?> productCostMap,
        Dictionary<string, decimal?> warehouseCostMap,
        out string costSource
    ) => SalesStatisticsProductStoreDailyDomainRules.ResolveUnitCost(
        branchCode,
        supplierCode,
        productCode,
        storeCostMap,
        productCostMap,
        warehouseCostMap,
        out costSource
    );

    internal sealed record ProductStatisticStatusResult(
        string Status,
        string? ErrorMessage,
        string? SourceProductVersion = null);

    internal static async Task<ProductStatisticStatusResult> BuildProductStatisticStatusAsync(
        SqlSugarContext context,
        DateTime targetDate,
        List<ProductStoreDailySalesStatistic> statisticsList,
        ProductStatisticDiagnostics diagnostics,
        DateTime? lastSourceUploadTime,
        IReadOnlyList<ProductStoreDailyBranchRollup> supplementalReturnAdjustments,
        IReadOnlyList<StoreSalesStatistic>? atomicStoreStatistics = null
    )
    {
        var existingStoreSales = atomicStoreStatistics == null
            ? await context.Db.Queryable<StoreSalesStatistic>()
                .Where(s => s.Date == targetDate)
                .Select(s => new { s.BranchCode, s.TotalAmount, s.TotalQuantity })
                .ToListAsync()
            : atomicStoreStatistics.Select(s => new
            {
                s.BranchCode,
                s.TotalAmount,
                s.TotalQuantity,
            }).ToList();

        var storeRollups = existingStoreSales
            .Where(x => !string.IsNullOrWhiteSpace(x.BranchCode))
            .Select(x => new ProductStoreDailyBranchRollup(
                x.BranchCode!,
                x.TotalAmount,
                x.TotalQuantity
            ))
            .ToList();
        var effectiveStoreRollups = ProductStoreDailyReconciliationCalculator
            .ApplyExistingStoreAdjustments(storeRollups, supplementalReturnAdjustments);

        var productRollups = statisticsList
            .Where(x => !string.IsNullOrWhiteSpace(x.BranchCode))
            .Select(x => new ProductStoreDailyBranchRollup(
                x.BranchCode,
                x.TotalAmount,
                x.TotalQuantity
            ));

        var branchDiagnostics = diagnostics.BranchDiagnostics
            .ToDictionary(
                item => item.Key,
                item => new ProductStoreDailyBranchDiagnostic(
                    item.Value.UnmatchedSupplierAmount,
                    item.Value.UnmatchedSupplierQuantity,
                    item.Value.UnmatchedSupplierProductCount
                )
            );

        var reconciliation = ProductStoreDailyReconciliationCalculator.Calculate(
            targetDate,
            productRollups,
            effectiveStoreRollups,
            branchDiagnostics
        );

        return new ProductStatisticStatusResult(
            reconciliation.Status,
            reconciliation.ErrorMessage,
            SupplierStatisticVersion.ComputeProductVersion(statisticsList));
    }

    internal static async Task UpsertProductStatisticStateAsync(
        SqlSugarContext context,
        DateTime targetDate,
        ProductStatisticStatusResult status,
        DateTime? lastSourceUploadTime,
        bool overwriteLastSourceUploadTime = false,
        bool markAggregated = false
    )
    {
        // markAggregated：对账不一致但分店与商品行已经实际发布时，状态虽是 Failed，仍要记录本次聚合时间，
        // 否则当天首次刷新就对账失败的日期会因没有聚合时间被报表当成「尚未发布」。
        var completed = IsCompletedProductStatus(status.Status) || markAggregated;
        var nextDate = targetDate.Date.AddDays(1);
        var existing = await context.Db.Queryable<SalesStatisticRefreshState>()
            // SQLite/SQL Server 的 DateTime 精度不同，按规范化日期范围读取已有状态。
            .Where(s =>
                s.StatisticType == SalesStatisticType.ProductStoreDaily
                && s.Date >= targetDate.Date
                && s.Date < nextDate
            )
            .FirstAsync();

        if (existing == null)
        {
            existing = new SalesStatisticRefreshState
            {
                StatisticType = SalesStatisticType.ProductStoreDaily,
                Date = targetDate,
            };
            existing.Status = status.Status;
            existing.LastSourceUploadTime = lastSourceUploadTime;
            existing.SourceTimeZone = "POSM_LOCAL";
            existing.SourceProductVersion = IsCompletedProductStatus(status.Status)
                ? status.SourceProductVersion
                : null;
            existing.LastCheckedAtUtc = DateTime.UtcNow;
            existing.ErrorMessage = status.ErrorMessage;
            if (completed)
            {
                existing.LastAggregatedAtUtc = DateTime.UtcNow;
                existing.CompletedAtUtc = DateTime.UtcNow;
            }
            await context.Db.Insertable(existing).ExecuteCommandAsync();
            return;
        }

        existing.Status = status.Status;
        existing.LastSourceUploadTime = overwriteLastSourceUploadTime
            ? lastSourceUploadTime
            : lastSourceUploadTime ?? existing.LastSourceUploadTime;
        existing.SourceTimeZone = "POSM_LOCAL";
        if (IsCompletedProductStatus(status.Status)
            && !string.IsNullOrWhiteSpace(status.SourceProductVersion))
            existing.SourceProductVersion = status.SourceProductVersion;
        existing.LastCheckedAtUtc = DateTime.UtcNow;
        existing.ErrorMessage = status.ErrorMessage;
        if (completed)
        {
            existing.LastAggregatedAtUtc = DateTime.UtcNow;
            existing.CompletedAtUtc = DateTime.UtcNow;
        }
        await context.Db.Updateable(existing).ExecuteCommandAsync();
    }

    internal static async Task UpsertStatisticStateAsync(
        SqlSugarContext context,
        string statisticType,
        DateTime targetDate,
        string status,
        DateTime? lastSourceUploadTime,
        string? errorMessage,
        bool overwriteLastSourceUploadTime = false,
        string? sourceProductVersion = null,
        bool markAggregated = false
    )
    {
        var now = DateTime.UtcNow;
        var nextDate = targetDate.Date.AddDays(1);
        var existing = await context.Db.Queryable<SalesStatisticRefreshState>()
            .Where(s =>
                s.StatisticType == statisticType
                && s.Date >= targetDate.Date
                && s.Date < nextDate
            )
            .FirstAsync();
        var isNew = existing == null;

        if (isNew)
        {
            existing = new SalesStatisticRefreshState
            {
                StatisticType = statisticType,
                Date = targetDate.Date,
                SourceTimeZone = "POSM_LOCAL",
            };
        }
        var state = existing!;

        state.Status = status;
        if ((status == SalesStatisticRefreshStatus.Fresh || status == ProvisionalFreshStatus)
            && !string.IsNullOrWhiteSpace(sourceProductVersion))
            state.SourceProductVersion = sourceProductVersion;
        state.LastSourceUploadTime = overwriteLastSourceUploadTime
            ? lastSourceUploadTime
            : lastSourceUploadTime ?? state.LastSourceUploadTime;
        state.LastCheckedAtUtc = now;
        state.ErrorMessage = errorMessage;
        if (status == SalesStatisticRefreshStatus.Running)
        {
            state.StartedAtUtc = now;
        }
        else if (status == SalesStatisticRefreshStatus.Fresh || status == ProvisionalFreshStatus || markAggregated)
        {
            state.LastAggregatedAtUtc = now;
            state.CompletedAtUtc = now;
        }

        if (isNew)
        {
            await context.Db.Insertable(state).ExecuteCommandAsync();
        }
        else
        {
            await context.Db.Updateable(state).ExecuteCommandAsync();
        }
    }

    /// <summary>
    /// 只有同日全部报表维度都已实际提交后，才发布报表可读快照。
    /// 运行、跳过和失败均不得触碰已有发布记录，因此最后成功时间可跨失败尝试保留。
    /// </summary>
    internal static async Task PublishRevenueReportSnapshotAsync(
        SqlSugarContext context,
        DateTime targetDate
    )
    {
        var date = targetDate.Date;
        var nextDate = date.AddDays(1);
        var requiredTypes = SalesStatisticType.DailyAlignmentTypes;
        var states = await context.Db.Queryable<SalesStatisticRefreshState>()
            .Where(state =>
                requiredTypes.Contains(state.StatisticType)
                && state.Date >= date
                && state.Date < nextDate
            )
            .ToListAsync();

        // 对账不一致（例如某店有订单表头但明细没上传）时分店与商品行已经整体发布，状态只是带警告的 Failed；
        // 这类状态不能挡住报表快照发布，否则一家店的明细缺口会让全部门店的营业额停止更新。
        // 真异常导致的 Failed 不会推进聚合时间，也不带对账前缀，仍然拒绝发布。
        var reconciliationTypes = new[]
        {
            SalesStatisticType.StoreSales,
            SalesStatisticType.ProductStoreDaily,
            SalesStatisticType.AustralianSupplierStoreSales,
            SalesStatisticType.ChinaSupplierStoreSales,
        };
        var missingOrIncomplete = requiredTypes.Where(type =>
            !states.Any(state =>
                state.StatisticType == type
                && state.LastAggregatedAtUtc.HasValue
                && state.CompletedAtUtc.HasValue
                && (state.Status == SalesStatisticRefreshStatus.Fresh
                    || (state.Status == SalesStatisticRefreshStatus.Failed
                        && reconciliationTypes.Contains(type)
                        && ProductStoreDailyReconciliationCalculator.IsReconciliationFailure(state.ErrorMessage)))
            )
        ).ToList();
        if (missingOrIncomplete.Count > 0)
        {
            throw new InvalidOperationException(
                $"统计维度尚未全部提交，不能发布报表快照: {date:yyyy-MM-dd}; {string.Join(",", missingOrIncomplete)}"
            );
        }

        var now = DateTime.UtcNow;
        var published = await context.Db.Queryable<SalesStatisticRefreshState>()
            .Where(state =>
                state.StatisticType == SalesStatisticType.RevenueReportPublished
                && state.Date >= date
                && state.Date < nextDate
            )
            .FirstAsync();
        if (published == null)
        {
            await context.Db.Insertable(new SalesStatisticRefreshState
            {
                StatisticType = SalesStatisticType.RevenueReportPublished,
                Date = date,
                Status = SalesStatisticRefreshStatus.Fresh,
                SourceTimeZone = "POSM_LOCAL",
                LastAggregatedAtUtc = now,
                LastCheckedAtUtc = now,
                CompletedAtUtc = now,
            }).ExecuteCommandAsync();
            return;
        }

        published.Status = SalesStatisticRefreshStatus.Fresh;
        published.LastAggregatedAtUtc = now;
        published.LastCheckedAtUtc = now;
        published.CompletedAtUtc = now;
        published.ErrorMessage = null;
        await context.Db.Updateable(published).ExecuteCommandAsync();
    }

    private static bool IsCompletedProductStatus(string status) =>
        status == SalesStatisticRefreshStatus.Fresh || status == SalesStatisticRefreshStatus.ProvisionalFresh;

    internal static async Task<DateTime?> QueryDailyPosmSourceWatermarkAsync(
        POSMSqlSugarContext posmContext,
        DateTime date
    )
    {
        var targetDate = date.Date;
        var nextDate = targetDate.AddDays(1);

        // ADO 不会自动应用 Queryable 的 NOLOCK 配置，显式保持原查询的隔离规则。
        var settings = posmContext.Db.CurrentConnectionConfig.MoreSettings;
        var noLockHint = posmContext.Db.CurrentConnectionConfig.DbType == SqlSugar.DbType.SqlServer
            && settings?.IsWithNoLockQuery == true
            && !(settings.DisableWithNoLockWithTran == true && posmContext.Db.Ado.Transaction != null)
                ? " WITH (NOLOCK)"
                : string.Empty;

        // 用固定参数化 SQL 替换本次失败经过的 MaxAsync 查询路径，一次读取三个来源的水位。
        // 保留传入的连接，使当天统计继续参与调用方的 Snapshot 事务。
        return await posmContext.Db.Ado.SqlQuerySingleAsync<DateTime?>(
            $"""
            SELECT MAX([SourceWatermark])
            FROM (
                SELECT MAX([LastUploadTime]) AS [SourceWatermark]
                FROM [sales_order]{noLockHint}
                WHERE [Status] IN (1, 3, 4)
                  AND [OrderTime] >= @TargetDate AND [OrderTime] < @NextDate
                UNION ALL
                SELECT MAX([payment].[LastUploadTime]) AS [SourceWatermark]
                FROM [payment_detail] AS [payment]{noLockHint}
                INNER JOIN [sales_order] AS [orders]{noLockHint} ON [payment].[OrderGuid] = [orders].[OrderGuid]
                WHERE [orders].[Status] IN (1, 3, 4)
                  AND [orders].[OrderTime] >= @TargetDate AND [orders].[OrderTime] < @NextDate
                UNION ALL
                SELECT MAX([detail].[LastUploadTime]) AS [SourceWatermark]
                FROM [sales_order_detail] AS [detail]{noLockHint}
                INNER JOIN [sales_order] AS [orders]{noLockHint} ON [detail].[OrderGuid] = [orders].[OrderGuid]
                WHERE [orders].[Status] IN (1, 3, 4)
                  AND [orders].[OrderTime] >= @TargetDate AND [orders].[OrderTime] < @NextDate
            ) AS [watermarks]
            """,
            new { TargetDate = targetDate, NextDate = nextDate }
        );
    }

    internal static async Task<DateTime?> QueryDailySourceWatermarkAsync(
        POSMSqlSugarContext posmContext,
        HBSalesRecordSqlSugarContext? hbSalesContext,
        DateTime date,
        IReadOnlyCollection<ProductStoreDailySourceRow>? preloadedHBSalesRows = null
    )
    {
        var targetDate = date.Date;
        var nextDate = targetDate.AddDays(1);
        var mainCheckoutDateWindowStart = targetDate.AddDays(
            -HBSalesMainCheckoutDateWindowDays
        );
        var mainCheckoutDateWindowEnd = nextDate.AddDays(HBSalesMainCheckoutDateWindowDays);
        var watermarks = new List<DateTime?>
        {
            await QueryDailyPosmSourceWatermarkAsync(posmContext, targetDate),
        };

        if (SalesStatisticsHBSalesHistoryWindow.Includes(targetDate))
        {
            var requiredHBSalesContext = hbSalesContext
                ?? throw new InvalidOperationException("HBSales 历史窗口内的统计水位查询缺少 HBSalesRecord 上下文");
            if (preloadedHBSalesRows != null)
            {
                // 仅 pre 阶段复用已加载明细；post 阶段不传此参数，必须重新查库检测漂移。
                watermarks.Add(
                    SalesStatisticsProductStoreDailyDomainRules.GetHBSalesSourceWatermark(
                        preloadedHBSalesRows
                    )
                );
            }
            else
            {
                var originalCommandTimeout = requiredHBSalesContext.Db.Ado.CommandTimeOut;
                requiredHBSalesContext.Db.Ado.CommandTimeOut = Math.Max(
                    originalCommandTimeout,
                    CommandTimeoutSeconds
                );
                HBSalesSourceWatermarkRow? hbSalesWatermark;
                try
                {
                    hbSalesWatermark = await requiredHBSalesContext.Db.Queryable<SalesOrderMain>()
                        .LeftJoin<SalesOrderDetailRecord>((main, detail) =>
                            main.B销售单号 == detail.B销售单号
                        )
                        .Where((main, detail) =>
                            detail.B结账日期.HasValue
                            && detail.B结账日期.Value >= targetDate
                            && detail.B结账日期.Value < nextDate
                            && main.B结账日期.HasValue
                            && main.B结账日期.Value >= mainCheckoutDateWindowStart
                            && main.B结账日期.Value < mainCheckoutDateWindowEnd
                            && (main.B单据类型 == null || main.B单据类型.Trim() != "2")
                        )
                        .Select((main, detail) => new HBSalesSourceWatermarkRow
                        {
                            MainLastModifiedAt = SqlFunc.AggregateMax(main.FGC_LastModifyDate),
                            MainCreatedAt = SqlFunc.AggregateMax(main.FGC_CreateDate),
                            DetailLastModifiedAt = SqlFunc.AggregateMax(detail.FGC_LastModifyDate),
                            DetailCreatedAt = SqlFunc.AggregateMax(detail.FGC_CreateDate),
                        })
                        .FirstAsync();
                }
                finally
                {
                    requiredHBSalesContext.Db.Ado.CommandTimeOut = originalCommandTimeout;
                }
                if (hbSalesWatermark != null)
                {
                    watermarks.Add(
                        SalesStatisticsProductStoreDailyDomainRules.GetLatestSourceTime(
                            hbSalesWatermark.MainLastModifiedAt,
                            hbSalesWatermark.MainCreatedAt,
                            hbSalesWatermark.DetailLastModifiedAt,
                            hbSalesWatermark.DetailCreatedAt
                        )
                    );
                }
            }
        }

        var values = watermarks
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        return values.Count == 0 ? null : values.Max();
    }

    internal static string BuildProductStoreDailySubmitMessage(int submittedCount, int skippedCount)
    {
        if (submittedCount == 0 && skippedCount > 0)
        {
            return $"所选 {skippedCount} 天商品统计已有任务执行中，未重复提交";
        }

        if (skippedCount > 0)
        {
            return $"已提交 {submittedCount} 天商品统计重算，跳过 {skippedCount} 天执行中的任务";
        }

        return $"已提交 {submittedCount} 天商品统计重算任务";
    }

    internal static async Task UpsertProductStatisticQueuedStateAsync(
        SqlSugarContext context,
        DateTime targetDate,
        Guid jobId,
        string? requestedBy
    )
    {
        var now = DateTime.UtcNow;
        var existing = await context.Db.Queryable<SalesStatisticRefreshState>()
            .Where(s => s.StatisticType == SalesStatisticType.ProductStoreDaily && s.Date == targetDate)
            .FirstAsync();

        if (existing == null)
        {
            await context.Db.Insertable(new SalesStatisticRefreshState
            {
                StatisticType = SalesStatisticType.ProductStoreDaily,
                Date = targetDate,
                Status = SalesStatisticRefreshStatus.Queued,
                SourceTimeZone = "POSM_LOCAL",
                JobId = jobId,
                RequestedBy = requestedBy,
                RequestedAtUtc = now,
                LastCheckedAtUtc = now,
            }).ExecuteCommandAsync();
            return;
        }

        existing.Status = SalesStatisticRefreshStatus.Queued;
        existing.JobId = jobId;
        existing.RequestedBy = requestedBy;
        existing.RequestedAtUtc = now;
        existing.StartedAtUtc = null;
        existing.CompletedAtUtc = null;
        existing.LastCheckedAtUtc = now;
        existing.ErrorMessage = null;
        await context.Db.Updateable(existing).ExecuteCommandAsync();
    }

    /// <summary>
    /// 在主统计事务内以 Running + JobId 条件触碰状态行，防止租约已换主的旧 worker 覆盖后继任务。
    /// </summary>
    internal static async Task FenceProductStatisticExecutionOwnerAsync(
        SqlSugarContext context,
        DateTime targetDate,
        Guid? expectedJobId)
    {
        if (!expectedJobId.HasValue)
            return;
        if (expectedJobId.Value == Guid.Empty)
            throw new InvalidOperationException("队列任务 JobId 不能为空");

        var normalizedDate = targetDate.Date;
        var affectedRows = await context.Db.Updateable<SalesStatisticRefreshState>()
            .SetColumns(state => state.LastCheckedAtUtc == state.LastCheckedAtUtc)
            .Where(state =>
                state.StatisticType == SalesStatisticType.ProductStoreDaily
                && state.Date >= normalizedDate
                && state.Date < normalizedDate.AddDays(1)
                && state.JobId == expectedJobId.Value
                && state.Status == SalesStatisticRefreshStatus.Running)
            .ExecuteCommandAsync();
        if (affectedRows != 1)
        {
            throw new InvalidOperationException(
                $"商品统计执行权已变化，拒绝旧 worker 写入: {normalizedDate:yyyy-MM-dd} {expectedJobId.Value}");
        }
    }

    }
}
