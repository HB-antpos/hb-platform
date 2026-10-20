namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 销售明细「商品在各分店的表现」抽屉的时间窗口：销售开始日前 N 天 ~ 销售结束日。
    /// 销售曲线与进货共用这一窗口，前后端取同一个常量，窗口随返回值带回。
    /// </summary>
    public static class SalesDetailProductWindow
    {
        public const int PurchaseLookbackDays = 60;
        /// <summary>销售区间上限 731 天（含闰日）加回看天数。</summary>
        public const int MaxWindowDays = 731 + PurchaseLookbackDays;
    }

    /// <summary>
    /// 商品销售曲线（日）：窗口内该商品在授权分店（或所选单店）的每日销量。
    /// 只返回有销量的日期，缺口由前端补 0；月视图由前端按自然月汇总。
    /// </summary>
    public sealed class SalesDetailProductTrendDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? BranchCode { get; set; }
        public DateTime WindowStart { get; set; }
        public DateTime WindowEnd { get; set; }
        public List<SalesDetailProductTrendDayDto> Daily { get; set; } = new();
    }

    public sealed class SalesDetailProductTrendDayDto
    {
        public DateTime Date { get; set; }
        public decimal Quantity { get; set; }
    }

    /// <summary>
    /// 商品进货明细：窗口内每一批进货。来源 warehouse = 总仓配货（仓库订货单配货数量，按出库日期、未出库按订单日期），
    /// supplier = 供应商进货单（分店进货单数量，按入库日期、无则订单日期）；与批量货号销量、分店进货销量分析同一口径。
    /// </summary>
    public sealed class SalesDetailProductPurchasesDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public DateTime WindowStart { get; set; }
        public DateTime WindowEnd { get; set; }
        public List<SalesDetailProductPurchaseStoreDto> Stores { get; set; } = new();
        public List<SalesDetailProductPurchaseBatchDto> Batches { get; set; } = new();
    }

    public sealed class SalesDetailProductPurchaseStoreDto
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public sealed class SalesDetailProductPurchaseBatchDto
    {
        public string BranchCode { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        /// <summary>warehouse | supplier</summary>
        public string Source { get; set; } = string.Empty;
        public string? DocumentNo { get; set; }
        public decimal Quantity { get; set; }
    }
}
