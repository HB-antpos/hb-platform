using BlazorApp.Shared.Helper;
using System.Text.Json.Serialization;

namespace BlazorApp.Shared.DTOs;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContainerArrivalDateBasis
{
    Actual,
    Expected,
}

public class ContainerAllocationSalesQueryRequest
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? Search { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string? SortBy { get; set; } = "productCode";
    public string? SortDirection { get; set; } = "asc";
}

public class ContainerAllocationSalesBranchesQueryRequest
{
    public string ProductCode { get; set; } = string.Empty;
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
}

public class ContainerAllocationSalesReportResponse
{
    public string ContainerGuid { get; set; } = string.Empty;
    [JsonConverter(typeof(ContainerNumberJsonConverter))]
    public string? ContainerNumber { get; set; }
    public DateTime? ArrivalDate { get; set; }
    public ContainerArrivalDateBasis? ArrivalDateBasis { get; set; }
    public bool IsEstimatedArrivalDate { get; set; }
    public bool CanQuery { get; set; }
    public string? QueryMessage { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int DayCount { get; set; }
    public int StartWeek { get; set; }
    public int EndWeek { get; set; }
    public string RangeLabel { get; set; } = string.Empty;
    public List<ContainerAllocationSalesProductDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public ContainerAllocationSalesTotalsDto Totals { get; set; } = new();
    public string StatisticStatus { get; set; } = string.Empty;
    public string? StatisticMessage { get; set; }
}

public class ContainerAllocationSalesProductDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string? ProductImage { get; set; }
    public string? ItemNumber { get; set; }
    public string? ProductName { get; set; }
    public decimal LoadingQuantity { get; set; }
    public decimal AllocationQuantity { get; set; }
    public decimal AllocationImportAmount { get; set; }
    public decimal? SalesQuantity { get; set; }
    public decimal? SalesAmount { get; set; }
    public decimal? AverageSalesPrice { get; set; }
    public decimal? GrossProfit { get; set; }
    public decimal? GrossMarginRate { get; set; }
    public bool? IsGrossMarginComplete { get; set; }
}

public class ContainerAllocationSalesTotalsDto
{
    public int ProductCount { get; set; }
    public decimal LoadingQuantity { get; set; }
    public decimal AllocationQuantity { get; set; }
    public decimal AllocationImportAmount { get; set; }
    public decimal? SalesQuantity { get; set; }
    public decimal? SalesAmount { get; set; }
    public decimal? AverageSalesPrice { get; set; }
    public decimal? GrossProfit { get; set; }
    public decimal? GrossMarginRate { get; set; }
    public bool? IsGrossMarginComplete { get; set; }
}

public class ContainerAllocationSalesBranchesResponse
{
    public string ContainerGuid { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string StatisticStatus { get; set; } = string.Empty;
    public string? StatisticMessage { get; set; }
    public List<ContainerAllocationSalesBranchDto> Items { get; set; } = new();
}

public class ContainerAllocationSalesBranchDto
{
    public string BranchCode { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public decimal AllocationQuantity { get; set; }
    public decimal AllocationImportAmount { get; set; }
    public decimal? SalesQuantity { get; set; }
    public decimal? SalesAmount { get; set; }
    public decimal? AverageSalesPrice { get; set; }
    public decimal? GrossProfit { get; set; }
    public decimal? GrossMarginRate { get; set; }
    public bool? IsGrossMarginComplete { get; set; }
}
