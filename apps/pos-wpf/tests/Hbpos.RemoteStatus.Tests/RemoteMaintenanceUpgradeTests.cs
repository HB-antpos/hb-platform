using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hbpos.RemoteMaintenance.Setup;

namespace Hbpos.RemoteStatus.Tests;

/// <summary>
/// RustDesk 升级（SYSTEM 代理执行）与 WPF 升级时同步状态服务：只装白名单内的构建、装完核对随包 custom.txt、
/// 不碰无人值守密码、服务器被重置时按升级前的值写回。用临时目录模拟 Program Files，命令行与服务都是替身。
/// </summary>
public sealed class RemoteMaintenanceUpgradeTests : IDisposable
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Repeat((byte)3, 32).ToArray());
    private static readonly RemoteMaintenanceConfig Dedicated = new("rd.hotbargain.vip:21116", "rd.hotbargain.vip:21117", Key);
    private static readonly byte[] CustomClient = "signed-custom-client"u8.ToArray();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hbpos-upgrade-" + Guid.NewGuid().ToString("N"));

    public RemoteMaintenanceUpgradeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task 官方版升级到定制构建_装上custom文件且不碰密码与受管选项()
    {
        var rig = new Rig(_root);

        await rig.Installer.UpgradeRustDeskAsync(rig.ArtifactPath, rig.Sha256);

        Assert.Contains("--silent-install", rig.RustDesk.InstallerRuns);
        Assert.Equal(CustomClient, File.ReadAllBytes(rig.CustomClientPath));
        Assert.DoesNotContain(rig.RustDesk.Commands, x => x.StartsWith("--password ", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.RustDesk.Commands, x => Regex.IsMatch(x, "^--option \"(verification-method|approve-mode|allow-only-conn-window-open)\" \""));
        // 服务器没被重置，不需要重写配置。
        Assert.DoesNotContain(rig.RustDesk.Commands, x => x.StartsWith("--config ", StringComparison.Ordinal));
        Assert.Equal(Dedicated.IdServer, rig.RustDesk.Options["custom-rendezvous-server"]);
        // 暂存的安装包执行后删除。
        Assert.Empty(Directory.EnumerateFiles(rig.StagingDirectory));
    }

    [Fact]
    public async Task 已是目标构建时不重装()
    {
        var rig = new Rig(_root);
        File.WriteAllBytes(rig.CustomClientPath, CustomClient);

        await rig.Installer.UpgradeRustDeskAsync(rig.ArtifactPath, rig.Sha256);

        Assert.Empty(rig.RustDesk.InstallerRuns);
    }

    [Fact]
    public async Task 白名单外的构建在触碰本机前拒绝()
    {
        var rig = new Rig(_root);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            rig.Installer.UpgradeRustDeskAsync(rig.ArtifactPath, new string('a', 64)));

        Assert.Empty(rig.RustDesk.Commands);
        Assert.Empty(rig.RustDesk.InstallerRuns);
    }

    [Fact]
    public async Task 安装包内容与白名单哈希不符时不执行()
    {
        var rig = new Rig(_root);
        // 大小相同、内容不同：模拟下载后被替换。
        var bytes = File.ReadAllBytes(rig.ArtifactPath);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(rig.ArtifactPath, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => rig.Installer.UpgradeRustDeskAsync(rig.ArtifactPath, rig.Sha256));

        Assert.Empty(rig.RustDesk.InstallerRuns);
        Assert.Empty(Directory.EnumerateFiles(rig.StagingDirectory));
    }

    [Theory]
    [InlineData(@"\\attacker\share\rustdesk.exe")]
    [InlineData(@"\\?\C:\Users\x\rustdesk.exe")]
    [InlineData("relative\\rustdesk.exe")]
    [InlineData("C:\\Users\\x\\rustdesk.txt")]
    public void 升级包路径只接受本机绝对路径的exe(string path)
    {
        Assert.Throws<InvalidDataException>(() => WindowsRemoteMaintenanceInstaller.ValidateLocalArtifactPath(path));
    }

    [Fact]
    public async Task 没有接管标记的RustDesk不升级()
    {
        var rig = new Rig(_root, marker: false);

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() =>
            rig.Installer.UpgradeRustDeskAsync(rig.ArtifactPath, rig.Sha256));

        Assert.Equal(RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged, error.Error);
        Assert.Empty(rig.RustDesk.InstallerRuns);
    }

    [Fact]
    public async Task 安装后没有custom文件按配置失败处理()
    {
        var rig = new Rig(_root);
        rig.RustDesk.WriteCustomClientOnInstall = false;

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() =>
            rig.Installer.UpgradeRustDeskAsync(rig.ArtifactPath, rig.Sha256));

        Assert.Equal(RemoteMaintenanceSetupError.RustDeskConfigurationFailed, error.Error);
    }

    [Fact]
    public async Task 安装后受管选项丢失按配置失败处理且不尝试设密码()
    {
        var rig = new Rig(_root);
        rig.RustDesk.WipeManagedOptionsOnInstall = true;

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() =>
            rig.Installer.UpgradeRustDeskAsync(rig.ArtifactPath, rig.Sha256));

        Assert.Equal(RemoteMaintenanceSetupError.RustDeskConfigurationFailed, error.Error);
        Assert.DoesNotContain(rig.RustDesk.Commands, x => x.StartsWith("--password ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 安装后服务器被重置时按升级前的值写回并补回api服务器()
    {
        var rig = new Rig(_root);
        rig.RustDesk.Options["api-server"] = "https://hotbargain.vip/api/rustdesk";
        rig.RustDesk.ResetServersOnInstall = true;

        await rig.Installer.UpgradeRustDeskAsync(rig.ArtifactPath, rig.Sha256);

        Assert.Equal(Dedicated.IdServer, rig.RustDesk.Options["custom-rendezvous-server"]);
        Assert.Equal(Dedicated.RelayServer, rig.RustDesk.Options["relay-server"]);
        Assert.Equal(Key, rig.RustDesk.Options["key"]);
        Assert.Equal("https://hotbargain.vip/api/rustdesk", rig.RustDesk.Options["api-server"]);
    }

    [Fact]
    public async Task 同步状态服务_程序不同才替换并恢复运行()
    {
        var rig = new Rig(_root);
        File.WriteAllBytes(rig.StatusAgentPath, [1, 2, 3]);
        var source = Path.Combine(_root, "new-agent.exe");
        File.WriteAllBytes(source, [4, 5, 6]);

        Assert.True(await rig.Installer.SyncStatusAgentAsync(source));

        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(rig.StatusAgentPath));
        Assert.Equal(new[] { "stop:HBPOSRemoteStatus", "start:HBPOSRemoteStatus" }, rig.Service.Events);
        // 再同步一次：内容相同，什么也不做。
        Assert.False(await rig.Installer.SyncStatusAgentAsync(source));
        Assert.Equal(2, rig.Service.Events.Count);
    }

    [Fact]
    public async Task 同步状态服务_没装状态服务时不动()
    {
        var rig = new Rig(_root);
        rig.Service.StatusAgentInstalled = false;
        var source = Path.Combine(_root, "new-agent.exe");
        File.WriteAllBytes(source, [4, 5, 6]);

        Assert.False(await rig.Installer.SyncStatusAgentAsync(source));
        Assert.Empty(rig.Service.Events);
    }

    [Fact]
    public void 升级请求只接受白名单内的构建()
    {
        const string hb1 = "54425f81db1433646d6ce0f440b90993770b3c15c75ebaac927a5bccce4f4884";
        var request = RemoteMaintenanceAgentUpgradeRequest.Create(hb1.ToUpperInvariant(), @"C:\Users\pos\AppData\Local\HBPOS\x.exe");

        Assert.True(RemoteMaintenanceAgentUpgradeRequest.TryDecode(request.Encode(), out var decoded));
        Assert.Equal(hb1, decoded!.Sha256);
        Assert.Equal(request.ArtifactPath, decoded.ArtifactPath);
        // 换服务器的解码器不认升级请求，反之亦然。
        Assert.False(RemoteMaintenanceAgentRequest.TryDecode(request.Encode(), out _));
        Assert.False(RemoteMaintenanceAgentUpgradeRequest.TryDecode(RemoteMaintenanceAgentRequest.CreateReconcile(Dedicated).Encode(), out _));

        var untrusted = RemoteMaintenanceAgentUpgradeRequest.Create(new string('a', 64), @"C:\x.exe");
        Assert.False(RemoteMaintenanceAgentUpgradeRequest.TryDecode(untrusted.Encode(), out _));
    }

    [Fact]
    public void 代理收到升级请求时交给升级逻辑并写结果()
    {
        const string hb1 = "54425f81db1433646d6ce0f440b90993770b3c15c75ebaac927a5bccce4f4884";
        var helperPath = Path.Combine(_root, "Hbpos.RemoteMaintenance.Setup.exe");
        (string Path, string Sha)? called = null;
        var agent = new RemoteMaintenanceAgent(helperPath, (_, _) => throw new InvalidOperationException("不该换服务器"),
            upgrade: (path, sha, _) => { called = (path, sha); return Task.CompletedTask; });
        var request = RemoteMaintenanceAgentUpgradeRequest.Create(hb1, @"C:\Users\pos\x.exe");

        Assert.Equal(0, agent.Run(request.Encode()));

        Assert.NotNull(called);
        Assert.Equal(@"C:\Users\pos\x.exe", called.Value.Path);
        Assert.Equal(hb1, called.Value.Sha);
        var status = RemoteMaintenanceAgentStatus.TryParse(
            File.ReadAllText(RemoteMaintenanceAgentLayout.StatusFile(helperPath, request.RequestId)), request.RequestId);
        Assert.True(status!.Completed);
        // 没配置升级逻辑的旧调用方把升级请求当无效请求。
        Assert.Equal(RemoteMaintenanceAgent.InvalidRequestExitCode,
            new RemoteMaintenanceAgent(helperPath, (_, _) => Task.CompletedTask).Run(request.Encode()));
    }

    [Fact]
    public void 白名单里的公司构建带custom文件哈希()
    {
        Assert.True(WindowsRemoteMaintenanceInstaller.TryGetTrustedRustDeskBuild(
            "54425F81DB1433646D6CE0F440B90993770B3C15C75EBAAC927A5BCCCE4F4884", out var hb1));
        Assert.Equal("1.4.9-hb1", hb1.Version);
        Assert.Equal(24351744, hb1.Size);
        Assert.Equal("bdc2b18780d49bbacb1888dadd10ed881fb7c861ea1a7da7504c7ec1bb48cbc5", hb1.CustomClientSha256);
        Assert.True(WindowsRemoteMaintenanceInstaller.TryGetTrustedRustDeskBuild(
            "212b72a745d841f7730f077ce173281fea526893945682b940c1206b6377a72c", out var hb2));
        Assert.Equal("1.5.0-hb2", hb2.Version);
        Assert.Equal(25765376, hb2.Size);
        Assert.Equal("f53058f04d05c7f29746c5fa64df81fff81b318b9d76501a93a35dc8272632b1", hb2.CustomClientSha256);
        Assert.True(WindowsRemoteMaintenanceInstaller.TryGetTrustedRustDeskBuild(
            "eaedeb0088e687bf46f7c46a9c6ea5493ce51f3134dfd6acbedb47b5b9136274", out var official));
        Assert.Null(official.CustomClientSha256);
        Assert.False(WindowsRemoteMaintenanceInstaller.TryGetTrustedRustDeskBuild(null, out _));
    }

    /// <summary>临时目录里的一台“受管机器”：RustDesk 程序、接管标记、待升级安装包（已加进测试白名单）、状态服务程序。</summary>
    private sealed class Rig
    {
        public Rig(string root, bool marker = true)
        {
            var rustDeskDirectory = Path.Combine(root, "RustDesk");
            var statusDirectory = Path.Combine(root, "RemoteStatus");
            StagingDirectory = Path.Combine(root, "staging");
            Directory.CreateDirectory(rustDeskDirectory);
            Directory.CreateDirectory(statusDirectory);
            Directory.CreateDirectory(StagingDirectory);
            var executable = Path.Combine(rustDeskDirectory, "rustdesk.exe");
            File.WriteAllBytes(executable, [1]);
            var markerPath = Path.Combine(statusDirectory, "managed-server.json");
            if (marker) File.WriteAllText(markerPath, JsonSerializer.Serialize(Dedicated));
            StatusAgentPath = Path.Combine(statusDirectory, "Hbpos.RemoteStatus.exe");
            File.WriteAllBytes(StatusAgentPath, [9]);

            ArtifactPath = Path.Combine(root, "download", "rustdesk-hb.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(ArtifactPath)!);
            File.WriteAllBytes(ArtifactPath, RandomNumberGenerator.GetBytes(4096));
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ArtifactPath))).ToLowerInvariant();

            var paths = new WindowsRemoteMaintenanceInstaller.ManagedPaths(
                executable, markerPath, EnforceProtectedLocation: false, StagingDirectory, StatusAgentPath);
            CustomClientPath = paths.CustomClientConfig;
            RustDesk = new FakeRustDesk(CustomClientPath);
            RustDesk.Apply(Dedicated);
            Service = new FakeService();
            Installer = new WindowsRemoteMaintenanceInstaller(RustDesk, Service)
            {
                Paths = paths,
                PollInterval = TimeSpan.Zero,
                DaemonReadyTimeout = TimeSpan.Zero,
                TrustedBuilds = new Dictionary<string, TrustedRustDeskBuild>
                {
                    [Sha256] = new("1.4.9-test", 4096, Convert.ToHexString(SHA256.HashData(CustomClient)).ToLowerInvariant())
                }
            };
        }

        public string ArtifactPath { get; }
        public string Sha256 { get; }
        public string StagingDirectory { get; }
        public string CustomClientPath { get; }
        public string StatusAgentPath { get; }
        public FakeRustDesk RustDesk { get; }
        public FakeService Service { get; }
        public WindowsRemoteMaintenanceInstaller Installer { get; }
    }

    /// <summary>内存里的 RustDesk 命令行；执行暂存安装包时按开关模拟安装脚本的各种结果。</summary>
    private sealed class FakeRustDesk(string customClientPath) : IRemoteMaintenanceCommandRunner
    {
        private static readonly string[] ManagedKeys = ["verification-method", "approve-mode", "allow-only-conn-window-open"];
        public Dictionary<string, string> Options { get; } = [];
        public List<string> Commands { get; } = [];
        public List<string> InstallerRuns { get; } = [];
        public bool WriteCustomClientOnInstall { get; set; } = true;
        public bool ResetServersOnInstall { get; set; }
        public bool WipeManagedOptionsOnInstall { get; set; }

        public void Apply(RemoteMaintenanceConfig config)
        {
            Options["custom-rendezvous-server"] = config.IdServer;
            Options["relay-server"] = config.RelayServer;
            Options["key"] = config.PublicKey;
            Options["verification-method"] = "use-permanent-password";
            Options["approve-mode"] = "password";
            Options["allow-only-conn-window-open"] = "N";
        }

        public Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
        {
            if (arguments == "--silent-install")
            {
                InstallerRuns.Add(arguments);
                if (WriteCustomClientOnInstall) File.WriteAllBytes(customClientPath, CustomClient);
                if (ResetServersOnInstall)
                    foreach (var key in new[] { "custom-rendezvous-server", "relay-server", "key", "api-server" }) Options.Remove(key);
                if (WipeManagedOptionsOnInstall)
                    foreach (var key in ManagedKeys) Options.Remove(key);
                return Task.FromResult(0);
            }

            Commands.Add(arguments);
            var quoted = Regex.Matches(arguments, "\"([^\"]*)\"").Select(x => x.Groups[1].Value).ToArray();
            if (arguments.StartsWith("--config ", StringComparison.Ordinal))
            {
                var fields = quoted[0].Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x[1]);
                Options["custom-rendezvous-server"] = fields["host"];
                Options["relay-server"] = fields["relay"];
                Options["key"] = fields["key"];
                // 与 RustDesk 1.4.9 一致：--config 按 api= 覆盖 api-server，缺省即清空。
                if (fields.TryGetValue("api", out var api)) Options["api-server"] = api;
                else Options.Remove("api-server");
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
            var key = Regex.Match(arguments, "\"([^\"]*)\"").Groups[1].Value;
            return Task.FromResult(new RemoteMaintenanceCommandResult(0, Options.GetValueOrDefault(key, "") + "\r\n", ""));
        }
    }

    private sealed class FakeService : IRemoteMaintenanceServiceControl
    {
        public bool StatusAgentInstalled { get; set; } = true;
        public List<string> Events { get; } = [];

        public Task<string?> QueryAsync(string serviceName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(serviceName == "HBPOSRemoteStatus" && !StatusAgentInstalled ? null : "running");

        public Task<int> CreateOrUpdateAsync(string serviceName, string binaryPath, string accountName, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<int> StartAsync(string serviceName, CancellationToken cancellationToken)
        {
            // RustDesk 服务一直在跑，只记录状态服务的启停，便于断言同步顺序。
            if (serviceName == "HBPOSRemoteStatus") Events.Add("start:" + serviceName);
            return Task.FromResult(0);
        }

        public Task<int> StopAsync(string serviceName, CancellationToken cancellationToken)
        {
            if (serviceName == "HBPOSRemoteStatus") Events.Add("stop:" + serviceName);
            return Task.FromResult(0);
        }
    }
}
