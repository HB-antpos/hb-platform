using System.Text.Json.Serialization;
using BlazorApp.Shared.Helper;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>单一业务日期范围。</summary>
    public class WarehouseProductFlowDatePeriodDto
    {
        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }
    }

    /// <summary>货柜、订单/发货、销量三套独立日期范围。</summary>
    public class WarehouseProductFlowPeriodsDto
    {
        public WarehouseProductFlowDatePeriodDto ContainerPeriod { get; set; } = new();

        public WarehouseProductFlowDatePeriodDto OrderShipmentPeriod { get; set; } = new();

        public WarehouseProductFlowDatePeriodDto SalesPeriod { get; set; } = new();
    }

    /// <summary>
    /// /candidates 的独立请求体：只携带主档筛选与分页，不携带日期、选择或当前商品。
    /// </summary>
    public class WarehouseProductFlowCandidateRequest
    {
        public WarehouseProductFlowAnalysisFilterDto Filter { get; set; } = new();

        public int PageNumber { get; set; } = 1;

        public int PageSize { get; set; } = 50;

        public string? SortBy { get; set; }

        public string? SortDirection { get; set; }

        public bool ForceRefresh { get; set; }
    }

    /// <summary>
    /// /candidates 的纯商品主档候选行，不包含任何流转指标。
    /// </summary>
    public class WarehouseProductFlowCandidateDto
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

        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? SupplierName { get; set; }
    }
}
