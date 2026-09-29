namespace BlazorApp.Shared.DTOs;

/// <summary>批量货号销量分析的日期和门店范围。</summary>
public class BatchProductSalesScopeDto
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public List<string>? StoreCodes { get; set; }
}

public sealed class BatchProductSalesQueryRequestDto : BatchProductSalesScopeDto
{
    public List<string>? ItemNumbers { get; set; }
}

public sealed class BatchProductSalesDetailRequestDto : BatchProductSalesScopeDto
{
    public string? ProductCode { get; set; }
    /// <summary>默认保留旧客户端的折扣分类读取；false 时只读取已完成日销量统计。</summary>
    public bool IncludeDiscounts { get; set; } = true;
    /// <summary>摘要返回的可用日期版本；与 readyDates 一起传入可锁定明细读取边界。</summary>
    public string? CoverageVersion { get; set; }
    public List<string>? ReadyDates { get; set; }
}

public sealed class BatchProductSalesPendingDateDto
{
    /// <summary>ISO 8601 日期（yyyy-MM-dd）。</summary>
    public string Date { get; set; } = string.Empty;
    /// <summary>真实刷新状态，或 queued / active / queueFailed / missing。</summary>
    public string Reason { get; set; } = string.Empty;
}

public sealed class BatchProductSalesCoverageDto
{
    /// <summary>complete / partial / pending。</summary>
    public string Status { get; set; } = "pending";
    public List<string> ReadyDates { get; set; } = [];
    public List<BatchProductSalesPendingDateDto> PendingDates { get; set; } = [];
    /// <summary>仅由 readyDates 及其逐日统计版本生成，用于详情锁定。</summary>
    public string Version { get; set; } = string.Empty;
}

public sealed class BatchProductSalesStoreDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class BatchProductSalesOptionsDto
{
    public List<BatchProductSalesStoreDto> Stores { get; set; } = [];
    public int MaxItemNumbers { get; set; } = 3000;
    public int MaxDays { get; set; } = 366;
}

public sealed class BatchProductSalesMatchDto
{
    public string ItemNumber { get; set; } = string.Empty;
    /// <summary>matched / ambiguous / notFound</summary>
    public string Status { get; set; } = "notFound";
    public List<string> ProductCodes { get; set; } = [];
}

public class BatchProductSalesProductDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string ItemNumber { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string? EnglishName { get; set; }
    public string? Barcode { get; set; }
    public string? ImageUrl { get; set; }
}

public sealed class BatchProductSalesProductSummaryDto : BatchProductSalesProductDto
{
    /// <summary>无可用日期时为 null；0 表示全部可用日期的真实零销量。</summary>
    public decimal? Quantity { get; set; }
    public decimal? SalesAmount { get; set; }
}

/// <summary>
/// 所有数量均为有符号净销量；returnQuantity 仅用于展示退货绝对数量，不能再次扣减。
/// </summary>
public sealed class BatchProductSalesMetricsDto
{
    public decimal Quantity { get; set; }
    public decimal RegularQuantity { get; set; }
    public decimal DiscountQuantity { get; set; }
    public decimal UnknownQuantity { get; set; }
    public decimal ReturnQuantity { get; set; }
    public decimal SalesAmount { get; set; }
    /// <summary>complete / partial / unknown / pending（待计算，分类数量不得显示为真实值）</summary>
    public string DiscountStatus { get; set; } = "unknown";
    public decimal? OriginalPriceMin { get; set; }
    public decimal? OriginalPriceMax { get; set; }
    public decimal? DiscountPriceMin { get; set; }
    public decimal? DiscountPriceMax { get; set; }
}

public sealed class BatchProductSalesDailyDto
{
    public DateTime Date { get; set; }
    public BatchProductSalesMetricsDto Metrics { get; set; } = new();
}

public class BatchProductSalesBranchDto
{
    public string BranchCode { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public BatchProductSalesMetricsDto Metrics { get; set; } = new();
    public List<BatchProductSalesDailyDto> Daily { get; set; } = [];
}

public sealed class BatchProductSalesQueryResultDto : BatchProductSalesScopeDto
{
    public List<BatchProductSalesMatchDto> Matches { get; set; } = [];
    public List<BatchProductSalesProductSummaryDto> Products { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string? StatisticStatus { get; set; }
    public DateTime? StatisticUpdatedAt { get; set; }
    public BatchProductSalesCoverageDto Coverage { get; set; } = new();
    public BatchProductSalesOverviewDto Overview { get; set; } = new();
}

public sealed class BatchProductSalesDetailDto : BatchProductSalesScopeDto
{
    public string StatisticStatus { get; set; } = "Fresh";
    public DateTime? StatisticUpdatedAt { get; set; }
    public string DiscountStatisticStatus { get; set; } = "Fresh";
    public DateTime? DiscountUpdatedAt { get; set; }
    public BatchProductSalesCoverageDto Coverage { get; set; } = new();
    public List<string> ProductCodes { get; set; } = [];
    public BatchProductSalesProductDto Product { get; set; } = new();
    public BatchProductSalesMetricsDto Metrics { get; set; } = new();
    public List<BatchProductSalesDailyDto> Daily { get; set; } = [];
    public List<BatchProductSalesBranchDto> Branches { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class BatchProductSalesOverviewBranchDto : BatchProductSalesBranchDto
{
    public int ContributingProductCount { get; set; }
    public int SelectedProductCount { get; set; }
}

public sealed class BatchProductSalesOverviewDto
{
    public BatchProductSalesMetricsDto? Metrics { get; set; }
    public List<BatchProductSalesDailyDto> Daily { get; set; } = [];
    public List<BatchProductSalesOverviewBranchDto> Branches { get; set; } = [];
}

public class BatchProductSalesFollowupRequestDto : BatchProductSalesScopeDto
{
    public List<string>? ProductCodes { get; set; }
    public string? CoverageVersion { get; set; }
    public List<string>? ReadyDates { get; set; }
}

public sealed class BatchProductSalesBranchOverviewRequestDto : BatchProductSalesFollowupRequestDto
{
    public string? BranchCode { get; set; }
}

public sealed class BatchProductSalesBranchProductDto : BatchProductSalesProductDto
{
    public BatchProductSalesMetricsDto Metrics { get; set; } = new();
}

public sealed class BatchProductSalesBranchOverviewDto : BatchProductSalesScopeDto
{
    public List<string> ProductCodes { get; set; } = [];
    public BatchProductSalesCoverageDto Coverage { get; set; } = new();
    public BatchProductSalesBranchDto Branch { get; set; } = new();
    public List<BatchProductSalesBranchProductDto> Products { get; set; } = [];
}

public sealed class BatchProductSalesDiscountOverviewDto : BatchProductSalesScopeDto
{
    public List<string> ProductCodes { get; set; } = [];
    public BatchProductSalesCoverageDto Coverage { get; set; } = new();
    public BatchProductSalesOverviewDto Overview { get; set; } = new();
    public BatchProductSalesBranchDto? Branch { get; set; }
    public List<BatchProductSalesBranchProductDto> Products { get; set; } = [];
    public string DiscountStatisticStatus { get; set; } = "Pending";
    public DateTime? DiscountUpdatedAt { get; set; }
    public List<string> Warnings { get; set; } = [];
}
