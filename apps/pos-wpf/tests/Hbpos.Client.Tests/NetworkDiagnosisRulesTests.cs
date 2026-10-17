using Hbpos.Client.Wpf.Services.NetworkDiagnostics;

namespace Hbpos.Client.Tests;

public sealed class NetworkDiagnosisRulesTests
{
    private static NetworkMeasurements Healthy() => new()
    {
        AdapterName = "Ethernet",
        Gateway = "192.168.1.1",
        SystemDnsServers = ["192.168.1.1"],
        SystemDnsMs = 30,
        SystemDnsAddresses = ["203.0.113.20"],
        PublicDnsMs = 38,
        PublicDnsAddresses = ["203.0.113.20"],
        Ipv4ConnectMs = 46,
        HasIpv6Address = false,
        ProxyAutoDetectEnabled = true,
        DirectReplyMs = 310,
        SystemProxyReplyMs = 330,
        Reply = ReplyOutcome.Online,
        ClockSkew = TimeSpan.FromSeconds(2)
    };

    [Fact]
    public void Healthy_network_has_no_finding_and_every_checked_step_is_ok()
    {
        var m = Healthy();

        Assert.Equal(DiagnosisKind.None, NetworkDiagnosisRules.Evaluate(m).Kind);
        var steps = NetworkDiagnosisRules.BuildSteps(m);
        Assert.Equal(10, steps.Count);
        Assert.DoesNotContain(steps, step => step.Status is DiagnosticStepStatus.Failed or DiagnosticStepStatus.Slow);
        Assert.Equal(DiagnosticStepStatus.Skipped, steps.Single(step => step.Id == DiagnosticStepId.ConnectIpv6).Status);
    }

    [Fact]
    public void Slow_system_dns_with_fast_public_dns_suggests_the_public_dns_fix()
    {
        var m = Healthy() with { SystemDnsMs = 4200 };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(DiagnosisKind.DnsSlow, finding.Kind);
        Assert.Equal(NetworkFixKind.PublicDns, finding.SuggestedFix);
        var step = NetworkDiagnosisRules.BuildSteps(m).Single(item => item.Id == DiagnosticStepId.SystemDns);
        Assert.Equal(DiagnosticStepStatus.Slow, step.Status);
        Assert.Equal(4200, step.ElapsedMs);
    }

    [Fact]
    public void Slow_system_dns_is_not_blamed_when_public_dns_is_slow_too()
    {
        var m = Healthy() with { SystemDnsMs = 4200, PublicDnsMs = 3900 };

        Assert.NotEqual(DiagnosisKind.DnsSlow, NetworkDiagnosisRules.Evaluate(m).Kind);
    }

    [Fact]
    public void Failed_system_dns_while_public_dns_answers_is_a_dns_failure()
    {
        var m = Healthy() with { SystemDnsFailed = true, SystemDnsAddresses = [], SystemDnsMs = 6000 };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(DiagnosisKind.DnsFailed, finding.Kind);
        Assert.Equal(NetworkFixKind.PublicDns, finding.SuggestedFix);
    }

    [Fact]
    public void Different_dns_answers_are_flagged_only_when_the_server_cannot_be_reached()
    {
        var wrong = Healthy() with
        {
            SystemDnsAddresses = ["10.0.0.99"],
            Reply = ReplyOutcome.Unreachable
        };
        var workingDespiteDifference = Healthy() with { SystemDnsAddresses = ["10.0.0.99"] };

        Assert.Equal(DiagnosisKind.DnsMismatch, NetworkDiagnosisRules.Evaluate(wrong).Kind);
        Assert.Equal(NetworkFixKind.PublicDns, NetworkDiagnosisRules.Evaluate(wrong).SuggestedFix);
        // 系统 DNS 的答案和公共 DNS 不同但服务器能连上（例如就近解析）：不能误报。
        Assert.Equal(DiagnosisKind.None, NetworkDiagnosisRules.Evaluate(workingDespiteDifference).Kind);
    }

    [Fact]
    public void Automatic_proxy_detection_that_costs_seconds_suggests_turning_it_off()
    {
        var m = Healthy() with { DirectReplyMs = 300, SystemProxyReplyMs = 3200, ProxyAutoDetectEnabled = true };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(DiagnosisKind.ProxyAutoDetectSlow, finding.Kind);
        Assert.Equal(NetworkFixKind.DisableProxyAutoDetect, finding.SuggestedFix);
    }

    [Fact]
    public void Configured_proxy_that_does_not_work_is_reported_but_never_auto_fixed()
    {
        var m = Healthy() with
        {
            ProxyConfigured = true,
            ProxyAddress = "10.0.0.5:3128",
            SystemProxyReplyFailed = true,
            SystemProxyReplyMs = 8000
        };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(DiagnosisKind.ProxyUnreachable, finding.Kind);
        Assert.Null(finding.SuggestedFix);
    }

    [Fact]
    public void Slow_ipv6_with_fast_ipv4_is_reported_without_an_automatic_fix()
    {
        var m = Healthy() with { HasIpv6Address = true, Ipv6ConnectFailed = true, Ipv6ConnectMs = 6000 };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(DiagnosisKind.Ipv6Slow, finding.Kind);
        Assert.Null(finding.SuggestedFix);
    }

    [Fact]
    public void Clock_skew_is_reported_ahead_of_a_secure_connection_failure()
    {
        var m = Healthy() with
        {
            Reply = ReplyOutcome.SecureConnectionFailed,
            ClockSkew = TimeSpan.FromHours(-2)
        };

        Assert.Equal(DiagnosisKind.ClockSkew, NetworkDiagnosisRules.Evaluate(m).Kind);
        var step = NetworkDiagnosisRules.BuildSteps(m).Single(item => item.Id == DiagnosticStepId.Clock);
        Assert.Equal(DiagnosticStepStatus.Failed, step.Status);
        Assert.Contains("2 h", step.Detail);
    }

    [Theory]
    [InlineData(ReplyOutcome.SecureConnectionFailed, DiagnosisKind.SecureConnectionFailed)]
    [InlineData(ReplyOutcome.ServerError, DiagnosisKind.ServerError)]
    [InlineData(ReplyOutcome.NotPosApi, DiagnosisKind.NotPosApi)]
    [InlineData(ReplyOutcome.Timeout, DiagnosisKind.ServerUnreachable)]
    [InlineData(ReplyOutcome.Unreachable, DiagnosisKind.ServerUnreachable)]
    public void Reply_outcomes_map_to_a_finding_without_an_automatic_fix(ReplyOutcome outcome, DiagnosisKind expected)
    {
        var m = Healthy() with { Reply = outcome, HttpStatus = 502 };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(expected, finding.Kind);
        Assert.Null(finding.SuggestedFix);
    }

    [Fact]
    public void No_local_network_wins_over_everything_else()
    {
        var m = Healthy() with { HasLocalNetwork = false, SystemDnsFailed = true, Reply = ReplyOutcome.Unreachable };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(DiagnosisKind.NoLocalNetwork, finding.Kind);
        Assert.Null(finding.SuggestedFix);
        Assert.Equal(DiagnosticStepStatus.Failed, NetworkDiagnosisRules.BuildSteps(m)[0].Status);
    }

    [Fact]
    public void A_router_that_drops_packets_is_blamed_before_dns_because_it_explains_everything_being_slow()
    {
        var m = Healthy() with
        {
            GatewayPingSent = 6,
            GatewayPingLost = 1,
            GatewayAvgMs = 40,
            GatewayMaxMs = 900,
            SystemDnsMs = 4200
        };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(DiagnosisKind.LocalLinkUnstable, finding.Kind);
        Assert.Null(finding.SuggestedFix);
        var step = NetworkDiagnosisRules.BuildSteps(m).Single(item => item.Id == DiagnosticStepId.GatewayLink);
        Assert.Equal(DiagnosticStepStatus.Slow, step.Status);
        Assert.Contains("17% lost", step.Detail);
        Assert.Contains("max 900 ms", step.Detail);
    }

    [Fact]
    public void A_router_with_a_high_average_delay_is_unstable_even_without_loss()
    {
        var m = Healthy() with { GatewayPingSent = 6, GatewayPingLost = 0, GatewayAvgMs = 450, GatewayMaxMs = 800 };

        Assert.Equal(DiagnosisKind.LocalLinkUnstable, NetworkDiagnosisRules.Evaluate(m).Kind);
    }

    [Fact]
    public void Heavy_loss_to_the_router_is_a_failed_step()
    {
        var m = Healthy() with { GatewayPingSent = 6, GatewayPingLost = 4, GatewayAvgMs = 20, GatewayMaxMs = 60 };

        var step = NetworkDiagnosisRules.BuildSteps(m).Single(item => item.Id == DiagnosticStepId.GatewayLink);

        Assert.Equal(DiagnosticStepStatus.Failed, step.Status);
    }

    [Fact]
    public void A_router_that_never_answers_ping_gives_no_verdict_because_many_routers_block_ping()
    {
        var m = Healthy() with { GatewayPingSent = 6, GatewayPingLost = 6, GatewayAvgMs = null, GatewayMaxMs = null };

        Assert.Equal(DiagnosisKind.None, NetworkDiagnosisRules.Evaluate(m).Kind);
        var step = NetworkDiagnosisRules.BuildSteps(m).Single(item => item.Id == DiagnosticStepId.GatewayLink);
        Assert.Equal(DiagnosticStepStatus.Skipped, step.Status);
        Assert.Contains("may block ping", step.Detail);
    }

    [Fact]
    public void A_clean_router_link_is_ok()
    {
        var m = Healthy() with { GatewayPingSent = 6, GatewayPingLost = 0, GatewayAvgMs = 3, GatewayMaxMs = 6 };

        Assert.Equal(DiagnosisKind.None, NetworkDiagnosisRules.Evaluate(m).Kind);
        var step = NetworkDiagnosisRules.BuildSteps(m).Single(item => item.Id == DiagnosticStepId.GatewayLink);
        Assert.Equal(DiagnosticStepStatus.Ok, step.Status);
        Assert.Contains("0% lost", step.Detail);
    }

    [Fact]
    public void Server_connect_failing_while_the_reference_server_answers_quickly_means_the_route_to_the_server_is_blocked()
    {
        // 这正是 1012 店那份报告的形状：DNS 正常，到服务器 443 连不上，HTTPS 无回复。
        var m = Healthy() with
        {
            Ipv4ConnectFailed = true,
            Ipv4ConnectMs = 6000,
            Reply = ReplyOutcome.Timeout,
            DirectReplyMs = 12000,
            ReferenceHost = "1.1.1.1",
            ReferenceConnectMs = 38
        };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(DiagnosisKind.ServerPathBlocked, finding.Kind);
        Assert.Null(finding.SuggestedFix);
    }

    [Fact]
    public void Server_connect_failing_while_the_reference_server_also_fails_means_the_internet_beyond_the_router_is_down()
    {
        var m = Healthy() with
        {
            Ipv4ConnectFailed = true,
            Ipv4ConnectMs = 6000,
            Reply = ReplyOutcome.Timeout,
            ReferenceHost = "1.1.1.1",
            ReferenceConnectFailed = true,
            ReferenceConnectMs = 6000
        };

        Assert.Equal(DiagnosisKind.InternetUnreachable, NetworkDiagnosisRules.Evaluate(m).Kind);
    }

    [Fact]
    public void A_slow_reference_server_also_points_at_the_internet_connection_not_at_our_server()
    {
        var m = Healthy() with
        {
            Ipv4ConnectMs = 4000,
            Reply = ReplyOutcome.Timeout,
            ReferenceHost = "1.1.1.1",
            ReferenceConnectMs = 3800
        };

        Assert.Equal(DiagnosisKind.InternetUnreachable, NetworkDiagnosisRules.Evaluate(m).Kind);
    }

    [Fact]
    public void An_in_between_reference_time_stays_the_generic_server_unreachable()
    {
        var m = Healthy() with
        {
            Ipv4ConnectFailed = true,
            Reply = ReplyOutcome.Timeout,
            ReferenceHost = "1.1.1.1",
            ReferenceConnectMs = 900
        };

        Assert.Equal(DiagnosisKind.ServerUnreachable, NetworkDiagnosisRules.Evaluate(m).Kind);
    }

    [Fact]
    public void Without_a_reference_measurement_a_dead_server_stays_the_generic_server_unreachable()
    {
        var m = Healthy() with { Ipv4ConnectFailed = true, Reply = ReplyOutcome.Timeout };

        Assert.Equal(DiagnosisKind.ServerUnreachable, NetworkDiagnosisRules.Evaluate(m).Kind);
    }

    [Fact]
    public void The_reference_step_shows_it_is_a_different_server_used_for_comparison()
    {
        var ok = NetworkDiagnosisRules.BuildSteps(Healthy() with { ReferenceHost = "1.1.1.1", ReferenceConnectMs = 38 })
            .Single(item => item.Id == DiagnosticStepId.ReferenceConnect);
        var failed = NetworkDiagnosisRules.BuildSteps(Healthy() with { ReferenceHost = "1.1.1.1", ReferenceConnectFailed = true })
            .Single(item => item.Id == DiagnosticStepId.ReferenceConnect);
        var skipped = NetworkDiagnosisRules.BuildSteps(Healthy())
            .Single(item => item.Id == DiagnosticStepId.ReferenceConnect);

        Assert.Equal(DiagnosticStepStatus.Ok, ok.Status);
        Assert.Contains("1.1.1.1 port 443", ok.Detail);
        Assert.Contains("used for comparison", ok.Detail);
        Assert.Equal(DiagnosticStepStatus.Failed, failed.Status);
        Assert.Equal(DiagnosticStepStatus.Skipped, skipped.Status);
    }

    [Fact]
    public void Report_text_lists_target_finding_and_every_step_for_support()
    {
        var m = Healthy() with { SystemDnsMs = 4200 };
        var report = new NetworkDiagnosisReport(
            "https://hotbargain.vip/pos-api/",
            new DateTimeOffset(2026, 10, 17, 20, 8, 21, TimeSpan.FromHours(11)),
            TimeSpan.FromSeconds(9.4),
            NetworkDiagnosisRules.BuildSteps(m),
            NetworkDiagnosisRules.Evaluate(m),
            m,
            "Windows 10");

        var text = report.ToReportText();

        Assert.Contains("https://hotbargain.vip/pos-api/", text);
        Assert.Contains("DnsSlow (suggested fix: PublicDns)", text);
        Assert.Contains("2026-10-17 20:08:21 +11:00", text);
        Assert.Contains("4.2 s", text);
        foreach (var id in Enum.GetValues<DiagnosticStepId>())
        {
            Assert.Contains(id.ToString(), text);
        }
    }

    [Theory]
    [InlineData(38L, "38 ms")]
    [InlineData(999L, "999 ms")]
    [InlineData(1000L, "1.0 s")]
    [InlineData(4200L, "4.2 s")]
    public void Milliseconds_are_formatted_for_people(long milliseconds, string expected)
    {
        Assert.Equal(expected, NetworkDiagnosisReport.FormatMs(milliseconds));
    }
}
