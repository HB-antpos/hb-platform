using System.Text.Json.Serialization;
using BlazorApp.Shared.Helper;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 仓库商品流转分析筛选条件，只承载主档筛选，不再包含任何日期范围。
    /// </summary>
    public class WarehouseProductFlowAnalysisFilterDto
    {
        /// <summary>货号/条码/中英文名称关键字。</summary>
        public string? Keyword { get; set; }

        /// <summary>目标仓库分类 GUID；父分类会包含子分类。</summary>
        public List<string>? WarehouseCategoryGuids { get; set; }

        /// <summary>国内供应商代码。</summary>
        public List<string>? SupplierCodes { get; set; }

        /// <summary>货柜/订单等单据关键字。</summary>
        public string? DocumentKeyword { get; set; }
    }

    /// <summary>
    /// 跨分页商品选择语义。
    /// </summary>
    public class WarehouseProductFlowAnalysisSelectionDto
    {
        public string Mode { get; set; } = "allFiltered";

        public List<string>? IncludedProductCodes { get; set; }

        public List<string>? ExcludedProductCodes { get; set; }
    }

    /// <summary>
    /// 仓库商品流转分析统一请求体。
    /// </summary>
    public class WarehouseProductFlowAnalysisRequest
    {
        public WarehouseProductFlowAnalysisFilterDto Filter { get; set; } = new();

        public WarehouseProductFlowPeriodsDto Periods { get; set; } = new();

        public WarehouseProductFlowAnalysisSelectionDto Selection { get; set; } = new();

        /// <summary>product-daily / containers / orders / shipments / branches / branch-daily 使用的当前商品代码。</summary>
        public string? CurrentProductCode { get; set; }

        /// <summary>branch-daily 使用的分店代码。</summary>
        public string? BranchCode { get; set; }

        public int PageNumber { get; set; } = 1;

        public int PageSize { get; set; } = 50;

        public string? SortBy { get; set; }

        public string? SortDirection { get; set; }

        public bool ForceRefresh { get; set; }
    }

    /// <summary>options 接口返回的仓库分类与国内供应商选项。</summary>
    public class WarehouseProductFlowAnalysisOptionsDto
    {
        public List<WarehouseCategoryOptionDto> WarehouseCategories { get; set; } = new();

        public List<WarehouseProductFlowSupplierOptionDto> DomesticSuppliers { get; set; } = new();
    }

    public class WarehouseCategoryOptionDto
    {
        public string CategoryGuid { get; set; } = string.Empty;

        public string CategoryName { get; set; } = string.Empty;

        public string? ParentGuid { get; set; }
    }

    public class WarehouseProductFlowSupplierOptionDto
    {
        public string Code { get; set; } = string.Empty;

        public string? Name { get; set; }
    }

    /// <summary>分页数据。</summary>
    public class WarehouseProductFlowAnalysisPagedDto<T>
    {
        public List<T> Items { get; set; } = new();

        public int Total { get; set; }

        public int PageNumber { get; set; }

        public int PageSize { get; set; }
    }

    /// <summary>商品流转指标。</summary>
    public class WarehouseProductFlowMetricsDto
    {
        public decimal InboundQuantity { get; set; }

        public decimal OrderedQuantity { get; set; }

        public decimal ShippedQuantity { get; set; }

        public int NetSalesQuantity { get; set; }

        public decimal NetSalesAmount { get; set; }

        public decimal? AverageUnitPrice { get; set; }
    }

    /// <summary>商品流转商品行。</summary>
    public class WarehouseProductFlowProductDto
    {
        public string ProductCode { get; set; } = string.Empty;

        public string? ItemNumber { get; set; }

        public string? Barcode { get; set; }

        public string? ProductName { get; set; }

        public string? EnglishName { get; set; }

        public string? ImageUrl { get; set; }

        public string? CategoryGuid { get; set; }

        public string? CategoryName { get; set; }

        public string? SupplierCode { get; set; }

        public string? SupplierName { get; set; }

        public WarehouseProductFlowMetricsDto Metrics { get; set; } = new();
    }

    /// <summary>summary 接口返回的已选合计与分页商品行。</summary>
    public class WarehouseProductFlowAnalysisSummaryDto
    {
        public WarehouseProductFlowMetricsDto Totals { get; set; } = new();

        /// <summary>当前商品独立返回，不受 summary 分页影响。</summary>
        public WarehouseProductFlowProductDto? CurrentProduct { get; set; }

        public List<WarehouseProductFlowProductDto> Items { get; set; } = new();

        public int Total { get; set; }

        public int PageNumber { get; set; }

        public int PageSize { get; set; }
    }

    /// <summary>商品/分店每日流转趋势（扁平结构）。</summary>
    public class WarehouseProductFlowDailyDto
    {
        public DateTime Date { get; set; }

        public decimal InboundQuantity { get; set; }

        public decimal OrderedQuantity { get; set; }

        public decimal ShippedQuantity { get; set; }

        public int NetSalesQuantity { get; set; }

        public decimal NetSalesAmount { get; set; }

        public decimal? AverageUnitPrice { get; set; }
    }

    public class WarehouseProductFlowContainerDto
    {
        [JsonConverter(typeof(ContainerNumberJsonConverter))]
        public string ContainerNumber { get; set; } = string.Empty;

        public DateTime? ArrivalDate { get; set; }

        public decimal InboundQuantity { get; set; }

        public decimal? InboundUnitPrice { get; set; }

        public string? SupplierName { get; set; }
    }

    public class WarehouseProductFlowOrderDto
    {
        public string OrderNumber { get; set; } = string.Empty;

        public string? BranchName { get; set; }

        public DateTime? OrderDate { get; set; }

        public decimal OrderedQuantity { get; set; }
    }

    public class WarehouseProductFlowShipmentDto
    {
        public string? ShipmentNumber { get; set; }

        public string? OrderNumber { get; set; }

        public string? BranchName { get; set; }

        public DateTime? ShipmentDate { get; set; }

        public decimal ShippedQuantity { get; set; }

        /// <summary>订单对应的分店代码。</summary>
        public string? BranchCode { get; set; }

        /// <summary>分店 Store.IsActive 配置；未匹配门店时为 null。</summary>
        public bool? PosEnabled { get; set; }

        /// <summary>SalesPeriod 内该商品该分店净销量；同店多张发货单重复展示，不按发货单分摊。</summary>
        public int NetSalesQuantity { get; set; }
    }

    public class WarehouseProductFlowBranchDto
    {
        public string BranchCode { get; set; } = string.Empty;

        public string? BranchName { get; set; }

        public decimal OrderedQuantity { get; set; }

        public decimal ShippedQuantity { get; set; }

        public int NetSalesQuantity { get; set; }

        public decimal NetSalesAmount { get; set; }

        public decimal? SellThroughRate { get; set; }

        public decimal? AverageUnitPrice { get; set; }
    }
}
