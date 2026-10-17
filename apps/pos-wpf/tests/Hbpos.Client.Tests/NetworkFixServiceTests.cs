using Hbpos.Client.Wpf.Services.NetworkDiagnostics;

namespace Hbpos.Client.Tests;

public sealed class NetworkFixServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 20, 31, 0, TimeSpan.FromHours(11));

    [Fact]
    public void Dns_plan_shows_what_changes_and_that_administrator_permission_is_needed()
    {
        var env = new Env { Dns = new DnsSettings(["192.168.1.1"], true) };

        var plan = env.Service.Plan(NetworkFixKind.PublicDns);

        Assert.NotNull(plan);
        Assert.True(plan.NeedsAdministrator);
        Assert.Equal("DNS on Ethernet", plan.Target);
        Assert.Equal("192.168.1.1 (automatic)", plan.Before);
        Assert.Equal("1.1.1.1, 8.8.8.8", plan.After);
        Assert.Equal(12, plan.AdapterIndex);
        // 只是计划：什么都没改。
        Assert.Empty(env.Dns_SetCalls);
        Assert.Empty(env.Store.Records);
    }

    [Fact]
    public void Dns_plan_is_unavailable_without_an_active_adapter()
    {
        var env = new Env { Local = new LocalNetworkInfo(false, null, null, [], null) };

        Assert.Null(env.Service.Plan(NetworkFixKind.PublicDns));
    }

    [Fact]
    public async Task Applying_dns_sets_the_public_servers_and_records_the_previous_state()
    {
        var env = new Env { Dns = new DnsSettings(["192.168.1.1"], true) };
        var plan = env.Service.Plan(NetworkFixKind.PublicDns)!;

        var result = await env.Service.ApplyAsync(plan);

        Assert.True(result.Succeeded);
        Assert.Equal([(12, "1.1.1.1,8.8.8.8")], env.Dns_SetCalls);
        var record = Assert.Single(env.Store.Records);
        Assert.Equal(NetworkFixKind.PublicDns, record.Kind);
        Assert.Equal(["192.168.1.1"], record.PreviousServers);
        Assert.True(record.PreviousAutomatic);
        Assert.Equal(Now, record.AppliedAt);
    }

    [Fact]
    public async Task Declining_the_windows_permission_prompt_changes_nothing_and_records_nothing()
    {
        var env = new Env { Dns = new DnsSettings(["192.168.1.1"], true), Elevation = false };

        var result = await env.Service.ApplyAsync(env.Service.Plan(NetworkFixKind.PublicDns)!);

        Assert.False(result.Succeeded);
        Assert.True(result.Cancelled);
        Assert.Empty(env.Store.Records);
    }

    [Fact]
    public async Task A_failing_change_is_reported_and_not_recorded()
    {
        var env = new Env { Dns = new DnsSettings(["192.168.1.1"], true), SetThrows = new InvalidOperationException("exit code 1") };

        var result = await env.Service.ApplyAsync(env.Service.Plan(NetworkFixKind.PublicDns)!);

        Assert.False(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.Equal("exit code 1", result.Error);
        Assert.Empty(env.Store.Records);
    }

    [Fact]
    public async Task Undo_restores_manual_servers_and_forgets_the_record()
    {
        var env = new Env { Dns = new DnsSettings(["10.1.1.1", "10.1.1.2"], false) };
        await env.Service.ApplyAsync(env.Service.Plan(NetworkFixKind.PublicDns)!);
        env.Dns_SetCalls.Clear();

        var result = await env.Service.UndoAsync(NetworkFixKind.PublicDns);

        Assert.True(result.Succeeded);
        Assert.Equal([(12, "10.1.1.1,10.1.1.2")], env.Dns_SetCalls);
        Assert.Empty(env.Dns_ResetCalls);
        Assert.Empty(env.Store.Records);
        Assert.Null(env.Service.GetApplied(NetworkFixKind.PublicDns));
    }

    [Fact]
    public async Task Undo_of_automatic_dns_goes_back_to_automatic_instead_of_pinning_the_router_address()
    {
        var env = new Env { Dns = new DnsSettings(["192.168.1.1"], true) };
        await env.Service.ApplyAsync(env.Service.Plan(NetworkFixKind.PublicDns)!);

        var result = await env.Service.UndoAsync(NetworkFixKind.PublicDns);

        Assert.True(result.Succeeded);
        Assert.Equal([12], env.Dns_ResetCalls);
    }

    [Fact]
    public async Task Applying_twice_keeps_the_original_state_so_undo_goes_back_to_before_the_first_fix()
    {
        var env = new Env { Dns = new DnsSettings(["192.168.1.1"], true) };
        await env.Service.ApplyAsync(env.Service.Plan(NetworkFixKind.PublicDns)!);
        env.Dns = new DnsSettings(["1.1.1.1", "8.8.8.8"], false);
        await env.Service.ApplyAsync(env.Service.Plan(NetworkFixKind.PublicDns)!);

        Assert.Equal(["192.168.1.1"], Assert.Single(env.Store.Records).PreviousServers);
        await env.Service.UndoAsync(NetworkFixKind.PublicDns);
        Assert.Equal([12], env.Dns_ResetCalls);
    }

    [Fact]
    public async Task Undo_without_a_record_reports_nothing_to_undo()
    {
        var env = new Env();

        var result = await env.Service.UndoAsync(NetworkFixKind.PublicDns);

        Assert.False(result.Succeeded);
        Assert.Equal("Nothing to undo.", result.Error);
    }

    [Fact]
    public async Task Turning_off_proxy_auto_detection_needs_no_administrator_and_is_undone_to_the_previous_value()
    {
        var env = new Env { ProxyValue = null };
        var plan = env.Service.Plan(NetworkFixKind.DisableProxyAutoDetect)!;

        Assert.False(plan.NeedsAdministrator);
        Assert.Equal("on", plan.Before);
        Assert.Equal("off", plan.After);

        await env.Service.ApplyAsync(plan);
        Assert.Equal(0, env.ProxyValue);

        await env.Service.UndoAsync(NetworkFixKind.DisableProxyAutoDetect);
        Assert.Null(env.ProxyValue);
        Assert.Empty(env.Store.Records);
    }

    [Fact]
    public void Only_ipv4_addresses_are_accepted_as_dns_servers_so_nothing_else_reaches_the_script()
    {
        var configurator = new PowerShellDnsConfigurator();

        // 校验在同步部分完成，任何脚本都没有机会启动。
        Assert.Throws<ArgumentException>(() => { _ = configurator.SetServersAsync(12, ["1.1.1.1; calc"], CancellationToken.None); });
        Assert.Throws<ArgumentException>(() => { _ = configurator.SetServersAsync(12, ["2001:db8::1"], CancellationToken.None); });
        Assert.Throws<ArgumentException>(() => { _ = configurator.SetServersAsync(12, ["not an address"], CancellationToken.None); });
    }

    [Fact]
    public void Json_store_round_trips_records_and_survives_a_corrupt_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hbpos-netfix-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "network-fix.json");
        try
        {
            var store = new JsonNetworkFixStore(path);
            Assert.Empty(store.Load());

            var record = new NetworkFixRecord(NetworkFixKind.PublicDns, 12, "Ethernet", ["192.168.1.1"], true, null, Now);
            store.Save([record]);
            var loaded = Assert.Single(store.Load());
            Assert.Equal(NetworkFixKind.PublicDns, loaded.Kind);
            Assert.Equal(12, loaded.AdapterIndex);
            Assert.Equal(["192.168.1.1"], loaded.PreviousServers);
            Assert.Equal(Now, loaded.AppliedAt);

            File.WriteAllText(path, "{ not json");
            Assert.Empty(store.Load());

            store.Save([]);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class Env
    {
        public Env()
        {
            Probe = new FixProbe(this);
            Service = new NetworkFixService(Probe, new FixDns(this), new FixProxy(this), Store, () => Now);
        }

        public LocalNetworkInfo Local { get; set; } = new(true, "Ethernet", "192.168.1.1", ["192.168.1.1"], 12);

        public DnsSettings? Dns { get; set; } = new(["192.168.1.1"], true);

        public bool Elevation { get; set; } = true;

        public Exception? SetThrows { get; set; }

        public int? ProxyValue { get; set; }

        public List<(int Index, string Servers)> Dns_SetCalls { get; } = [];

        public List<int> Dns_ResetCalls { get; } = [];

        public FakeStore Store { get; } = new();

        public INetworkProbe Probe { get; }

        public NetworkFixService Service { get; }

        private sealed class FixProbe(Env env) : INetworkProbe
        {
            public LocalNetworkInfo GetLocalNetwork() => env.Local;

            public Task<DnsProbeResult> ResolveSystemDnsAsync(string host, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<DnsProbeResult> ResolvePublicDnsAsync(string host, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<ConnectProbeResult> ConnectAsync(string address, int port, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<PingProbeResult> PingAsync(string address, int count, TimeSpan timeoutPerPing, CancellationToken cancellationToken) => throw new NotSupportedException();

            public ProxyProbeInfo GetProxyInfo(Uri target) => throw new NotSupportedException();

            public Task<ReplyProbeResult> CheckReplyAsync(Uri healthUri, bool useSystemProxy, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private sealed class FixDns(Env env) : IDnsConfigurator
        {
            public DnsSettings? GetCurrent(int adapterIndex) => env.Dns;

            public Task<bool> SetServersAsync(int adapterIndex, IReadOnlyList<string> servers, CancellationToken cancellationToken)
            {
                if (env.SetThrows is not null)
                {
                    throw env.SetThrows;
                }

                if (!env.Elevation)
                {
                    return Task.FromResult(false);
                }

                env.Dns_SetCalls.Add((adapterIndex, string.Join(",", servers)));
                return Task.FromResult(true);
            }

            public Task<bool> ResetToAutomaticAsync(int adapterIndex, CancellationToken cancellationToken)
            {
                if (!env.Elevation)
                {
                    return Task.FromResult(false);
                }

                env.Dns_ResetCalls.Add(adapterIndex);
                return Task.FromResult(true);
            }
        }

        private sealed class FixProxy(Env env) : IProxyAutoDetectStore
        {
            public int? Get() => env.ProxyValue;

            public void Set(int? value) => env.ProxyValue = value;
        }
    }

    private sealed class FakeStore : INetworkFixStore
    {
        public List<NetworkFixRecord> Records { get; private set; } = [];

        public IReadOnlyList<NetworkFixRecord> Load() => Records.ToArray();

        public void Save(IReadOnlyList<NetworkFixRecord> records) => Records = [.. records];
    }
}
