using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBSalesRecord;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;

namespace BlazorApp.Api.Services;

/// <summary>
/// 分时统计的唯一刷新实现。业务切片与完整刷新编排器都经由此处写 HourlySalesStatistic，
/// 避免两份 POSM-only 副本再次分叉：金额取支付明细、销量取销售明细、订单数取订单头；
/// HBSales 历史窗口内叠加 HBSales 小时来源，与分店日统计的双来源相加口径一致。
/// </summary>
internal static class SalesStatisticsHourlyRefresher
{
    private const int BatchSize = 5000;

    internal static async Task RefreshAsync(
        SqlSugarContext context,
        POSMSqlSugarContext posmContext,
        HBSalesRecordSqlSugarContext? hbSalesContext,
        ILogger logger,
        DateTime date,
        int? hour
    )
    {
        try
        {
            // 确定要更新的小时列表
            var targetHours = hour.HasValue
                ? new[] { hour.Value }
                : Enumerable.Range(0, 24).ToArray();
            var rangeStart = hour.HasValue ? date.Date.AddHours(hour.Value) : date.Date;
            var rangeEnd = hour.HasValue ? rangeStart.AddHours(1) : date.Date.AddDays(1);

            logger.LogInformation(
                "开始更新分时统计数据: {Date}, 小时: {Hours}",
                date,
                hour.HasValue ? hour.Value.ToString() : "0-23"
            );

            // 金额取支付明细、销量取销售明细、订单数取订单头，避免拆分支付放大非金额指标。
            // 金额与销量并入退款单（Status=3，负数）得净额，订单数/客数仍只数 1/4（退款单不算一单）。
            var hourlyRevenueRows = await posmContext
                .Db.Queryable<PaymentDetail, SalesOrder>(
                    (pd, so) => pd.OrderGuid == so.OrderGuid
                )
                .Where(
                    (pd, so) =>
                        so.Status != null
                        && SalesOrderStatusRules.NetSalesStatuses.Contains(so.Status)
                        && so.OrderTime != null
                        && so.OrderTime >= rangeStart
                        && so.OrderTime < rangeEnd
                )
                .GroupBy(
                    (pd, so) =>
                        new
                        {
                            Date = so.OrderTime!.Value.Date,
                            Hour = so.OrderTime!.Value.Hour,
                            so.BranchCode,
                            so.DeviceCode,
                        }
                )
                .Select(
                    (pd, so) =>
                        new HourlyStatisticSourceRow
                        {
                            Date = so.OrderTime!.Value.Date,
                            Hour = so.OrderTime!.Value.Hour,
                            BranchCode = so.BranchCode,
                            DeviceCode = so.DeviceCode,
                            TotalAmount = SqlFunc.AggregateSum(pd.Amount) ?? 0m,
                        }
                )
                .ToListAsync();

            var hourlyQuantityRows = await posmContext
                .Db.Queryable<SalesOrderDetail, SalesOrder>(
                    (detail, so) => detail.OrderGuid == so.OrderGuid
                )
                .Where(
                    (detail, so) =>
                        so.Status != null
                        && SalesOrderStatusRules.NetSalesStatuses.Contains(so.Status)
                        && so.OrderTime != null
                        && so.OrderTime >= rangeStart
                        && so.OrderTime < rangeEnd
                )
                .GroupBy(
                    (detail, so) =>
                        new
                        {
                            Date = so.OrderTime!.Value.Date,
                            Hour = so.OrderTime!.Value.Hour,
                            so.BranchCode,
                            so.DeviceCode,
                        }
                )
                .Select(
                    (detail, so) =>
                        new HourlyStatisticSourceRow
                        {
                            Date = so.OrderTime!.Value.Date,
                            Hour = so.OrderTime!.Value.Hour,
                            BranchCode = so.BranchCode,
                            DeviceCode = so.DeviceCode,
                            TotalQuantity = SqlFunc.AggregateSum(detail.Quantity) ?? 0,
                        }
                )
                .ToListAsync();

            var hourlyOrderRows = await posmContext
                .Db.Queryable<SalesOrder>()
                .Where(
                    so =>
                        so.Status != null
                        && (so.Status == 1 || so.Status == 4)
                        && so.OrderTime != null
                        && so.OrderTime >= rangeStart
                        && so.OrderTime < rangeEnd
                )
                .GroupBy(
                    so =>
                        new
                        {
                            Date = so.OrderTime!.Value.Date,
                            Hour = so.OrderTime!.Value.Hour,
                            so.BranchCode,
                            so.DeviceCode,
                        }
                )
                .Select(
                    so =>
                        new HourlyStatisticSourceRow
                        {
                            Date = so.OrderTime!.Value.Date,
                            Hour = so.OrderTime!.Value.Hour,
                            BranchCode = so.BranchCode,
                            DeviceCode = so.DeviceCode,
                            OrderCount = SqlFunc.AggregateCount(so.OrderGuid),
                            CustomerCount = SqlFunc.AggregateCount(so.OrderGuid),
                        }
                )
                .ToListAsync();

            var combinedHourlyRows = hourlyRevenueRows
                .Concat(hourlyQuantityRows)
                .Concat(hourlyOrderRows)
                .ToList();

            if (SalesStatisticsHBSalesHistoryWindow.Includes(date))
            {
                // 并存期内同一分店同一天两来源都可能有真实交易，这里与分店日统计一样直接相加。
                // 若窗口内拿不到 HBSales 上下文，宁可失败也不能写出只含 POSM 的“完整”统计。
                var hbSalesRows = await SalesStatisticsProductStoreDailySourceQueries
                    .LoadHBSalesHourlyAggregatesAsync(
                        hbSalesContext
                            ?? throw new InvalidOperationException(
                                "HBSales 历史窗口内的分时统计缺少 HBSalesRecord 上下文"
                            ),
                        date.Date,
                        date.Date.AddDays(1)
                    );
                combinedHourlyRows.AddRange(
                    hbSalesRows
                        .Where(row => targetHours.Contains(row.Hour))
                        .Select(row => new HourlyStatisticSourceRow
                        {
                            Date = date.Date,
                            Hour = row.Hour,
                            BranchCode = row.BranchCode,
                            TotalAmount = row.TotalAmount,
                            TotalQuantity = (int)row.TotalQuantity,
                            OrderCount = row.OrderCount,
                            CustomerCount = row.OrderCount,
                        })
                );
            }

            var deviceBranchMap = await SalesStatisticsProductStoreDailySourceQueries.LoadDeviceBranchMapAsync(
                posmContext,
                combinedHourlyRows.Where(row => string.IsNullOrWhiteSpace(row.BranchCode))
                    .Select(row => row.DeviceCode));
            var allHourlyData = combinedHourlyRows
                .Select(row => new
                {
                    Row = row,
                    BranchCode = SalesStatisticsCodeRules.ResolveBranchCode(
                        row.BranchCode,
                        row.DeviceCode,
                        deviceBranchMap),
                })
                .GroupBy(row => new { row.Row.Date, row.Row.Hour, row.BranchCode })
                .Select(group => new HourlyStatisticSourceRow
                {
                    Date = group.Key.Date.Date,
                    Hour = group.Key.Hour,
                    BranchCode = group.Key.BranchCode,
                    TotalAmount = group.Sum(row => row.Row.TotalAmount),
                    TotalQuantity = group.Sum(row => row.Row.TotalQuantity),
                    OrderCount = group.Sum(row => row.Row.OrderCount),
                    CustomerCount = group.Sum(row => row.Row.CustomerCount),
                })
                .ToList();

            if (!allHourlyData.Any())
            {
                logger.LogInformation("没有找到销售数据: {Date}", date);
            }

            // 获取所有分店代码
            var branchCodes = allHourlyData
                .Select(d => d.BranchCode)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .ToList();

            // 查询分店信息
            var stores = await context
                .Db.Queryable<Store>()
                .Where(s => branchCodes.Contains(s.StoreCode))
                .ToListAsync();

            var storeDict = stores.ToDictionary(s => s.StoreCode, s => s);

            var statisticsList = new List<HourlySalesStatistic>();

            // 为每个小时创建全店汇总记录
            foreach (var h in targetHours)
            {
                var hourlyDataForHour = allHourlyData.Where(d => d.Hour == h).ToList();

                if (hourlyDataForHour.Any())
                {
                    var allStoreData = new HourlySalesStatistic
                    {
                        Date = date,
                        Hour = h,
                        BranchCode = "ALL",
                        BranchName = "All Stores",
                        TotalAmount = hourlyDataForHour.Sum(d => d.TotalAmount),
                        TotalQuantity = (int)hourlyDataForHour.Sum(d => d.TotalQuantity),
                        OrderCount = hourlyDataForHour.Sum(d => d.OrderCount),
                        CustomerCount = hourlyDataForHour.Sum(d => d.CustomerCount),
                        AverageOrderValue =
                            hourlyDataForHour.Sum(d => d.OrderCount) > 0
                                ? hourlyDataForHour.Sum(d => d.TotalAmount)
                                    / hourlyDataForHour.Sum(d => d.OrderCount)
                                : 0m,
                        UpdateTime = DateTime.Now,
                    };
                    statisticsList.Add(allStoreData);
                }
            }

            LogSkippedBranchCodeRows(
                logger,
                "分时分店销售统计",
                allHourlyData,
                data => data.BranchCode,
                data => data.TotalAmount,
                data => data.TotalQuantity
            );

            // 为每个分店创建分时统计记录
            foreach (var data in allHourlyData)
            {
                // 分店维度统计必须有有效分店编码，避免把空编码写入统计表。
                if (string.IsNullOrWhiteSpace(data.BranchCode))
                    continue;
                var branchCode = data.BranchCode;
                var store = storeDict.GetValueOrDefault(branchCode);

                var storeStatistic = new HourlySalesStatistic
                {
                    Date = data.Date,
                    Hour = data.Hour,
                    BranchCode = branchCode,
                    BranchName = store?.StoreName ?? branchCode,
                    TotalAmount = data.TotalAmount,
                    TotalQuantity = (int)data.TotalQuantity,
                    OrderCount = data.OrderCount,
                    CustomerCount = data.CustomerCount,
                    AverageOrderValue =
                        data.OrderCount > 0 ? data.TotalAmount / data.OrderCount : 0m,
                    UpdateTime = DateTime.Now,
                };
                statisticsList.Add(storeStatistic);
            }

            await SalesStatisticsDailyHourlySlice.ExecuteTransactionSafelyAsync(
                beginAsync: () => context.Db.Ado.BeginTranAsync(),
                workAsync: async () =>
                {
                    // 删除指定日期和小时的旧记录；来源确实无销售时，旧行也必须一起清掉。
                    var deletedCount = await context
                        .Db.Deleteable<HourlySalesStatistic>()
                        .Where(s => s.Date >= date.Date && s.Date < date.Date.AddDays(1) && targetHours.Contains(s.Hour))
                        .ExecuteCommandAsync();
                    logger.LogInformation("删除 {Count} 条分时统计旧记录", deletedCount);

                    // 批量插入新记录
                    if (statisticsList.Any())
                        context.Db.Fastest<HourlySalesStatistic>()
                            .PageSize(BatchSize)
                            .BulkCopy(statisticsList);
                },
                commitAsync: () => context.Db.Ado.CommitTranAsync(),
                rollbackAsync: () => context.Db.Ado.RollbackTranAsync(),
                logger: logger,
                operationName: "分时统计数据更新"
            );

            logger.LogInformation(
                "分时统计数据更新完成: {Date}, 小时: {Hours}, 总记录: {Total}",
                date,
                hour.HasValue ? hour.Value.ToString() : "0-23",
                statisticsList.Count
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新分时统计数据失败: {Date} {Hour}", date, hour);
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
            return;

        // 各统计切片采用同一缺失分店编码告警口径，避免静默丢弃来源行。
        logger.LogWarning(
            "{StatisticName} 跳过 {Count} 条缺少分店编码的销售记录，金额合计 {Amount}，数量合计 {Quantity}",
            statisticName,
            skippedRows.Count,
            skippedRows.Sum(amountSelector),
            skippedRows.Sum(quantitySelector));
    }
}
