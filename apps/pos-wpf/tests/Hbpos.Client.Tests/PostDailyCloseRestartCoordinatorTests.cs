using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>日结后重启协调器的完整流程：触发条件、阻止、稍后、倒计时取消、成功与失败，全部用虚拟时间与假界面驱动。</summary>
public sealed class PostDailyCloseRestartCoordinatorTests
{
    private static readonly DateTime BusinessDate = new(2026, 10, 15);

    [Fact]
    public async Task Not_due_when_prompt_is_disabled()
    {
        var harness = new Harness();
        await harness.Store.SetPromptEnabledAsync(false);

        var outcome = await harness.RunAsync();

        Assert.Equal(RestartFlowOutcome.NotDue, outcome);
        Assert.Equal(0, harness.Ui.PromptShown);
    }

    [Fact]
    public async Task Not_due_when_uptime_is_below_threshold()
    {
        var harness = new Harness { UptimeSeconds = (long)TimeSpan.FromDays(6.9).TotalSeconds };

        var outcome = await harness.RunAsync();

        Assert.Equal(RestartFlowOutcome.NotDue, outcome);
        Assert.Equal(0, harness.Ui.PromptShown);
        Assert.Empty(harness.Audit.Events);
    }

    [Fact]
    public async Task Not_due_when_uptime_is_unknown()
    {
        var harness = new Harness { UptimeSeconds = null };

        Assert.Equal(RestartFlowOutcome.NotDue, await harness.RunAsync());
    }

    [Fact]
    public async Task Prompts_once_per_business_date_even_after_later()
    {
        var harness = new Harness();
        harness.Ui.PromptChoice = RestartPromptChoice.Later;

        Assert.Equal(RestartFlowOutcome.Later, await harness.RunAsync());
        // 同一营业日再次日结（例如补做）不再提示。
        Assert.Equal(RestartFlowOutcome.NotDue, await harness.RunAsync());
        // 下一个营业日会再提示。
        Assert.Equal(RestartFlowOutcome.Later, await harness.RunAsync(BusinessDate.AddDays(1)));

        Assert.Equal(2, harness.Ui.PromptShown);
        Assert.Equal(0, harness.Restart.Calls);
    }

    [Fact]
    public async Task Later_records_skipped_audit_and_does_not_restart()
    {
        var harness = new Harness();
        harness.Ui.PromptChoice = RestartPromptChoice.Later;

        await harness.RunAsync();

        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("SYSTEM_RESTART", audit.OperationType);
        Assert.Equal("Skipped", audit.Outcome);
        Assert.Equal("PROMPT_LATER", audit.ReasonCode);
        Assert.Contains("uptimeDays=9.0", audit.SafeMessage);
        Assert.Equal(0, harness.Exit.Calls);
        Assert.True(harness.Ui.Hidden > 0);
    }

    [Fact]
    public async Task Hard_issue_before_prompt_blocks_silently_and_keeps_the_day_open_for_next_close()
    {
        var harness = new Harness();
        harness.Checker.Report = Report(Hard(RestartSafetyIssueCode.CartNotEmpty, 2));

        var outcome = await harness.RunAsync();

        Assert.Equal(RestartFlowOutcome.Blocked, outcome);
        Assert.Equal(0, harness.Ui.PromptShown);
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("Skipped", audit.Outcome);
        Assert.Equal("BLOCKED_CART", audit.ReasonCode);
        // 没弹窗就不占用当天的提示名额。
        Assert.Null(await harness.Store.GetLastPromptBusinessDateAsync());
    }

    [Fact]
    public async Task Confirmed_flow_runs_countdown_flushes_checkpoints_and_restarts()
    {
        var harness = new Harness();
        harness.Ui.PromptChoice = RestartPromptChoice.RestartNow;
        harness.Checker.Flush = new RestartUploadFlushResult(0, false, TimeSpan.FromSeconds(2));

        var outcome = await harness.RunWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Initiated, outcome);
        Assert.Equal(1, harness.Ui.CountdownShown);
        Assert.Equal(30, harness.Ui.CountdownTotal);
        Assert.Equal(0, harness.Ui.LastRemaining);
        Assert.Equal(1, harness.Ui.WorkingShown);
        Assert.Equal(1, harness.Checkpoint.Calls);
        Assert.Equal(TimeSpan.FromSeconds(30), harness.Checker.LastFlushWait);
        Assert.Equal(1, harness.Restart.Calls);
        Assert.Equal(TimeSpan.FromSeconds(15), harness.Restart.LastDelay);
        Assert.Equal(1, harness.Exit.Calls);

        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("Succeeded", audit.Outcome);
        Assert.Equal("INITIATED", audit.ReasonCode);
        Assert.Contains("uploadRemaining=0", audit.SafeMessage);
    }

    [Fact]
    public async Task Restart_now_button_skips_remaining_countdown_but_still_rechecks()
    {
        var harness = new Harness();
        harness.Ui.CountdownAction = RestartCountdownAction.RestartNow;

        var outcome = await harness.RunWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Initiated, outcome);
        // 点「立即重启」仍要过最后一次检查：预检 + 闸门 + 最终检查至少 3 次。
        Assert.True(harness.Checker.CheckCalls >= 3);
    }

    [Fact]
    public async Task Cancelling_countdown_stops_restart_and_records_audit()
    {
        var harness = new Harness();
        harness.Ui.CountdownAction = RestartCountdownAction.Cancel;

        var outcome = await harness.RunWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Cancelled, outcome);
        Assert.Equal(0, harness.Restart.Calls);
        Assert.Equal(0, harness.Exit.Calls);
        Assert.Equal(0, harness.Checkpoint.Calls);
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("COUNTDOWN_CANCELLED", audit.ReasonCode);
        Assert.Equal("Skipped", audit.Outcome);
    }

    [Fact]
    public async Task Hard_issue_after_confirm_shows_blocked_and_dismiss_ends_without_restart()
    {
        var harness = new Harness();
        harness.Checker.Sequence.Enqueue(RestartSafetyReport.Safe);                              // 预检
        harness.Checker.Sequence.Enqueue(Report(Hard(RestartSafetyIssueCode.PaymentActive)));    // 闸门
        harness.Ui.BlockedChoices.Enqueue(RestartBlockedChoice.Dismiss);

        var outcome = await harness.RunWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Blocked, outcome);
        Assert.Equal(1, harness.Ui.BlockedShown);
        Assert.Equal(0, harness.Restart.Calls);
        Assert.Contains(harness.Audit.Events, e => e.ReasonCode == "BLOCKED_PAYMENT" && e.Outcome == "Skipped");
    }

    [Fact]
    public async Task Blocked_then_recheck_proceeds_once_the_problem_is_fixed()
    {
        var harness = new Harness();
        harness.Checker.Sequence.Enqueue(RestartSafetyReport.Safe);                              // 预检
        harness.Checker.Sequence.Enqueue(Report(Hard(RestartSafetyIssueCode.CartNotEmpty, 1)));  // 闸门：被拦
        harness.Ui.BlockedChoices.Enqueue(RestartBlockedChoice.Recheck);                         // 之后默认安全

        var outcome = await harness.RunWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Initiated, outcome);
        Assert.Equal(1, harness.Ui.BlockedShown);
        Assert.Equal(1, harness.Restart.Calls);
    }

    [Fact]
    public async Task Blocked_go_to_pos_is_reported_to_the_caller()
    {
        var harness = new Harness();
        harness.Checker.Sequence.Enqueue(RestartSafetyReport.Safe);
        harness.Checker.Sequence.Enqueue(Report(Hard(RestartSafetyIssueCode.CartNotEmpty, 1)));
        harness.Ui.BlockedChoices.Enqueue(RestartBlockedChoice.GoToPos);

        Assert.Equal(RestartFlowOutcome.GoToPos, await harness.RunWithClockAsync());
        Assert.Equal(0, harness.Restart.Calls);
    }

    [Fact]
    public async Task Becoming_unsafe_at_the_final_check_never_restarts()
    {
        var harness = new Harness();
        // 预检、闸门通过；倒计时中途每 5 秒复查通过；最后一次检查才出问题（模拟最后一刻有新状态）。
        harness.Checker.UnsafeAfterCalls = 2 + 6; // 2 次前置 + 6 次倒计时复查（5/10/…/30 秒）之后
        harness.Checker.UnsafeForCalls = 2;       // 最终检查与回到闸门的重新检查都不安全
        harness.Ui.BlockedChoices.Enqueue(RestartBlockedChoice.Dismiss);

        var outcome = await harness.RunWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Blocked, outcome);
        Assert.Equal(0, harness.Restart.Calls);
        Assert.Equal(0, harness.Exit.Calls);
    }

    [Fact]
    public async Task Pending_uploads_after_wait_still_proceed_and_are_audited()
    {
        var harness = new Harness();
        harness.Checker.Flush = new RestartUploadFlushResult(3, true, TimeSpan.FromSeconds(30));

        var outcome = await harness.RunWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Initiated, outcome);
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("Succeeded", audit.Outcome);
        Assert.Equal("UPLOAD_PENDING_PROCEEDED", audit.ReasonCode);
        Assert.Contains("uploadRemaining=3", audit.SafeMessage);
    }

    [Fact]
    public async Task Checkpoint_failure_does_not_stop_the_restart()
    {
        var harness = new Harness();
        harness.Checkpoint.Exception = new InvalidOperationException("locked");

        var outcome = await harness.RunWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Initiated, outcome);
        Assert.Equal(1, harness.Restart.Calls);
    }

    [Fact]
    public async Task Command_failure_shows_failure_audits_failed_and_keeps_pos_running()
    {
        var harness = new Harness();
        harness.Restart.Result = new SystemRestartResult(SystemRestartStatus.Failed, 5, "Access is denied.");

        var outcome = await harness.RunWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Failed, outcome);
        Assert.Equal(0, harness.Exit.Calls);
        var failure = Assert.Single(harness.Ui.Failures);
        Assert.Equal(5, failure.ExitCode);
        Assert.StartsWith("RB-20261015-", failure.ReferenceCode);
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("Failed", audit.Outcome);
        Assert.Equal("COMMAND_FAILED", audit.ReasonCode);
        Assert.Contains("exitCode=5", audit.SafeMessage);
    }

    [Fact]
    public async Task Internal_errors_are_swallowed_and_never_reach_the_daily_close_flow()
    {
        var harness = new Harness();
        harness.Checker.Exception = new InvalidOperationException("kaboom");

        var outcome = await harness.RunAsync();

        Assert.Equal(RestartFlowOutcome.Failed, outcome);
        Assert.Equal(0, harness.Restart.Calls);
    }

    // ---------- 设置页「重启电脑」测试入口 ----------

    [Fact]
    public async Task Test_run_ignores_switch_uptime_and_daily_limit_and_skips_the_prompt()
    {
        // 开关关、开机才 1 小时、今天已提示过：测试入口都不受影响。
        var harness = new Harness { UptimeSeconds = 3600 };
        await harness.Store.SetPromptEnabledAsync(false);
        await harness.Store.MarkPromptedAsync(DateOnly.FromDateTime(BusinessDate));

        var outcome = await harness.RunTestWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Initiated, outcome);
        Assert.Equal(0, harness.Ui.PromptShown);          // 不再弹「建议重启」
        Assert.Equal(1, harness.Ui.CountdownShown);       // 仍有可取消倒计时
        Assert.Equal(1, harness.Restart.Calls);
        Assert.Equal(1, harness.Exit.Calls);
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("INITIATED", audit.ReasonCode);
        Assert.Contains("test=true", audit.SafeMessage);
    }

    [Fact]
    public async Task Test_run_does_not_consume_the_business_day_prompt_quota()
    {
        var harness = new Harness();
        harness.Ui.CountdownAction = RestartCountdownAction.Cancel;

        await harness.RunTestWithClockAsync();

        Assert.Null(await harness.Store.GetLastPromptBusinessDateAsync());
    }

    [Fact]
    public async Task Test_run_still_blocks_on_hard_issues_without_go_to_pos_option()
    {
        var harness = new Harness();
        harness.Checker.Report = Report(Hard(RestartSafetyIssueCode.CartNotEmpty, 2));

        var outcome = await harness.RunTestWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Blocked, outcome);
        Assert.Equal(1, harness.Ui.BlockedShown);
        Assert.False(harness.Ui.LastBlockedCanGoToPos);   // 设置页里不提供「回到收银台」
        Assert.Equal(0, harness.Restart.Calls);
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("BLOCKED_CART", audit.ReasonCode);
        Assert.Contains("test=true", audit.SafeMessage);
    }

    [Fact]
    public async Task Test_run_cancel_is_audited_as_test()
    {
        var harness = new Harness();
        harness.Ui.CountdownAction = RestartCountdownAction.Cancel;

        var outcome = await harness.RunTestWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Cancelled, outcome);
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("COUNTDOWN_CANCELLED", audit.ReasonCode);
        Assert.Contains("test=true", audit.SafeMessage);
        Assert.Equal(0, harness.Restart.Calls);
    }

    [Fact]
    public async Task Test_run_command_failure_shows_failure_and_keeps_running()
    {
        var harness = new Harness();
        harness.Restart.Result = new SystemRestartResult(SystemRestartStatus.Failed, 5, "denied");

        var outcome = await harness.RunTestWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Failed, outcome);
        Assert.Single(harness.Ui.Failures);
        Assert.Equal(0, harness.Exit.Calls);
        Assert.Contains("test=true", Assert.Single(harness.Audit.Events).SafeMessage);
    }

    // ---------- 工具 ----------

    private static RestartSafetyIssue Hard(RestartSafetyIssueCode code, int count = 0) =>
        new(code, RestartSafetySeverity.Hard, count);

    private static RestartSafetyReport Report(params RestartSafetyIssue[] issues) => new(issues);

    private sealed class Harness
    {
        private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));

        public long? UptimeSeconds { get; set; } = (long)TimeSpan.FromDays(9).TotalSeconds;

        public SystemRestartSettingsStore Store { get; } = new(new InMemorySettings());

        public FakeChecker Checker { get; } = new();

        public FakeRestartService Restart { get; } = new();

        public FakeUi Ui { get; } = new();

        public CountingExit Exit { get; } = new();

        public FakeCheckpoint Checkpoint { get; } = new();

        public FakeAudit Audit { get; } = new();

        public Task<RestartFlowOutcome> RunAsync(DateTime? businessDate = null) => CreateCoordinator()
            .RunAsync(Session(), businessDate ?? BusinessDate);

        public async Task<RestartFlowOutcome> RunTestWithClockAsync()
        {
            var task = CreateCoordinator().RunTestAsync(Session());
            await SystemRestartFoundationTests.AdvanceUntilCompletedAsync(task, _time);
            return await task;
        }

        public async Task<RestartFlowOutcome> RunWithClockAsync()
        {
            var task = CreateCoordinator().RunAsync(Session(), BusinessDate);
            await SystemRestartFoundationTests.AdvanceUntilCompletedAsync(task, _time);
            return await task;
        }

        private PostDailyCloseRestartCoordinator CreateCoordinator() => new(
            new FakeSampler(UptimeSeconds),
            Store,
            Checker,
            Restart,
            Ui,
            Exit,
            SystemRestartOptions.Default,
            Checkpoint,
            Audit,
            _time);

        private static PosSessionState Session() =>
            new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
    }

    private sealed class FakeSampler(long? uptimeSeconds) : IRuntimeResourceSampler
    {
        public RuntimeResourceSnapshot Capture() => new(uptimeSeconds, null, null);
    }

    private sealed class FakeChecker : IRestartSafetyChecker
    {
        public RestartSafetyReport Report { get; set; } = RestartSafetyReport.Safe;

        /// <summary>按顺序逐次返回；用完后回落到 <see cref="Report"/>。</summary>
        public Queue<RestartSafetyReport> Sequence { get; } = new();

        public int? UnsafeAfterCalls { get; set; }

        public int UnsafeForCalls { get; set; } = 1;

        public Exception? Exception { get; set; }

        public int CheckCalls { get; private set; }

        public RestartUploadFlushResult Flush { get; set; } = new(0, false, TimeSpan.Zero);

        public TimeSpan LastFlushWait { get; private set; }

        public Task<RestartSafetyReport> CheckAsync(CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            if (Exception is not null)
            {
                return Task.FromException<RestartSafetyReport>(Exception);
            }

            if (Sequence.Count > 0)
            {
                return Task.FromResult(Sequence.Dequeue());
            }

            if (UnsafeAfterCalls is { } limit && CheckCalls > limit)
            {
                if (--UnsafeForCalls <= 0)
                {
                    UnsafeAfterCalls = null; // 连续不安全指定次数之后恢复安全
                }

                return Task.FromResult(PostDailyCloseRestartCoordinatorTests.Report(
                    Hard(RestartSafetyIssueCode.CartNotEmpty, 1)));
            }

            return Task.FromResult(Report);
        }

        public Task<RestartUploadFlushResult> FlushUploadsAsync(TimeSpan maxWait, CancellationToken cancellationToken = default)
        {
            LastFlushWait = maxWait;
            return Task.FromResult(Flush);
        }
    }

    private sealed class FakeRestartService : ISystemRestartService
    {
        public SystemRestartResult Result { get; set; } = new(SystemRestartStatus.Scheduled, 0);

        public int Calls { get; private set; }

        public TimeSpan LastDelay { get; private set; }

        public Task<SystemRestartResult> RestartAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastDelay = delay;
            return Task.FromResult(Result);
        }
    }

    private sealed class CountingExit : IApplicationExitService
    {
        public int Calls { get; private set; }

        public void Exit() => Calls++;
    }

    private sealed class FakeCheckpoint : ILocalSqliteCheckpointService
    {
        public int Calls { get; private set; }

        public Exception? Exception { get; set; }

        public Task CheckpointWalAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
        }
    }

    private sealed class FakeAudit : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent) => Events.Add(auditEvent);
    }

    private sealed class FakeUi : IRestartPromptUi
    {
        public RestartPromptChoice PromptChoice { get; set; } = RestartPromptChoice.RestartNow;

        public Queue<RestartBlockedChoice> BlockedChoices { get; } = new();

        /// <summary>倒计时界面的用户动作：null 表示不点任何按钮、让倒计时自然走完。</summary>
        public RestartCountdownAction? CountdownAction { get; set; }

        public int PromptShown { get; private set; }

        public int BlockedShown { get; private set; }

        public bool LastBlockedCanGoToPos { get; private set; } = true;

        public int CountdownShown { get; private set; }

        public int CountdownTotal { get; private set; }

        public int LastRemaining { get; private set; } = -1;

        public int WorkingShown { get; private set; }

        public int Hidden { get; private set; }

        public List<RestartFailureInfo> Failures { get; } = [];

        public Task<RestartPromptChoice> ShowPromptAsync(RestartPromptInfo info, CancellationToken cancellationToken)
        {
            PromptShown++;
            return Task.FromResult(PromptChoice);
        }

        public Task<RestartBlockedChoice> ShowBlockedAsync(
            RestartSafetyReport report,
            bool canGoToPos,
            CancellationToken cancellationToken)
        {
            BlockedShown++;
            LastBlockedCanGoToPos = canGoToPos;
            return Task.FromResult(
                BlockedChoices.Count > 0 ? BlockedChoices.Dequeue() : RestartBlockedChoice.Dismiss);
        }

        public Task<RestartCountdownAction> ShowCountdown(int totalSeconds, CancellationToken cancellationToken)
        {
            CountdownShown++;
            CountdownTotal = totalSeconds;
            return CountdownAction is { } action
                ? Task.FromResult(action)
                : new TaskCompletionSource<RestartCountdownAction>().Task;
        }

        public void SetCountdownRemaining(int remainingSeconds) => LastRemaining = remainingSeconds;

        public void ShowWorking() => WorkingShown++;

        public Task ShowFailureAsync(RestartFailureInfo info, CancellationToken cancellationToken)
        {
            Failures.Add(info);
            return Task.CompletedTask;
        }

        public void Hide() => Hidden++;
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
}
