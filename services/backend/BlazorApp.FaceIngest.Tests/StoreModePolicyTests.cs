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

    [Theory]
    [InlineData("staff")]
    [InlineData("dedup")]
    [InlineData("unwelcome")]
    public async Task Match缺跨境审查地域不等于当前配置_降级_有允许对象则Capture否则Off_并记Warning(string feature)
    {
        var wrongRegion = Profile(
            staff: feature == "staff",
            dedup: feature == "dedup",
            unwelcome: feature == "unwelcome",
            crossBorderRegion: "ap-singapore",
            allowedSubjects: "仅内部测试员工");
        var wrongRegionNoSubjects = wrongRegion with { StoreCode = "S2", AllowedSubjects = null };
        var wrongRegionBlankSubjects = wrongRegion with { StoreCode = "S3", AllowedSubjects = "   " };
        using var harness = await MatchHarness.CreateAsync([wrongRegion, wrongRegionNoSubjects, wrongRegionBlankSubjects]);

        var withSubjects = Resolve(harness);
        var withoutSubjects = Resolve(harness, "S2");
        var blankSubjects = Resolve(harness, "S3");

        Assert.Equal(StoreModeKind.Capture, withSubjects.Kind);
        Assert.False(withSubjects.Evaluate);
        Assert.False(withSubjects.StaffExclusion);
        Assert.Equal("downgraded-to-capture", withSubjects.Reason);
        Assert.Equal(StoreModeKind.Off, withoutSubjects.Kind);
        Assert.Equal(StoreModeKind.Off, blankSubjects.Kind);
        Assert.Contains(harness.Factory.LogEntries, log => log.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && log.Message.Contains("cross-border-gate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 地域配置改动后旧审查作废_同一份档案换地域即降级()
    {
        var profiles = new[] { Profile(staff: true, crossBorderRegion: "ap-guangzhou", allowedSubjects: null) };
        using var guangzhou = await MatchHarness.CreateAsync(profiles);
        using var shanghai = await MatchHarness.CreateAsync(profiles, configure: s => s["FaceMatch:Tencent:Region"] = "ap-shanghai");

        Assert.Equal(StoreModeKind.Match, Resolve(guangzhou).Kind);
        Assert.Equal(StoreModeKind.Off, Resolve(shanghai).Kind);
    }

    [Fact]
    public async Task 跨境审查记录缺失任一项同样降级()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, crossBorderReviewed: false)]);

        Assert.Equal(StoreModeKind.Off, Resolve(harness).Kind);
    }

    [Fact]
    public async Task Match但没开任何比对_不要求跨境审查_只数抓拍数()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(crossBorderRegion: null, crossBorderReviewed: false)]);

        var mode = Resolve(harness);

        Assert.Equal(StoreModeKind.Match, mode.Kind);
        Assert.False(mode.StaffExclusion || mode.TrafficDedup || mode.Unwelcome);
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

    [Theory]
    [InlineData(false, true, "unwelcome-gate")]
    [InlineData(true, false, "unwelcome-no-cos")]
    public async Task 名单是暗发布_专项记录不全或没有COS就不启用(bool recordComplete, bool cosAvailable, string expectedLogKey)
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, unwelcome: true, unwelcomeRecord: recordComplete)]);
        harness.Hits.Available = cosAvailable;

        var mode = Resolve(harness);

        Assert.Equal(StoreModeKind.Match, mode.Kind);
        Assert.True(mode.StaffExclusion);
        Assert.False(mode.Unwelcome);
        Assert.Contains(harness.Factory.LogEntries, log => log.Message.Contains(expectedLogKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task 名单记录齐全且COS可用时才启用()
    {
        using var harness = await MatchHarness.CreateAsync([Profile(staff: true, unwelcome: true)]);

        Assert.True(Resolve(harness).Unwelcome);
    }

    [Fact]
    public async Task 档案门店编码大小写不敏感()
    {
        using var harness = await MatchHarness.CreateAsync([Profile("s1001", mode: 1)]);

        Assert.Equal(StoreModeKind.Capture, Resolve(harness, "S1001").Kind);
    }
}
