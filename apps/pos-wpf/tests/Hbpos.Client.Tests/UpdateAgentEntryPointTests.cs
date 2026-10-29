using Hbpos.Updater;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class UpdateAgentEntryPointTests
{
    private const string ProgramFiles = @"C:\Program Files";
    private static readonly string UpdaterDirectory = @"C:\Program Files\HB POS\updater" + Path.DirectorySeparatorChar;

    [Fact]
    public void Register_creates_task_for_updater_inside_program_files()
    {
        var scheduler = new RecordingScheduler();

        var exitCode = UpdateAgentEntryPoint.Register(UpdaterDirectory, ProgramFiles, scheduler);

        Assert.Equal(0, exitCode);
        Assert.Equal(@"C:\Program Files\HB POS\updater\Hbpos.Updater.exe", Assert.Single(scheduler.Registered));
        Assert.Equal(0, scheduler.UnregisterCalls);
    }

    [Theory]
    [InlineData(@"C:\HB POS\updater\")]
    [InlineData(@"C:\Program Files (x86)\HB POS\updater\")]
    [InlineData(@"C:\Users\Cashier\AppData\Local\Temp\HbposUpdater\abc\")]
    public void Register_refuses_install_locations_that_ordinary_users_may_be_able_to_write(string baseDirectory)
    {
        var scheduler = new RecordingScheduler();

        var exitCode = UpdateAgentEntryPoint.Register(baseDirectory, ProgramFiles, scheduler);

        // 中文注释：不在 Program Files 下时不注册，还要清掉旧任务，避免 SYSTEM 任务指向普通用户可改写的程序。
        Assert.Equal(UpdateAgentEntryPoint.NotUnderProgramFilesExitCode, exitCode);
        Assert.Empty(scheduler.Registered);
        Assert.Equal(1, scheduler.UnregisterCalls);
    }

    [Fact]
    public void Register_reports_failure_without_throwing_when_task_scheduler_is_unavailable()
    {
        var scheduler = new RecordingScheduler { RegisterException = new UnauthorizedAccessException() };

        Assert.Equal(UpdateAgentEntryPoint.RegistrationFailedExitCode, UpdateAgentEntryPoint.Register(UpdaterDirectory, ProgramFiles, scheduler));
    }

    [Fact]
    public void Unregister_removes_task_and_tolerates_failures()
    {
        var scheduler = new RecordingScheduler();

        Assert.Equal(0, UpdateAgentEntryPoint.Unregister(scheduler));
        Assert.Equal(1, scheduler.UnregisterCalls);

        scheduler.UnregisterException = new IOException("locked");
        Assert.Equal(UpdateAgentEntryPoint.RegistrationFailedExitCode, UpdateAgentEntryPoint.Unregister(scheduler));
    }

    [Theory]
    [InlineData("--register-agent-task", true)]
    [InlineData("--unregister-agent-task", true)]
    [InlineData("--agent", true)]
    [InlineData("--agent-worker", true)]
    [InlineData("--installer", false)]
    [InlineData("--to", false)]
    public void TryStart_only_claims_agent_commands_so_normal_update_window_launch_is_untouched(string command, bool expected)
    {
        Assert.Equal(expected, UpdateAgentEntryPoint.TryStart([command, "value"], out _));
        Assert.False(UpdateAgentEntryPoint.TryStart([], out _));
    }

    [Fact]
    public void Task_definition_only_lets_ordinary_users_read_and_run_it()
    {
        // 中文注释：D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)：SYSTEM/管理员完全控制，已登录用户只读+执行，不能改动作或删除。
        Assert.Contains("(A;;FA;;;SY)", WindowsUpdateAgentTaskScheduler.TaskSecurityDescriptor);
        Assert.Contains("(A;;FA;;;BA)", WindowsUpdateAgentTaskScheduler.TaskSecurityDescriptor);
        Assert.Contains("(A;;GRGX;;;AU)", WindowsUpdateAgentTaskScheduler.TaskSecurityDescriptor);
        Assert.DoesNotContain("GA;;;AU", WindowsUpdateAgentTaskScheduler.TaskSecurityDescriptor);
        Assert.DoesNotContain(";;;WD", WindowsUpdateAgentTaskScheduler.TaskSecurityDescriptor);
        Assert.Equal("--agent $(Arg0)", WindowsUpdateAgentTaskScheduler.AgentArguments);
    }

    [Fact]
    public void Task_execution_time_limit_outlasts_the_longest_download_and_install()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Updater", "UpdateAgentTaskScheduler.cs"));

        // 中文注释：worker 是任务的子进程，时限到点会连它一起终止；时限必须大于 下载超时 + 安装超时，否则会在安装中途被杀。
        Assert.Contains("ExecutionTimeLimit = \"PT2H\"", source);
        Assert.True(
            TimeSpan.FromHours(2) > UpdateAgent.InstallTimeout + TimeSpan.FromMinutes(15),
            "Task time limit must exceed download timeout plus install timeout.");
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "apps", "pos-wpf")) &&
                Directory.Exists(Path.Combine(current.FullName, "services", "backend")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class RecordingScheduler : IUpdateAgentTaskScheduler
    {
        public List<string> Registered { get; } = [];

        public int UnregisterCalls { get; private set; }

        public Exception? RegisterException { get; set; }

        public Exception? UnregisterException { get; set; }

        public string? GetRegisteredAgentPath() => Registered.LastOrDefault();

        public bool TryRun(string encodedRequest, out string error)
        {
            error = string.Empty;
            return true;
        }

        public void Register(string agentExecutablePath)
        {
            if (RegisterException is { } exception)
            {
                throw exception;
            }

            Registered.Add(agentExecutablePath);
        }

        public void Unregister()
        {
            UnregisterCalls++;
            if (UnregisterException is { } exception)
            {
                throw exception;
            }
        }
    }
}

public sealed class AgentInstallProcessTests
{
    private const string RequestId = "0123456789abcdef0123456789abcdef";
    private const string StatusPath = @"C:\Program Files\HB POS\UpdateAgent\status\0123456789abcdef0123456789abcdef.json";

    [Fact]
    public async Task Waits_for_final_status_and_reports_success_as_exit_code_zero()
    {
        var harness = new Harness();
        var wait = harness.Process.WaitForExitAsync(CancellationToken.None);

        harness.Status = new UpdateAgentStatus(RequestId, UpdateAgentState.Installing, null, null, DateTimeOffset.UnixEpoch);
        await harness.AdvanceAsync(wait, steps: 3);
        Assert.False(wait.IsCompleted);

        harness.Status = new UpdateAgentStatus(RequestId, UpdateAgentState.Succeeded, 0, null, DateTimeOffset.UnixEpoch);
        await harness.AdvanceAsync(wait);

        Assert.Equal(0, harness.Process.ExitCode);
        Assert.Null(harness.Process.FailureDetail);
    }

    [Fact]
    public async Task Reports_installer_exit_code_and_agent_error_on_failure()
    {
        var harness = new Harness();
        var wait = harness.Process.WaitForExitAsync(CancellationToken.None);

        harness.Status = new UpdateAgentStatus(RequestId, UpdateAgentState.Failed, 8, "installer-exit-code", DateTimeOffset.UnixEpoch);
        await harness.AdvanceAsync(wait);

        Assert.Equal(8, harness.Process.ExitCode);
        Assert.Equal("installer-exit-code", harness.Process.FailureDetail);
    }

    [Fact]
    public async Task Uses_generic_failure_code_when_agent_fails_without_exit_code()
    {
        var harness = new Harness();
        var wait = harness.Process.WaitForExitAsync(CancellationToken.None);

        harness.Status = new UpdateAgentStatus(RequestId, UpdateAgentState.Failed, null, "sha256-mismatch", DateTimeOffset.UnixEpoch);
        await harness.AdvanceAsync(wait);

        Assert.Equal(AgentInstallProcess.AgentFailedExitCode, harness.Process.ExitCode);
        Assert.Equal("sha256-mismatch", harness.Process.FailureDetail);
    }

    [Fact]
    public async Task Reports_not_responding_when_agent_never_accepts_the_request()
    {
        var harness = new Harness();
        var wait = harness.Process.WaitForExitAsync(CancellationToken.None);

        await harness.AdvanceAsync(wait, AgentInstallProcess.AcceptTimeout + TimeSpan.FromSeconds(1));

        Assert.Equal(AgentInstallProcess.AgentNotRespondingExitCode, harness.Process.ExitCode);
    }

    [Fact]
    public async Task Ignores_status_files_for_other_requests()
    {
        var harness = new Harness();
        var wait = harness.Process.WaitForExitAsync(CancellationToken.None);

        harness.Status = new UpdateAgentStatus("ffffffffffffffffffffffffffffffff", UpdateAgentState.Succeeded, 0, null, DateTimeOffset.UnixEpoch);
        await harness.AdvanceAsync(wait, AgentInstallProcess.AcceptTimeout + TimeSpan.FromSeconds(1));

        // 中文注释：别的请求留下的“成功”状态不能被当成本次安装成功。
        Assert.Equal(AgentInstallProcess.AgentNotRespondingExitCode, harness.Process.ExitCode);
    }

    [Fact]
    public async Task Fails_when_agent_accepted_but_never_finishes()
    {
        var harness = new Harness();
        var wait = harness.Process.WaitForExitAsync(CancellationToken.None);

        harness.Status = new UpdateAgentStatus(RequestId, UpdateAgentState.Installing, null, null, DateTimeOffset.UnixEpoch);
        await harness.AdvanceAsync(wait, AgentInstallProcess.CompletionTimeout + TimeSpan.FromSeconds(1));

        Assert.Equal(AgentInstallProcess.AgentFailedExitCode, harness.Process.ExitCode);
        Assert.Equal("update-agent-timeout", harness.Process.FailureDetail);
    }

    private sealed class Harness
    {
        private volatile UpdateAgentStatus? _status;

        public Harness()
        {
            Process = new AgentInstallProcess(RequestId, StatusPath, ReadStatus, Time);
        }

        public FakeTimeProvider Time { get; } = new();

        public AgentInstallProcess Process { get; }

        public UpdateAgentStatus? Status
        {
            set => _status = value;
        }

        public async Task AdvanceAsync(Task wait, TimeSpan? jump = null, int? steps = null)
        {
            if (jump is { } total)
            {
                // 中文注释：超时用例一次性拨快虚拟时钟：等待循环醒来后按已过去的总时间判断超时。
                Time.Advance(total);
                await wait.WaitAsync(TimeSpan.FromSeconds(10));
                return;
            }

            var count = steps ?? 60;
            for (var step = 0; step < count && !wait.IsCompleted; step++)
            {
                Time.Advance(AgentInstallProcess.PollInterval);
                await Task.Delay(1);
            }

            if (steps is null)
            {
                await wait.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private string? ReadStatus(string path)
        {
            Assert.Equal(StatusPath, path);
            return _status?.Serialize();
        }
    }
}
