using System.Globalization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Models;

public sealed record LocalOrderSummary(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    string SyncStatus,
    int LineCount,
    string PaymentSummary)
{
    public string ShortOrderId => OrderGuid.ToString("N")[..8].ToUpperInvariant();

    public string SoldAtDisplay => SoldAt.ToLocalTime().ToString("MMM dd, yyyy HH:mm");

    public string StatusLabel => SyncStatus;
}

public sealed record ReceiptPreviewLine(
    string DisplayName,
    string LookupCode,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal ActualAmount)
{
    public string QuantityDisplay => Quantity.ToString("0.##");

    public string? ProductCode { get; init; }

    public string? ItemNumber { get; init; }

    public string? ProductImage { get; init; }
}

public sealed record ReceiptPaymentLine(
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null)
{
    public string? DisplayReference => PaymentReferenceDisplay.Format(Method, Reference);

    public decimal? VoucherRemainingBalance => PaymentReferenceDisplay.GetVoucherRemainingBalance(Method, Reference);

    public string? CardSummary => CardTransactions is { Count: > 0 }
        ? PaymentReferenceDisplay.FormatCardSummary(CardTransactions[0])
        : null;

    public string? ReceiptText => CardTransactions?
        .Select(transaction => transaction.ReceiptText)
        .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

    public string MethodLabel => Method switch
    {
        PaymentMethodKind.Cash => "Cash",
        PaymentMethodKind.Card => PaymentReferenceDisplay.Format(Method, Reference)?.StartsWith("SQ", StringComparison.OrdinalIgnoreCase) == true
            ? "Square"
            : PaymentReferenceDisplay.Format(Method, Reference)?.StartsWith("ANZ:", StringComparison.OrdinalIgnoreCase) == true
                ? "ANZ Linkly"
                : "Card",
        PaymentMethodKind.Voucher => "Voucher",
        _ => Method.ToString()
    };
}

public static class PaymentReferenceDisplay
{
    public static string? Format(PaymentMethodKind method, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        if (method == PaymentMethodKind.Voucher)
        {
            var parts = reference.Split(':', StringSplitOptions.TrimEntries);
            return parts.Length >= 2 && parts[0].Equals("VOUCHER", StringComparison.OrdinalIgnoreCase)
                ? parts[1]
                : reference;
        }

        if (method != PaymentMethodKind.Card)
        {
            return reference;
        }

        var cardReference = CardRefundReference.GetDisplayReference(reference);
        return LinklyBackendPaymentReference.GetDisplayReference(cardReference);
    }

    public static decimal? GetVoucherRemainingBalance(PaymentMethodKind method, string? reference)
    {
        if (method != PaymentMethodKind.Voucher || string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var parts = reference.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length < 4 || !parts[0].Equals("VOUCHER", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 中文注释：余额段是本地打印扩展，非法或非正数余额都按无余额处理。
        return decimal.TryParse(parts[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var balance) &&
            balance > 0m
                ? balance
                : null;
    }

    public static string? FormatCardSummary(CardTransactionDto transaction)
    {
        var parts = new[]
        {
            string.IsNullOrWhiteSpace(transaction.CardType) ? null : transaction.CardType.Trim(),
            string.IsNullOrWhiteSpace(transaction.MaskedCardNumber) ? null : transaction.MaskedCardNumber.Trim(),
            string.IsNullOrWhiteSpace(transaction.AuthCode) ? null : $"Auth {transaction.AuthCode.Trim()}",
            FormatResponseText(transaction)
        }.Where(part => !string.IsNullOrWhiteSpace(part));

        var summary = string.Join(" | ", parts);
        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }

    private static string? FormatResponseText(CardTransactionDto transaction)
    {
        var text = string.IsNullOrWhiteSpace(transaction.ResponseText) ? null : transaction.ResponseText.Trim();
        // 中文注释：ANZ 终端偶发在批准（00/08/11 + 批准码）的回包里带着 OPERATOR TIMEOUT 文案
        // （终端批准后等操作员确认超时，10-25 1033 店实测，交易与小票均为 APPROVED）。
        // 批准码为准：展示层把明显的超时/取消/拒绝文案换成 APPROVED，避免店员和顾客误以为没付成功；
        // 库里的原始 ResponseText 不改，仍可用于排查。
        if (text is not null &&
            LinklyApprovalResponseCodes.IsApproved(transaction.ResponseCode) &&
            !text.Contains("APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            return "APPROVED";
        }

        return text;
    }
}
