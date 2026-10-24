using System.Text.Json.Serialization;
using BlazorApp.Shared.Helper;

namespace BlazorApp.Shared.DTOs;

/// <summary>移动端仓库商品进销查询的日期区间；天数含首尾。</summary>
public sealed class WarehouseProductInsightRangeDto
{
    public string StartDate { get; set; } = string.Empty;
    public string EndDate { get; set; } = string.Empty;
    public int DayCount { get; set; }
}

public sealed class WarehouseProductInsightProductDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public string? ProductImage { get; set; }
    public string? SupplierCode { get; set; }
    public string? SupplierName { get; set; }
    public string? LocationCode { get; set; }
    public int? StockQuantity { get; set; }
}

/// <summary>全仓库合计；进货只统计已实际到货的货柜，在途数量单列。</summary>
public sealed class WarehouseProductInsightTotalsDto
{
    public decimal InboundQuantity { get; set; }
    public int ContainerCount { get; set; }
    public decimal InTransitQuantity { get; set; }
    public int InTransitContainerCount { get; set; }
    public decimal OrderedQuantity { get; set; }
    public int OrderedStoreCount { get; set; }
    public int OrderDocumentCount { get; set; }
    public decimal ShippedQuantity { get; set; }
    public int ShippedStoreCount { get; set; }
    public int ShipmentDocumentCount { get; set; }
    public decimal PendingQuantity { get; set; }
    public int PendingStoreCount { get; set; }
    public int SalesQuantity { get; set; }
    public decimal SalesAmount { get; set; }
    public int SalesStoreCount { get; set; }
}

public sealed class WarehouseProductInsightBranchDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public decimal OrderedQuantity { get; set; }
    public decimal ShippedQuantity { get; set; }
    public decimal PendingQuantity { get; set; }
    public int SalesQuantity { get; set; }
    public decimal SalesAmount { get; set; }
    /// <summary>售罄率 = 销售数量 / 发货数量；发货为 0 时不计算。</summary>
    public decimal? SellThroughRate { get; set; }
}

public sealed class WarehouseProductInsightContainerDto
{
    [JsonConverter(typeof(ContainerNumberJsonConverter))]
    public string ContainerNumber { get; set; } = string.Empty;
    public DateTime ArrivalDate { get; set; }
    /// <summary>true 表示该日期是预计到岸日，货柜尚未实际到货，不计入进货合计。</summary>
    public bool IsEstimatedArrival { get; set; }
    public decimal Quantity { get; set; }
    public decimal? Pieces { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class WarehouseProductInsightMovementDto
{
    public string DocumentNo { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public decimal Quantity { get; set; }
}

public sealed class WarehouseProductInsightDailySalesDto
{
    public DateTime Date { get; set; }
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>GET /api/react/v1/warehouse-product-insights 的数据载荷。</summary>
public sealed class WarehouseProductInsightDto
{
    public WarehouseProductInsightRangeDto Range { get; set; } = new();
    /// <summary>货柜进货的独立区间：固定最近一年，不跟随 Range。</summary>
    public WarehouseProductInsightRangeDto InboundRange { get; set; } = new();
    public DateTime GeneratedAt { get; set; }
    public DateTime? SalesStatisticLastUpdatedAt { get; set; }
    /// <summary>all-stores 表示覆盖全部分店，authorized-stores 表示仅当前用户授权分店。</summary>
    public string Scope { get; set; } = "all-stores";
    public WarehouseProductInsightProductDto Product { get; set; } = new();
    public WarehouseProductInsightTotalsDto Totals { get; set; } = new();
    public List<WarehouseProductInsightBranchDto> Branches { get; set; } = [];
    public List<WarehouseProductInsightContainerDto> Containers { get; set; } = [];
    public List<WarehouseProductInsightMovementDto> Orders { get; set; } = [];
    public List<WarehouseProductInsightMovementDto> Shipments { get; set; } = [];
    public List<WarehouseProductInsightDailySalesDto> DailySales { get; set; } = [];
}

/// <summary>仓库商品进销查询入参；日期为业务日，含首尾。</summary>
public sealed class WarehouseProductInsightQuery
{
    public string ProductCode { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool ForceRefresh { get; set; }
}
