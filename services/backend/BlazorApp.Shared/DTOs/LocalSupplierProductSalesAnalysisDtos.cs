using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 澳洲本地商品销量分析统一请求。
    /// 所有 POST 接口共用同一请求体，按端点取用相关字段。
    /// </summary>
    public class LocalSupplierProductSalesAnalysisRequest
    {
        public LocalSupplierProductSalesAnalysisFilterDto Filter { get; set; } = new();

        public LocalSupplierProductSalesSelectionDto Selection { get; set; } = new();

        /// <summary>product-daily / invoice-details / branch-daily 接口下的当前商品代码。</summary>
        public string? CurrentProductCode { get; set; }

        /// <summary>branch-daily 接口下的分店代码。</summary>
        public string? BranchCode { get; set; }

        public int PageNumber { get; set; } = 1;

        public int PageSize { get; set; } = 50;

        /// <summary>排序字段，按 summary 端点白名单生效。</summary>
        public string? SortBy { get; set; }

        /// <summary>排序方向：asc / desc。</summary>
        public string? SortDirection { get; set; }

        /// <summary>为 true 时绕过 60 秒成功缓存重新查询。</summary>
        public bool ForceRefresh { get; set; }

        /// <summary>
        /// bootstrap 选择语义：为 true 时在缺少有效 currentProductCode 时自动选中首个候选
        /// （mount/query/reset 场景）；为 false 时保留用户选择并尊重主动清空（refresh 场景）。
        /// </summary>
        public bool AutoSelectFirst { get; set; }

        /// <summary>bootstrap 下候选分页页码（与 PageNumber 解耦）。</summary>
        public int CandidatePageNumber { get; set; }

        /// <summary>bootstrap 下候选每页数量。</summary>
        public int CandidatePageSize { get; set; }

        /// <summary>bootstrap 下汇总分页页码（与 PageNumber 解耦）。</summary>
        public int SummaryPageNumber { get; set; }

        /// <summary>bootstrap 下汇总每页数量。</summary>
        public int SummaryPageSize { get; set; }
    }

    /// <summary>日期与商品/供应商过滤条件。</summary>
    public class LocalSupplierProductSalesAnalysisFilterDto
    {
        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        /// <summary>货号/条码/名称关键字。</summary>
        public string? Keyword { get; set; }

        /// <summary>仓库分类 GUID（前端单值绑定；父分类自动包含其子孙分类）。</summary>
        public string? CategoryGuid { get; set; }

        /// <summary>供应商编码（前端单值绑定）。</summary>
        public string? SupplierCode { get; set; }

        /// <summary>仓库分类 GUID 列表，与 CategoryGuid 合并使用。</summary>
        public List<string>? WarehouseCategoryGuids { get; set; }

        /// <summary>供应商编码列表，与 SupplierCode 合并使用。</summary>
        public List<string>? SupplierCodes { get; set; }

        /// <summary>供应商网站分类 GUID 列表；供应商 200 使用仓库分类，和仓库分类条件取交集。</summary>
        public List<string>? SupplierCategoryGuids { get; set; }

        /// <summary>进货单据号/备注关键字。</summary>
        public string? DocumentKeyword { get; set; }
    }

    /// <summary>跨分页商品选择语义（与仓库商品销量分析同形）。</summary>
    public class LocalSupplierProductSalesSelectionDto
    {
        public string Mode { get; set; } = "allFiltered";

        public List<string>? IncludedProductCodes { get; set; }

        public List<string>? ExcludedProductCodes { get; set; }
    }

    /// <summary>供应商 code/name 引用。</summary>
    public class LocalSupplierProductSalesSupplierRefDto
    {
        public string Code { get; set; } = string.Empty;
        public string? Name { get; set; }
    }

    /// <summary>仓库分类选项。</summary>
    public class LocalSupplierProductSalesCategoryOptionDto
    {
        public string Guid { get; set; } = string.Empty;
        public string? Name { get; set; }
    }

    /// <summary>供应商选项。</summary>
    public class LocalSupplierProductSalesSupplierOptionDto
    {
        public string Code { get; set; } = string.Empty;
        public string? Name { get; set; }
    }

    /// <summary>
    /// 按供应商分组的供应商分类树节点。
    /// 父节点用于展示层级，只有叶节点保持原有 GUID 可选/筛选语义。
    /// </summary>
    public class LocalSupplierProductSalesSupplierCategoryOptionDto
    {
        public string SupplierCode { get; set; } = string.Empty;
        public string Guid { get; set; } = string.Empty;
        public string? ParentGuid { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsSelectable { get; set; }
        public List<LocalSupplierProductSalesSupplierCategoryOptionDto> Children { get; set; } = new();
    }

    /// <summary>options 接口响应。</summary>
    public class LocalSupplierProductSalesOptionsDto
    {
        public List<LocalSupplierProductSalesCategoryOptionDto> WarehouseCategories { get; set; } = new();
        public List<LocalSupplierProductSalesSupplierOptionDto> Suppliers { get; set; } = new();
    }

    /// <summary>候选商品行。</summary>
    public class LocalSupplierProductSalesCandidateDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductName { get; set; }
        public string? ImageUrl { get; set; }
        public string? WarehouseCategoryGuid { get; set; }
        public string? WarehouseCategoryName { get; set; }
    }

    /// <summary>分页数据。</summary>
    public class LocalSupplierProductSalesPagedDto<T>
    {
        public List<T> Items { get; set; } = new();
        public int Total { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
    }

    /// <summary>summary 汇总指标。</summary>
    public class LocalSupplierProductSalesSummaryTotalsDto
    {
        public decimal PurchaseQuantity { get; set; }
        public decimal PurchaseAmount { get; set; }
        public decimal NetSalesQuantity { get; set; }
        public decimal NetSalesAmount { get; set; }
        public decimal? SellThroughRate { get; set; }
    }

    /// <summary>summary 商品行。</summary>
    public class LocalSupplierProductSalesSummaryRowDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductName { get; set; }
        public string? ImageUrl { get; set; }
        public string? WarehouseCategoryGuid { get; set; }
        public string? WarehouseCategoryName { get; set; }
        public List<LocalSupplierProductSalesSupplierRefDto> Suppliers { get; set; } = new();
        public decimal PurchaseQuantity { get; set; }
        public decimal PurchaseAmount { get; set; }
        public decimal NetSalesQuantity { get; set; }
        public decimal NetSalesAmount { get; set; }
        public decimal? SellThroughRate { get; set; }
    }

    /// <summary>summary 响应。</summary>
    public class LocalSupplierProductSalesSummaryResponseDto
    {
        public LocalSupplierProductSalesSummaryTotalsDto Totals { get; set; } = new();
        public List<LocalSupplierProductSalesSummaryRowDto> Items { get; set; } = new();
        public int Total { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
    }

    /// <summary>商品每日进货/销量趋势。</summary>
    public class LocalSupplierProductSalesDailyDto
    {
        public DateTime Date { get; set; }
        public decimal PurchaseQuantity { get; set; }
        public decimal PurchaseAmount { get; set; }
        public decimal NetSalesQuantity { get; set; }
        public decimal NetSalesAmount { get; set; }
        public decimal? AverageUnitPrice { get; set; }
    }

    /// <summary>当前商品进货明细行。</summary>
    public class LocalSupplierProductSalesInvoiceDetailDto
    {
        public string DetailGUID { get; set; } = string.Empty;
        public string InvoiceGUID { get; set; } = string.Empty;
        public string? InvoiceNo { get; set; }
        public string? StoreCode { get; set; }
        public string? StoreName { get; set; }
        public string? SupplierCode { get; set; }
        public string? SupplierName { get; set; }
        public DateTime? PurchaseDate { get; set; }
        public string? ProductCode { get; set; }
        public string? ProductName { get; set; }
        public decimal Quantity { get; set; }
        public decimal? PurchasePrice { get; set; }
        public decimal Amount { get; set; }
    }

    /// <summary>当前商品进货明细分页响应。</summary>
    public class LocalSupplierProductSalesInvoiceDetailPageDto
    {
        public List<LocalSupplierProductSalesInvoiceDetailDto> Items { get; set; } = new();
        public int Total { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
    }

    /// <summary>分店销售排行。</summary>
    public class LocalSupplierProductSalesBranchDto
    {
        public string BranchCode { get; set; } = string.Empty;
        public string? BranchName { get; set; }
        public decimal NetSalesQuantity { get; set; }
        public decimal NetSalesAmount { get; set; }
        public decimal? AverageUnitPrice { get; set; }
    }

    /// <summary>分店每日销售趋势。</summary>
    public class LocalSupplierProductSalesBranchDailyDto
    {
        public DateTime Date { get; set; }
        public decimal NetSalesQuantity { get; set; }
        public decimal NetSalesAmount { get; set; }
        public decimal? AverageUnitPrice { get; set; }
    }

    /// <summary>经过裁剪后的有效选择（included/allFiltered 语义已清洗）。</summary>
    public class LocalSupplierProductSalesEffectiveSelectionDto : LocalSupplierProductSalesSelectionDto
    {
    }

    /// <summary>bootstrap 聚合响应，包含所有分段与部分失败信息。</summary>
    public class LocalSupplierProductSalesBootstrapResponseDto
    {
        public LocalSupplierProductSalesOptionsDto Options { get; set; } = new();
        public LocalSupplierProductSalesPagedDto<LocalSupplierProductSalesCandidateDto> Candidates
        {
            get;
            set;
        } = new();
        public LocalSupplierProductSalesEffectiveSelectionDto EffectiveSelection { get; set; } =
            new();
        public LocalSupplierProductSalesCandidateDto? CurrentProduct { get; set; }
        public LocalSupplierProductSalesSummaryResponseDto Summary { get; set; } = new();
        public LocalSupplierProductSalesInvoiceDetailPageDto InvoiceDetails { get; set; } = new();
        public List<LocalSupplierProductSalesDailyDto> ProductDaily { get; set; } = new();
        public List<LocalSupplierProductSalesBranchDto> Branches { get; set; } = new();

        /// <summary>是否部分分段失败（options/summary/details/daily/branches）。</summary>
        public bool Partial { get; set; }

        /// <summary>分段失败信息，键为分段名，值为错误信息。</summary>
        public Dictionary<string, string> SectionErrors { get; set; } = new();

        /// <summary>阶段耗时（毫秒），供控制器生成 Server-Timing。</summary>
        [JsonIgnore]
        public Dictionary<string, double> ServerTimings { get; set; } = new();
    }
}
