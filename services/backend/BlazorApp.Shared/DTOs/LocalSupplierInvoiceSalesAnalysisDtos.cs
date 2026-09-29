namespace BlazorApp.Shared.DTOs
{
    using System.Collections.Generic;
    /// <summary>
    /// 指定分店进货单的商品销量分析响应。
    /// </summary>
    public class LocalSupplierInvoiceSalesAnalysisResponseDto
    {
        public string InvoiceGUID { get; set; } = string.Empty;
        public string? InvoiceNo { get; set; }
        public string? StoreCode { get; set; }
        public string? StoreName { get; set; }
        public string? SupplierCode { get; set; }
        public string? SupplierName { get; set; }
        public DateTime? OrderDate { get; set; }
        public DateTime? InboundDate { get; set; }
        public DateTime? AnalysisDate { get; set; }
        public DateTime? SalesStatisticLastUpdate { get; set; }
        public List<LocalSupplierInvoiceSalesAnalysisItemDto> Items { get; set; } = new();
        public string CalculationNote { get; set; } =
            "进货后30/60/90天销量从本次进货日期次日开始统计；上次到本次区间销量仍按历史区间显示。";
    }

    /// <summary>
    /// 指定进货单商品行的销量分析。
    /// </summary>
    public class LocalSupplierInvoiceSalesAnalysisItemDto
    {
        public string DetailGUID { get; set; } = string.Empty;
        public string? ProductCode { get; set; }
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductName { get; set; }
        public string? ProductImage { get; set; }
        public string? Specification { get; set; }
        public string? Unit { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? PurchasePrice { get; set; }
        public decimal? RetailPrice { get; set; }
        public decimal? Amount { get; set; }
        public int SalesQty30 { get; set; }
        public int SalesQty60 { get; set; }
        public int SalesQty90 { get; set; }
        public DateTime? PreviousPurchaseDate { get; set; }
        public int? PreviousToCurrentDays { get; set; }
        public int? SalesSincePreviousPurchase { get; set; }
        public int? SalesSincePreviousPurchase30 { get; set; }
        public int? SalesSincePreviousPurchase60 { get; set; }
        public int? SalesSincePreviousPurchase90 { get; set; }
        public DateTime? SalesStatisticLastUpdate { get; set; }
    }

    /// <summary>
    /// 分店供应商进货销量分析查询条件。
    /// </summary>
    public class LocalSupplierPurchaseSalesAnalysisQueryDto
    {
        public string? StoreCode { get; set; }
        public string? SupplierCode { get; set; }
        public List<string>? SupplierCategoryGuids { get; set; }
        public DateTime? OrderDateStart { get; set; }
        public DateTime? OrderDateEnd { get; set; }
        public string? Keyword { get; set; }
        public string? SortBy { get; set; }
        public string? SortOrder { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 100;

        /// <summary>false 时跳过逐日销量补充，让表格先出；逐日数据由前端随后单独请求。</summary>
        public bool IncludeDailySales { get; set; } = true;
    }

    /// <summary>
    /// 分店供应商进货销量分析单行结果。
    /// </summary>
    public class LocalSupplierPurchaseSalesAnalysisRowDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductName { get; set; }
        public string? ProductImage { get; set; }
        public string SupplierCode { get; set; } = string.Empty;
        public string? SupplierName { get; set; }
        public DateTime? LatestPurchaseDate { get; set; }
        public decimal? LatestPurchaseQty { get; set; }
        public DateTime? PreviousPurchaseDate { get; set; }
        public decimal? PreviousPurchaseQty { get; set; }
        public int? PurchaseIntervalDays { get; set; }
        public int? SalesBetweenPurchases { get; set; }
        public int SalesQty30 { get; set; }
        public int SalesQty60 { get; set; }
        public int SalesQty90 { get; set; }

        /// <summary>最近进货当天起至今的累计净销量（含退货负数）；与售出比、图表口径一致，可用于排序。</summary>
        public int TotalSalesSinceLatestPurchase { get; set; }
        public DateTime? SalesStatisticLastUpdate { get; set; }

        /// <summary>逐日净销量序列：从上次进货日（无上次进货则最近进货前 30 天）到布里斯班今天，缺失日期补 0。</summary>
        public List<LocalSupplierPurchaseSalesDailyPointDto> DailySales { get; set; } = new();

        /// <summary>图表窗口内的进货事件：上次进货（如有）与最近进货。</summary>
        public List<LocalSupplierPurchaseSalesPurchaseEventDto> Purchases { get; set; } = new();
    }

    /// <summary>逐日销量点；Quantity 为净销量，退货可为负。</summary>
    public class LocalSupplierPurchaseSalesDailyPointDto
    {
        public DateTime Date { get; set; }
        public int Quantity { get; set; }
    }

    /// <summary>图表窗口内的进货事件，数量取整数。</summary>
    public class LocalSupplierPurchaseSalesPurchaseEventDto
    {
        public DateTime Date { get; set; }
        public int Quantity { get; set; }
    }

    /// <summary>
    /// 分店供应商进货销量分析分页响应。
    /// </summary>
    public class LocalSupplierPurchaseSalesAnalysisResponseDto
    {
        public List<LocalSupplierPurchaseSalesAnalysisRowDto> Items { get; set; } = new();
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public DateTime? SalesStatisticLastUpdate { get; set; }
        public string CalculationNote { get; set; } =
            "进货按订单日期范围过滤、按进货发生日期汇总；日销量从上次进货起逐日展示，售出比与累计销量从最近进货当天起统计。";
    }

    /// <summary>
    /// 分店供应商进货销量分析可选分店。
    /// </summary>
    public class LocalSupplierPurchaseSalesAnalysisStoreOptionDto
    {
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// 分店供应商进货销量分析可选供应商。
    /// </summary>
    public class LocalSupplierPurchaseSalesAnalysisSupplierOptionDto
    {
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
