using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>
/// 日结后凌晨自动重启协调器的完整流程：日结后排定（不弹窗）、凌晨到点无人值守执行（阻止、窗口作废、倒计时取消、成功与失败），
/// 以及设置页测试入口；全部用虚拟时间（本地时区固定为 UTC）与假界面驱动。
/// </summary>
public sealed class PostDailyCloseRestartCoordinatorTests
{
    private static readonly DateTime BusinessDate = new(2026, 10, 15);

    // 默认「现在」是 2026-10-15 08:00（本地 = UTC），下一个凌晨 3 点是 10-16 03:00。
    private static readonly DateTimeOffset NextNight = new(2026, 10, 16, 3, 0, 0, TimeSpan.Zero);

    // ---------- 日结后排定 ----------

    [Fact]
    public async Task Not_scheduled_when_switch_is_disabled()
    {
        var harness = new Harness();
        await harness.Store.SetPromptEnabledAsync(false);

        Assert.Null(await harness.ScheduleAsync());
        Assert.Null(await harness.Store.GetScheduledRestartAtAsync());
    }

    [Fact]
    public async Task Not_scheduled_when_uptime_is_below_threshold()
    {
        var harness = new Harness { UptimeSeconds = (long)TimeSpan.FromDays(6.9).TotalSeconds };

        Assert.Null(await harness.ScheduleAsync());
        Assert.Empty(harness.Audit.Events);
    }

    [Fact]
    public async Task Not_scheduled_when_uptime_is_unknown()
    {
        var harness = new Harness { UptimeSeconds = null };

        Assert.Null(await harness.ScheduleAsync());
    }

    [Fact]
    public async Task Schedules_next_overnight_restart_without_any_prompt()
    {
        var harness = new Harness();

        var restartAt = await harness.ScheduleAsync();

        Assert.Equal(NextNight, restartAt);
        Assert.Equal(NextNight, await harness.Store.GetScheduledRestartAtAsync());
        Assert.Equal(0, harness.Ui.PromptShown);
        Assert.Equal(0, harness.Ui.CountdownShown);
        Assert.Equal(0, harness.Checker.CheckCalls);
        Assert.Equal(0, harness.Restart.Calls);
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("SYSTEM_RESTART", audit.OperationType);
        Assert.Equal("Skipped", audit.Outcome);
        Assert.Equal("OVERNIGHT_SCHEDULED", audit.ReasonCode);
        Assert.Contains("uptimeDays=9.0", audit.SafeMessage);
        Assert.Contains("restartAt=2026-10-16T03:00", audit.SafeMessage);
    }

    [Fact]
    public async Task Daily_close_after_midnight_schedules_the_same_night()
    {
        var harness = new Harness(new DateTimeOffset(2026, 10, 16, 1, 30, 0, TimeSpan.Zero));

        Assert.Equal(NextNight, await harness.ScheduleAsync());
    }

    [Fact]
    public async Task Daily_close_inside_the_window_is_due_right_away()
    {
        var now = new DateTimeOffset(2026, 10, 16, 3, 40, 0, TimeSpan.Zero);
        var harness = new Harness(now);

        Assert.Equal(now, await harness.ScheduleAsync());
    }

    [Fact]
    public async Task Same_business_date_keeps_the_original_schedule()
    {
        var harness = new Harness();

        Assert.Equal(NextNight, await harness.ScheduleAsync());
        // 同一营业日补做日结：沿用原时间、不重复审计。
        Assert.Equal(NextNight, await harness.ScheduleAsync());
        Assert.Single(harness.Audit.Events);
    }

    [Fact]
    public async Task Schedule_errors_are_swallowed_and_never_reach_the_daily_close_flow()
    {
        var harness = new Harness();
        harness.Settings.Exception = new InvalidOperationException("db locked");

        Assert.Null(await harness.ScheduleAsync());
    }

    [Fact]
    public void Overnight_time_skips_a_daylight_saving_gap()
    {
        // 自定义时区：10-04 03:00 起跳到 04:00（03:00 不存在）。
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 4),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 4, 5));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test DST", TimeSpan.FromHours(10), "Test DST", "Test", "Test DST", [rule]);
        var localNow = new DateTimeOffset(2026, 10, 3, 22, 0, 0, TimeSpan.FromHours(10));

        var restartAt = PostDailyCloseRestartCoordinator.ComputeOvernightRestartAt(localNow, zone, SystemRestartOptions.Default);

        Assert.Equal(new DateTimeOffset(2026, 10, 4, 4, 0, 0, TimeSpan.FromHours(11)), restartAt);
    }

    // ---------- 凌晨到点无人值守执行 ----------

    [Fact]
    public async Task Overnight_check_is_not_due_without_a_schedule()
    {
        var harness = new Harness();

        Assert.Equal(RestartFlowOutcome.NotDue, await harness.RunOvernightAsync());
        Assert.Equal(0, harness.Checker.CheckCalls);
    }

    [Fact]
    public async Task Overnight_check_is_not_due_before_the_scheduled_time()
    {
        var harness = new Harness();
        await harness.ScheduleAsync();

        Assert.Equal(RestartFlowOutcome.NotDue, await harness.RunOvernightAsync());
        Assert.Equal(0, harness.Ui.CountdownShown);
        Assert.Equal(NextNight, await harness.Store.GetScheduledRestartAtAsync());
    }

    [Fact]
    public async Task Due_restart_runs_countdown_flushes_checkpoints_and_restarts_unattended()
    {
        var harness = new Harness();
        await harness.SetDueAsync();
        harness.Checker.Flush = new RestartUploadFlushResult(0, false, TimeSpan.FromSeconds(2));

        var outcome = await harness.RunOvernightWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Initiated, outcome);
        Assert.Equal(0, harness.Ui.PromptShown);
        Assert.Equal(0, harness.Ui.BlockedShown);
        Assert.Equal(1, harness.Ui.CountdownShown);
        Assert.Equal(30, harness.Ui.CountdownTotal);
        Assert.Equal(0, harness.Ui.LastRemaining);
        Assert.Equal(1, harness.Ui.WorkingShown);
        Assert.Equal(1, harness.Checkpoint.Calls);
        Assert.Equal(TimeSpan.FromSeconds(30), harness.Checker.LastFlushWait);
        Assert.Equal(1, harness.Restart.Calls);
        Assert.Equal(TimeSpan.FromSeconds(15), harness.Restart.LastDelay);
        Assert.Equal(1, harness.Exit.Calls);
        Assert.Null(await harness.Store.GetScheduledRestartAtAsync());

        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("Succeeded", audit.Outcome);
        Assert.Equal("INITIATED", audit.ReasonCode);
        Assert.Contains("uploadRemaining=0", audit.SafeMessage);
    }

    [Fact]
    public async Task Hard_issue_keeps_the_schedule_shows_no_ui_and_audits_once_then_restarts_when_clear()
    {
        var harness = new Harness();
        await harness.SetDueAsync();
        harness.Checker.Report = Report(Hard(RestartSafetyIssueCode.CartNotEmpty, 2));

        Assert.Equal(RestartFlowOutcome.Blocked, await harness.RunOvernightAsync());
        Assert.Equal(RestartFlowOutcome.Blocked, await harness.RunOvernightAsync());

        Assert.Equal(0, harness.Ui.BlockedShown);       // 无人值守：不弹阻止界面
        Assert.Equal(0, harness.Ui.CountdownShown);
        Assert.Equal(0, harness.Restart.Calls);
        Assert.NotNull(await harness.Store.GetScheduledRestartAtAsync());
        var audit = Assert.Single(harness.Audit.Events); // 同一原因只记一次
        Assert.Equal("BLOCKED_CART", audit.ReasonCode);
        Assert.Contains("overnight=true", audit.SafeMessage);

        harness.Checker.Report = RestartSafetyReport.Safe;
        Assert.Equal(RestartFlowOutcome.Initiated, await harness.RunOvernightWithClockAsync());
        Assert.Equal(1, harness.Restart.Calls);
    }

    [Fact]
    public async Task Missed_window_clears_the_schedule_and_never_restarts_during_business_hours()
    {
        var harness = new Harness();
        await harness.Store.SetScheduledRestartAtAsync(harness.Now - TimeSpan.FromHours(3));

        Assert.Equal(RestartFlowOutcome.NotDue, await harness.RunOvernightAsync());

        Assert.Null(await harness.Store.GetScheduledRestartAtAsync());
        Assert.Equal(0, harness.Checker.CheckCalls);
        Assert.Equal(0, harness.Restart.Calls);
        Assert.Equal("OVERNIGHT_WINDOW_MISSED", Assert.Single(harness.Audit.Events).ReasonCode);
    }

    [Fact]
    public async Task Turning_the_switch_off_after_scheduling_cancels_the_restart()
    {
        var harness = new Harness();
        await harness.SetDueAsync();
        await harness.Store.SetPromptEnabledAsync(false);

        Assert.Equal(RestartFlowOutcome.NotDue, await harness.RunOvernightAsync());
        Assert.Null(await harness.Store.GetScheduledRestartAtAsync());
        Assert.Equal(0, harness.Restart.Calls);
    }

    [Fact]
    public async Task Computer_already_restarted_since_scheduling_cancels_the_restart()
    {
        var harness = new Harness();
        await harness.SetDueAsync();
        harness.UptimeSeconds = 3600;

        Assert.Equal(RestartFlowOutcome.NotDue, await harness.RunOvernightAsync());
        Assert.Null(await harness.Store.GetScheduledRestartAtAsync());
        Assert.Equal(0, harness.Restart.Calls);
    }

    [Fact]
    public async Task Cancelling_the_overnight_countdown_drops_tonights_restart()
    {
        var harness = new Harness();
        await harness.SetDueAsync();
        harness.Ui.CountdownAction = RestartCountdownAction.Cancel;

        var outcome = await harness.RunOvernightWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Cancelled, outcome);
        Assert.Equal(0, harness.Restart.Calls);
        Assert.Equal(0, harness.Checkpoint.Calls);
        Assert.Null(await harness.Store.GetScheduledRestartAtAsync());
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("COUNTDOWN_CANCELLED", audit.ReasonCode);
        Assert.Contains("overnight=true", audit.SafeMessage);
    }

    [Fact]
    public async Task Becoming_unsafe_during_the_countdown_hides_it_and_keeps_the_schedule()
    {
        var harness = new Harness();
        await harness.SetDueAsync();
        harness.Checker.UnsafeAfterCalls = 1; // 第一次检查通过，倒计时中的复查出问题

        var outcome = await harness.RunOvernightWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Blocked, outcome);
        Assert.Equal(0, harness.Restart.Calls);
        Assert.Equal(0, harness.Ui.BlockedShown);
        Assert.True(harness.Ui.Hidden > 0);
        Assert.NotNull(await harness.Store.GetScheduledRestartAtAsync());
    }

    [Fact]
    public async Task Pending_uploads_after_wait_still_proceed_and_are_audited()
    {
        var harness = new Harness();
        await harness.SetDueAsync();
        harness.Checker.Flush = new RestartUploadFlushResult(3, true, TimeSpan.FromSeconds(30));

        var outcome = await harness.RunOvernightWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Initiated, outcome);
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("UPLOAD_PENDING_PROCEEDED", audit.ReasonCode);
        Assert.Contains("uploadRemaining=3", audit.SafeMessage);
    }

    [Fact]
    public async Task Checkpoint_failure_does_not_stop_the_restart()
    {
        var harness = new Harness();
        await harness.SetDueAsync();
        harness.Checkpoint.Exception = new InvalidOperationException("locked");

        Assert.Equal(RestartFlowOutcome.Initiated, await harness.RunOvernightWithClockAsync());
        Assert.Equal(1, harness.Restart.Calls);
    }

    [Fact]
    public async Task Command_failure_shows_failure_clears_schedule_and_keeps_pos_running()
    {
        var harness = new Harness();
        await harness.SetDueAsync();
        harness.Restart.Result = new SystemRestartResult(SystemRestartStatus.Failed, 5, "Access is denied.");

        var outcome = await harness.RunOvernightWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Failed, outcome);
        Assert.Equal(0, harness.Exit.Calls);
        var failure = Assert.Single(harness.Ui.Failures);
        Assert.Equal(5, failure.ExitCode);
        Assert.StartsWith("RB-20261015-", failure.ReferenceCode);
        Assert.Null(await harness.Store.GetScheduledRestartAtAsync()); // 不在本窗口内反复尝试
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("Failed", audit.Outcome);
        Assert.Equal("COMMAND_FAILED", audit.ReasonCode);
    }

    [Fact]
    public async Task Overnight_internal_errors_are_swallowed()
    {
        var harness = new Harness();
        await harness.SetDueAsync();
        harness.Checker.Exception = new InvalidOperationException("kaboom");

        Assert.Equal(RestartFlowOutcome.Failed, await harness.RunOvernightAsync());
        Assert.Equal(0, harness.Restart.Calls);
    }

    // ---------- 设置页「重启电脑」测试入口 ----------

    [Fact]
    public async Task Test_run_ignores_switch_uptime_and_schedule_and_skips_the_prompt()
    {
        // 开关关、开机才 1 小时、今天已排过：测试入口都不受影响。
        var harness = new Harness { UptimeSeconds = 3600 };
        await harness.Store.SetPromptEnabledAsync(false);
        await harness.Store.MarkPromptedAsync(DateOnly.FromDateTime(BusinessDate));

        var outcome = await harness.RunTestWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Initiated, outcome);
        Assert.Equal(0, harness.Ui.PromptShown);          // 不弹「建议重启」
        Assert.Equal(1, harness.Ui.CountdownShown);       // 仍有可取消倒计时
        Assert.Equal(1, harness.Restart.Calls);
        Assert.Equal(1, harness.Exit.Calls);
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("INITIATED", audit.ReasonCode);
        Assert.Contains("test=true", audit.SafeMessage);
    }

    [Fact]
    public async Task Test_run_does_not_consume_the_business_day_schedule_quota()
    {
        var harness = new Harness();
        harness.Ui.CountdownAction = RestartCountdownAction.Cancel;

        await harness.RunTestWithClockAsync();

        Assert.Null(await harness.Store.GetLastPromptBusinessDateAsync());
        Assert.Null(await harness.Store.GetScheduledRestartAtAsync());
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
    public async Task Test_run_blocked_then_recheck_proceeds_once_the_problem_is_fixed()
    {
        var harness = new Harness();
        harness.Checker.Sequence.Enqueue(Report(Hard(RestartSafetyIssueCode.CartNotEmpty, 1)));  // 闸门：被拦
        harness.Ui.BlockedChoices.Enqueue(RestartBlockedChoice.Recheck);                         // 之后默认安全

        var outcome = await harness.RunTestWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Initiated, outcome);
        Assert.Equal(1, harness.Ui.BlockedShown);
        Assert.Equal(1, harness.Restart.Calls);
    }

    [Fact]
    public async Task Test_run_restart_now_button_skips_remaining_countdown_but_still_rechecks()
    {
        var harness = new Harness();
        harness.Ui.CountdownAction = RestartCountdownAction.RestartNow;

        var outcome = await harness.RunTestWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Initiated, outcome);
        // 点「立即重启」仍要过最后一次检查：闸门 + 最终检查至少 2 次。
        Assert.True(harness.Checker.CheckCalls >= 2);
    }

    [Fact]
    public async Task Test_run_becoming_unsafe_at_the_final_check_never_restarts()
    {
        var harness = new Harness();
        // 闸门通过；倒计时中途每 5 秒复查通过；最后一次检查才出问题（模拟最后一刻有新状态）。
        harness.Checker.UnsafeAfterCalls = 1 + 6; // 1 次闸门 + 6 次倒计时复查（5/10/…/30 秒）之后
        harness.Checker.UnsafeForCalls = 2;       // 最终检查与回到闸门的重新检查都不安全
        harness.Ui.BlockedChoices.Enqueue(RestartBlockedChoice.Dismiss);

        var outcome = await harness.RunTestWithClockAsync();

        Assert.Equal(RestartFlowOutcome.Blocked, outcome);
        Assert.Equal(0, harness.Restart.Calls);
        Assert.Equal(0, harness.Exit.Calls);
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
        private readonly FakeTimeProvider _time;
        private PostDailyCloseRestartCoordinator? _coordinator;

        public Harness(DateTimeOffset? now = null)
        {
            _time = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
            // 本地时区固定为 UTC，凌晨重启时间与机器所在时区无关。
            _time.SetLocalTimeZone(TimeZoneInfo.Utc);
            Store = new SystemRestartSettingsStore(Settings);
        }

        public long? UptimeSeconds { get; set; } = (long)TimeSpan.FromDays(9).TotalSeconds;

        public DateTimeOffset Now => _time.GetUtcNow();

        public InMemorySettings Settings { get; } = new();

        public SystemRestartSettingsStore Store { get; }

        public FakeChecker Checker { get; } = new();

        public FakeRestartService Restart { get; } = new();

        public FakeUi Ui { get; } = new();

        public CountingExit Exit { get; } = new();

        public FakeCheckpoint Checkpoint { get; } = new();

        public FakeAudit Audit { get; } = new();

        public Task<DateTimeOffset?> ScheduleAsync(DateTime? businessDate = null) => Coordinator
            .ScheduleAfterDailyCloseAsync(Session(), businessDate ?? BusinessDate);

        /// <summary>模拟日结后已排定、且刚好到点（1 分钟前）。</summary>
        public Task SetDueAsync() => Store.SetScheduledRestartAtAsync(Now - TimeSpan.FromMinutes(1));

        public Task<RestartFlowOutcome> RunOvernightAsync() => Coordinator.RunScheduledIfDueAsync(Session());

        public async Task<RestartFlowOutcome> RunOvernightWithClockAsync()
        {
            var task = Coordinator.RunScheduledIfDueAsync(Session());
            await SystemRestartFoundationTests.AdvanceUntilCompletedAsync(task, _time);
            return await task;
        }

        public async Task<RestartFlowOutcome> RunTestWithClockAsync()
        {
            var task = Coordinator.RunTestAsync(Session());
            await SystemRestartFoundationTests.AdvanceUntilCompletedAsync(task, _time);
            return await task;
        }

        // 同一个实例贯穿整个测试：生产里协调器是单例（「同一原因只审计一次」依赖它）。
        private PostDailyCloseRestartCoordinator Coordinator => _coordinator ??= new(
            new FakeSampler(() => UptimeSeconds),
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

    private sealed class FakeSampler(Func<long?> uptimeSeconds) : IRuntimeResourceSampler
    {
        public RuntimeResourceSnapshot Capture() => new(uptimeSeconds(), null, null);
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

        /// <summary>设置后读取会失败（模拟本地库被锁）。</summary>
        public Exception? Exception { get; set; }

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
            Exception is not null
                ? Task.FromException<string?>(Exception)
                : Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

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
