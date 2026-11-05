namespace Hbpos.Client.Wpf.Services.Zeller;

/// <summary>一次扣款或退款调用结束后，下一步该怎么办。</summary>
public enum ZellerCallDecision
{
    /// <summary>批准：金额、reference 都对得上，可以建单（退款：退款已完成）。</summary>
    Approved,

    /// <summary>银行明确拒绝：未付款。</summary>
    Declined,

    /// <summary>请求确定没有发出：未付款，不需要查询。</summary>
    NotSubmitted,

    /// <summary>取消、超时、刷卡机没连上：立即按 reference 查一次，查到 0 笔才算未付款。</summary>
    VerifyThenNotPaid,

    /// <summary>卡片类错误：立即按 reference 查一次确认，再按拒付处理。</summary>
    VerifyThenDeclined,

    /// <summary>结果未知：自动重查，查不清就进恢复中心。绝不重新发起扣款。</summary>
    Unknown
}

/// <summary>给付款页选提示文案用的失败种类。</summary>
public enum ZellerFailureKind
{
    None = 0,
    BankDeclined,
    CardProblem,
    Cancelled,
    Timeout,
    TerminalUnavailable,
    SetupRequired,
    InvalidRequest,
    Busy,

    /// <summary>Zeller SDK 本身起不来（例如缺少 WebView2 运行时），请求没有发出。</summary>
    SdkUnavailable,
    OriginalTransactionNotFound,
    ResultUnknown
}

/// <summary>结果未知的原因，落库后供恢复中心和排查使用。</summary>
public enum ZellerUnknownReason
{
    None = 0,
    SdkError,
    WatchdogTimeout,
    SdkException,
    ReferenceMismatch,
    AmountMismatch,
    MissingTransactionUuid,
    UnrecognisedStatus,
    TerminalConnectionLost,
    MultipleApproved,
    LateApproval,
    VerificationFailed,
    ProcessExited,

    /// <summary>同一笔退货已经有一笔退款在进行或结果未知，不能再发第二笔。只出现在返回给调用方的结论里，不落库。</summary>
    RefundAlreadyInProgress
}

/// <summary>本地 attempt 对这次调用的预期：类型、金额（分）和发给 Zeller 的 reference。</summary>
public sealed record ZellerAttemptExpectation(ZellerTransactionType Type, int AmountCents, string Reference);

public sealed record ZellerCallClassification(
    ZellerCallDecision Decision,
    ZellerFailureKind FailureKind = ZellerFailureKind.None,
    ZellerUnknownReason UnknownReason = ZellerUnknownReason.None,
    bool RebuildTerminalFirst = false,
    bool SerialGateBreached = false,
    ZellerTransaction? Transaction = null);

/// <summary>按 reference 查询之后的结论。</summary>
public enum ZellerVerificationDecision
{
    Approved,
    Declined,
    NotPaid,

    /// <summary>仍然不能下结论：保持结果未知。UnknownReason 为 None 表示只是还没查到，可以继续重查。</summary>
    KeepUnknown
}

public sealed record ZellerVerificationClassification(
    ZellerVerificationDecision Decision,
    ZellerUnknownReason UnknownReason = ZellerUnknownReason.None,
    ZellerTransaction? Transaction = null);

/// <summary>
/// 把 SDK 的返回和按 reference 查询的结果变成本地结论。纯函数，不访问网络和数据库。
/// </summary>
/// <remarks>
/// 中文注释：规则来自 Zeller 直连接入方案的状态机一节，依据是 2026-11-05 的两轮真机验证。原则：
/// 只有 Status=Approved 且金额、reference 都对得上才算批准；只有明确的拒绝，
/// 或者明确的取消、超时并且按 reference 查到 0 笔，才算未付款；其余一律结果未知。
/// 判定只看 Status 和错误类型，不看 ResponseText（同一笔交易在返回和查询里文案不同），也不看时间戳。
/// </remarks>
public static class ZellerResultClassifier
{
    /// <summary>扣款或退款调用刚结束时的分类。</summary>
    public static ZellerCallClassification ClassifyCall(ZellerCallResult result, ZellerAttemptExpectation expected)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(expected);

        return result.Outcome switch
        {
            ZellerCallOutcome.Transaction => ClassifyReturnedTransaction(result.Transaction, expected),
            ZellerCallOutcome.Error => ClassifyError(result.ErrorType ?? ZellerErrorType.Unrecognised, result.ReachedTerminal, expected.Type),

            // 看门狗放弃的调用和抛异常的调用，SDK 内部状态都不可信：先重建 Terminal 再查询。
            ZellerCallOutcome.Abandoned => Unknown(ZellerUnknownReason.WatchdogTimeout, rebuildTerminalFirst: true),
            ZellerCallOutcome.Faulted => Unknown(ZellerUnknownReason.SdkException, rebuildTerminalFirst: true),

            // 请求没有进入 SDK：要么在网关里排队时被取消，要么创建 Terminal 或发起调用时就失败了。
            ZellerCallOutcome.NotStarted => new ZellerCallClassification(
                ZellerCallDecision.NotSubmitted,
                result.ErrorType == ZellerErrorType.Cancelled ? ZellerFailureKind.Cancelled : ZellerFailureKind.SdkUnavailable),
            _ => Unknown(ZellerUnknownReason.SdkError)
        };
    }

    /// <summary>
    /// 按 reference 查询之后的分类。pending 是 ClassifyCall 给出的、需要查询才能定论的三种决定之一。
    /// </summary>
    public static ZellerVerificationClassification ClassifyVerification(
        ZellerCallDecision pending,
        ZellerQueryResult query,
        ZellerAttemptExpectation expected)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(expected);
        if (pending is not (ZellerCallDecision.VerifyThenNotPaid or ZellerCallDecision.VerifyThenDeclined or ZellerCallDecision.Unknown))
        {
            throw new ArgumentOutOfRangeException(nameof(pending), pending, "Only decisions that need verification can be verified.");
        }

        // 查询本身失败时，「没有交易」不是结论。
        if (!query.Succeeded)
        {
            return KeepUnknown(ZellerUnknownReason.VerificationFailed);
        }

        var sameReference = query.Transactions
            .Where(transaction => ReferenceMatches(transaction.ExternalReference, expected.Reference))
            .ToList();
        if (sameReference.Any(transaction =>
                transaction.Type == ZellerTransactionType.Unknown ||
                transaction.Status == ZellerTransactionStatus.Unknown))
        {
            return KeepUnknown(ZellerUnknownReason.UnrecognisedStatus);
        }

        var relevant = sameReference.Where(transaction => transaction.Type == expected.Type).ToList();
        var approved = relevant.Where(transaction => transaction.Status == ZellerTransactionStatus.Approved).ToList();

        // 同一个 reference 下出现多笔批准（Zeller 不去重），只能由主管处理。被拒的笔数不算。
        if (approved.Count > 1)
        {
            return KeepUnknown(ZellerUnknownReason.MultipleApproved);
        }

        if (approved.Count == 1)
        {
            var transaction = approved[0];
            if (transaction.AmountCents != expected.AmountCents)
            {
                return KeepUnknown(ZellerUnknownReason.AmountMismatch, transaction);
            }

            return string.IsNullOrWhiteSpace(transaction.TransactionUuid)
                ? KeepUnknown(ZellerUnknownReason.MissingTransactionUuid, transaction)
                : new ZellerVerificationClassification(ZellerVerificationDecision.Approved, Transaction: transaction);
        }

        var declined = relevant.LastOrDefault(transaction =>
            transaction.Status is ZellerTransactionStatus.Declined or ZellerTransactionStatus.Failed);
        if (declined is not null)
        {
            return new ZellerVerificationClassification(ZellerVerificationDecision.Declined, Transaction: declined);
        }

        // 查到 0 笔：只有 SDK 已经明确说未付款的那两类才能收尾，结果未知的继续保持未知。
        return pending switch
        {
            ZellerCallDecision.VerifyThenNotPaid => new ZellerVerificationClassification(ZellerVerificationDecision.NotPaid),
            ZellerCallDecision.VerifyThenDeclined => new ZellerVerificationClassification(ZellerVerificationDecision.Declined),
            _ => KeepUnknown(ZellerUnknownReason.None)
        };
    }

    /// <summary>
    /// 按未付款收尾约 60 秒后的复核。查到批准说明顾客其实已被扣款：返回 KeepUnknown，
    /// 调用方要把记录转回结果未知并交给恢复中心。查询失败时维持未付款，UnknownReason 标成 VerificationFailed 供调用方决定是否再查。
    /// </summary>
    public static ZellerVerificationClassification ClassifyLateRecheck(ZellerQueryResult query, ZellerAttemptExpectation expected)
    {
        var verification = ClassifyVerification(ZellerCallDecision.VerifyThenNotPaid, query, expected);
        return verification.Decision switch
        {
            ZellerVerificationDecision.Approved =>
                KeepUnknown(ZellerUnknownReason.LateApproval, verification.Transaction),
            ZellerVerificationDecision.KeepUnknown when verification.UnknownReason == ZellerUnknownReason.VerificationFailed =>
                new ZellerVerificationClassification(ZellerVerificationDecision.NotPaid, ZellerUnknownReason.VerificationFailed),
            _ => verification
        };
    }

    private static ZellerCallClassification ClassifyReturnedTransaction(ZellerTransaction? transaction, ZellerAttemptExpectation expected)
    {
        if (transaction is null)
        {
            return Unknown(ZellerUnknownReason.SdkError);
        }

        // 返回的交易不是这次尝试的：SDK 可能把别的调用的结果交给了我们（并发串线时出现过），重建后再查。
        if (!ReferenceMatches(transaction.ExternalReference, expected.Reference) || transaction.Type != expected.Type)
        {
            return Unknown(ZellerUnknownReason.ReferenceMismatch, rebuildTerminalFirst: true, transaction);
        }

        switch (transaction.Status)
        {
            case ZellerTransactionStatus.Approved:
                if (transaction.AmountCents != expected.AmountCents)
                {
                    return Unknown(ZellerUnknownReason.AmountMismatch, transaction: transaction);
                }

                return string.IsNullOrWhiteSpace(transaction.TransactionUuid)
                    ? Unknown(ZellerUnknownReason.MissingTransactionUuid, transaction: transaction)
                    : new ZellerCallClassification(ZellerCallDecision.Approved, Transaction: transaction);

            // 被拒的交易也是「成交类响应」，必须看 Status。
            case ZellerTransactionStatus.Declined:
            case ZellerTransactionStatus.Failed:
                return new ZellerCallClassification(ZellerCallDecision.Declined, ZellerFailureKind.BankDeclined, Transaction: transaction);

            default:
                return Unknown(ZellerUnknownReason.UnrecognisedStatus, transaction: transaction);
        }
    }

    private static ZellerCallClassification ClassifyError(ZellerErrorType errorType, bool reachedTerminal, ZellerTransactionType operation)
    {
        switch (errorType)
        {
            // 本地参数校验，0 毫秒返回，请求没有发出。
            case ZellerErrorType.OperationRequestInvalid:
                return new ZellerCallClassification(ZellerCallDecision.NotSubmitted, ZellerFailureKind.InvalidRequest);

            // 这一笔没有发出。但它说明我们的串行门有漏洞，而且当时进行中的那一笔结果已经丢失，调用方要按故障处理。
            case ZellerErrorType.OperationBusy:
                return new ZellerCallClassification(
                    ZellerCallDecision.NotSubmitted,
                    ZellerFailureKind.Busy,
                    RebuildTerminalFirst: true,
                    SerialGateBreached: true);

            case ZellerErrorType.SetupRequired:
            case ZellerErrorType.FeatureNotAvailable:
            case ZellerErrorType.PermissionsMissing:
                return new ZellerCallClassification(ZellerCallDecision.VerifyThenNotPaid, ZellerFailureKind.SetupRequired);

            case ZellerErrorType.Cancelled:
                return new ZellerCallClassification(ZellerCallDecision.VerifyThenNotPaid, ZellerFailureKind.Cancelled);

            case ZellerErrorType.InactivityTimeout:
                return new ZellerCallClassification(ZellerCallDecision.VerifyThenNotPaid, ZellerFailureKind.Timeout);

            // 刷卡机连不上：请求没到刷卡机时可以查一次就收尾；已经到了刷卡机，就不能排除它后来完成了扣款。
            case ZellerErrorType.ConnectionFailure:
            case ZellerErrorType.PairedDeviceBusy:
            case ZellerErrorType.PairedDeviceNotAvailable:
                return reachedTerminal
                    ? Unknown(ZellerUnknownReason.TerminalConnectionLost)
                    : new ZellerCallClassification(ZellerCallDecision.VerifyThenNotPaid, ZellerFailureKind.TerminalUnavailable);

            case ZellerErrorType.CardDeclined:
            case ZellerErrorType.TransactionDeclined:
                return new ZellerCallClassification(ZellerCallDecision.VerifyThenDeclined, ZellerFailureKind.BankDeclined);

            case ZellerErrorType.CardBlocked:
            case ZellerErrorType.CardNotSupported:
            case ZellerErrorType.CardReadFailure:
            case ZellerErrorType.CardInsertRequired:
                return new ZellerCallClassification(ZellerCallDecision.VerifyThenDeclined, ZellerFailureKind.CardProblem);

            // 退款时原交易不存在：退款没有发生，不重试。扣款不该出现这个错误，出现了按未知处理。
            case ZellerErrorType.TransactionNotFound:
                return operation == ZellerTransactionType.Refund
                    ? new ZellerCallClassification(ZellerCallDecision.NotSubmitted, ZellerFailureKind.OriginalTransactionNotFound)
                    : Unknown(ZellerUnknownReason.SdkError);

            // 收到了别的调用的响应（并发串线时出现过）：SDK 内部状态不可信，先重建。
            case ZellerErrorType.OperationResponseInvalid:
                return Unknown(ZellerUnknownReason.SdkError, rebuildTerminalFirst: true);

            // NetworkFailure 不代表没扣款：实测一次已扣、一次没扣。其余几种没有观察到，按保守的一侧处理。
            default:
                return Unknown(ZellerUnknownReason.SdkError);
        }
    }

    private static bool ReferenceMatches(string? actual, string expected) =>
        string.Equals(actual?.Trim(), expected, StringComparison.Ordinal);

    private static ZellerCallClassification Unknown(
        ZellerUnknownReason reason,
        bool rebuildTerminalFirst = false,
        ZellerTransaction? transaction = null) =>
        new(ZellerCallDecision.Unknown, ZellerFailureKind.ResultUnknown, reason, rebuildTerminalFirst, Transaction: transaction);

    private static ZellerVerificationClassification KeepUnknown(ZellerUnknownReason reason, ZellerTransaction? transaction = null) =>
        new(ZellerVerificationDecision.KeepUnknown, reason, transaction);
}
