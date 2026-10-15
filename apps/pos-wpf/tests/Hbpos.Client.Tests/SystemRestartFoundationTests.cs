using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>日结后重启：选项、设置存储、安全检查、重启命令封装。</summary>
public sealed class SystemRestartFoundationTests
{
    // ---------- 选项 ----------

    [Fact]
    public void Options_use_defaults_when_nothing_configured()
    {
        var options = SystemRestartOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal(7, options.UptimeThresholdDays);
        Assert.Equal(30, options.CountdownSeconds);
        Assert.Equal(30, options.UploadWaitSeconds);
        Assert.Equal(15, options.SystemShutdownDelaySeconds);
        Assert.Equal(TimeSpan.FromDays(7), options.UptimeThreshold);
    }

    [Fact]
    public void Options_read_configured_values_and_fall_back_when_out_of_range()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Maintenance:RestartPrompt:UptimeDays"] = "14",
                ["Maintenance:RestartPrompt:CountdownSeconds"] = "0",       // 越界 → 默认
                ["Maintenance:RestartPrompt:UploadWaitSeconds"] = "abc",    // 非数字 → 默认
                ["Maintenance:RestartPrompt:SystemShutdownDelaySeconds"] = "20"
            })
            .Build();

        var options = SystemRestartOptions.FromConfiguration(configuration);

        Assert.Equal(14, options.UptimeThresholdDays);
        Assert.Equal(30, options.CountdownSeconds);
        Assert.Equal(30, options.UploadWaitSeconds);
        Assert.Equal(20, options.SystemShutdownDelaySeconds);
    }

    // ---------- 设置存储 ----------

    [Fact]
    public async Task SettingsStore_defaults_to_enabled_and_persists_toggle()
    {
        var store = new SystemRestartSettingsStore(new InMemorySettings());

        Assert.True(await store.IsPromptEnabledAsync());

        await store.SetPromptEnabledAsync(false);
        Assert.False(await store.IsPromptEnabledAsync());

        await store.SetPromptEnabledAsync(true);
        Assert.True(await store.IsPromptEnabledAsync());
    }

    [Fact]
    public async Task SettingsStore_treats_garbage_value_as_enabled()
    {
        var settings = new InMemorySettings();
        await settings.SetValueAsync(SystemRestartSettingsStore.EnabledKey, "maybe");

        Assert.True(await new SystemRestartSettingsStore(settings).IsPromptEnabledAsync());
    }

    [Fact]
    public async Task SettingsStore_round_trips_last_prompt_business_date()
    {
        var store = new SystemRestartSettingsStore(new InMemorySettings());
        Assert.Null(await store.GetLastPromptBusinessDateAsync());

        await store.MarkPromptedAsync(new DateOnly(2026, 10, 15));

        Assert.Equal(new DateOnly(2026, 10, 15), await store.GetLastPromptBusinessDateAsync());
    }

    // ---------- 安全检查 ----------

    [Fact]
    public async Task Checker_reports_no_issue_when_everything_is_idle()
    {
        var checker = new RestartSafetyChecker(new FakeProbe());

        var report = await checker.CheckAsync();

        Assert.True(report.IsSafe);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public async Task Checker_collects_every_hard_issue_with_counts()
    {
        var probe = new FakeProbe
        {
            CartLineCount = 3,
            IsPaymentActive = true,
            IsDailyCloseBusy = true,
            IsAppUpdateActive = true,
            CardTerminalBusy = true,
            OpenCardRecovery = 2,
            UnresolvedSettlement = true
        };

        var report = await new RestartSafetyChecker(probe).CheckAsync();

        Assert.False(report.IsSafe);
        var codes = report.HardIssues.Select(issue => issue.Code).ToArray();
        Assert.Contains(RestartSafetyIssueCode.CartNotEmpty, codes);
        Assert.Contains(RestartSafetyIssueCode.PaymentActive, codes);
        Assert.Contains(RestartSafetyIssueCode.DailyCloseBusy, codes);
        Assert.Contains(RestartSafetyIssueCode.AppUpdateActive, codes);
        Assert.Contains(RestartSafetyIssueCode.CardTerminalBusy, codes);
        Assert.Contains(RestartSafetyIssueCode.CardRecoveryOpen, codes);
        Assert.Contains(RestartSafetyIssueCode.LinklySettlementUnresolved, codes);
        Assert.Equal(3, report.HardIssues.Single(i => i.Code == RestartSafetyIssueCode.CartNotEmpty).Count);
        Assert.Equal(2, report.HardIssues.Single(i => i.Code == RestartSafetyIssueCode.CardRecoveryOpen).Count);
    }

    [Fact]
    public async Task Checker_fails_closed_when_a_state_read_throws()
    {
        var probe = new FakeProbe { CardRecoveryException = new InvalidOperationException("db locked") };

        var report = await new RestartSafetyChecker(probe).CheckAsync();

        Assert.False(report.IsSafe);
        Assert.Contains(report.HardIssues, issue => issue.Code == RestartSafetyIssueCode.CheckFailed);
    }

    [Fact]
    public async Task Checker_treats_upload_backlog_as_soft_issue_only()
    {
        var probe = new FakeProbe { Backlog = new RestartUploadBacklog(2, 1, 0) };

        var report = await new RestartSafetyChecker(probe).CheckAsync();

        Assert.True(report.IsSafe);
        var soft = Assert.Single(report.SoftIssues);
        Assert.Equal(RestartSafetyIssueCode.UploadPending, soft.Code);
        Assert.Equal(3, soft.Count);
    }

    [Fact]
    public async Task Checker_ignores_failure_to_read_upload_backlog()
    {
        var probe = new FakeProbe { BacklogException = new InvalidOperationException("boom") };

        var report = await new RestartSafetyChecker(probe).CheckAsync();

        Assert.True(report.IsSafe);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public async Task Flush_returns_immediately_when_nothing_is_pending()
    {
        var probe = new FakeProbe();

        var result = await new RestartSafetyChecker(probe).FlushUploadsAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.Remaining);
        Assert.False(result.TimedOut);
        Assert.Equal(0, probe.RequestUploadCalls);
    }

    [Fact]
    public async Task Flush_waits_until_backlog_clears()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe { BacklogSequence = new Queue<RestartUploadBacklog>([
            new RestartUploadBacklog(2, 0, 0),   // 起始读取
            new RestartUploadBacklog(1, 0, 0),   // 第 1 秒
            new RestartUploadBacklog(0, 0, 0)])  // 第 2 秒：清空
        };
        var checker = new RestartSafetyChecker(probe, time);

        var task = checker.FlushUploadsAsync(TimeSpan.FromSeconds(30));
        await AdvanceUntilCompletedAsync(task, time);
        var result = await task;

        Assert.Equal(0, result.Remaining);
        Assert.False(result.TimedOut);
        Assert.Equal(1, probe.RequestUploadCalls);
    }

    [Fact]
    public async Task Flush_gives_up_after_max_wait_and_reports_remaining()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe { Backlog = new RestartUploadBacklog(5, 0, 0) };
        var checker = new RestartSafetyChecker(probe, time);

        var task = checker.FlushUploadsAsync(TimeSpan.FromSeconds(3));
        await AdvanceUntilCompletedAsync(task, time);
        var result = await task;

        Assert.True(result.TimedOut);
        Assert.Equal(5, result.Remaining);
    }

    // ---------- 重启命令封装 ----------

    [Fact]
    public async Task RestartService_registers_runonce_then_runs_shutdown_with_expected_arguments()
    {
        var runner = new FakeRunner(new ProcessRunResult(0, string.Empty));
        var registrar = new FakeRegistrar();
        var service = new SystemRestartService(
            runner,
            registrar,
            () => @"C:\Windows\System32\shutdown.exe",
            () => @"C:\Program Files\HB POS\HB POS.exe");

        var result = await service.RestartAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(SystemRestartStatus.Scheduled, result.Status);
        Assert.Equal([@"C:\Program Files\HB POS\HB POS.exe"], registrar.Registered);
        Assert.Equal(0, registrar.UnregisterCalls);
        Assert.Equal(@"C:\Windows\System32\shutdown.exe", runner.FileName);
        Assert.StartsWith("/r /f /t 15 ", runner.Arguments);
        Assert.Contains("/d p:4:1", runner.Arguments);
    }

    [Theory]
    [InlineData(1190)]
    [InlineData(1115)]
    public async Task RestartService_treats_already_scheduled_as_success(int exitCode)
    {
        var service = new SystemRestartService(
            new FakeRunner(new ProcessRunResult(exitCode, "already")),
            new FakeRegistrar(),
            () => "shutdown.exe",
            () => "pos.exe");

        var result = await service.RestartAsync(TimeSpan.FromSeconds(15));

        Assert.True(result.Succeeded);
        Assert.Equal(SystemRestartStatus.AlreadyScheduled, result.Status);
    }

    [Fact]
    public async Task RestartService_unregisters_runonce_when_command_fails()
    {
        var registrar = new FakeRegistrar();
        var service = new SystemRestartService(
            new FakeRunner(new ProcessRunResult(5, "Access is denied.")),
            registrar,
            () => "shutdown.exe",
            () => "pos.exe");

        var result = await service.RestartAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(SystemRestartStatus.Failed, result.Status);
        Assert.Equal(5, result.ExitCode);
        Assert.Equal(1, registrar.UnregisterCalls);
    }

    [Fact]
    public async Task RestartService_maps_start_exception_to_failure_with_native_error_code()
    {
        var registrar = new FakeRegistrar();
        var service = new SystemRestartService(
            new FakeRunner(exception: new System.ComponentModel.Win32Exception(5, "denied")),
            registrar,
            () => "shutdown.exe",
            () => "pos.exe");

        var result = await service.RestartAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(SystemRestartStatus.Failed, result.Status);
        Assert.Equal(5, result.ExitCode);
        Assert.Equal(1, registrar.UnregisterCalls);
    }

    [Fact]
    public async Task RestartService_still_restarts_when_autolaunch_registration_fails()
    {
        var runner = new FakeRunner(new ProcessRunResult(0, string.Empty));
        var service = new SystemRestartService(
            runner,
            new FakeRegistrar { RegisterResult = false },
            () => "shutdown.exe",
            () => "pos.exe");

        var result = await service.RestartAsync(TimeSpan.FromSeconds(15));

        // 登记失败只意味着重启后要手动打开 POS，不能因此放弃重启。
        Assert.Equal(SystemRestartStatus.Scheduled, result.Status);
        Assert.NotNull(runner.FileName);
    }

    [Fact]
    public async Task RestartService_clamps_delay_into_supported_range()
    {
        var runner = new FakeRunner(new ProcessRunResult(0, string.Empty));
        var service = new SystemRestartService(runner, new FakeRegistrar(), () => "s.exe", () => "p.exe");

        await service.RestartAsync(TimeSpan.Zero);

        Assert.StartsWith("/r /f /t 5 ", runner.Arguments);
    }

    // ---------- 工具与替身 ----------

    internal static async Task AdvanceUntilCompletedAsync(Task task, FakeTimeProvider time)
    {
        // 协调器/检查器用 Task.Delay(TimeProvider) 等待：每轮推进 1 秒虚拟时间并让出线程，直到任务结束。
        var guard = 0;
        while (!task.IsCompleted)
        {
            if (++guard > 2000)
            {
                throw new TimeoutException("task did not complete while advancing virtual time");
            }

            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }
    }

    private sealed class InMemorySettings : ILocalAppSettingsRepository
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetValuesAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
        {
            foreach (var pair in values)
            {
                _values[pair.Key] = pair.Value;
            }

            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeProbe : IRestartSafetyProbe
    {
        public int CartLineCount { get; set; }

        public bool IsPaymentActive { get; set; }

        public bool IsDailyCloseBusy { get; set; }

        public bool IsAppUpdateActive { get; set; }

        public bool CardTerminalBusy { get; set; }

        public int OpenCardRecovery { get; set; }

        public bool UnresolvedSettlement { get; set; }

        public Exception? CardRecoveryException { get; set; }

        public Exception? BacklogException { get; set; }

        public RestartUploadBacklog Backlog { get; set; } = new(0, 0, 0);

        /// <summary>设置后按顺序逐次返回，用完后一直返回最后一个。</summary>
        public Queue<RestartUploadBacklog>? BacklogSequence { get; set; }

        public int RequestUploadCalls { get; private set; }

        public ValueTask<bool> IsCardTerminalBusyAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(CardTerminalBusy);

        public Task<int> CountOpenCardRecoveryAsync(CancellationToken cancellationToken) =>
            CardRecoveryException is null
                ? Task.FromResult(OpenCardRecovery)
                : Task.FromException<int>(CardRecoveryException);

        public Task<bool> HasUnresolvedLinklySettlementAsync(CancellationToken cancellationToken) =>
            Task.FromResult(UnresolvedSettlement);

        public Task<RestartUploadBacklog> GetUploadBacklogAsync(CancellationToken cancellationToken)
        {
            if (BacklogException is not null)
            {
                return Task.FromException<RestartUploadBacklog>(BacklogException);
            }

            if (BacklogSequence is { Count: > 0 } sequence)
            {
                Backlog = sequence.Count > 1 ? sequence.Dequeue() : sequence.Peek();
            }

            return Task.FromResult(Backlog);
        }

        public Task RequestUploadsAsync(CancellationToken cancellationToken)
        {
            RequestUploadCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRunner(ProcessRunResult? result = null, Exception? exception = null) : IProcessRunner
    {
        public string? FileName { get; private set; }

        public string Arguments { get; private set; } = string.Empty;

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            string arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            FileName = fileName;
            Arguments = arguments;
            return exception is not null
                ? Task.FromException<ProcessRunResult>(exception)
                : Task.FromResult(result!);
        }
    }

    private sealed class FakeRegistrar : IRestartAutoLaunchRegistrar
    {
        public List<string> Registered { get; } = [];

        public bool RegisterResult { get; init; } = true;

        public int UnregisterCalls { get; private set; }

        public bool TryRegister(string executablePath)
        {
            Registered.Add(executablePath);
            return RegisterResult;
        }

        public void Unregister() => UnregisterCalls++;
    }
}
