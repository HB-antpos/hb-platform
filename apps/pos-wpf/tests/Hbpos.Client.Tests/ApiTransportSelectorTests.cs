using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>多入口（edge / aws / direct）选择器状态机：排序、各自冷却、写请求不探路、并发失败、首选变化清零。</summary>
public sealed class ApiTransportSelectorTests
{
    private const string Edge = "pos.hotbargain.vip";
    private const string Aws = "pos-syd.hotbargain.vip";
    private const string Direct = "hotbargain.vip";
    private static readonly Uri Configured = new("https://pos.hotbargain.vip/pos-api/");

    private static (ApiTransportSelector Selector, FakeTimeProvider Time) Create(string? preferred = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-24T00:00:00Z"));
        var selector = new ApiTransportSelector(time);
        selector.SetPreferredMode(preferred);
        return (selector, time);
    }

    private static Uri Entry(string host) => new UriBuilder(Configured) { Host = host }.Uri;

    [Theory]
    [InlineData(null, Edge)]
    [InlineData("edge", Edge)]
    [InlineData("aws", Aws)]
    [InlineData("direct", Direct)]
    public void Resolve_starts_on_the_preferred_entry(string? preferred, string expectedHost)
    {
        var (selector, _) = Create(preferred);

        Assert.Equal(expectedHost, selector.Resolve(Configured).Host);
        Assert.False(selector.IsUsingFallback(Configured));
    }

    [Theory]
    [InlineData("edge", new[] { Edge, Aws, Direct })]
    [InlineData("aws", new[] { Aws, Edge, Direct })]
    [InlineData("direct", new[] { Direct, Aws, Edge })]
    public void Entries_fail_over_one_by_one_in_the_fixed_order(string preferred, string[] order)
    {
        var (selector, _) = Create(preferred);

        Assert.Equal(order[0], selector.Resolve(Configured).Host);
        selector.ReportConnectFailure(Configured, Entry(order[0]));
        Assert.Equal(order[1], selector.Resolve(Configured).Host);
        selector.ReportConnectFailure(Configured, Entry(order[1]));
        Assert.Equal(order[2], selector.Resolve(Configured).Host);
    }

    [Theory]
    [InlineData("edge", Edge, Aws)]
    [InlineData("edge", Aws, Direct)]
    [InlineData("aws", Aws, Edge)]
    [InlineData("direct", Direct, Aws)]
    [InlineData("direct", Aws, Edge)]
    public void GetFallback_is_the_next_entry_in_the_order(string preferred, string used, string expected)
    {
        var (selector, _) = Create(preferred);

        Assert.Equal(expected, selector.GetFallback(Configured, Entry(used))!.Host);
    }

    [Fact]
    public void GetFallback_skips_entries_that_are_already_cooling_and_wraps_around_at_the_end()
    {
        var (selector, _) = Create("edge");
        selector.ReportConnectFailure(Configured, Entry(Aws));

        // aws 已知故障：edge 失败后直接换 direct，而不是去撞 aws。
        Assert.Equal(Direct, selector.GetFallback(Configured, Entry(Edge))!.Host);

        // 用的是最后一个入口时绕回开头（同样优先跳过冷却中的）。
        Assert.Equal(Edge, selector.GetFallback(Configured, Entry(Direct))!.Host);
    }

    [Fact]
    public void GetFallback_uses_the_immediate_next_entry_when_all_others_are_cooling()
    {
        var (selector, _) = Create("edge");
        selector.ReportConnectFailure(Configured, Entry(Aws));
        selector.ReportConnectFailure(Configured, Entry(Direct));

        Assert.Equal(Aws, selector.GetFallback(Configured, Entry(Edge))!.Host);
    }

    [Fact]
    public void Each_entry_keeps_its_own_cooldown_and_backoff()
    {
        var (selector, time) = Create("edge");
        selector.ReportConnectFailure(Configured, Entry(Edge)); // edge: 2 分钟
        time.Advance(TimeSpan.FromMinutes(2.1));
        selector.ReportConnectFailure(Configured, selector.Resolve(Configured)); // edge 试探失败：4 分钟
        selector.ReportConnectFailure(Configured, Entry(Aws)); // aws 第一次失败：2 分钟

        time.Advance(TimeSpan.FromMinutes(2.1));
        // aws 的 2 分钟已过，edge 的 4 分钟还没过：顺序中第一个不在冷却的是 aws。
        Assert.Equal(Aws, selector.Resolve(Configured).Host);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(Edge, selector.Resolve(Configured).Host);
    }

    [Fact]
    public void Backoff_climbs_through_the_steps_up_to_thirty_minutes()
    {
        var (selector, time) = Create("edge");
        var expectedMinutes = new[] { 2, 4, 8, 16, 30, 30 };
        foreach (var minutes in expectedMinutes)
        {
            selector.ReportConnectFailure(Configured, Entry(Edge));
            time.Advance(TimeSpan.FromMinutes(minutes - 0.5));
            Assert.True(selector.IsUsingFallback(Configured), $"{minutes} 分钟冷却未结束");
            time.Advance(TimeSpan.FromMinutes(0.6));
            Assert.False(selector.IsUsingFallback(Configured), $"{minutes} 分钟冷却应已结束");
        }
    }

    [Fact]
    public void All_entries_cooling_resolves_to_the_one_whose_cooldown_ends_first()
    {
        var (selector, time) = Create("edge");
        selector.ReportConnectFailure(Configured, Entry(Aws));   // 先失败：先结束
        time.Advance(TimeSpan.FromSeconds(10));
        selector.ReportConnectFailure(Configured, Entry(Edge));
        time.Advance(TimeSpan.FromSeconds(10));
        selector.ReportConnectFailure(Configured, Entry(Direct));

        Assert.Equal(Aws, selector.Resolve(Configured).Host);
        Assert.Equal(Aws, selector.Resolve(Configured, allowProbe: false).Host);
    }

    [Fact]
    public void All_entries_cooling_with_the_same_end_time_resolves_to_the_first_in_order()
    {
        var (selector, _) = Create("aws");
        foreach (var host in new[] { Aws, Edge, Direct })
        {
            selector.ReportConnectFailure(Configured, Entry(host));
        }

        Assert.Equal(Aws, selector.Resolve(Configured).Host);
    }

    [Fact]
    public void Writes_do_not_probe_an_entry_that_failed_until_a_read_confirms_it_recovered()
    {
        var (selector, time) = Create("edge");
        selector.ReportConnectFailure(Configured, Entry(Edge));
        time.Advance(TimeSpan.FromMinutes(3)); // edge 冷却已过，但还没被确认恢复。

        // 写请求继续走已确认可用的入口；读请求去试探 edge。
        Assert.Equal(Aws, selector.Resolve(Configured, allowProbe: false).Host);
        Assert.Equal(Edge, selector.Resolve(Configured, allowProbe: true).Host);

        // 读请求试探成功：清零，写请求也回到 edge。
        selector.ReportSuccess(Configured, Entry(Edge));
        Assert.Equal(Edge, selector.Resolve(Configured, allowProbe: false).Host);
    }

    [Fact]
    public void Writes_fall_back_to_a_probe_candidate_when_no_entry_is_clean()
    {
        var (selector, time) = Create("edge");
        foreach (var host in new[] { Edge, Aws, Direct })
        {
            selector.ReportConnectFailure(Configured, Entry(host));
        }

        time.Advance(TimeSpan.FromMinutes(3));

        // 没有「干净」入口时，写请求取顺序中第一个冷却已结束的（总比落在已知故障期内的入口强）。
        Assert.Equal(Edge, selector.Resolve(Configured, allowProbe: false).Host);
    }

    [Fact]
    public void Late_failures_from_concurrent_requests_on_one_entry_do_not_escalate_its_backoff()
    {
        var (selector, time) = Create("edge");

        for (var index = 0; index < 6; index++)
        {
            selector.ReportConnectFailure(Configured, Entry(Edge));
        }

        time.Advance(TimeSpan.FromMinutes(2.1));
        Assert.Equal(Edge, selector.Resolve(Configured).Host);
        selector.ReportConnectFailure(Configured, Entry(Edge));
        time.Advance(TimeSpan.FromMinutes(2));
        // 第二次真实失败才升级到 4 分钟（而不是被并发失败顶到 30 分钟）。
        Assert.True(selector.IsUsingFallback(Configured));
        time.Advance(TimeSpan.FromMinutes(2.1));
        Assert.False(selector.IsUsingFallback(Configured));
    }

    [Fact]
    public void Success_clears_only_that_entry()
    {
        var (selector, time) = Create("edge");
        selector.ReportConnectFailure(Configured, Entry(Edge));
        selector.ReportConnectFailure(Configured, Entry(Aws));
        time.Advance(TimeSpan.FromMinutes(2.1));

        selector.ReportSuccess(Configured, Entry(Aws));

        // aws 清零；edge 仍然「失败过、待确认」：写请求走 aws，读请求去试探 edge。
        Assert.Equal(Aws, selector.Resolve(Configured, allowProbe: false).Host);
        Assert.Equal(Edge, selector.Resolve(Configured).Host);

        // aws 再失败又只冷却 2 分钟（退避已清零）。
        selector.ReportConnectFailure(Configured, Entry(Aws));
        time.Advance(TimeSpan.FromMinutes(2.1));
        Assert.False(selector.IsUsingFallback(Configured));
    }

    [Fact]
    public void Changing_the_preferred_entry_clears_every_cooldown_and_backoff()
    {
        var (selector, _) = Create("edge");
        foreach (var host in new[] { Edge, Aws })
        {
            selector.ReportConnectFailure(Configured, Entry(host));
        }

        Assert.Equal(Direct, selector.Resolve(Configured).Host);

        Assert.True(selector.SetPreferredMode("aws"));

        Assert.Equal(Aws, selector.Resolve(Configured).Host);
        Assert.Equal(Aws, selector.Resolve(Configured, allowProbe: false).Host);
        Assert.False(selector.IsUsingFallback(Configured));
        Assert.Equal("aws", selector.PreferredMode);
    }

    [Fact]
    public void Re_applying_the_same_preferred_entry_keeps_cooldowns()
    {
        var (selector, _) = Create("aws");
        selector.ReportConnectFailure(Configured, Entry(Aws));

        // 每 60 秒重复下发同一个值，不能把正在冷却的坏线路放出来。
        selector.SetPreferredMode("aws");
        selector.SetPreferredMode("AWS");

        Assert.Equal(Edge, selector.Resolve(Configured).Host);
        Assert.True(selector.IsUsingFallback(Configured));
    }

    [Fact]
    public void Null_means_edge_and_equals_an_explicit_edge_without_resetting()
    {
        var (selector, _) = Create("edge");
        selector.ReportConnectFailure(Configured, Entry(Edge));

        Assert.True(selector.SetPreferredMode(null));

        Assert.Equal("edge", selector.PreferredMode);
        Assert.True(selector.IsUsingFallback(Configured));

        selector.SetPreferredMode("direct");
        selector.SetPreferredMode(null);
        Assert.Equal("edge", selector.PreferredMode);
        Assert.Equal(Edge, selector.Resolve(Configured).Host);
    }

    [Theory]
    [InlineData("satellite")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("pos.hotbargain.vip")]
    public void Unknown_preferred_values_are_ignored_and_keep_the_current_setting(string unknown)
    {
        var (selector, _) = Create("aws");
        selector.ReportConnectFailure(Configured, Entry(Aws));

        Assert.False(selector.SetPreferredMode(unknown));

        Assert.Equal("aws", selector.PreferredMode);
        Assert.True(selector.IsUsingFallback(Configured), "未知值不应清零冷却");
    }

    [Fact]
    public void Failures_reported_for_unrelated_hosts_are_ignored()
    {
        var (selector, _) = Create("edge");

        selector.ReportConnectFailure(Configured, new Uri("https://cdn.example.com/pos-api/"));
        selector.ReportSuccess(Configured, new Uri("https://cdn.example.com/pos-api/"));

        Assert.Equal(Edge, selector.Resolve(Configured).Host);
        Assert.False(selector.IsUsingFallback(Configured));
    }

    [Fact]
    public void State_is_shared_by_all_aliases_of_the_same_server()
    {
        var (selector, _) = Create("edge");
        var viaDirect = new Uri("https://hotbargain.vip/pos-api/");

        selector.ReportConnectFailure(Configured, Entry(Edge));

        // 不管 configured 写成哪个别名，看到的都是同一份入口状态。
        Assert.Equal(Aws, selector.Resolve(viaDirect).Host);
    }

    [Fact]
    public void Addresses_outside_the_alias_group_are_never_switched()
    {
        var (selector, _) = Create("aws");
        var other = new Uri("https://other.example.com/pos-api/");

        selector.ReportConnectFailure(other, other);

        Assert.Equal(other, selector.Resolve(other));
        Assert.Null(selector.GetFallback(other, other));
        Assert.False(selector.IsUsingFallback(other));
        Assert.Null(selector.GetStatus(other).ActiveMode);
    }

    [Fact]
    public void Status_reports_preferred_active_and_fallback_without_changing_state()
    {
        var (selector, time) = Create("aws");
        Assert.Equal(new ApiTransportStatus("aws", "aws", false), selector.GetStatus(Configured));

        selector.ReportConnectFailure(Configured, Entry(Aws));
        Assert.Equal(new ApiTransportStatus("aws", "edge", true), selector.GetStatus(Configured));

        // 冷却结束后的展示：读请求会去试探首选，所以实际入口回到 aws，不算回退中。
        time.Advance(TimeSpan.FromMinutes(2.1));
        Assert.Equal(new ApiTransportStatus("aws", "aws", false), selector.GetStatus(Configured));
    }

    [Fact]
    public async Task The_selector_is_safe_under_concurrent_use()
    {
        var (selector, _) = Create("edge");
        var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 2000; index++)
            {
                var transport = selector.Resolve(Configured, allowProbe: index % 2 == 0);
                if (index % 7 == 0)
                {
                    selector.ReportConnectFailure(Configured, transport);
                }
                else
                {
                    selector.ReportSuccess(Configured, transport);
                }

                selector.GetFallback(Configured, transport);
                if (worker == 0 && index % 100 == 0)
                {
                    selector.SetPreferredMode(index % 200 == 0 ? "aws" : "direct");
                }
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Contains(selector.Resolve(Configured).Host, new[] { Edge, Aws, Direct });
    }
}
