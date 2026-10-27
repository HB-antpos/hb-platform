using BlazorApp.Api.Services.OperationAudits;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>操作日志按门店时区换算：本地时间解析、按时区分段、夏令时边界。</summary>
public sealed class OperationAuditStoreTimeZonesTests
{
    private static OperationAuditStoreTimeZones CreateZones() =>
        new(new Dictionary<string, string>
        {
            ["1014"] = "Australia/Brisbane",
            ["1001"] = "Australia/Brisbane",
            ["1018"] = "Australia/Sydney",
            ["2012"] = "Australia/Melbourne",
        });

    [Theory]
    [InlineData("2026-10-27T16:20:24", true)]
    [InlineData("2026-10-27 16:20:24", true)]
    [InlineData("2026-10-27T16:20", true)]
    [InlineData("2026-10-27T16:20:24.123", true)]
    // 带 Z / 偏移说明前端传的是 UTC 而不是门店墙钟，必须拒绝，避免被当成本地时间。
    [InlineData("2026-10-27T05:20:24Z", false)]
    [InlineData("2026-10-27T16:20:24+11:00", false)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParseLocal_AcceptsOnlyZoneLessWallClock(string? value, bool expected)
    {
        Assert.Equal(expected, OperationAuditStoreTimeZones.TryParseLocal(value, out var local));
        if (expected)
        {
            Assert.Equal(DateTimeKind.Unspecified, local.Kind);
        }
    }

    [Fact]
    public void Resolve_ReturnsConfiguredZoneAndFallsBackToSydney()
    {
        var zones = CreateZones();

        Assert.Equal("Australia/Brisbane", zones.Resolve("1014"));
        Assert.Equal("Australia/Brisbane", zones.Resolve(" 1014 "));
        Assert.Equal("Australia/Sydney", zones.Resolve("1018"));
        Assert.Equal("Australia/Melbourne", zones.Resolve("2012"));
        Assert.Equal("Australia/Sydney", zones.Resolve("UNKNOWN"));
        Assert.Equal("Australia/Sydney", zones.Resolve(null));
        Assert.Equal("Australia/Sydney", OperationAuditStoreTimeZones.Empty.Resolve("1014"));
    }

    [Fact]
    public void BuildSegments_GroupsStoresByZoneAndKeepsSydneyAsDefaultSegment()
    {
        // 现场案例：同一个墙钟范围 16:20–16:29，悉尼店（UTC+11）和昆士兰店（UTC+10）对应不同 UTC 区间。
        var segments = CreateZones().BuildSegments(
            new DateTime(2026, 10, 27, 16, 20, 0),
            new DateTime(2026, 10, 27, 16, 29, 0));

        var brisbane = Assert.Single(segments, segment => segment.TimeZoneId == "Australia/Brisbane");
        Assert.False(brisbane.IsDefault);
        Assert.Equal(["1001", "1014"], brisbane.StoreCodes);
        Assert.Equal(new DateTime(2026, 10, 27, 6, 20, 0, DateTimeKind.Utc), brisbane.FromUtc);
        Assert.Equal(new DateTime(2026, 10, 27, 6, 29, 0, DateTimeKind.Utc), brisbane.ToUtc);

        var melbourne = Assert.Single(segments, segment => segment.TimeZoneId == "Australia/Melbourne");
        Assert.Equal(["2012"], melbourne.StoreCodes);
        Assert.Equal(new DateTime(2026, 10, 27, 5, 20, 0, DateTimeKind.Utc), melbourne.FromUtc);

        // 默认段：悉尼及门店表里没有的编码，排除其他时区门店。
        var sydney = Assert.Single(segments, segment => segment.IsDefault);
        Assert.Equal("Australia/Sydney", sydney.TimeZoneId);
        Assert.Equal(["1001", "1014", "2012"], sydney.StoreCodes);
        Assert.Equal(new DateTime(2026, 10, 27, 5, 20, 0, DateTimeKind.Utc), sydney.FromUtc);
        Assert.Equal(new DateTime(2026, 10, 27, 5, 29, 0, DateTimeKind.Utc), sydney.ToUtc);
    }

    [Fact]
    public void BuildSegments_WithoutStoreData_ReturnsOnlyDefaultSegmentCoveringAllStores()
    {
        var segments = OperationAuditStoreTimeZones.Empty.BuildSegments(
            new DateTime(2026, 7, 1, 10, 0, 0),
            new DateTime(2026, 7, 1, 11, 0, 0));

        var only = Assert.Single(segments);
        Assert.True(only.IsDefault);
        Assert.Empty(only.StoreCodes);
        // 7 月悉尼是标准时间 UTC+10。
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), only.FromUtc);
        Assert.Equal(new DateTime(2026, 7, 1, 1, 0, 0, DateTimeKind.Utc), only.ToUtc);
    }

    [Fact]
    public void BuildSegments_HandlesDaylightSavingBoundariesLikeTheWebPicker()
    {
        var zones = OperationAuditStoreTimeZones.Empty;

        // 2026-10-04 02:00 悉尼拨快 1 小时：02:30 不存在，顺延为 03:30（UTC+11）= 前一天 16:30Z。
        var gap = zones.BuildSegments(new DateTime(2026, 10, 4, 2, 30, 0), new DateTime(2026, 10, 4, 3, 30, 0));
        Assert.Equal(new DateTime(2026, 10, 3, 16, 30, 0, DateTimeKind.Utc), Assert.Single(gap).FromUtc);

        // 2026-04-05 03:00 悉尼拨回：02:30 出现两次，取后一次（标准时间 UTC+10）= 前一天 16:30Z。
        var overlap = zones.BuildSegments(new DateTime(2026, 4, 5, 2, 30, 0), new DateTime(2026, 4, 5, 2, 45, 0));
        Assert.Equal(new DateTime(2026, 4, 4, 16, 30, 0, DateTimeKind.Utc), Assert.Single(overlap).FromUtc);
    }
}
