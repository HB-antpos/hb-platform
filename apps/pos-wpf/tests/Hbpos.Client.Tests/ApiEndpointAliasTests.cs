using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class ApiEndpointAliasTests
{
    private const string PosEntry = "https://pos.hotbargain.vip/pos-api/";
    private const string DirectEntry = "https://hotbargain.vip/pos-api/";

    [Theory]
    [InlineData("https://pos.hotbargain.vip/pos-api/", true)]
    [InlineData("https://hotbargain.vip/pos-api/", true)]
    [InlineData("HTTPS://POS.HotBargain.VIP/pos-api/", true)]
    [InlineData("http://hotbargain.vip/pos-api/", false)]
    [InlineData("https://hotbargain.vip:8443/pos-api/", false)]
    [InlineData("https://hotbargain.vip/api/", false)]
    [InlineData("https://hotbargain.vip/", false)]
    [InlineData("https://evil-hotbargain.vip/pos-api/", false)]
    [InlineData("https://pos.hotbargain.vip.evil.com/pos-api/", false)]
    [InlineData("https://sub.pos.hotbargain.vip/pos-api/", false)]
    [InlineData("https://hotbargain.group/pos-api/", false)]
    public void Alias_group_only_contains_the_production_https_pos_api_entries(string address, bool expected)
    {
        // 别名组必须收得很紧：只有生产的 https 默认端口 + /pos-api/ 路径，否则会把不同环境当成同一台服务器而串数据。
        Assert.Equal(expected, ApiEndpointAliases.IsAlias(new Uri(address)));
    }

    [Fact]
    public void Canonicalize_maps_the_pos_entry_to_the_canonical_host_and_leaves_other_addresses_alone()
    {
        Assert.Equal(DirectEntry, ApiEndpointAliases.Canonicalize(new Uri(PosEntry)).AbsoluteUri);
        Assert.Equal(DirectEntry, ApiEndpointAliases.Canonicalize(new Uri(DirectEntry)).AbsoluteUri);
        Assert.Equal(
            "https://other.example.com/pos-api/",
            ApiEndpointAliases.Canonicalize(new Uri("https://other.example.com/pos-api/")).AbsoluteUri);
        Assert.Equal(
            "http://localhost:5159/",
            ApiEndpointAliases.Canonicalize(new Uri("http://localhost:5159/")).AbsoluteUri);
    }

    [Fact]
    public void Same_identity_treats_aliases_as_one_server_but_not_unrelated_servers()
    {
        Assert.True(ApiEndpointAliases.SameIdentity(new Uri(PosEntry), new Uri(DirectEntry)));
        Assert.True(ApiEndpointAliases.SameIdentity(new Uri(DirectEntry), new Uri(DirectEntry)));
        Assert.False(ApiEndpointAliases.SameIdentity(new Uri(PosEntry), new Uri("https://other.example.com/pos-api/")));
    }

    [Fact]
    public void Candidates_start_with_the_configured_entry_followed_by_the_other_alias()
    {
        Assert.Equal(
            new[] { PosEntry, DirectEntry },
            ApiEndpointAliases.GetCandidates(new Uri(PosEntry)).Select(uri => uri.AbsoluteUri));
        Assert.Equal(
            new[] { DirectEntry, PosEntry },
            ApiEndpointAliases.GetCandidates(new Uri(DirectEntry)).Select(uri => uri.AbsoluteUri));
        Assert.Single(ApiEndpointAliases.GetCandidates(new Uri("https://other.example.com/pos-api/")));
    }

    [Fact]
    public void Selector_never_switches_addresses_outside_the_alias_group()
    {
        var selector = new ApiTransportSelector(new FakeTimeProvider());
        var configured = new Uri("https://other.example.com/pos-api/");

        selector.ReportConnectFailure(configured, configured);

        Assert.Equal(configured, selector.Resolve(configured));
        Assert.Null(selector.GetFallback(configured, configured));
        Assert.False(selector.IsUsingFallback(configured));
    }

    [Fact]
    public void Selector_uses_the_fallback_after_a_primary_failure_and_doubles_the_cooldown_on_repeated_failures()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-22T00:00:00Z"));
        var selector = new ApiTransportSelector(time);
        var configured = new Uri(PosEntry);
        var primary = selector.Resolve(configured);
        Assert.Equal("pos.hotbargain.vip", primary.Host);

        selector.ReportConnectFailure(configured, primary);

        // 第一次冷却 2 分钟：期间所有请求直接走备用入口。
        Assert.Equal("hotbargain.vip", selector.Resolve(configured).Host);
        time.Advance(TimeSpan.FromSeconds(110));
        Assert.Equal("hotbargain.vip", selector.Resolve(configured).Host);
        time.Advance(TimeSpan.FromSeconds(20));
        var probe = selector.Resolve(configured);
        Assert.Equal("pos.hotbargain.vip", probe.Host);

        // 试探又失败：冷却翻倍到 4 分钟。
        selector.ReportConnectFailure(configured, probe);
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal("hotbargain.vip", selector.Resolve(configured).Host);
        time.Advance(TimeSpan.FromMinutes(1.1));
        Assert.Equal("pos.hotbargain.vip", selector.Resolve(configured).Host);
    }

    [Fact]
    public void Selector_resets_the_cooldown_after_the_primary_entry_succeeds_again()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-22T00:00:00Z"));
        var selector = new ApiTransportSelector(time);
        var configured = new Uri(PosEntry);
        var primary = selector.Resolve(configured);

        selector.ReportConnectFailure(configured, primary);
        time.Advance(TimeSpan.FromMinutes(2.1));
        selector.ReportConnectFailure(configured, selector.Resolve(configured));
        time.Advance(TimeSpan.FromMinutes(4.1));
        var recovered = selector.Resolve(configured);
        selector.ReportSuccess(configured, recovered);

        // 成功后清零：再次失败时又只冷却 2 分钟，而不是继续翻倍。
        selector.ReportConnectFailure(configured, selector.Resolve(configured));
        time.Advance(TimeSpan.FromMinutes(2.1));

        Assert.Equal("pos.hotbargain.vip", selector.Resolve(configured).Host);
        Assert.False(selector.IsUsingFallback(configured));
    }

    [Fact]
    public void A_failure_on_the_fallback_entry_does_not_change_the_active_entry()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-22T00:00:00Z"));
        var selector = new ApiTransportSelector(time);
        var configured = new Uri(PosEntry);
        selector.ReportConnectFailure(configured, selector.Resolve(configured));
        var fallback = selector.Resolve(configured);

        selector.ReportConnectFailure(configured, fallback);

        Assert.Equal("hotbargain.vip", selector.Resolve(configured).Host);
        Assert.True(selector.IsUsingFallback(configured));
    }

    [Fact]
    public void Aliased_entries_share_the_legacy_local_database_for_existing_and_new_installs()
    {
        var root = CreateTempDirectory();
        try
        {
            // 老设备：本地库历史上绑定在 hotbargain.vip 上。
            var existing = new ApiEndpointDatabasePartitionResolver(root, DirectEntry);
            var legacyPath = Path.Combine(root, "hbpos_client.db");
            Assert.Equal(legacyPath, existing.GetDatabasePath(DirectEntry));

            // 升级后默认入口换成 pos 域名：必须仍落在同一份本地库上，否则门店会丢掉激活状态并重新下载全部数据。
            var upgraded = new ApiEndpointDatabasePartitionResolver(root, PosEntry);
            Assert.Equal(legacyPath, upgraded.GetDatabasePath(PosEntry));
            Assert.Equal(legacyPath, upgraded.GetDatabasePath(DirectEntry));

            // 真正不同的服务器仍然使用独立分区。
            Assert.NotEqual(legacyPath, upgraded.GetDatabasePath("https://other.example.com/pos-api/"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_fresh_install_on_the_pos_entry_binds_the_canonical_database()
    {
        var root = CreateTempDirectory();
        try
        {
            var fresh = new ApiEndpointDatabasePartitionResolver(root, PosEntry);
            var legacyPath = Path.Combine(root, "hbpos_client.db");

            Assert.Equal(legacyPath, fresh.GetDatabasePath(PosEntry));
            Assert.Equal(legacyPath, fresh.GetDatabasePath(DirectEntry));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hbpos-alias-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
