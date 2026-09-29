using System;
using System.Collections.Generic;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 商品经营分析查询条件。
    /// </summary>
    public class ProductMovementReportQueryDto
    {
        public string? StoreCode { get; set; }
        public DateTime? AsOfDate { get; set; }
        public string? Suggestion { get; set; }
        public string? DataCredibility { get; set; }
        public string? Keyword { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;

        /// <summary>排序字段：salesQty30（近 30 天销量）；为空时按建议紧急程度排序。</summary>
        public string? SortBy { get; set; }

        /// <summary>排序方向：asc / desc，只在指定排序字段时生效。</summary>
        public string? SortDirection { get; set; }
    }

    /// <summary>
    /// 商品经营分析单行结果。
    /// </summary>
    public class ProductMovementReportRowDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public string ProductCode { get; set; } = string.Empty;
        /// <summary>货号，取自商品档案；与内部商品编码 ProductCode 不是同一个值。</summary>
        public string? ItemNumber { get; set; }
        public string? ProductName { get; set; }
        public string? Barcode { get; set; }
        public string? ImageUrl { get; set; }
        public int SalesQty30 { get; set; }
        public int SalesQty90 { get; set; }
        public decimal DailySalesQty30 { get; set; }
        public decimal SalesAmount90Aud { get; set; }
        public decimal? GrossProfit90Aud { get; set; }
        public decimal? GrossMarginRate90 { get; set; }
        public DateTime? LastSaleDate { get; set; }
        public int? NoSaleDays { get; set; }
        public decimal PurchaseQty180 { get; set; }
        public int SalesQty180 { get; set; }
        public decimal EstimatedRemainingQty { get; set; }
        public decimal? EstimatedCoverDays { get; set; }
        public string DataCredibility { get; set; } = "中";
        public string DataExceptionFlag { get; set; } = "正常";
        public string SystemSuggestion { get; set; } = "正常";
        public string StoreManagerAction { get; set; } = "暂无特殊动作，按正常陈列和订货节奏处理。";
        public DateTime? SalesStatisticLastUpdate { get; set; }
    }

    /// <summary>
    /// 商品经营分析汇总项。
    /// </summary>
    public class ProductMovementReportSummaryDto
    {
        public string Key { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    /// <summary>
    /// 商品经营分析可选分店。
    /// </summary>
    public class ProductMovementReportStoreOptionDto
    {
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// 门店 IANA 时区（如 Australia/Sydney）。移动营业额报表据此按门店本地时间计算同期对比的截止整点；
        /// 未维护时为 null，客户端回退为固定 UTC+10。
        /// </summary>
        public string? TimeZoneId { get; set; }
    }

    /// <summary>
    /// 商品经营分析分页响应。
    /// </summary>
    public class ProductMovementReportResponseDto
    {
        public List<ProductMovementReportRowDto> Items { get; set; } = new();
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public List<ProductMovementReportSummaryDto> SuggestionSummary { get; set; } = new();
        public List<ProductMovementReportSummaryDto> CredibilitySummary { get; set; } = new();
        public DateTime? SalesStatisticLastUpdate { get; set; }
        /// <summary>数据来自预计算快照时为快照生成时间（UTC）；实时计算时为 null。</summary>
        public DateTime? SnapshotGeneratedAtUtc { get; set; }
        public string CalculationNote { get; set; } =
            "估算剩余量=近180天进货单数量-近180天销售数量；不是货架库存、后仓库存或财务库存。";
        public string DataScopeNote { get; set; } =
            "系统没有货架库存和后仓库存，本页不能判断从后仓补到货架；店长需要现场核对。";
    }
}
