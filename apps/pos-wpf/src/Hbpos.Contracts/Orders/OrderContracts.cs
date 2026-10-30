using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Contracts.Orders;

public enum PaymentMethodKind
{
    Cash = 1,
    Card = 2,
    Voucher = 3
}

public enum OrderLineKind
{
    Sale = 1,
    Return = 2
}

/// <summary>
/// 订单来源的显式类型：RemoteClaim 表示来自共享挂单 claim 的远程取单，
/// OfflineOrigin 表示设备离线恢复/无 claim 的上传来源。
/// </summary>
public enum HeldOrderSourceKind
{
    RemoteClaim = 1,
    OfflineOrigin = 2
}

/// <summary>订单同步时的共享挂单来源；仅在末尾追加，保持旧调用源兼容。 </summary>
public sealed record HeldOrderSourceDto(
    Guid HoldGuid,
    Guid? ClaimGuid = null,
    HeldOrderSourceKind? SourceKind = null)
{
    /// <summary>显式 SourceKind 优先；缺省时按 ClaimGuid 是否存在推断。 </summary>
    [JsonIgnore]
    public HeldOrderSourceKind Kind =>
        SourceKind ?? (ClaimGuid is null
            ? HeldOrderSourceKind.OfflineOrigin
            : HeldOrderSourceKind.RemoteClaim);
}

/// <summary>订单与共享挂单的关联结果，语义严格限定为四种。 </summary>
public enum HeldOrderDisposition
{
    None = 0,
    Primary = 1,
    Duplicate = 2,
    Unmatched = 3
}

public sealed record OrderSyncRequest(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    IReadOnlyList<OrderLineSyncDto> Lines,
    IReadOnlyList<PaymentSyncDto> Payments,
    HeldOrderSourceDto? HeldOrderSource = null);

public sealed record OrderLineSyncDto(
    Guid OrderLineGuid,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal ActualAmount,
    PriceSourceKind PriceSource,
    string? ItemNumber = null,
    OrderLineKind Kind = OrderLineKind.Sale,
    string? ReturnSourceKey = null,
    Guid? OriginalOrderGuid = null,
    Guid? OriginalOrderDetailGuid = null);

public sealed record PaymentSyncDto(
    Guid PaymentGuid,
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference,
    string? ReservationToken = null,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null);

public sealed record CardTransactionDto(
    string Processor,
    string? TxnRef,
    string? AuthCode,
    string? CardType,
    int? CardBin,
    string? MaskedCardNumber,
    string? MerchantId,
    string? ResponseCode,
    string? ResponseText,
    string? Stan,
    DateTimeOffset? BankDateTime,
    decimal Amount,
    string? ReceiptText,
    string? RefundReference = null);

public static class CardRefundReference
{
    private const string Prefix = "CARD_REFUND";
    private const string RefundPart = "refund=";
    private const string OriginalPart = "original=";

    public static string Format(string? refundReference, string originalReference)
    {
        if (string.IsNullOrWhiteSpace(originalReference))
        {
            throw new ArgumentException("Original card reference is required.", nameof(originalReference));
        }

        var refund = string.IsNullOrWhiteSpace(refundReference) ? string.Empty : refundReference.Trim();
        return $"{Prefix}|{RefundPart}{Encode(refund)}|{OriginalPart}{Encode(originalReference.Trim())}";
    }

    public static string? GetDisplayReference(string? reference)
    {
        return TryGetRefundReference(reference, out var refundReference) && !string.IsNullOrWhiteSpace(refundReference)
            ? refundReference
            : reference;
    }

    public static bool TryGetOriginalReference(string? reference, out string? originalReference)
    {
        originalReference = ReadPart(reference, OriginalPart);
        return !string.IsNullOrWhiteSpace(originalReference);
    }

    public static bool TryGetRefundReference(string? reference, out string? refundReference)
    {
        refundReference = ReadPart(reference, RefundPart);
        return refundReference is not null;
    }

    private static string? ReadPart(string? reference, string partName)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var trimmed = reference.Trim();
        if (!trimmed.StartsWith($"{Prefix}|", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var part in trimmed.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(partName, StringComparison.OrdinalIgnoreCase))
            {
                return Decode(part[partName.Length..]);
            }
        }

        return null;
    }

    private static string Encode(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string? Decode(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public sealed record OrderSyncResponse(
    Guid OrderGuid,
    bool Accepted,
    bool AlreadySynced,
    string? Message = null,
    HeldOrderDisposition HeldOrderDisposition = HeldOrderDisposition.None);

public sealed record OrderHistoryQueryRequest(
    string StoreCode,
    string? DeviceCode = null,
    DateTimeOffset? SoldFrom = null,
    DateTimeOffset? SoldTo = null,
    string? Keyword = null,
    int Take = 100,
    int Skip = 0);

public sealed record OrderHistoryQueryResponse(
    IReadOnlyList<OrderHistorySummaryDto> Orders);

public sealed record OrderHistorySummaryDto(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    int LineCount,
    string PaymentSummary,
    string StatusLabel);

public sealed record OrderHistoryDetailsDto(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    IReadOnlyList<OrderHistoryLineDto> Lines,
    IReadOnlyList<OrderHistoryPaymentDto> Payments);

public sealed record OrderHistoryLineDto(
    Guid OrderLineGuid,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal ActualAmount,
    OrderLineKind Kind = OrderLineKind.Sale,
    string? ReturnSourceKey = null,
    Guid? OriginalOrderGuid = null,
    Guid? OriginalOrderDetailGuid = null);

public sealed record OrderHistoryPaymentDto(
    Guid PaymentGuid,
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null);

public sealed record OrderReturnContextDto(
    OrderHistoryDetailsDto Order,
    IReadOnlyList<OrderReturnRecordDto> ReturnRecords,
    IReadOnlyList<OrderReturnLineCapacityDto>? LineCapacities = null,
    IReadOnlyList<OrderReturnPaymentCapacityDto>? PaymentCapacities = null);

public sealed record OrderReturnLineCapacityDto(
    Guid OriginalOrderLineGuid,
    decimal OriginalAmount,
    decimal ReturnedAmount,
    decimal RemainingAmount);

public sealed record OrderReturnPaymentCapacityDto(
    PaymentMethodKind Method,
    decimal OriginalAmount,
    decimal RefundedAmount,
    decimal RemainingAmount,
    string? Reference,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null,
    Guid? OriginalOrderGuid = null);

public sealed record OrderReturnRecordDto(
    Guid ReturnDetailGuid,
    Guid? ReturnOrderGuid,
    Guid? OriginalOrderGuid,
    Guid? OriginalOrderDetailGuid,
    string ProductCode,
    string? ReferenceCode,
    decimal ReturnQuantity,
    decimal ReturnAmount,
    string StaffCode,
    DateTimeOffset CreatedAt);

public sealed record OrderReturnRecordCreateRequest(
    Guid ReturnOrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    IReadOnlyList<OrderReturnRecordCreateLineDto> Lines);

public sealed record OrderReturnRecordCreateLineDto(
    Guid? OriginalOrderGuid,
    Guid? OriginalOrderDetailGuid,
    string ProductCode,
    string? ReferenceCode,
    decimal ReturnQuantity,
    decimal ReturnAmount);

public sealed record OrderReturnRecordCreateResponse(
    Guid ReturnOrderGuid,
    IReadOnlyList<OrderReturnRecordDto> ReturnRecords);
