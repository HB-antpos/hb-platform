namespace Hbpos.Client.Wpf.Services;

public enum RestartSafetyIssueCode
{
    CartNotEmpty,
    PaymentActive,
    CardTerminalBusy,
    CardRecoveryOpen,
    LinklySettlementUnresolved,
    DailyCloseBusy,
    AppUpdateActive,

    /// <summary>读取状态本身失败：无法证明安全，按失败关闭处理。</summary>
    CheckFailed,

    /// <summary>订单 / 日结 / Linkly 结算上传还有积压（软：持久化在 SQLite，重启不丢）。</summary>
    UploadPending
}

public enum RestartSafetySeverity
{
    /// <summary>必须先处理，否则不能重启。</summary>
    Hard,

    /// <summary>只提醒；重启前会尽力冲刷，超时仍放行并记录日志。</summary>
    Soft
}

public sealed record RestartSafetyIssue(
    RestartSafetyIssueCode Code,
    RestartSafetySeverity Severity,
    int Count = 0)
{
    /// <summary>写入操作审计 ReasonCode 的稳定编码（Web 日志按它检索，不要随意改名）。</summary>
    public string ReasonCode => Code switch
    {
        RestartSafetyIssueCode.CartNotEmpty => "BLOCKED_CART",
        RestartSafetyIssueCode.PaymentActive or RestartSafetyIssueCode.CardTerminalBusy => "BLOCKED_PAYMENT",
        RestartSafetyIssueCode.CardRecoveryOpen => "BLOCKED_CARD_RECOVERY",
        RestartSafetyIssueCode.LinklySettlementUnresolved => "BLOCKED_SETTLEMENT",
        RestartSafetyIssueCode.AppUpdateActive => "BLOCKED_UPDATE",
        RestartSafetyIssueCode.DailyCloseBusy => "BLOCKED_DAILY_CLOSE",
        RestartSafetyIssueCode.UploadPending => "UPLOAD_PENDING",
        _ => "BLOCKED_CHECK_FAILED"
    };
}

public sealed record RestartSafetyReport(IReadOnlyList<RestartSafetyIssue> Issues)
{
    public static RestartSafetyReport Safe { get; } = new([]);

    public IReadOnlyList<RestartSafetyIssue> HardIssues =>
        Issues.Where(issue => issue.Severity == RestartSafetySeverity.Hard).ToArray();

    public IReadOnlyList<RestartSafetyIssue> SoftIssues =>
        Issues.Where(issue => issue.Severity == RestartSafetySeverity.Soft).ToArray();

    /// <summary>没有任何硬性问题才算可以重启（软问题只提醒）。</summary>
    public bool IsSafe => Issues.All(issue => issue.Severity != RestartSafetySeverity.Hard);
}

/// <summary>各类上传积压条数；订单含 Pending/Failed/Syncing，日结含 Pending/Uploading，结算含 Pending/Failed/Uploading。</summary>
public sealed record RestartUploadBacklog(int Orders, int DailyCloses, int Settlements)
{
    public int Total => Orders + DailyCloses + Settlements;
}

public sealed record RestartUploadFlushResult(int Remaining, bool TimedOut, TimeSpan Elapsed);

/// <summary>
/// 重启前安全检查的状态来源。把「从哪里读」与「怎么判定」拆开：判定逻辑在 <see cref="RestartSafetyChecker"/>（可单测），
/// 读取实现在外壳层（依赖主界面状态，沿用 ShellAppUpdateInstallSafetyGuard 的做法）。
/// </summary>
public interface IRestartSafetyProbe
{
    int CartLineCount { get; }

    /// <summary>付款页有进行中的刷卡、被锁定，或已录入部分款项。</summary>
    bool IsPaymentActive { get; }

    bool IsDailyCloseBusy { get; }

    /// <summary>应用更新正在下载，或安装包已就绪等待安装。</summary>
    bool IsAppUpdateActive { get; }

    /// <summary>刷卡终端有金融操作在飞（支付 / 恢复 / 结算）。</summary>
    ValueTask<bool> IsCardTerminalBusyAsync(CancellationToken cancellationToken);

    /// <summary>异常中心未结卡交易数（含 Square 退款被拒待主管处理）。</summary>
    Task<int> CountOpenCardRecoveryAsync(CancellationToken cancellationToken);

    Task<bool> HasUnresolvedLinklySettlementAsync(CancellationToken cancellationToken);

    Task<RestartUploadBacklog> GetUploadBacklogAsync(CancellationToken cancellationToken);

    /// <summary>唤醒各类上传并尽力立即补传一轮；调用方负责限时。</summary>
    Task RequestUploadsAsync(CancellationToken cancellationToken);
}

public interface IRestartSafetyChecker
{
    Task<RestartSafetyReport> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>触发上传并最多等待 <paramref name="maxWait"/>，返回剩余积压；超时不是错误。</summary>
    Task<RestartUploadFlushResult> FlushUploadsAsync(TimeSpan maxWait, CancellationToken cancellationToken = default);
}

public sealed class RestartSafetyChecker(
    IRestartSafetyProbe probe,
    TimeProvider? timeProvider = null) : IRestartSafetyChecker
{
    private static readonly TimeSpan FlushPollInterval = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<RestartSafetyReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var issues = new List<RestartSafetyIssue>();

        // 同步状态：内存里的购物车、付款页、日结页、更新状态。
        try
        {
            if (probe.CartLineCount > 0)
            {
                issues.Add(Hard(RestartSafetyIssueCode.CartNotEmpty, probe.CartLineCount));
            }

            if (probe.IsPaymentActive)
            {
                issues.Add(Hard(RestartSafetyIssueCode.PaymentActive));
            }

            if (probe.IsDailyCloseBusy)
            {
                issues.Add(Hard(RestartSafetyIssueCode.DailyCloseBusy));
            }

            if (probe.IsAppUpdateActive)
            {
                issues.Add(Hard(RestartSafetyIssueCode.AppUpdateActive));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteError("RestartAfterClose", $"safety probe (sync) failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
            issues.Add(Hard(RestartSafetyIssueCode.CheckFailed));
        }

        // 异步状态：任何一项读取失败都按失败关闭（无法证明安全就不重启），而不是当作「没有问题」。
        await CheckAsyncStateAsync(
            async () =>
            {
                if (await probe.IsCardTerminalBusyAsync(cancellationToken))
                {
                    issues.Add(Hard(RestartSafetyIssueCode.CardTerminalBusy));
                }
            },
            issues,
            "card-terminal");
        await CheckAsyncStateAsync(
            async () =>
            {
                var open = await probe.CountOpenCardRecoveryAsync(cancellationToken);
                if (open > 0)
                {
                    issues.Add(Hard(RestartSafetyIssueCode.CardRecoveryOpen, open));
                }
            },
            issues,
            "card-recovery");
        await CheckAsyncStateAsync(
            async () =>
            {
                if (await probe.HasUnresolvedLinklySettlementAsync(cancellationToken))
                {
                    issues.Add(Hard(RestartSafetyIssueCode.LinklySettlementUnresolved));
                }
            },
            issues,
            "linkly-settlement");

        // 上传积压是软问题：队列都落在 SQLite，启动时自动恢复；读取失败也只记日志，不因此拦住重启。
        try
        {
            var backlog = await probe.GetUploadBacklogAsync(cancellationToken);
            if (backlog.Total > 0)
            {
                issues.Add(new RestartSafetyIssue(
                    RestartSafetyIssueCode.UploadPending,
                    RestartSafetySeverity.Soft,
                    backlog.Total));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteWarning("RestartAfterClose", $"upload backlog read failed error={ex.GetType().Name} message={ex.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new RestartSafetyReport(issues);
    }

    public async Task<RestartUploadFlushResult> FlushUploadsAsync(
        TimeSpan maxWait,
        CancellationToken cancellationToken = default)
    {
        var startedAt = _clock.GetTimestamp();
        var remaining = 0;
        var timedOut = false;

        try
        {
            // 先读一次：本来就是空的就不用唤醒、不用等。
            remaining = (await probe.GetUploadBacklogAsync(cancellationToken)).Total;
            if (remaining > 0 && maxWait > TimeSpan.Zero)
            {
                // 超时用带 TimeProvider 的 CTS，测试里才能用虚拟时间推进。
                using var timeout = new CancellationTokenSource(maxWait, _clock);
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

                // 唤醒上传放到后台任务里，等待循环自己限时；上传卡住不会拖过 maxWait。
                var requestTask = Task.Run(() => probe.RequestUploadsAsync(budget.Token), CancellationToken.None);
                _ = requestTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                while (remaining > 0)
                {
                    try
                    {
                        await Task.Delay(FlushPollInterval, _clock, budget.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        timedOut = true;
                        break;
                    }

                    remaining = (await probe.GetUploadBacklogAsync(CancellationToken.None)).Total;
                }
            }
            else if (remaining > 0)
            {
                timedOut = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 冲刷是尽力而为：读不到积压就按「还有未知积压」放行，由调用方记日志。
            ConsoleLog.WriteWarning("RestartAfterClose", $"upload flush failed error={ex.GetType().Name} message={ex.Message}");
            timedOut = true;
        }

        return new RestartUploadFlushResult(remaining, timedOut, _clock.GetElapsedTime(startedAt));
    }

    private static async Task CheckAsyncStateAsync(
        Func<Task> check,
        List<RestartSafetyIssue> issues,
        string name)
    {
        try
        {
            await check();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError("RestartAfterClose", $"safety probe failed name={name} error={ex.GetType().Name} message={ex.Message}", exception: ex);
            issues.Add(Hard(RestartSafetyIssueCode.CheckFailed));
        }
    }

    private static RestartSafetyIssue Hard(RestartSafetyIssueCode code, int count = 0) =>
        new(code, RestartSafetySeverity.Hard, count);
}
