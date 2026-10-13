using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 客流统计与审计查询：计数桶按整点 UTC 存，按门店时区归到本地营业日 / 小时（布里斯班 UTC+10、悉尼夏令时 UTC+11），
/// 与运行机器的时区无关；非管理员只能看自己门店。基准「今天」2026-10-14 02:00 UTC。
/// </summary>
public sealed class FaceTrafficTests : IDisposable
{
    private readonly FaceMatchTestHarness _h = new();

    public FaceTrafficTests()
    {
        _h.Database.AddUser("mgr-1", "Manager", "s-1013");
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task 单日统计_按门店本地时区归日_分小时明细_布里斯班()
    {
        // 布里斯班 UTC+10：10-13T13:00Z = 10-13 23:00 本地；10-13T14:00Z = 10-14 00:00 本地；10-14T13:00Z = 10-14 23:00 本地。
        Bucket("1013", "2026-10-13T13:00:00", capture: 5, staff: 2, unique: 1, repeat: 1, low: 1, failure: 0);
        Bucket("1013", "2026-10-13T14:00:00", capture: 10, staff: 3, unique: 4, repeat: 2, low: 1, failure: 0);
        Bucket("1013", "2026-10-14T01:00:00", capture: 20, staff: 5, unique: 9, repeat: 4, low: 2, failure: 1);   // 本地 11:00
        Bucket("1013", "2026-10-14T13:00:00", capture: 7, staff: 0, unique: 3, repeat: 2, low: 1, failure: 1);    // 本地 23:00
        Bucket("1013", "2026-10-14T14:00:00", capture: 99, staff: 99, unique: 99, repeat: 99, low: 99, failure: 99); // 本地 10-15：不算

        var result = await _h.Traffic.GetTrafficAsync("1013", "2026-10-14", "2026-10-14", FaceMatchTestHarness.Admin);

        Assert.Equal("Australia/Brisbane", result.TimeZoneId);
        Assert.Equal("Orion", result.StoreName);
        var day = Assert.Single(result.Days);
        Assert.Equal("2026-10-14", day.Date);
        Assert.Equal(new FaceTrafficCountsDto { CaptureCount = 37, StaffMatchCount = 8, UniqueVisitorCount = 16, RepeatCount = 8, LowQualityCount = 4, ApiFailureCount = 2 }, day.Counts);
        Assert.Equal(24, result.Hours.Count);
        Assert.Equal(10, result.Hours.Single(h => h.Hour == 0).Counts.CaptureCount);
        Assert.Equal(20, result.Hours.Single(h => h.Hour == 11).Counts.CaptureCount);
        Assert.Equal(7, result.Hours.Single(h => h.Hour == 23).Counts.CaptureCount);
        Assert.Equal(37, result.Total.CaptureCount);
        Assert.True(result.UniqueVisitorIsEstimate);
    }

    [Fact]
    public async Task 悉尼夏令时_UTC加11归日()
    {
        // 悉尼 2026-10-14T13:00Z = 10-15 00:00 本地（UTC+11）；若误按 UTC+10 会算成 10-14 23:00。
        Bucket("2001", "2026-10-14T12:00:00", capture: 3);
        Bucket("2001", "2026-10-14T13:00:00", capture: 8);

        var result = await _h.Traffic.GetTrafficAsync("2001", "2026-10-14", "2026-10-15", FaceMatchTestHarness.Admin);

        Assert.Equal("Australia/Sydney", result.TimeZoneId);
        Assert.Equal([3, 8], result.Days.Select(d => d.Counts.CaptureCount));
        Assert.Empty(result.Hours);   // 多日范围不返回分小时明细
    }

    [Fact]
    public async Task 门店档案里的时区覆盖门店表()
    {
        _h.Db.Insertable(new FaceStoreProfile { StoreCode = "1013", TimeZoneId = "Australia/Sydney", UpdatedAtUtc = FaceMatchTestDatabase.Now }).ExecuteCommand();
        Bucket("1013", "2026-10-14T13:00:00", capture: 8);   // 按悉尼算是 10-15 00:00

        var result = await _h.Traffic.GetTrafficAsync("1013", "2026-10-14", "2026-10-15", FaceMatchTestHarness.Admin);

        Assert.Equal("Australia/Sydney", result.TimeZoneId);
        Assert.Equal([0, 8], result.Days.Select(d => d.Counts.CaptureCount));
    }

    [Fact]
    public async Task 默认最近7天_含零计数的日子_按门店本地今天结束()
    {
        Bucket("1013", "2026-10-14T01:00:00", capture: 4);

        var result = await _h.Traffic.GetTrafficAsync("1013", null, null, FaceMatchTestHarness.Admin);

        Assert.Equal(7, result.Days.Count);
        Assert.Equal("2026-10-08", result.From);
        Assert.Equal("2026-10-14", result.To);
        Assert.Equal(4, result.Days[^1].Counts.CaptureCount);
        Assert.All(result.Days.Take(6), d => Assert.Equal(0, d.Counts.CaptureCount));
        Assert.Empty(result.Hours);
    }

    [Theory]
    [InlineData("2026-10-15", "2026-10-14")]
    [InlineData("2026/10/01", "2026-10-14")]
    [InlineData("2026-01-01", "2026-10-14")]   // 超过 92 天
    public async Task 日期范围非法_400(string from, string to)
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Traffic.GetTrafficAsync("1013", from, to, FaceMatchTestHarness.Admin));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task 恰好92天可以查询()
    {
        var result = await _h.Traffic.GetTrafficAsync("1013", "2026-07-15", "2026-10-14", FaceMatchTestHarness.Admin);
        Assert.Equal(92, result.Days.Count);
    }

    [Fact]
    public async Task 门店编码非法或不存在()
    {
        var invalid = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Traffic.GetTrafficAsync("", null, null, FaceMatchTestHarness.Admin));
        var unknown = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Traffic.GetTrafficAsync("0000", null, null, FaceMatchTestHarness.Admin));
        Assert.Equal(400, invalid.StatusCode);
        Assert.Equal(404, unknown.StatusCode);
    }

    [Fact]
    public async Task 非管理员只能看自己所属门店()
    {
        var manager = new FaceMatchActor("mgr-1", "Manager");
        Bucket("1013", "2026-10-14T01:00:00", capture: 4);
        Bucket("2001", "2026-10-14T01:00:00", capture: 4);

        var own = await _h.Traffic.GetTrafficAsync("1013", null, null, manager);
        var other = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Traffic.GetTrafficAsync("2001", null, null, manager));

        Assert.Equal(4, own.Total.CaptureCount);
        Assert.Equal(403, other.StatusCode);
        Assert.Equal("FACE_MATCH_STORE_FORBIDDEN", other.Code);
    }

    [Fact]
    public async Task 响应里没有任何生物信息字段()
    {
        Bucket("1013", "2026-10-14T01:00:00", capture: 4);

        var json = System.Text.Json.JsonSerializer.Serialize(
            await _h.Traffic.GetTrafficAsync("1013", null, null, FaceMatchTestHarness.Admin));

        foreach (var forbidden in new[] { "image", "face-match", "personId", "score", "objectKey" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------- 审计查询 ----------

    [Fact]
    public async Task 审计查询_倒序分页_按门店动作时间筛选()
    {
        for (var i = 0; i < 5; i++)
        {
            _h.Time.Now = FaceMatchTestDatabase.Now.AddMinutes(i);
            await _h.Audit.WriteAsync(FaceMatchTestHarness.Admin, i % 2 == 0 ? "profile.update" : "consent.create", i < 3 ? "1013" : "2001", $"t{i}", "ok");
        }

        var page1 = await _h.Traffic.ListAuditAsync(null, null, null, null, 1, 2);
        var page3 = await _h.Traffic.ListAuditAsync(null, null, null, null, 3, 2);
        Assert.Equal(["t4", "t3"], page1.Items.Select(a => a.TargetRef));
        Assert.True(page1.HasMore);
        Assert.Equal(["t0"], page3.Items.Select(a => a.TargetRef));
        Assert.False(page3.HasMore);

        Assert.Equal(["t2", "t1", "t0"], (await _h.Traffic.ListAuditAsync("1013", null, null, null, 1, 20)).Items.Select(a => a.TargetRef));
        Assert.Equal(["t4", "t2", "t0"], (await _h.Traffic.ListAuditAsync(null, "profile.update", null, null, 1, 20)).Items.Select(a => a.TargetRef));
        var window = await _h.Traffic.ListAuditAsync(null, null, FaceMatchTestDatabase.Now.AddMinutes(1), FaceMatchTestDatabase.Now.AddMinutes(3), 1, 20);
        Assert.Equal(["t2", "t1"], window.Items.Select(a => a.TargetRef));
        var item = page1.Items[0];
        Assert.Equal("admin-1", item.ActorId);
        Assert.Equal("admin.tester", item.ActorName);
        Assert.Equal(DateTimeKind.Utc, item.AtUtc.Kind);
    }

    [Fact]
    public async Task 审计写入_去控制字符并截断_不含对象键与密钥()
    {
        await _h.Audit.WriteAsync(
            new FaceMatchActor("id\r\nforged", new string('n', 300)), "a\tb", "1013\n", new string('t', 300), "ok\u0007");

        var row = Assert.Single(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());
        Assert.Equal("idforged", row.ActorId);
        Assert.Equal(100, row.ActorName.Length);
        Assert.Equal("ab", row.Action);
        Assert.Equal("1013", row.StoreCode);
        Assert.Equal(100, row.TargetRef!.Length);
        Assert.Equal("ok", row.Result);
    }

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
