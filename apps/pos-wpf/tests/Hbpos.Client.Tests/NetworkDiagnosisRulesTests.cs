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
        Assert.Equal(14, steps.Count);
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

    private const string LetsEncryptIssuer = "CN=YE2, O=Let's Encrypt, C=US";

    private static NetworkMeasurements TlsHealthy() => Healthy() with
    {
        TlsOutcome = TlsProbeOutcome.Completed,
        TlsHandshakeMs = 650,
        TlsOfflineHandshakeMs = 600,
        TlsProtocol = "Tls12",
        ChainChecked = true,
        ChainTrusted = true,
        ChainSubjects = ["CN=hotbargain.vip", LetsEncryptIssuer],
        CertificateIssuer = LetsEncryptIssuer
    };

    [Fact]
    public void A_healthy_tls_handshake_and_trusted_chain_are_ok()
    {
        var m = TlsHealthy();

        Assert.Equal(DiagnosisKind.None, NetworkDiagnosisRules.Evaluate(m).Kind);
        var steps = NetworkDiagnosisRules.BuildSteps(m);
        var handshake = steps.Single(step => step.Id == DiagnosticStepId.TlsHandshake);
        var trust = steps.Single(step => step.Id == DiagnosticStepId.CertificateTrust);
        Assert.Equal(DiagnosticStepStatus.Ok, handshake.Status);
        Assert.Equal(650, handshake.ElapsedMs);
        Assert.Contains("Tls12", handshake.Detail);
        Assert.Equal(DiagnosticStepStatus.Ok, trust.Status);
        Assert.Contains("2 certificates", trust.Detail);
        Assert.Contains("Let's Encrypt YE2", trust.Detail);
    }

    [Fact]
    public void A_missing_root_certificate_is_diagnosed_and_offers_to_install_it()
    {
        // 老系统缺 ISRG Root X1：证书链在本机不受信任，Windows 联网找根证书时 HTTPS 无回复。
        var m = TlsHealthy() with
        {
            Reply = ReplyOutcome.Timeout,
            DirectReplyMs = 12000,
            TlsOutcome = TlsProbeOutcome.TimedOut,
            TlsHandshakeMs = 12000,
            ChainTrusted = false,
            ChainProblems = ["UntrustedRoot"],
            MissingTrustedRoots = ["ISRG Root X1"]
        };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(DiagnosisKind.CertificateRootMissing, finding.Kind);
        Assert.Equal(NetworkFixKind.InstallTrustedRoots, finding.SuggestedFix);
        var steps = NetworkDiagnosisRules.BuildSteps(m);
        Assert.Equal(DiagnosticStepStatus.Failed, steps.Single(step => step.Id == DiagnosticStepId.TlsHandshake).Status);
        var trust = steps.Single(step => step.Id == DiagnosticStepId.CertificateTrust);
        Assert.Equal(DiagnosticStepStatus.Failed, trust.Status);
        Assert.Contains("does not trust the root: ISRG Root X1", trust.Detail);
    }

    [Fact]
    public void An_untrusted_chain_without_a_known_missing_root_is_a_generic_secure_connection_failure()
    {
        var m = TlsHealthy() with
        {
            Reply = ReplyOutcome.SecureConnectionFailed,
            ChainTrusted = false,
            ChainProblems = ["PartialChain"]
        };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(DiagnosisKind.SecureConnectionFailed, finding.Kind);
        Assert.Null(finding.SuggestedFix);
        Assert.Contains("PartialChain", NetworkDiagnosisRules.BuildSteps(m).Single(step => step.Id == DiagnosticStepId.CertificateTrust).Detail);
    }

    [Fact]
    public void A_trusted_chain_whose_default_handshake_times_out_means_windows_is_stuck_looking_up_certificates_online()
    {
        var m = TlsHealthy() with
        {
            Reply = ReplyOutcome.Timeout,
            TlsOutcome = TlsProbeOutcome.TimedOut,
            TlsHandshakeMs = 12000,
            TlsOfflineHandshakeMs = 500
        };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(DiagnosisKind.CertificateCheckSlow, finding.Kind);
        Assert.Null(finding.SuggestedFix);
    }

    [Fact]
    public void A_default_handshake_that_is_seconds_slower_than_the_offline_one_is_a_slow_certificate_check()
    {
        var m = TlsHealthy() with { TlsHandshakeMs = 4200, TlsOfflineHandshakeMs = 420 };

        Assert.Equal(DiagnosisKind.CertificateCheckSlow, NetworkDiagnosisRules.Evaluate(m).Kind);
        var handshake = NetworkDiagnosisRules.BuildSteps(m).Single(step => step.Id == DiagnosticStepId.TlsHandshake);
        Assert.Equal(DiagnosticStepStatus.Slow, handshake.Status);
        Assert.Contains("of it spent checking the certificate online", handshake.Detail);
    }

    [Fact]
    public void A_two_second_handshake_on_a_healthy_overseas_path_is_not_flagged()
    {
        // 这是在真机上量到的情形：服务器在海外，握手加首次加载花了 2.3 秒，其余全部正常，不能标成"慢"。
        var m = TlsHealthy() with { TlsHandshakeMs = 2300, TlsOfflineHandshakeMs = 1200 };

        Assert.Equal(DiagnosisKind.None, NetworkDiagnosisRules.Evaluate(m).Kind);
        var handshake = NetworkDiagnosisRules.BuildSteps(m).Single(step => step.Id == DiagnosticStepId.TlsHandshake);
        Assert.Equal(DiagnosticStepStatus.Ok, handshake.Status);
        Assert.Equal(2300, handshake.ElapsedMs);
    }

    [Fact]
    public void A_handshake_over_four_seconds_is_flagged_as_slow_even_without_an_offline_baseline()
    {
        var m = TlsHealthy() with { TlsHandshakeMs = 4500, TlsOfflineHandshakeMs = null };

        var handshake = NetworkDiagnosisRules.BuildSteps(m).Single(step => step.Id == DiagnosticStepId.TlsHandshake);

        Assert.Equal(DiagnosticStepStatus.Slow, handshake.Status);
    }

    [Fact]
    public void A_slow_handshake_that_is_slow_offline_too_is_not_blamed_on_the_certificate_check()
    {
        // 距离远 / 链路慢：两种方式一样慢，说明不是联网查证书造成的。
        var m = TlsHealthy() with { TlsHandshakeMs = 2000, TlsOfflineHandshakeMs = 1900 };

        Assert.NotEqual(DiagnosisKind.CertificateCheckSlow, NetworkDiagnosisRules.Evaluate(m).Kind);
    }

    [Fact]
    public void An_edgeone_certificate_from_trustasia_is_an_expected_issuer()
    {
        // 默认入口 pos.hotbargain.vip 走 EdgeOne，其托管证书由 TrustAsia 签发。
        // 线路真有问题（回复超时、TLS 超时）时，不能被误判成"HTTPS 被重新签名"，否则会把店员引到错误的方向。
        var m = TlsHealthy() with
        {
            Reply = ReplyOutcome.Timeout,
            TlsOutcome = TlsProbeOutcome.TimedOut,
            TlsHandshakeMs = 12000,
            CertificateIssuer = "CN=TrustAsia DV TLS RSA CA 2025, O=\"TrustAsia Technologies, Inc.\", C=CN"
        };

        Assert.NotEqual(DiagnosisKind.HttpsIntercepted, NetworkDiagnosisRules.Evaluate(m).Kind);
        var trust = NetworkDiagnosisRules.BuildSteps(m).Single(step => step.Id == DiagnosticStepId.CertificateTrust);
        Assert.NotEqual(DiagnosticStepStatus.Failed, trust.Status);
    }

    [Fact]
    public void A_certificate_from_an_unexpected_issuer_means_something_is_re_signing_https()
    {
        var m = TlsHealthy() with
        {
            Reply = ReplyOutcome.Timeout,
            TlsOutcome = TlsProbeOutcome.TimedOut,
            TlsHandshakeMs = 12000,
            CertificateIssuer = "CN=Kaspersky Anti-Virus Personal Root Certificate"
        };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(DiagnosisKind.HttpsIntercepted, finding.Kind);
        Assert.Null(finding.SuggestedFix);
        var trust = NetworkDiagnosisRules.BuildSteps(m).Single(step => step.Id == DiagnosticStepId.CertificateTrust);
        Assert.Equal(DiagnosticStepStatus.Failed, trust.Status);
        Assert.Contains("Kaspersky Anti-Virus Personal Root Certificate", trust.Detail);
        Assert.Contains("re-signing HTTPS", trust.Detail);
    }

    [Fact]
    public void An_unexpected_issuer_is_not_flagged_while_everything_works()
    {
        // 不能因为签发者不是 Let's Encrypt 就报警：服务器将来换了证书颁发机构，只要一切正常就不该打扰现场。
        var m = TlsHealthy() with { CertificateIssuer = "CN=Some Other CA" };

        Assert.Equal(DiagnosisKind.None, NetworkDiagnosisRules.Evaluate(m).Kind);
    }

    [Fact]
    public void Certificate_dates_that_do_not_match_the_clock_are_reported_as_a_clock_problem()
    {
        var m = TlsHealthy() with
        {
            Reply = ReplyOutcome.SecureConnectionFailed,
            ChainTrusted = false,
            ChainNotTimeValid = true,
            ChainProblems = ["NotTimeValid"]
        };

        Assert.Equal(DiagnosisKind.ClockSkew, NetworkDiagnosisRules.Evaluate(m).Kind);
        Assert.Contains("clock", NetworkDiagnosisRules.BuildSteps(m).Single(step => step.Id == DiagnosticStepId.CertificateTrust).Detail);
    }

    [Fact]
    public void Tls_steps_are_skipped_when_nothing_was_probed()
    {
        var steps = NetworkDiagnosisRules.BuildSteps(Healthy());

        Assert.Equal(DiagnosticStepStatus.Skipped, steps.Single(step => step.Id == DiagnosticStepId.TlsHandshake).Status);
        Assert.Equal(DiagnosticStepStatus.Skipped, steps.Single(step => step.Id == DiagnosticStepId.CertificateTrust).Status);
    }

    [Theory]
    [InlineData("CN=YE2, O=Let's Encrypt, C=US", "Let's Encrypt YE2")]
    [InlineData("CN=ISRG Root X1, O=Internet Security Research Group, C=US", "Internet Security Research Group ISRG Root X1")]
    [InlineData("CN=Kaspersky Anti-Virus Personal Root Certificate", "Kaspersky Anti-Virus Personal Root Certificate")]
    [InlineData("O=Only Org", "Only Org")]
    [InlineData("", "unknown")]
    public void Issuer_names_are_shortened_for_people(string subject, string expected)
    {
        Assert.Equal(expected, NetworkDiagnosisRules.ShortName(subject));
    }

    private static NetworkMeasurements StabilityHealthy() => Healthy() with
    {
        StabilityAttempts = 5,
        StabilitySucceeded = 5,
        StabilityAvgMs = 270,
        StabilityMaxMs = 310,
        ReferenceStabilityAttempts = 5,
        ReferenceStabilitySucceeded = 5,
        ReferenceStabilityMaxMs = 45,
        BurstAttempts = 6,
        BurstSucceeded = 6,
        BurstMaxMs = 420
    };

    [Fact]
    public void Clean_connection_runs_to_the_server_are_ok_and_say_so()
    {
        var m = StabilityHealthy();

        Assert.Equal(DiagnosisKind.None, NetworkDiagnosisRules.Evaluate(m).Kind);
        var steps = NetworkDiagnosisRules.BuildSteps(m);
        var sequential = steps.Single(step => step.Id == DiagnosticStepId.ConnectStability);
        var burst = steps.Single(step => step.Id == DiagnosticStepId.ConnectBurst);
        Assert.Equal(DiagnosticStepStatus.Ok, sequential.Status);
        Assert.Equal("one at a time: 5/5 connected · avg 270 ms · max 310 ms · reference 5/5", sequential.Detail);
        Assert.Equal(DiagnosticStepStatus.Ok, burst.Status);
        Assert.Equal("6 at once: 6/6 connected · max 420 ms", burst.Detail);
    }

    [Fact]
    public void Connections_that_sometimes_fail_while_the_reference_is_clean_mean_the_route_to_the_server_is_lossy()
    {
        var m = StabilityHealthy() with { StabilitySucceeded = 4, StabilityAvgMs = 300, StabilityMaxMs = 310 };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(DiagnosisKind.ServerPathLossy, finding.Kind);
        Assert.Null(finding.SuggestedFix);
        var step = NetworkDiagnosisRules.BuildSteps(m).Single(item => item.Id == DiagnosticStepId.ConnectStability);
        Assert.Equal(DiagnosticStepStatus.Failed, step.Status);
        Assert.Contains("4/5 connected", step.Detail);
    }

    [Fact]
    public void Connections_that_all_succeed_but_take_seconds_are_also_lossy_because_the_handshake_had_to_be_resent()
    {
        var m = StabilityHealthy() with { StabilityAvgMs = 1200, StabilityMaxMs = 3100 };

        Assert.Equal(DiagnosisKind.ServerPathLossy, NetworkDiagnosisRules.Evaluate(m).Kind);
        Assert.Equal(DiagnosticStepStatus.Slow, NetworkDiagnosisRules.BuildSteps(m).Single(item => item.Id == DiagnosticStepId.ConnectStability).Status);
    }

    [Fact]
    public void Single_connections_working_but_several_at_once_failing_means_something_limits_concurrent_connections()
    {
        var m = StabilityHealthy() with { BurstSucceeded = 3, BurstMaxMs = 3000 };

        var finding = NetworkDiagnosisRules.Evaluate(m);

        Assert.Equal(DiagnosisKind.ConnectionBurstLimited, finding.Kind);
        Assert.Null(finding.SuggestedFix);
        var burst = NetworkDiagnosisRules.BuildSteps(m).Single(item => item.Id == DiagnosticStepId.ConnectBurst);
        Assert.Equal(DiagnosticStepStatus.Failed, burst.Status);
        Assert.Contains("6 at once: 3/6 connected", burst.Detail);
    }

    [Fact]
    public void A_failed_connection_in_the_main_batch_with_clean_one_at_a_time_runs_is_a_concurrency_problem_not_a_blocked_route()
    {
        // 1012 店第二份报告的形状：主检测里同时开了多个连接，其中到服务器的那个失败；逐个连却全部正常。
        var m = StabilityHealthy() with
        {
            Ipv4ConnectFailed = true,
            Ipv4ConnectMs = 6000,
            Reply = ReplyOutcome.Timeout,
            ReferenceHost = "1.1.1.1",
            ReferenceConnectMs = 39,
            BurstAttempts = null,
            BurstSucceeded = null,
            BurstMaxMs = null
        };

        Assert.Equal(DiagnosisKind.ConnectionBurstLimited, NetworkDiagnosisRules.Evaluate(m).Kind);
    }

    [Fact]
    public void An_unclean_reference_means_the_stability_numbers_say_nothing_about_the_server_route()
    {
        var m = StabilityHealthy() with { StabilitySucceeded = 3, ReferenceStabilitySucceeded = 3 };

        Assert.NotEqual(DiagnosisKind.ServerPathLossy, NetworkDiagnosisRules.Evaluate(m).Kind);
    }

    [Fact]
    public void A_server_that_never_accepts_a_connection_is_still_a_blocked_route_not_a_lossy_one()
    {
        var m = StabilityHealthy() with
        {
            StabilitySucceeded = 0,
            StabilityAvgMs = null,
            StabilityMaxMs = null,
            Ipv4ConnectFailed = true,
            Ipv4ConnectMs = 6000,
            Reply = ReplyOutcome.Timeout,
            ReferenceHost = "1.1.1.1",
            ReferenceConnectMs = 39
        };

        Assert.Equal(DiagnosisKind.ServerPathBlocked, NetworkDiagnosisRules.Evaluate(m).Kind);
    }

    [Fact]
    public void The_stability_steps_are_skipped_when_they_were_not_measured()
    {
        var steps = NetworkDiagnosisRules.BuildSteps(Healthy());

        Assert.Equal(DiagnosticStepStatus.Skipped, steps.Single(step => step.Id == DiagnosticStepId.ConnectStability).Status);
        Assert.Equal(DiagnosticStepStatus.Skipped, steps.Single(step => step.Id == DiagnosticStepId.ConnectBurst).Status);
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
