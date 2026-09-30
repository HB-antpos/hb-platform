namespace BlazorApp.Shared.DTOs;

public sealed class ContainerNewProductsResponseDto
{
    public string StoreCode { get; init; } = string.Empty;
    public string StateCode { get; init; } = string.Empty;
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
    public string ContainerCode { get; init; } = string.Empty;
    public string? ContainerNumber { get; init; }
    public DateOnly EstimatedStoreArrivalDate { get; init; }
    public string Basis { get; init; } = string.Empty;
}
