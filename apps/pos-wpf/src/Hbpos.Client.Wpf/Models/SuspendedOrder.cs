using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Models;

public enum SuspendedOrderStatus
{
    Pending = 0,
    Recalled = 1,
    Canceled = 2
}

public sealed record SuspendedOrder(
    Guid SuspendedOrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    DateTimeOffset SuspendedAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    SuspendedOrderStatus Status,
    IReadOnlyList<SuspendedOrderLine> Lines)
{
    public IReadOnlyList<OrderReturnPaymentCapacityDto> ReturnPaymentCapacities { get; init; } = [];

    /// <summary>
    /// 挂单时深拷贝冻结的自动促销规则（目录刷新/重启后仍按此精确恢复金额与促销 id）。
    /// 旧挂单/无促销挂单允许 null。
    /// </summary>
    public IReadOnlyList<CatalogPromotionRuleDto>? FrozenPromotionRules { get; init; }
}

public sealed record SuspendedOrderLine(
    Guid SuspendedOrderLineGuid,
    Guid SuspendedOrderGuid,
    string StoreCode,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    string? ProductImage,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal? DiscountPercent,
    decimal ActualAmount,
    PriceSourceKind PriceSource,
    string PriceSourceLabel,
    PosCartLineDiscountSource DiscountSource = PosCartLineDiscountSource.None)
{
    public CartLineKind Kind { get; init; } = CartLineKind.Sale;

    public string ReturnSourceKey { get; init; } = string.Empty;

    /// <summary>base price provenance：手工改价/目录价格，挂单往返必须保留。</summary>
    public bool IsManualPrice { get; init; }

    /// <summary>目录折扣基线，以万分比保存；手工折扣覆盖时也必须随挂单保留。</summary>
    public int CatalogDiscountBasisPoints { get; init; }

    // 自动满减折扣标记必须随挂单保存，取单后才会继续按规则重算。
    public bool IsAutomaticPromotionDiscount => DiscountSource == PosCartLineDiscountSource.Promotion;

    public Guid? OriginalOrderGuid { get; init; }

    public Guid? OriginalOrderDetailGuid { get; init; }

    public string? ReturnReason { get; init; }
}

public sealed record SuspendedOrderSummary(
    Guid SuspendedOrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset SuspendedAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    int LineCount,
    SuspendedOrderStatus Status)
{
    public string ShortOrderId => ReceiptOrderShortCode.Create(SuspendedOrderGuid);

    public string SoldAtDisplay => SuspendedAt.ToLocalTime().ToString("MMM dd, yyyy HH:mm");

    public string StatusLabel => Status.ToString();
}
