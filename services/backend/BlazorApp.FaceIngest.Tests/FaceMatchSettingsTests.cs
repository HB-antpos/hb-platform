using System.Text.Json;
using BlazorApp.FaceIngest.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>
/// 比对配置：地域白名单、凭据只认 FaceMatch:Tencent:*、缺失 / 非法时整体关闭且<b>绝不发出任何网络请求</b>。
/// </summary>
public sealed class FaceMatchSettingsTests
{
    private static FaceMatchSettings Create(Action<Dictionary<string, string?>> configure)
    {
        var data = new Dictionary<string, string?>();
        configure(data);
        return FaceMatchSettings.Create(new ConfigurationBuilder().AddInMemoryCollection(data).Build());
    }

    private static void Valid(Dictionary<string, string?> data, string region = "ap-guangzhou")
    {
        data["FaceMatch:Tencent:Region"] = region;
        data["FaceMatch:Tencent:SecretId"] = MatchSamples.FakeSecretId;
        data["FaceMatch:Tencent:SecretKey"] = MatchSamples.FakeSecretKey;
    }

    [Theory]
    [InlineData("ap-guangzhou")]
    [InlineData("ap-shanghai")]
    [InlineData("ap-singapore")]
    public void 白名单内的地域可用(string region)
    {
        var settings = Create(data => Valid(data, region));

        Assert.True(settings.MatcherEnabled);
        Assert.Equal(region, settings.Region);
        Assert.Null(settings.DisabledReason);
        Assert.True(settings.CredentialsConfigured);
    }

    [Theory]
    [InlineData("ap-beijing")]
    [InlineData("AP-GUANGZHOU")]
    [InlineData("ap-guangzhou ")]
    [InlineData("ap-hongkong")]
    [InlineData("guangzhou")]
    public void 不在白名单的地域整体关闭_不回退任何别的地域(string region)
    {
        var settings = Create(data => Valid(data, region));

        // 带尾随空格的值会被 Trim 后命中白名单，属于合法写法；其余必须关闭。
        if (region.Trim() == "ap-guangzhou")
        {
            Assert.True(settings.MatcherEnabled);
            return;
        }

        Assert.False(settings.MatcherEnabled);
        Assert.Equal(string.Empty, settings.Region);
        Assert.Contains("FaceMatch:Tencent:Region is not in the allowed list", settings.DisabledReason, StringComparison.Ordinal);
        // 原因里不回显非法值本身（万一有人把密钥误填到地域里）。
        Assert.DoesNotContain(region.Trim(), settings.DisabledReason!.Replace("ap-guangzhou, ap-shanghai, ap-singapore", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void 地域缺失或为空整体关闭()
    {
        var missing = Create(data =>
        {
            data["FaceMatch:Tencent:SecretId"] = MatchSamples.FakeSecretId;
            data["FaceMatch:Tencent:SecretKey"] = MatchSamples.FakeSecretKey;
        });
        var blank = Create(data => Valid(data, "   "));

        Assert.False(missing.MatcherEnabled);
        Assert.Contains("Region is missing", missing.DisabledReason, StringComparison.Ordinal);
        Assert.False(blank.MatcherEnabled);
        Assert.Contains("Region is missing", blank.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void 出厂appsettings的默认地域是广州且默认没有凭据()
    {
        using var factory = new FaceIngestAppFactory();

        var settings = factory.GetService<FaceMatchSettings>();

        Assert.Equal("ap-guangzhou", settings.Region);
        Assert.False(settings.MatcherEnabled);
        Assert.False(settings.CredentialsConfigured);
        Assert.Contains("SecretId is missing", settings.DisabledReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SecretId")]
    [InlineData("SecretKey")]
    public void 缺任一凭据整体关闭_且仍视为功能已配置(string missing)
    {
        var settings = Create(data =>
        {
            Valid(data);
            data["FaceMatch:Tencent:" + missing] = "";
        });

        Assert.False(settings.MatcherEnabled);
        Assert.Contains(missing + " is missing", settings.DisabledReason, StringComparison.Ordinal);
        Assert.True(settings.CredentialsConfigured);
    }

    [Fact]
    public void 只有COS密钥时不得顶替_凭据视为未配置()
    {
        // 关键：COS 的 SecretId/SecretKey（FaceIngest:Cos:*）与主密钥（TENCENT_SECRET_*）一概不读。
        var settings = Create(data =>
        {
            data["FaceMatch:Tencent:Region"] = "ap-guangzhou";
            data["FaceIngest:Cos:Enabled"] = "true";
            data["FaceIngest:Cos:SecretId"] = "AKIDcosSecretIdShouldNeverBeUsed1";
            data["FaceIngest:Cos:SecretKey"] = "cosSecretKeyShouldNeverBeUsed-123456";
            data["TENCENT_SECRET_ID"] = "AKIDmasterSecretIdShouldNeverBeUsed";
            data["TENCENT_SECRET_KEY"] = "masterSecretKeyShouldNeverBeUsed-123";
        });

        Assert.False(settings.MatcherEnabled);
        Assert.False(settings.CredentialsConfigured);
    }

    [Fact]
    public async Task 缺凭据或地域非法时不发出任何网络请求_即使门店档案要求Match()
    {
        // 配了凭据但地域非法 -> 档案闸门生效：Match 门店按 Off 处理（丢弃），没有任何出站请求。
        using var badRegion = await MatchHarness.CreateAsync(
            [MatchSamples.Profile(staff: true, dedup: true)],
            realTencentClient: true,
            configure: settings => settings["FaceMatch:Tencent:Region"] = "ap-beijing");
        await badRegion.PostAsync(MatchSamples.Capture(MatchSamples.Face(1)));
        await badRegion.RunAsync();
        Assert.False(badRegion.Factory.GetService<FaceMatchSettings>().MatcherEnabled);
        Assert.Equal(0, badRegion.Http.RequestCount);
        Assert.Empty(badRegion.LeftoverFiles());

        // 完全没配凭据：功能未启用，处理器保持 P1 现状，同样没有出站请求。
        using var noCredentials = await MatchHarness.CreateAsync(
            [MatchSamples.Profile(staff: true, dedup: true)],
            credentials: false,
            realTencentClient: true);
        await noCredentials.PostAsync(MatchSamples.Capture(MatchSamples.Face(1)));
        await noCredentials.RunAsync();
        Assert.Equal(0, noCredentials.Http.RequestCount);
    }

    [Fact]
    public async Task 整体关闭时health报告matcherEnabled为false且不含密钥()
    {
        using var harness = await MatchHarness.CreateAsync(
            [],
            configure: settings => settings["FaceMatch:Tencent:Region"] = "ap-beijing");

        var json = await harness.Client.GetStringAsync("/health");

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("matcherEnabled").GetBoolean());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("matcherRegion").GetString());
        Assert.DoesNotContain(MatchSamples.FakeSecretKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(MatchSamples.FakeSecretId, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 可用时health报告地域名_仍不含密钥()
    {
        using var harness = await MatchHarness.CreateAsync([], configure: settings => settings["FaceMatch:Tencent:Region"] = "ap-shanghai");

        var json = await harness.Client.GetStringAsync("/health");

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("matcherEnabled").GetBoolean());
        Assert.Equal("ap-shanghai", document.RootElement.GetProperty("matcherRegion").GetString());
        Assert.DoesNotContain(MatchSamples.FakeSecretKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(MatchSamples.FakeSecretId, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("StaffThreshold", "0")]
    [InlineData("StaffThreshold", "101")]
    [InlineData("VisitorThreshold", "abc")]
    [InlineData("Unwelcome:Threshold", "-5")]
    [InlineData("StaffSuppressSeconds", "-1")]
    [InlineData("Retry:MaxAttempts", "0")]
    [InlineData("Quality:MinEdgePixels", "10")]
    public void 非法的数值配置整体关闭(string key, string value)
    {
        var settings = Create(data =>
        {
            Valid(data);
            data["FaceMatch:" + key] = value;
        });

        Assert.False(settings.MatcherEnabled);
        Assert.Contains("FaceMatch:" + key, settings.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void 名单阈值低于员工或访客阈值视为非法()
    {
        var settings = Create(data =>
        {
            Valid(data);
            data["FaceMatch:StaffThreshold"] = "90";
            data["FaceMatch:Unwelcome:Threshold"] = "80";
        });

        Assert.False(settings.MatcherEnabled);
        Assert.Contains("unwelcome threshold must not be lower", settings.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void 默认阈值偏严_名单最严_可按门店覆盖()
    {
        var settings = Create(data =>
        {
            Valid(data);
            data["FaceMatch:Stores:S2002:StaffThreshold"] = "90";
            data["FaceMatch:Stores:S2002:UnwelcomeThreshold"] = "98.5";
        });

        Assert.True(settings.MatcherEnabled, settings.DisabledReason);
        Assert.Equal(new StoreThresholds(85, 85, 95), settings.ThresholdsFor("S1001"));
        Assert.Equal(new StoreThresholds(90, 85, 98.5), settings.ThresholdsFor("s2002"));
        Assert.True(settings.UnwelcomeThreshold > settings.StaffThreshold);
    }

    [Fact]
    public void 门店覆盖里的名单阈值低于该店员工阈值同样非法()
    {
        var settings = Create(data =>
        {
            Valid(data);
            data["FaceMatch:Stores:S2002:StaffThreshold"] = "97";
        });

        Assert.False(settings.MatcherEnabled);
        Assert.Contains("FaceMatch:Stores:*", settings.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void 凭据格式非法整体关闭_原因不含凭据内容()
    {
        var settings = Create(data =>
        {
            Valid(data);
            data["FaceMatch:Tencent:SecretId"] = "bad id\r\nInjected: header";
        });

        Assert.False(settings.MatcherEnabled);
        Assert.Contains("SecretId has an invalid format", settings.DisabledReason, StringComparison.Ordinal);
        Assert.DoesNotContain("Injected", settings.DisabledReason, StringComparison.Ordinal);
    }
}
