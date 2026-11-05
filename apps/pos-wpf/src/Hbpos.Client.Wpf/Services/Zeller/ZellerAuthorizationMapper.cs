using System.Globalization;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services.Zeller;

/// <summary>
/// 把 Zeller 扣款的结论转成收款流程现在使用的 <see cref="PaymentAuthorizationResult"/>。
/// 接入收款流程时，ConfiguredCardTerminalClient 的 Zeller 分支直接返回这个结果。
/// </summary>
/// <remarks>
/// 中文注释：字段对应按「Zeller 直连接入方案」的 D1——Processor 写 Zeller，TxnRef 写 transactionUuid，
/// AuthCode 写批准码，CardType 写卡组织，订单引用写 ZELLER:{reference}:{transactionUuid}。
/// 现有的 CardTransactionDto 没有 RRN 字段，RRN 留在本地尝试表和小票原文里。
/// </remarks>
public static class ZellerAuthorizationMapper
{
    public const string StatusKeyPrefix = "payment.card.zeller.";

    public static PaymentAuthorizationResult ToAuthorizationResult(ZellerPaymentOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var amount = outcome.AmountCents / 100m;
        var isRefund = outcome.Operation == ZellerAttemptOperation.Refund;

        switch (outcome.State)
        {
            case ZellerPaymentState.Approved:
                var transaction = outcome.Transaction
                    ?? throw new ArgumentException("An approved Zeller outcome must carry its transaction.", nameof(outcome));

                // 中文注释：退款时 Reference 是 ZELLERRF:…，收款流程会再和原交易引用包成 CARD_REFUND|refund=…|original=…。
                return new PaymentAuthorizationResult(
                    Approved: true,
                    Reference: outcome.PaymentReference,
                    Message: isRefund ? "Zeller refund approved." : "Zeller payment approved.",
                    AuthorizedAmount: transaction.AmountCents / 100m,
                    CardTransactions: [ToCardTransaction(transaction)],
                    Processor: ZellerPaymentReference.Processor,
                    TxnRef: outcome.Reference,
                    ResponseCode: transaction.ResponseCode,
                    ResponseText: transaction.ResponseText,
                    StatusKey: StatusKeyPrefix + (isRefund ? "refundApproved" : "approved"));

            case ZellerPaymentState.Declined:
                // 中文注释：带上银行的响应码和文案，付款页才会弹「银行未批准」而不是普通错误。
                // 退款被拒最常见的是超额退款（code 13）。
                return new PaymentAuthorizationResult(
                    Approved: false,
                    Reference: outcome.Reference,
                    Message: outcome.Transaction?.ResponseText ?? (isRefund ? "The refund was declined." : "The card was declined."),
                    Processor: ZellerPaymentReference.Processor,
                    TxnRef: outcome.Reference,
                    ResponseCode: outcome.Transaction?.ResponseCode ?? "DECLINED",
                    ResponseText: outcome.Transaction?.ResponseText ?? "Declined",
                    StatusKey: StatusKeyPrefix + (isRefund
                        ? "refundDeclined"
                        : outcome.FailureKind == ZellerFailureKind.CardProblem ? "cardProblem" : "declined"));

            case ZellerPaymentState.NotPaid:
                return new PaymentAuthorizationResult(
                    Approved: false,
                    Reference: outcome.Reference,
                    Message: NotPaidMessage(outcome.FailureKind),
                    Processor: ZellerPaymentReference.Processor,
                    TxnRef: outcome.Reference,
                    StatusKey: StatusKeyPrefix + NotPaidKey(outcome.FailureKind));

            default:
                var inProgress = outcome.UnknownReason == ZellerUnknownReason.RefundAlreadyInProgress;
                return new PaymentAuthorizationResult(
                    Approved: false,
                    Reference: outcome.Reference,
                    Message: inProgress
                        ? "A refund for this return is already in progress. Check it in the recovery center instead of refunding again."
                        : isRefund
                            ? "The Zeller refund result is not known yet. Do not refund again; check it in the recovery center."
                            : "The Zeller payment result is not known yet. Do not ask the customer to pay again; check it in the recovery center.",
                    AuthorizedAmount: amount,
                    Processor: ZellerPaymentReference.Processor,
                    TxnRef: outcome.Reference,
                    StatusKey: StatusKeyPrefix + (inProgress ? "refundInProgress" : "resultUnknown"),
                    ResultUnknown: true);
        }
    }

    public static CardTransactionDto ToCardTransaction(ZellerTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return new CardTransactionDto(
            ZellerPaymentReference.Processor,
            TxnRef: transaction.TransactionUuid,
            AuthCode: string.IsNullOrWhiteSpace(transaction.ApprovalCode) ? null : transaction.ApprovalCode,
            CardType: transaction.Scheme,
            CardBin: int.TryParse(transaction.Bin, NumberStyles.None, CultureInfo.InvariantCulture, out var bin) ? bin : null,
            MaskedCardNumber: transaction.PanMasked,
            MerchantId: transaction.MerchantId,
            ResponseCode: transaction.ResponseCode,
            ResponseText: transaction.ResponseText,
            Stan: null,
            BankDateTime: transaction.Timestamp,
            Amount: transaction.AmountCents / 100m,
            ReceiptText: string.IsNullOrWhiteSpace(transaction.ReceiptText) ? null : transaction.ReceiptText);
    }

    private static string NotPaidKey(ZellerFailureKind kind) => kind switch
    {
        ZellerFailureKind.Cancelled => "cancelled",
        ZellerFailureKind.Timeout => "timeout",
        ZellerFailureKind.TerminalUnavailable => "terminalUnavailable",
        ZellerFailureKind.SetupRequired => "setupRequired",
        ZellerFailureKind.SdkUnavailable => "sdkUnavailable",
        ZellerFailureKind.Busy => "busy",
        ZellerFailureKind.InvalidRequest => "invalidRequest",
        ZellerFailureKind.CardProblem => "cardProblem",
        ZellerFailureKind.BankDeclined => "declined",
        ZellerFailureKind.OriginalTransactionNotFound => "originalNotFound",
        _ => "notPaid"
    };

    private static string NotPaidMessage(ZellerFailureKind kind) => kind switch
    {
        ZellerFailureKind.Cancelled => "The Zeller payment was cancelled. The customer was not charged.",
        ZellerFailureKind.Timeout => "No card was presented in time. The customer was not charged.",
        ZellerFailureKind.TerminalUnavailable => "The Zeller terminal is not reachable. Check that it is on and online.",
        ZellerFailureKind.SetupRequired => "This POS is not paired with a Zeller terminal. Pair it in Settings.",
        ZellerFailureKind.SdkUnavailable => "The Zeller payment component could not start on this POS.",
        ZellerFailureKind.Busy => "The Zeller terminal is busy. Try again in a moment.",
        ZellerFailureKind.InvalidRequest => "The Zeller payment request was invalid.",
        ZellerFailureKind.OriginalTransactionNotFound => "Zeller could not find the original transaction. Nothing was refunded.",
        _ => "The customer was not charged."
    };
}
