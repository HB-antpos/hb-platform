using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 门店档案与启用闸门。2026-10-13 用户决定「Match 全部闸门改成警告」：
/// 结构性 / 功能性前提（Capture 缺 allowedSubjects、评估匹配非 Capture、开关在 Off 下、员工排除缺登记）仍然 409 阻塞并列出缺哪一项；
/// Match 的合规记录（PIA、告示、律师确认、跨境审查、NSW 员工通知、去重专项、名单专项）缺了只进 Warnings，照样保存成功，
/// 审计 Result 记 "mode=N;warnings=M"。另外覆盖地域与审查记录的关系、NSW 通知、列表警告数、默认全部关闭。
/// 「今天」固定为 2026-10-14 02:00 UTC（悉尼 / 布里斯班本地均为 10-14），用例不依赖机器时区。
/// </summary>
public sealed class FaceStoreProfileTests : IDisposable
{
    private const string Qld = FaceMatchTestDatabase.QldStore;
    private const string Nsw = FaceMatchTestDatabase.NswStore;

    private readonly FaceMatchTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ---------- 默认关闭 ----------

    [Fact]
    public async Task 没有档案的门店_读到的是全部关闭的默认值_闸门无请求项()
    {
        var profile = await _h.Profiles.GetAsync(Qld);

        Assert.False(profile.Exists);
        Assert.Equal(FaceStoreMode.Off, profile.Mode);
        Assert.False(profile.EvaluateMatch || profile.StaffExclusionEnabled || profile.TrafficDedupEnabled || profile.UnwelcomeListEnabled);
        Assert.True(profile.Gates.CanSave);
        Assert.False(profile.Gates.HasWarnings);
        Assert.All(profile.Gates.Facets, facet => { Assert.False(facet.Requested); Assert.Empty(facet.Missing); Assert.Empty(facet.Warnings); });
        Assert.Equal("QLD", profile.Jurisdiction);
        Assert.Equal("Australia/Brisbane", profile.EffectiveTimeZoneId);
        Assert.False(profile.CrossBorderReviewValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad code!")]
    [InlineData("../x")]
    public async Task 门店编码非法_400(string code)
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.GetAsync(code));
        Assert.Equal(400, ex.StatusCode);
    }

    [Theory]
    [InlineData("0000")]
    [InlineData(FaceMatchTestDatabase.DeletedStore)]
    public async Task 门店不存在或已删除_404(string code)
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.GetAsync(code));
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("FACE_MATCH_STORE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task 保存关闭模式_写入一行_再次保存是更新而不是新增_并写审计()
    {
        await _h.Profiles.UpdateAsync(Qld, Req(), FaceMatchTestHarness.Admin);
        await _h.Profiles.UpdateAsync(Qld, Req(mode: FaceStoreMode.Off, allowedSubjects: "x"), FaceMatchTestHarness.Admin);

        var rows = await _h.Db.Queryable<FaceStoreProfile>().ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(Qld, row.StoreCode);
        Assert.Equal("admin.tester", row.UpdatedBy);
        Assert.Equal(FaceMatchTestDatabase.Now, row.UpdatedAtUtc);
        var audits = await _h.Db.Queryable<FaceAuditLog>().Where(a => a.Action == FaceMatchAuditActions.ProfileUpdate).ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, a => { Assert.Equal(Qld, a.StoreCode); Assert.Equal("ok", a.Result.StartsWith("mode=") ? "ok" : a.Result); });
        // 没有警告时审计结果保持原样，不带 warnings。
        Assert.All(audits, a => Assert.Equal("mode=0", a.Result));
    }

    // ---------- Capture ----------

    [Fact]
    public async Task Capture模式缺AllowedSubjects只给警告_仍可保存_声明后警告消失()
    {
        var withoutSubjects = await _h.Profiles.UpdateAsync(Qld, Req(mode: FaceStoreMode.Capture), FaceMatchTestHarness.Admin);

        Assert.Equal(FaceStoreMode.Capture, withoutSubjects.Mode);
        AssertWarned(withoutSubjects, "capture", "allowedSubjects");

        var saved = await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Capture, allowedSubjects: "仅内部测试员工"), FaceMatchTestHarness.Admin);
        Assert.Equal(FaceStoreMode.Capture, saved.Mode);
        Assert.Equal("仅内部测试员工", saved.AllowedSubjects);
        Assert.Empty(saved.Gates.Facets.Single(f => f.Facet == "capture").Warnings);
    }

    [Fact]
    public async Task 评估匹配在非Capture模式下只给警告_Capture下没有警告()
    {
        var off = await _h.Profiles.UpdateAsync(Qld, Req(evaluateMatch: true), FaceMatchTestHarness.Admin);
        Assert.True(off.EvaluateMatch);
        AssertWarned(off, "capture", "evaluateMatchRequiresCapture");

        var saved = await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Capture, allowedSubjects: "内部", evaluateMatch: true), FaceMatchTestHarness.Admin);
        Assert.True(saved.EvaluateMatch);
        Assert.Empty(saved.Gates.Facets.Single(f => f.Facet == "capture").Warnings);
    }

    [Fact]
    public async Task 评估匹配在Match模式下可以保存_只给警告_提示它在Match下不会执行()
    {
        var saved = await _h.Profiles.UpdateAsync(Qld, MatchRequest() with { EvaluateMatch = true }, FaceMatchTestHarness.Admin);

        Assert.Equal(FaceStoreMode.Match, saved.Mode);
        Assert.True(saved.EvaluateMatch);
        AssertWarned(saved, "capture", "evaluateMatchRequiresCapture");
        Assert.Single(await _h.Db.Queryable<FaceStoreProfile>().ToListAsync());
    }

    [Fact]
    public async Task Capture模式下去重和名单不产生任何合规警告_只处理内部知情人员()
    {
        var saved = await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Capture, allowedSubjects: "内部", dedup: true, unwelcome: true), FaceMatchTestHarness.Admin);

        Assert.True(saved.Gates.CanSave);
        Assert.False(saved.Gates.HasWarnings);
        Assert.All(saved.Gates.Facets, facet => Assert.Empty(facet.Warnings));
        var audit = Assert.Single(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());
        Assert.Equal("mode=1", audit.Result);
    }

    // ---------- Match：合规记录缺口是警告，不再阻塞 ----------

    [Fact]
    public async Task Match模式材料齐全_QLD门店保存成功_审查有效_没有警告()
    {
        var saved = await _h.Profiles.UpdateAsync(Qld, MatchRequest(), FaceMatchTestHarness.Admin);

        Assert.Equal(FaceStoreMode.Match, saved.Mode);
        Assert.True(saved.CrossBorderReviewValid);
        Assert.True(saved.Gates.CanSave);
        Assert.False(saved.Gates.HasWarnings);
        Assert.Empty(saved.Gates.Facets.Single(f => f.Facet == "match").Warnings);
        Assert.Equal("2026-09-01", saved.SignageInstalledOn);
        Assert.Equal("ap-guangzhou", saved.CrossBorderRegion);
        Assert.Equal("mode=2", Assert.Single(await _h.Db.Queryable<FaceAuditLog>().ToListAsync()).Result);
    }

    [Theory]
    [InlineData("piaReference")]
    [InlineData("signageInstalledOn")]
    [InlineData("legalSignoffBy")]
    [InlineData("legalSignoffAt")]
    [InlineData("crossBorderRegion")]
    [InlineData("crossBorderReviewRef")]
    [InlineData("crossBorderReviewedBy")]
    [InlineData("crossBorderReviewedAt")]
    public async Task Match模式缺任何一项合规记录_照样保存成功_报告里是警告而不是阻塞_审计带警告数(string missingKey)
    {
        var request = missingKey switch
        {
            "piaReference" => MatchRequest() with { PiaReference = null },
            "signageInstalledOn" => MatchRequest() with { SignageInstalledOn = null },
            "legalSignoffBy" => MatchRequest() with { LegalSignoffBy = "  " },
            "legalSignoffAt" => MatchRequest() with { LegalSignoffAt = null },
            "crossBorderRegion" => MatchRequest() with { CrossBorderRegion = null },
            "crossBorderReviewRef" => MatchRequest() with { CrossBorderReviewRef = null },
            "crossBorderReviewedBy" => MatchRequest() with { CrossBorderReviewedBy = null },
            _ => MatchRequest() with { CrossBorderReviewedAt = null },
        };

        var saved = await _h.Profiles.UpdateAsync(Qld, request, FaceMatchTestHarness.Admin);

        Assert.Equal(FaceStoreMode.Match, saved.Mode);
        Assert.True(saved.Gates.CanSave);
        Assert.True(saved.Gates.HasWarnings);
        var match = saved.Gates.Facets.Single(f => f.Facet == "match");
        Assert.True(match.Requested);
        Assert.Empty(match.Missing);
        Assert.Equal([missingKey], match.Warnings);
        // 落库的就是用户填的（缺的保持为空），没有任何默认值或伪造值。
        var row = await _h.Db.Queryable<FaceStoreProfile>().SingleAsync();
        Assert.Equal(FaceStoreMode.Match, row.Mode);
        Assert.Equal(missingKey == "piaReference", row.PiaReference == null);
        Assert.Equal(missingKey == "crossBorderRegion", row.CrossBorderRegion == null);
        // 审计留痕：带着缺口启用。
        Assert.Equal("mode=2;warnings=1", Assert.Single(await _h.Db.Queryable<FaceAuditLog>().ToListAsync()).Result);
        // 重新读取得到同样的警告。
        Assert.Equal([missingKey], (await _h.Profiles.GetAsync(Qld)).Gates.Facets.Single(f => f.Facet == "match").Warnings);
    }

    [Fact]
    public async Task Match模式什么记录都没填_照样保存成功_全部列为警告_审计警告数等于各facet警告之和()
    {
        var saved = await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Match, dedup: true, unwelcome: true), FaceMatchTestHarness.Admin);

        Assert.True(saved.Gates.CanSave);
        Assert.True(saved.Gates.HasWarnings);
        Assert.All(saved.Gates.Facets, facet => Assert.Empty(facet.Missing));
        var total = saved.Gates.Facets.Sum(f => f.Warnings.Count);
        // QLD 门店：match 8 + 去重 2 + 名单 11（同一字段在不同 facet 分别计数）。
        Assert.Equal(8, saved.Gates.Facets.Single(f => f.Facet == "match").Warnings.Count);
        Assert.Equal(2, saved.Gates.Facets.Single(f => f.Facet == "trafficDedup").Warnings.Count);
        Assert.Equal(11, saved.Gates.Facets.Single(f => f.Facet == "unwelcome").Warnings.Count);
        var audit = Assert.Single(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());
        Assert.Equal($"mode=2;warnings={total}", audit.Result);
        Assert.True(audit.Result.Length <= 32);
    }

    [Fact]
    public async Task 审计Result最坏情况_NSW门店全部缺失_仍不超过32个字符()
    {
        var saved = await _h.Profiles.UpdateAsync(
            Nsw, Req(mode: FaceStoreMode.Match, dedup: true, unwelcome: true), FaceMatchTestHarness.Admin);

        var total = saved.Gates.Facets.Sum(f => f.Warnings.Count);
        Assert.Equal(22, total);
        var audit = Assert.Single(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());
        Assert.Equal("mode=2;warnings=22", audit.Result);
        Assert.True(audit.Result.Length <= 32);
    }

    [Fact]
    public async Task 检查接口返回同样的报告_警告不阻塞_且不写任何数据()
    {
        var report = await _h.Profiles.CheckAsync(Qld, MatchRequest() with { PiaReference = null });

        Assert.True(report.CanSave);
        Assert.True(report.HasWarnings);
        var match = report.Facets.Single(f => f.Facet == "match");
        Assert.Contains("piaReference", match.Warnings);
        Assert.Empty(match.Missing);
        Assert.Empty(await _h.Db.Queryable<FaceStoreProfile>().ToListAsync());
        Assert.Empty(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());
    }

    [Fact]
    public async Task 检查接口对原来阻塞的项也只报警告_CanSave恒为true()
    {
        var report = await _h.Profiles.CheckAsync(Qld, Req(mode: FaceStoreMode.Capture));

        Assert.True(report.CanSave);
        Assert.True(report.HasWarnings);
        var capture = report.Facets.Single(f => f.Facet == "capture");
        Assert.Empty(capture.Missing);
        Assert.Equal(["allowedSubjects"], capture.Warnings);
    }

    [Fact]
    public async Task 员工排除没有登记在Match下也只给警告_与合规记录缺口一并列出()
    {
        var saved = await _h.Profiles.UpdateAsync(Qld, Req(mode: FaceStoreMode.Match, staff: true), FaceMatchTestHarness.Admin);

        Assert.True(saved.Gates.CanSave);
        AssertWarned(saved, "staffExclusion", "activeEnrollment");
        Assert.NotEmpty(saved.Gates.Facets.Single(f => f.Facet == "match").Warnings);
        Assert.Single(await _h.Db.Queryable<FaceStoreProfile>().ToListAsync());
    }

    [Fact]
    public async Task NSW门店Match模式缺员工通知日期_只是警告_QLD门店同样的材料没有这条警告()
    {
        var request = MatchRequest() with { StaffNoticeDate = null };

        var nsw = await _h.Profiles.UpdateAsync(Nsw, request, FaceMatchTestHarness.Admin);
        var qld = await _h.Profiles.UpdateAsync(Qld, request, FaceMatchTestHarness.Admin);

        Assert.True(nsw.Gates.CanSave);
        Assert.Equal(["staffNoticeDate"], nsw.Gates.Facets.Single(f => f.Facet == "match").Warnings);
        Assert.False(qld.Gates.HasWarnings);
    }

    [Theory]
    [InlineData("2026-09-30", true)]    // 恰好满 14 天（门店本地 2026-10-14）
    [InlineData("2026-09-01", true)]
    [InlineData("2026-10-01", false)]   // 13 天：不满
    [InlineData("2026-10-14", false)]
    public async Task NSW通知日期不满14天是警告_满14天没有警告_都能保存(string noticeDate, bool enough)
    {
        var request = MatchRequest() with { StaffNoticeDate = noticeDate };

        var saved = await _h.Profiles.UpdateAsync(Nsw, request, FaceMatchTestHarness.Admin);

        Assert.True(saved.Gates.CanSave);
        var warnings = saved.Gates.Facets.Single(f => f.Facet == "match").Warnings;
        if (enough)
        {
            Assert.Empty(warnings);
            Assert.False(saved.Gates.HasWarnings);
        }
        else
        {
            Assert.Equal(["staffNoticeTooRecent"], warnings);
        }
    }

    // ---------- 跨境披露审查与地域 ----------

    [Fact]
    public async Task 审查记录的地域不等于当前配置地域_是警告_仍可保存()
    {
        var saved = await _h.Profiles.UpdateAsync(
            Qld, MatchRequest() with { CrossBorderRegion = "ap-shanghai" }, FaceMatchTestHarness.Admin);

        Assert.True(saved.Gates.CanSave);
        Assert.Equal(["regionMismatch"], saved.Gates.Facets.Single(f => f.Facet == "match").Warnings);
        Assert.False(saved.CrossBorderReviewValid);
        Assert.Equal("ap-shanghai", saved.CrossBorderRegion);
    }

    [Fact]
    public async Task 配置地域缺失_功能未开启_Match照样能保存_缺口显示为警告()
    {
        _h.Settings.Options.Tencent.Region = null;

        var saved = await _h.Profiles.UpdateAsync(Qld, MatchRequest(), FaceMatchTestHarness.Admin);

        Assert.Equal(FaceStoreMode.Match, saved.Mode);
        Assert.Equal(["regionMismatch"], saved.Gates.Facets.Single(f => f.Facet == "match").Warnings);
        // 关闭模式永远可以。
        await _h.Profiles.UpdateAsync(Qld, Req(), FaceMatchTestHarness.Admin);
    }

    [Fact]
    public async Task 地域配置改动后_已保存的审查记录显示为无效_闸门是警告_列表带警告数()
    {
        await _h.Profiles.UpdateAsync(Qld, MatchRequest(), FaceMatchTestHarness.Admin);
        Assert.True((await _h.Profiles.GetAsync(Qld)).CrossBorderReviewValid);
        Assert.Equal(0, Assert.Single(await _h.Profiles.ListAsync()).GateWarningCount);

        _h.Settings.Options.Tencent.Region = "ap-shanghai";

        var after = await _h.Profiles.GetAsync(Qld);
        Assert.False(after.CrossBorderReviewValid);
        var match = after.Gates.Facets.Single(f => f.Facet == "match");
        Assert.Contains("regionMismatch", match.Warnings);
        Assert.Empty(match.Missing);
        Assert.True(after.Gates.CanSave);
        var list = await _h.Profiles.ListAsync();
        var item = Assert.Single(list);
        Assert.False(item.CrossBorderReviewValid);
        Assert.Equal(1, item.GateWarningCount);
    }

    [Fact]
    public async Task 不在白名单的审查地域_400()
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Profiles.UpdateAsync(Qld, MatchRequest() with { CrossBorderRegion = "ap-beijing" }, FaceMatchTestHarness.Admin));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("FACE_MATCH_FIELD_INVALID", ex.Code);
    }

    // ---------- 列表：警告数 ----------

    [Fact]
    public async Task 列表的GateWarningCount_Match缺记录按警告数计_NSW多一条_Capture和齐全的为0_已删除门店不崩()
    {
        // 1013 QLD：Match，什么记录都没填 = match 8 条；2001 NSW：同样 + 员工通知日期 = 9 条。
        await _h.Profiles.UpdateAsync(Qld, Req(mode: FaceStoreMode.Match), FaceMatchTestHarness.Admin);
        await _h.Profiles.UpdateAsync(Nsw, Req(mode: FaceStoreMode.Match), FaceMatchTestHarness.Admin);
        // 已删除门店也有档案行（门店后来被停用）：列表不能因为查不到门店而崩，按悉尼时区、非 NSW 计。
        _h.Db.Insertable(new FaceStoreProfile
        {
            StoreCode = FaceMatchTestDatabase.DeletedStore,
            Mode = FaceStoreMode.Match,
            UpdatedAtUtc = FaceMatchTestDatabase.Now,
        }).ExecuteCommand();

        var list = (await _h.Profiles.ListAsync()).ToDictionary(i => i.StoreCode);

        Assert.Equal(8, list[Qld].GateWarningCount);
        Assert.Equal(9, list[Nsw].GateWarningCount);
        Assert.Equal(8, list[FaceMatchTestDatabase.DeletedStore].GateWarningCount);
        Assert.Null(list[FaceMatchTestDatabase.DeletedStore].StoreName);

        // 补齐后归零；Capture 本来就不要求这些记录。
        await _h.Profiles.UpdateAsync(Qld, MatchRequest(), FaceMatchTestHarness.Admin);
        await _h.Profiles.UpdateAsync(Nsw, Req(mode: FaceStoreMode.Capture, allowedSubjects: "内部", dedup: true, unwelcome: true), FaceMatchTestHarness.Admin);
        var after = (await _h.Profiles.ListAsync()).ToDictionary(i => i.StoreCode);
        Assert.Equal(0, after[Qld].GateWarningCount);
        Assert.Equal(0, after[Nsw].GateWarningCount);
    }

    [Fact]
    public async Task 闸门报告序列化后的字段名是前端依赖的_warnings_hasWarnings_gateWarningCount()
    {
        await _h.Profiles.UpdateAsync(Qld, Req(mode: FaceStoreMode.Match), FaceMatchTestHarness.Admin);

        var profile = await _h.Profiles.GetAsync(Qld);
        var list = await _h.Profiles.ListAsync();

        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var profileJson = System.Text.Json.JsonSerializer.Serialize(profile.Gates, options);
        var listJson = System.Text.Json.JsonSerializer.Serialize(list, options);
        Assert.Contains("\"hasWarnings\":true", profileJson, StringComparison.Ordinal);
        Assert.Contains("\"canSave\":true", profileJson, StringComparison.Ordinal);
        Assert.Contains("\"warnings\":[\"piaReference\"", profileJson, StringComparison.Ordinal);
        Assert.Contains("\"missing\":[]", profileJson, StringComparison.Ordinal);
        Assert.Contains("\"gateWarningCount\":8", listJson, StringComparison.Ordinal);
    }

    // ---------- 员工排除 ----------

    [Fact]
    public async Task 员工排除没有有效的同意与登记时只给警告_登记后警告消失()
    {
        var request = Req(mode: FaceStoreMode.Capture, allowedSubjects: "内部", staff: true);

        var early = await _h.Profiles.UpdateAsync(Qld, request, FaceMatchTestHarness.Admin);
        Assert.True(early.StaffExclusionEnabled);
        AssertWarned(early, "staffExclusion", "activeEnrollment");

        _h.Database.AddUser("u-1", "Alice", "s-1013");
        SeedEnrollment("u-1", withdrawnConsent: false);

        var saved = await _h.Profiles.UpdateAsync(Qld, request, FaceMatchTestHarness.Admin);
        Assert.True(saved.StaffExclusionEnabled);
        Assert.Empty(saved.Gates.Facets.Single(f => f.Facet == "staffExclusion").Warnings);
    }

    [Fact]
    public async Task 同意已撤回的登记_或其他门店员工的登记_都不算数()
    {
        _h.Database.AddUser("u-1", "Alice", "s-1013");
        _h.Database.AddUser("u-2", "Bob", "s-2001");
        SeedEnrollment("u-1", withdrawnConsent: true);
        SeedEnrollment("u-2", withdrawnConsent: false);

        var saved = await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Capture, allowedSubjects: "内部", staff: true), FaceMatchTestHarness.Admin);

        AssertWarned(saved, "staffExclusion", "activeEnrollment");
    }

    [Fact]
    public async Task 员工排除在Match下有登记即可保存_员工facet本身没有警告_Match合规缺口另算()
    {
        _h.Database.AddUser("u-1", "Alice", "s-1013");
        SeedEnrollment("u-1", withdrawnConsent: false);

        var saved = await _h.Profiles.UpdateAsync(Qld, Req(mode: FaceStoreMode.Match, staff: true), FaceMatchTestHarness.Admin);

        Assert.True(saved.StaffExclusionEnabled);
        Assert.True(saved.Gates.CanSave);
        Assert.Empty(saved.Gates.Facets.Single(f => f.Facet == "staffExclusion").Warnings);
        Assert.NotEmpty(saved.Gates.Facets.Single(f => f.Facet == "match").Warnings);
    }

    [Theory]
    [InlineData("staff")]
    [InlineData("dedup")]
    [InlineData("unwelcome")]
    public async Task 三个开关在Off下只给警告_仍可保存_提示它们不会生效(string toggle)
    {
        var request = Req(staff: toggle == "staff", dedup: toggle == "dedup", unwelcome: toggle == "unwelcome");

        var saved = await _h.Profiles.UpdateAsync(Qld, request, FaceMatchTestHarness.Admin);

        Assert.Equal(FaceStoreMode.Off, saved.Mode);
        AssertWarned(saved, toggle switch { "staff" => "staffExclusion", "dedup" => "trafficDedup", _ => "unwelcome" }, "modeOff");
        Assert.Single(await _h.Db.Queryable<FaceStoreProfile>().ToListAsync());
    }

    [Fact]
    public async Task 开关要求模式不是Off_否则只给警告()
    {
        var saved = await _h.Profiles.UpdateAsync(Qld, Req(dedup: true), FaceMatchTestHarness.Admin);

        AssertWarned(saved, "trafficDedup", "modeOff");
    }

    // ---------- 去重 ----------

    [Fact]
    public async Task 去重在Match模式下缺专项律师确认是警告_在Capture下仅内部验证既不要求也不警告()
    {
        var withGaps = await _h.Profiles.UpdateAsync(
            Qld, MatchRequest() with { TrafficDedupEnabled = true }, FaceMatchTestHarness.Admin);
        Assert.True(withGaps.TrafficDedupEnabled);
        Assert.True(withGaps.Gates.CanSave);
        var dedup = withGaps.Gates.Facets.Single(f => f.Facet == "trafficDedup");
        Assert.Empty(dedup.Missing);
        Assert.Equal(["dedupLegalSignoffBy", "dedupLegalSignoffAt"], dedup.Warnings);
        // match 本身材料齐全，所以总警告数就是去重这 2 条。
        Assert.Equal("mode=2;warnings=2", (await _h.Db.Queryable<FaceAuditLog>().OrderByDescending(a => a.AuditId).FirstAsync()).Result);

        var inCapture = await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Capture, allowedSubjects: "内部", dedup: true), FaceMatchTestHarness.Admin);
        Assert.True(inCapture.TrafficDedupEnabled);
        Assert.False(inCapture.Gates.HasWarnings);

        var full = await _h.Profiles.UpdateAsync(
            Qld,
            MatchRequest() with { TrafficDedupEnabled = true, DedupLegalSignoffBy = "Counsel", DedupLegalSignoffAt = _h.At(TimeSpan.FromDays(-1)) },
            FaceMatchTestHarness.Admin);
        Assert.True(full.TrafficDedupEnabled);
        Assert.False(full.Gates.HasWarnings);
    }

    // ---------- 不受欢迎人员名单 ----------

    [Fact]
    public async Task 名单开关在Match下缺专项记录是警告_照样保存_材料齐全后警告消失()
    {
        var saved = await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Match, unwelcome: true), FaceMatchTestHarness.Admin);

        Assert.Equal(FaceStoreMode.Match, saved.Mode);
        Assert.True(saved.UnwelcomeListEnabled);
        Assert.True(saved.Gates.CanSave);
        var unwelcome = saved.Gates.Facets.Single(f => f.Facet == "unwelcome");
        Assert.Empty(unwelcome.Missing);
        foreach (var key in new[]
                 {
                     "unwelcomeAssessmentRef", "unwelcomeLegalSignoffBy", "unwelcomeLegalSignoffAt", "piaReference", "signageInstalledOn",
                     "staffTrainingDate", "privacyContact", "crossBorderRegion", "crossBorderReviewRef", "crossBorderReviewedBy", "crossBorderReviewedAt",
                 })
        {
            Assert.Contains(key, unwelcome.Warnings);
        }

        var full = await _h.Profiles.UpdateAsync(Qld, UnwelcomeMatchRequest(), FaceMatchTestHarness.Admin);
        Assert.Equal(FaceStoreMode.Match, full.Mode);
        Assert.True(full.UnwelcomeListEnabled);
        Assert.False(full.Gates.HasWarnings);
        Assert.Equal("Orion Privacy Officer", full.PrivacyContact);
    }

    [Fact]
    public async Task 名单开关在Match下审查地域与当前配置不一致_是警告_仍可保存()
    {
        var saved = await _h.Profiles.UpdateAsync(
            Qld, UnwelcomeMatchRequest() with { CrossBorderRegion = "ap-singapore" }, FaceMatchTestHarness.Admin);

        Assert.True(saved.Gates.CanSave);
        Assert.Equal(["regionMismatch"], saved.Gates.Facets.Single(f => f.Facet == "unwelcome").Warnings);
        Assert.Equal(["regionMismatch"], saved.Gates.Facets.Single(f => f.Facet == "match").Warnings);
    }

    [Fact]
    public async Task 名单开关在Capture下只处理内部知情人员_不要求任何专项记录_也没有警告()
    {
        // 放宽规则（#761）：Capture 下的名单只用于上线前用内部员工照片做端到端测试，与顾客去重同一原则。
        var saved = await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Capture, allowedSubjects: "内部", unwelcome: true), FaceMatchTestHarness.Admin);

        Assert.Equal(FaceStoreMode.Capture, saved.Mode);
        Assert.True(saved.UnwelcomeListEnabled);
        Assert.True(saved.Gates.CanSave);
        Assert.False(saved.Gates.HasWarnings);
        Assert.Null(saved.UnwelcomeAssessmentRef);
        Assert.Null(saved.CrossBorderRegion);
    }

    [Fact]
    public async Task 名单开关在Capture下缺允许对象或在Off下_只给警告_仍可保存()
    {
        var noSubjects = await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Capture, unwelcome: true), FaceMatchTestHarness.Admin);
        AssertWarned(noSubjects, "capture", "allowedSubjects");

        var off = await _h.Profiles.UpdateAsync(Qld, Req(mode: FaceStoreMode.Off, unwelcome: true), FaceMatchTestHarness.Admin);
        AssertWarned(off, "unwelcome", "modeOff");
    }

    [Fact]
    public async Task 名单在Capture下开启后切到Match_能保存_专项记录重新计算为警告()
    {
        await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Capture, allowedSubjects: "内部", unwelcome: true), FaceMatchTestHarness.Admin);

        var saved = await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Match, unwelcome: true), FaceMatchTestHarness.Admin);

        Assert.Equal(FaceStoreMode.Match, saved.Mode);
        Assert.True(saved.Gates.CanSave);
        var warnings = saved.Gates.Facets.Single(f => f.Facet == "unwelcome").Warnings;
        Assert.Contains("unwelcomeAssessmentRef", warnings);
        Assert.Contains("unwelcomeLegalSignoffBy", warnings);
        Assert.Contains("crossBorderRegion", warnings);
    }

    // ---------- 字段校验 ----------

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(255)]
    public async Task 模式取值非法_400(int mode)
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.UpdateAsync(Qld, Req() with { Mode = mode }, FaceMatchTestHarness.Admin));
        Assert.Equal("FACE_MATCH_FIELD_INVALID", ex.Code);
    }

    [Fact]
    public async Task 缺少模式_400()
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.UpdateAsync(Qld, Req() with { Mode = null }, FaceMatchTestHarness.Admin));
        Assert.Equal(400, ex.StatusCode);
    }

    [Theory]
    [InlineData("pia", "2026-13-40")]
    [InlineData("pia", "14/10/2026")]
    [InlineData("signage", "2099-01-01")]
    public async Task 日期格式非法或晚于今天_400(string _, string date)
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Profiles.UpdateAsync(Qld, Req() with { SignageInstalledOn = date }, FaceMatchTestHarness.Admin));
        Assert.Equal("FACE_MATCH_FIELD_INVALID", ex.Code);
    }

    [Fact]
    public async Task 文本超长或含控制字符_时区不在白名单_确认时间在未来_都是400()
    {
        foreach (var request in new[]
                 {
                     Req() with { PiaReference = new string('x', 101) },
                     Req() with { PiaReference = "a\nb" },
                     Req() with { TimeZoneId = "America/New_York" },
                     Req() with { LegalSignoffAt = _h.At(TimeSpan.FromDays(10)) },
                 })
        {
            var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.UpdateAsync(Qld, request, FaceMatchTestHarness.Admin));
            Assert.Equal(400, ex.StatusCode);
        }
    }

    // ---------- 配置接口 ----------

    [Fact]
    public async Task 配置接口_开启时返回地域_关闭时给出原因且不返回地域()
    {
        var flags = new FaceMatchPermissionFlagsDto { Manage = true };

        var on = await _h.Profiles.GetConfigAsync(flags);
        Assert.True(on.Enabled);
        Assert.Equal("ap-guangzhou", on.Region);
        Assert.True(on.ObjectStoreConfigured);
        Assert.Equal(90m, on.HighBandScore);
        Assert.Equal(7, on.CaptureRetentionDays);
        Assert.Contains("ap-singapore", on.AllowedRegions);

        _h.Settings.Options.Tencent.AdminSecretKey = null;
        _h.Store.Configured = false;
        var off = await _h.Profiles.GetConfigAsync(flags);
        Assert.False(off.Enabled);
        Assert.Equal(FaceMatchDisabledReasons.CredentialsMissing, off.DisabledReason);
        Assert.Null(off.Region);
        Assert.False(off.ObjectStoreConfigured);
    }

    // ---------- 辅助 ----------

    // ---------- 名单命中自动确认阈值（用户 2026-10-13 决定：高相似度自动通过，阈值可设置） ----------

    [Fact]
    public async Task 自动确认阈值_默认90_请求省略保持库里原值_0是关闭_改动写审计()
    {
        // 没有档案行：生效值是默认 90。
        Assert.Equal(90m, (await _h.Profiles.GetAsync(Qld)).UnwelcomeAutoConfirmScore);

        // 第一次保存不带阈值：落库默认 90，审计不追加 auto。
        var first = await _h.Profiles.UpdateAsync(Qld, Req(), FaceMatchTestHarness.Admin);
        Assert.Equal(90m, first.UnwelcomeAutoConfirmScore);
        Assert.Equal(90m, (await _h.Db.Queryable<FaceStoreProfile>().SingleAsync()).UnwelcomeAutoConfirmScore);

        // 设置 80.5：DTO、库、审计都反映。
        var set = await _h.Profiles.UpdateAsync(Qld, Req() with { UnwelcomeAutoConfirmScore = 80.5m }, FaceMatchTestHarness.Admin);
        Assert.Equal(80.5m, set.UnwelcomeAutoConfirmScore);
        Assert.Equal(80.5m, (await _h.Db.Queryable<FaceStoreProfile>().SingleAsync()).UnwelcomeAutoConfirmScore);

        // 旧版 App 整体替换保存时不带这个字段：必须保持 80.5，不能被清成默认或 0。
        var omitted = await _h.Profiles.UpdateAsync(Qld, Req(allowedSubjects: "x"), FaceMatchTestHarness.Admin);
        Assert.Equal(80.5m, omitted.UnwelcomeAutoConfirmScore);
        Assert.Equal(80.5m, (await _h.Db.Queryable<FaceStoreProfile>().SingleAsync()).UnwelcomeAutoConfirmScore);
        Assert.Equal(80.5m, (await _h.Profiles.GetAsync(Qld)).UnwelcomeAutoConfirmScore);

        // 0 = 关闭；边界 50 与 100 合法。
        Assert.Equal(0m, (await _h.Profiles.UpdateAsync(Qld, Req() with { UnwelcomeAutoConfirmScore = 0m }, FaceMatchTestHarness.Admin)).UnwelcomeAutoConfirmScore);
        Assert.Equal(50m, (await _h.Profiles.UpdateAsync(Qld, Req() with { UnwelcomeAutoConfirmScore = 50m }, FaceMatchTestHarness.Admin)).UnwelcomeAutoConfirmScore);
        Assert.Equal(100m, (await _h.Profiles.UpdateAsync(Qld, Req() with { UnwelcomeAutoConfirmScore = 100m }, FaceMatchTestHarness.Admin)).UnwelcomeAutoConfirmScore);

        // 审计：只有改动的那几次才追加 ;auto=…，未改动（默认 90 → 默认 90、省略保持）不追加。
        var results = (await _h.Db.Queryable<FaceAuditLog>().Where(a => a.Action == FaceMatchAuditActions.ProfileUpdate).ToListAsync())
            .OrderBy(a => a.AuditId).Select(a => a.Result).ToList();
        Assert.Equal(["mode=0", "mode=0;auto=80.5", "mode=0", "mode=0;auto=0", "mode=0;auto=50", "mode=0;auto=100"], results);
    }

    [Theory]
    [InlineData("49.99")]
    [InlineData("1")]
    [InlineData("-5")]
    [InlineData("100.01")]
    [InlineData("101")]
    [InlineData("90.123")]
    public async Task 自动确认阈值_非0且低于50_超过100_或超过两位小数_400并指出字段(string raw)
    {
        var value = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        var request = Req() with { UnwelcomeAutoConfirmScore = value };

        var save = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.UpdateAsync(Qld, request, FaceMatchTestHarness.Admin));
        var check = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.CheckAsync(Qld, request));

        Assert.All(new[] { save, check }, ex =>
        {
            Assert.Equal(400, ex.StatusCode);
            Assert.Equal("FACE_MATCH_FIELD_INVALID", ex.Code);
            Assert.Contains("unwelcomeAutoConfirmScore", System.Text.Json.JsonSerializer.Serialize(ex.Details));
        });
        Assert.Empty(await _h.Db.Queryable<FaceStoreProfile>().ToListAsync());   // 非法值不写库
    }

    private static FaceStoreProfileUpdateRequest Req(
        int mode = FaceStoreMode.Off,
        string? allowedSubjects = null,
        bool evaluateMatch = false,
        bool staff = false,
        bool dedup = false,
        bool unwelcome = false) => new()
    {
        Mode = mode,
        AllowedSubjects = allowedSubjects,
        EvaluateMatch = evaluateMatch,
        StaffExclusionEnabled = staff,
        TrafficDedupEnabled = dedup,
        UnwelcomeListEnabled = unwelcome,
    };

    private FaceStoreProfileUpdateRequest MatchRequest() => Req(mode: FaceStoreMode.Match) with
    {
        PiaReference = "PIA-2026-001",
        SignageInstalledOn = "2026-09-01",
        StaffNoticeDate = "2026-09-01",
        LegalSignoffBy = "Counsel A",
        LegalSignoffAt = _h.At(TimeSpan.FromDays(-3)),
        CrossBorderRegion = "ap-guangzhou",
        CrossBorderReviewRef = "XB-2026-001",
        CrossBorderReviewedBy = "Counsel A",
        CrossBorderReviewedAt = _h.At(TimeSpan.FromDays(-2)),
    };

    private FaceStoreProfileUpdateRequest UnwelcomeMatchRequest() => MatchRequest() with
    {
        UnwelcomeListEnabled = true,
        UnwelcomeAssessmentRef = "UW-2026-001",
        UnwelcomeLegalSignoffBy = "Counsel B",
        UnwelcomeLegalSignoffAt = _h.At(TimeSpan.FromDays(-3)),
        StaffTrainingDate = "2026-09-10",
        PrivacyContact = "Orion Privacy Officer",
    };

    /// <summary>断言保存成功（CanSave）且指定项带有指定键的警告：所有闸门现在都只给提示，不拦截（用户 2026-10-13 决定）。</summary>
    private static void AssertWarned(FaceStoreProfileDto saved, string facet, string key)
    {
        Assert.True(saved.Gates.CanSave);
        Assert.True(saved.Gates.HasWarnings);
        var item = saved.Gates.Facets.Single(f => f.Facet == facet);
        Assert.True(item.Requested);
        Assert.Empty(item.Missing);
        Assert.Contains(key, item.Warnings);
    }

    private void SeedEnrollment(string userGuid, bool withdrawnConsent)
    {
        var consentId = Guid.NewGuid().ToString("N");
        _h.Db.Insertable(new FaceConsentRecord
        {
            ConsentId = consentId,
            UserGuid = userGuid,
            ConsentVersion = "v1",
            SignedAtUtc = FaceMatchTestDatabase.Now.AddDays(-30),
            Method = "paper",
            WithdrawnAtUtc = withdrawnConsent ? FaceMatchTestDatabase.Now.AddDays(-1) : null,
            CreatedAtUtc = FaceMatchTestDatabase.Now.AddDays(-30),
            CreatedBy = "seed",
        }).ExecuteCommand();
        _h.Db.Insertable(new FaceEnrollment
        {
            EnrollmentId = Guid.NewGuid().ToString("N"),
            UserGuid = userGuid,
            ConsentId = consentId,
            TencentPersonId = "st_seed",
            PhotoObjectKeysJson = "[]",
            Status = FaceEnrollmentStatus.Active,
            EnrolledAtUtc = FaceMatchTestDatabase.Now.AddDays(-30),
            EnrolledBy = "seed",
        }).ExecuteCommand();
    }
}
