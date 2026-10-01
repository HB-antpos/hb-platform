namespace BlazorApp.Shared.DTOs;

public sealed class ContainerNewProductsResponseDto
{
    public string StoreCode { get; init; } = string.Empty;
    public string StateCode { get; init; } = string.Empty;
    /// <summary>门店所在州的本地今天；移动端据此把到店日拆成「过去 1 周 / 未来 2 周」两组，避免设备时区不同导致分界漂移。</summary>
    public DateOnly LocalToday { get; init; }
    public IReadOnlyList<ContainerNewProductItemDto> Items { get; init; } = [];
}

public sealed class ContainerNewProductItemDto
{
    public string ProductCode { get; init; } = string.Empty;
    /// <summary>HB 货号（DomesticProduct.HBProductNo），同一到店日内按它排序；ProductCode 是 UUID 主键，不适合给人看。</summary>
    public string? HbProductNo { get; init; }
    /// <summary>装柜数量（最小单位，ContainerDetail.LoadingQuantity）；同一货柜同一商品多行时合计。</summary>
    public decimal? Quantity { get; init; }
    public string? ImageUrl { get; init; }
    /// <summary>商品条码：优先国内商品资料，其次商品主档；都没有时为 null，前端不画条码。</summary>
    public string? Barcode { get; init; }
    /// <summary>
    /// 零售价：本门店启用中的分店价 → 商品主档零售价 → 货柜明细零售价（未建档新品的计划价）；
    /// 都没有或不大于 0 时为 null，前端不显示。
    /// </summary>
    public decimal? RetailPrice { get; init; }
    public string ContainerCode { get; init; } = string.Empty;
    public string? ContainerNumber { get; init; }
    public DateOnly EstimatedStoreArrivalDate { get; init; }
    public string Basis { get; init; } = string.Empty;
}
