namespace Hbpos.Client.Wpf.Services;

public enum CardPaymentTerminalOutcome
{
    None,
    Cancelled,
    TimedOut,
    ResultUnknown
}

public enum CardPaymentErrorKind
{
    None,
    ConnectionFailed,
    PinpadOffline,
    CloudCommunicationFailed,
    ActiveSessionRequiresRecovery,
    SquareCommunicationFailed,
    Timeout,
    CardDeclined
}

public sealed record CardPaymentResultDisposition(
    CardPaymentTerminalOutcome Outcome,
    CardPaymentErrorKind ErrorKind = CardPaymentErrorKind.None,
    bool PreserveStatus = false)
{
    public bool RequiresRecovery => Outcome == CardPaymentTerminalOutcome.ResultUnknown;

    public static CardPaymentResultDisposition None { get; } = new(CardPaymentTerminalOutcome.None);
}

/// <summary>按支付提供商把终端原始结果收敛为付款页可安全呈现的结果。</summary>
public interface ICardPaymentResultPolicy
{
    bool CanClassify(PaymentTenderAttemptResult result);

    CardPaymentResultDisposition Classify(PaymentTenderAttemptResult result);
}

public sealed class CardPaymentResultPolicyResolver(IEnumerable<ICardPaymentResultPolicy> policies)
{
    private readonly IReadOnlyList<ICardPaymentResultPolicy> _policies = policies.ToArray();

    public PaymentTenderAttemptResult Apply(PaymentTenderAttemptResult result)
    {
        if (result.Succeeded || result.CardResult is not null)
        {
            return result;
        }

        var policy = _policies.FirstOrDefault(candidate => candidate.CanClassify(result));
        return result with { CardResult = policy?.Classify(result) ?? CardPaymentResultDisposition.None };
    }
}

public sealed class LinklyCardPaymentResultPolicy : ICardPaymentResultPolicy
{
    public bool CanClassify(PaymentTenderAttemptResult result) =>
        result.StatusKey.StartsWith("linkly.", StringComparison.Ordinal) ||
        string.Equals(result.StatusKey, "payment.card.resultUnknown", StringComparison.Ordinal);

    public CardPaymentResultDisposition Classify(PaymentTenderAttemptResult result)
    {
        return result.StatusKey switch
        {
            "payment.card.resultUnknown" or
            "linkly.backend.resultUnknown" or
            "linkly.backend.cancelledUnknown" or
            "linkly.cloud.resultUnknown" => new(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            // 本机还有未结的刷卡交易：不是新交易的结果未知，不锁付款页（可改用其他方式付款），
            // 但提示去异常中心处理，处理完成前不会向终端发起新的扣款。
            "linkly.recovery.openAttemptBlocksPayment" => new(
                CardPaymentTerminalOutcome.None,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            "linkly.local.connectionFailed" or "payment.card.linklyUnavailable" => new(
                CardPaymentTerminalOutcome.None,
                CardPaymentErrorKind.ConnectionFailed),
            "linkly.local.pinpadOffline" => new(
                CardPaymentTerminalOutcome.None,
                CardPaymentErrorKind.PinpadOffline),
            "linkly.cloud.communicationFailed" or "linkly.backend.communicationFailed" => new(
                CardPaymentTerminalOutcome.None,
                CardPaymentErrorKind.CloudCommunicationFailed),
            "linkly.local.timeout" or "linkly.cloud.timeout" or "linkly.backend.timeout" => new(
                CardPaymentTerminalOutcome.TimedOut,
                CardPaymentErrorKind.Timeout),
            _ => CardPaymentResultDisposition.None
        };
    }
}

public sealed class SquareCardPaymentResultPolicy : ICardPaymentResultPolicy
{
    public bool CanClassify(PaymentTenderAttemptResult result) =>
        result.StatusKey.StartsWith("payment.card.square", StringComparison.Ordinal);

    public CardPaymentResultDisposition Classify(PaymentTenderAttemptResult result)
    {
        return result.StatusKey switch
        {
            "payment.card.squareCanceled" or
            "payment.card.squareCanceledBuyer" or
            "payment.card.squareCanceledSeller" => new(
                CardPaymentTerminalOutcome.Cancelled,
                PreserveStatus: true),
            "payment.card.squareTimedOut" => new(
                CardPaymentTerminalOutcome.TimedOut,
                CardPaymentErrorKind.Timeout,
                PreserveStatus: true),
            "payment.card.squareTerminalOffline" or "payment.card.squareTerminalNotPickedUp" or
            "payment.card.squareCommunicationFailed" => new(
                CardPaymentTerminalOutcome.None,
                CardPaymentErrorKind.SquareCommunicationFailed,
                PreserveStatus: true),
            _ => CardPaymentResultDisposition.None
        };
    }
}

public sealed class FallbackCardPaymentResultPolicy : ICardPaymentResultPolicy
{
    public bool CanClassify(PaymentTenderAttemptResult result) => true;

    public CardPaymentResultDisposition Classify(PaymentTenderAttemptResult result)
    {
        var statusKey = result.StatusKey;
        var message = result.StatusMessage ?? string.Empty;
        if (string.Equals(statusKey, "payment.card.resultUnknown", StringComparison.Ordinal) ||
            message.Contains("could not be confirmed", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true);
        }

        if (string.Equals(statusKey, "payment.status.cardCancelled", StringComparison.Ordinal) ||
            message.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return new(CardPaymentTerminalOutcome.Cancelled);
        }

        if (string.Equals(statusKey, "payment.status.cardTimedOut", StringComparison.Ordinal) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return new(CardPaymentTerminalOutcome.TimedOut, CardPaymentErrorKind.Timeout);
        }

        if (message.Contains("unfinished card transaction", StringComparison.OrdinalIgnoreCase))
        {
            return new(CardPaymentTerminalOutcome.ResultUnknown, CardPaymentErrorKind.ActiveSessionRequiresRecovery);
        }

        if (message.Contains("connection failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("connection was closed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("could not be sent", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return new(CardPaymentTerminalOutcome.None, CardPaymentErrorKind.ConnectionFailed);
        }

        if (message.Contains("communication failed", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                CardPaymentTerminalOutcome.None,
                message.Contains("Square", StringComparison.OrdinalIgnoreCase)
                    ? CardPaymentErrorKind.SquareCommunicationFailed
                    : CardPaymentErrorKind.CloudCommunicationFailed);
        }

        return result.IsTerminalDecline &&
               string.Equals(statusKey, "payment.status.cardDeclined", StringComparison.Ordinal)
            ? new(CardPaymentTerminalOutcome.None, CardPaymentErrorKind.CardDeclined)
            : CardPaymentResultDisposition.None;
    }
}
