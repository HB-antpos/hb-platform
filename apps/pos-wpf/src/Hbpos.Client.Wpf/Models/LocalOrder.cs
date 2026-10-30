using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Models;

public sealed record LocalOrder(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    IReadOnlyList<LocalOrderLine> Lines,
    IReadOnlyList<LocalPayment> Payments,
    decimal? TenderedAmount = null,
    decimal? ChangeAmount = null,
    HeldOrderSourceDto? HeldOrderSource = null);

public sealed record LocalOrderLine(
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
    PriceSourceKind PriceSource,
    OrderLineKind Kind = OrderLineKind.Sale,
    string? ReturnSourceKey = null,
    Guid? OriginalOrderGuid = null,
    Guid? OriginalOrderDetailGuid = null);

public sealed record LocalOrderHistoryQuery(
    DateTimeOffset? SoldFrom = null,
    DateTimeOffset? SoldTo = null,
    string? DeviceCode = null,
    string? Keyword = null,
    int Skip = 0);

public sealed record LocalPayment(
    Guid PaymentGuid,
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null,
    string? IdempotencyKey = null);

public sealed record PaymentTender(
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference = null,
    string? DisplayLabel = null,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null,
    string? IdempotencyKey = null)
{
    public string? DisplayReference => PaymentReferenceDisplay.Format(Method, Reference);
}

public sealed record CashCheckoutResult(LocalOrder Order, decimal TenderedAmount, decimal ChangeAmount);

public sealed record PaymentCheckoutResult(LocalOrder Order, decimal TenderedAmount, decimal ChangeAmount);
