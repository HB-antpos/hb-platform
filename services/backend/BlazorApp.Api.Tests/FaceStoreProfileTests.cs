using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 门店档案与启用闸门：缺项一律 409 并列出缺哪一项、地域与审查记录的关系、NSW 通知、默认全部关闭。
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
        Assert.All(profile.Gates.Facets, facet => Assert.False(facet.Requested));
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
    }

    // ---------- Capture ----------

    [Fact]
    public async Task Capture模式必填AllowedSubjects()
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.UpdateAsync(Qld, Req(mode: FaceStoreMode.Capture), FaceMatchTestHarness.Admin));

        AssertMissing(ex, "capture", "allowedSubjects");
        Assert.Empty(await _h.Db.Queryable<FaceStoreProfile>().ToListAsync());

        var saved = await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Capture, allowedSubjects: "仅内部测试员工"), FaceMatchTestHarness.Admin);
        Assert.Equal(FaceStoreMode.Capture, saved.Mode);
        Assert.Equal("仅内部测试员工", saved.AllowedSubjects);
    }

    [Fact]
    public async Task 评估匹配只允许在Capture模式下()
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.UpdateAsync(Qld, Req(evaluateMatch: true), FaceMatchTestHarness.Admin));

        AssertMissing(ex, "capture", "evaluateMatchRequiresCapture");
        var saved = await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Capture, allowedSubjects: "内部", evaluateMatch: true), FaceMatchTestHarness.Admin);
        Assert.True(saved.EvaluateMatch);
    }

    // ---------- Match ----------

    [Fact]
    public async Task Match模式材料齐全_QLD门店保存成功_审查有效()
    {
        var saved = await _h.Profiles.UpdateAsync(Qld, MatchRequest(), FaceMatchTestHarness.Admin);

        Assert.Equal(FaceStoreMode.Match, saved.Mode);
        Assert.True(saved.CrossBorderReviewValid);
        Assert.True(saved.Gates.CanSave);
        Assert.Equal("2026-09-01", saved.SignageInstalledOn);
        Assert.Equal("ap-guangzhou", saved.CrossBorderRegion);
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
    public async Task Match模式缺任何一项_409并列出缺哪一项_且不落库(string missingKey)
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

        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.UpdateAsync(Qld, request, FaceMatchTestHarness.Admin));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("FACE_MATCH_GATE_NOT_MET", ex.Code);
        AssertMissing(ex, "match", missingKey);
        Assert.Empty(await _h.Db.Queryable<FaceStoreProfile>().ToListAsync());
        // 被拒绝的保存也进审计。
        Assert.Contains(await _h.Db.Queryable<FaceAuditLog>().ToListAsync(), a => a.Result == "gate-not-met");
    }

    [Fact]
    public async Task 检查接口返回同样的报告_但不写任何数据()
    {
        var report = await _h.Profiles.CheckAsync(Qld, MatchRequest() with { PiaReference = null });

        Assert.False(report.CanSave);
        Assert.Contains("piaReference", report.Facets.Single(f => f.Facet == "match").Missing);
        Assert.Empty(await _h.Db.Queryable<FaceStoreProfile>().ToListAsync());
        Assert.Empty(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());
    }

    // ---------- NSW ----------

    [Fact]
    public async Task NSW门店Match模式需要员工通知日期()
    {
        var request = MatchRequest() with { StaffNoticeDate = null };

        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.UpdateAsync(Nsw, request, FaceMatchTestHarness.Admin));

        AssertMissing(ex, "match", "staffNoticeDate");
        // 同样的材料用在 QLD 门店不需要通知日期。
        await _h.Profiles.UpdateAsync(Qld, request, FaceMatchTestHarness.Admin);
    }

    [Theory]
    [InlineData("2026-09-30", true)]    // 恰好满 14 天（门店本地 2026-10-14）
    [InlineData("2026-09-01", true)]
    [InlineData("2026-10-01", false)]   // 13 天：不满
    [InlineData("2026-10-14", false)]
    public async Task NSW通知日期必须提前至少14天(string noticeDate, bool allowed)
    {
        var request = MatchRequest() with { StaffNoticeDate = noticeDate };

        if (allowed)
        {
            await _h.Profiles.UpdateAsync(Nsw, request, FaceMatchTestHarness.Admin);
            return;
        }

        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.UpdateAsync(Nsw, request, FaceMatchTestHarness.Admin));
        AssertMissing(ex, "match", "staffNoticeTooRecent");
    }

    // ---------- 跨境披露审查与地域 ----------

    [Fact]
    public async Task 审查记录的地域必须等于当前配置地域()
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Profiles.UpdateAsync(Qld, MatchRequest() with { CrossBorderRegion = "ap-shanghai" }, FaceMatchTestHarness.Admin));

        AssertMissing(ex, "match", "regionMismatch");
    }

    [Fact]
    public async Task 配置地域缺失_功能未开启_Match保存被拒绝()
    {
        _h.Settings.Options.Tencent.Region = null;

        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.UpdateAsync(Qld, MatchRequest(), FaceMatchTestHarness.Admin));

        AssertMissing(ex, "match", "regionMismatch");
        // 关闭模式不受影响：关掉功能永远可以。
        await _h.Profiles.UpdateAsync(Qld, Req(), FaceMatchTestHarness.Admin);
    }

    [Fact]
    public async Task 地域配置改动后_已保存的审查记录显示为无效()
    {
        await _h.Profiles.UpdateAsync(Qld, MatchRequest(), FaceMatchTestHarness.Admin);
        Assert.True((await _h.Profiles.GetAsync(Qld)).CrossBorderReviewValid);

        _h.Settings.Options.Tencent.Region = "ap-shanghai";

        var after = await _h.Profiles.GetAsync(Qld);
        Assert.False(after.CrossBorderReviewValid);
        Assert.Contains("regionMismatch", after.Gates.Facets.Single(f => f.Facet == "match").Missing);
        var list = await _h.Profiles.ListAsync();
        Assert.False(Assert.Single(list).CrossBorderReviewValid);
    }

    [Fact]
    public async Task 不在白名单的审查地域_400()
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Profiles.UpdateAsync(Qld, MatchRequest() with { CrossBorderRegion = "ap-beijing" }, FaceMatchTestHarness.Admin));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("FACE_MATCH_FIELD_INVALID", ex.Code);
    }

    // ---------- 员工排除 ----------

    [Fact]
    public async Task 员工排除需要门店成员里存在有效的同意与登记()
    {
        var request = Req(mode: FaceStoreMode.Capture, allowedSubjects: "内部", staff: true);

        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.UpdateAsync(Qld, request, FaceMatchTestHarness.Admin));
        AssertMissing(ex, "staffExclusion", "activeEnrollment");

        _h.Database.AddUser("u-1", "Alice", "s-1013");
        SeedEnrollment("u-1", withdrawnConsent: false);

        var saved = await _h.Profiles.UpdateAsync(Qld, request, FaceMatchTestHarness.Admin);
        Assert.True(saved.StaffExclusionEnabled);
    }

    [Fact]
    public async Task 同意已撤回的登记_或其他门店员工的登记_都不算数()
    {
        _h.Database.AddUser("u-1", "Alice", "s-1013");
        _h.Database.AddUser("u-2", "Bob", "s-2001");
        SeedEnrollment("u-1", withdrawnConsent: true);
        SeedEnrollment("u-2", withdrawnConsent: false);

        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Capture, allowedSubjects: "内部", staff: true), FaceMatchTestHarness.Admin));

        AssertMissing(ex, "staffExclusion", "activeEnrollment");
    }

    [Fact]
    public async Task 开关要求模式不是Off()
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Profiles.UpdateAsync(Qld, Req(dedup: true), FaceMatchTestHarness.Admin));

        AssertMissing(ex, "trafficDedup", "modeOff");
    }

    // ---------- 去重 ----------

    [Fact]
    public async Task 去重在Match模式下需要专项律师确认_在Capture下仅内部验证不要求()
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Profiles.UpdateAsync(Qld, MatchRequest() with { TrafficDedupEnabled = true }, FaceMatchTestHarness.Admin));
        var missing = AssertMissing(ex, "trafficDedup", "dedupLegalSignoffBy");
        Assert.Contains("dedupLegalSignoffAt", missing);

        var inCapture = await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Capture, allowedSubjects: "内部", dedup: true), FaceMatchTestHarness.Admin);
        Assert.True(inCapture.TrafficDedupEnabled);

        var full = await _h.Profiles.UpdateAsync(
            Qld,
            MatchRequest() with { TrafficDedupEnabled = true, DedupLegalSignoffBy = "Counsel", DedupLegalSignoffAt = _h.At(TimeSpan.FromDays(-1)) },
            FaceMatchTestHarness.Admin);
        Assert.True(full.TrafficDedupEnabled);
    }

    // ---------- 不受欢迎人员名单 ----------

    [Fact]
    public async Task 名单开关在Match下缺任何前置条件都拒绝_材料齐全才放行()
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Profiles.UpdateAsync(Qld, Req(mode: FaceStoreMode.Match, unwelcome: true), FaceMatchTestHarness.Admin));
        var missing = AssertMissing(ex, "unwelcome", "unwelcomeAssessmentRef");
        foreach (var key in new[]
                 {
                     "unwelcomeLegalSignoffBy", "unwelcomeLegalSignoffAt", "piaReference", "signageInstalledOn",
                     "staffTrainingDate", "privacyContact", "crossBorderRegion", "crossBorderReviewRef",
                 })
        {
            Assert.Contains(key, missing);
        }

        var saved = await _h.Profiles.UpdateAsync(Qld, UnwelcomeMatchRequest(), FaceMatchTestHarness.Admin);
        Assert.Equal(FaceStoreMode.Match, saved.Mode);
        Assert.True(saved.UnwelcomeListEnabled);
        Assert.Equal("Orion Privacy Officer", saved.PrivacyContact);
    }

    [Fact]
    public async Task 名单开关在Match下要求跨境披露审查与地域一致()
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Profiles.UpdateAsync(Qld, UnwelcomeMatchRequest() with { CrossBorderRegion = "ap-singapore" }, FaceMatchTestHarness.Admin));

        AssertMissing(ex, "unwelcome", "regionMismatch");
    }

    [Fact]
    public async Task 名单开关在Capture下只处理内部知情人员_不要求任何专项记录()
    {
        // 放宽规则：Capture 下的名单只用于上线前用内部员工照片做端到端测试，与顾客去重同一原则。
        var saved = await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Capture, allowedSubjects: "内部", unwelcome: true), FaceMatchTestHarness.Admin);

        Assert.Equal(FaceStoreMode.Capture, saved.Mode);
        Assert.True(saved.UnwelcomeListEnabled);
        Assert.True(saved.Gates.CanSave);
        Assert.Null(saved.UnwelcomeAssessmentRef);
        Assert.Null(saved.CrossBorderRegion);
    }

    [Fact]
    public async Task 名单开关在Capture下仍要求声明内部知情人员_Off下不能开()
    {
        var noSubjects = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Profiles.UpdateAsync(Qld, Req(mode: FaceStoreMode.Capture, unwelcome: true), FaceMatchTestHarness.Admin));
        AssertMissing(noSubjects, "capture", "allowedSubjects");

        var off = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Profiles.UpdateAsync(Qld, Req(mode: FaceStoreMode.Off, unwelcome: true), FaceMatchTestHarness.Admin));
        AssertMissing(off, "unwelcome", "modeOff");
    }

    [Fact]
    public async Task 名单在Capture下开启后切到Match_会重新要求全部专项记录()
    {
        await _h.Profiles.UpdateAsync(
            Qld, Req(mode: FaceStoreMode.Capture, allowedSubjects: "内部", unwelcome: true), FaceMatchTestHarness.Admin);

        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Profiles.UpdateAsync(Qld, Req(mode: FaceStoreMode.Match, unwelcome: true), FaceMatchTestHarness.Admin));

        var missing = AssertMissing(ex, "unwelcome", "unwelcomeAssessmentRef");
        Assert.Contains("unwelcomeLegalSignoffBy", missing);
        Assert.Contains("crossBorderRegion", missing);
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

    /// <summary>断言 409 闸门错误里指定项缺了指定键，返回该项完整缺项列表。</summary>
    private static IReadOnlyList<string> AssertMissing(FaceMatchException ex, string facet, string key)
    {
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("FACE_MATCH_GATE_NOT_MET", ex.Code);
        var report = Assert.IsType<FaceGateReportDto>(ex.Details);
        Assert.False(report.CanSave);
        var item = report.Facets.Single(f => f.Facet == facet);
        Assert.True(item.Requested);
        Assert.Contains(key, item.Missing);
        return item.Missing;
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
