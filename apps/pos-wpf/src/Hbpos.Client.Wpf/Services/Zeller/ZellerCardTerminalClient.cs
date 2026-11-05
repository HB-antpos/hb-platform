using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services.Zeller;

/// <summary>扣款客户端的时间设置。默认值来自「Zeller 直连接入方案」的 D3、D4。</summary>
public sealed record ZellerClientOptions
{
    /// <summary>结果未知时自动重查的间隔。</summary>
    public TimeSpan VerificationInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>结果未知时自动重查多久，之后交给恢复中心。</summary>
    public TimeSpan VerificationWindow { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>按未付款收尾之后，过多久再复核一次，防止迟到的批准被漏掉。</summary>
    public TimeSpan LateRecheckDelay { get; init; } = TimeSpan.FromSeconds(60);

    internal void Validate()
    {
        Require(VerificationInterval, nameof(VerificationInterval));
        Require(VerificationWindow, nameof(VerificationWindow));
        Require(LateRecheckDelay, nameof(LateRecheckDelay));
    }

    private static void Require(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, "The duration must be positive.");
        }
    }
}

/// <summary>发起一次扣款所需的信息。订单草稿由调用方序列化，扣款批准后用它建单、崩溃后用它补单。</summary>
public sealed record ZellerPurchaseContext(
    int AmountCents,
    string OrderDraftJson,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    Guid? OperationGuid = null);

/// <summary>一次扣款的最终结论。</summary>
public enum ZellerPaymentState
{
    Approved,
    Declined,

    /// <summary>未付款：取消、超时、刷卡机没连上等，已经确认没有产生交易。</summary>
    NotPaid,

    /// <summary>结果未知：已落库，交给恢复中心。绝不能让顾客重新刷卡。</summary>
    Unknown
}

public enum ZellerPaymentProgressKind
{
    /// <summary>请求已经交给刷卡机，等待顾客刷卡。</summary>
    Submitted,

    /// <summary>刷卡机正在提示刷卡。</summary>
    AwaitingCard,

    /// <summary>刷卡机已读到卡，正在处理。</summary>
    Processing,

    /// <summary>结果不明，正在向 Zeller 核实。</summary>
    Verifying
}

/// <summary>扣款、退款过程中的进度。AttemptGuid 只在 Submitted 时带上：调用方据此知道请求已经发出、对应哪一条尝试。</summary>
public sealed record ZellerPaymentProgress(ZellerPaymentProgressKind Kind, int VerificationRound = 0, Guid AttemptGuid = default);

/// <summary>
/// 按原交易退款所需的信息。OriginalPaymentReference 是原扣款写在订单里的引用（ZELLER:…），
/// 也可以是包装过的退款引用；退款用其中的 transactionUuid 指向原交易，不用 reference（一个 reference 可能对应多笔）。
/// RefundBusinessKey 标识「这一笔退货要退回原卡」这件事，同一个键同时只允许一笔进行中或结果未知的退款。
/// Unmatched 为 true 表示无关联退款：原付款无法关联到 Zeller 交易（主管确认的未核实付款、人工刷卡），
/// 不带原交易号，由顾客在刷卡机上刷一张卡，钱退到刷的那张卡；OriginalPaymentReference 仍是原付款的引用，只用于订单记录。
/// </summary>
public sealed record ZellerRefundContext(
    int AmountCents,
    string OriginalPaymentReference,
    string OrderDraftJson,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string? RefundBusinessKey = null,
    Guid? OperationGuid = null,
    bool Unmatched = false);

public sealed record ZellerPaymentOutcome(
    ZellerPaymentState State,
    Guid AttemptGuid,
    string Reference,
    int AmountCents,
    ZellerFailureKind FailureKind = ZellerFailureKind.None,
    ZellerUnknownReason UnknownReason = ZellerUnknownReason.None,
    ZellerTransaction? Transaction = null,
    ZellerAttemptOperation Operation = ZellerAttemptOperation.Sale)
{
    /// <summary>
    /// 写进订单付款记录的引用，只有批准时才有。扣款是 ZELLER:{reference}:{transactionUuid}；
    /// 退款是 ZELLERRF:{refundReference}:{refundTransactionUuid}，由收款流程再和原交易引用包在一起。
    /// </summary>
    public string? PaymentReference =>
        State == ZellerPaymentState.Approved && !string.IsNullOrWhiteSpace(Transaction?.TransactionUuid)
            ? Operation == ZellerAttemptOperation.Refund
                ? ZellerPaymentReference.FormatRefund(Reference, Transaction.TransactionUuid)
                : ZellerPaymentReference.FormatPayment(Reference, Transaction.TransactionUuid)
            : null;
}

/// <summary>
/// Zeller 扣款的完整流程：先落库，再调用刷卡机，然后按结果分类、按 reference 核实、必要时自动重查。
/// </summary>
/// <remarks>
/// 中文注释：原则（来自 2026-11-05 的真机验证）——
/// 1. 每次扣款用新的 attempt 和新的 reference，并在调用 SDK 之前落库。Zeller 不对 reference 去重。
/// 2. 只有 Status=Approved 且金额、reference 对得上才算批准；判定只看 Status 和错误类型。
/// 3. 取消、超时、刷卡机没连上：立即查一次，查到 0 笔才算未付款，并在 60 秒后再复核一次。
/// 4. 其余（断网报错、看门狗放弃、SDK 异常）结果未知：每 5 秒查一次，最多 3 分钟，之后交恢复中心。
///    绝不重新发起扣款。
/// 5. 落库不受调用方取消的影响：收银员取消只会让等待提前结束，记录照样写对。
/// </remarks>
public sealed class ZellerCardTerminalClient
{
    private readonly IZellerTerminalGateway _gateway;
    private readonly ILocalZellerPaymentAttemptRepository _attempts;
    private readonly ZellerClientOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;

    public ZellerCardTerminalClient(
        IZellerTerminalGateway gateway,
        ILocalZellerPaymentAttemptRepository attempts,
        ZellerClientOptions? options = null,
        TimeProvider? timeProvider = null,
        Action<string>? log = null,
        ZellerAttemptActivity? activity = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
        _options = options ?? new ZellerClientOptions();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
        _log = log;
        Activity = activity ?? new ZellerAttemptActivity();
    }

    /// <summary>本进程里正在处理的尝试。恢复服务必须和客户端共用同一个实例。</summary>
    public ZellerAttemptActivity Activity { get; }

    public async Task<ZellerPaymentOutcome> PurchaseAsync(
        ZellerPurchaseContext context,
        IProgress<ZellerPaymentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.AmountCents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(context), context.AmountCents, "The amount must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(context.OrderDraftJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.StoreCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.DeviceCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.CashierId);

        var attemptGuid = Guid.NewGuid();
        var reference = ZellerPaymentReference.CreatePurchaseReference(attemptGuid);
        var now = _time.GetUtcNow();
        var pending = new LocalZellerPaymentAttempt(
            attemptGuid,
            ZellerAttemptOperation.Sale,
            reference,
            context.AmountCents,
            ZellerAttemptStatus.Pending,
            context.OrderDraftJson,
            context.StoreCode,
            context.DeviceCode,
            context.CashierId,
            now,
            now)
        {
            OperationGuid = context.OperationGuid
        };

        // 中文注释：先登记再落库，恢复服务看到这条记录时就知道它正在处理，不会去动它。
        using var lease = Activity.TryEnter(attemptGuid)
            ?? throw new InvalidOperationException($"Zeller attempt {attemptGuid} is already active.");

        // 中文注释：先落库再扣款。这一步失败就直接抛出，刷卡机没有收到任何请求。
        await _attempts.CreateAsync(pending, CancellationToken.None).ConfigureAwait(false);
        Log($"attempt {attemptGuid} created, reference {reference}, amount {context.AmountCents} cents");

        return await RunAsync(
            pending,
            (events, token) => _gateway.PurchaseAsync(new ZellerPurchaseRequest(context.AmountCents, reference), events, token),
            ZellerTransactionType.Purchase,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按原交易退款。和扣款一样先落库；结果未知时只按退款的 reference 查询，绝不重发退款。
    /// 同一个退款业务键已有进行中或结果未知的退款时，不发新的退款，直接返回那一笔（结果未知）。
    /// </summary>
    public async Task<ZellerPaymentOutcome> RefundAsync(
        ZellerRefundContext context,
        IProgress<ZellerPaymentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.AmountCents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(context), context.AmountCents, "The amount must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(context.OrderDraftJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.StoreCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.DeviceCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.CashierId);
        string? originalTransactionUuid = null;
        if (!context.Unmatched)
        {
            if (!ZellerPaymentReference.TryGetOriginalTransactionUuid(context.OriginalPaymentReference, out var parsedOriginalTransactionUuid))
            {
                // 调用方只应该把 Zeller 的原交易路由到这里（方案 D7）；无法关联的原付款要显式走无关联退款。
                throw new ArgumentException("The original payment is not a Zeller payment reference.", nameof(context));
            }

            originalTransactionUuid = parsedOriginalTransactionUuid;
        }

        var attemptGuid = Guid.NewGuid();
        var reference = ZellerPaymentReference.CreateRefundReference(attemptGuid);
        var now = _time.GetUtcNow();
        var businessKey = string.IsNullOrWhiteSpace(context.RefundBusinessKey) ? null : context.RefundBusinessKey.Trim();
        var pending = new LocalZellerPaymentAttempt(
            attemptGuid,
            ZellerAttemptOperation.Refund,
            reference,
            context.AmountCents,
            ZellerAttemptStatus.Pending,
            context.OrderDraftJson,
            context.StoreCode,
            context.DeviceCode,
            context.CashierId,
            now,
            now)
        {
            OperationGuid = context.OperationGuid,
            RefundBusinessKey = businessKey,
            OriginalTransactionUuid = originalTransactionUuid
        };

        using var lease = Activity.TryEnter(attemptGuid)
            ?? throw new InvalidOperationException($"Zeller attempt {attemptGuid} is already active.");
        if (businessKey is null)
        {
            await _attempts.CreateAsync(pending, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            var stored = await _attempts.CreateOrGetOpenRefundAsync(pending, CancellationToken.None).ConfigureAwait(false);
            if (stored.AttemptGuid != attemptGuid)
            {
                Log($"refund for business key {businessKey} is already in progress as attempt {stored.AttemptGuid}; not sending another");
                return Outcome(stored, ZellerPaymentState.Unknown) with { UnknownReason = ZellerUnknownReason.RefundAlreadyInProgress };
            }
        }

        Log(originalTransactionUuid is null
            ? $"refund attempt {attemptGuid} created, reference {reference}, amount {context.AmountCents} cents, UNMATCHED (card present, no original transaction)"
            : $"refund attempt {attemptGuid} created, reference {reference}, amount {context.AmountCents} cents, original {originalTransactionUuid}");
        return await RunAsync(
            pending,
            (events, token) => _gateway.RefundAsync(new ZellerRefundRequest(context.AmountCents, reference, originalTransactionUuid), events, token),
            ZellerTransactionType.Refund,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>扣款和退款共用的后半段：标记已发出、调用刷卡机、分类、核实。</summary>
    private async Task<ZellerPaymentOutcome> RunAsync(
        LocalZellerPaymentAttempt pending,
        Func<IProgress<ZellerTerminalEvent>, CancellationToken, Task<ZellerCallResult>> call,
        ZellerTransactionType type,
        IProgress<ZellerPaymentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var attemptGuid = pending.AttemptGuid;
        if (cancellationToken.IsCancellationRequested)
        {
            var canceled = await SaveAsync(pending, pending with { Status = ZellerAttemptStatus.Canceled, FailureKind = ZellerFailureKind.Cancelled })
                .ConfigureAwait(false);
            return Outcome(canceled, ZellerPaymentState.NotPaid);
        }

        var submitted = await SaveAsync(pending, pending with { Status = ZellerAttemptStatus.Submitted }).ConfigureAwait(false);
        progress?.Report(new ZellerPaymentProgress(ZellerPaymentProgressKind.Submitted, AttemptGuid: attemptGuid));

        ZellerCallResult result;
        try
        {
            result = await call(new EventForwarder(progress), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 网关不应该抛出；万一抛了，请求可能已经发出，只能按结果未知处理。
            Log($"attempt {attemptGuid}: the gateway threw {ex.GetType().Name}: {ex.Message}");
            result = ZellerCallResult.Faulted($"{ex.GetType().Name}: {ex.Message}");
        }

        var expectation = new ZellerAttemptExpectation(type, pending.AmountCents, pending.Reference);
        var classification = ZellerResultClassifier.ClassifyCall(result, expectation);
        var afterCall = submitted with
        {
            ReachedTerminal = result.ReachedTerminal || result.ProcessingStarted,
            ProcessingStarted = result.ProcessingStarted
        };
        Log($"attempt {attemptGuid}: call ended as {result.Outcome} {result.ErrorType}, decision {classification.Decision}");

        return classification.Decision switch
        {
            ZellerCallDecision.Approved => await FinishApprovedAsync(submitted, afterCall, classification.Transaction!).ConfigureAwait(false),
            ZellerCallDecision.Declined => await FinishDeclinedAsync(submitted, afterCall, classification.Transaction, classification.FailureKind).ConfigureAwait(false),
            ZellerCallDecision.NotSubmitted => await FinishNotPaidAsync(submitted, afterCall, classification.FailureKind, scheduleLateRecheck: false).ConfigureAwait(false),
            ZellerCallDecision.VerifyThenNotPaid or ZellerCallDecision.VerifyThenDeclined =>
                await VerifyOnceAsync(submitted, afterCall, classification, expectation, progress, cancellationToken).ConfigureAwait(false),
            _ => await VerifyUntilKnownAsync(
                submitted,
                afterCall,
                classification.UnknownReason,
                classification.RebuildTerminalFirst,
                classification.Transaction,
                expectation,
                progress,
                cancellationToken).ConfigureAwait(false)
        };
    }

    /// <summary>订单落盘之后调用：把批准的尝试标记为已建单。不是批准状态时返回 false。</summary>
    public async Task<bool> MarkOrderCompletedAsync(Guid attemptGuid, CancellationToken cancellationToken = default)
    {
        var attempt = await _attempts.GetAsync(attemptGuid, cancellationToken).ConfigureAwait(false);
        if (attempt?.Status != ZellerAttemptStatus.Approved)
        {
            return false;
        }

        var next = attempt with { Status = ZellerAttemptStatus.OrderCompleted, UpdatedAt = _time.GetUtcNow() };
        return await _attempts.TryUpdateAsync(attempt, next, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// 对一条按未付款收尾的记录做迟到复核。查到批准时把记录转回结果未知（顾客其实已被扣款），交恢复中心处理。
    /// 不需要复核、或已经复核过的记录返回 null。
    /// </summary>
    public async Task<ZellerPaymentOutcome?> RecheckAsync(Guid attemptGuid, CancellationToken cancellationToken = default)
    {
        // 别人（例如恢复服务或主管结案）正在处理这条记录时不复核，留给下次。
        using var lease = Activity.TryEnter(attemptGuid);
        if (lease is null)
        {
            return null;
        }

        var attempt = await _attempts.GetAsync(attemptGuid, cancellationToken).ConfigureAwait(false);
        if (attempt is not { Status: ZellerAttemptStatus.Canceled, LateRecheckDueAt: not null, LateRecheckedAt: null })
        {
            return null;
        }

        var expectation = new ZellerAttemptExpectation(
            attempt.OperationKind == ZellerAttemptOperation.Refund ? ZellerTransactionType.Refund : ZellerTransactionType.Purchase,
            attempt.AmountCents,
            attempt.Reference);
        var query = await _gateway.GetTransactionsAsync(attempt.Reference, cancellationToken).ConfigureAwait(false);
        var recheck = ZellerResultClassifier.ClassifyLateRecheck(query, expectation);
        var now = _time.GetUtcNow();

        if (recheck.Decision == ZellerVerificationDecision.KeepUnknown)
        {
            Log($"attempt {attemptGuid}: late recheck found {recheck.UnknownReason}; reopening as unknown");
            var reopened = await SaveAsync(attempt, (recheck.Transaction is { } found ? attempt.WithTransaction(found) : attempt) with
            {
                Status = ZellerAttemptStatus.Unknown,
                FailureKind = ZellerFailureKind.ResultUnknown,
                UnknownReason = recheck.UnknownReason,
                LateRecheckedAt = now
            }).ConfigureAwait(false);
            return Outcome(reopened, ZellerPaymentState.Unknown, recheck.Transaction);
        }

        // 查询失败：不记复核时间，留给下次（例如程序重启后）再查。
        if (recheck.UnknownReason == ZellerUnknownReason.VerificationFailed)
        {
            Log($"attempt {attemptGuid}: late recheck could not query Zeller; will retry later");
            return Outcome(attempt, ZellerPaymentState.NotPaid);
        }

        var stamped = await SaveAsync(attempt, attempt with { LateRecheckedAt = now }).ConfigureAwait(false);
        return Outcome(stamped, ZellerPaymentState.NotPaid);
    }

    /// <summary>补做所有已经到期的迟到复核，例如程序重启之后。返回复核了几条。</summary>
    public async Task<int> RunDueLateRechecksAsync(CancellationToken cancellationToken = default)
    {
        var due = await _attempts.ListLateRechecksDueAsync(_time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        var count = 0;
        foreach (var attempt in due)
        {
            if (await RecheckAsync(attempt.AttemptGuid, cancellationToken).ConfigureAwait(false) is not null)
            {
                count++;
            }
        }

        return count;
    }

    // ───────────── 各种结局 ─────────────

    private async Task<ZellerPaymentOutcome> FinishApprovedAsync(
        LocalZellerPaymentAttempt snapshot,
        LocalZellerPaymentAttempt working,
        ZellerTransaction transaction)
    {
        var approved = await SaveAsync(snapshot, working.WithTransaction(transaction) with
        {
            Status = ZellerAttemptStatus.Approved,
            FailureKind = ZellerFailureKind.None,
            UnknownReason = ZellerUnknownReason.None,
            CompletedAt = _time.GetUtcNow()
        }).ConfigureAwait(false);
        Log($"attempt {approved.AttemptGuid}: approved, transaction {transaction.TransactionUuid}");
        return Outcome(approved, ZellerPaymentState.Approved, transaction);
    }

    private async Task<ZellerPaymentOutcome> FinishDeclinedAsync(
        LocalZellerPaymentAttempt snapshot,
        LocalZellerPaymentAttempt working,
        ZellerTransaction? transaction,
        ZellerFailureKind failureKind)
    {
        var kind = failureKind is ZellerFailureKind.BankDeclined or ZellerFailureKind.CardProblem ? failureKind : ZellerFailureKind.BankDeclined;
        var declined = await SaveAsync(snapshot, (transaction is null ? working : working.WithTransaction(transaction)) with
        {
            Status = ZellerAttemptStatus.Declined,
            FailureKind = kind,
            UnknownReason = ZellerUnknownReason.None,
            CompletedAt = _time.GetUtcNow()
        }).ConfigureAwait(false);
        return Outcome(declined, ZellerPaymentState.Declined, transaction);
    }

    private async Task<ZellerPaymentOutcome> FinishNotPaidAsync(
        LocalZellerPaymentAttempt snapshot,
        LocalZellerPaymentAttempt working,
        ZellerFailureKind failureKind,
        bool scheduleLateRecheck)
    {
        var now = _time.GetUtcNow();
        var canceled = await SaveAsync(snapshot, working with
        {
            Status = ZellerAttemptStatus.Canceled,
            FailureKind = failureKind,
            UnknownReason = ZellerUnknownReason.None,
            CompletedAt = now,
            LateRecheckDueAt = scheduleLateRecheck ? now + _options.LateRecheckDelay : null
        }).ConfigureAwait(false);

        if (canceled.LateRecheckDueAt is { } dueAt)
        {
            ScheduleLateRecheck(canceled.AttemptGuid, dueAt);
        }

        return Outcome(canceled, ZellerPaymentState.NotPaid);
    }

    /// <summary>取消、超时、刷卡机没连上、卡片类错误：立即按 reference 查一次。</summary>
    private async Task<ZellerPaymentOutcome> VerifyOnceAsync(
        LocalZellerPaymentAttempt snapshot,
        LocalZellerPaymentAttempt working,
        ZellerCallClassification classification,
        ZellerAttemptExpectation expectation,
        IProgress<ZellerPaymentProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (classification.RebuildTerminalFirst)
        {
            await _gateway.ResetAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // 中文注释：这一次查询不受收银员取消的影响，它决定的是钱有没有扣。网关自带查询超时。
        progress?.Report(new ZellerPaymentProgress(ZellerPaymentProgressKind.Verifying, 1));
        var query = await _gateway.GetTransactionsAsync(expectation.Reference, CancellationToken.None).ConfigureAwait(false);
        var verification = ZellerResultClassifier.ClassifyVerification(classification.Decision, query, expectation);
        working = working with { VerificationRounds = working.VerificationRounds + 1 };

        return verification.Decision switch
        {
            ZellerVerificationDecision.Approved => await FinishApprovedAsync(snapshot, working, verification.Transaction!).ConfigureAwait(false),
            ZellerVerificationDecision.Declined => await FinishDeclinedAsync(snapshot, working, verification.Transaction, classification.FailureKind).ConfigureAwait(false),
            ZellerVerificationDecision.NotPaid => await FinishNotPaidAsync(snapshot, working, classification.FailureKind, scheduleLateRecheck: true).ConfigureAwait(false),
            _ => await VerifyUntilKnownAsync(
                snapshot,
                working,
                verification.UnknownReason == ZellerUnknownReason.None ? ZellerUnknownReason.VerificationFailed : verification.UnknownReason,
                rebuildTerminalFirst: false,
                verification.Transaction,
                expectation,
                progress,
                cancellationToken).ConfigureAwait(false)
        };
    }

    /// <summary>结果未知：先落库为未知，再每隔一段时间按 reference 查询，直到有结论或时间用完。</summary>
    private async Task<ZellerPaymentOutcome> VerifyUntilKnownAsync(
        LocalZellerPaymentAttempt snapshot,
        LocalZellerPaymentAttempt working,
        ZellerUnknownReason reason,
        bool rebuildTerminalFirst,
        ZellerTransaction? evidence,
        ZellerAttemptExpectation expectation,
        IProgress<ZellerPaymentProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 先落库：轮询期间程序崩溃，启动时的自动恢复能接着处理。
        var unknown = await SaveAsync(snapshot, (evidence is null ? working : working.WithTransaction(evidence)) with
        {
            Status = ZellerAttemptStatus.Unknown,
            FailureKind = ZellerFailureKind.ResultUnknown,
            UnknownReason = reason
        }).ConfigureAwait(false);

        if (rebuildTerminalFirst)
        {
            await _gateway.ResetAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // 多笔批准、金额不符这类情况重查也不会变，直接交主管。
        if (RequiresSupervisor(reason))
        {
            Log($"attempt {unknown.AttemptGuid}: {reason} needs a supervisor");
            return Outcome(unknown, ZellerPaymentState.Unknown, evidence);
        }

        var deadline = _time.GetUtcNow() + _options.VerificationWindow;
        var rounds = 0;
        try
        {
            while (true)
            {
                rounds++;
                progress?.Report(new ZellerPaymentProgress(ZellerPaymentProgressKind.Verifying, rounds));
                var query = await _gateway.GetTransactionsAsync(expectation.Reference, cancellationToken).ConfigureAwait(false);
                var verification = ZellerResultClassifier.ClassifyVerification(ZellerCallDecision.Unknown, query, expectation);
                var counted = unknown with { VerificationRounds = unknown.VerificationRounds + rounds };

                switch (verification.Decision)
                {
                    case ZellerVerificationDecision.Approved:
                        return await FinishApprovedAsync(unknown, counted, verification.Transaction!).ConfigureAwait(false);
                    case ZellerVerificationDecision.Declined:
                        return await FinishDeclinedAsync(unknown, counted, verification.Transaction, ZellerFailureKind.BankDeclined).ConfigureAwait(false);
                    case ZellerVerificationDecision.KeepUnknown when RequiresSupervisor(verification.UnknownReason):
                        var flagged = await SaveAsync(unknown, (verification.Transaction is null ? counted : counted.WithTransaction(verification.Transaction)) with
                        {
                            UnknownReason = verification.UnknownReason
                        }).ConfigureAwait(false);
                        Log($"attempt {flagged.AttemptGuid}: verification found {verification.UnknownReason}; needs a supervisor");
                        return Outcome(flagged, ZellerPaymentState.Unknown, verification.Transaction);
                }

                if (_time.GetUtcNow() + _options.VerificationInterval > deadline)
                {
                    break;
                }

                await Task.Delay(_options.VerificationInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log($"attempt {unknown.AttemptGuid}: verification stopped by the caller after {rounds} round(s)");
        }

        var final = await SaveAsync(unknown, unknown with { VerificationRounds = unknown.VerificationRounds + rounds }).ConfigureAwait(false);
        Log($"attempt {final.AttemptGuid}: still unknown after {rounds} round(s); leaving it for recovery");
        return Outcome(final, ZellerPaymentState.Unknown, evidence);
    }

    // ───────────── 辅助 ─────────────

    private static bool RequiresSupervisor(ZellerUnknownReason reason) => reason is
        ZellerUnknownReason.MultipleApproved or
        ZellerUnknownReason.AmountMismatch or
        ZellerUnknownReason.MissingTransactionUuid or
        ZellerUnknownReason.UnrecognisedStatus or
        ZellerUnknownReason.LateApproval;

    private void ScheduleLateRecheck(Guid attemptGuid, DateTimeOffset dueAt) =>
        _ = Task.Run(async () =>
        {
            try
            {
                // 中文注释：按落库的到期时间算剩余等待，不受计时器登记延迟的影响。
                var remaining = dueAt - _time.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, _time).ConfigureAwait(false);
                }

                await RecheckAsync(attemptGuid).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log($"attempt {attemptGuid}: late recheck failed: {ex.GetType().Name}: {ex.Message}");
            }
        });

    /// <summary>落库：落库不受调用方取消的影响。记录被别人改过说明有并发写入，属于程序错误。</summary>
    private async Task<LocalZellerPaymentAttempt> SaveAsync(LocalZellerPaymentAttempt current, LocalZellerPaymentAttempt next) =>
        await _attempts.TryUpdateAsync(current, next with { UpdatedAt = _time.GetUtcNow() }, CancellationToken.None).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Zeller attempt {current.AttemptGuid} was changed by another writer.");

    private static ZellerPaymentOutcome Outcome(
        LocalZellerPaymentAttempt attempt,
        ZellerPaymentState state,
        ZellerTransaction? transaction = null) =>
        new(state, attempt.AttemptGuid, attempt.Reference, attempt.AmountCents, attempt.FailureKind, attempt.UnknownReason, transaction, attempt.OperationKind);

    private void Log(string message) => _log?.Invoke(message);

    /// <summary>把网关的事件转成付款页能显示的进度。</summary>
    private sealed class EventForwarder(IProgress<ZellerPaymentProgress>? target) : IProgress<ZellerTerminalEvent>
    {
        public void Report(ZellerTerminalEvent value)
        {
            var kind = value.Kind switch
            {
                ZellerTerminalEventKind.AwaitingPaymentMethod => ZellerPaymentProgressKind.AwaitingCard,
                ZellerTerminalEventKind.Processing => ZellerPaymentProgressKind.Processing,
                _ => (ZellerPaymentProgressKind?)null
            };
            if (kind is { } progressKind)
            {
                target?.Report(new ZellerPaymentProgress(progressKind));
            }
        }
    }
}
