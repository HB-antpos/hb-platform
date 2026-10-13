using BlazorApp.FaceIngest.Tests.Infrastructure;
using Xunit;
using static BlazorApp.FaceIngest.Tests.Infrastructure.MatchSamples;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>门店档案 → 有效处理模式：读不到 / 缺档案按 Off、闸门降级、各种开关的折算。</summary>
public sealed class StoreModePolicyTests
{
    private static EffectiveStoreMode Resolve(MatchHarness harness, string store = "S1001")
        => harness.Factory.GetService<StoreModePolicy>().Resolve(store);

    [Fact]
    public async Task 档案读取从未成功_已配置功能时按Off()
    {
        using var harness = await MatchHarness.CreateAsync(profilesLoadFails: true);

        var mode = Resolve(harness);

        Assert.Equal(StoreModeKind.Off, mode.Kind);
        Assert.Equal("profiles-unavailable", mode.Reason);
        Assert.Null(harness.ProfileSource.Snapshot);
    }

    [Fact]
    public async Task 没有该店档案_已配置功能时按Off_其它店有档案不影响()
    {
        using var harness = await MatchHarness.CreateAsync([Profile("OTHER", mode: 1)]);

        Assert.Equal(StoreModeKind.Off, Resolve(harness).Kind);
        Assert.Equal("no-profile", Resolve(harness).Reason);
        Assert.Equal(StoreModeKind.Capture, Resolve(harness, "OTHER").Kind);
    }

    [Fact]
    public async Task 功能完全没配置_缺档案保持P1现状_不改变现有行为()
    {
        using var noProfiles = await MatchHarness.CreateAsync([], credentials: false);
        using var unreadable = await MatchHarness.CreateAsync(credentials: false, profilesLoadFails: true);

        Assert.Equal(EffectiveStoreMode.Legacy, Resolve(noProfiles));
        Assert.Equal(EffectiveStoreMode.Legacy, Resolve(unreadable));
        Assert.Equal(StoreModeKind.Capture, Resolve(noProfiles).Kind);
        Assert.False(Resolve(noProfiles).Evaluate);
    }

    [Fact]
    public async Task 没配数据库就没有档案机制_保持现状()
    {
        using var factory = new FaceIngestAppFactory();

        var mode = factory.GetService<StoreModePolicy>().Resolve("S1001");

        Assert.Equal(EffectiveStoreMode.Legacy, mode);
        Assert.False(factory.GetService<StoreModePolicy>().ShouldDeferProcessing);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 刷新失败保留旧快照_档案变化在下次刷新生效()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(mode: 1)]);
        Assert.Equal(StoreModeKind.Capture, Resolve(harness).Kind);

        harness.Profiles.Fail();
        Assert.False(await harness.ProfileSource.RefreshAsync(CancellationToken.None));
        Assert.Equal(StoreModeKind.Capture, Resolve(harness).Kind);
        Assert.Contains(harness.Factory.LogEntries, log => log.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && log.Message.Contains("keeping previous snapshot", StringComparison.Ordinal));

        harness.Profiles.Return(Profile(mode: 0));
        Assert.True(await harness.ProfileSource.RefreshAsync(CancellationToken.None));
        Assert.Equal(StoreModeKind.Off, Resolve(harness).Kind);
    }

    [Fact]
    public async Task 档案读取失败的Error日志被节流()
    {
        using var harness = await MatchHarness.CreateAsync(profilesLoadFails: true);
        for (var i = 0; i < 5; i++)
        {
            await harness.ProfileSource.RefreshAsync(CancellationToken.None);
        }

        Assert.Equal(1, harness.Factory.LogEntries.Count(log => log.Message.Contains("profile refresh failed", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task 启动瞬间尚未尝试读档案时处理器先等一轮_读失败过一次后不再等()
    {
        using var harness = await MatchHarness.CreateAsync(profilesLoadFails: true, loadProfiles: false);
        var policy = harness.Factory.GetService<StoreModePolicy>();
        await harness.PostAsync(MatchSamples.Capture(Face(1)));

        Assert.True(policy.ShouldDeferProcessing);
        Assert.Equal(new ProcessingSummary(0, 0, 0, 0), await harness.RunAsync());
        Assert.NotEmpty(harness.LeftoverFiles());

        await harness.ProfileSource.RefreshAsync(CancellationToken.None);
        Assert.False(policy.ShouldDeferProcessing);
    }

    [Fact]
    public async Task 档案Off和未知模式都按Off()
    {
        using var harness = await MatchHarness.CreateAsync([Profile("S0", mode: 0), Profile("S9", mode: 9)]);

        Assert.Equal("profile-off", Resolve(harness, "S0").Reason);
        Assert.Equal("unknown-mode", Resolve(harness, "S9").Reason);
    }

    [Fact]
    public async Task Capture加评估_需要匹配可用且至少开了员工或去重()
    {
        using var enabled = await MatchHarness.CreateAsync([Profile(mode: 1, evaluate: true, staff: true, dedup: true)]);
        using var noGroups = await MatchHarness.CreateAsync([Profile(mode: 1, evaluate: true)]);
        using var noCredentials = await MatchHarness.CreateAsync([Profile(mode: 1, evaluate: true, staff: true)], credentials: false);

        var mode = Resolve(enabled);
        Assert.Equal(StoreModeKind.Capture, mode.Kind);
        Assert.True(mode.Evaluate);
        Assert.True(mode.StaffExclusion);
        Assert.True(mode.TrafficDedup);
        Assert.False(Resolve(noGroups).Evaluate);
        Assert.False(Resolve(noCredentials).Evaluate);
        Assert.Equal(StoreModeKind.Capture, Resolve(noCredentials).Kind);
    }

    [Fact]
    public async Task Match全部条件满足时开关原样生效()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true)]);

        var mode = Resolve(harness);

        Assert.Equal(StoreModeKind.Match, mode.Kind);
        Assert.True(mode.StaffExclusion);
        Assert.True(mode.TrafficDedup);
        Assert.False(mode.Unwelcome);
        Assert.Equal("Australia/Brisbane", mode.TimeZoneId);
    }

    private const string LegalRecordsWarningKey = "match-without-legal-records";

    private static bool HasLegalRecordsWarning(MatchHarness harness)
        => harness.Factory.LogEntries.Any(log => log.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && log.Message.Contains(LegalRecordsWarningKey, StringComparison.Ordinal));

    // ---- 2026-10-13 用户决定：Match 合规闸门全部改为警告。缺记录不再降级、不再关闭任何功能，只按门店节流记 Warning ----

    [Theory]
    [InlineData("staff")]
    [InlineData("dedup")]
    [InlineData("unwelcome")]
    public async Task Match缺跨境审查或地域不等于当前配置_照常生效_不降级_只记Warning(string feature)
    {
        var wrongRegion = Profile(
            staff: feature == "staff",
            dedup: feature == "dedup",
            unwelcome: feature == "unwelcome",
            crossBorderRegion: "ap-singapore",
            allowedSubjects: "仅内部测试员工");
        // 有没有 AllowedSubjects 都一样：不再因为跨境审查而退回 Capture / Off。
        var wrongRegionNoSubjects = wrongRegion with { StoreCode = "S2", AllowedSubjects = null };
        var wrongRegionBlankSubjects = wrongRegion with { StoreCode = "S3", AllowedSubjects = "   " };
        using var harness = await MatchHarness.CreateAsync([wrongRegion, wrongRegionNoSubjects, wrongRegionBlankSubjects]);

        foreach (var store in new[] { "S1001", "S2", "S3" })
        {
            var mode = Resolve(harness, store);
            Assert.Equal(StoreModeKind.Match, mode.Kind);
            Assert.Equal("profile-match", mode.Reason);
            Assert.Equal(feature == "staff", mode.StaffExclusion);
            Assert.Equal(feature == "dedup", mode.TrafficDedup);
            Assert.Equal(feature == "unwelcome", mode.Unwelcome);
        }

        Assert.True(HasLegalRecordsWarning(harness));
    }

    [Fact]
    public async Task 地域配置改动后旧审查对不上_同一份档案照常Match_只是多一条Warning()
    {
        var profiles = new[] { Profile(staff: true, crossBorderRegion: "ap-guangzhou", allowedSubjects: null) };
        using var guangzhou = await MatchHarness.CreateAsync(profiles);
        using var shanghai = await MatchHarness.CreateAsync(profiles, configure: s => s["FaceMatch:Tencent:Region"] = "ap-shanghai");

        Assert.Equal(StoreModeKind.Match, Resolve(guangzhou).Kind);
        Assert.False(HasLegalRecordsWarning(guangzhou));
        var mode = Resolve(shanghai);
        Assert.Equal(StoreModeKind.Match, mode.Kind);
        Assert.True(mode.StaffExclusion);
        Assert.True(HasLegalRecordsWarning(shanghai));
    }

    [Fact]
    public async Task 跨境审查记录缺失任一项同样照常Match_只记Warning()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, crossBorderReviewed: false)]);

        var mode = Resolve(harness);

        Assert.Equal(StoreModeKind.Match, mode.Kind);
        Assert.True(mode.StaffExclusion);
        Assert.True(HasLegalRecordsWarning(harness));
    }

    [Fact]
    public async Task Match的合规记录齐全时不记缺记录Warning()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, dedup: true, unwelcome: true)]);

        var mode = Resolve(harness);

        Assert.Equal(StoreModeKind.Match, mode.Kind);
        Assert.True(mode.StaffExclusion && mode.TrafficDedup && mode.Unwelcome);
        Assert.False(HasLegalRecordsWarning(harness));
    }

    [Fact]
    public async Task 缺记录Warning按门店节流_同一店十分钟内只记一条_窗口过后再记_不同店各自记()
    {
        using var harness = await MatchHarness.CreateAsync(
            [Profile(staff: true, crossBorderReviewed: false), Profile("S2", staff: true, crossBorderReviewed: false)]);

        for (var i = 0; i < 5; i++)
        {
            Resolve(harness);
        }

        int Count(string store) => harness.Factory.LogEntries.Count(log => log.Message.Contains(LegalRecordsWarningKey, StringComparison.Ordinal)
            && log.Message.Contains("store=" + store, StringComparison.Ordinal));
        Assert.Equal(1, Count("S1001"));
        Resolve(harness, "S2");
        Assert.Equal(1, Count("S2"));

        harness.Factory.Time.Advance(TimeSpan.FromMinutes(11));
        Resolve(harness);
        Assert.Equal(2, Count("S1001"));
    }

    [Fact]
    public async Task 缺记录Warning的消息不含档案内容或个人信息()
    {
        using var harness = await MatchHarness.CreateAsync(
            [Profile(staff: true, unwelcome: true, crossBorderRegion: "ap-singapore", unwelcomeRecord: false)]);

        Resolve(harness);

        var log = Assert.Single(harness.Factory.LogEntries, l => l.Message.Contains(LegalRecordsWarningKey, StringComparison.Ordinal));
        Assert.Contains("Match mode is running without complete legal records", log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ap-singapore", log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("XB-REVIEW", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Match但没开任何比对_不涉及跨境审查_不记缺记录Warning_只数抓拍数()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(crossBorderRegion: null, crossBorderReviewed: false)]);

        var mode = Resolve(harness);

        Assert.Equal(StoreModeKind.Match, mode.Kind);
        Assert.False(mode.StaffExclusion || mode.TrafficDedup || mode.Unwelcome);
        Assert.False(HasLegalRecordsWarning(harness));
    }

    [Fact]
    public async Task Match档案但匹配被配置关闭_按Off而不是退回Capture()
    {
        using var harness = await MatchHarness.CreateAsync(
            [Profile(staff: true, allowedSubjects: "仅内部测试员工")],
            configure: s => s["FaceMatch:Tencent:Region"] = "ap-beijing");

        var mode = Resolve(harness);

        Assert.Equal(StoreModeKind.Off, mode.Kind);
        Assert.Equal("match-disabled-by-configuration", mode.Reason);
    }

    [Fact]
    public async Task 去重需要可解析的门店时区_否则关掉去重并记Warning()
    {
        using var harness = await MatchHarness.CreateAsync(
            [Profile(staff: true, dedup: true, timeZoneId: null), Profile("S2", staff: true, dedup: true, timeZoneId: "Mars/Olympus")]);

        foreach (var store in new[] { "S1001", "S2" })
        {
            var mode = Resolve(harness, store);
            Assert.Equal(StoreModeKind.Match, mode.Kind);
            Assert.True(mode.StaffExclusion);
            Assert.False(mode.TrafficDedup);
        }

        Assert.Contains(harness.Factory.LogEntries, log => log.Message.Contains("TimeZoneId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Match名单没有COS就不启用_这是功能前提_员工排除不受影响_并记Warning()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, unwelcome: true)]);
        harness.Hits.Available = false;

        var mode = Resolve(harness);

        Assert.Equal(StoreModeKind.Match, mode.Kind);
        Assert.True(mode.StaffExclusion);
        Assert.False(mode.Unwelcome);
        Assert.Contains(harness.Factory.LogEntries, log => log.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && log.Message.Contains("unwelcome-no-cos", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Match名单专项记录不全_名单照常启用_只记缺记录Warning()
    {
        // 2026-10-13 起专项记录缺失不再关掉名单（此前是 unwelcome-gate 兜底）。
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, unwelcome: true, unwelcomeRecord: false)]);

        var mode = Resolve(harness);

        Assert.Equal(StoreModeKind.Match, mode.Kind);
        Assert.True(mode.StaffExclusion);
        Assert.True(mode.Unwelcome);
        Assert.True(HasLegalRecordsWarning(harness));
        Assert.DoesNotContain(harness.Factory.LogEntries, log => log.Message.Contains("unwelcome-gate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Match名单专项记录不全且没有COS_名单关闭的原因只是COS_缺记录另记Warning()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(unwelcome: true, unwelcomeRecord: false)]);
        harness.Hits.Available = false;

        var mode = Resolve(harness);

        Assert.Equal(StoreModeKind.Match, mode.Kind);
        Assert.False(mode.Unwelcome);
        Assert.True(HasLegalRecordsWarning(harness));
        Assert.Contains(harness.Factory.LogEntries, log => log.Message.Contains("unwelcome-no-cos", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 名单记录齐全且COS可用时启用_且无缺记录Warning()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, unwelcome: true)]);

        Assert.True(Resolve(harness).Unwelcome);
        Assert.False(HasLegalRecordsWarning(harness));
    }

    // ---- 放宽规则：Capture 下名单只用于内部知情人员的测试，不要求专项记录与跨境审查 ----

    [Fact]
    public async Task Capture加名单_不要求专项记录与跨境审查_只要匹配可用且声明了内部知情人员()
    {
        using var harness = await MatchHarness.CreateAsync(
            [Profile(mode: 1, unwelcome: true, allowedSubjects: "仅内部测试员工", unwelcomeRecord: false, crossBorderRegion: null, crossBorderReviewed: false)]);

        var mode = Resolve(harness);

        Assert.Equal(StoreModeKind.Capture, mode.Kind);
        Assert.True(mode.Unwelcome);
        Assert.False(mode.Evaluate);
        Assert.False(mode.StaffExclusion || mode.TrafficDedup);
        Assert.Equal("profile-capture-unwelcome", mode.Reason);
    }

    [Fact]
    public async Task Capture加名单加评估_两件事互不影响()
    {
        using var harness = await MatchHarness.CreateAsync(
            [Profile(mode: 1, evaluate: true, staff: true, unwelcome: true, allowedSubjects: "仅内部测试员工", unwelcomeRecord: false)]);

        var mode = Resolve(harness);

        Assert.Equal(StoreModeKind.Capture, mode.Kind);
        Assert.True(mode.Evaluate);
        Assert.True(mode.StaffExclusion);
        Assert.True(mode.Unwelcome);
        Assert.Equal("profile-capture-evaluate-unwelcome", mode.Reason);
    }

    [Theory]
    [InlineData("no-subjects", "capture-unwelcome-no-allowed-subjects")]
    [InlineData("blank-subjects", "capture-unwelcome-no-allowed-subjects")]
    [InlineData("no-credentials", "capture-unwelcome-disabled")]
    [InlineData("no-cos", "unwelcome-no-cos")]
    public async Task Capture加名单缺允许对象_匹配被关闭_或没有COS_名单这一块关闭并记Warning(string scenario, string expectedLogKey)
    {
        var profile = Profile(
            mode: 1,
            unwelcome: true,
            allowedSubjects: scenario == "no-subjects" ? null : scenario == "blank-subjects" ? "   " : "仅内部测试员工");
        using var harness = await MatchHarness.CreateAsync([profile], credentials: scenario != "no-credentials");
        harness.Hits.Available = scenario != "no-cos";

        var mode = Resolve(harness);

        // 名单这一块关掉，但 Capture 的现有行为不变（照常留存）。
        Assert.Equal(StoreModeKind.Capture, mode.Kind);
        Assert.False(mode.Unwelcome);
        Assert.Contains(harness.Factory.LogEntries, log => log.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && log.Message.Contains(expectedLogKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Capture不涉及Match的合规记录警告_缺记录不记Warning()
    {
        using var harness = await MatchHarness.CreateAsync(
            [Profile(mode: 1, evaluate: true, staff: true, unwelcome: true, allowedSubjects: "仅内部测试员工", unwelcomeRecord: false, crossBorderRegion: null, crossBorderReviewed: false)]);

        Assert.Equal(StoreModeKind.Capture, Resolve(harness).Kind);
        Assert.False(HasLegalRecordsWarning(harness));
    }

    [Fact]
    public async Task 档案门店编码大小写不敏感()
    {
        using var harness = await MatchHarness.CreateAsync([Profile("s1001", mode: 1)]);

        Assert.Equal(StoreModeKind.Capture, Resolve(harness, "S1001").Kind);
    }
}
