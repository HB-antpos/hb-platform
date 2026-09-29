namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 比较模式枚举
    /// </summary>
    public enum CompareMode
    {
        /// <summary>
        /// 按周比较
        /// </summary>
        ByWeek,

        /// <summary>
        /// 按日期比较
        /// </summary>
        ByDate
    }

    /// <summary>
    /// 日期范围 DTO
    /// 用于指定查询的主日期范围和对比日期范围
    /// </summary>
    public class DateRangeDto
    {
        /// <summary>
        /// 开始日期
        /// </summary>
        public DateTime StartDate { get; set; }

        /// <summary>
        /// 结束日期
        /// </summary>
        public DateTime EndDate { get; set; }

        /// <summary>
        /// 对比开始日期
        /// </summary>
        public DateTime? CompareStartDate { get; set; }

        /// <summary>
        /// 对比结束日期
        /// </summary>
        public DateTime? CompareEndDate { get; set; }

        /// <summary>
        /// 比较模式
        /// </summary>
        public CompareMode CompareMode { get; set; } = CompareMode.ByDate;

        public override string ToString()
        {
            return $"{StartDate:yyyy-MM-dd}|{EndDate:yyyy-MM-dd}|{CompareStartDate:yyyy-MM-dd}|{CompareEndDate:yyyy-MM-dd}|{CompareMode}";
        }
    }

    /// <summary>
    /// 仪表板汇总数据 DTO
    /// 包含总销售额、总销量、订单数等关键指标及其增长率
    /// </summary>
    public class DashboardSummaryDto
    {
        /// <summary>
        /// 统计开始日期
        /// </summary>
        public DateTime StartDate { get; set; }

        /// <summary>
        /// 统计结束日期
        /// </summary>
        public DateTime EndDate { get; set; }

        /// <summary>
        /// 总销售额
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// 总销售数量
        /// </summary>
        public int TotalQuantity { get; set; }

        /// <summary>
        /// 订单总数
        /// </summary>
        public int OrderCount { get; set; }

        /// <summary>
        /// SKU 数量
        /// </summary>
        public int SkuCount { get; set; }

        /// <summary>
        /// 客户数量
        /// </summary>
        public int CustomerCount { get; set; }

        /// <summary>
        /// 平均客单价
        /// </summary>
        public decimal AverageOrderValue { get; set; }

        /// <summary>
        /// 对比期间总销售额
        /// </summary>
        public decimal? CompareTotalAmount { get; set; }

        /// <summary>
        /// 对比期间总销售数量
        /// </summary>
        public int? CompareTotalQuantity { get; set; }

        /// <summary>
        /// 对比期间订单总数
        /// </summary>
        public int? CompareOrderCount { get; set; }

        /// <summary>
        /// 对比期间 SKU 数量
        /// </summary>
        public int? CompareSkuCount { get; set; }

        /// <summary>
        /// 对比期间客户数量
        /// </summary>
        public int? CompareCustomerCount { get; set; }

        /// <summary>
        /// 对比期间平均客单价
        /// </summary>
        public decimal? CompareAvgOrderValue { get; set; }

        /// <summary>
        /// 销售额增长率
        /// </summary>
        public decimal? TotalAmountGrowth { get; set; }

        /// <summary>
        /// 销售数量增长率
        /// </summary>
        public decimal? TotalQuantityGrowth { get; set; }

        /// <summary>
        /// 订单数增长率
        /// </summary>
        public decimal? OrderCountGrowth { get; set; }

        /// <summary>
        /// SKU 数量增长率
        /// </summary>
        public decimal? SkuCountGrowth { get; set; }

        /// <summary>
        /// 客户数量增长率
        /// </summary>
        public decimal? CustomerCountGrowth { get; set; }

        /// <summary>
        /// 客单价增长率
        /// </summary>
        public decimal? AvgOrderValueGrowth { get; set; }
    }

    /// <summary>
    /// 分时销售数据 DTO
    /// 用于展示一天中各小时的销售情况
    /// </summary>
    public class HourlySalesDto
    {
        /// <summary>
        /// 日期
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// 小时 (0-23)
        /// </summary>
        public int Hour { get; set; }

        /// <summary>
        /// 分店代码
        /// </summary>
        public string? BranchCode { get; set; }

        /// <summary>
        /// 分店名称
        /// </summary>
        public string? BranchName { get; set; }

        /// <summary>
        /// 总销售额
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// 总销售数量
        /// </summary>
        public int TotalQuantity { get; set; }

        /// <summary>
        /// 客户数量
        /// </summary>
        public int CustomerCount { get; set; }

        /// <summary>
        /// 平均客单价
        /// </summary>
        public decimal AverageOrderValue { get; set; }

        /// <summary>
        /// 对比期间总销售额
        /// </summary>
        public decimal? CompareTotalAmount { get; set; }

        /// <summary>
        /// 对比期间总销售数量
        /// </summary>
        public int? CompareTotalQuantity { get; set; }

        /// <summary>
        /// 对比期间客户数量
        /// </summary>
        public int? CompareCustomerCount { get; set; }

        /// <summary>
        /// 对比期间平均客单价
        /// </summary>
        public decimal? CompareAvgOrderValue { get; set; }
    }

    /// <summary>
    /// 分店销售排行 DTO
    /// 用于展示分店销售业绩排名
    /// </summary>
    public class StoreSalesRankDto
    {
        /// <summary>
        /// 统计开始日期
        /// </summary>
        public DateTime StartDate { get; set; }

        /// <summary>
        /// 统计结束日期
        /// </summary>
        public DateTime EndDate { get; set; }

        /// <summary>
        /// 分店代码
        /// </summary>
        public string BranchCode { get; set; } = string.Empty;

        /// <summary>
        /// 分店名称
        /// </summary>
        public string BranchName { get; set; } = string.Empty;

        /// <summary>
        /// 总销售额
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// 总销售数量
        /// </summary>
        public int TotalQuantity { get; set; }

        /// <summary>
        /// 订单总数
        /// </summary>
        public int OrderCount { get; set; }

        /// <summary>
        /// 客户数量
        /// </summary>
        public int CustomerCount { get; set; }

        /// <summary>
        /// 平均客单价
        /// </summary>
        public decimal AverageOrderValue { get; set; }

        /// <summary>
        /// 对比期间总销售额
        /// </summary>
        public decimal? CompareTotalAmount { get; set; }

        /// <summary>
        /// 对比期间总销售数量
        /// </summary>
        public int? CompareTotalQuantity { get; set; }

        /// <summary>
        /// 对比期间订单总数
        /// </summary>
        public int? CompareOrderCount { get; set; }

        /// <summary>
        /// 对比期间平均客单价
        /// </summary>
        public decimal? CompareAvgOrderValue { get; set; }

        /// <summary>
        /// 销售额增长率
        /// </summary>
        public decimal? TotalAmountGrowth { get; set; }

        /// <summary>
        /// 销售数量增长率
        /// </summary>
        public decimal? TotalQuantityGrowth { get; set; }

        /// <summary>
        /// 订单数增长率
        /// </summary>
        public decimal? OrderCountGrowth { get; set; }

        /// <summary>
        /// 客单价增长率
        /// </summary>
        public decimal? AvgOrderValueGrowth { get; set; }
    }

    /// <summary>
    /// 供应商销售排行 DTO
    /// 用于展示供应商销售业绩排名
    /// </summary>
    public class SupplierSalesRankDto
    {
        /// <summary>
        /// 统计开始日期
        /// </summary>
        public DateTime StartDate { get; set; }

        /// <summary>
        /// 统计结束日期
        /// </summary>
        public DateTime EndDate { get; set; }

        /// <summary>
        /// 供应商代码
        /// </summary>
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商名称
        /// </summary>
        public string SupplierName { get; set; } = string.Empty;

        /// <summary>
        /// 总销售额
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// 总销售数量
        /// </summary>
        public int TotalQuantity { get; set; }

        /// <summary>
        /// 客单数
        /// </summary>
        public int OrderCount { get; set; }

        /// <summary>
        /// 客单价
        /// </summary>
        public decimal AverageTransaction { get; set; }

        /// <summary>
        /// 销售该供应商产品的分店数量
        /// </summary>
        public int StoreCount { get; set; }

        /// <summary>
        /// 对比期间总销售额
        /// </summary>
        public decimal? CompareTotalAmount { get; set; }

        /// <summary>
        /// 对比期间客单数
        /// </summary>
        public int? CompareOrderCount { get; set; }

        /// <summary>
        /// 对比期间客单价
        /// </summary>
        public decimal? CompareAverageTransaction { get; set; }

        /// <summary>
        /// 销售额增长率
        /// </summary>
        public decimal? TotalAmountGrowth { get; set; }

        /// <summary>
        /// 当前期毛利额；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? GrossProfit { get; set; }

        /// <summary>
        /// 当前期毛利率；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? GrossMarginRate { get; set; }

        /// <summary>
        /// 同期毛利额；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? CompareGrossProfit { get; set; }

        /// <summary>
        /// 同期毛利率；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? CompareGrossMarginRate { get; set; }

        /// <summary>当前期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CostStatus { get; set; } = "NoActivity";

        /// <summary>同期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CompareCostStatus { get; set; } = "NoActivity";
    }

    /// <summary>
    /// 中国供应商销售排行 DTO
    /// 专门用于展示中国供应商的销售业绩排名
    /// </summary>
    public class ChinaSupplierSalesRankDto
    {
        /// <summary>
        /// 统计开始日期
        /// </summary>
        public DateTime StartDate { get; set; }

        /// <summary>
        /// 统计结束日期
        /// </summary>
        public DateTime EndDate { get; set; }

        /// <summary>
        /// 供应商代码
        /// </summary>
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商名称
        /// </summary>
        public string SupplierName { get; set; } = string.Empty;

        /// <summary>
        /// 总销售额
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// 总销售数量
        /// </summary>
        public int TotalQuantity { get; set; }

        /// <summary>
        /// 客单数
        /// </summary>
        public int OrderCount { get; set; }

        /// <summary>
        /// 客单价
        /// </summary>
        public decimal AverageTransaction { get; set; }

        /// <summary>
        /// 销售该供应商产品的分店数量
        /// </summary>
        public int StoreCount { get; set; }

        /// <summary>
        /// 对比期间总销售额
        /// </summary>
        public decimal? CompareTotalAmount { get; set; }

        /// <summary>
        /// 对比期间客单数
        /// </summary>
        public int? CompareOrderCount { get; set; }

        /// <summary>
        /// 对比期间客单价
        /// </summary>
        public decimal? CompareAverageTransaction { get; set; }

        /// <summary>
        /// 销售额增长率
        /// </summary>
        public decimal? TotalAmountGrowth { get; set; }

        /// <summary>
        /// 当前期毛利额；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? GrossProfit { get; set; }

        /// <summary>
        /// 当前期毛利率；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? GrossMarginRate { get; set; }

        /// <summary>
        /// 同期毛利额；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? CompareGrossProfit { get; set; }

        /// <summary>
        /// 同期毛利率；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? CompareGrossMarginRate { get; set; }

        /// <summary>当前期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CostStatus { get; set; } = "NoActivity";

        /// <summary>同期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CompareCostStatus { get; set; } = "NoActivity";
    }

    /// <summary>
    /// 供应商分店销售数据 DTO
    /// 展示特定供应商在各分店的销售情况
    /// </summary>
    public class SupplierStoreSalesDto
    {
        /// <summary>
        /// 统计开始日期
        /// </summary>
        public DateTime StartDate { get; set; }

        /// <summary>
        /// 统计结束日期
        /// </summary>
        public DateTime EndDate { get; set; }

        /// <summary>
        /// 供应商代码
        /// </summary>
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商名称
        /// </summary>
        public string SupplierName { get; set; } = string.Empty;

        /// <summary>
        /// 分店代码
        /// </summary>
        public string BranchCode { get; set; } = string.Empty;

        /// <summary>
        /// 分店名称
        /// </summary>
        public string BranchName { get; set; } = string.Empty;

        /// <summary>
        /// 总销售额
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// 总销售数量
        /// </summary>
        public int TotalQuantity { get; set; }

        /// <summary>
        /// 客单数
        /// </summary>
        public int OrderCount { get; set; }

        /// <summary>
        /// 客单价
        /// </summary>
        public decimal AverageTransaction { get; set; }

        /// <summary>
        /// 对比期间总销售额
        /// </summary>
        public decimal? CompareTotalAmount { get; set; }

        /// <summary>
        /// 对比期间客单数
        /// </summary>
        public int? CompareOrderCount { get; set; }

        /// <summary>
        /// 对比期间客单价
        /// </summary>
        public decimal? CompareAverageTransaction { get; set; }

        /// <summary>
        /// 销售额增长率
        /// </summary>
        public decimal? TotalAmountGrowth { get; set; }

        /// <summary>
        /// 当前期毛利额；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? GrossProfit { get; set; }

        /// <summary>
        /// 当前期毛利率；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? GrossMarginRate { get; set; }

        /// <summary>
        /// 同期毛利额；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? CompareGrossProfit { get; set; }

        /// <summary>
        /// 同期毛利率；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? CompareGrossMarginRate { get; set; }

        /// <summary>当前期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CostStatus { get; set; } = "NoActivity";

        /// <summary>同期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CompareCostStatus { get; set; } = "NoActivity";
    }

    /// <summary>
    /// 分店供应商销售数据 DTO
    /// 展示特定分店中各供应商的销售情况
    /// </summary>
    public class StoreSupplierSalesDto
    {
        /// <summary>
        /// 统计开始日期
        /// </summary>
        public DateTime StartDate { get; set; }

        /// <summary>
        /// 统计结束日期
        /// </summary>
        public DateTime EndDate { get; set; }

        /// <summary>
        /// 分店代码
        /// </summary>
        public string BranchCode { get; set; } = string.Empty;

        /// <summary>
        /// 分店名称
        /// </summary>
        public string BranchName { get; set; } = string.Empty;

        /// <summary>
        /// 供应商代码
        /// </summary>
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商名称
        /// </summary>
        public string SupplierName { get; set; } = string.Empty;

        /// <summary>
        /// 总销售额
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// 总销售数量
        /// </summary>
        public int TotalQuantity { get; set; }

        /// <summary>
        /// 对比期间总销售额
        /// </summary>
        public decimal? CompareTotalAmount { get; set; }

        /// <summary>
        /// 销售额增长率
        /// </summary>
        public decimal? TotalAmountGrowth { get; set; }

        /// <summary>当前期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CostStatus { get; set; } = "NoActivity";

        /// <summary>同期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CompareCostStatus { get; set; } = "NoActivity";
    }

    /// <summary>
    /// 销售商品明细 DTO
    /// </summary>
    public class SalesProductDetailDto
    {
        /// <summary>
        /// 商品代码
        /// </summary>
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>
        /// 商品图片URL
        /// </summary>
        public string? ProductImage { get; set; }

        /// <summary>
        /// 商品名称
        /// </summary>
        public string? ProductName { get; set; }

        /// <summary>
        /// 销售数量
        /// </summary>
        public int Quantity { get; set; }

        /// <summary>
        /// 销售金额
        /// </summary>
        public decimal SalesAmount { get; set; }

        /// <summary>
        /// 单价
        /// </summary>
        public decimal UnitPrice { get; set; }

        /// <summary>
        /// 原价（如果有）
        /// </summary>
        public decimal? OriginalPrice { get; set; }
    }

    /// <summary>
    /// 分页的销售商品明细 DTO
    /// </summary>
    public class PagedSalesProductDetailDto
    {
        /// <summary>
        /// 销售商品明细列表
        /// </summary>
        public List<SalesProductDetailDto> Data { get; set; } = new();

        /// <summary>
        /// 总记录数
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// 当前页码
        /// </summary>
        public int PageIndex { get; set; }

        /// <summary>
        /// 每页记录数
        /// </summary>
        public int PageSize { get; set; }
    }

    /// <summary>
    /// 带折扣信息的销售商品明细 DTO
    /// </summary>
    public class SalesProductDetailWithDiscountDto
    {
        /// <summary>
        /// 商品代码
        /// </summary>
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>
        /// 商品货号
        /// </summary>
        public string? ItemNumber { get; set; }

        /// <summary>
        /// 商品图片URL
        /// </summary>
        public string? ProductImage { get; set; }

        /// <summary>
        /// 商品名称
        /// </summary>
        public string? ProductName { get; set; }

        /// <summary>
        /// 销售总数量
        /// </summary>
        public int Quantity { get; set; }

        /// <summary>
        /// 折扣销售数量
        /// </summary>
        public int DiscountedQuantity { get; set; }

        /// <summary>
        /// 销售总金额
        /// </summary>
        public decimal SalesAmount { get; set; }

        /// <summary>
        /// 平均单价
        /// </summary>
        public decimal AverageUnitPrice { get; set; }

        /// <summary>
        /// 平均原价
        /// </summary>
        public decimal? AverageOriginalPrice { get; set; }

        /// <summary>
        /// 订单数量
        /// </summary>
        public int OrderCount { get; set; }

        /// <summary>
        /// 对比期销售总数量
        /// </summary>
        public int QuantityLY { get; set; }

        /// <summary>
        /// 对比期折扣销售数量
        /// </summary>
        public int DiscountedQuantityLY { get; set; }

        /// <summary>
        /// 对比期销售总金额
        /// </summary>
        public decimal SalesAmountLY { get; set; }

        /// <summary>
        /// 对比期平均单价
        /// </summary>
        public decimal AverageUnitPriceLY { get; set; }

        /// <summary>
        /// 对比期平均原价
        /// </summary>
        public decimal? AverageOriginalPriceLY { get; set; }

        /// <summary>
        /// 对比期订单数量
        /// </summary>
        public int OrderCountLY { get; set; }

        /// <summary>
        /// 当前期毛利额；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? GrossProfit { get; set; }

        /// <summary>
        /// 当前期毛利率；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? GrossMarginRate { get; set; }

        /// <summary>
        /// 同期毛利额；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? GrossProfitLY { get; set; }

        /// <summary>
        /// 同期毛利率；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? GrossMarginRateLY { get; set; }

        /// <summary>当前期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CostStatus { get; set; } = "NoActivity";

        /// <summary>同期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CompareCostStatus { get; set; } = "NoActivity";
    }

    /// <summary>
    /// 商品在各分店销售情况 DTO
    /// </summary>
    public class ProductBranchSalesDto
    {
        /// <summary>
        /// 分店代码
        /// </summary>
        public string BranchCode { get; set; } = string.Empty;

        /// <summary>
        /// 分店名称
        /// </summary>
        public string BranchName { get; set; } = string.Empty;

        /// <summary>
        /// 销售数量
        /// </summary>
        public int Quantity { get; set; }

        /// <summary>
        /// 折扣销售数量
        /// </summary>
        public int DiscountedQuantity { get; set; }

        /// <summary>
        /// 销售金额
        /// </summary>
        public decimal SalesAmount { get; set; }

        /// <summary>
        /// 对比期销售金额
        /// </summary>
        public decimal CompareSalesAmount { get; set; }

        /// <summary>
        /// 对比期销售数量
        /// </summary>
        public int CompareQuantity { get; set; }

        /// <summary>
        /// 平均单价
        /// </summary>
        public decimal AverageUnitPrice { get; set; }

        /// <summary>
        /// 对比期平均单价
        /// </summary>
        public decimal CompareAverageUnitPrice { get; set; }

        /// <summary>
        /// 当前期毛利额；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? GrossProfit { get; set; }

        /// <summary>
        /// 当前期毛利率；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? GrossMarginRate { get; set; }

        /// <summary>
        /// 对比期毛利额；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? CompareGrossProfit { get; set; }

        /// <summary>
        /// 对比期毛利率；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? CompareGrossMarginRate { get; set; }

        /// <summary>当前期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CostStatus { get; set; } = "NoActivity";

        /// <summary>同期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CompareCostStatus { get; set; } = "NoActivity";
    }

    /// <summary>
    /// 中国供应商分店销售数据 DTO
    /// </summary>
    public class ChinaSupplierStoreSalesDto
    {
        /// <summary>
        /// 统计开始日期
        /// </summary>
        public DateTime StartDate { get; set; }

        /// <summary>
        /// 统计结束日期
        /// </summary>
        public DateTime EndDate { get; set; }

        /// <summary>
        /// 供应商代码
        /// </summary>
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商名称
        /// </summary>
        public string SupplierName { get; set; } = string.Empty;

        /// <summary>
        /// 分店代码
        /// </summary>
        public string BranchCode { get; set; } = string.Empty;

        /// <summary>
        /// 分店名称
        /// </summary>
        public string BranchName { get; set; } = string.Empty;

        /// <summary>
        /// 总销售额
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// 总销售数量
        /// </summary>
        public int TotalQuantity { get; set; }

        /// <summary>
        /// 客单数
        /// </summary>
        public int OrderCount { get; set; }

        /// <summary>
        /// 客单价
        /// </summary>
        public decimal AverageTransaction { get; set; }

        /// <summary>
        /// 对比期间总销售额
        /// </summary>
        public decimal? CompareTotalAmount { get; set; }

        /// <summary>
        /// 对比期间客单数
        /// </summary>
        public int? CompareOrderCount { get; set; }

        /// <summary>
        /// 对比期间客单价
        /// </summary>
        public decimal? CompareAverageTransaction { get; set; }

        /// <summary>
        /// 销售额增长率
        /// </summary>
        public decimal? TotalAmountGrowth { get; set; }

        /// <summary>
        /// 当前期毛利额；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? GrossProfit { get; set; }

        /// <summary>
        /// 当前期毛利率；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? GrossMarginRate { get; set; }

        /// <summary>
        /// 同期毛利额；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? CompareGrossProfit { get; set; }

        /// <summary>
        /// 同期毛利率；任一参与统计行缺少成本时为 null
        /// </summary>
        public decimal? CompareGrossMarginRate { get; set; }

        /// <summary>当前期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CostStatus { get; set; } = "NoActivity";

        /// <summary>同期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CompareCostStatus { get; set; } = "NoActivity";
    }

    /// <summary>
    /// 单个分店的中国货（全部中国供应商）销售合计。
    /// 与中国供应商排行同源同口径；同期覆盖同期期间的全部中国供应商，不只限本期上榜的供应商。
    /// 不含客单数：同一张小票会同时买多个中国供应商的商品，跨供应商相加会重复计数。
    /// </summary>
    public class ChinaSupplierBranchTotalDto
    {
        /// <summary>统计开始日期</summary>
        public DateTime StartDate { get; set; }

        /// <summary>统计结束日期</summary>
        public DateTime EndDate { get; set; }

        /// <summary>分店代码</summary>
        public string BranchCode { get; set; } = string.Empty;

        /// <summary>分店名称</summary>
        public string BranchName { get; set; } = string.Empty;

        /// <summary>本期中国货销售额</summary>
        public decimal TotalAmount { get; set; }

        /// <summary>本期中国货销售数量</summary>
        public int TotalQuantity { get; set; }

        /// <summary>本期在该分店有销售的中国供应商数</summary>
        public int SupplierCount { get; set; }

        /// <summary>本期毛利；任一参与统计行缺少成本时为 null</summary>
        public decimal? GrossProfit { get; set; }

        /// <summary>本期毛利率；任一参与统计行缺少成本时为 null</summary>
        public decimal? GrossMarginRate { get; set; }

        /// <summary>当前期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CostStatus { get; set; } = "NoActivity";

        /// <summary>同期中国货销售额；未传同期日期时为 null</summary>
        public decimal? CompareTotalAmount { get; set; }

        /// <summary>同期中国货销售数量；未传同期日期时为 null</summary>
        public int? CompareTotalQuantity { get; set; }

        /// <summary>同期毛利；任一参与统计行缺少成本时为 null</summary>
        public decimal? CompareGrossProfit { get; set; }

        /// <summary>同期毛利率；任一参与统计行缺少成本时为 null</summary>
        public decimal? CompareGrossMarginRate { get; set; }

        /// <summary>同期成本状态：Complete、Missing 或 NoActivity。</summary>
        public string CompareCostStatus { get; set; } = "NoActivity";
    }

    /// <summary>
    /// 分页的带折扣信息销售商品明细 DTO
    /// </summary>
    public class PagedSalesProductDetailWithDiscountDto
    {
        /// <summary>
        /// 数据列表
        /// </summary>
        public List<SalesProductDetailWithDiscountDto> Data { get; set; } = new();

        /// <summary>
        /// 总记录数
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// 当前页码
        /// </summary>
        public int PageIndex { get; set; }

        /// <summary>
        /// 每页大小
        /// </summary>
        public int PageSize { get; set; }
    }

    /// <summary>
    /// Sales Detail 三栏查询的业务范围。
    /// </summary>
    public enum SalesDetailKind
    {
        Australia,
        China,
    }

    /// <summary>
    /// Sales Detail 单栏查询类型。
    /// </summary>
    public enum SalesDetailSection
    {
        Suppliers,
        Branches,
        Products,
        Summary,
    }

    /// <summary>
    /// 三栏统一行 DTO。金额为 AUD，比例为 0..1。
    /// </summary>
    public sealed class SalesDetailRowDto
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? ProductImage { get; set; }
        public decimal Revenue { get; set; }
        public decimal? CompareRevenue { get; set; }
        public int Quantity { get; set; }
        public int? CompareQuantity { get; set; }
        public int? OrderCount { get; set; }
        public int? CompareOrderCount { get; set; }
        public decimal? AverageTransaction { get; set; }
        public decimal? CompareAverageTransaction { get; set; }
        public decimal? AverageUnitPrice { get; set; }
        public decimal? CompareAverageUnitPrice { get; set; }
        public decimal? GrossProfit { get; set; }
        public decimal? CompareGrossProfit { get; set; }
        public decimal? GrossMarginRate { get; set; }
        public decimal? CompareGrossMarginRate { get; set; }
        public decimal? Share { get; set; }
        public decimal? CompareShare { get; set; }
        public decimal? ChinaShare { get; set; }
        public decimal? CompareChinaShare { get; set; }
    }

    /// <summary>
    /// Sales Detail 单栏响应数据。Products 的 Summary 仅代表当前页。
    /// </summary>
    public sealed class SalesDetailSectionResultDto
    {
        public List<SalesDetailRowDto> Rows { get; set; } = new();
        public int Total { get; set; }
        public SalesDetailRowDto? Summary { get; set; }
        public string? OrderCountNote { get; set; }
    }

    /// <summary>
    /// 澳洲供应商分店销售明细 DTO
    /// </summary>
    public class AustralianSupplierStoreSalesDetailDto
    {
        /// <summary>
        /// 销售日期
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// 分店代码
        /// </summary>
        public string BranchCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商代码
        /// </summary>
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商名称
        /// </summary>
        public string? SupplierName { get; set; }

        /// <summary>
        /// 总销售额
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// 总销售数量
        /// </summary>
        public int TotalQuantity { get; set; }

        /// <summary>
        /// 订单数量
        /// </summary>
        public int? OrderCount { get; set; }

        /// <summary>
        /// 更新时间
        /// </summary>
        public DateTime UpdateTime { get; set; }
    }

    /// <summary>
    /// 中国供应商分店销售明细 DTO
    /// </summary>
    public class ChinaSupplierStoreSalesDetailDto
    {
        /// <summary>
        /// 销售日期
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// 分店代码
        /// </summary>
        public string BranchCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商代码
        /// </summary>
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商名称
        /// </summary>
        public string? SupplierName { get; set; }

        /// <summary>
        /// 总销售额
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// 总销售数量
        /// </summary>
        public int TotalQuantity { get; set; }

        /// <summary>
        /// 订单数量
        /// </summary>
        public int? OrderCount { get; set; }

        /// <summary>
        /// 更新时间
        /// </summary>
        public DateTime UpdateTime { get; set; }
    }

    /// <summary>
    /// Executive Dashboard 分店业绩 DTO
    /// 用于 Executive Sales Intelligence 页面
    /// </summary>
    public class ExecutiveBranchPerformanceDto
    {
        /// <summary>
        /// 排名
        /// </summary>
        public int Rank { get; set; }

        /// <summary>
        /// 分店代码
        /// </summary>
        public string BranchCode { get; set; } = string.Empty;

        /// <summary>
        /// 分店名称
        /// </summary>
        public string BranchName { get; set; } = string.Empty;

        /// <summary>
        /// 当前期销售额
        /// </summary>
        public decimal Revenue { get; set; }

        /// <summary>
        /// 去年同期销售额
        /// </summary>
        public decimal RevenueLY { get; set; }

        /// <summary>
        /// 当前期订单数
        /// </summary>
        public int OrderCount { get; set; }

        /// <summary>
        /// 去年同期订单数
        /// </summary>
        public int OrderCountLY { get; set; }

        /// <summary>
        /// 当前期平均订单价值
        /// </summary>
        public decimal Aov { get; set; }

        /// <summary>
        /// 去年同期平均订单价值
        /// </summary>
        public decimal AovLY { get; set; }
    }

    /// <summary>
    /// Executive Dashboard 分店业绩响应。
    /// 统计补算超过接口短等待预算时，仍会返回当前快照，并显式标记完整性状态。
    /// </summary>
    public class ExecutiveBranchPerformanceResultDto
    {
        /// <summary>
        /// 当前可用的分店业绩快照
        /// </summary>
        public List<ExecutiveBranchPerformanceDto> Items { get; set; } = new();

        /// <summary>
        /// 统计缺口是否仍在后台补算
        /// </summary>
        public bool StatisticsPending { get; set; }

        /// <summary>
        /// 当前查询范围预期返回的分店数量
        /// </summary>
        public int StatisticsExpectedBranchCount { get; set; }

        /// <summary>
        /// 当前响应快照已包含的分店数量
        /// </summary>
        public int StatisticsSnapshotBranchCount { get; set; }
    }

    /// <summary>
    /// 营业额明细统一响应。明细统计不可将“有数组”误判为完整：
    /// 客户端只有在待完成为 false 且预期/快照数量相等时才可展示为完整结果。
    /// </summary>
    public class ExecutiveReportResultDto<T> : IReadOnlyList<T>
    {
        /// <summary>当前可用明细快照</summary>
        public List<T> Items { get; set; } = new();

        /// <summary>统计是否仍在补算或快照不完整</summary>
        public bool StatisticsPending { get; set; }

        /// <summary>当前查询范围预期明细项数量</summary>
        public int StatisticsExpectedItemCount { get; set; }

        /// <summary>当前响应实际包含的明细项数量</summary>
        public int StatisticsSnapshotItemCount { get; set; }

        // 保留只读枚举能力，减少既有服务端调用方的迁移摩擦；HTTP 契约仍明确使用 Items。
        public int Count => Items.Count;

        public T this[int index] => Items[index];

        public IEnumerator<T> GetEnumerator() => Items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Executive Dashboard 每小时流量 DTO
    /// 用于 Executive Sales Intelligence 页面的每小时流量密度展示
    /// </summary>
    public class ExecutiveHourlyTrafficDto
    {
        /// <summary>
        /// 小时 (格式: "HH:mm")
        /// </summary>
        public string Hour { get; set; } = string.Empty;

        /// <summary>
        /// 分店代码
        /// </summary>
        public string BranchCode { get; set; } = string.Empty;

        /// <summary>
        /// 分店名称
        /// </summary>
        public string BranchName { get; set; } = string.Empty;

        /// <summary>
        /// 当前期销售额
        /// </summary>
        public decimal Revenue { get; set; }

        /// <summary>
        /// 去年同期销售额
        /// </summary>
        public decimal RevenueLY { get; set; }

        /// <summary>
        /// 当前期订单数
        /// </summary>
        public int OrderCount { get; set; }

        /// <summary>
        /// 去年同期订单数
        /// </summary>
        public int OrderCountLY { get; set; }

        /// <summary>
        /// 占高峰期的百分比 (0-100)
        /// </summary>
        public int Percentage { get; set; }

        /// <summary>
        /// 是否为高峰时段
        /// </summary>
        public bool IsPeak { get; set; }
    }

    /// <summary>
    /// 分店每日营业额 DTO
    /// 用于移动端周/月营业额下钻明细
    /// </summary>
    public class BranchDailyPerformanceDto
    {
        /// <summary>
        /// 统计日期
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// 分店代码
        /// </summary>
        public string BranchCode { get; set; } = string.Empty;

        /// <summary>
        /// 分店名称
        /// </summary>
        public string BranchName { get; set; } = string.Empty;

        /// <summary>
        /// 当前期销售额
        /// </summary>
        public decimal Revenue { get; set; }

        /// <summary>
        /// 去年同期销售额
        /// </summary>
        public decimal RevenueLY { get; set; }

        /// <summary>
        /// 当前期订单数
        /// </summary>
        public int OrderCount { get; set; }

        /// <summary>
        /// 去年同期订单数
        /// </summary>
        public int OrderCountLY { get; set; }
    }

    /// <summary>
    /// 周业绩层级数据 DTO
    /// 用于 Weekly Performance Hierarchy 组件
    /// 支持周、分店、日期三层嵌套结构
    /// </summary>
    public class WeeklyPerformanceHierarchyDto
    {
        /// <summary>
        /// 唯一标识键
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// 层级类型: week | branch | date
        /// </summary>
        public string Level { get; set; } = string.Empty;

        /// <summary>
        /// 层级显示名称 (如: 2023-W42, Nexus Downtown, 2023-10-20)
        /// </summary>
        public string Hierarchy { get; set; } = string.Empty;

        /// <summary>
        /// 当前期销售额
        /// </summary>
        public decimal Revenue { get; set; }

        /// <summary>
        /// 去年同期销售额
        /// </summary>
        public decimal RevenueLY { get; set; }

        /// <summary>
        /// 当前期订单数
        /// </summary>
        public int Orders { get; set; }

        /// <summary>
        /// 去年同期订单数
        /// </summary>
        public int OrdersLY { get; set; }

        /// <summary>
        /// 当前期平均订单价值
        /// </summary>
        public decimal Aov { get; set; }

        /// <summary>
        /// 去年同期平均订单价值
        /// </summary>
        public decimal AovLY { get; set; }

        /// <summary>
        /// 同比变化率 (%)
        /// </summary>
        public decimal? YoYChange { get; set; }

        /// <summary>
        /// 子节点列表
        /// </summary>
        public List<WeeklyPerformanceHierarchyDto>? Children { get; set; }
    }

    /// <summary>
    /// 分店销售聚合数据 DTO
    /// 用于 SalesDetailAnalysisV2 门店分布卡，直接返回分店级别的聚合数据
    /// 避免前端接收大量明细数据后再进行聚合
    /// </summary>
    public class BranchSalesAggregateDto
    {
        /// <summary>
        /// 分店代码
        /// </summary>
        public string BranchCode { get; set; } = string.Empty;

        /// <summary>
        /// 分店名称
        /// </summary>
        public string BranchName { get; set; } = string.Empty;

        /// <summary>
        /// 当前期总销售额
        /// </summary>
        public decimal TotalRevenue { get; set; }

        /// <summary>
        /// 去年同期总销售额
        /// </summary>
        public decimal TotalRevenueLY { get; set; }

        /// <summary>
        /// 当前期总销售数量
        /// </summary>
        public int TotalQuantity { get; set; }

        /// <summary>
        /// 去年同期总销售数量
        /// </summary>
        public int TotalQuantityLY { get; set; }

        /// <summary>
        /// 当前期订单数
        /// </summary>
        public int OrderCount { get; set; }

        /// <summary>
        /// 去年同期订单数
        /// </summary>
        public int OrderCountLY { get; set; }

        /// <summary>
        /// 当前期 Hot Bargain 供应商 (#200) 销售额
        /// </summary>
        public decimal HbRevenue { get; set; }

        /// <summary>
        /// 去年同期 Hot Bargain 供应商 (#200) 销售额
        /// </summary>
        public decimal HbRevenueLY { get; set; }
    }

    /// <summary>
    /// 紧凑销售看板：分店、国内供应商和商品明细使用同一统计口径。
    /// </summary>
    public sealed class CompactSalesBoardDto
    {
        /// <summary>分店栏：受国内供应商、商品的选中项约束，不受已选分店自身约束。</summary>
        public List<CompactSalesBoardStoreDto> Stores { get; set; } = new();
        /// <summary>国内供应商栏：受分店、商品的选中项约束，不受已选供应商自身约束。</summary>
        public List<CompactSalesBoardChinaSupplierDto> ChinaSuppliers { get; set; } = new();
        /// <summary>商品明细：受分店、国内供应商的选中项与关键词约束，服务端全量排序后分页。</summary>
        public PagedCompactSalesBoardProductDto ProductDetails { get; set; } = new();
        /// <summary>KPI 汇总：同时受三个维度的选中项约束；Overall* 仅受授权分店范围约束。</summary>
        public CompactSalesBoardSummaryDto Summary { get; set; } = new();
        public string StatisticStatus { get; set; } = string.Empty;
        public string? StatisticMessage { get; set; }
        public DateTime? StatisticUpdatedAt { get; set; }
        /// <summary>本次是否复用了服务端已缓存的聚合立方体。</summary>
        public bool FromCache { get; set; }
    }

    public sealed class CompactSalesBoardSummaryDto
    {
        public decimal TotalAmount { get; set; }
        public int TotalQuantity { get; set; }
        public int ProductCount { get; set; }
        public int StoreCount { get; set; }
        public int SupplierCount { get; set; }
        public decimal OverallAmount { get; set; }
        public int OverallQuantity { get; set; }
    }

    /// <summary>
    /// 紧凑销售看板查询条件。BranchCodes 是授权分店范围（null 表示全部），
    /// Selected* 是页面上的联动选中项，二者含义不同，不能互相替代。
    /// </summary>
    public sealed class CompactSalesBoardQuery
    {
        public const string SortByAmount = "amount";
        public const string SortByQuantity = "quantity";
        public const string SortByUnitPrice = "unitPrice";
        public const string SortByItemNumber = "itemNumber";

        public DateRangeDto DateRange { get; set; } = new();
        public List<string>? BranchCodes { get; set; }
        public string? SelectedBranchCode { get; set; }
        public string? SelectedChinaSupplierCode { get; set; }
        public string? SelectedProductCode { get; set; }
        public string? Keyword { get; set; }
        public string? SortField { get; set; }
        public string? SortOrder { get; set; }
        public int PageIndex { get; set; } = 1;
        public int PageSize { get; set; } = 80;
        public bool ForceRefresh { get; set; }
    }

    public sealed class CompactSalesBoardStoreDto
    {
        public string BranchCode { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public int TotalQuantity { get; set; }
        public decimal DomesticSupplierAmount { get; set; }
        /// <summary>该分店在区间内的总营业额（营业额日报同一口径，含全部供应商的商品），分店栏占比的分母；无统计时为 0。</summary>
        public decimal BranchTotalAmount { get; set; }
        public string AustralianSupplierCode { get; set; } = "200";
        public string AustralianSupplierName { get; set; } = "200-hotbargain";
        /// <summary>该分店在当前筛选下有销售的商品款数。</summary>
        public int ProductCount { get; set; }
    }

    public sealed class CompactSalesBoardChinaSupplierDto
    {
        public string SupplierCode { get; set; } = string.Empty;
        public string SupplierName { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public int TotalQuantity { get; set; }
        /// <summary>该供应商在当前筛选下有销售的商品款数。</summary>
        public int ProductCount { get; set; }
    }

    public sealed class PagedCompactSalesBoardProductDto
    {
        public List<CompactSalesBoardProductDto> Data { get; set; } = new();
        public int Total { get; set; }
        public int PageIndex { get; set; }
        public int PageSize { get; set; }
        /// <summary>商品栏在分店、供应商约束下（关键词过滤前）的营业额合计，作为商品占比的分母。</summary>
        public decimal ScopeAmount { get; set; }
    }

    public sealed class CompactSalesBoardProductDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? ProductImage { get; set; }
        public string? ProductName { get; set; }
        public string? ChinaSupplierCode { get; set; }
        public string? ChinaSupplierName { get; set; }
        public int TotalQuantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalAmount { get; set; }
    }

    /// <summary>
    /// Best Seller 商品 DTO
    /// </summary>
    public class BestSellerProductDto
    {
        /// <summary>
        /// 商品代码
        /// </summary>
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>
        /// 商品货号
        /// </summary>
        public string? ItemNumber { get; set; }

        /// <summary>
        /// 商品条码
        /// </summary>
        public string? Barcode { get; set; }

        /// <summary>
        /// 商品图片URL
        /// </summary>
        public string? ProductImage { get; set; }

        /// <summary>
        /// 商品名称
        /// </summary>
        public string? ProductName { get; set; }

        /// <summary>
        /// 销售数量
        /// </summary>
        public int Quantity { get; set; }

        /// <summary>
        /// 销售金额
        /// </summary>
        public decimal SalesAmount { get; set; }

        /// <summary>
        /// 成本金额
        /// </summary>
        public decimal? TotalCost { get; set; }

        /// <summary>
        /// 毛利额
        /// </summary>
        public decimal? GrossProfit { get; set; }

        /// <summary>
        /// 毛利率
        /// </summary>
        public decimal? GrossMarginRate { get; set; }

        /// <summary>
        /// 成本来源
        /// </summary>
        public string? CostSource { get; set; }

        /// <summary>
        /// 排名
        /// </summary>
        public int Rank { get; set; }

        /// <summary>
        /// 仓库库存表上下架状态
        /// </summary>
        public bool? IsActive { get; set; }

        /// <summary>
        /// 仓库库存表最小订货量
        /// </summary>
        public int? MinOrderQuantity { get; set; }

        /// <summary>
        /// 参与当前统计范围的分店数量
        /// </summary>
        public int BranchSalesCount { get; set; }

        /// <summary>
        /// 当前统计范围内的分店销量明细
        /// </summary>
        public List<BestSellerBranchSaleDto> BranchSales { get; set; } = new();

        /// <summary>
        /// 商品统计刷新状态
        /// </summary>
        public string? StatisticStatus { get; set; }
    }

    /// <summary>
    /// Best Seller 分店销量 DTO
    /// </summary>
    public class BestSellerBranchSaleDto
    {
        /// <summary>
        /// 分店代码
        /// </summary>
        public string BranchCode { get; set; } = string.Empty;

        /// <summary>
        /// 分店名称
        /// </summary>
        public string? BranchName { get; set; }

        /// <summary>
        /// 销售数量
        /// </summary>
        public int Quantity { get; set; }

        /// <summary>
        /// 销售金额
        /// </summary>
        public decimal SalesAmount { get; set; }

        /// <summary>
        /// 成本金额
        /// </summary>
        public decimal? TotalCost { get; set; }

        /// <summary>
        /// 毛利额
        /// </summary>
        public decimal? GrossProfit { get; set; }

        /// <summary>
        /// 毛利率
        /// </summary>
        public decimal? GrossMarginRate { get; set; }

        /// <summary>
        /// 成本来源
        /// </summary>
        public string? CostSource { get; set; }
    }

    /// <summary>
    /// Best Sellers 响应 DTO
    /// </summary>
    public class BestSellerResponseDto
    {
        /// <summary>
        /// 商品列表
        /// </summary>
        public List<BestSellerProductDto> Products { get; set; } = new();

        /// <summary>
        /// 总记录数
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// 当前页码
        /// </summary>
        public int PageIndex { get; set; }

        /// <summary>
        /// 每页记录数
        /// </summary>
        public int PageSize { get; set; }

        /// <summary>
        /// 总页数
        /// </summary>
        public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)Total / PageSize) : 0;

        /// <summary>
        /// 当前日期范围内商品统计状态
        /// </summary>
        public string? StatisticStatus { get; set; }

        /// <summary>
        /// 统计状态提示
        /// </summary>
        public string? StatisticMessage { get; set; }
    }

    /// <summary>
    /// 销售统计新鲜度信息
    /// </summary>
    public class StatisticsFreshnessDto
    {
        /// <summary>
        /// 最近一次成功统计完成时间（UTC）
        /// </summary>
        public DateTime? LastSuccessfulAtUtc { get; set; }

        /// <summary>
        /// 最近一次统计运行状态
        /// </summary>
        public string LatestRunStatus { get; set; } = "NeverRun";
    }
}
