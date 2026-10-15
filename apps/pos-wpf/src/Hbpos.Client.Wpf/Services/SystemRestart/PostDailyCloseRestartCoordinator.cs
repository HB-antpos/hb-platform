using System.Globalization;
using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.Services;

public enum RestartPromptChoice
{
    RestartNow,
    Later
}

public enum RestartBlockedChoice
{
    /// <summary>处理完问题后重新检查。</summary>
    Recheck,

    /// <summary>回到收银台去处理（购物车 / 付款相关）。</summary>
    GoToPos,

    Dismiss
}

public enum RestartCountdownAction
{
    RestartNow,
    Cancel
}

public sealed record RestartPromptInfo(TimeSpan Uptime, int PendingUploadCount);

public sealed record RestartFailureInfo(int ExitCode, string? Detail, string ReferenceCode, TimeSpan Uptime);

/// <summary>
/// 重启流程的界面抽象：协调器只描述「现在该显示什么」，倒计时、检查、审计都在协调器里，
/// 这样流程能用 FakeTimeProvider 与假界面完整单测，界面实现只负责状态与按钮。
/// </summary>
public interface IRestartPromptUi
{
    Task<RestartPromptChoice> ShowPromptAsync(RestartPromptInfo info, CancellationToken cancellationToken);

    Task<RestartBlockedChoice> ShowBlockedAsync(
        RestartSafetyReport report,
        bool canGoToPos,
        CancellationToken cancellationToken);

    /// <summary>进入倒计时界面；返回的任务在收银员点「取消重启 / 立即重启」时完成。</summary>
    Task<RestartCountdownAction> ShowCountdown(int totalSeconds, CancellationToken cancellationToken);

    void SetCountdownRemaining(int remainingSeconds);

    /// <summary>倒计时结束后：正在冲刷上传、保存本地数据并重启，界面锁定、无按钮。</summary>
    void ShowWorking();

    Task ShowFailureAsync(RestartFailureInfo info, CancellationToken cancellationToken);

    void Hide();
}

public enum RestartFlowOutcome
{
    /// <summary>不满足提示条件（开关关、开机不够久、今天已提示过）。</summary>
    NotDue,

    Later,

    /// <summary>有硬性问题，没有重启。</summary>
    Blocked,

    /// <summary>被拦后收银员选择回到收银台处理。</summary>
    GoToPos,

    Cancelled,

    /// <summary>已下发系统重启，应用正在退出。</summary>
    Initiated,

    Failed
}

public interface IPostDailyCloseRestartCoordinator
{
    /// <summary>
    /// 日结成功保存之后调用。内部自行判断是否需要提示；任何异常都在内部消化，绝不影响日结流程。
    /// </summary>
    Task<RestartFlowOutcome> RunAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置页「重启电脑」测试入口：跳过开关、开机时长与每营业日一次的限制，也不再弹「建议重启」提示，
    /// 直接走「安全检查 → 倒计时 → 冲刷上传 → 重启」；安全检查、可取消倒计时、日志与审计与正式流程完全一致（审计带 test=true）。
    /// </summary>
    Task<RestartFlowOutcome> RunTestAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default);
}

public sealed class PostDailyCloseRestartCoordinator(
    IRuntimeResourceSampler resourceSampler,
    ISystemRestartSettingsStore settingsStore,
    IRestartSafetyChecker safetyChecker,
    ISystemRestartService restartService,
    IRestartPromptUi ui,
    IApplicationExitService exitService,
    SystemRestartOptions options,
    ILocalSqliteCheckpointService? checkpointService = null,
    IOperationAuditLogger? auditLogger = null,
    TimeProvider? timeProvider = null) : IPostDailyCloseRestartCoordinator
{
    private const string LogCategory = "RestartAfterClose";

    // 倒计时期间每隔几秒复查一次：收银员若在倒计时里还偷偷做了操作（理论上弹窗是模态的），能及时发现。
    private const int RecheckEverySeconds = 5;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<RestartFlowOutcome> RunAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        var correlation = OperationAuditEvents.CreateCorrelation();
        var log = new ApplicationLogContext(TraceId: correlation.TraceId);
        var uptime = TimeSpan.Zero;
        try
        {
            if (!await settingsStore.IsPromptEnabledAsync(cancellationToken))
            {
                return RestartFlowOutcome.NotDue;
            }

            var uptimeSeconds = resourceSampler.Capture().SystemUptimeSeconds;
            if (uptimeSeconds is null)
            {
                return RestartFlowOutcome.NotDue;
            }

            uptime = TimeSpan.FromSeconds(uptimeSeconds.Value);
            if (uptime < options.UptimeThreshold)
            {
                return RestartFlowOutcome.NotDue;
            }

            var businessDay = DateOnly.FromDateTime(businessDate);
            if (await settingsStore.GetLastPromptBusinessDateAsync(cancellationToken) == businessDay)
            {
                ConsoleLog.WriteInformation(
                    LogCategory,
                    $"prompt skipped: already prompted for businessDate={businessDay:yyyy-MM-dd}",
                    log);
                return RestartFlowOutcome.NotDue;
            }

            // 弹窗前先预检：一开始就有硬性问题时不打扰收银员（弹了又拦只会让人困惑），只留日志与审计。
            var preflight = await safetyChecker.CheckAsync(cancellationToken);
            if (!preflight.IsSafe)
            {
                LogBlocked("blocked-before-prompt", preflight, log);
                RecordAudit(session, "Skipped", preflight.HardIssues[0].ReasonCode, Describe(uptime, preflight), correlation);
                return RestartFlowOutcome.Blocked;
            }

            // 营业日标记在弹出时就写：每营业日最多提示一次，哪怕随后程序被关掉也不会反复弹。
            await settingsStore.MarkPromptedAsync(businessDay, cancellationToken);

            var pendingUploads = preflight.SoftIssues.Sum(issue => issue.Count);
            ConsoleLog.WriteInformation(
                LogCategory,
                $"prompt-shown uptimeSeconds={uptimeSeconds.Value} thresholdDays={options.UptimeThresholdDays} businessDate={businessDay:yyyy-MM-dd} pendingUploads={pendingUploads}",
                log);

            var choice = await ui.ShowPromptAsync(new RestartPromptInfo(uptime, pendingUploads), cancellationToken);
            if (choice == RestartPromptChoice.Later)
            {
                ui.Hide();
                ConsoleLog.WriteInformation(LogCategory, "prompt-later", log);
                RecordAudit(session, "Skipped", "PROMPT_LATER", Describe(uptime, null), correlation);
                return RestartFlowOutcome.Later;
            }

            ConsoleLog.WriteInformation(LogCategory, "confirmed", log);
            return await ConfirmedFlowAsync(session, uptime, correlation, log, isTest: false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            HideQuietly();
            return RestartFlowOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            // 重启提示是日结之后的附加功能：自身出任何问题都只记日志，不能反过来影响日结与收银。
            ConsoleLog.WriteError(LogCategory, $"restart flow failed error={ex.GetType().Name} message={ex.Message}", log, ex);
            HideQuietly();
            return RestartFlowOutcome.Failed;
        }
    }

    public async Task<RestartFlowOutcome> RunTestAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var correlation = OperationAuditEvents.CreateCorrelation();
        var log = new ApplicationLogContext(TraceId: correlation.TraceId);
        try
        {
            var uptimeSeconds = resourceSampler.Capture().SystemUptimeSeconds ?? 0;
            var uptime = TimeSpan.FromSeconds(uptimeSeconds);
            ConsoleLog.WriteInformation(LogCategory, $"test-started uptimeSeconds={uptimeSeconds}", log);
            return await ConfirmedFlowAsync(session, uptime, correlation, log, isTest: true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            HideQuietly();
            return RestartFlowOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError(LogCategory, $"restart test failed error={ex.GetType().Name} message={ex.Message}", log, ex);
            HideQuietly();
            return RestartFlowOutcome.Failed;
        }
    }

    private async Task<RestartFlowOutcome> ConfirmedFlowAsync(
        PosSessionState session,
        TimeSpan uptime,
        (string CorrelationId, string TraceId) correlation,
        ApplicationLogContext log,
        bool isTest,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            // 闸门：任何硬性问题都说明原因并停下，由收银员处理后点「重新检查」。
            var report = await safetyChecker.CheckAsync(cancellationToken);
            if (!report.IsSafe)
            {
                LogBlocked("blocked", report, log);
                RecordAudit(session, "Skipped", report.HardIssues[0].ReasonCode, Describe(uptime, report, isTest), correlation);
                // 设置页测试时不提供「回到收银台」（当前就不在收银流程里）。
                var blocked = await ui.ShowBlockedAsync(report, canGoToPos: !isTest, cancellationToken);
                if (blocked == RestartBlockedChoice.Recheck)
                {
                    continue;
                }

                ui.Hide();
                return blocked == RestartBlockedChoice.GoToPos
                    ? RestartFlowOutcome.GoToPos
                    : RestartFlowOutcome.Blocked;
            }

            var countdown = await RunCountdownAsync(cancellationToken);
            if (countdown == CountdownEnd.Cancelled)
            {
                ui.Hide();
                ConsoleLog.WriteInformation(LogCategory, "countdown-cancelled", log);
                RecordAudit(session, "Skipped", "COUNTDOWN_CANCELLED", Describe(uptime, null, isTest), correlation);
                return RestartFlowOutcome.Cancelled;
            }

            if (countdown == CountdownEnd.BecameUnsafe)
            {
                // 回到循环开头：闸门会重新检查并把原因展示出来。
                continue;
            }

            break;
        }

        return await ExecuteRestartAsync(session, uptime, correlation, log, isTest, cancellationToken);
    }

    private enum CountdownEnd
    {
        Completed,
        Cancelled,
        BecameUnsafe
    }

    private async Task<CountdownEnd> RunCountdownAsync(CancellationToken cancellationToken)
    {
        var total = options.CountdownSeconds;
        var userAction = ui.ShowCountdown(total, cancellationToken);

        for (var remaining = total; remaining > 0; remaining--)
        {
            ui.SetCountdownRemaining(remaining);
            var tick = Task.Delay(TimeSpan.FromSeconds(1), _clock, cancellationToken);
            var finished = await Task.WhenAny(userAction, tick);
            if (finished == userAction)
            {
                if (await userAction == RestartCountdownAction.Cancel)
                {
                    return CountdownEnd.Cancelled;
                }

                // 「立即重启」：跳过剩余等待，但仍要过最后一次检查。
                break;
            }

            if (remaining % RecheckEverySeconds == 0 &&
                !(await safetyChecker.CheckAsync(cancellationToken)).IsSafe)
            {
                return CountdownEnd.BecameUnsafe;
            }
        }

        // 倒计时结束（或点了立即重启）时再检查一遍：这一刻才是真正决定重启的时刻。
        ui.SetCountdownRemaining(0);
        return (await safetyChecker.CheckAsync(cancellationToken)).IsSafe
            ? CountdownEnd.Completed
            : CountdownEnd.BecameUnsafe;
    }

    private async Task<RestartFlowOutcome> ExecuteRestartAsync(
        PosSessionState session,
        TimeSpan uptime,
        (string CorrelationId, string TraceId) correlation,
        ApplicationLogContext log,
        bool isTest,
        CancellationToken cancellationToken)
    {
        ui.ShowWorking();

        // 先冲刷上传：最多等 UploadWaitSeconds，仍没传完就放行（队列都在 SQLite 里，重启后自动续传）。
        var flush = await safetyChecker.FlushUploadsAsync(
            TimeSpan.FromSeconds(options.UploadWaitSeconds),
            cancellationToken);
        ConsoleLog.WriteInformation(
            LogCategory,
            $"flush-result remaining={flush.Remaining} timedOut={flush.TimedOut} elapsedMs={flush.Elapsed.TotalMilliseconds:0}",
            log);

        // 把 WAL 合并回主库再重启；失败只记日志（SQLite 本身崩溃安全，这一步只是让主库文件更「干净」）。
        if (checkpointService is not null)
        {
            try
            {
                await checkpointService.CheckpointWalAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ConsoleLog.WriteWarning(LogCategory, $"checkpoint failed error={ex.GetType().Name} message={ex.Message}", log);
            }
        }

        var result = await restartService.RestartAsync(
            TimeSpan.FromSeconds(options.SystemShutdownDelaySeconds),
            cancellationToken);

        if (result.Succeeded)
        {
            ConsoleLog.WriteInformation(
                LogCategory,
                $"shutdown-invoked status={result.Status} exitCode={result.ExitCode} uploadRemaining={flush.Remaining}",
                log);
            RecordAudit(
                session,
                "Succeeded",
                flush.Remaining > 0 ? "UPLOAD_PENDING_PROCEEDED" : "INITIATED",
                $"uptimeDays={FormatDays(uptime)};thresholdDays={options.UptimeThresholdDays};exitCode={result.ExitCode};uploadRemaining={flush.Remaining}{TestSuffix(isTest)}",
                correlation);

            // 界面保持在「正在重启」状态；Exit() 走正常关闭路径（取消终端、登出审计、停后台服务、刷日志）。
            exitService.Exit();
            return RestartFlowOutcome.Initiated;
        }

        var reference = $"RB-{_clock.GetUtcNow():yyyyMMdd}-{correlation.CorrelationId[..4].ToUpperInvariant()}";
        ConsoleLog.WriteError(
            LogCategory,
            $"shutdown-failed exitCode={result.ExitCode} reference={reference} detail={result.Message}",
            log);
        RecordAudit(
            session,
            "Failed",
            "COMMAND_FAILED",
            $"uptimeDays={FormatDays(uptime)};exitCode={result.ExitCode};ref={reference}{TestSuffix(isTest)}",
            correlation);
        await ui.ShowFailureAsync(
            new RestartFailureInfo(result.ExitCode, result.Message, reference, uptime),
            cancellationToken);
        ui.Hide();
        return RestartFlowOutcome.Failed;
    }

    private void RecordAudit(
        PosSessionState session,
        string outcome,
        string reasonCode,
        string safeMessage,
        (string CorrelationId, string TraceId) correlation)
    {
        try
        {
            OperationAuditEvents.RecordAction(
                auditLogger,
                OperationAuditTypes.SystemRestart,
                outcome,
                session,
                reasonCode: reasonCode,
                safeMessage: safeMessage,
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 审计写入失败不能阻断流程（本地 outbox 写失败也只记日志）。
            ConsoleLog.WriteWarning(LogCategory, $"audit record failed error={ex.GetType().Name} message={ex.Message}");
        }
    }

    private static void LogBlocked(string stage, RestartSafetyReport report, ApplicationLogContext log)
    {
        var issues = string.Join(',', report.Issues.Select(issue => $"{issue.Code}:{issue.Count}"));
        ConsoleLog.WriteInformation(LogCategory, $"{stage} issues={issues}", log);
    }

    private string Describe(TimeSpan uptime, RestartSafetyReport? report, bool isTest = false)
    {
        var message = $"uptimeDays={FormatDays(uptime)};thresholdDays={options.UptimeThresholdDays}";
        if (report is not null)
        {
            message += ";issues=" + string.Join('|', report.HardIssues.Select(issue => issue.Code));
        }

        return message + TestSuffix(isTest);
    }

    // 设置页测试触发的流程在审计里带 test=true，便于和日结后的正式提示区分。
    private static string TestSuffix(bool isTest) => isTest ? ";test=true" : string.Empty;

    private static string FormatDays(TimeSpan uptime) =>
        uptime.TotalDays.ToString("0.0", CultureInfo.InvariantCulture);

    private void HideQuietly()
    {
        try
        {
            ui.Hide();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteWarning(LogCategory, $"hide ui failed error={ex.GetType().Name}");
        }
    }
}
