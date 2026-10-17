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
        Assert.Equal(8, steps.Count);
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
