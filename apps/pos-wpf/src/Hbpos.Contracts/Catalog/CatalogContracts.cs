namespace Hbpos.Contracts.Catalog;

public enum PriceSourceKind
{
    ProductBase = 0,
    StoreRetailPrice = 1,
    ProductSetCode = 2,
    StoreMultiCodeProduct = 3,
    StoreClearancePrice = 4
}

public sealed record StoreDto(
    string StoreCode,
    string StoreName,
    bool IsActive);

public sealed record SellableItemDto(
    string StoreCode,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    string? Barcode,
    decimal RetailPrice,
    PriceSourceKind PriceSource,
    string PriceSourceLabel,
    decimal QuantityFactor,
    DateTimeOffset? UpdatedAt,
    string? ProductImage = null,
    decimal? DiscountRate = null,
    bool IsSpecialProduct = false);

public sealed record SellableItemsResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<SellableItemDto> Items);

/// <summary>
/// Local sync key: StoreCode + LookupCodeNormalized.
/// LookupCode is the actual sale/search code: barcode, item number, multi code, set code, or clearance code.
/// Suggested backend index only: StoreCode + LookupCodeNormalized, no automatic DDL.
/// </summary>
public sealed record CatalogLookupItemDto(
    string StoreCode,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string LookupCodeNormalized,
    string? ItemNumber,
    string? Barcode,
    decimal RetailPrice,
    PriceSourceKind PriceSource,
    string PriceSourceLabel,
    decimal QuantityFactor,
    DateTimeOffset? UpdatedAt,
    string? RowVersion,
    string? ProductImage = null,
    decimal? DiscountRate = null,
    bool IsSpecialProduct = false);

public sealed record CatalogLocalLookupVersionDto(
    string StoreCode,
    string LookupCode,
    string LookupCodeNormalized,
    DateTimeOffset? UpdatedAt,
    string? RowVersion);

public sealed record CatalogCompareRequest(
    string StoreCode,
    IReadOnlyList<CatalogLocalLookupVersionDto> LocalLookups);

/// <summary>
/// Exact delete tombstone for StoreCode + LookupCode/LookupCodeNormalized; never implies store/table clearing.
/// </summary>
public sealed record DeletedLookupDto(
    string StoreCode,
    string LookupCode,
    string LookupCodeNormalized,
    DateTimeOffset? DeletedAt);

public sealed record CatalogCompareResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<CatalogLookupItemDto> UpsertedLookups,
    IReadOnlyList<DeletedLookupDto> DeletedLookups,
    string? NextCursor,
    bool HasMore);

public sealed record CatalogSyncPageResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    string? Cursor,
    IReadOnlyList<CatalogLookupItemDto> Items,
    IReadOnlyList<DeletedLookupDto> DeletedLookups,
    string? NextCursor,
    bool HasMore,
    int TotalCount,
    string CatalogVersion = "",
    string PageChecksum = "",
    string? DownloadLeaseId = null);

/// <summary>
/// 同一门店目录的同步决策：首次、本地快照过期时全量；版本相同无需目录下载；
/// 仅在基准版本仍可读取时才允许使用增量。
/// </summary>
public static class CatalogSyncModes
{
    public const string NoChange = "noChange";
    public const string Delta = "delta";
    public const string Full = "full";
}

public sealed record CatalogSyncPlanResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    string Mode,
    string? BaseCatalogVersion,
    string TargetCatalogVersion,
    int TargetTotal,
    string? DownloadLeaseId = null,
    int? DeltaOperationCount = null);

/// <summary>
/// 一个不可变基准版本到目标版本的增量页。Items 是 upsert，DeletedLookups 是精确删除；
/// 两类操作按 LookupCodeNormalized 合并排序并共同受 cursor/checksum 保护。
/// </summary>
public sealed record CatalogDeltaPageResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    string BaseCatalogVersion,
    string TargetCatalogVersion,
    string? Cursor,
    IReadOnlyList<CatalogLookupItemDto> Items,
    IReadOnlyList<DeletedLookupDto> DeletedLookups,
    string? NextCursor,
    bool HasMore,
    int TargetTotal,
    string PageChecksum,
    string? DownloadLeaseId = null);

public sealed record CatalogPromotionProductDto(
    string ProductCode,
    int UnitWeight);

public sealed record CatalogPromotionRuleDto(
    string PromotionId,
    string Name,
    bool IsExclusive,
    int Priority,
    int ApplyQuantity,
    decimal FixedPrice,
    int? MaxApplicationsPerOrder,
    DateTimeOffset EffectiveStart,
    DateTimeOffset EffectiveEnd,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<CatalogPromotionProductDto> Products);

public sealed record CatalogPromotionsResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<CatalogPromotionRuleDto> Promotions);

public sealed record CatalogLookupResponse(
    string StoreCode,
    string LookupCode,
    string LookupCodeNormalized,
    bool Found,
    CatalogLookupItemDto? Item);

public sealed record CatalogSpecialProductMarkRequest(
    string StoreCode,
    string ProductCode,
    bool IsSpecialProduct);

public sealed record CatalogSpecialProductMarkResponse(
    string StoreCode,
    string ProductCode,
    bool IsSpecialProduct,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<CatalogLookupItemDto> Items);

public sealed record CatalogSpecialProductsPageResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    string? Cursor,
    IReadOnlyList<CatalogLookupItemDto> Items,
    string? NextCursor,
    bool HasMore,
    int TotalCount);

/// <summary>
/// 同一查询码对应多个不同商品时的全部候选：每个商品一条，按目录决胜顺序排列，首条即目录中的胜出项。
/// Available=false 表示服务端当前目录尚未算出冲突（例如刚从旧快照恢复），客户端应保留本地已有数据。
/// </summary>
public sealed record CatalogCodeConflictsResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    bool Available,
    IReadOnlyList<CatalogLookupItemDto> Items);
