using System.Globalization;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Services;

/// <summary>收银端订单查询时间窗口的配置；任一项 ≤ 0 即关闭该项限制（运维总闸）。</summary>
public sealed class OrderQueryWindowOptions
{
    public const string SectionName = "OrderQueryWindow";

    /// <summary>最早可查到门店今天往前多少个月的同一天。</summary>
    public int EarliestMonths { get; set; } = OrderQueryWindowRules.DefaultEarliestMonths;

    /// <summary>按日期区间浏览 / 按商品搜时，起止跨度最多多少个日历月（含首尾）。</summary>
    public int MaxSpanMonths { get; set; } = OrderQueryWindowRules.DefaultMaxSpanMonths;
}

/// <summary>校验结果：要么是补齐 / 规范化后的查询请求，要么是错误码与中英双语提示（收银端会把提示原样显示给收银员）。</summary>
public sealed record OrderQueryWindowDecision(
    OrderHistoryQueryRequest? Request,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool IsRejected => ErrorCode is not null;
}

public interface IOrderQueryWindowService
{
    Task<OrderQueryWindowDecision> ResolveHistoryWindowAsync(
        OrderHistoryQueryRequest request,
        CancellationToken cancellationToken);

    /// <summary>原单是否还在可退货的最早日期之内；超出时返回给收银员看的提示，否则返回 null。</summary>
    Task<string?> CheckReturnWindowAsync(
        string storeCode,
        DateTimeOffset soldAt,
        CancellationToken cancellationToken);
}

/// <summary>
/// 订单查询时间窗口的权威校验（规则见 <see cref="OrderQueryWindowRules"/>）：
/// 明确传了超范围的日期就拒绝；没传日期按规则补默认值而不拒绝，旧版收银端和退货页（按单号 / 按商品查都不带日期）照常可用。
/// 「今天」按门店时区算：POSM 的 OrderTime 是门店本地墙钟时间。
/// </summary>
public sealed class OrderQueryWindowService(
    IStoreTimeZoneResolver storeTimeZoneResolver,
    IOptions<OrderQueryWindowOptions> options,
    TimeProvider? timeProvider = null) : IOrderQueryWindowService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<OrderQueryWindowDecision> ResolveHistoryWindowAsync(
        OrderHistoryQueryRequest request,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var storeTimeZone = await storeTimeZoneResolver.ResolveAsync(request.StoreCode.Trim(), cancellationToken);
        var today = StoreWallClock.ToWallClock(_timeProvider.GetUtcNow(), storeTimeZone).Date;
        DateTime? fromDate = request.SoldFrom is { } soldFrom ? StoreWallClock.ToWallClock(soldFrom, storeTimeZone).Date : null;
        DateTime? toDate = request.SoldTo is { } soldTo ? StoreWallClock.ToWallClock(soldTo, storeTimeZone).Date : null;

        if (fromDate is not null && toDate is not null && fromDate > toDate)
        {
            return Reject(
                OrderQueryWindowRules.ErrorRangeInvalid,
                "开始日期不能晚于结束日期。 / The start date cannot be after the end date.");
        }

        DateTime? earliest = settings.EarliestMonths > 0
            ? OrderQueryWindowRules.EarliestDate(today, settings.EarliestMonths)
            : null;
        // 只给了结束日期时按结束日期判断：结束日期都早于最早日期，整个区间都查不到。
        var firstRequestedDate = fromDate ?? toDate;
        if (earliest is not null && firstRequestedDate is not null && firstRequestedDate < earliest)
        {
            return Reject(
                OrderQueryWindowRules.ErrorTooOld,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "最早只能查询 {0:yyyy-MM-dd} 及以后的订单。 / Orders can only be searched from {0:yyyy-MM-dd} onwards.",
                    earliest.Value));
        }

        var isLocator = OrderQueryWindowRules.IsOrderLocator(request.Keyword);
        var spanMonths = settings.MaxSpanMonths;
        if (!isLocator && spanMonths > 0)
        {
            if (fromDate is not null && toDate is not null)
            {
                if (!OrderQueryWindowRules.IsWithinSpan(fromDate.Value, toDate.Value, spanMonths))
                {
                    return Reject(
                        OrderQueryWindowRules.ErrorRangeTooLong,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "查询区间不能超过 {0} 个月，请缩小日期范围。 / The date range cannot exceed {0} month(s). Please narrow the range.",
                            spanMonths));
                }

                return Accept(request);
            }

            // 没带完整区间（交易历史页清空了日期、退货页按商品条码找原单）：补成跨度上限内、离今天最近的区间。
            var to = toDate ?? (fromDate is { } from ? Min(OrderQueryWindowRules.DefaultToDate(from, spanMonths), today) : today);
            var defaultFrom = fromDate ?? OrderQueryWindowRules.DefaultFromDate(to, spanMonths);
            if (earliest is not null && defaultFrom < earliest)
            {
                defaultFrom = earliest.Value;
            }

            return Accept(request with
            {
                SoldFrom = request.SoldFrom ?? StartOfDay(defaultFrom, storeTimeZone),
                SoldTo = request.SoldTo ?? EndOfDay(to, storeTimeZone)
            });
        }

        // 按单号定位（或跨度限制已关闭）：只补最早日期这一条下限。
        return earliest is not null && request.SoldFrom is null
            ? Accept(request with { SoldFrom = StartOfDay(earliest.Value, storeTimeZone) })
            : Accept(request);
    }

    public async Task<string?> CheckReturnWindowAsync(
        string storeCode,
        DateTimeOffset soldAt,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.EarliestMonths <= 0)
        {
            return null;
        }

        var storeTimeZone = await storeTimeZoneResolver.ResolveAsync(storeCode.Trim(), cancellationToken);
        var today = StoreWallClock.ToWallClock(_timeProvider.GetUtcNow(), storeTimeZone).Date;
        var earliest = OrderQueryWindowRules.EarliestDate(today, settings.EarliestMonths);
        var soldDate = StoreWallClock.ToWallClock(soldAt, storeTimeZone).Date;
        return soldDate >= earliest
            ? null
            : string.Format(
                CultureInfo.InvariantCulture,
                "原单日期 {0:yyyy-MM-dd} 早于 {1:yyyy-MM-dd}，超过可退货期限，不能按原单退货。 / The original order ({0:yyyy-MM-dd}) is older than {1:yyyy-MM-dd} and can no longer be returned against.",
                soldDate,
                earliest);
    }

    private static OrderQueryWindowDecision Accept(OrderHistoryQueryRequest request) => new(request);

    private static OrderQueryWindowDecision Reject(string code, string message) => new(null, code, message);

    private static DateTime Min(DateTime left, DateTime right) => left <= right ? left : right;

    private static DateTimeOffset StartOfDay(DateTime date, TimeZoneInfo storeTimeZone) =>
        StoreWallClock.ToDateTimeOffset(date.Date, storeTimeZone);

    private static DateTimeOffset EndOfDay(DateTime date, TimeZoneInfo storeTimeZone) =>
        StoreWallClock.ToDateTimeOffset(date.Date.AddDays(1).AddTicks(-1), storeTimeZone);
}
