using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 门店分时客流投影（FaceTrafficBucket UTC 整点桶 → FaceTrafficHourly 门店本地「日期 + 小时」）：
/// 布里斯班（UTC+10，无夏令时）整点换算、悉尼夏令时开始日（本地 02 点缺失）、结束日（本地 02 点出现两次，两个 UTC 小时必须相加）、
/// 跨本地日、幂等重算、晚到计数覆盖、改时区后窗口内被新时区覆盖而窗口外保持、VisitCount 不为负、没有桶的门店不产生行、
/// 手工回填 / 历史补齐，以及后台任务⑧的调度行为（不依赖腾讯云、表缺失不崩、总开关关闭空转）。
/// 基准「现在」2026-10-14 02:00 UTC = 布里斯班 12:00 = 悉尼 13:00（夏令时 UTC+11）。时间全部来自可注入的 TimeProvider，不依赖机器时区。
/// </summary>
public sealed class FaceTrafficHourlyTests : IDisposable
{
    private const string Brisbane = "Australia/Brisbane";
    private const string Sydney = "Australia/Sydney";

    private readonly FaceMatchTestHarness _h = new();
    private readonly FaceTrafficHourlyProjector _projector;

    public FaceTrafficHourlyTests()
    {
        _projector = new FaceTrafficHourlyProjector(
            _h.Database.Context, _h.Time, _h.Logs.CreateLogger<FaceTrafficHourlyProjector>());
    }

    public void Dispose() => _h.Dispose();

    // ---------- 换算 ----------

    [Fact]
    public async Task 布里斯班_UTC整点换算成本地日期和小时_时区与计算时间落行_派生列正确()
    {
        // 布里斯班 UTC+10：10-13T13:00Z = 10-13 23:00；10-13T14:00Z = 10-14 00:00；10-14T01:00Z = 10-14 11:00。
        Bucket("1013", "2026-10-13T13:00:00", capture: 5, staff: 2, unique: 1, repeat: 1, low: 1, failure: 0);
        Bucket("1013", "2026-10-13T14:00:00", capture: 10, staff: 3, unique: 4, repeat: 2, low: 1, failure: 0);
        Bucket("1013", "2026-10-14T01:00:00", capture: 20, staff: 5, unique: 9, repeat: 4, low: 2, failure: 1);

        var result = await _projector.ProjectRecentAsync(default);

        Assert.False(result.SchemaMissing);
        Assert.Equal(new FaceTrafficHourlyProjection(false, 1, 0, 3, 0, 0), result);
        Assert.Equal(["2026-10-13 23", "2026-10-14 00", "2026-10-14 11"], Keys("1013"));
        var row = Row("1013", "2026-10-14", 11);
        Assert.Equal((20, 5, 9, 4, 2, 1), (row.CaptureCount, row.StaffMatchCount, row.UniqueVisitorCount, row.RepeatCount, row.LowQualityCount, row.ApiFailureCount));
        Assert.Equal(12, row.VisitCount);   // 20 - 5 - 2 - 1
        Assert.Equal(Brisbane, row.TimeZoneId);
        Assert.Equal(FaceMatchTestDatabase.Now, DateTime.SpecifyKind(row.ComputedAtUtc, DateTimeKind.Utc));
        Assert.Equal(2, Row("1013", "2026-10-13", 23).VisitCount);   // 5 - 2 - 1 - 0：员工与低质量都要扣
    }

    [Fact]
    public async Task 悉尼夏令时开始日_本地02点不存在_没有该行_03点起按UTC加11()
    {
        // 悉尼 2026-10-04 02:00 AEST（UTC+10）直接跳到 03:00 AEDT（UTC+11）：10-03T16:00Z 起本地是 03 点，没有 02 点。
        _h.Time.Now = new DateTime(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);   // 悉尼 10-04 21:00
        Bucket("2001", "2026-10-03T13:00:00", capture: 1);   // 10-03 23:00（AEST）
        Bucket("2001", "2026-10-03T14:00:00", capture: 2);   // 10-04 00:00
        Bucket("2001", "2026-10-03T15:00:00", capture: 3);   // 10-04 01:00
        Bucket("2001", "2026-10-03T16:00:00", capture: 4);   // 10-04 03:00（AEDT）
        Bucket("2001", "2026-10-03T17:00:00", capture: 5);   // 10-04 04:00

        await _projector.ProjectRecentAsync(default);

        Assert.Equal(["2026-10-03 23", "2026-10-04 00", "2026-10-04 01", "2026-10-04 03", "2026-10-04 04"], Keys("2001"));
        Assert.DoesNotContain("2026-10-04 02", Keys("2001"));
        Assert.Equal(14, Rows("2001").Where(r => r.StatDate == new DateTime(2026, 10, 4)).Sum(r => r.CaptureCount));
        Assert.All(Rows("2001"), r => Assert.Equal(Sydney, r.TimeZoneId));
    }

    [Fact]
    public async Task 悉尼夏令时结束日_本地02点出现两次_两个UTC小时的计数相加落在同一行()
    {
        // 悉尼 2027-04-04 03:00 AEDT（UTC+11）回拨到 02:00 AEST（UTC+10）：
        // 04-03T15:00Z 是 02:00 AEDT，04-03T16:00Z 是 02:00 AEST，两者都落在 (04-04, 小时 2)，必须相加。
        _h.Time.Now = new DateTime(2027, 4, 4, 5, 0, 0, DateTimeKind.Utc);   // 悉尼 04-04 15:00 AEST
        Bucket("2001", "2027-04-03T13:00:00", capture: 1);                                                   // 04-04 00:00
        Bucket("2001", "2027-04-03T14:00:00", capture: 2);                                                   // 04-04 01:00
        Bucket("2001", "2027-04-03T15:00:00", capture: 4, staff: 1, unique: 2, repeat: 1, low: 0, failure: 0);   // 04-04 02:00（AEDT）
        Bucket("2001", "2027-04-03T16:00:00", capture: 6, staff: 2, unique: 1, repeat: 0, low: 1, failure: 1);   // 04-04 02:00（AEST）
        Bucket("2001", "2027-04-03T17:00:00", capture: 8);                                                   // 04-04 03:00

        var result = await _projector.ProjectRecentAsync(default);

        Assert.Equal(4, result.RowsInserted);   // 5 个 UTC 桶 → 4 行
        Assert.Equal(["2027-04-04 00", "2027-04-04 01", "2027-04-04 02", "2027-04-04 03"], Keys("2001"));
        var merged = Row("2001", "2027-04-04", 2);
        Assert.Equal((10, 3, 3, 1, 1, 1), (merged.CaptureCount, merged.StaffMatchCount, merged.UniqueVisitorCount, merged.RepeatCount, merged.LowQualityCount, merged.ApiFailureCount));
        Assert.Equal(5, merged.VisitCount);   // 10 - 3 - 1 - 1
        Assert.Equal(21, Rows("2001").Sum(r => r.CaptureCount));   // 全天总数不丢
    }

    [Fact]
    public async Task 跨本地日_同一个UTC桶在不同时区的门店落在不同的本地日期和小时()
    {
        // 2026-10-13T13:00Z：布里斯班 10-13 23:00；悉尼（夏令时 UTC+11）10-14 00:00 —— UTC 日期与本地日期都不一定相同。
        Bucket("1013", "2026-10-13T13:00:00", capture: 7);
        Bucket("2001", "2026-10-13T13:00:00", capture: 9);
        // UTC 傍晚对应本地次日凌晨 / 清晨：2026-10-13T20:00Z → 布里斯班 10-14 06:00。
        Bucket("1013", "2026-10-13T20:00:00", capture: 3);

        await _projector.ProjectRecentAsync(default);

        Assert.Equal(["2026-10-13 23", "2026-10-14 06"], Keys("1013"));
        Assert.Equal(["2026-10-14 00"], Keys("2001"));
        Assert.Equal(7, Row("1013", "2026-10-13", 23).CaptureCount);
        Assert.Equal(9, Row("2001", "2026-10-14", 0).CaptureCount);
    }

    [Fact]
    public async Task 门店档案时区覆盖门店表_门店表缺失时按悉尼兜底_已删除门店仍然投影()
    {
        _h.Db.Insertable(new FaceStoreProfile { StoreCode = "1013", TimeZoneId = Sydney, UpdatedAtUtc = FaceMatchTestDatabase.Now }).ExecuteCommand();
        Bucket("1013", "2026-10-13T13:00:00", capture: 1);   // 档案是悉尼 → 10-14 00:00
        Bucket("7777", "2026-10-13T13:00:00", capture: 2);   // 门店表里没有这个编码 → 悉尼兜底
        Bucket("9999", "2026-10-13T13:00:00", capture: 3);   // 已删除的门店（无时区、无地址）→ 悉尼兜底

        await _projector.ProjectRecentAsync(default);

        Assert.Equal(["2026-10-14 00"], Keys("1013"));
        Assert.Equal(["2026-10-14 00"], Keys("7777"));
        Assert.Equal(["2026-10-14 00"], Keys("9999"));
        Assert.All(Rows(null), r => Assert.Equal(Sydney, r.TimeZoneId));
    }

    // ---------- 窗口 ----------

    [Fact]
    public async Task 窗口是最近3个门店本地日_窗口外的桶不投影_窗口外已有的行保持原样()
    {
        // 今天（布里斯班）10-14，窗口 10-12 ~ 10-14。
        Bucket("1013", "2026-10-11T13:00:00", capture: 4);   // 10-11 23:00：窗口外
        Bucket("1013", "2026-10-11T14:00:00", capture: 5);   // 10-12 00:00：窗口内第一小时
        Seed("1013", "2026-10-01", 9, Brisbane, capture: 99, computedAt: FaceMatchTestDatabase.Now.AddDays(-13));

        await _projector.ProjectRecentAsync(default);

        Assert.Equal(["2026-10-01 09", "2026-10-12 00"], Keys("1013"));
        Assert.Equal(99, Row("1013", "2026-10-01", 9).CaptureCount);   // 窗口外的历史行没被碰
    }

    [Fact]
    public async Task 没有任何计数桶的门店不产生行_最近没有计数桶时什么都不做()
    {
        // 只有 2001 有桶；1013 没有桶，不会产生行。
        Bucket("2001", "2026-10-14T01:00:00", capture: 4);
        await _projector.ProjectRecentAsync(default);
        Assert.Empty(Keys("1013"));
        Assert.Equal(["2026-10-14 12"], Keys("2001"));

        // 最近 4 天内没有任何桶（只有一个月前的桶）：廉价查询返回空闲，不写库。
        _h.Db.Deleteable<FaceTrafficBucket>().ExecuteCommand();
        _h.Db.Deleteable<FaceTrafficHourly>().ExecuteCommand();
        Bucket("1013", "2026-09-10T01:00:00", capture: 4);
        var idle = await _projector.ProjectRecentAsync(default);

        Assert.Equal(FaceTrafficHourlyProjection.Idle, idle);
        Assert.Empty(Rows(null));
    }

    // ---------- 幂等与覆盖 ----------

    [Fact]
    public async Task 同一窗口重复运行幂等_行数与总和不变_内容没变的行不被改写()
    {
        Bucket("1013", "2026-10-13T13:00:00", capture: 5, staff: 1);
        Bucket("1013", "2026-10-14T01:00:00", capture: 20, staff: 5, unique: 9);
        Bucket("2001", "2026-10-14T01:00:00", capture: 8);
        await _projector.ProjectRecentAsync(default);
        var first = Snapshot();

        _h.Time.Now = FaceMatchTestDatabase.Now.AddMinutes(10);
        var second = await _projector.ProjectRecentAsync(default);
        _h.Time.Now = FaceMatchTestDatabase.Now.AddMinutes(20);
        var third = await _projector.ProjectRecentAsync(default);

        Assert.Equal(0, second.RowsChanged);
        Assert.Equal(0, third.RowsChanged);
        Assert.Equal(2, second.StoresProcessed);
        Assert.Equal(first, Snapshot());                      // 逐行逐列一致，包括 ComputedAtUtc
        Assert.Equal(3, Rows(null).Count);
        Assert.Equal(33, Rows(null).Sum(r => r.CaptureCount));
    }

    [Fact]
    public async Task 晚到的计数更新后重算整行覆盖_其他行不动()
    {
        Bucket("1013", "2026-10-14T00:00:00", capture: 10);
        Bucket("1013", "2026-10-14T01:00:00", capture: 20);
        await _projector.ProjectRecentAsync(default);
        var untouched = Row("1013", "2026-10-14", 10).ComputedAtUtc;

        // 接收端晚到的计数：同一个 UTC 桶被 UPDATE 加了增量，新增了低质量与失败计数。
        var bucketStart = new DateTime(2026, 10, 14, 1, 0, 0);
        var bucketEnd = bucketStart.AddHours(1);
        _h.Db.Updateable<FaceTrafficBucket>()
            .SetColumns(b => new FaceTrafficBucket { CaptureCount = 26, LowQualityCount = 2, ApiFailureCount = 1 })
            .Where(b => b.StoreCode == "1013" && b.BucketStartUtc >= bucketStart && b.BucketStartUtc < bucketEnd)
            .ExecuteCommand();
        _h.Time.Now = FaceMatchTestDatabase.Now.AddMinutes(10);
        var result = await _projector.ProjectRecentAsync(default);

        Assert.Equal(new FaceTrafficHourlyProjection(false, 1, 0, 0, 1, 0), result);
        var updated = Row("1013", "2026-10-14", 11);
        Assert.Equal((26, 2, 1, 23), (updated.CaptureCount, updated.LowQualityCount, updated.ApiFailureCount, updated.VisitCount));
        Assert.Equal(_h.Time.Now, DateTime.SpecifyKind(updated.ComputedAtUtc, DateTimeKind.Utc));
        Assert.Equal(untouched, Row("1013", "2026-10-14", 10).ComputedAtUtc);
    }

    [Fact]
    public async Task 改时区后窗口内的行被新时区覆盖_不再产生的行被删除_窗口外的历史行保持当时的时区()
    {
        Bucket("1013", "2026-10-05T01:00:00", capture: 7);    // 窗口外的历史：布里斯班 10-05 11:00
        Bucket("1013", "2026-10-13T13:00:00", capture: 5);    // B1：布里斯班 10-13 23:00 / 悉尼 10-14 00:00
        Bucket("1013", "2026-10-14T00:00:00", capture: 30);   // B3：布里斯班 10-14 10:00 / 悉尼 10-14 11:00
        Bucket("1013", "2026-10-14T01:00:00", capture: 20);   // B2：布里斯班 10-14 11:00 / 悉尼 10-14 12:00
        await _projector.RebuildAsync("1013", new DateTime(2026, 10, 5), new DateTime(2026, 10, 5), default);   // 历史行以布里斯班落库
        await _projector.ProjectRecentAsync(default);
        Assert.Equal(["2026-10-05 11", "2026-10-13 23", "2026-10-14 10", "2026-10-14 11"], Keys("1013"));
        var historyComputedAt = Row("1013", "2026-10-05", 11).ComputedAtUtc;

        // 门店改时区：布里斯班 → 悉尼。
        _h.Db.Updateable<Store>().SetColumns(s => s.TimeZoneId == Sydney).Where(s => s.StoreCode == "1013").ExecuteCommand();
        _h.Time.Now = FaceMatchTestDatabase.Now.AddMinutes(10);
        var result = await _projector.ProjectRecentAsync(default);

        // 悉尼口径：B1 → (10-14, 0)，B3 → (10-14, 11)，B2 → (10-14, 12)。
        //   (10-14,0) 与 (10-14,12) 新增；(10-14,11) 原是布里斯班的 B2，现在被悉尼的 B3 覆盖（更新）；(10-13,23)、(10-14,10) 不再产生，被删除。
        Assert.Equal(new FaceTrafficHourlyProjection(false, 1, 0, 2, 1, 2), result);
        Assert.Equal(["2026-10-05 11", "2026-10-14 00", "2026-10-14 11", "2026-10-14 12"], Keys("1013"));
        Assert.Equal(30, Row("1013", "2026-10-14", 11).CaptureCount);
        Assert.All(Rows("1013").Where(r => r.StatDate >= new DateTime(2026, 10, 12)), r => Assert.Equal(Sydney, r.TimeZoneId));
        var history = Row("1013", "2026-10-05", 11);
        Assert.Equal(Brisbane, history.TimeZoneId);              // 窗口外保持当时的时区
        Assert.Equal(historyComputedAt, history.ComputedAtUtc);   // 也没被改写
        Assert.Equal(7, history.CaptureCount);
    }

    [Fact]
    public async Task VisitCount不为负_员工与低质量与失败加起来超过抓拍数时夹到0()
    {
        Bucket("1013", "2026-10-14T01:00:00", capture: 2, staff: 3, low: 1, failure: 1);
        Bucket("1013", "2026-10-14T02:00:00", capture: 0, staff: 0, low: 0, failure: 0);

        await _projector.ProjectRecentAsync(default);

        Assert.Equal(0, Row("1013", "2026-10-14", 11).VisitCount);
        Assert.Equal(0, Row("1013", "2026-10-14", 12).VisitCount);
        Assert.All(Rows(null), r => Assert.True(r.VisitCount >= 0));
    }

    // ---------- 手工回填 / 历史补齐 / 只读查询 ----------

    [Fact]
    public async Task 手工回填_只重算指定区间_区间内多余的行被删_区间外不动_可重复执行()
    {
        Bucket("1013", "2026-10-05T01:00:00", capture: 7);   // 布里斯班 10-05 11:00
        Bucket("1013", "2026-10-06T02:00:00", capture: 8);   // 10-06 12:00
        Bucket("1013", "2026-10-07T03:00:00", capture: 9);   // 10-07 13:00：不在回填区间
        Bucket("2001", "2026-10-05T01:00:00", capture: 6);   // 悉尼 10-05 12:00
        Seed("1013", "2026-10-05", 3, Brisbane, capture: 1, computedAt: FaceMatchTestDatabase.Now.AddDays(-9));   // 区间内、没有桶支撑的残留行
        Seed("1013", "2026-10-09", 3, Brisbane, capture: 2, computedAt: FaceMatchTestDatabase.Now.AddDays(-5));   // 区间外

        var first = await _projector.RebuildAsync("1013", new DateTime(2026, 10, 5), new DateTime(2026, 10, 6), default);
        var again = await _projector.RebuildAsync("1013", new DateTime(2026, 10, 5), new DateTime(2026, 10, 6), default);

        Assert.Equal(new FaceTrafficHourlyProjection(false, 1, 0, 2, 0, 1), first);
        Assert.Equal(0, again.RowsChanged);
        Assert.Equal(["2026-10-05 11", "2026-10-06 12", "2026-10-09 03"], Keys("1013"));
        Assert.Empty(Keys("2001"));   // 只回填了指定门店

        // 不指定门店：所有有计数桶的门店；每店按自己的时区。
        var all = await _projector.RebuildAsync(null, new DateTime(2026, 10, 5), new DateTime(2026, 10, 7), default);
        Assert.Equal(["2026-10-05 12"], Keys("2001"));
        Assert.Equal(["2026-10-05 11", "2026-10-06 12", "2026-10-07 13", "2026-10-09 03"], Keys("1013"));
        Assert.Equal(2, all.StoresProcessed);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _projector.RebuildAsync("1013", new DateTime(2026, 10, 6), new DateTime(2026, 10, 5), default));
    }

    [Fact]
    public async Task 历史补齐_最早的计数桶早于最早的分时行时补出缺失日期_已有行的历史日期保持原样()
    {
        Bucket("1013", "2026-10-05T01:00:00", capture: 7);    // 布里斯班 10-05 11:00
        Bucket("1013", "2026-10-06T02:00:00", capture: 8);    // 10-06 12:00
        Bucket("1013", "2026-10-14T01:00:00", capture: 20);   // 10-14 11:00
        // 门店 2001：已有 10-06 的历史行（当时按别的口径写的，内容与桶不一致），补齐不能改写它。
        Bucket("2001", "2026-10-05T01:00:00", capture: 4);    // 悉尼 10-05 12:00
        Bucket("2001", "2026-10-06T01:00:00", capture: 5);    // 悉尼 10-06 12:00
        Seed("2001", "2026-10-06", 12, Sydney, capture: 55, computedAt: FaceMatchTestDatabase.Now.AddDays(-8));

        var result = await _projector.BackfillMissingHistoryAsync(default);

        // 1013 一行分时行都没有：补到门店本地今天；2001 最早分时行是 10-06：只补 10-05。
        Assert.Equal(["2026-10-05 11", "2026-10-06 12", "2026-10-14 11"], Keys("1013"));
        Assert.Equal(["2026-10-05 12", "2026-10-06 12"], Keys("2001"));
        Assert.Equal(55, Row("2001", "2026-10-06", 12).CaptureCount);
        Assert.Equal(4, result.RowsInserted);

        var again = await _projector.BackfillMissingHistoryAsync(default);
        Assert.Equal(0, again.RowsChanged);
    }

    [Fact]
    public async Task 只读查询_按本地日期区间与门店过滤_按日期门店小时排序()
    {
        Bucket("2001", "2026-10-13T13:00:00", capture: 1);   // 悉尼 10-14 00:00
        Bucket("1013", "2026-10-14T01:00:00", capture: 2);   // 布里斯班 10-14 11:00
        Bucket("1013", "2026-10-13T13:00:00", capture: 3);   // 布里斯班 10-13 23:00
        await _projector.ProjectRecentAsync(default);

        var all = await _projector.QueryAsync(null, new DateTime(2026, 10, 13), new DateTime(2026, 10, 14), default);
        var oneDay = await _projector.QueryAsync(null, new DateTime(2026, 10, 14), new DateTime(2026, 10, 14), default);
        var store = await _projector.QueryAsync("1013", new DateTime(2026, 10, 13), new DateTime(2026, 10, 14), default);

        Assert.Equal(["2026-10-13 1013 23", "2026-10-14 1013 11", "2026-10-14 2001 0"], all.Select(r => $"{r.StatDate:yyyy-MM-dd} {r.StoreCode} {r.StatHour}"));
        Assert.Equal(2, oneDay.Count);
        Assert.Equal([3, 2], store.Select(r => r.CaptureCount));
        Assert.All(all, r => Assert.Equal(TimeSpan.Zero, r.StatDate.TimeOfDay));
    }

    // ---------- 后台任务⑧ ----------

    [Fact]
    public async Task 调度_分时投影每10分钟一次_不依赖腾讯云凭据也不调用腾讯云()
    {
        _h.Settings.Options.Tencent = new FaceMatchTencentOptions();   // 没有任何管理凭据
        _h.Library.Available = false;
        Bucket("1013", "2026-10-14T01:00:00", capture: 20, staff: 5);
        var service = CreateScheduler();

        await service.TickAsync(default);                       // 第一次出现：安排在「现在 + 10 分钟」之后
        Assert.Empty(Rows(null));

        _h.Time.Now = _h.Time.Now.AddMinutes(9);
        await service.TickAsync(default);
        Assert.Empty(Rows(null));

        _h.Time.Now = _h.Time.Now.AddMinutes(1);
        await service.TickAsync(default);                       // 到点
        Assert.Equal(["2026-10-14 11"], Keys("1013"));
        Assert.Equal(15, Row("1013", "2026-10-14", 11).VisitCount);
        Assert.Empty(_h.Library.Calls);
        Assert.Empty(_h.Publisher.Requests);
        Assert.DoesNotContain(_h.Logs.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task 调度_进程启动后第一次投影顺带补齐历史_之后不再重复补齐()
    {
        Bucket("1013", "2026-10-05T01:00:00", capture: 7);    // 窗口外的历史
        Bucket("1013", "2026-10-14T01:00:00", capture: 20);
        var service = CreateScheduler();
        await service.TickAsync(default);
        _h.Time.Now = _h.Time.Now.AddMinutes(10);
        await service.TickAsync(default);

        Assert.Equal(["2026-10-05 11", "2026-10-14 11"], Keys("1013"));

        // 补齐只做一次：人为删掉历史行，下一次到点的投影只重算最近 3 天，不会把它补回来。
        var historyDay = new DateTime(2026, 10, 5);
        var nextDay = historyDay.AddDays(1);
        _h.Db.Deleteable<FaceTrafficHourly>().Where(r => r.StatDate >= historyDay && r.StatDate < nextDay).ExecuteCommand();
        _h.Time.Now = _h.Time.Now.AddMinutes(10);
        await service.TickAsync(default);
        Assert.Equal(["2026-10-14 11"], Keys("1013"));
    }

    [Fact]
    public async Task 调度_迁移未执行时表不存在_不崩_只记一次Warning_表出现后恢复投影()
    {
        _h.Db.Ado.ExecuteCommand("DROP TABLE FaceTrafficHourly");
        Bucket("1013", "2026-10-14T01:00:00", capture: 20);
        var service = CreateScheduler();

        await service.TickAsync(default);
        for (var i = 0; i < 3; i++)
        {
            _h.Time.Now = _h.Time.Now.AddMinutes(10);
            await service.TickAsync(default);                   // 到点三次，表都不存在
        }

        var warnings = _h.Logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("FaceTrafficHourly")).ToList();
        Assert.Single(warnings);
        Assert.DoesNotContain(_h.Logs.Entries, e => e.Level >= LogLevel.Error);   // 任务没有失败
        Assert.True((await _projector.ProjectRecentAsync(default)).SchemaMissing);
        Assert.True((await _projector.BackfillMissingHistoryAsync(default)).SchemaMissing);
        Assert.True((await _projector.RebuildAsync(null, new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), default)).SchemaMissing);

        // 迁移执行后（表出现）下一个到点的任务自动恢复。
        _h.Db.CodeFirst.InitTables(typeof(FaceTrafficHourly));
        _h.Time.Now = _h.Time.Now.AddMinutes(10);
        await service.TickAsync(default);
        Assert.Equal(["2026-10-14 11"], Keys("1013"));
        Assert.Single(_h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("FaceTrafficHourly"));
    }

    [Fact]
    public async Task 调度_总开关关闭时空转_不读不写_打开后才投影()
    {
        Bucket("1013", "2026-10-14T01:00:00", capture: 20);
        var service = CreateScheduler();
        _h.Settings.Options.BackgroundEnabled = false;

        for (var i = 0; i < 5; i++)
        {
            await service.TickAsync(default);
            _h.Time.Now = _h.Time.Now.AddMinutes(10);
        }

        Assert.Empty(Rows(null));
        Assert.Empty(_h.Logs.Entries);

        _h.Settings.Options.BackgroundEnabled = true;
        await service.TickAsync(default);                       // 第一次出现，安排 10 分钟后
        _h.Time.Now = _h.Time.Now.AddMinutes(10);
        await service.TickAsync(default);
        Assert.Equal(["2026-10-14 11"], Keys("1013"));
    }

    [Fact]
    public async Task 调度_单个门店投影失败只影响自己_其余门店照常投影()
    {
        Bucket("1013", "2026-10-14T01:00:00", capture: 20);
        Bucket("2001", "2026-10-14T01:00:00", capture: 8);
        // 用 SQLite 触发器让 1013 的分时行写入失败（模拟唯一冲突 / 死锁之类的单店写库错误），2001 不受影响。
        _h.Db.Ado.ExecuteCommand("CREATE TRIGGER trg_block_1013 BEFORE INSERT ON FaceTrafficHourly WHEN NEW.StoreCode = '1013' BEGIN SELECT RAISE(ABORT, 'blocked'); END");

        var result = await _projector.ProjectRecentAsync(default);

        Assert.Equal(1, result.StoresFailed);
        Assert.Equal(1, result.StoresProcessed);
        Assert.Empty(Keys("1013"));
        Assert.Equal(["2026-10-14 12"], Keys("2001"));
        Assert.Contains(_h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("1013"));
    }

    // ---------- 辅助 ----------

    private FaceMatchBackgroundService CreateScheduler()
    {
        // 调度里每个任务用独立作用域：这里的作用域工厂总是返回同一批共享实例（同一个库、同一个假时间）。
        var services = new ServiceCollection();
        services.AddSingleton(_h.Notifier);
        services.AddSingleton(_h.Maintenance);
        services.AddSingleton(_projector);
        var provider = services.BuildServiceProvider();
        return new FaceMatchBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(), _h.Settings, _h.Time, _h.Logs.CreateLogger<FaceMatchBackgroundService>());
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

    private void Seed(string storeCode, string date, int hour, string timeZoneId, int capture, DateTime computedAt) =>
        _h.Db.Insertable(new FaceTrafficHourly
        {
            StoreCode = storeCode,
            StatDate = DateTime.Parse(date),
            StatHour = (byte)hour,
            CaptureCount = capture,
            VisitCount = capture,
            TimeZoneId = timeZoneId,
            ComputedAtUtc = computedAt,
        }).ExecuteCommand();

    // SQLite 把 date 列存成文本，单行 / 多行插入的文本格式不同（有无毫秒），SQL 排序不可靠，这里取出来后在内存里按规范化后的值排序。
    private List<FaceTrafficHourly> Rows(string? storeCode) =>
        _h.Db.Queryable<FaceTrafficHourly>()
            .WhereIF(storeCode != null, r => r.StoreCode == storeCode)
            .ToList()
            .OrderBy(r => r.StoreCode, StringComparer.Ordinal)
            .ThenBy(r => r.StatDate.Date)
            .ThenBy(r => r.StatHour)
            .ToList();

    private List<string> Keys(string storeCode) =>
        Rows(storeCode).Select(r => $"{r.StatDate:yyyy-MM-dd} {r.StatHour:00}").ToList();

    private FaceTrafficHourly Row(string storeCode, string date, int hour)
    {
        var day = DateTime.Parse(date);
        return Rows(storeCode).Single(r => r.StatDate == day && r.StatHour == hour);
    }

    /// <summary>全部分时行的文本快照，用来断言重算前后逐行逐列一致。</summary>
    private List<string> Snapshot() =>
        Rows(null).Select(r => string.Join('|', r.StoreCode, r.StatDate.ToString("yyyy-MM-dd"), r.StatHour, r.CaptureCount, r.StaffMatchCount,
            r.UniqueVisitorCount, r.RepeatCount, r.LowQualityCount, r.ApiFailureCount, r.VisitCount, r.TimeZoneId, r.ComputedAtUtc.Ticks)).ToList();
}
