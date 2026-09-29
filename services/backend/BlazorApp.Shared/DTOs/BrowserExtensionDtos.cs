using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs;

public static class BrowserExtensionMatchStatuses
{
    public const string Matched = "matched";
    public const string NoPurchase = "no-purchase";
    public const string Unmatched = "unmatched";
}

public static class BrowserExtensionSalesRankBands
{
    public const string Top10 = "top-10";
    public const string Top20 = "top-20";
    public const string Top30 = "top-30";
}

public sealed class BrowserExtensionReleaseNotesDto
{
    public string Zh { get; set; } = string.Empty;
    public string En { get; set; } = string.Empty;
}

public sealed class BrowserExtensionReleaseDto
{
    public string LatestVersion { get; set; } = string.Empty;
    public string MinimumVersion { get; set; } = string.Empty;
    public string ChromeStoreUrl { get; set; } = string.Empty;
    public string EdgeStoreUrl { get; set; } = string.Empty;
    public string SafariStoreUrl { get; set; } = string.Empty;
    public BrowserExtensionReleaseNotesDto ReleaseNotes { get; set; } = new();
}

public sealed class BrowserExtensionItemNumberRuleDto
{
    public string Source { get; set; } = "attribute";
    public string? Selector { get; set; }
    public string? Attribute { get; set; }
    public List<string> Transforms { get; set; } = new();
}

public sealed class BrowserExtensionSupplierProfileDto
{
    public string SupplierCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public List<string> Origins { get; set; } = new();
    public List<string> ListPagePatterns { get; set; } = new();
    public string CardSelector { get; set; } = string.Empty;
    public BrowserExtensionItemNumberRuleDto ItemNumber { get; set; } = new();
    public string MountSelector { get; set; } = string.Empty;
    public string MountPosition { get; set; } = "afterend";

    /// <summary>
    /// 供应商分类采集配置；仅 1.5.0 及以上客户端下发，缺省表示该供应商不采集分类。
    /// </summary>
    public BrowserExtensionSupplierCategoryProfileDto? Category { get; set; }
}

/// <summary>
/// 下发给扩展的分类采集声明式配置：只含选择器、路径通配与数值上限，扩展不执行任何远程代码。
/// </summary>
public sealed class BrowserExtensionSupplierCategoryProfileDto
{
    public bool Enabled { get; set; }
    public bool PassiveEnabled { get; set; } = true;
    public bool CrawlEnabled { get; set; } = true;
    public List<string> CategoryPagePatterns { get; set; } = new();
    public List<string> CategoryExcludePatterns { get; set; } = new();
    public string? BreadcrumbSelector { get; set; }
    public int BreadcrumbSkip { get; set; } = 1;
    public string? TitleSelector { get; set; }
    public string KeySource { get; set; } = "pathname";
    public List<string> KeyQueryParams { get; set; } = new();
    public string? NavRootUrl { get; set; }
    public string? NavSelector { get; set; }
    public string? SubcategoryLinkSelector { get; set; }
    public string? PaginationNextSelector { get; set; }
    public int MaxPages { get; set; } = 20;
    public int MaxDepth { get; set; } = 4;
    public int MaxCategories { get; set; } = 400;
    public int CrawlDelayMs { get; set; } = 1500;
    public List<string> PromotionalPatterns { get; set; } = new();
}

public sealed class BrowserExtensionSupplierProfilesDto
{
    public string ConfigVersion { get; set; } = string.Empty;
    public List<BrowserExtensionSupplierProfileDto> Profiles { get; set; } = new();
}

public sealed class BrowserExtensionProductSummaryBatchRequestDto
{
    [Required]
    [StringLength(50)]
    public string StoreCode { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string SupplierCode { get; set; } = string.Empty;

    [Required]
    [MinLength(1)]
    [MaxLength(100)]
    public List<string> ItemNumbers { get; set; } = new();

    public int SalesRankingDays { get; set; } = 60;
}

public sealed class BrowserExtensionProductSummaryDto
{
    public string ItemNumber { get; set; } = string.Empty;
    public string MatchStatus { get; set; } = BrowserExtensionMatchStatuses.Unmatched;
    public string? ProductCode { get; set; }
    public string? ProductName { get; set; }
    public DateOnly? LatestPurchaseDate { get; set; }
    public decimal? LatestPurchaseQuantity { get; set; }
    public decimal SalesSinceLatestPurchase { get; set; }
    public DateTime? SalesStatisticLastUpdate { get; set; }
    public string? SalesRankBand { get; set; }
}

public sealed class BrowserExtensionProductSummaryBatchDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string SupplierCode { get; set; } = string.Empty;
    public DateOnly EndDate { get; set; }
    public bool SalesRankingAvailable { get; set; }
    public int SalesRankingDays { get; set; } = 60;
    public DateOnly SalesRankingStartDate { get; set; }
    public DateOnly SalesRankingEndDate { get; set; }
    public int SalesRankingEnabledStoreCount { get; set; }
    public int SalesRankingTotalProductCount { get; set; }
    public DateTime? SalesRankingStatisticLastUpdate { get; set; }
    public List<BrowserExtensionProductSummaryDto> Items { get; set; } = new();
}

public sealed class BrowserExtensionPurchaseCyclesRequestDto
{
    [Required]
    [StringLength(50)]
    public string StoreCode { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string SupplierCode { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string ItemNumber { get; set; } = string.Empty;
}

public sealed class BrowserExtensionPurchaseCycleDto
{
    public DateOnly PurchaseDate { get; set; }
    public List<string> InvoiceNumbers { get; set; } = new();
    public decimal PurchaseQuantity { get; set; }
    public decimal? AveragePurchasePrice { get; set; }
    public DateOnly SalesStartDate { get; set; }
    public DateOnly SalesEndDate { get; set; }
    public decimal SalesQuantity { get; set; }
    public decimal? AverageSalePrice { get; set; }
}

public sealed class BrowserExtensionPurchaseCyclesDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string SupplierCode { get; set; } = string.Empty;
    public string ItemNumber { get; set; } = string.Empty;
    public string MatchStatus { get; set; } = BrowserExtensionMatchStatuses.Unmatched;
    public string? ProductCode { get; set; }
    public string? ProductName { get; set; }
    public DateOnly EndDate { get; set; }
    public DateTime? SalesStatisticLastUpdate { get; set; }
    public DateOnly? LatestPurchaseDate { get; set; }
    public decimal? LatestPurchaseQuantity { get; set; }
    public decimal SalesSinceLatestPurchase { get; set; }
    public List<BrowserExtensionPurchaseCycleDto> Cycles { get; set; } = new();
}

public sealed class BrowserExtensionStoreOptionDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
}

public sealed class BrowserExtensionStoreOptionsDto
{
    public List<BrowserExtensionStoreOptionDto> Stores { get; set; } = new();
}

public sealed class BrowserExtensionSupplierTopSalesRequestDto
{
    [Required]
    [StringLength(50)]
    public string SupplierCode { get; set; } = string.Empty;

    [Range(1, 90)]
    public int Days { get; set; } = 60;

    public int? TopPercent { get; set; }

    public int? Page { get; set; }

    public int? PageSize { get; set; }
}

public sealed class BrowserExtensionSupplierTopSalesItemDto
{
    public int Rank { get; set; }
    public string ItemNumber { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public decimal SalesQuantity { get; set; }
    public decimal? AverageSellingPrice { get; set; }
    public string? SalesRankBand { get; set; }
}

public sealed class BrowserExtensionSupplierTopSalesDto
{
    public string SupplierCode { get; set; } = string.Empty;
    public string SnapshotVersion { get; set; } = string.Empty;
    public int Days { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int EnabledStoreCount { get; set; }
    public int TotalProductCount { get; set; }
    public int TopPercent { get; set; } = 10;
    public int TotalRankedCount { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
    public int? TotalPages { get; set; }
    public DateTime? SalesStatisticLastUpdate { get; set; }
    public List<BrowserExtensionSupplierTopSalesItemDto> Items { get; set; } = new();
}

public sealed class BrowserExtensionSupplierProductStoreSalesRequestDto
{
    [Required]
    [StringLength(50)]
    public string SupplierCode { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string ProductCode { get; set; } = string.Empty;

    [Range(1, 90)]
    public int Days { get; set; } = 60;

    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public decimal ExpectedTotalSalesQuantity { get; set; }

    [Required]
    [StringLength(200)]
    public string SnapshotVersion { get; set; } = string.Empty;
}

public sealed class BrowserExtensionSupplierProductStoreSalesItemDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public decimal SalesQuantity { get; set; }
}

public sealed class BrowserExtensionSupplierProductStoreSalesDto
{
    public string SupplierCode { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string SnapshotVersion { get; set; } = string.Empty;
    public int Days { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int EnabledStoreCount { get; set; }
    public decimal TotalSalesQuantity { get; set; }
    public DateTime? SalesStatisticLastUpdate { get; set; }
    public List<BrowserExtensionSupplierProductStoreSalesItemDto> Stores { get; set; } = new();
}

public static class BrowserExtensionCategoryCaptureModes
{
    public const string Passive = "passive";
    public const string Crawl = "crawl";
}

/// <summary>
/// 分类路径中的一级节点；Key 是站点稳定标识（归一化后的分类页路径），服务端会再归一化一次。
/// </summary>
public sealed class BrowserExtensionCategoryPathNodeDto
{
    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(400)]
    public string Key { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Url { get; set; }
}

public sealed class BrowserExtensionCategoryCaptureRequestDto
{
    [Required]
    [StringLength(50)]
    public string SupplierCode { get; set; } = string.Empty;

    [Required]
    [StringLength(1000)]
    public string PageUrl { get; set; } = string.Empty;

    /// <summary>从根到叶的分类路径，不含站点首页。</summary>
    [Required]
    [MinLength(1)]
    [MaxLength(8)]
    public List<BrowserExtensionCategoryPathNodeDto> CategoryPath { get; set; } = new();

    [Required]
    [MinLength(1)]
    [MaxLength(100)]
    public List<string> ItemNumbers { get; set; } = new();

    public DateTimeOffset? CapturedAt { get; set; }

    [Required]
    [StringLength(16)]
    public string Mode { get; set; } = BrowserExtensionCategoryCaptureModes.Passive;

    public int? PageNumber { get; set; }
}

public sealed class BrowserExtensionCategoryCaptureResultDto
{
    public string CategoryGuid { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public int Depth { get; set; }
    public bool IsPromotional { get; set; }
    public int CategoriesCreated { get; set; }
    public int MatchedProducts { get; set; }
    public int AssignedProducts { get; set; }
    public int UnchangedProducts { get; set; }
    public int SkippedManual { get; set; }
    public int UnmatchedItemNumberCount { get; set; }
    public List<string> UnmatchedSamples { get; set; } = new();
}

public sealed class BrowserExtensionCategoryTreeNodeDto
{
    [Required]
    [StringLength(400)]
    public string Key { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(400)]
    public string? ParentKey { get; set; }

    [StringLength(1000)]
    public string? Url { get; set; }

    public int? SortOrder { get; set; }
}

public sealed class BrowserExtensionCategoryTreeSnapshotRequestDto
{
    [Required]
    [StringLength(50)]
    public string SupplierCode { get; set; } = string.Empty;

    [Required]
    [StringLength(1000)]
    public string SourceUrl { get; set; } = string.Empty;

    [Required]
    [MinLength(1)]
    [MaxLength(2000)]
    public List<BrowserExtensionCategoryTreeNodeDto> Nodes { get; set; } = new();
}

public sealed class BrowserExtensionCategoryTreeSnapshotResultDto
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int OrphanCount { get; set; }
    public int PromotionalCount { get; set; }
}
