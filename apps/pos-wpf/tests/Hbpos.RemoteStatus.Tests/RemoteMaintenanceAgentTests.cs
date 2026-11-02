using System.Text;
using System.Text.Json;
using Hbpos.RemoteMaintenance.Setup;

namespace Hbpos.RemoteStatus.Tests;

/// <summary>
/// 免 UAC 代理：请求按不可信输入校验、SYSTEM 端只依据请求里的白名单服务器执行并写固定退出码、
/// 普通用户端在任务不可用或未受理时如实报告。用临时目录模拟 helper 安装目录，不触碰任务计划与真实服务。
/// </summary>
public sealed class RemoteMaintenanceAgentTests : IDisposable
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Repeat((byte)3, 32).ToArray());
    private static readonly RemoteMaintenanceConfig Dedicated = new("rd.hotbargain.vip:21116", "rd.hotbargain.vip:21117", Key);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hbpos-agent-" + Guid.NewGuid().ToString("N"));
    private string HelperPath => Path.Combine(_root, "Hbpos.RemoteMaintenance.Setup.exe");

    public RemoteMaintenanceAgentTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void 请求编码往返保留请求号与目标服务器()
    {
        var request = RemoteMaintenanceAgentRequest.CreateReconcile(Dedicated);

        Assert.True(RemoteMaintenanceAgentRequest.TryDecode(request.Encode(), out var decoded));
        Assert.Equal(request, decoded);
        // 编码结果可直接作为任务计划参数：只含 base64url 字符。
        Assert.Matches("^[A-Za-z0-9_-]+$", request.Encode());
    }

    [Fact]
    public void 白名单外的服务器请求被拒绝()
    {
        var foreign = new RemoteMaintenanceAgentRequest(Guid.NewGuid().ToString("N"),
            new("rs-ny.rustdesk.com:21116", "rs-ny.rustdesk.com:21117", Key));

        Assert.False(RemoteMaintenanceAgentRequest.TryDecode(foreign.Encode(), out var decoded));
        Assert.Null(decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("e30")] // {}
    public void 格式不对的请求被拒绝(string? encoded)
    {
        Assert.False(RemoteMaintenanceAgentRequest.TryDecode(encoded, out _));
    }

    [Theory]
    // 只开放 reconcile；安装、配置、清理仍走 UAC helper。
    [InlineData(1, "install", true)]
    [InlineData(2, "reconcile", true)]
    // 请求号必须是 32 位十六进制 GUID，避免被拼进状态文件路径时穿越目录。
    [InlineData(1, "reconcile", false)]
    public void 阶段版本与请求号都必须严格匹配(int version, string stage, bool validId)
    {
        var id = validId ? Guid.NewGuid().ToString("N") : "..\\..\\evil";
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["v"] = version, ["id"] = id, ["stage"] = stage,
            ["idServer"] = Dedicated.IdServer, ["relayServer"] = Dedicated.RelayServer, ["key"] = Dedicated.PublicKey
        });
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.False(RemoteMaintenanceAgentRequest.TryDecode(encoded, out _));
    }

    [Fact]
    public void 超长请求在解析前被拒绝()
    {
        Assert.False(RemoteMaintenanceAgentRequest.TryDecode(new string('A', RemoteMaintenanceAgentRequest.MaxEncodedLength + 1), out _));
    }

    [Fact]
    public void 代理按请求执行对账并写入完成状态()
    {
        RemoteMaintenanceConfig? applied = null;
        var agent = new RemoteMaintenanceAgent(HelperPath, (config, _) => { applied = config; return Task.CompletedTask; });
        var request = RemoteMaintenanceAgentRequest.CreateReconcile(Dedicated);

        Assert.Equal(0, agent.Run(request.Encode()));

        Assert.Equal(Dedicated, applied);
        var status = ReadStatus(request.RequestId);
        Assert.True(status!.Completed);
        Assert.Equal(0, status.ExitCode);
    }

    [Fact]
    public void 代理把安装器的固定故障码写进状态且不带异常原文()
    {
        var agent = new RemoteMaintenanceAgent(HelperPath,
            (_, _) => throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged));
        var request = RemoteMaintenanceAgentRequest.CreateReconcile(Dedicated);

        Assert.Equal((int)RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged, agent.Run(request.Encode()));

        var raw = File.ReadAllText(RemoteMaintenanceAgentLayout.StatusFile(HelperPath, request.RequestId));
        Assert.Equal((int)RemoteMaintenanceSetupError.ExistingRustDeskUnmanaged, ReadStatus(request.RequestId)!.ExitCode);
        Assert.DoesNotContain("检查未通过", raw);
    }

    [Fact]
    public void 未知异常统一写成通用失败码()
    {
        var agent = new RemoteMaintenanceAgent(HelperPath, (_, _) => throw new InvalidOperationException("secret detail"));
        var request = RemoteMaintenanceAgentRequest.CreateReconcile(Dedicated);

        Assert.Equal(RemoteMaintenanceAgent.FailureExitCode, agent.Run(request.Encode()));
        Assert.DoesNotContain("secret detail", File.ReadAllText(RemoteMaintenanceAgentLayout.StatusFile(HelperPath, request.RequestId)));
    }

    [Fact]
    public void 无效请求不执行对账也不写状态()
    {
        var called = false;
        var agent = new RemoteMaintenanceAgent(HelperPath, (_, _) => { called = true; return Task.CompletedTask; });

        Assert.Equal(RemoteMaintenanceAgent.InvalidRequestExitCode, agent.Run("garbage"));

        Assert.False(called);
        Assert.False(Directory.Exists(RemoteMaintenanceAgentLayout.StatusDirectory(HelperPath)));
    }

    [Fact]
    public void 任务未注册或指向别处的程序时代理不可用()
    {
        Assert.False(new RemoteMaintenanceAgentClient(new FakeScheduler(null)).IsAvailable(HelperPath));
        Assert.False(new RemoteMaintenanceAgentClient(new FakeScheduler(Path.Combine(_root, "other", "Hbpos.RemoteMaintenance.Setup.exe")))
            .IsAvailable(HelperPath));
        Assert.True(new RemoteMaintenanceAgentClient(new FakeScheduler(HelperPath.ToUpperInvariant())).IsAvailable(HelperPath));
    }

    [Fact]
    public async Task 客户端触发任务后读取代理写的结果()
    {
        var scheduler = new FakeScheduler(HelperPath)
        {
            // 模拟任务计划同步拉起代理：代理执行完写入状态文件。
            OnRun = encoded => new RemoteMaintenanceAgent(HelperPath,
                (_, _) => throw new RemoteMaintenanceSetupException(RemoteMaintenanceSetupError.RustDeskSetupIncomplete)).Run(encoded)
        };
        var client = new RemoteMaintenanceAgentClient(scheduler) { PollInterval = TimeSpan.FromMilliseconds(10) };

        var exitCode = await client.ReconcileAsync(HelperPath, Dedicated);

        Assert.Equal((int)RemoteMaintenanceSetupError.RustDeskSetupIncomplete, exitCode);
        Assert.True(RemoteMaintenanceAgentRequest.TryDecode(scheduler.Requests.Single(), out var sent));
        Assert.Equal(Dedicated, sent!.Config);
    }

    [Fact]
    public async Task 任务触发失败或迟迟不受理时报告未响应()
    {
        var notRun = new RemoteMaintenanceAgentClient(new FakeScheduler(HelperPath) { RunSucceeds = false });
        Assert.Equal(RemoteMaintenanceAgentClient.NotRespondingExitCode, await notRun.ReconcileAsync(HelperPath, Dedicated));

        var silent = new RemoteMaintenanceAgentClient(new FakeScheduler(HelperPath))
        {
            AcceptTimeout = TimeSpan.FromMilliseconds(50),
            PollInterval = TimeSpan.FromMilliseconds(10)
        };
        Assert.Equal(RemoteMaintenanceAgentClient.NotRespondingExitCode, await silent.ReconcileAsync(HelperPath, Dedicated));
    }

    [Fact]
    public async Task 已受理后等待完成而不是按受理超时放弃()
    {
        RemoteMaintenanceAgentRequest? pending = null;
        var scheduler = new FakeScheduler(HelperPath)
        {
            OnRun = encoded =>
            {
                RemoteMaintenanceAgentRequest.TryDecode(encoded, out pending);
                WriteStatus(new(pending!.RequestId, Completed: false, ExitCode: 0, DateTimeOffset.UtcNow));
            }
        };
        var client = new RemoteMaintenanceAgentClient(scheduler)
        {
            AcceptTimeout = TimeSpan.FromMilliseconds(30),
            PollInterval = TimeSpan.FromMilliseconds(10)
        };

        var running = client.ReconcileAsync(HelperPath, Dedicated);
        await Task.Delay(150);
        Assert.False(running.IsCompleted);
        WriteStatus(new(pending!.RequestId, Completed: true, ExitCode: 0, DateTimeOffset.UtcNow));

        Assert.Equal(0, await running);
    }

    [Fact]
    public async Task 其他请求的状态文件不会被当成本次结果()
    {
        var scheduler = new FakeScheduler(HelperPath)
        {
            OnRun = encoded =>
            {
                RemoteMaintenanceAgentRequest.TryDecode(encoded, out var request);
                // 状态文件按请求号命名；内容里的请求号对不上时视为没有结果。
                Directory.CreateDirectory(RemoteMaintenanceAgentLayout.StatusDirectory(HelperPath));
                File.WriteAllText(RemoteMaintenanceAgentLayout.StatusFile(HelperPath, request!.RequestId),
                    new RemoteMaintenanceAgentStatus(Guid.NewGuid().ToString("N"), true, 0, DateTimeOffset.UtcNow).Serialize());
            }
        };
        var client = new RemoteMaintenanceAgentClient(scheduler)
        {
            AcceptTimeout = TimeSpan.FromMilliseconds(50),
            PollInterval = TimeSpan.FromMilliseconds(10)
        };

        Assert.Equal(RemoteMaintenanceAgentClient.NotRespondingExitCode, await client.ReconcileAsync(HelperPath, Dedicated));
    }

    [Fact]
    public void 本地配置不存在时新建只含更新开关的文件()
    {
        var content = RustDeskLocalConfigEditor.SetOption(null, "enable-check-update", "N");

        Assert.Equal("[options]\nenable-check-update = 'N'\n", content);
    }

    [Fact]
    public void 已有options段时插入到段末并保留其他内容与换行风格()
    {
        const string original = "remote_id = '123'\r\nfav = []\r\n\r\n[options]\r\nlang = 'zh-cn'\r\ntheme = 'dark'\r\n\r\n[ui_flutter]\r\nwm_Main = 'x'\r\n";

        var content = RustDeskLocalConfigEditor.SetOption(original, "enable-check-update", "N");

        Assert.Equal(
            "remote_id = '123'\r\nfav = []\r\n\r\n[options]\r\nlang = 'zh-cn'\r\ntheme = 'dark'\r\nenable-check-update = 'N'\r\n\r\n[ui_flutter]\r\nwm_Main = 'x'\r\n",
            content);
    }

    [Theory]
    [InlineData("[options]\nenable-check-update = 'Y'\n")]
    [InlineData("[options]\n\"enable-check-update\" = \"Y\"\n")]
    public void 已有开关为开启时改成关闭(string original)
    {
        Assert.Equal("[options]\nenable-check-update = 'N'\n", RustDeskLocalConfigEditor.SetOption(original, "enable-check-update", "N"));
    }

    [Theory]
    [InlineData("[options]\nenable-check-update = 'N'\n")]
    [InlineData("[options]\nenable-check-update = \"N\"\n")]
    public void 已经关闭时不改写文件(string original)
    {
        Assert.Null(RustDeskLocalConfigEditor.SetOption(original, "enable-check-update", "N"));
    }

    [Fact]
    public void 其他段里的同名键不算数()
    {
        const string original = "[ui_flutter]\nenable-check-update = 'N'\n\n[options]\nlang = 'en'\n";

        Assert.Equal(
            "[ui_flutter]\nenable-check-update = 'N'\n\n[options]\nlang = 'en'\nenable-check-update = 'N'\n",
            RustDeskLocalConfigEditor.SetOption(original, "enable-check-update", "N"));
    }

    [Fact]
    public void 没有options段时在文件末尾追加()
    {
        Assert.Equal(
            "remote_id = '1'\n\n[options]\nenable-check-update = 'N'\n",
            RustDeskLocalConfigEditor.SetOption("remote_id = '1'\n", "enable-check-update", "N"));
    }

    [Fact]
    public void 顶层是内联options表时不动文件()
    {
        // 再追加 [options] 会造成重复键，RustDesk 解析失败会丢掉整份本地配置。
        Assert.Null(RustDeskLocalConfigEditor.SetOption("options = { lang = 'en' }\n", "enable-check-update", "N"));
    }

    private RemoteMaintenanceAgentStatus? ReadStatus(string requestId) =>
        RemoteMaintenanceAgentStatus.TryParse(File.ReadAllText(RemoteMaintenanceAgentLayout.StatusFile(HelperPath, requestId)), requestId);

    private void WriteStatus(RemoteMaintenanceAgentStatus status)
    {
        Directory.CreateDirectory(RemoteMaintenanceAgentLayout.StatusDirectory(HelperPath));
        File.WriteAllText(RemoteMaintenanceAgentLayout.StatusFile(HelperPath, status.RequestId), status.Serialize());
    }

    private sealed class FakeScheduler(string? registeredPath) : IRemoteMaintenanceAgentTaskScheduler
    {
        public bool RunSucceeds { get; init; } = true;
        public Action<string>? OnRun { get; init; }
        public List<string> Requests { get; } = [];
        public string? GetRegisteredAgentPath() => registeredPath;

        public bool TryRun(string encodedRequest)
        {
            if (!RunSucceeds) return false;
            Requests.Add(encodedRequest);
            OnRun?.Invoke(encodedRequest);
            return true;
        }

        public void Register(string helperPath) => throw new NotSupportedException();
        public void Unregister() => throw new NotSupportedException();
    }
}
