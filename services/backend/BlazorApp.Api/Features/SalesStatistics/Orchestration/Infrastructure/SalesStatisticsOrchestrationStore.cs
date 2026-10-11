using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBSalesRecord;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;

namespace BlazorApp.Api.Services;

/// <summary>
/// 完整刷新编排切片的持久化适配器。所有 SqlSugar 查询、写入和事务均集中在此处。
/// </summary>
internal sealed class SalesStatisticsOrchestrationStore
{
    private const int BatchSize = 5000;
    private const string UnknownSupplierCode = "UNKNOWN";

    internal async Task<SalesStatisticRefreshState?> GetProductStoreDailyRefreshStateAsync(
        SqlSugarContext context,
        DateTime date)
    {
        // 先规范化为日期参数，避免 ORM 在不同数据库中把 Date 成员翻译成不同格式的字符串。
        var targetDate = date.Date;
        // SqlSugar 的 FirstAsync 可返回空记录，但其泛型签名未标注可空；在此处 await 后如实暴露契约。
        return await context.Db
            .Queryable<SalesStatisticRefreshState>()
            .Where(state =>
                state.StatisticType == SalesStatisticType.ProductStoreDaily
                && state.Date == targetDate)
            .FirstAsync();
    }

    internal async Task UpsertDailySalesStatisticAsync(
        SqlSugarContext context,
        DailySalesStatistic statistic)
    {
        var existing = await context.Db.Queryable<DailySalesStatistic>()
            .Where(row => row.Date == statistic.Date)
            .FirstAsync();
        if (existing != null)
        {
            await context.Db.Updateable(statistic).ExecuteCommandAsync();
            return;
        }

        await context.Db.Insertable(statistic).ExecuteCommandAsync();
    }

    internal async Task ReplaceStoreStatisticsAsync(
        SqlSugarContext context,
        ILogger logger,
        DateTime targetDate,
        List<string>? branchCodes,
        List<StoreSalesStatistic> statisticsList,
        Guid? expectedProductStatisticJobId = null,
        Func<Task>? validateExecutionOwnershipBeforeCommitAsync = null,
        DateTime? sourceWatermark = null,
        Func<Task>? validateSourceWatermarkBeforeCommitAsync = null)
    {
        var targetBranchCodes = SalesStatisticsCodeRules.NormalizeBranchCodes(branchCodes);
        await SalesStatisticsTransactionExecutor.ExecuteAsync(
            beginAsync: () => context.Db.Ado.BeginTranAsync(),
            workAsync: async () =>
            {
                // 前置分店写入复用商品队列的 JobId fencing，避免过期 worker 在商品提交前先替换营业额。
                await SalesStatisticsProductStoreDailyStateSlice.FenceProductStatisticExecutionOwnerAsync(
                    context,
                    targetDate,
                    expectedProductStatisticJobId);
                var deleteable = context.Db.Deleteable<StoreSalesStatistic>()
                    .Where(row => row.Date == targetDate);
                if (targetBranchCodes.Any())
                {
                    deleteable = deleteable.Where(row =>
                        targetBranchCodes.Contains(row.BranchCode));
                }

                var deletedCount = await deleteable.ExecuteCommandAsync();
                logger.LogInformation("删除 {Count} 条分店统计旧记录", deletedCount);
                if (statisticsList.Any())
                {
                    context.Db.Fastest<StoreSalesStatistic>()
                        .PageSize(BatchSize)
                        .BulkCopy(statisticsList);
                }

                // 队列前置路径必须先确认 POSM 仍是构建时的同一版本，再将行和 StoreSales
                // Fresh/watermark 一起提交；报表完整性和缓存指纹依赖这个状态行。
                if (validateSourceWatermarkBeforeCommitAsync != null)
                    await validateSourceWatermarkBeforeCommitAsync();
                if (expectedProductStatisticJobId.HasValue)
                {
                    await SalesStatisticsProductStoreDailyStateSlice.UpsertStatisticStateAsync(
                        context,
                        SalesStatisticType.StoreSales,
                        targetDate,
                        SalesStatisticRefreshStatus.Fresh,
                        sourceWatermark,
                        null,
                        overwriteLastSourceUploadTime: true);
                }

                // 提交前在同一事务内再围栏，防止 callback 与接管写入竞争；失败会回滚整次替换。
                await SalesStatisticsProductStoreDailyStateSlice.FenceProductStatisticExecutionOwnerAsync(
                    context,
                    targetDate,
                    expectedProductStatisticJobId);
                if (validateExecutionOwnershipBeforeCommitAsync != null)
                    await validateExecutionOwnershipBeforeCommitAsync();
            },
            commitAsync: () => context.Db.Ado.CommitTranAsync(),
            rollbackAsync: () => context.Db.Ado.RollbackTranAsync(),
            logger: logger,
            operationName: "并发分店统计数据更新"
        );
    }

    internal Task UpdateHourlyStatisticsWithContext(
            SqlSugarContext context,
            POSMSqlSugarContext posmContext,
            HBSalesRecordSqlSugarContext? hbSalesContext,
            ILogger logger,
            DateTime date,
            int? hour
        )
        {
            // 与业务切片共用同一实现：历史窗口内叠加 HBSales，并以删后重插保证消失的小时行被清掉。
            return SalesStatisticsHourlyRefresher.RefreshAsync(
                context,
                posmContext,
                hbSalesContext,
                logger,
                date,
                hour
            );
        }

    internal async Task UpdateStoreSupplierStatisticsWithContext(
            SqlSugarContext context,
            POSMSqlSugarContext posmContext,
            ILogger logger,
            DateTime? date,
            List<string>? branchCodes,
            List<string>? supplierCodes
        )
        {
            try
            {
                var targetDate = (date ?? SalesStatisticsBusinessDate.Today()).Date;
                var nextDate = targetDate.AddDays(1);
                var targetBranchCodes = SalesStatisticsCodeRules.NormalizeBranchCodes(branchCodes);
                var targetSupplierCodes = SalesStatisticsCodeRules.NormalizeSupplierCodes(
                    supplierCodes
                );

                logger.LogInformation(
                    "开始更新门店供应商统计数据: {Date}, 分店: {Branches}, 供应商: {Suppliers}",
                    targetDate,
                    branchCodes != null ? string.Join(", ", branchCodes) : "All",
                    supplierCodes != null ? string.Join(", ", supplierCodes) : "All"
                );

                // 构建查询
                var query = posmContext
                    .Db.Queryable<SalesOrder>()
                    .LeftJoin<SalesOrderDetail>((o, d) => o.OrderGuid == d.OrderGuid)
                    .LeftJoin<PosmProductSupplierMapping>(
                        (o, d, m) => d.ProductCode == m.ProductCode
                    )
                    // 金额、数量按净额并入退款单（Status=3，负数）；订单数只数 1/4。
                    .Where(o =>
                        o.Status != null
                        && SalesOrderStatusRules.NetSalesStatuses.Contains(o.Status)
                        && o.OrderTime != null
                        && o.OrderTime >= targetDate
                        && o.OrderTime < nextDate
                    );

                // 设置分店过滤条件
                if (targetBranchCodes.Any())
                {
                    query = query.Where(o =>
                        (o.BranchCode != null && targetBranchCodes.Contains(o.BranchCode.Trim()))
                        || o.BranchCode == null
                        || o.BranchCode.Trim() == ""
                    );
                }

                // 设置供应商过滤条件
                if (targetSupplierCodes.Any())
                {
                    var includesUnknownSupplier = targetSupplierCodes.Contains(UnknownSupplierCode);
                    query = query.Where(
                        (o, d, m) =>
                            (
                                m.LocalSupplierCode != null
                                && targetSupplierCodes.Contains(m.LocalSupplierCode.Trim())
                            )
                            || (
                                m.ChinaSupplierCode != null
                                && targetSupplierCodes.Contains(m.ChinaSupplierCode.Trim())
                            )
                            || (d.SupplierCode != null && targetSupplierCodes.Contains(d.SupplierCode.Trim()))
                            || (
                                includesUnknownSupplier
                                && (m.LocalSupplierCode == null || m.LocalSupplierCode.Trim() == "")
                                && (d.SupplierCode == null || d.SupplierCode.Trim() == "")
                            )
                    );
                }

                // 查询销售明细后按最终供应商编码聚合，确保订单数按订单去重。
                var rawStoreSupplierData = await query
                    .Select(
                        (o, d, m) =>
                            new
                            {
                                Date = o.OrderTime!.Value.Date,
                                BranchCode = o.BranchCode,
                                DeviceCode = o.DeviceCode,
                                OrderGuid = o.OrderGuid,
                                OrderStatus = o.Status,
                                DetailSupplierCode = d.SupplierCode,
                                LocalSupplierCode = m.LocalSupplierCode,
                                ChinaSupplierCode = m.ChinaSupplierCode,
                                ActualAmount = d.ActualAmount ?? 0m,
                                Quantity = d.Quantity ?? 0m,
                            }
                    )
                    .ToListAsync();
                var orderAmountMaps = await SalesStatisticsProductStoreDailySourceQueries
                    .LoadOrderAmountMapsAsync(
                    posmContext,
                    targetDate,
                    nextDate,
                    rawStoreSupplierData,
                    row => row.OrderGuid,
                    row => row.ActualAmount
                );
                var deviceBranchMap = await SalesStatisticsProductStoreDailySourceQueries
                    .LoadDeviceBranchMapAsync(
                    posmContext,
                    rawStoreSupplierData
                        .Where(row => string.IsNullOrWhiteSpace(row.BranchCode))
                        .Select(row => row.DeviceCode)
                );
                var storeSupplierData = rawStoreSupplierData
                    .Select(row => new StoreSupplierSourceRow
                    {
                        Date = row.Date,
                        BranchCode = SalesStatisticsCodeRules.ResolveBranchCode(
                            row.BranchCode,
                            row.DeviceCode,
                            deviceBranchMap
                        ),
                        DeviceCode = row.DeviceCode,
                        OrderGuid = row.OrderGuid,
                        OrderStatus = row.OrderStatus,
                        DetailSupplierCode = row.DetailSupplierCode,
                        LocalSupplierCode = row.LocalSupplierCode,
                        ChinaSupplierCode = row.ChinaSupplierCode,
                        ActualAmount = SalesStatisticsProductStoreDailyDomainRules
                            .ResolveStatisticAmount(
                            row.OrderGuid,
                            row.ActualAmount,
                            orderAmountMaps.PaymentAmounts,
                            orderAmountMaps.DetailAmounts
                        ),
                        Quantity = row.Quantity,
                    })
                    .Where(row =>
                        !targetBranchCodes.Any()
                        || targetBranchCodes.Contains(row.BranchCode ?? string.Empty)
                    )
                    .ToList();

                // 获取所有本地供应商代码
                var allLocalSupplierCodes = storeSupplierData
                    .Select(d =>
                        !string.IsNullOrWhiteSpace(d.LocalSupplierCode)
                            ? d.LocalSupplierCode!.Trim()
                            : d.DetailSupplierCode?.Trim()
                    )
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!)
                    .Distinct()
                    .ToList();

                // 查询本地供应商信息
                var localSupplierDict = new Dictionary<string, HBLocalSupplier>();
                if (allLocalSupplierCodes.Any())
                {
                    var localSuppliers = await context.HBLocalSupplierDb.GetListAsync(s =>
                        s.LocalSupplierCode != null
                        && allLocalSupplierCodes.Contains(s.LocalSupplierCode)
                        && !s.IsDeleted
                    );
                    localSupplierDict = localSuppliers.ToDictionary(
                        s => s.LocalSupplierCode!,
                        s => s
                    );
                }

                // 获取所有国内供应商代码
                var allChinaSupplierCodes = storeSupplierData
                    .Where(d => !string.IsNullOrEmpty(d.ChinaSupplierCode))
                    .Select(d => d.ChinaSupplierCode!)
                    .Distinct()
                    .ToList();

                // 查询国内供应商信息
                var chinaSupplierDict = new Dictionary<string, ChinaSupplier>();
                if (allChinaSupplierCodes.Any())
                {
                    var chinaSuppliers = await context.ChinaSupplierDb.GetListAsync(cs =>
                        cs.SupplierCode != null
                        && allChinaSupplierCodes.Contains(cs.SupplierCode)
                        && !cs.IsDeleted
                    );
                    chinaSupplierDict = chinaSuppliers
                        .Where(cs => !string.IsNullOrEmpty(cs.SupplierCode))
                        .ToDictionary(cs => cs.SupplierCode!, cs => cs);
                }

                LogSkippedBranchCodeRows(
                    logger,
                    "分店供应商销售统计",
                    storeSupplierData,
                    data => data.BranchCode,
                    data => data.ActualAmount,
                    data => data.Quantity
                );

                var statisticsList = SalesStatisticsStoreSupplierSlice
                    .BuildStoreSupplierSalesDetails(
                    storeSupplierData,
                    localSupplierDict,
                    chinaSupplierDict,
                    DateTime.Now
                );

                await SalesStatisticsTransactionExecutor.ExecuteAsync(
                    beginAsync: () => context.Db.Ado.BeginTranAsync(),
                    workAsync: async () =>
                    {
                        // 并发路径也按本次影响范围重建，避免旧供应商统计残留。
                        var deleteable = context.Db.Deleteable<StoreSupplierSalesDetail>()
                            .Where(s => s.Date == targetDate);
                        if (targetBranchCodes.Any())
                        {
                            deleteable = deleteable.Where(s => targetBranchCodes.Contains(s.BranchCode));
                        }
                        if (targetSupplierCodes.Any())
                        {
                            var deleteSupplierCodes = new List<string>();
                            if (targetSupplierCodes.Contains("200"))
                            {
                                var existingDomesticQuery = context.Db.Queryable<StoreSupplierSalesDetail>()
                                    .Where(s => s.Date == targetDate && s.IsDomestic == true);
                                if (targetBranchCodes.Any())
                                {
                                    existingDomesticQuery = existingDomesticQuery.Where(s =>
                                        targetBranchCodes.Contains(s.BranchCode)
                                    );
                                }

                                var existingDomesticSupplierCodes = await existingDomesticQuery
                                    .Select(s => s.SupplierCode)
                                    .Distinct()
                                    .ToListAsync();
                                deleteSupplierCodes.AddRange(existingDomesticSupplierCodes);
                            }

                            deleteSupplierCodes = deleteSupplierCodes
                                .Concat(targetSupplierCodes)
                                .Concat(statisticsList.Select(s => s.SupplierCode))
                                .Where(code => !string.IsNullOrWhiteSpace(code))
                                .Select(code => code.Trim())
                                .Distinct()
                                .ToList();
                            var includesUnknownSupplier = targetSupplierCodes.Contains(UnknownSupplierCode);
                            deleteable = deleteable.Where(s =>
                                deleteSupplierCodes.Contains(s.SupplierCode.Trim())
                                || (
                                    includesUnknownSupplier
                                    && (s.SupplierCode == null || s.SupplierCode.Trim() == "")
                                )
                            );
                        }

                        var deletedCount = await deleteable.ExecuteCommandAsync();
                        logger.LogInformation("删除 {Count} 条门店供应商统计旧记录", deletedCount);

                        if (!statisticsList.Any())
                        {
                            logger.LogInformation("没有找到门店供应商统计数据: {Date}", targetDate);
                            return;
                        }

                        context
                            .Db.Fastest<StoreSupplierSalesDetail>()
                            .PageSize(BatchSize)
                            .BulkCopy(statisticsList);
                        logger.LogInformation("批量插入 {Count} 条门店供应商统计记录", statisticsList.Count);
                    },
                    commitAsync: () => context.Db.Ado.CommitTranAsync(),
                    rollbackAsync: () => context.Db.Ado.RollbackTranAsync(),
                    logger: logger,
                    operationName: "门店供应商统计数据更新"
                );

                logger.LogInformation(
                    "门店供应商统计数据更新完成: {Date}, 总记录: {Total}",
                    targetDate,
                    statisticsList.Count
                );
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "更新门店供应商统计数据失败: {Date}", date);
                throw;
            }
        }

    private static void LogSkippedBranchCodeRows<T>(
        ILogger logger,
        string statisticName,
        IEnumerable<T> rows,
        Func<T, string?> branchCodeSelector,
        Func<T, decimal> amountSelector,
        Func<T, decimal> quantitySelector)
    {
        var skippedRows = rows
            .Where(row => string.IsNullOrWhiteSpace(branchCodeSelector(row)))
            .ToList();
        if (skippedRows.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "{StatisticName} 跳过 {Count} 条缺少分店编码的销售记录，金额合计 {Amount}，数量合计 {Quantity}",
            statisticName,
            skippedRows.Count,
            skippedRows.Sum(amountSelector),
            skippedRows.Sum(quantitySelector));
    }
}
