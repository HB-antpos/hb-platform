namespace BlazorApp.Api.Features.StoreOrders.ProductHistory.Infrastructure;

internal sealed class ProductHistoryDynamicHistoryRow
{
    public string? ProductCode { get; set; }
    public string? OrderGUID { get; set; }
    public DateTime? OrderDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? AllocQuantity { get; set; }
}

internal sealed class ProductHistoryOrderHistoryRow
{
    public string? OrderGUID { get; set; }
    public string? OrderNo { get; set; }
    public DateTime? OrderDate { get; set; }
    public DateTime? OutboundDate { get; set; }
    public int? FlowStatus { get; set; }
    public DateTime CreatedAt { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? AllocQuantity { get; set; }
}

internal sealed class ProductHistoryActivityHistoryRow
{
    public string RecordType { get; set; } = "order";
    public DateTime? RecordDate { get; set; }
    public DateTime? SortDate { get; set; }
    public int SortType { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string OrderGUID { get; set; } = string.Empty;
    public string? OrderNo { get; set; }
    public DateTime? OrderDate { get; set; }
    public DateTime? OutboundDate { get; set; }
    public int? FlowStatus { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? AllocQuantity { get; set; }
    public int? SalesQuantity { get; set; }
    public decimal? AveragePrice { get; set; }
    public DateTime? PeriodStartDate { get; set; }
    public DateTime? PeriodEndDate { get; set; }
}

// 单独的销售投影避免 SQL Server 把订单侧整列 NULL 推断为字符串。
internal sealed class ProductHistorySalesActivityHistoryRow
{
    public string RecordType { get; set; } = "sales";
    public DateTime? RecordDate { get; set; }
    public DateTime? SortDate { get; set; }
    public int SortType { get; set; }
    public int? SalesQuantity { get; set; }
    public decimal? AveragePrice { get; set; }
    public DateTime? PeriodStartDate { get; set; }
    public DateTime? PeriodEndDate { get; set; }
}

internal sealed class ProductHistorySalesInterval
{
    public string AnchorOrderGuid { get; init; } = string.Empty;
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
}

internal sealed class ProductHistoryLastArrivalRow
{
    public string? ProductCode { get; set; }
    public DateTime? OutboundDate { get; set; }
}

internal sealed class ProductHistoryProductSalesStatisticRow
{
    public string ProductCode { get; set; } = string.Empty;
    public int TotalQuantity { get; set; }
}

internal sealed class ProductHistorySalesCutoffGroup
{
    public DateTime ArrivalDate { get; init; }
    public List<string> ProductCodes { get; init; } = new();
}

internal sealed class ProductHistorySalesQuantityMapResult
{
    public Dictionary<string, int> SalesQuantityMap { get; } = new(
        StringComparer.OrdinalIgnoreCase
    );
    public int ArrivalRows { get; init; }
    public int CutoffGroupCount { get; init; }
    public int StatsQueryCount { get; init; }
}

internal sealed class ProductHistoryDailySalesStatisticRow
{
    public DateTime Date { get; set; }
    public int TotalQuantity { get; set; }
    public decimal TotalAmount { get; set; }
}

internal sealed class ProductHistorySalesStoreRow
{
    public bool IsActive { get; set; }
    public string? StoreCode { get; set; }
    public string? StoreName { get; set; }
    public string? Address { get; set; }
    public string? TimeZoneId { get; set; }
}

internal sealed class ProductHistorySalesContext
{
    public string StoreCode { get; init; } = string.Empty;
    public DateTime EndDate { get; init; }
}

internal sealed class ProductHistoryDynamicCartRow
{
    public string? ProductCode { get; set; }
    public decimal? CartQuantity { get; set; }
}
