namespace BlazorApp.Api.Services;

/// <summary>
/// HBSales 历史来源的已核验窗口。窗口内的分店、商品与分时统计都必须叠加 HBSales 来源。
/// 2025 年 10 月起各分店逐台收银机切换到 POSM，并存期内同一分店同一天两来源都可能有真实交易，
/// 因此统计口径是两来源相加，而不是按日期二选一。
/// 生产只读核验（2026-09-17）：HBSales 最后一笔结账为 2026-04-12，之后再无单据，窗口终点据此定为 2026-05-01。
/// </summary>
internal static class SalesStatisticsHBSalesHistoryWindow
{
    internal static readonly DateTime StartDate = new(2024, 9, 14);
    internal static readonly DateTime EndExclusive = new(2026, 5, 1);

    internal static bool Includes(DateTime date)
    {
        var day = date.Date;
        return day >= StartDate && day < EndExclusive;
    }

    /// <summary>
    /// HBSales 断开后，窗口内的日统计是唯一留存的完整数据，不能再重算，
    /// 否则会被只含 POSM 的结果覆盖。
    /// </summary>
    internal static bool IsFrozen(DateTime date) =>
        !BlazorApp.Api.Data.LegacyDatabaseGate.HBSalesEnabled && Includes(date);
}
