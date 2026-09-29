using System.Net;
using Hbpos.RemoteMaintenance.Setup;

namespace Hbpos.RemoteStatus.Tests;

public sealed class RemoteMaintenanceSafetyTests
{
    private static readonly RemoteMaintenanceConfig Config = new("hotbargain.vip:21116", "hotbargain.vip:21117", Convert.ToBase64String(new byte[32]));

    [Fact]
    public async Task 配置密码分开调用且读回实际服务选项()
    {
        var runner = new CliRunner(Config);
        var installer = new WindowsRemoteMaintenanceInstaller(runner, null!);
        await installer.ConfigureRustDeskAsync("unused.exe", Config, "test-password-only", CancellationToken.None);
        Assert.Single(runner.Commands.Where(x => x.StartsWith("--config ")));
        Assert.Single(runner.Commands.Where(x => x.StartsWith("--password ")));
        Assert.DoesNotContain(runner.Commands, x => x.Contains("--config") && x.Contains("--password"));
        Assert.Contains("--option \"custom-rendezvous-server\"", runner.Commands);
        Assert.Contains("--option \"relay-server\"", runner.Commands);
        Assert.Contains("--option \"key\"", runner.Commands);
    }

    [Fact]
    public async Task 密码命令退出零但未确认成功必须失败()
    {
        var installer = new WindowsRemoteMaintenanceInstaller(new CliRunner(Config, passwordAcknowledged: false), null!)
        { DaemonReadyTimeout = TimeSpan.Zero, PollInterval = TimeSpan.Zero };
        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() =>
            installer.ConfigureRustDeskAsync("unused.exe", Config, "test-password-only", CancellationToken.None));
        Assert.Equal(RemoteMaintenanceSetupError.RustDeskConfigurationFailed, error.Error);
    }

    [Fact]
    public async Task 服务IPC就绪前密码未确认会重试且确认后才写服务器配置()
    {
        // 服务刚启动时 --password 连不上 IPC；若先写 --config 会静默落到管理员本地配置。
        var runner = new CliRunner(Config, passwordFailuresBeforeAck: 3);
        var installer = new WindowsRemoteMaintenanceInstaller(runner, null!) { PollInterval = TimeSpan.Zero };

        await installer.ConfigureRustDeskAsync("unused.exe", Config, "test-password-only", CancellationToken.None);

        Assert.Equal(4, runner.Commands.Count(x => x.StartsWith("--password ")));
        Assert.True(runner.Commands.FindLastIndex(x => x.StartsWith("--password ")) <
            runner.Commands.FindIndex(x => x.StartsWith("--config ")));
    }

    [Fact]
    public async Task 安装外壳退出后等待内层安装进程结束才继续()
    {
        var runner = new DetachedSetupRunner(runningChecks: 3);
        var installer = new WindowsRemoteMaintenanceInstaller(runner, null!) { PollInterval = TimeSpan.Zero };

        await installer.WaitForDetachedRustDeskSetupAsync(CancellationToken.None);

        Assert.Equal(4, runner.Checks);
        Assert.EndsWith(Path.Combine("RustDesk", "rustdesk.exe"), runner.CheckedExecutable);
    }

    [Fact]
    public async Task 内层安装进程超时未结束给出安装未完成故障码()
    {
        var installer = new WindowsRemoteMaintenanceInstaller(new DetachedSetupRunner(int.MaxValue), null!)
        { SetupWaitTimeout = TimeSpan.Zero, PollInterval = TimeSpan.Zero };

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() =>
            installer.WaitForDetachedRustDeskSetupAsync(CancellationToken.None));

        Assert.Equal(RemoteMaintenanceSetupError.RustDeskSetupIncomplete, error.Error);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("stopped")]
    public async Task 外部RustDesk给出需先卸载的故障码且不重新启动它(string status)
    {
        // stopped 分支依赖 Program Files 下没有公司接管标记；CI runner 上不存在该文件。
        var control = new RecordingServiceControl { Status = status };
        var installer = new WindowsRemoteMaintenanceInstaller(new OptionRunner("rs-ny.rustdesk.com", wrongReads: int.MaxValue), control)
        { PollInterval = TimeSpan.Zero };

        var error = await Assert.ThrowsAsync<RemoteMaintenanceSetupException>(() =>
            installer.EnsureNoForeignRustDeskConfigAsync(Config, CancellationToken.None));

        Assert.Equal(RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged, error.Error);
        Assert.Empty(control.StartedServices);
    }

    [Fact]
    public async Task 公司配置的RustDesk读回在IPC短暂回退时重试而不误判为外部()
    {
        var runner = new OptionRunner("unused", wrongReads: 2);
        var installer = new WindowsRemoteMaintenanceInstaller(runner, new RecordingServiceControl { Status = "running" })
        { PollInterval = TimeSpan.Zero };

        await installer.EnsureNoForeignRustDeskConfigAsync(Config, CancellationToken.None);

        Assert.Equal(3, runner.Reads["custom-rendezvous-server"]);
    }

    [Theory]
    [InlineData("api/remote-maintenance/artifacts/rustdesk")]
    [InlineData("/api/remote-maintenance/artifacts/status-agent")]
    public async Task 下载保留生产POS前缀(string path)
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://hotbargain.vip/pos-api/") };
        await using var stream = await new RemoteMaintenanceApiClient(http).DownloadArtifactAsync(path);
        Assert.StartsWith("https://hotbargain.vip/pos-api/api/remote-maintenance/artifacts/", handler.Url);
    }

    [Theory]
    [InlineData("https://evil.example/payload.exe")]
    [InlineData("//evil.example/payload.exe")]
    [InlineData("../artifacts/rustdesk")]
    public async Task 外部下载地址在发送设备票据之前拒绝(string path)
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://hotbargain.vip/pos-api/") };
        await Assert.ThrowsAsync<RemoteMaintenanceApiException>(() => new RemoteMaintenanceApiClient(http).DownloadArtifactAsync(path));
        Assert.Null(handler.Url);
    }

    [Fact]
    public void SCM内层路径引号不会丢失()
    {
        var encoded = WindowsRemoteMaintenanceCommandRunner.Quote("\"C:\\Program Files\\HBPOS\\Agent.exe\" --service");
        Assert.Equal("\"\\\"C:\\Program Files\\HBPOS\\Agent.exe\\\" --service\"", encoded);
        Assert.Equal(32, Convert.FromHexString(WindowsRemoteMaintenanceInstaller.TrustedRustDeskSha256).Length);
    }

    [Fact]
    public async Task FailClosed只停止已实际触及的服务()
    {
        var control = new RecordingServiceControl();
        var installer = new WindowsRemoteMaintenanceInstaller(new NoopCommandRunner(), control);
        var empty = new RemoteMaintenanceInstallationResult(false, false, "", "1.4.9", "data");
        await installer.FailClosedAsync(empty);
        Assert.Empty(control.StoppedServices);

        var rustDeskOnly = new RemoteMaintenanceInstallationResult(true, false, "", "1.4.9", "data");
        await installer.FailClosedAsync(rustDeskOnly);
        Assert.Equal(new[] { "RustDesk" }, control.StoppedServices);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Url { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        }
    }
    private sealed class CliRunner(RemoteMaintenanceConfig config, bool passwordAcknowledged = true, int passwordFailuresBeforeAck = 0) : IRemoteMaintenanceCommandRunner
    {
        private int _passwordAttempts;
        public List<string> Commands { get; } = [];
        public Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
        { Commands.Add(arguments); return Task.FromResult(0); }
        public Task<RemoteMaintenanceCommandResult> RunWithOutputAsync(string fileName, string arguments, CancellationToken cancellationToken)
        {
            Commands.Add(arguments);
            var output = arguments switch
            {
                "--option \"custom-rendezvous-server\"" => config.IdServer,
                "--option \"relay-server\"" => config.RelayServer,
                "--option \"key\"" => config.PublicKey,
                "--option \"approve-mode\"" => "password",
                "--option \"verification-method\"" => "use-permanent-password",
                "--option \"allow-only-conn-window-open\"" => "N",
                // 与 RustDesk 1.4.9 一致：IPC 未就绪时打印连接错误而不是 Done!。
                _ when arguments.StartsWith("--password ") =>
                    passwordAcknowledged && ++_passwordAttempts > passwordFailuresBeforeAck ? "Done!\r\n" : "Installation required!",
                _ => throw new InvalidOperationException("unexpected command")
            };
            return Task.FromResult(new RemoteMaintenanceCommandResult(0, output, ""));
        }
    }

    private sealed class NoopCommandRunner : IRemoteMaintenanceCommandRunner
    {
        public Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class DetachedSetupRunner(int runningChecks) : IRemoteMaintenanceCommandRunner
    {
        public int Checks { get; private set; }
        public string? CheckedExecutable { get; private set; }
        public Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken) => Task.FromResult(0);
        public bool IsDetachedProcessRunning(string installedExecutable)
        {
            CheckedExecutable = installedExecutable;
            return ++Checks <= runningChecks;
        }
    }

    // 模拟 --option 读回：前 wrongReads 次返回外部服务器（IPC 回退到本地配置），之后返回公司配置。
    private sealed class OptionRunner(string foreignServer, int wrongReads) : IRemoteMaintenanceCommandRunner
    {
        public Dictionary<string, int> Reads { get; } = [];
        public Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<RemoteMaintenanceCommandResult> RunWithOutputAsync(string fileName, string arguments, CancellationToken cancellationToken)
        {
            var key = arguments["--option \"".Length..^1];
            Reads[key] = Reads.GetValueOrDefault(key) + 1;
            var output = Reads[key] <= wrongReads ? foreignServer : key switch
            {
                "custom-rendezvous-server" => Config.IdServer,
                "relay-server" => Config.RelayServer,
                "key" => Config.PublicKey,
                _ => throw new InvalidOperationException("unexpected command")
            };
            return Task.FromResult(new RemoteMaintenanceCommandResult(0, output + "\r\n", ""));
        }
    }

    private sealed class RecordingServiceControl : IRemoteMaintenanceServiceControl
    {
        public string? Status { get; init; }
        public List<string> StoppedServices { get; } = [];
        public List<string> StartedServices { get; } = [];
        public Task<string?> QueryAsync(string serviceName, CancellationToken cancellationToken) => Task.FromResult(Status);
        public Task<int> CreateOrUpdateAsync(string serviceName, string binaryPath, string accountName, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<int> StartAsync(string serviceName, CancellationToken cancellationToken)
        {
            StartedServices.Add(serviceName);
            return Task.FromResult(0);
        }
        public Task<int> StopAsync(string serviceName, CancellationToken cancellationToken)
        {
            StoppedServices.Add(serviceName);
            return Task.FromResult(0);
        }
    }
}
