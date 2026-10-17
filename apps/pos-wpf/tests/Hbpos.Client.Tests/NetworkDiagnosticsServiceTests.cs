using Hbpos.Client.Wpf.Services.NetworkDiagnostics;

namespace Hbpos.Client.Tests;

public sealed class NetworkDiagnosticsServiceTests
{
    private static readonly Uri Api = new("https://hotbargain.vip/pos-api/");

    [Fact]
    public async Task Slow_system_dns_is_found_and_the_connect_test_uses_the_address_the_machine_would_use()
    {
        var probe = new FakeProbe
        {
            SystemDns = new DnsProbeResult(4200, ["203.0.113.20"], false),
            PublicDns = new DnsProbeResult(38, ["203.0.113.20"], false)
        };
        var service = new NetworkDiagnosticsService(probe);

        var report = await service.RunAsync(Api);

        Assert.Equal(DiagnosisKind.DnsSlow, report.Finding.Kind);
        Assert.Equal(NetworkFixKind.PublicDns, report.Finding.SuggestedFix);
        Assert.True(report.HasSuggestedFix);
        Assert.Equal(["203.0.113.20:443", "1.1.1.1:443"], probe.Connects.OrderByDescending(item => item.StartsWith("2")).ToArray());
        Assert.Equal(["hotbargain.vip"], probe.SystemLookups);
        Assert.Equal(["hotbargain.vip"], probe.PublicLookups);
        Assert.Equal("https://hotbargain.vip/pos-api/", report.Target);
        Assert.Equal(DiagnosticStepStatus.Slow, report.Steps.Single(step => step.Id == DiagnosticStepId.SystemDns).Status);
    }

    [Fact]
    public async Task When_system_dns_fails_the_public_dns_answer_is_used_for_the_connect_test()
    {
        var probe = new FakeProbe
        {
            SystemDns = new DnsProbeResult(6000, [], true),
            PublicDns = new DnsProbeResult(40, ["203.0.113.20", "2001:db8::20"], false)
        };

        var report = await new NetworkDiagnosticsService(probe).RunAsync(Api);

        Assert.Equal(DiagnosisKind.DnsFailed, report.Finding.Kind);
        Assert.Contains("203.0.113.20:443", probe.Connects);
        Assert.Contains("2001:db8::20:443", probe.Connects);
        Assert.True(report.Measurements.HasIpv6Address);
    }

    [Fact]
    public async Task Ip_literal_and_localhost_skip_dns_and_ipv6_steps()
    {
        var probe = new FakeProbe();
        var service = new NetworkDiagnosticsService(probe);

        var report = await service.RunAsync(new Uri("http://localhost:5159/"));

        Assert.Empty(probe.SystemLookups);
        Assert.Empty(probe.PublicLookups);
        Assert.Equal(["127.0.0.1:5159"], probe.Connects); // 本机开发地址不做参照连接
        Assert.Equal(DiagnosticStepStatus.Skipped, report.Steps.Single(step => step.Id == DiagnosticStepId.PublicDns).Status);
        Assert.Equal(DiagnosticStepStatus.Skipped, report.Steps.Single(step => step.Id == DiagnosticStepId.ConnectIpv6).Status);
    }

    [Fact]
    public async Task Both_health_checks_run_and_proxy_difference_is_diagnosed()
    {
        var probe = new FakeProbe
        {
            DirectReply = new ReplyProbeResult(300, ReplyOutcome.Online, 200, null),
            ProxyReply = new ReplyProbeResult(3300, ReplyOutcome.Online, 200, null),
            Proxy = new ProxyProbeInfo(false, null, true)
        };

        var report = await new NetworkDiagnosticsService(probe).RunAsync(Api);

        Assert.Equal(DiagnosisKind.ProxyAutoDetectSlow, report.Finding.Kind);
        Assert.Equal(NetworkFixKind.DisableProxyAutoDetect, report.Finding.SuggestedFix);
        Assert.Equal([false, true], probe.ReplyProxyFlags.OrderBy(flag => flag).ToArray());
    }

    [Fact]
    public async Task Clock_skew_is_measured_against_the_server_time_with_half_the_round_trip_removed()
    {
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        var probe = new FakeProbe
        {
            DirectReply = new ReplyProbeResult(2000, ReplyOutcome.Online, 200, now.AddHours(-2))
        };

        var report = await new NetworkDiagnosticsService(probe, () => now).RunAsync(Api);

        Assert.Equal(DiagnosisKind.ClockSkew, report.Finding.Kind);
        // 本机比服务器快 2 小时，再加上一半往返耗时 1 秒。
        Assert.Equal(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1), report.Measurements.ClockSkew);
    }

    [Fact]
    public async Task The_router_is_pinged_and_the_reference_server_is_connected_to_for_comparison()
    {
        var probe = new FakeProbe { GatewayPing = new PingProbeResult(6, 2, 40, 900) };

        var report = await new NetworkDiagnosticsService(probe).RunAsync(Api);

        Assert.Equal(["192.168.1.1x6"], probe.Pings);
        Assert.Contains("1.1.1.1:443", probe.Connects);
        Assert.Equal(2, report.Measurements.GatewayPingLost);
        Assert.Equal(900, report.Measurements.GatewayMaxMs);
        Assert.Equal("1.1.1.1", report.Measurements.ReferenceHost);
        Assert.Equal(DiagnosisKind.LocalLinkUnstable, report.Finding.Kind);
    }

    [Fact]
    public async Task The_1012_report_shape_is_diagnosed_as_a_blocked_route_to_the_server()
    {
        // 服务器地址连不上，参照地址很快：到服务器的路径有问题，而不是整条链路。
        var probe = new FakeProbe
        {
            ConnectResultFor = address => address == "1.1.1.1" ? new ConnectProbeResult(38, false) : new ConnectProbeResult(6000, true),
            DirectReply = new ReplyProbeResult(12000, ReplyOutcome.Timeout, null, null),
            ProxyReply = new ReplyProbeResult(12000, ReplyOutcome.Timeout, null, null)
        };

        var report = await new NetworkDiagnosticsService(probe).RunAsync(Api);

        Assert.Equal(DiagnosisKind.ServerPathBlocked, report.Finding.Kind);
        Assert.Equal(DiagnosticStepStatus.Failed, report.Steps.Single(step => step.Id == DiagnosticStepId.ConnectIpv4).Status);
        Assert.Equal(DiagnosticStepStatus.Ok, report.Steps.Single(step => step.Id == DiagnosticStepId.ReferenceConnect).Status);
    }

    [Fact]
    public async Task When_the_reference_server_fails_too_the_internet_beyond_the_router_is_blamed()
    {
        var probe = new FakeProbe
        {
            ConnectResult = new ConnectProbeResult(6000, true),
            DirectReply = new ReplyProbeResult(12000, ReplyOutcome.Timeout, null, null),
            ProxyReply = new ReplyProbeResult(12000, ReplyOutcome.Timeout, null, null)
        };

        var report = await new NetworkDiagnosticsService(probe).RunAsync(Api);

        Assert.Equal(DiagnosisKind.InternetUnreachable, report.Finding.Kind);
    }

    [Fact]
    public async Task Without_an_active_adapter_the_router_is_not_pinged()
    {
        var probe = new FakeProbe { Local = new LocalNetworkInfo(false, null, null, [], null) };

        var report = await new NetworkDiagnosticsService(probe).RunAsync(Api);

        Assert.Empty(probe.Pings);
        Assert.Equal(DiagnosisKind.NoLocalNetwork, report.Finding.Kind);
        Assert.Equal(DiagnosticStepStatus.Skipped, report.Steps.Single(step => step.Id == DiagnosticStepId.GatewayLink).Status);
    }

    [Fact]
    public async Task Both_tls_probes_run_for_an_https_server_and_fill_the_handshake_and_chain_measurements()
    {
        var probe = new FakeProbe();

        var report = await new NetworkDiagnosticsService(probe).RunAsync(Api);

        Assert.Equal(
            ["hotbargain.vip:443:default", "hotbargain.vip:443:offline"],
            probe.TlsCalls.OrderBy(call => call).ToArray());
        Assert.Equal(TlsProbeOutcome.Completed, report.Measurements.TlsOutcome);
        Assert.Equal(650, report.Measurements.TlsHandshakeMs);
        Assert.Equal(600, report.Measurements.TlsOfflineHandshakeMs);
        Assert.True(report.Measurements.ChainChecked);
        Assert.True(report.Measurements.ChainTrusted);
        Assert.Equal("CN=YE2, O=Let's Encrypt, C=US", report.Measurements.CertificateIssuer);
        Assert.Equal(0, probe.MissingRootsCalls);
        Assert.Equal(DiagnosisKind.None, report.Finding.Kind);
    }

    [Fact]
    public async Task Tls_is_not_probed_for_localhost_or_plain_http()
    {
        var probe = new FakeProbe();
        var service = new NetworkDiagnosticsService(probe);

        await service.RunAsync(new Uri("http://localhost:5159/"));
        await service.RunAsync(new Uri("http://pos.example.test/"));

        Assert.Empty(probe.TlsCalls);
    }

    [Fact]
    public async Task A_missing_root_on_an_old_machine_is_diagnosed_end_to_end_and_the_fix_is_offered()
    {
        var probe = new FakeProbe
        {
            TlsOffline = FakeProbe.Tls(450, trusted: false),
            TlsDefault = FakeProbe.Tls(12000, trusted: false, TlsProbeOutcome.TimedOut),
            MissingRoots = ["ISRG Root X1"],
            DirectReply = new ReplyProbeResult(12000, ReplyOutcome.Timeout, null, null),
            ProxyReply = new ReplyProbeResult(12000, ReplyOutcome.Timeout, null, null)
        };

        var report = await new NetworkDiagnosticsService(probe).RunAsync(Api);

        Assert.Equal(DiagnosisKind.CertificateRootMissing, report.Finding.Kind);
        Assert.Equal(NetworkFixKind.InstallTrustedRoots, report.Finding.SuggestedFix);
        Assert.Equal(["ISRG Root X1"], report.Measurements.MissingTrustedRoots);
        Assert.Equal(1, probe.MissingRootsCalls);
        Assert.Equal(DiagnosticStepStatus.Failed, report.Steps.Single(step => step.Id == DiagnosticStepId.CertificateTrust).Status);
    }

    [Fact]
    public async Task A_trusted_chain_with_a_stuck_default_handshake_is_a_slow_certificate_check()
    {
        var probe = new FakeProbe
        {
            TlsOffline = FakeProbe.Tls(450, trusted: true),
            TlsDefault = FakeProbe.Tls(12000, trusted: true, TlsProbeOutcome.TimedOut),
            DirectReply = new ReplyProbeResult(12000, ReplyOutcome.Timeout, null, null),
            ProxyReply = new ReplyProbeResult(12000, ReplyOutcome.Timeout, null, null)
        };

        var report = await new NetworkDiagnosticsService(probe).RunAsync(Api);

        Assert.Equal(DiagnosisKind.CertificateCheckSlow, report.Finding.Kind);
    }

    [Fact]
    public async Task A_certificate_re_signed_by_security_software_is_reported_with_its_issuer()
    {
        var probe = new FakeProbe
        {
            TlsOffline = FakeProbe.Tls(450, trusted: true, issuer: "CN=ESET SSL Filter CA, O=ESET, spol. s r.o."),
            TlsDefault = FakeProbe.Tls(12000, trusted: true, TlsProbeOutcome.TimedOut, "CN=ESET SSL Filter CA, O=ESET, spol. s r.o."),
            DirectReply = new ReplyProbeResult(12000, ReplyOutcome.Timeout, null, null),
            ProxyReply = new ReplyProbeResult(12000, ReplyOutcome.Timeout, null, null)
        };

        var report = await new NetworkDiagnosticsService(probe).RunAsync(Api);

        Assert.Equal(DiagnosisKind.HttpsIntercepted, report.Finding.Kind);
        Assert.Contains("ESET SSL Filter CA", report.Measurements.CertificateIssuer);
    }

    [Fact]
    public async Task Cancelling_the_caller_token_aborts_the_run()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var probe = new FakeProbe { ThrowIfCancelled = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new NetworkDiagnosticsService(probe).RunAsync(Api, cancellation.Token));
    }

    private sealed class FakeProbe : INetworkProbe
    {
        public LocalNetworkInfo Local { get; set; } = new(true, "Ethernet", "192.168.1.1", ["192.168.1.1"], 12);

        public DnsProbeResult SystemDns { get; set; } = new(30, ["203.0.113.20"], false);

        public DnsProbeResult PublicDns { get; set; } = new(38, ["203.0.113.20"], false);

        public ConnectProbeResult ConnectResult { get; set; } = new(46, false);

        public PingProbeResult GatewayPing { get; set; } = new(6, 0, 3, 6);

        public TlsProbeResult TlsOffline { get; set; } = Tls(600, true);

        public TlsProbeResult TlsDefault { get; set; } = Tls(650, true);

        public IReadOnlyList<string> MissingRoots { get; set; } = [];

        public int MissingRootsCalls { get; private set; }

        public List<string> TlsCalls { get; } = [];

        public static TlsProbeResult Tls(long ms, bool trusted, TlsProbeOutcome outcome = TlsProbeOutcome.Completed, string issuer = "CN=YE2, O=Let's Encrypt, C=US") => new(
            ms,
            outcome,
            outcome == TlsProbeOutcome.Completed ? "Tls12" : null,
            null,
            ChainChecked: true,
            ChainTrusted: trusted,
            NotTimeValid: false,
            trusted ? [] : ["UntrustedRoot"],
            ["CN=hotbargain.vip", issuer],
            issuer);

        public List<string> Pings { get; } = [];

        public Func<string, ConnectProbeResult>? ConnectResultFor { get; set; }

        public ProxyProbeInfo Proxy { get; set; } = new(false, null, false);

        public ReplyProbeResult DirectReply { get; set; } = new(310, ReplyOutcome.Online, 200, null);

        public ReplyProbeResult? ProxyReply { get; set; }

        public bool ThrowIfCancelled { get; set; }

        public List<string> Connects { get; } = [];

        public List<string> SystemLookups { get; } = [];

        public List<string> PublicLookups { get; } = [];

        public List<bool> ReplyProxyFlags { get; } = [];

        public LocalNetworkInfo GetLocalNetwork() => Local;

        public Task<DnsProbeResult> ResolveSystemDnsAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            SystemLookups.Add(host);
            return Task.FromResult(SystemDns);
        }

        public Task<DnsProbeResult> ResolvePublicDnsAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            PublicLookups.Add(host);
            return Task.FromResult(PublicDns);
        }

        public Task<ConnectProbeResult> ConnectAsync(string address, int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            lock (Connects)
            {
                Connects.Add($"{address}:{port}");
            }

            return Task.FromResult(ConnectResultFor?.Invoke(address) ?? ConnectResult);
        }

        public Task<TlsProbeResult> ProbeTlsAsync(string host, int port, bool offlineChainCheck, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            lock (TlsCalls)
            {
                TlsCalls.Add($"{host}:{port}:{(offlineChainCheck ? "offline" : "default")}");
            }

            return Task.FromResult(offlineChainCheck ? TlsOffline : TlsDefault);
        }

        public IReadOnlyList<string> GetMissingTrustedRoots()
        {
            MissingRootsCalls++;
            return MissingRoots;
        }

        public Task<PingProbeResult> PingAsync(string address, int count, TimeSpan timeoutPerPing, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            lock (Pings)
            {
                Pings.Add($"{address}x{count}");
            }

            return Task.FromResult(GatewayPing);
        }

        public ProxyProbeInfo GetProxyInfo(Uri target) => Proxy;

        public Task<ReplyProbeResult> CheckReplyAsync(Uri healthUri, bool useSystemProxy, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            lock (ReplyProxyFlags)
            {
                ReplyProxyFlags.Add(useSystemProxy);
            }

            Assert.Equal("/pos-api/api/v1/health", healthUri.AbsolutePath == "/api/v1/health" ? "/pos-api/api/v1/health" : healthUri.AbsolutePath);
            return Task.FromResult(useSystemProxy ? ProxyReply ?? DirectReply : DirectReply);
        }

        private void Check(CancellationToken cancellationToken)
        {
            if (ThrowIfCancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}
