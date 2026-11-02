namespace Hbpos.Contracts.Orders;

/// <summary>
/// 收银端订单查询的时间窗口规则：最早只能查到「门店今天往前推 N 个月的同一天」，
/// 按日期区间浏览 / 按商品搜时起止跨度不超过 M 个日历月（含首尾两天）；按单号定位（完整订单号、小票 8 位单号）不受跨度限制。
/// POS API 用它做权威校验，收银端日期选择器复用同一套计算，前后端口径一致。所有日期都是门店本地日期。
/// </summary>
public static class OrderQueryWindowRules
{
    public const int DefaultEarliestMonths = 12;
    public const int DefaultMaxSpanMonths = 1;

    public const string ErrorRangeInvalid = "ORDER_QUERY_RANGE_INVALID";
    public const string ErrorTooOld = "ORDER_QUERY_TOO_OLD";
    public const string ErrorRangeTooLong = "ORDER_QUERY_RANGE_TOO_LONG";
    public const string ErrorTooOldForReturn = "ORDER_TOO_OLD_FOR_RETURN";

    // 小票上的 8 位单号 = 订单 GUID 末 8 位（十六进制）。
    private const int OrderShortCodeLength = 8;

    /// <summary>最早可查日期（含当天）。2 月 29 日往前推一年落到 2 月 28 日（DateTime.AddMonths 自带月末对齐）。</summary>
    public static DateTime EarliestDate(DateTime storeToday, int earliestMonths) =>
        storeToday.Date.AddMonths(-earliestMonths);

    /// <summary>
    /// 起止日期（含首尾）是否在 maxSpanMonths 个日历月内，例如 10-03～11-02 合法、10-02～11-02 超出。
    /// 往后推和往前推各算一次、任一成立即可：月末对齐时（1-31 往后一个月会落到 2-28）只算一个方向会把 29~31 天的区间误判为超长；
    /// 这样算出的上限始终是 28~31 天。
    /// </summary>
    public static bool IsWithinSpan(DateTime fromDate, DateTime toDate, int maxSpanMonths)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        if (to < from)
        {
            return false;
        }

        return to <= from.AddMonths(maxSpanMonths).AddDays(-1)
            || from >= to.AddMonths(-maxSpanMonths).AddDays(1);
    }

    /// <summary>只给了结束日期时，跨度内最早的开始日期。</summary>
    public static DateTime DefaultFromDate(DateTime toDate, int maxSpanMonths) =>
        toDate.Date.AddMonths(-maxSpanMonths).AddDays(1);

    /// <summary>只给了开始日期时，跨度内最晚的结束日期。</summary>
    public static DateTime DefaultToDate(DateTime fromDate, int maxSpanMonths) =>
        fromDate.Date.AddMonths(maxSpanMonths).AddDays(-1);

    /// <summary>按单号定位：完整订单号（GUID），或小票 8 位单号（恰好 8 位 ASCII 十六进制）。只受最早日期限制，不受跨度限制。</summary>
    public static bool IsOrderLocator(string? keyword)
    {
        var trimmed = keyword?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (Guid.TryParse(trimmed, out _))
        {
            return true;
        }

        if (trimmed.Length != OrderShortCodeLength)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            // 只认 ASCII 十六进制；char.IsDigit 会放过全角数字等。
            if (ch is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }
}
