using System.Text.Json.Serialization;
using BlazorApp.Shared.Helper;
using System;
using System.Collections.Generic;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 只读仓库商品档案：商品摘要响应。
    /// JSON 序列化遵循全局 camelCase 策略。
    /// </summary>
    public class WarehouseProductRecordSummaryDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductName { get; set; }
        public string? EnglishName { get; set; }
        public string? ImageUrl { get; set; }
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// 货柜进货记录查询请求。
    /// </summary>
    public class WarehouseProductRecordContainerQueryRequest
    {
        public string? ContainerKeyword { get; set; }
        public DateTime? ArrivalStartDate { get; set; }
        public DateTime? ArrivalEndDate { get; set; }
        public List<int>? Statuses { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 20;
        public string? SortBy { get; set; }
        public string? SortDirection { get; set; }
    }

    /// <summary>
    /// 货柜进货记录过滤全量合计。
    /// </summary>
    public class WarehouseProductRecordContainerSummaryDto
    {
        public int ContainerCount { get; set; }
        public decimal LoadingPieces { get; set; }
        public decimal LoadingQuantity { get; set; }
        public decimal TotalAmount { get; set; }
    }

    /// <summary>
    /// 单条货柜进货明细（不合并）。
    /// </summary>
    public class WarehouseProductRecordContainerItemDto
    {
        public string DetailCode { get; set; } = string.Empty;
        public string ContainerCode { get; set; } = string.Empty;
        [JsonConverter(typeof(ContainerNumberJsonConverter))]
        public string? ContainerNumber { get; set; }
        public DateTime? LoadingDate { get; set; }
        public DateTime? EstimatedArrivalDate { get; set; }
        public DateTime? ActualArrivalDate { get; set; }
        public DateTime? EffectiveArrivalDate { get; set; }
        public int? Status { get; set; }
        public decimal? LoadingPieces { get; set; }
        public decimal? LoadingQuantity { get; set; }
        public decimal? DomesticPrice { get; set; }
        public decimal? ImportPrice { get; set; }
        public decimal? TotalAmount { get; set; }
    }

    /// <summary>
    /// 货柜进货记录查询结果。
    /// </summary>
    public class WarehouseProductRecordContainerQueryResultDto
    {
        public int TotalCount { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public WarehouseProductRecordContainerSummaryDto Summary { get; set; } = new();
        public List<WarehouseProductRecordContainerItemDto> Items { get; set; } = new();
    }

    /// <summary>
    /// 配货统计查询请求。
    /// </summary>
    public class WarehouseProductRecordAllocationQueryRequest
    {
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
    }

    /// <summary>
    /// 配货统计全量合计。
    /// </summary>
    public class WarehouseProductRecordAllocationSummaryDto
    {
        public decimal AllocationQuantity { get; set; }
        public decimal AllocationAmount { get; set; }
        public int OrderCount { get; set; }
    }

    /// <summary>
    /// 单分店配货统计。
    /// </summary>
    public class WarehouseProductRecordAllocationBranchDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public decimal AllocationQuantity { get; set; }
        public decimal AllocationAmount { get; set; }
        public int OrderCount { get; set; }
        public DateTime? FirstAllocationDate { get; set; }
        public DateTime? LastAllocationDate { get; set; }
    }

    /// <summary>
    /// 配货统计查询结果。
    /// </summary>
    public class WarehouseProductRecordAllocationQueryResultDto
    {
        public WarehouseProductRecordAllocationSummaryDto Summary { get; set; } = new();
        public List<WarehouseProductRecordAllocationBranchDto> Branches { get; set; } = new();
    }
}
