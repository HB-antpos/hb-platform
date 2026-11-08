using BlazorApp.Api.Features.StoreOrders.ProductHistory.Domain;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.ProductHistory.Infrastructure;

/// <summary>
/// 去年同期查询的结果与诊断计数：Result 为 null 表示窗口不可用（没有商品，或去年此时本店没有日销数据）。
/// </summary>
internal sealed record LastYearSalesQueryOutcome(
    ProductHistoryLastYearSalesResult? Result,
    int StatsQueryCount,
    bool StoreReady
);

/// <summary>
/// 本店去年同期（往前推 364 天、同星期几起算）未来 30 / 60 天的商品销量。
/// 订货页商品卡与浏览器订货助手共用这一份口径，避免两处各写一遍后慢慢跑偏。
/// 调用方负责先确认分店是 POS 启用分店（启用且未删除）；这里只读日销统计。
/// </summary>
internal static class LastYearSameWeekdaySalesQuery
{
    internal static async Task<LastYearSalesQueryOutcome> QueryAsync(
        ISqlSugarClient db,
        string storeCode,
        IReadOnlyList<string> productCodes,
        DateTime storeToday
    )
    {
        var normalizedProductCodes = productCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedProductCodes.Count == 0)
        {
            return new LastYearSalesQueryOutcome(null, 0, false);
        }

        var windowStart = ProductHistoryRules.GetLastYearWindowStartDate(storeToday);
        var probeEnd = windowStart.AddDays(ProductHistoryRules.LastYearStoreReadyProbeDays);
        var shortEnd = windowStart.AddDays(ProductHistoryRules.LastYearShortWindowDays);
        var longEnd = windowStart.AddDays(ProductHistoryRules.LastYearLongWindowDays);

        // 日统计 Date 恒为零点，统一用半开区间 [起点, 终点)，避免 SQLite/SQL Server 对「<= 当天」的文本/时间比较差异。
        var statsQueryCount = 1;
        var probeRow = await db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(item =>
                item.BranchCode == storeCode
                && item.Date >= windowStart
                && item.Date < probeEnd
            )
            .Select(item => new ProductHistoryStatisticDateRow { Date = item.Date })
            .FirstAsync();
        if (probeRow == null)
        {
            // 窗口开头本店没有任何日销统计：去年此时没营业或数据未覆盖，整段不可用，调用方据此隐藏而不是显示 0。
            return new LastYearSalesQueryOutcome(null, statsQueryCount, false);
        }

        var result = new ProductHistoryLastYearSalesResult { WindowStartDate = windowStart };
        foreach (var codes in normalizedProductCodes.Chunk(
                     ProductHistoryRules.SalesStatisticsMaxProductCodesPerCutoffGroup
                 ))
        {
            var chunkCodes = codes.ToList();
            statsQueryCount++;
            // 一次读出 60 天的行，前 30 天用条件求和，同一批行算两个数，切换 30/60 不用再查。
            var rows = await db.Queryable<ProductStoreDailySalesStatistic>()
                .Where(item =>
                    item.BranchCode == storeCode
                    && chunkCodes.Contains(item.ProductCode)
                    && item.Date >= windowStart
                    && item.Date < longEnd
                )
                .GroupBy(item => item.ProductCode)
                .Select(item => new ProductHistoryLastYearSalesRow
                {
                    ProductCode = item.ProductCode,
                    Quantity30Days = SqlFunc.AggregateSum(
                        SqlFunc.IIF(item.Date < shortEnd, item.TotalQuantity, 0)
                    ),
                    Quantity60Days = SqlFunc.AggregateSum(item.TotalQuantity),
                })
                .ToListAsync();
            foreach (var row in rows.Where(row => !string.IsNullOrWhiteSpace(row.ProductCode)))
            {
                if (result.SalesMap.TryGetValue(row.ProductCode, out var existing))
                {
                    existing.Quantity30Days += row.Quantity30Days;
                    existing.Quantity60Days += row.Quantity60Days;
                }
                else
                {
                    result.SalesMap[row.ProductCode] = row;
                }
            }
        }

        return new LastYearSalesQueryOutcome(result, statsQueryCount, true);
    }
}
