namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 销售明细「澳洲供应商分类」页签：所选澳洲供应商按各自分类树汇总的本期/同期销售，
    /// 以及（可选）某个分类节点下的商品明细分页。金额为 AUD，毛利率为 0..1。
    /// </summary>
    public sealed class SalesDetailCategoryReportDto
    {
        /// <summary>所选供应商合计。</summary>
        public SalesDetailCategoryMetricsDto Summary { get; set; } = new();

        /// <summary>所选供应商中没有分类（或分类已删除）的商品合计。</summary>
        public SalesDetailCategoryMetricsDto Unassigned { get; set; } = new();

        /// <summary>每个所选供应商一棵树；只含本期或同期有销售的节点。不请求分类树时为空。</summary>
        public List<SalesDetailCategorySupplierDto> Suppliers { get; set; } = new();

        /// <summary>请求了节点时返回该节点（含子分类）下的商品分页，按本期营业额降序。</summary>
        public SalesDetailSectionResultDto? Products { get; set; }
    }

    /// <summary>
    /// 汇总指标。分类之间不做收据去重，因此不提供客单数。
    /// </summary>
    public class SalesDetailCategoryMetricsDto
    {
        public decimal Revenue { get; set; }
        public decimal? CompareRevenue { get; set; }
        public int Quantity { get; set; }
        public int? CompareQuantity { get; set; }
        /// <summary>成本未补全时为 null。</summary>
        public decimal? GrossProfit { get; set; }
        public decimal? CompareGrossProfit { get; set; }
        public decimal? GrossMarginRate { get; set; }
        public decimal? CompareGrossMarginRate { get; set; }
        /// <summary>本期有销售记录的商品数。</summary>
        public int ProductCount { get; set; }
        public int? CompareProductCount { get; set; }
    }

    public sealed class SalesDetailCategorySupplierDto : SalesDetailCategoryMetricsDto
    {
        public string SupplierCode { get; set; } = string.Empty;
        public string SupplierName { get; set; } = string.Empty;

        /// <summary>supplier：供应商网站分类；warehouse：供应商 200 使用的仓库分类。</summary>
        public string CategorySource { get; set; } = SalesDetailCategorySources.Supplier;

        public List<SalesDetailCategoryNodeDto> Categories { get; set; } = new();

        /// <summary>该供应商未归类商品；没有时为 null。</summary>
        public SalesDetailCategoryNodeDto? Unassigned { get; set; }
    }

    public sealed class SalesDetailCategoryNodeDto : SalesDetailCategoryMetricsDto
    {
        /// <summary>分类 GUID；未归类节点为 <see cref="SalesDetailCategorySources.UnassignedKey"/>。</summary>
        public string CategoryGuid { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Depth { get; set; }
        public bool IsActive { get; set; } = true;
        public List<SalesDetailCategoryNodeDto> Children { get; set; } = new();
    }

    /// <summary>「澳洲供应商分类」页签的筛选选项：供应商与账号可见分店。</summary>
    public sealed class SalesDetailCategoryOptionsDto
    {
        public List<SalesDetailCategorySupplierOptionDto> Suppliers { get; set; } = new();
        public List<SalesDetailCategoryStoreOptionDto> Stores { get; set; } = new();
    }

    public sealed class SalesDetailCategoryStoreOptionDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
    }

    /// <summary>「澳洲供应商分类」页签的供应商选项。</summary>
    public sealed class SalesDetailCategorySupplierOptionDto
    {
        public string SupplierCode { get; set; } = string.Empty;
        public string SupplierName { get; set; } = string.Empty;
        public string CategorySource { get; set; } = SalesDetailCategorySources.Supplier;
        /// <summary>启用中的分类数；200 为仓库分类数。</summary>
        public int CategoryCount { get; set; }
        /// <summary>已归类商品数（分类归属的供应商与商品当前供应商一致）。</summary>
        public int AssignedProductCount { get; set; }
    }

    public static class SalesDetailCategorySources
    {
        public const string Supplier = "supplier";
        public const string Warehouse = "warehouse";
        /// <summary>请求与响应中代表「未归类」节点的保留键。</summary>
        public const string UnassignedKey = "__unassigned__";
    }
}
