using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 移动端客流总览（traffic/overview）：周期起止、对照期、分小时汇总、进店人次口径、夏令时归并与权限。
/// 基准「今天」2026-10-14 02:00 UTC：布里斯班（UTC+10）本地 10-14 12:00，星期三；悉尼夏令时 UTC+11。
/// </summary>
public sealed class FaceTrafficOverviewTests : IDisposable
{
    private readonly FaceMatchTestHarness _h = new();

    public FaceTrafficOverviewTests()
    {
        _h.Database.AddUser("mgr-1", "Manager", "s-1013");
    }

    public void Dispose() => _h.Dispose();

    // ---------- 基本信息 ----------

    [Fact]
    public async Task 基本信息_门店本地今天_取数时刻_当期标记()
    {
        var result = await Overview("1013", "day", null);

        Assert.Equal("1013", result.StoreCode);
        Assert.Equal("Orion", result.StoreName);
        Assert.Equal("Australia/Brisbane", result.TimeZoneId);
        Assert.Equal("day", result.Period);
        Assert.Equal("2026-10-14", result.Today);
        Assert.Equal("2026-10-14T12:00", result.AsOfLocal);
        Assert.True(result.IsCurrent);
        Assert.Equal(("2026-10-14", "2026-10-14"), (result.From, result.To));
        Assert.Equal(("2026-10-13", "2026-10-13"), (result.PrevFrom, result.PrevTo));
    }

    [Fact]
    public async Task 门店本地今天按门店时区算_悉尼已是次日()
    {
        // 基准 02:00Z：悉尼 UTC+11 = 13:00 同一天；把时间推到 14:00Z 悉尼已是 10-15 01:00，而布里斯班仍是 10-14 24:00 前。
        _h.Time.Now = new DateTime(2026, 10, 14, 14, 30, 0, DateTimeKind.Utc);

        var sydney = await Overview("2001", "day", null);
        var brisbane = await Overview("1013", "day", null);

        Assert.Equal("2026-10-15", sydney.Today);
        Assert.Equal("2026-10-15T01:30", sydney.AsOfLocal);
        Assert.Equal("2026-10-15", sydney.From);
        Assert.Equal("2026-10-15", brisbane.Today);   // 布里斯班 00:30 已过零点
        _h.Time.Now = new DateTime(2026, 10, 14, 13, 30, 0, DateTimeKind.Utc);
        Assert.Equal("2026-10-14", (await Overview("1013", "day", null)).Today);   // 布里斯班 23:30
    }

    // ---------- day ----------

    [Fact]
    public async Task day_今天与昨天_分小时计数与进店人次()
    {
        Bucket("1013", "2026-10-14T01:00:00", capture: 20, staff: 5, unique: 9, repeat: 4, low: 2, failure: 1);   // 今天本地 11:00
        Bucket("1013", "2026-10-14T00:00:00", capture: 10, staff: 2);                                           // 今天本地 10:00
        Bucket("1013", "2026-10-13T01:00:00", capture: 6, staff: 1);                                            // 昨天本地 11:00
        Bucket("1013", "2026-10-13T13:00:00", capture: 3);                                                      // 昨天本地 23:00

        var result = await Overview("1013", "day", "2026-10-14");

        var today = Assert.Single(result.Current.Days);
        Assert.Equal("2026-10-14", today.Date);
        Assert.Equal(30, today.Counts.CaptureCount);
        Assert.Equal(30 - 7 - 2 - 1, today.Counts.VisitCount);   // 抓拍 − 员工 − 低质量 − 失败
        Assert.Equal(24, result.Current.Hours.Count);
        Assert.Equal(Enumerable.Range(0, 24), result.Current.Hours.Select(h => h.Hour));
        Assert.Equal(8, Hour(result.Current, 10).VisitCount);
        Assert.Equal(12, Hour(result.Current, 11).VisitCount);
        Assert.Equal(9, Hour(result.Current, 11).UniqueVisitorCount);
        Assert.Equal(4, Hour(result.Current, 11).RepeatCount);
        Assert.Equal(1, result.Current.HourlyDays);   // 当天（进行中）有抓拍也算 1

        var yesterday = Assert.Single(result.Previous.Days);
        Assert.Equal("2026-10-13", yesterday.Date);
        Assert.Equal(9, yesterday.Counts.CaptureCount);
        Assert.Equal(5, Hour(result.Previous, 11).VisitCount);
        Assert.Equal(3, Hour(result.Previous, 23).VisitCount);
        Assert.Equal(1, result.Previous.HourlyDays);
        Assert.Equal(8, result.Previous.Total.VisitCount);
    }

    [Fact]
    public async Task day_进店人次对聚合后的数值取零下限_不是先逐桶取零再相加()
    {
        Bucket("1013", "2026-10-14T00:00:00", capture: 10, staff: 2);   // 本地 10 点：visit 8
        Bucket("1013", "2026-10-14T01:00:00", capture: 2, staff: 5);    // 本地 11 点：2−5 → 0

        var result = await Overview("1013", "day", null);

        Assert.Equal(8, Hour(result.Current, 10).VisitCount);
        Assert.Equal(0, Hour(result.Current, 11).VisitCount);
        Assert.Equal(12 - 7, result.Current.Total.VisitCount);   // 按聚合值 5，而不是 8 + 0
        Assert.Equal(5, result.Current.Days[0].Counts.VisitCount);
    }

    [Fact]
    public async Task day_当天无抓拍_hourlyDays为0_小时全为0()
    {
        Bucket("1013", "2026-10-14T01:00:00", capture: 0, staff: 0);   // 有桶但抓拍为 0

        var result = await Overview("1013", "day", null);

        Assert.Equal(0, result.Current.HourlyDays);
        Assert.Equal(0, result.Previous.HourlyDays);
        Assert.Equal(24, result.Current.Hours.Count);
        Assert.All(result.Current.Hours, h => Assert.Equal(0, h.Counts.CaptureCount));
    }

    [Fact]
    public async Task day_查历史日_isCurrent为false_对照为前一天()
    {
        var result = await Overview("1013", "day", "2026-10-01");

        Assert.False(result.IsCurrent);
        Assert.Equal(("2026-10-01", "2026-10-01"), (result.From, result.To));
        Assert.Equal(("2026-09-30", "2026-09-30"), (result.PrevFrom, result.PrevTo));
        Assert.Equal("2026-10-14", result.Today);
    }

    // ---------- week ----------

    [Fact]
    public async Task week_周一到周日边界_对照为上一整周()
    {
        // 今天周三 10-14：本周 10-12(周一)..10-18(周日)，上周 10-05..10-11。
        Bucket("1013", "2026-10-11T13:00:00", capture: 4);   // 周日 23:00 本地 → 上周
        Bucket("1013", "2026-10-11T14:00:00", capture: 7);   // 周一 00:00 本地 → 本周
        Bucket("1013", "2026-10-18T13:00:00", capture: 2);   // 下周日 23:00（未来日）→ 本周
        Bucket("1013", "2026-10-18T14:00:00", capture: 90);  // 下周一 00:00 → 都不算
        Bucket("1013", "2026-10-04T13:00:00", capture: 80);  // 上上周日 → 都不算

        var result = await Overview("1013", "week", null);

        Assert.Equal(("2026-10-12", "2026-10-18"), (result.From, result.To));
        Assert.Equal(("2026-10-05", "2026-10-11"), (result.PrevFrom, result.PrevTo));
        Assert.True(result.IsCurrent);
        Assert.Equal(7, result.Current.Days.Count);
        Assert.Equal(7, result.Previous.Days.Count);
        Assert.Equal(["2026-10-12", "2026-10-13", "2026-10-14", "2026-10-15", "2026-10-16", "2026-10-17", "2026-10-18"], result.Current.Days.Select(d => d.Date));
        Assert.Equal(7, result.Current.Days[0].Counts.CaptureCount);
        Assert.Equal(2, result.Current.Days[6].Counts.CaptureCount);   // 未来日也照常带数据（这里是造数）
        Assert.Equal(9, result.Current.Total.CaptureCount);
        Assert.Equal(4, result.Previous.Days[6].Counts.CaptureCount);
        Assert.Equal(4, result.Previous.Total.CaptureCount);
    }

    [Fact]
    public async Task week_锚点是周日或周一_都落在同一周()
    {
        var sunday = await Overview("1013", "week", "2026-10-11");
        var monday = await Overview("1013", "week", "2026-10-05");

        Assert.Equal(("2026-10-05", "2026-10-11"), (sunday.From, sunday.To));
        Assert.Equal(("2026-09-28", "2026-10-04"), (sunday.PrevFrom, sunday.PrevTo));
        Assert.False(sunday.IsCurrent);
        Assert.Equal(("2026-10-05", "2026-10-11"), (monday.From, monday.To));
    }

    [Fact]
    public async Task week_分小时只汇总已结束且有抓拍的日子_今天与无抓拍日不计入()
    {
        // 本周：周一 10-12 有抓拍（计入）；周二 10-13 只有全 0 的桶（不计）；周三 10-14 今天有抓拍（未结束，不计）。
        Bucket("1013", "2026-10-12T01:00:00", capture: 10, staff: 2);   // 周一本地 11:00
        Bucket("1013", "2026-10-12T02:00:00", capture: 5);              // 周一本地 12:00
        Bucket("1013", "2026-10-13T01:00:00");                          // 周二：全 0
        Bucket("1013", "2026-10-14T01:00:00", capture: 100);            // 今天本地 11:00
        // 上周：周一 10-05、周三 10-07 有抓拍（计入），周五 10-09 无任何桶。
        Bucket("1013", "2026-10-05T01:00:00", capture: 6);
        Bucket("1013", "2026-10-07T01:00:00", capture: 4, low: 1);
        Bucket("1013", "2026-10-07T03:00:00", capture: 3);

        var result = await Overview("1013", "week", null);

        Assert.Equal(1, result.Current.HourlyDays);
        Assert.Equal(10, Hour(result.Current, 11).CaptureCount);   // 不含今天的 100
        Assert.Equal(8, Hour(result.Current, 11).VisitCount);
        Assert.Equal(5, Hour(result.Current, 12).CaptureCount);
        Assert.Equal(115, result.Current.Total.CaptureCount);      // Total 含今天

        Assert.Equal(2, result.Previous.HourlyDays);
        Assert.Equal(10, Hour(result.Previous, 11).CaptureCount);   // 6 + 4
        Assert.Equal(9, Hour(result.Previous, 11).VisitCount);      // 10 − 1
        Assert.Equal(3, Hour(result.Previous, 13).CaptureCount);
        Assert.Equal(24, result.Previous.Hours.Count);
    }

    [Fact]
    public async Task week_跨年的一周()
    {
        // 2026-01-01 是周四：2025-12-29..2026-01-04；上一周 2025-12-22..12-28。
        var result = await Overview("1013", "week", "2026-01-01");

        Assert.Equal(("2025-12-29", "2026-01-04"), (result.From, result.To));
        Assert.Equal(("2025-12-22", "2025-12-28"), (result.PrevFrom, result.PrevTo));
        Assert.Equal("2025-12-31", result.Current.Days[2].Date);
        Assert.Equal("2026-01-01", result.Current.Days[3].Date);
    }

    // ---------- month ----------

    [Fact]
    public async Task month_整月与上月_边界在本地午夜()
    {
        Bucket("1013", "2026-09-30T13:00:00", capture: 3);   // 9-30 本地 23:00 → 上月
        Bucket("1013", "2026-09-30T14:00:00", capture: 5);   // 10-01 本地 00:00 → 本月
        Bucket("1013", "2026-10-31T13:00:00", capture: 7);   // 10-31 本地 23:00 → 本月（未来日）
        Bucket("1013", "2026-08-31T13:00:00", capture: 50);  // 8-31 → 都不算

        var result = await Overview("1013", "month", null);

        Assert.Equal(("2026-10-01", "2026-10-31"), (result.From, result.To));
        Assert.Equal(("2026-09-01", "2026-09-30"), (result.PrevFrom, result.PrevTo));
        Assert.Equal(31, result.Current.Days.Count);
        Assert.Equal(30, result.Previous.Days.Count);
        Assert.Equal(5, result.Current.Days[0].Counts.CaptureCount);
        Assert.Equal(7, result.Current.Days[30].Counts.CaptureCount);
        Assert.Equal(12, result.Current.Total.CaptureCount);
        Assert.Equal(3, result.Previous.Days[29].Counts.CaptureCount);
        Assert.Equal(3, result.Previous.Total.CaptureCount);
    }

    [Fact]
    public async Task month_二月与闰年_对照为上一整月()
    {
        var feb = await Overview("1013", "month", "2026-02-10");
        Assert.Equal(("2026-02-01", "2026-02-28"), (feb.From, feb.To));
        Assert.Equal(("2026-01-01", "2026-01-31"), (feb.PrevFrom, feb.PrevTo));
        Assert.Equal(28, feb.Current.Days.Count);
        Assert.Equal(31, feb.Previous.Days.Count);
        Assert.False(feb.IsCurrent);

        var leap = await Overview("1013", "month", "2024-02-29");
        Assert.Equal(("2024-02-01", "2024-02-29"), (leap.From, leap.To));
        Assert.Equal(29, leap.Current.Days.Count);

        // 三月回看二月：对照期是 28 天的 2 月。
        var mar = await Overview("1013", "month", "2026-03-31");
        Assert.Equal(("2026-02-01", "2026-02-28"), (mar.PrevFrom, mar.PrevTo));
    }

    [Fact]
    public async Task month_跨年_一月对照上年十二月()
    {
        var result = await Overview("1013", "month", "2026-01-15");

        Assert.Equal(("2026-01-01", "2026-01-31"), (result.From, result.To));
        Assert.Equal(("2025-12-01", "2025-12-31"), (result.PrevFrom, result.PrevTo));
        Assert.Equal(31, result.Previous.Days.Count);
    }

    [Fact]
    public async Task month_分小时汇总只含已结束且有抓拍的日子()
    {
        Bucket("1013", "2026-10-01T01:00:00", capture: 4);   // 10-01 本地 11:00
        Bucket("1013", "2026-10-02T01:00:00", capture: 6);   // 10-02 本地 11:00
        Bucket("1013", "2026-10-14T01:00:00", capture: 99);  // 今天：不计入分小时
        Bucket("1013", "2026-09-10T01:00:00", capture: 2);   // 上月 9-10

        var result = await Overview("1013", "month", null);

        Assert.Equal(2, result.Current.HourlyDays);
        Assert.Equal(10, Hour(result.Current, 11).CaptureCount);
        Assert.Equal(109, result.Current.Total.CaptureCount);
        Assert.Equal(1, result.Previous.HourlyDays);
        Assert.Equal(2, Hour(result.Previous, 11).CaptureCount);
    }

    // ---------- 夏令时 ----------

    [Fact]
    public async Task 夏令时开始日_悉尼缺02点_小时为0()
    {
        // 悉尼 2026-10-04 02:00 → 03:00（UTC 10-03T16:00Z 起为 UTC+11）。本地 10-04 00:00 = 10-03T14:00Z。
        Bucket("2001", "2026-10-03T14:00:00", capture: 1);   // 本地 00:00
        Bucket("2001", "2026-10-03T15:00:00", capture: 2);   // 本地 01:00
        Bucket("2001", "2026-10-03T16:00:00", capture: 4);   // 本地 03:00（02 点不存在）
        Bucket("2001", "2026-10-04T12:00:00", capture: 8);   // 本地 23:00

        var result = await Overview("2001", "day", "2026-10-04");

        Assert.Equal("Australia/Sydney", result.TimeZoneId);
        Assert.Equal(24, result.Current.Hours.Count);
        Assert.Equal(0, Hour(result.Current, 2).CaptureCount);
        Assert.Equal(2, Hour(result.Current, 1).CaptureCount);
        Assert.Equal(4, Hour(result.Current, 3).CaptureCount);
        Assert.Equal(8, Hour(result.Current, 23).CaptureCount);
        Assert.Equal(15, result.Current.Days[0].Counts.CaptureCount);
    }

    [Fact]
    public async Task 夏令时结束日_悉尼02点两个UTC小时合并()
    {
        // 悉尼 2027-04-04 03:00 AEDT → 02:00 AEST：本地 02 点出现两次（04-03T15:00Z 与 16:00Z）。
        _h.Time.Now = new DateTime(2027, 4, 10, 2, 0, 0, DateTimeKind.Utc);
        Bucket("2001", "2027-04-03T14:00:00", capture: 3);   // 本地 01:00
        Bucket("2001", "2027-04-03T15:00:00", capture: 5, staff: 1);   // 本地 02:00（第一次）
        Bucket("2001", "2027-04-03T16:00:00", capture: 7, staff: 2);   // 本地 02:00（第二次）
        Bucket("2001", "2027-04-03T17:00:00", capture: 11);  // 本地 03:00

        var result = await Overview("2001", "day", "2027-04-04");

        Assert.Equal(3, Hour(result.Current, 1).CaptureCount);
        Assert.Equal(12, Hour(result.Current, 2).CaptureCount);
        Assert.Equal(9, Hour(result.Current, 2).VisitCount);   // 12 − 3
        Assert.Equal(11, Hour(result.Current, 3).CaptureCount);
        Assert.Equal(26, result.Current.Days[0].Counts.CaptureCount);
        Assert.Equal(24, result.Current.Hours.Count);
    }

    // ---------- 校验与权限 ----------

    [Theory]
    [InlineData("day", "2026-10-15")]    // 晚于门店本地今天
    [InlineData("week", "2026-10-15")]
    [InlineData("month", "2027-01-01")]
    [InlineData("day", "2026/10/01")]    // 格式错
    [InlineData("day", "20261001")]
    [InlineData("day", "not-a-date")]
    [InlineData("day", "1999-12-31")]    // 过早，防下溢
    [InlineData("year", "2026-10-01")]   // period 非法
    [InlineData("Quarter", null)]
    public async Task 参数非法_400(string period, string? anchor)
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Traffic.GetOverviewAsync("1013", period, anchor, FaceMatchTestHarness.Admin));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("FACE_MATCH_FIELD_INVALID", ex.Code);
    }

    [Fact]
    public async Task period缺省按day_anchor等于今天可以查询()
    {
        var result = await _h.Traffic.GetOverviewAsync("1013", null, "2026-10-14", FaceMatchTestHarness.Admin);

        Assert.Equal("day", result.Period);
        Assert.Equal("2026-10-14", result.From);
    }

    [Fact]
    public async Task 门店编码非法或不存在()
    {
        var invalid = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Traffic.GetOverviewAsync("", "day", null, FaceMatchTestHarness.Admin));
        var unknown = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Traffic.GetOverviewAsync("0000", "day", null, FaceMatchTestHarness.Admin));

        Assert.Equal(400, invalid.StatusCode);
        Assert.Equal(404, unknown.StatusCode);
    }

    [Fact]
    public async Task 非管理员只能看自己所属门店_其他门店403()
    {
        var manager = new FaceMatchActor("mgr-1", "Manager");
        Bucket("1013", "2026-10-14T01:00:00", capture: 4);

        var own = await _h.Traffic.GetOverviewAsync("1013", "day", null, manager);
        var other = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Traffic.GetOverviewAsync("2001", "day", null, manager));

        Assert.Equal(4, own.Current.Total.CaptureCount);
        Assert.Equal(403, other.StatusCode);
        Assert.Equal("FACE_MATCH_STORE_FORBIDDEN", other.Code);
    }

    // ---------- 档案开关 ----------

    [Fact]
    public async Task 无档案_计数与去重都为false_数据照常返回()
    {
        Bucket("1013", "2026-10-14T01:00:00", capture: 4);

        var result = await Overview("1013", "day", null);

        Assert.False(result.CountingEnabled);
        Assert.False(result.TrafficDedupEnabled);
        Assert.Equal(4, result.Current.Total.CaptureCount);
        Assert.Equal(24, result.Current.Hours.Count);
    }

    [Fact]
    public async Task Capture模式_countingEnabled为false_无桶时按0返回不报错()
    {
        _h.Db.Insertable(new FaceStoreProfile
        {
            StoreCode = "1013",
            Mode = FaceStoreMode.Capture,
            TrafficDedupEnabled = true,
            UpdatedAtUtc = FaceMatchTestDatabase.Now,
        }).ExecuteCommand();

        var result = await Overview("1013", "week", null);

        Assert.False(result.CountingEnabled);
        Assert.True(result.TrafficDedupEnabled);   // 两个开关各自独立取档案值
        Assert.Equal(7, result.Current.Days.Count);
        Assert.All(result.Current.Days, d => Assert.Equal(0, d.Counts.CaptureCount));
        Assert.Equal(0, result.Current.HourlyDays);
        Assert.Equal(0, result.Current.Total.VisitCount);
    }

    [Fact]
    public async Task Match档案_countingEnabled为true_去重开关取档案值()
    {
        _h.Db.Insertable(new FaceStoreProfile
        {
            StoreCode = "1013",
            Mode = FaceStoreMode.Match,
            TrafficDedupEnabled = true,
            UpdatedAtUtc = FaceMatchTestDatabase.Now,
        }).ExecuteCommand();
        _h.Db.Insertable(new FaceStoreProfile
        {
            StoreCode = "2001",
            Mode = FaceStoreMode.Match,
            TrafficDedupEnabled = false,
            UpdatedAtUtc = FaceMatchTestDatabase.Now,
        }).ExecuteCommand();

        var orion = await Overview("1013", "day", null);
        var bankstown = await Overview("2001", "day", null);

        Assert.True(orion.CountingEnabled);
        Assert.True(orion.TrafficDedupEnabled);
        Assert.True(bankstown.CountingEnabled);
        Assert.False(bankstown.TrafficDedupEnabled);
    }

    [Fact]
    public async Task 档案时区覆盖门店表_归日按档案时区()
    {
        _h.Db.Insertable(new FaceStoreProfile { StoreCode = "1013", TimeZoneId = "Australia/Sydney", Mode = FaceStoreMode.Match, UpdatedAtUtc = FaceMatchTestDatabase.Now }).ExecuteCommand();
        Bucket("1013", "2026-10-14T13:00:00", capture: 8);   // 悉尼 10-15 00:00

        var result = await Overview("1013", "week", null);

        Assert.Equal("Australia/Sydney", result.TimeZoneId);
        Assert.Equal(8, result.Current.Days.Single(d => d.Date == "2026-10-15").Counts.CaptureCount);
    }

    [Fact]
    public async Task 响应里没有任何生物信息字段()
    {
        Bucket("1013", "2026-10-14T01:00:00", capture: 4);

        var json = System.Text.Json.JsonSerializer.Serialize(await Overview("1013", "month", null));

        foreach (var forbidden in new[] { "image", "face-match", "personId", "score", "objectKey" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------- 辅助 ----------

    private Task<FaceTrafficOverviewDto> Overview(string store, string period, string? anchor) =>
        _h.Traffic.GetOverviewAsync(store, period, anchor, FaceMatchTestHarness.Admin);

    private static FaceTrafficOverviewCountsDto Hour(FaceTrafficOverviewSegmentDto segment, int hour) =>
        segment.Hours.Single(h => h.Hour == hour).Counts;

    private void Bucket(
        string storeCode, string hourStartUtc, int capture = 0, int staff = 0, int unique = 0, int repeat = 0, int low = 0, int failure = 0) =>
        _h.Db.Insertable(new FaceTrafficBucket
        {
            StoreCode = storeCode,
            BucketStartUtc = DateTime.SpecifyKind(DateTime.Parse(hourStartUtc), DateTimeKind.Utc),
            CaptureCount = capture,
            StaffMatchCount = staff,
            UniqueVisitorCount = unique,
            RepeatCount = repeat,
            LowQualityCount = low,
            ApiFailureCount = failure,
        }).ExecuteCommand();
}
