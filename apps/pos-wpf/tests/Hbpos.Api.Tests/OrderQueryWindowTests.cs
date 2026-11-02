using Hbpos.Api.Services;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class OrderQueryWindowTests
{
    [Theory]
    [InlineData("2026-10-03", "2026-11-02", true)]   // 方案里的合法例子
    [InlineData("2026-10-02", "2026-11-02", false)]  // 方案里的超出例子
    [InlineData("2026-11-02", "2026-11-02", true)]   // 当天
    [InlineData("2026-01-01", "2026-01-31", true)]   // 整月 31 天
    [InlineData("2026-01-31", "2026-02-28", true)]   // 月末对齐：1-31 往后一个月落到 2-28，只算单向会误判
    [InlineData("2026-08-31", "2026-09-30", true)]
    [InlineData("2026-01-30", "2026-03-01", false)]  // 跨了两个月边界
    [InlineData("2027-02-28", "2027-03-28", false)]  // 2-28～3-27 才是一个日历月
    [InlineData("2027-02-28", "2027-03-27", true)]
    [InlineData("2026-11-02", "2026-11-01", false)]  // 结束早于开始
    public void Span_is_one_calendar_month_including_both_ends(string from, string to, bool expected)
    {
        Assert.Equal(expected, OrderQueryWindowRules.IsWithinSpan(DateTime.Parse(from), DateTime.Parse(to), 1));
    }

    [Fact]
    public void Span_never_exceeds_31_days_across_a_whole_leap_year()
    {
        // 逐日扫一遍闰年：任何被判为合法的区间都不超过 31 天，且「开始日往后 28 天」总是合法。
        for (var from = new DateTime(2028, 1, 1); from.Year == 2028; from = from.AddDays(1))
        {
            Assert.True(OrderQueryWindowRules.IsWithinSpan(from, from.AddDays(27), 1));
            Assert.False(OrderQueryWindowRules.IsWithinSpan(from, from.AddDays(31), 1));
        }
    }

    [Theory]
    [InlineData("2026-11-02", "2025-11-02")]
    [InlineData("2028-02-29", "2027-02-28")]  // 闰日往前一年落到 2-28
    [InlineData("2027-03-01", "2026-03-01")]
    public void Earliest_date_is_the_same_day_one_year_earlier(string today, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), OrderQueryWindowRules.EarliestDate(DateTime.Parse(today), 12));
    }

    [Theory]
    [InlineData("DB596F11", true)]
    [InlineData("db596f11", true)]
    [InlineData(" 6a3c4dfd-ab7b-4d95-84db-024edb596f11 ", true)]
    [InlineData("6a3c4dfdab7b4d9584db024edb596f11", true)]
    [InlineData("12345678", true)]        // 8 位数字与小票单号同形，按单号定位处理（只受 1 年下限）
    [InlineData("9311192735139", false)]  // 13 位商品条码
    [InlineData("DB596F1", false)]
    [InlineData("ITEM-TARGET", false)]
    [InlineData("１２３４５６７８", false)]  // 全角数字
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Order_locator_is_full_guid_or_eight_hex_short_code(string? keyword, bool expected)
    {
        Assert.Equal(expected, OrderQueryWindowRules.IsOrderLocator(keyword));
    }

    [Fact]
    public async Task Today_is_taken_in_the_store_time_zone_not_utc()
    {
        // 2026-11-01 13:30Z：UTC 还是 11-01，悉尼（+11）已是 11-02 00:30，布里斯班（+10）是 11-01 23:30。
        var now = DateTimeOffset.Parse("2026-11-01T13:30:00Z");
        var sydney = CreateService(TestStoreTimeZones.Sydney, now);
        var brisbane = CreateService(TestStoreTimeZones.Brisbane, now);

        var sydneyWindow = await sydney.ResolveHistoryWindowAsync(new OrderHistoryQueryRequest("S01"), CancellationToken.None);
        var brisbaneWindow = await brisbane.ResolveHistoryWindowAsync(new OrderHistoryQueryRequest("S01"), CancellationToken.None);

        // 悉尼 2026 年夏令时 10-04 才开始，10-03 当天仍是 +10:00。
        Assert.Equal(DateTimeOffset.Parse("2026-10-03T00:00:00+10:00"), sydneyWindow.Request!.SoldFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T00:00:00+10:00"), brisbaneWindow.Request!.SoldFrom);
    }

    [Fact]
    public async Task Only_end_date_given_fills_the_earliest_start_within_one_month()
    {
        var service = CreateService(TestStoreTimeZones.Sydney, DateTimeOffset.Parse("2026-11-02T01:00:00Z"));

        var window = await service.ResolveHistoryWindowAsync(
            new OrderHistoryQueryRequest("S01", SoldTo: DateTimeOffset.Parse("2026-08-31T23:59:59+10:00")),
            CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2026-08-01T00:00:00+10:00"), window.Request!.SoldFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-08-31T23:59:59+10:00"), window.Request.SoldTo);
    }

    [Fact]
    public async Task Only_start_date_given_fills_end_within_one_month_but_not_past_today()
    {
        var service = CreateService(TestStoreTimeZones.Sydney, DateTimeOffset.Parse("2026-11-02T01:00:00Z"));

        var past = await service.ResolveHistoryWindowAsync(
            new OrderHistoryQueryRequest("S01", SoldFrom: DateTimeOffset.Parse("2026-08-01T00:00:00+10:00")),
            CancellationToken.None);
        var recent = await service.ResolveHistoryWindowAsync(
            new OrderHistoryQueryRequest("S01", SoldFrom: DateTimeOffset.Parse("2026-10-20T00:00:00+11:00")),
            CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2026-09-01T00:00:00+10:00").AddTicks(-1), past.Request!.SoldTo);
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T00:00:00+11:00").AddTicks(-1), recent.Request!.SoldTo);
    }

    [Fact]
    public async Task Default_start_is_never_before_the_earliest_date()
    {
        // 配置成最早 1 个月、跨度 3 个月：默认区间的开始日要被最早日期截住。
        var service = CreateService(
            TestStoreTimeZones.Sydney,
            DateTimeOffset.Parse("2026-11-02T01:00:00Z"),
            new OrderQueryWindowOptions { EarliestMonths = 1, MaxSpanMonths = 3 });

        var window = await service.ResolveHistoryWindowAsync(new OrderHistoryQueryRequest("S01"), CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2026-10-02T00:00:00+10:00"), window.Request!.SoldFrom);
    }

    [Fact]
    public async Task End_date_before_the_earliest_date_is_rejected_as_too_old()
    {
        var service = CreateService(TestStoreTimeZones.Sydney, DateTimeOffset.Parse("2026-11-02T01:00:00Z"));

        var window = await service.ResolveHistoryWindowAsync(
            new OrderHistoryQueryRequest("S01", SoldTo: DateTimeOffset.Parse("2025-10-31T23:59:59+11:00")),
            CancellationToken.None);

        Assert.Equal(OrderQueryWindowRules.ErrorTooOld, window.ErrorCode);
    }

    [Fact]
    public async Task Order_locator_with_explicit_long_range_is_allowed_but_still_bounded_by_one_year()
    {
        var service = CreateService(TestStoreTimeZones.Sydney, DateTimeOffset.Parse("2026-11-02T01:00:00Z"));

        var longRange = await service.ResolveHistoryWindowAsync(
            new OrderHistoryQueryRequest(
                "S01",
                SoldFrom: DateTimeOffset.Parse("2026-01-01T00:00:00+11:00"),
                SoldTo: DateTimeOffset.Parse("2026-11-02T23:59:59+11:00"),
                Keyword: "DB596F11"),
            CancellationToken.None);
        var tooOld = await service.ResolveHistoryWindowAsync(
            new OrderHistoryQueryRequest("S01", SoldFrom: DateTimeOffset.Parse("2025-06-01T00:00:00+10:00"), Keyword: "DB596F11"),
            CancellationToken.None);

        Assert.False(longRange.IsRejected);
        Assert.Equal(OrderQueryWindowRules.ErrorTooOld, tooOld.ErrorCode);
    }

    [Fact]
    public async Task Start_after_end_is_rejected()
    {
        var service = CreateService(TestStoreTimeZones.Sydney, DateTimeOffset.Parse("2026-11-02T01:00:00Z"));

        var window = await service.ResolveHistoryWindowAsync(
            new OrderHistoryQueryRequest(
                "S01",
                SoldFrom: DateTimeOffset.Parse("2026-11-02T00:00:00+11:00"),
                SoldTo: DateTimeOffset.Parse("2026-11-01T23:59:59+11:00")),
            CancellationToken.None);

        Assert.Equal(OrderQueryWindowRules.ErrorRangeInvalid, window.ErrorCode);
    }

    [Fact]
    public async Task Zero_settings_turn_both_limits_off()
    {
        var service = CreateService(
            TestStoreTimeZones.Sydney,
            DateTimeOffset.Parse("2026-11-02T01:00:00Z"),
            new OrderQueryWindowOptions { EarliestMonths = 0, MaxSpanMonths = 0 });
        var request = new OrderHistoryQueryRequest(
            "S01",
            SoldFrom: DateTimeOffset.Parse("2024-01-01T00:00:00+11:00"),
            SoldTo: DateTimeOffset.Parse("2026-11-02T23:59:59+11:00"));

        var window = await service.ResolveHistoryWindowAsync(request, CancellationToken.None);
        var returnCheck = await service.CheckReturnWindowAsync("S01", DateTimeOffset.Parse("2020-01-01T10:00:00+11:00"), CancellationToken.None);
        var noDates = await service.ResolveHistoryWindowAsync(new OrderHistoryQueryRequest("S01"), CancellationToken.None);

        Assert.Same(request, window.Request);
        Assert.Null(returnCheck);
        Assert.Null(noDates.Request!.SoldFrom);
        Assert.Null(noDates.Request.SoldTo);
    }

    [Fact]
    public async Task Return_window_uses_the_store_local_sale_date()
    {
        var service = CreateService(TestStoreTimeZones.Sydney, DateTimeOffset.Parse("2026-11-02T01:00:00Z"));

        // 2025-11-01 13:30Z 在悉尼已是 2025-11-02 00:30：按门店日期算，正好是最早可退那天。
        var boundary = await service.CheckReturnWindowAsync("S01", DateTimeOffset.Parse("2025-11-01T13:30:00Z"), CancellationToken.None);
        var dayBefore = await service.CheckReturnWindowAsync("S01", DateTimeOffset.Parse("2025-11-01T12:30:00Z"), CancellationToken.None);

        Assert.Null(boundary);
        Assert.NotNull(dayBefore);
    }

    private static OrderQueryWindowService CreateService(
        TimeZoneInfo storeTimeZone,
        DateTimeOffset now,
        OrderQueryWindowOptions? options = null) =>
        new(
            new StubStoreTimeZoneResolver(storeTimeZone),
            Options.Create(options ?? new OrderQueryWindowOptions()),
            new FixedTimeProvider(now));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
