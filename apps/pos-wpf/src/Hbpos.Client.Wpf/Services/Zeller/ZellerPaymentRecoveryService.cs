using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services.Zeller;

/// <summary>主管对一笔结果未知的 Zeller 付款或退款的决定。</summary>
public enum ZellerSupervisorDecision
{
    /// <summary>确认顾客已付款（退款：确认已退款）。</summary>
    ConfirmPaid,

    /// <summary>确认顾客没有付款（退款：确认没有退款）。</summary>
    ConfirmNotPaid,

    /// <summary>暂不结案，继续等待。记录保持结果未知。</summary>
    ContinueWaiting
}

public enum ZellerRecoveryStatus
{
    /// <summary>查到了结论：批准或拒绝。</summary>
    Resolved,

    /// <summary>仍然不能确定，留在恢复中心。</summary>
    StillUnknown,

    /// <summary>请求从未发出的记录，已按未付款关闭。</summary>
    Closed,

    /// <summary>这条记录正在被本进程的其他流程处理（例如付款页还在核实），这次没有动它。</summary>
    InProgress,

    /// <summary>记录不存在，或者已经有结论，不需要恢复。</summary>
    NotOpen
}

public sealed record ZellerRecoveryResult(
    ZellerRecoveryStatus Status,
    Guid AttemptGuid,
    LocalZellerPaymentAttempt? Attempt = null,
    ZellerPaymentOutcome? Outcome = null)
{
    /// <summary>批准时写进订单的引用，调用方用它和尝试里的订单草稿补单。</summary>
    public string? PaymentReference => Outcome?.PaymentReference;
}

/// <summary>一次自动恢复的结果。AwaitingOrderCompletion 里的尝试已批准但订单还没落盘，调用方要用订单草稿补单。</summary>
public sealed record ZellerAutoRecoverySummary(
    IReadOnlyList<ZellerRecoveryResult> Results,
    int LateRechecks,
    IReadOnlyList<LocalZellerPaymentAttempt> AwaitingOrderCompletion);

public sealed record ZellerSupervisorResolution(
    ZellerSupervisorDecision Decision,
    string OperatorId,
    string OperatorName,
    string? Note = null,
    string? FinancialReference = null);

public sealed record ZellerResolutionResult(
    bool Succeeded,
    string? Error = null,
    LocalZellerPaymentAttempt? Attempt = null,
    string? OrderPaymentReference = null,
    LocalFinancialSupervisorResolution? Journal = null)
{
    public static ZellerResolutionResult Fail(string error) => new(false, error);
}

/// <summary>
/// Zeller 的恢复：程序启动、收银员登录时自动按 reference 查未结记录；恢复中心里逐条查询；主管结案。
/// </summary>
/// <remarks>
/// 中文注释：规则来自「Zeller 直连接入方案」的 D3 和状态机一节——
/// 1. 恢复只查询，不重提。查到一笔批准就标记为批准，由调用方用订单草稿补单，补完再调 MarkOrderCompletedAsync。
/// 2. 进程在调用中途退出（记录停在「已发出」）后查到 0 笔，试点期间不自动收尾，转为结果未知交主管。
/// 3. 记录停在「待发出」说明进程在调用刷卡机之前就退出了，请求确定没发出，可以直接按未付款关闭。
/// 4. 主管结案以人工确认为准，不实时查询；只保留本机证据的保险：
///    本地已经存下这一笔的批准交易（迟到的批准、同一 reference 多笔批准）时，拒绝「确认未付款」。
/// 5. 和扣款客户端共用 ZellerAttemptActivity，正在处理的记录不碰。
/// 主管结案还没有写进现有的主管结案日志和审计上传，接入收款流程时再接。
/// </remarks>
public sealed class ZellerPaymentRecoveryService
{
    public const int MaxNoteLength = 500;
    public const int MaxFinancialReferenceLength = 100;

    private readonly IZellerTerminalGateway _gateway;
    private readonly ILocalZellerPaymentAttemptRepository _attempts;
    private readonly ZellerCardTerminalClient _client;
    private readonly ZellerAttemptActivity _activity;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;

    public ZellerPaymentRecoveryService(
        IZellerTerminalGateway gateway,
        ILocalZellerPaymentAttemptRepository attempts,
        ZellerCardTerminalClient client,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _activity = client.Activity;
        _time = timeProvider ?? TimeProvider.System;
        _log = log;
    }

    /// <summary>恢复中心要显示的记录：已发出但没有结果，或结果未知。</summary>
    public Task<IReadOnlyList<LocalZellerPaymentAttempt>> ListOpenAsync(
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken = default) =>
        _attempts.ListOpenAsync(storeCode, deviceCode, cancellationToken);

    /// <summary>已批准但订单还没落盘的记录。</summary>
    public Task<IReadOnlyList<LocalZellerPaymentAttempt>> ListAwaitingOrderCompletionAsync(
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken = default) =>
        _attempts.ListByStatusAsync(storeCode, deviceCode, [ZellerAttemptStatus.Approved], cancellationToken);

    /// <summary>程序启动或收银员登录时调用：补做迟到复核、关闭从未发出的记录、按 reference 查询未结记录。</summary>
    public async Task<ZellerAutoRecoverySummary> AutoRecoverAsync(
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken = default)
    {
        var lateRechecks = await _client.RunDueLateRechecksAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ZellerRecoveryResult>();

        foreach (var pending in await _attempts.ListByStatusAsync(storeCode, deviceCode, [ZellerAttemptStatus.Pending], cancellationToken).ConfigureAwait(false))
        {
            results.Add(await ClosePendingAsync(pending.AttemptGuid).ConfigureAwait(false));
        }

        foreach (var open in await _attempts.ListOpenAsync(storeCode, deviceCode, cancellationToken).ConfigureAwait(false))
        {
            results.Add(await RecoverAttemptAsync(open.AttemptGuid, cancellationToken).ConfigureAwait(false));
        }

        var awaiting = await ListAwaitingOrderCompletionAsync(storeCode, deviceCode, cancellationToken).ConfigureAwait(false);
        Log($"auto recovery: {results.Count} attempt(s) checked, {lateRechecks} late recheck(s), {awaiting.Count} awaiting order completion");
        return new ZellerAutoRecoverySummary(results, lateRechecks, awaiting);
    }

    /// <summary>按 reference 查询一条未结记录并据此更新。恢复中心和付款页的恢复提示都用它。</summary>
    public async Task<ZellerRecoveryResult> RecoverAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default)
    {
        using var lease = _activity.TryEnter(attemptGuid);
        if (lease is null)
        {
            return new ZellerRecoveryResult(ZellerRecoveryStatus.InProgress, attemptGuid);
        }

        var attempt = await _attempts.GetAsync(attemptGuid, cancellationToken).ConfigureAwait(false);
        if (attempt is not { Status: ZellerAttemptStatus.Submitted or ZellerAttemptStatus.Unknown })
        {
            return new ZellerRecoveryResult(ZellerRecoveryStatus.NotOpen, attemptGuid, attempt);
        }

        var expectation = Expectation(attempt);
        var query = await _gateway.GetTransactionsAsync(attempt.Reference, cancellationToken).ConfigureAwait(false);
        var verification = ZellerResultClassifier.ClassifyVerification(ZellerCallDecision.Unknown, query, expectation);
        var now = _time.GetUtcNow();

        switch (verification.Decision)
        {
            case ZellerVerificationDecision.Approved:
                var approved = await SaveAsync(attempt, attempt.WithTransaction(verification.Transaction!) with
                {
                    Status = ZellerAttemptStatus.Approved,
                    FailureKind = ZellerFailureKind.None,
                    UnknownReason = ZellerUnknownReason.None,
                    VerificationRounds = attempt.VerificationRounds + 1,
                    CompletedAt = now
                }).ConfigureAwait(false);
                Log($"attempt {attemptGuid}: recovered as approved, transaction {verification.Transaction!.TransactionUuid}");
                return Resolved(approved, ZellerPaymentState.Approved, verification.Transaction);

            case ZellerVerificationDecision.Declined:
                var declined = await SaveAsync(attempt, (verification.Transaction is null ? attempt : attempt.WithTransaction(verification.Transaction)) with
                {
                    Status = ZellerAttemptStatus.Declined,
                    FailureKind = ZellerFailureKind.BankDeclined,
                    UnknownReason = ZellerUnknownReason.None,
                    VerificationRounds = attempt.VerificationRounds + 1,
                    CompletedAt = now
                }).ConfigureAwait(false);
                Log($"attempt {attemptGuid}: recovered as declined");
                return Resolved(declined, ZellerPaymentState.Declined, verification.Transaction);
        }

        // 中文注释：查到 0 笔或查询失败都不收尾。查询找到需要主管处理的情况（多笔批准、金额不符）时记下原因和交易。
        var reason = verification.UnknownReason is ZellerUnknownReason.None or ZellerUnknownReason.VerificationFailed
            ? attempt.Status == ZellerAttemptStatus.Submitted ? ZellerUnknownReason.ProcessExited : attempt.UnknownReason
            : verification.UnknownReason;
        var mustWrite = attempt.Status == ZellerAttemptStatus.Submitted ||
            reason != attempt.UnknownReason ||
            verification.Transaction is not null;
        var unknown = mustWrite
            ? await SaveAsync(attempt, (verification.Transaction is null ? attempt : attempt.WithTransaction(verification.Transaction)) with
            {
                Status = ZellerAttemptStatus.Unknown,
                FailureKind = ZellerFailureKind.ResultUnknown,
                UnknownReason = reason,
                VerificationRounds = attempt.VerificationRounds + 1
            }).ConfigureAwait(false)
            : attempt;
        return new ZellerRecoveryResult(
            ZellerRecoveryStatus.StillUnknown,
            attemptGuid,
            unknown,
            Outcome(unknown, ZellerPaymentState.Unknown, verification.Transaction));
    }

    /// <summary>主管结案。以人工确认为准，不实时查询（恢复中心打开结案弹窗前应先调 RecoverAttemptAsync 查一次）。</summary>
    /// <param name="journalFactory">
    /// 给出时，用结案后的记录生成一条主管结案日志，和记录的更新在同一个事务里写入（LocalFinancialSupervisorResolutions），
    /// 写入的日志随结果返回，调用方提交后再触发审计上传。
    /// </param>
    public async Task<ZellerResolutionResult> ResolveAsync(
        Guid attemptGuid,
        ZellerSupervisorResolution resolution,
        Func<LocalZellerPaymentAttempt, LocalFinancialSupervisorResolution>? journalFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (string.IsNullOrWhiteSpace(resolution.OperatorId) || string.IsNullOrWhiteSpace(resolution.OperatorName))
        {
            return ZellerResolutionResult.Fail("A supervisor must be identified to resolve a Zeller payment.");
        }

        var note = string.IsNullOrWhiteSpace(resolution.Note) ? null : resolution.Note.Trim();
        var financialReference = string.IsNullOrWhiteSpace(resolution.FinancialReference) ? null : resolution.FinancialReference.Trim();
        if (note?.Length > MaxNoteLength)
        {
            return ZellerResolutionResult.Fail($"The supervisor note must be at most {MaxNoteLength} characters.");
        }

        if (financialReference?.Length > MaxFinancialReferenceLength)
        {
            return ZellerResolutionResult.Fail($"The payment reference must be at most {MaxFinancialReferenceLength} characters.");
        }

        using var lease = _activity.TryEnter(attemptGuid);
        if (lease is null)
        {
            return ZellerResolutionResult.Fail("This payment is still being processed on this POS. Try again in a moment.");
        }

        var attempt = await _attempts.GetAsync(attemptGuid, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            return ZellerResolutionResult.Fail("The Zeller payment was not found.");
        }

        if (attempt.Status is not (ZellerAttemptStatus.Submitted or ZellerAttemptStatus.Unknown))
        {
            return ZellerResolutionResult.Fail("This Zeller payment already has an outcome.");
        }

        var now = _time.GetUtcNow();
        var stamped = attempt with
        {
            SupervisorDecision = resolution.Decision,
            SupervisorNote = note,
            SupervisorFinancialReference = financialReference,
            SupervisorOperatorId = resolution.OperatorId.Trim(),
            SupervisorOperatorName = resolution.OperatorName.Trim(),
            SupervisorDecidedAt = now
        };

        LocalFinancialSupervisorResolution? journal = null;
        async Task<LocalZellerPaymentAttempt> PersistAsync(LocalZellerPaymentAttempt next)
        {
            if (journalFactory is null)
            {
                return await SaveAsync(attempt, next).ConfigureAwait(false);
            }

            var dated = next with { UpdatedAt = _time.GetUtcNow() };
            journal = journalFactory(dated);
            return await _attempts.TryUpdateWithJournalAsync(attempt, dated, journal, CancellationToken.None).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Zeller attempt {attempt.AttemptGuid} was changed by another writer.");
        }

        switch (resolution.Decision)
        {
            case ZellerSupervisorDecision.ContinueWaiting:
                var waiting = await PersistAsync(stamped with
                {
                    Status = ZellerAttemptStatus.Unknown,
                    FailureKind = ZellerFailureKind.ResultUnknown,
                    UnknownReason = attempt.Status == ZellerAttemptStatus.Submitted ? ZellerUnknownReason.ProcessExited : attempt.UnknownReason
                }).ConfigureAwait(false);
                Log($"attempt {attemptGuid}: supervisor {waiting.SupervisorOperatorId} chose to keep waiting");
                return new ZellerResolutionResult(true, Attempt: waiting, Journal: journal);

            case ZellerSupervisorDecision.ConfirmNotPaid:
                if (HoldsLocalApprovalEvidence(attempt))
                {
                    return ZellerResolutionResult.Fail(
                        "Zeller has already reported this payment as approved, so it cannot be confirmed as not paid. Confirm it as paid, or refund it.");
                }

                var notPaid = await PersistAsync(stamped with
                {
                    Status = ZellerAttemptStatus.Canceled,
                    FailureKind = ZellerFailureKind.None,
                    UnknownReason = ZellerUnknownReason.None,
                    CompletedAt = now
                }).ConfigureAwait(false);
                Log($"attempt {attemptGuid}: supervisor {notPaid.SupervisorOperatorId} confirmed not paid");
                return new ZellerResolutionResult(true, Attempt: notPaid, Journal: journal);

            default:
                var paid = await PersistAsync(stamped with
                {
                    Status = ZellerAttemptStatus.Approved,
                    FailureKind = ZellerFailureKind.None,
                    UnknownReason = ZellerUnknownReason.None,
                    CompletedAt = now
                }).ConfigureAwait(false);
                var orderReference = OrderReferenceFor(paid);
                Log($"attempt {attemptGuid}: supervisor {paid.SupervisorOperatorId} confirmed paid, order reference {orderReference}");
                return new ZellerResolutionResult(true, Attempt: paid, OrderPaymentReference: orderReference, Journal: journal);
        }
    }

    // ───────────── 辅助 ─────────────

    private async Task<ZellerRecoveryResult> ClosePendingAsync(Guid attemptGuid)
    {
        using var lease = _activity.TryEnter(attemptGuid);
        if (lease is null)
        {
            return new ZellerRecoveryResult(ZellerRecoveryStatus.InProgress, attemptGuid);
        }

        var attempt = await _attempts.GetAsync(attemptGuid, CancellationToken.None).ConfigureAwait(false);
        if (attempt?.Status != ZellerAttemptStatus.Pending)
        {
            return new ZellerRecoveryResult(ZellerRecoveryStatus.NotOpen, attemptGuid, attempt);
        }

        // 中文注释：客户端先把记录改成「已发出」才调用刷卡机，停在「待发出」说明请求确定没发出。
        var closed = await SaveAsync(attempt, attempt with
        {
            Status = ZellerAttemptStatus.Canceled,
            FailureKind = ZellerFailureKind.Cancelled,
            UnknownReason = ZellerUnknownReason.None,
            CompletedAt = _time.GetUtcNow()
        }).ConfigureAwait(false);
        Log($"attempt {attemptGuid}: never submitted; closed as not paid");
        return new ZellerRecoveryResult(ZellerRecoveryStatus.Closed, attemptGuid, closed, Outcome(closed, ZellerPaymentState.NotPaid));
    }

    /// <summary>本地已经存下这一笔的批准交易：迟到的批准，或同一 reference 下多笔批准。</summary>
    private static bool HoldsLocalApprovalEvidence(LocalZellerPaymentAttempt attempt) =>
        !string.IsNullOrWhiteSpace(attempt.TransactionUuid) &&
        attempt.UnknownReason is ZellerUnknownReason.LateApproval or ZellerUnknownReason.MultipleApproved;

    /// <summary>主管确认已付款后写进订单的引用。查得到 transactionUuid 就带上；查不到用未核实的格式，不能原路退款。</summary>
    internal static string OrderReferenceFor(LocalZellerPaymentAttempt attempt)
    {
        var hasUuid = Guid.TryParse(attempt.TransactionUuid, out var uuid) && uuid != Guid.Empty;
        return attempt.OperationKind == ZellerAttemptOperation.Refund
            ? hasUuid ? ZellerPaymentReference.FormatRefund(attempt.Reference, uuid.ToString("D")) : ZellerPaymentReference.FormatUnverifiedRefund(attempt.Reference)
            : hasUuid ? ZellerPaymentReference.FormatPayment(attempt.Reference, uuid.ToString("D")) : ZellerPaymentReference.FormatUnverifiedPayment(attempt.Reference);
    }

    private static ZellerAttemptExpectation Expectation(LocalZellerPaymentAttempt attempt) => new(
        attempt.OperationKind == ZellerAttemptOperation.Refund ? ZellerTransactionType.Refund : ZellerTransactionType.Purchase,
        attempt.AmountCents,
        attempt.Reference);

    private async Task<LocalZellerPaymentAttempt> SaveAsync(LocalZellerPaymentAttempt current, LocalZellerPaymentAttempt next) =>
        await _attempts.TryUpdateAsync(current, next with { UpdatedAt = _time.GetUtcNow() }, CancellationToken.None).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Zeller attempt {current.AttemptGuid} was changed by another writer.");

    private static ZellerRecoveryResult Resolved(LocalZellerPaymentAttempt attempt, ZellerPaymentState state, ZellerTransaction? transaction) =>
        new(ZellerRecoveryStatus.Resolved, attempt.AttemptGuid, attempt, Outcome(attempt, state, transaction));

    private static ZellerPaymentOutcome Outcome(LocalZellerPaymentAttempt attempt, ZellerPaymentState state, ZellerTransaction? transaction = null) =>
        new(state, attempt.AttemptGuid, attempt.Reference, attempt.AmountCents, attempt.FailureKind, attempt.UnknownReason, transaction, attempt.OperationKind);

    private void Log(string message) => _log?.Invoke(message);
}
