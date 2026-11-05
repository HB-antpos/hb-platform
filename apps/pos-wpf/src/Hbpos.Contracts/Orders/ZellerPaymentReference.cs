namespace Hbpos.Contracts.Orders;

/// <summary>
/// Zeller 直连刷卡的引用格式：发给 Zeller 的 reference，以及写进订单付款记录的引用。客户端与服务端共用。
/// </summary>
/// <remarks>
/// 中文注释：两类引用分开。
/// 1. 发给 Zeller 的 reference（HBZ-… / HBZR-…）由本地 attempt 的 GUID 派生，付款前先落库，是崩溃后找回交易的唯一线索。
///    Zeller 不对 reference 去重：同一个 reference 再次扣款会再扣一次，所以每次尝试必须用新的 attempt。
/// 2. 订单付款记录里的引用（ZELLER:… / ZELLERRF:…）同时带上 reference 和 Zeller 的 transactionUuid。
///    一个 reference 可能对应多笔交易，退款必须用 transactionUuid 才能唯一指向原交易。
/// </remarks>
public static class ZellerPaymentReference
{
    public const string Processor = "Zeller";
    public const string PaymentPrefix = "ZELLER:";
    public const string RefundPrefix = "ZELLERRF:";
    public const string PurchaseReferencePrefix = "HBZ-";
    public const string RefundReferencePrefix = "HBZR-";

    /// <summary>扣款时发给 Zeller 的 reference：HBZ- 加 32 位十六进制，共 36 个字符，不含个人信息。</summary>
    public static string CreatePurchaseReference(Guid attemptGuid) =>
        $"{PurchaseReferencePrefix}{RequireAttempt(attemptGuid):N}";

    /// <summary>退款时发给 Zeller 的 reference：HBZR- 加 32 位十六进制，共 37 个字符。</summary>
    public static string CreateRefundReference(Guid attemptGuid) =>
        $"{RefundReferencePrefix}{RequireAttempt(attemptGuid):N}";

    /// <summary>订单里的扣款引用：ZELLER:{reference}:{transactionUuid}。</summary>
    public static string FormatPayment(string reference, string transactionUuid) =>
        $"{PaymentPrefix}{RequireReference(reference, PurchaseReferencePrefix)}:{RequireTransactionUuid(transactionUuid)}";

    /// <summary>订单里的退款引用：ZELLERRF:{refundReference}:{refundTransactionUuid}。</summary>
    public static string FormatRefund(string refundReference, string refundTransactionUuid) =>
        $"{RefundPrefix}{RequireReference(refundReference, RefundReferencePrefix)}:{RequireTransactionUuid(refundTransactionUuid)}";

    // 中文注释：防护判断包括损坏的 Zeller 引用，不能因为格式错误而回退到别的刷卡通道。
    public static bool IsZeller(string? reference)
    {
        var trimmed = reference?.Trim();
        return trimmed is not null &&
            (trimmed.StartsWith(PaymentPrefix, StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith(RefundPrefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>退款来源是否属于 Zeller：原付款引用本身，或包装过的退款引用里的原付款。</summary>
    public static bool IsZellerRefundSource(string? reference) =>
        IsZeller(reference) ||
        (CardRefundReference.TryGetOriginalReference(reference, out var original) && IsZeller(original));

    public static bool TryParsePayment(string? reference, out ZellerPaymentReferenceParts parts) =>
        TryParse(reference, PaymentPrefix, PurchaseReferencePrefix, out parts);

    public static bool TryParseRefund(string? reference, out ZellerPaymentReferenceParts parts) =>
        TryParse(reference, RefundPrefix, RefundReferencePrefix, out parts);

    /// <summary>
    /// 取出退款要用的原交易 transactionUuid。入参可以是原付款引用，也可以是包装过的退款引用。
    /// </summary>
    public static bool TryGetOriginalTransactionUuid(string? reference, out string transactionUuid)
    {
        transactionUuid = string.Empty;
        var source = CardRefundReference.TryGetOriginalReference(reference, out var original) ? original : reference;
        if (!TryParsePayment(source, out var parts))
        {
            return false;
        }

        transactionUuid = parts.TransactionUuid;
        return true;
    }

    private static bool TryParse(
        string? reference,
        string prefix,
        string referencePrefix,
        out ZellerPaymentReferenceParts parts)
    {
        parts = default;
        var trimmed = reference?.Trim();
        if (trimmed is null || !trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = trimmed[prefix.Length..].Split(':');
        if (segments.Length != 2 ||
            !IsAttemptReference(segments[0], referencePrefix) ||
            !Guid.TryParse(segments[1], out var transactionUuid) ||
            transactionUuid == Guid.Empty)
        {
            return false;
        }

        parts = new ZellerPaymentReferenceParts(segments[0], transactionUuid.ToString("D"));
        return true;
    }

    private static bool IsAttemptReference(string value, string referencePrefix) =>
        value.StartsWith(referencePrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(value[referencePrefix.Length..], "N", out var attemptGuid) &&
        attemptGuid != Guid.Empty;

    private static Guid RequireAttempt(Guid attemptGuid) =>
        attemptGuid == Guid.Empty
            ? throw new ArgumentException("Zeller attempt identity is required.", nameof(attemptGuid))
            : attemptGuid;

    private static string RequireReference(string reference, string referencePrefix)
    {
        var trimmed = reference?.Trim() ?? string.Empty;
        return IsAttemptReference(trimmed, referencePrefix)
            ? trimmed
            : throw new ArgumentException($"Zeller reference must be {referencePrefix} followed by the attempt identity.", nameof(reference));
    }

    private static string RequireTransactionUuid(string transactionUuid) =>
        Guid.TryParse(transactionUuid, out var parsed) && parsed != Guid.Empty
            ? parsed.ToString("D")
            : throw new ArgumentException("Zeller transaction UUID is required.", nameof(transactionUuid));
}

/// <summary>订单里的 Zeller 引用拆出的两部分：发给 Zeller 的 reference，和 Zeller 返回的 transactionUuid。</summary>
public readonly record struct ZellerPaymentReferenceParts(string Reference, string TransactionUuid);
