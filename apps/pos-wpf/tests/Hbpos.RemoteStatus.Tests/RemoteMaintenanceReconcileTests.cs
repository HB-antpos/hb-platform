using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hbpos.RemoteMaintenance.Setup;

namespace Hbpos.RemoteStatus.Tests;

/// <summary>
/// 启动对账的提权侧：服务器白名单、接管标记保护、写入顺序、中断恢复，以及“绝不碰无人值守密码”。
/// 用内存里的 RustDesk 命令行与服务替身，不触碰真实 Program Files 与服务控制器。
/// </summary>
public sealed class RemoteMaintenanceReconcileTests
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Repeat((byte)3, 32).ToArray());
    private static readonly RemoteMaintenanceConfig Legacy = new("hotbargain.vip:21116", "hotbargain.vip:21117", Key);
    private static readonly RemoteMaintenanceConfig Dedicated = new("rd.hotbargain.vip:21116", "rd.hotbargain.vip:21117", Key);
    private static readonly RemoteMaintenanceConfig Foreign = new("rs-ny.rustdesk.com:21116", "rs-ny.rustdesk.com:21117", Key);

    [Theory]
    [InlineData("hotbargain.vip:21116", "hotbargain.vip:21117", true)]
    [InlineData("rd.hotbargain.vip:21116", "rd.hotbargain.vip:21117", true)]
    // ID 与中继必须是同一个公司主机，不能混用。
    [InlineData("rd.hotbargain.vip:21116", "hotbargain.vip:21117", false)]
    [InlineData("hotbargain.vip:21116", "rd.hotbargain.vip:21117", false)]
    // 端口固定。
    [InlineData("rd.hotbargain.vip:21115", "rd.hotbargain.vip:21117", false)]
    [InlineData("rd.hotbargain.vip:21116", "rd.hotbargain.vip:21118", false)]
    // 后缀伪造、其他子域、大小写变体、带空白都不被信任。
    [InlineData("rd.hotbargain.vip.evil.com:21116", "rd.hotbargain.vip.evil.com:21117", false)]
    [InlineData("evilhotbargain.vip:21116", "evilhotbargain.vip:21117", false)]
    [InlineData("pos.hotbargain.vip:21116", "pos.hotbargain.vip:21117", false)]
    [InlineData("RD.hotbargain.vip:21116", "RD.hotbargain.vip:21117", false)]
    [InlineData(" rd.hotbargain.vip:21116", " rd.hotbargain.vip:21117", false)]
    [InlineData("", "", false)]
    public void 服务器白名单只接受公司两个地址(string idServer, string relayServer, bool expected)
    {
        Assert.Equal(expected, RemoteMaintenanceServerPolicy.IsTrusted(new(idServer, relayServer, Key)));
    }

    [Fact]
    public void 公钥必须是三十二字节的规范base64()
    {
        static RemoteMaintenanceConfig WithKey(string key) => new("rd.hotbargain.vip:21116", "rd.hotbargain.vip:21117", key);
        Assert.True(RemoteMaintenanceServerPolicy.IsTrusted(WithKey(Convert.ToBase64String(new byte[32]))));
        // 31 与 33 字节、空串、非 base64 都不是 ed25519 公钥。
        Assert.False(RemoteMaintenanceServerPolicy.IsTrusted(WithKey(Convert.ToBase64String(new byte[31]))));
        Assert.False(RemoteMaintenanceServerPolicy.IsTrusted(WithKey(Convert.ToBase64String(new byte[33]))));
        Assert.False(RemoteMaintenanceServerPolicy.IsTrusted(WithKey("")));
        Assert.False(RemoteMaintenanceServerPolicy.IsTrusted(WithKey("not-base64!!")));
        // base64 解码会忽略空白；带换行或空格的公钥不是规范写法，不被信任。
        Assert.False(RemoteMaintenanceServerPolicy.IsTrusted(WithKey(Convert.ToBase64String(new byte[32]) + "\n")));
        Assert.False(RemoteMaintenanceServerPolicy.IsTrusted(WithKey(" " + Convert.ToBase64String(new byte[32]))));
        Assert.False(RemoteMaintenanceServerPolicy.IsTrusted(null));
    }

    [Fact]
    public void 安装器校验同时接受新旧两个公司地址()
    {
        WindowsRemoteMaintenanceInstaller.ValidateConfig(Legacy);
        WindowsRemoteMaintenanceInstaller.ValidateConfig(Dedicated);
        Assert.Throws<InvalidDataException>(() =>
            WindowsRemoteMaintenanceInstaller.ValidateConfig(new("evil.example.com:21116", "evil.example.com:21117", Key)));
    }

    [Fact]
    public async Task 从历史地址切换到专用域名_先写服务配置再改标记最后重启()
    {
        using var rig = new Rig(Legacy);

        await rig.Installer.ReconcileServerAsync(Dedicated);

        Assert.Equal(Dedicated, rig.ReadMarker());
        Assert.Equal(Dedicated.IdServer, rig.RustDesk.Options["custom-rendezvous-server"]);
        Assert.Equal(Dedicated.RelayServer, rig.RustDesk.Options["relay-server"]);
        Assert.Equal(Key, rig.RustDesk.Options["key"]);
        // 先写服务配置（此时标记仍是旧值），写完后才改标记，改完标记才重启。
        Assert.Equal(new[] { "config", "stop", "start" }, rig.OrderedEvents(["config", "stop", "start"]));
        Assert.Equal(Legacy, rig.RustDesk.MarkerSeenAtConfig);
        Assert.Equal(Dedicated, rig.Service.MarkerSeenAtStop);
    }

    [Fact]
    public async Task 对账绝不设置无人值守密码也不重写无人值守选项()
    {
        // 提权 helper 不能把普通用户可写的 journal 里的密码写成 RustDesk 永久密码。
        using var rig = new Rig(Legacy);

        await rig.Installer.ReconcileServerAsync(Dedicated);

        Assert.DoesNotContain(rig.RustDesk.Commands, x => x.StartsWith("--password "));
        Assert.DoesNotContain(rig.RustDesk.Commands, x => Regex.IsMatch(x, "^--option \"[^\"]*\" \"[^\"]*\"$"));
        // 受管选项原样保留。
        Assert.Equal("use-permanent-password", rig.RustDesk.Options["verification-method"]);
        Assert.Equal("password", rig.RustDesk.Options["approve-mode"]);
        Assert.Equal("N", rig.RustDesk.Options["allow-only-conn-window-open"]);
    }

    [Fact]
    public async Task 配置已经一致时不重写配置_但仍重启一次()
    {
        // helper 只在 journal 尚未记录目标配置时被调用，说明上次可能在“标记已改、重启未做”时中断。
        using var rig = new Rig(Dedicated);

        await rig.Installer.ReconcileServerAsync(Dedicated);

        Assert.DoesNotContain(rig.RustDesk.Commands, x => x.StartsWith("--config "));
        Assert.Equal(new[] { "stop", "start" }, rig.OrderedEvents(["config", "stop", "start"]));
        Assert.Equal(Dedicated, rig.ReadMarker());
    }

    [Fact]
    public async Task 上次在写完服务配置后中断_重跑只补写标记并重启_不重复写配置()
    {
        // 服务里已是专用域名，但接管标记还停在历史地址。
        using var rig = new Rig(Legacy, actual: Dedicated);

        await rig.Installer.ReconcileServerAsync(Dedicated);

        Assert.Equal(Dedicated, rig.ReadMarker());
        Assert.DoesNotContain(rig.RustDesk.Commands, x => x.StartsWith("--config "));
        Assert.Equal(new[] { "stop", "start" }, rig.OrderedEvents(["config", "stop", "start"]));
        Assert.Equal(Dedicated, rig.Service.MarkerSeenAtStop);
    }

    [Fact]
    public async Task 半成品_标记是目标值但服务仍是旧地址_重跑会重新写入服务配置()
    {
        // 写入中途失败或崩溃后，中心又回滚到标记值：标记==目标，服务实际是另一个公司地址。
        using var rig = new Rig(Dedicated, actual: Legacy);

        await rig.Installer.ReconcileServerAsync(Dedicated);

        Assert.Equal(Dedicated.IdServer, rig.RustDesk.Options["custom-rendezvous-server"]);
        Assert.Equal(Dedicated, rig.ReadMarker());
        Assert.Equal(new[] { "config", "stop", "start" }, rig.OrderedEvents(["config", "stop", "start"]));
    }

    [Fact]
    public async Task 对账可以原路改回历史地址()
    {
        using var rig = new Rig(Dedicated);

        await rig.Installer.ReconcileServerAsync(Legacy);

        Assert.Equal(Legacy, rig.ReadMarker());
        Assert.Equal(Legacy.IdServer, rig.RustDesk.Options["custom-rendezvous-server"]);
    }

    [Fact]
    public async Task 没有接管标记时绝不改动外部RustDesk()
    {
        using var rig = new Rig(marker: null, actual: Foreign);

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() => rig.Installer.ReconcileServerAsync(Dedicated));

        Assert.Equal(RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged, error.Error);
        rig.AssertUntouched();
    }

    [Fact]
    public async Task 接管标记不是公司配置时拒绝()
    {
        using var rig = new Rig(new("evil.example.com:21116", "evil.example.com:21117", Key));

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() => rig.Installer.ReconcileServerAsync(Dedicated));

        Assert.Equal(RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged, error.Error);
        rig.AssertUntouched();
    }

    [Fact]
    public async Task 接管标记损坏时按未接管处理()
    {
        using var rig = new Rig(Legacy, rawMarker: "{ not json");

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() => rig.Installer.ReconcileServerAsync(Dedicated));

        Assert.Equal(RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged, error.Error);
        rig.AssertUntouched();
    }

    [Fact]
    public async Task 服务当前配置不是公司地址说明被外部改过_不覆盖()
    {
        using var rig = new Rig(Legacy, actual: Foreign);

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() => rig.Installer.ReconcileServerAsync(Dedicated));

        Assert.Equal(RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged, error.Error);
        rig.AssertUntouched();
    }

    [Fact]
    public async Task 读不到受管选项时不写配置_避免IPC不通时写进管理员本地配置()
    {
        // IPC 不通时 CLI 回退读管理员本地配置，那里没有这三项无人值守选项。
        using var rig = new Rig(Legacy, managedOptionsPresent: false);

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() => rig.Installer.ReconcileServerAsync(Dedicated));

        Assert.Equal(RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged, error.Error);
        rig.AssertUntouched();
    }

    [Fact]
    public async Task 对账不允许更换公钥()
    {
        using var rig = new Rig(Legacy);
        var otherKey = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(() => rig.Installer.ReconcileServerAsync(Dedicated with { PublicKey = otherKey }));

        rig.AssertUntouched();
    }

    [Fact]
    public async Task 目标配置不在白名单时拒绝()
    {
        using var rig = new Rig(Legacy);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            rig.Installer.ReconcileServerAsync(new("evil.example.com:21116", "evil.example.com:21117", Key)));

        rig.AssertUntouched();
    }

    [Fact]
    public async Task RustDesk未安装时交给完整重装流程()
    {
        using var rig = new Rig(Legacy, serviceStatus: null);

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() => rig.Installer.ReconcileServerAsync(Dedicated));

        Assert.Equal(RemoteMaintenanceSetupError.RustDeskSetupIncomplete, error.Error);
        rig.AssertUntouched();
    }

    [Fact]
    public async Task 服务停止时先拉起再读写配置()
    {
        using var rig = new Rig(Legacy, serviceStatus: "stopped");

        await rig.Installer.ReconcileServerAsync(Dedicated);

        Assert.Equal(Dedicated, rig.ReadMarker());
        Assert.Equal("running", rig.Service.Status);
        // 第一次 start 是拉起已停止的服务，必须发生在写入服务器配置之前。
        Assert.True(rig.Events.IndexOf("start") < rig.Events.IndexOf("config"));
    }

    [Fact]
    public async Task 服务配置写入未能读回时标记保持旧值_不重启_并尽力还原服务配置()
    {
        using var rig = new Rig(Legacy, ignoreConfigWrites: true);

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() => rig.Installer.ReconcileServerAsync(Dedicated));

        Assert.Equal(RemoteMaintenanceSetupError.RustDeskConfigurationFailed, error.Error);
        Assert.Equal(Legacy, rig.ReadMarker());
        Assert.DoesNotContain(rig.Events, x => x is "stop" or "start");
        // 失败后又发了一次 --config 还原到对账前的旧地址。
        var configs = rig.RustDesk.Commands.Where(x => x.StartsWith("--config ")).ToArray();
        Assert.Equal(2, configs.Length);
        Assert.Contains("host=hotbargain.vip:21116", configs[1]);
        Assert.DoesNotContain("rd.hotbargain.vip", configs[1]);
    }

    [Fact]
    public async Task 写入后IPC回退导致读不到受管选项时同样失败且不改标记()
    {
        // 写入期间 IPC 掉线：CLI 把新地址写进了管理员本地配置，读回时受管选项为空，必须判定失败。
        using var rig = new Rig(Legacy, fallbackAfterConfig: true);

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() => rig.Installer.ReconcileServerAsync(Dedicated));

        Assert.Equal(RemoteMaintenanceSetupError.RustDeskConfigurationFailed, error.Error);
        Assert.Equal(Legacy, rig.ReadMarker());
        Assert.DoesNotContain(rig.Events, x => x is "stop" or "start");
    }

    [Fact]
    public async Task 重启时启动失败一次会重试一次()
    {
        using var rig = new Rig(Legacy, startFailures: 1);

        await rig.Installer.ReconcileServerAsync(Dedicated);

        Assert.Equal("running", rig.Service.Status);
    }

    [Fact]
    public async Task 重启时启动连续失败给出安装未完成故障码()
    {
        using var rig = new Rig(Legacy, startFailures: 2);

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() => rig.Installer.ReconcileServerAsync(Dedicated));

        Assert.Equal(RemoteMaintenanceSetupError.RustDeskSetupIncomplete, error.Error);
        // 配置与标记已经落盘，服务是自动启动类型，下次对账或重启会继续。
        Assert.Equal(Dedicated, rig.ReadMarker());
    }

    [Fact]
    public async Task 对账后目录里只有接管标记()
    {
        using var rig = new Rig(Legacy);

        await rig.Installer.ReconcileServerAsync(Dedicated);

        Assert.Equal(new[] { "managed-server.json" }, Directory.GetFiles(Path.GetDirectoryName(rig.MarkerPath)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task 受保护位置校验在生产配置下生效()
    {
        // 默认 EnforceProtectedLocation=true：RustDesk 不在 Program Files 下时必须拒绝。
        if (!OperatingSystem.IsWindows()) return;
        using var rig = new Rig(Legacy);
        var strict = new WindowsRemoteMaintenanceInstaller(rig.RustDesk, rig.Service)
        { Paths = new(rig.Paths.RustDeskExecutable, rig.MarkerPath) };

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() => strict.ReconcileServerAsync(Dedicated));

        Assert.Equal(RemoteMaintenanceSetupError.InstallationLocationInvalid, error.Error);
        rig.AssertUntouched();
    }

    [Fact]
    public async Task 安装前外部检查接受服务器切换期间仍按旧公司地址运行的受管RustDesk()
    {
        // 标记是可信的旧公司地址，中心已切到新地址：中断后的重装不能被误判为外部 RustDesk。
        using var rig = new Rig(Legacy);

        await rig.Installer.EnsureNoForeignRustDeskConfigAsync(Dedicated, CancellationToken.None);

        rig.AssertUntouched();
    }

    [Fact]
    public async Task 安装前外部检查仍拒绝非公司地址()
    {
        using var rig = new Rig(Legacy, actual: Foreign);

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() =>
            rig.Installer.EnsureNoForeignRustDeskConfigAsync(Dedicated, CancellationToken.None));

        Assert.Equal(RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged, error.Error);
    }

    [Fact]
    public async Task 安装前外部检查在服务停止且有可信标记时才拉起服务()
    {
        using var rig = new Rig(Legacy, serviceStatus: "stopped");

        await rig.Installer.EnsureNoForeignRustDeskConfigAsync(Dedicated, CancellationToken.None);
        Assert.Equal("running", rig.Service.Status);

        using var noMarker = new Rig(marker: null, actual: Legacy, serviceStatus: "stopped");
        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() =>
            noMarker.Installer.EnsureNoForeignRustDeskConfigAsync(Dedicated, CancellationToken.None));
        Assert.Equal(RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged, error.Error);
        Assert.Equal("stopped", noMarker.Service.Status);
    }

    [Fact]
    public async Task 旧版journal没有对账字段时仍可读取()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hbpos-reconcile-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "remote-maintenance.journal");
            var journal = new RemoteMaintenanceJournal(path, new PrefixProtector());
            await journal.WriteAsync(new RemoteMaintenanceJournalState(
                Guid.NewGuid(), Guid.NewGuid(), RemoteMaintenanceOperationState.Committed, "id", "1.4.9",
                "protected:pw", null, null, DateTimeOffset.UtcNow, Legacy, PendingConfig: Dedicated, ReconcileFailureCount: 3));
            // 模拟 1.0.84 及更早版本写下的 journal：没有这两个字段。
            var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            node.Remove("pendingConfig");
            node.Remove("reconcileFailureCount");
            await File.WriteAllTextAsync(path, node.ToJsonString());

            var state = await journal.ReadAsync();

            Assert.NotNull(state);
            Assert.Equal(Legacy, state.Config);
            Assert.Null(state.PendingConfig);
            Assert.Equal(0, state.ReconcileFailureCount);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class PrefixProtector : IRemoteMaintenanceSecretProtector
    {
        public string Protect(string plaintext) => "protected:" + plaintext;
        public string? Unprotect(string protectedValue) => protectedValue.StartsWith("protected:", StringComparison.Ordinal) ? protectedValue[10..] : null;
    }

    /// <summary>一套带临时目录的“本机”：接管标记、RustDesk 命令行状态、服务状态与事件时间线。</summary>
    private sealed class Rig : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hbpos-reconcile-" + Guid.NewGuid().ToString("N"));
        private readonly string? _initialMarker;

        // marker 为 null 表示没有接管标记；actual 缺省与 marker 相同，表示服务确实按标记运行。
        public Rig(RemoteMaintenanceConfig? marker, RemoteMaintenanceConfig? actual = null, string? serviceStatus = "running",
            bool ignoreConfigWrites = false, int startFailures = 0, string? rawMarker = null,
            bool managedOptionsPresent = true, bool fallbackAfterConfig = false)
        {
            Directory.CreateDirectory(Path.Combine(_root, "RustDesk"));
            Directory.CreateDirectory(Path.Combine(_root, "RemoteStatus"));
            var executable = Path.Combine(_root, "RustDesk", "rustdesk.exe");
            File.WriteAllBytes(executable, [1]);
            MarkerPath = Path.Combine(_root, "RemoteStatus", "managed-server.json");
            if (rawMarker is not null) File.WriteAllText(MarkerPath, rawMarker);
            else if (marker is not null) File.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker));

            RustDesk = new FakeRustDesk(Events, ignoreConfigWrites, fallbackAfterConfig, ReadMarkerOrNull);
            var running = actual ?? marker;
            if (running is not null) RustDesk.Apply(running, managedOptionsPresent);
            Service = new FakeService(Events, serviceStatus, startFailures, ReadMarkerOrNull);
            Paths = new(executable, MarkerPath, EnforceProtectedLocation: false);
            Installer = new WindowsRemoteMaintenanceInstaller(RustDesk, Service)
            {
                Paths = Paths,
                PollInterval = TimeSpan.Zero,
                DaemonReadyTimeout = TimeSpan.Zero
            };
            // 构造阶段的预置写入不算对账动作。
            Events.Clear();
            RustDesk.Commands.Clear();
            _initialMarker = File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath) : null;
        }

        public List<string> Events { get; } = [];
        public string MarkerPath { get; }
        public WindowsRemoteMaintenanceInstaller.ManagedPaths Paths { get; }
        public FakeRustDesk RustDesk { get; }
        public FakeService Service { get; }
        public WindowsRemoteMaintenanceInstaller Installer { get; }

        public RemoteMaintenanceConfig? ReadMarker() =>
            File.Exists(MarkerPath) ? JsonSerializer.Deserialize<RemoteMaintenanceConfig>(File.ReadAllText(MarkerPath)) : null;

        // 供替身在被调用的瞬间记录接管标记，用来证明“先写服务配置、后改标记、再重启”的顺序。
        private RemoteMaintenanceConfig? ReadMarkerOrNull()
        {
            try { return ReadMarker(); } catch (JsonException) { return null; }
        }

        // 取出事件时间线里属于 names 的事件并保持发生顺序，便于断言“先 A 后 B”。
        public string[] OrderedEvents(string[] names) => Events.Where(e => names.Contains(e)).ToArray();

        // 被拒绝的对账：没有任何写入、没有重启，标记原样不动。
        public void AssertUntouched()
        {
            Assert.DoesNotContain(RustDesk.Commands, x => x.StartsWith("--config "));
            Assert.DoesNotContain(Events, x => x is "config" or "stop" or "start");
            Assert.Equal(_initialMarker, File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath) : null);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    /// <summary>内存里的 RustDesk 命令行：--config/--option 写入、--option 读取、--password 确认。</summary>
    private sealed class FakeRustDesk(List<string> events, bool ignoreConfigWrites, bool fallbackAfterConfig,
        Func<RemoteMaintenanceConfig?> markerReader) : IRemoteMaintenanceCommandRunner
    {
        private static readonly string[] ManagedKeys = ["verification-method", "approve-mode", "allow-only-conn-window-open"];
        private bool _configWritten;
        public RemoteMaintenanceConfig? MarkerSeenAtConfig { get; private set; }
        public Dictionary<string, string> Options { get; } = [];
        public List<string> Commands { get; } = [];

        public void Apply(RemoteMaintenanceConfig config, bool managedOptionsPresent = true)
        {
            Options["custom-rendezvous-server"] = config.IdServer;
            Options["relay-server"] = config.RelayServer;
            Options["key"] = config.PublicKey;
            if (!managedOptionsPresent) return;
            Options["verification-method"] = "use-permanent-password";
            Options["approve-mode"] = "password";
            Options["allow-only-conn-window-open"] = "N";
        }

        public Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
        {
            Commands.Add(arguments);
            var quoted = Regex.Matches(arguments, "\"([^\"]*)\"").Select(x => x.Groups[1].Value).ToArray();
            if (arguments.StartsWith("--config ", StringComparison.Ordinal))
            {
                events.Add("config");
                MarkerSeenAtConfig = markerReader();
                _configWritten = true;
                if (!ignoreConfigWrites)
                {
                    var fields = quoted[0].Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x[1]);
                    Options["custom-rendezvous-server"] = fields["host"];
                    Options["relay-server"] = fields["relay"];
                    Options["key"] = fields["key"];
                }
            }
            else if (arguments.StartsWith("--option ", StringComparison.Ordinal) && quoted.Length == 2)
            {
                Options[quoted[0]] = quoted[1];
            }

            return Task.FromResult(0);
        }

        public Task<RemoteMaintenanceCommandResult> RunWithOutputAsync(string fileName, string arguments, CancellationToken cancellationToken)
        {
            Commands.Add(arguments);
            if (arguments.StartsWith("--password ", StringComparison.Ordinal))
                return Task.FromResult(new RemoteMaintenanceCommandResult(0, "Done!\r\n", ""));
            var key = Regex.Match(arguments, "\"([^\"]*)\"").Groups[1].Value;
            // 模拟写入期间 IPC 掉线：新地址落进了管理员本地配置，而受管选项在本地配置里不存在。
            var value = fallbackAfterConfig && _configWritten && ManagedKeys.Contains(key) ? "" : Options.GetValueOrDefault(key, "");
            return Task.FromResult(new RemoteMaintenanceCommandResult(0, value + "\r\n", ""));
        }
    }

    private sealed class FakeService(List<string> events, string? status, int startFailures, Func<RemoteMaintenanceConfig?> markerReader) : IRemoteMaintenanceServiceControl
    {
        private int _remainingStartFailures = startFailures;
        public RemoteMaintenanceConfig? MarkerSeenAtStop { get; private set; }
        public string? Status { get; private set; } = status;
        public Task<string?> QueryAsync(string serviceName, CancellationToken cancellationToken) => Task.FromResult(Status);
        public Task<int> CreateOrUpdateAsync(string serviceName, string binaryPath, string accountName, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<int> StartAsync(string serviceName, CancellationToken cancellationToken)
        {
            if (Status == "running") return Task.FromResult(0);
            events.Add("start");
            if (_remainingStartFailures > 0)
            {
                _remainingStartFailures--;
                return Task.FromResult(1053);
            }

            Status = "running";
            return Task.FromResult(0);
        }

        public Task<int> StopAsync(string serviceName, CancellationToken cancellationToken)
        {
            if (Status is null or "stopped") return Task.FromResult(0);
            events.Add("stop");
            MarkerSeenAtStop = markerReader();
            Status = "stopped";
            return Task.FromResult(0);
        }
    }
}
