using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    public enum OrderType
    {
        All = -1,
        Pending = 0,
        Paid = 1,
        Cancelled = 2,
        Refunded = 3,
        Installment = 4
    }

    public enum OrderStatus
    {
        Pending = 0,
        Paid = 1,
        Cancelled = 2,
        Refunded = 3,
        Installment = 4
    }

    /// <summary>
    /// POSM 销售订单查询参数
    /// </summary>
    public class PosmSalesOrderQueryParams
    {
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string? BranchCode { get; set; }
        public List<string>? BranchCodes { get; set; }
        public string? DeviceCode { get; set; }
        public OrderType? OrderType { get; set; }
        public string? Keyword { get; set; }
        public string? OrderGuidKeyword { get; set; }
        public string? DeviceCodeKeyword { get; set; }
        public TimeSpan? TimeStart { get; set; }
        public TimeSpan? TimeEnd { get; set; }
        public int? SkuCountMin { get; set; }
        public int? SkuCountMax { get; set; }
        public int? ItemCountMin { get; set; }
        public int? ItemCountMax { get; set; }
        /// <summary>件数（明细数量之和）区间；需逐单汇总明细，全部分店时受 7 天上限约束。</summary>
        public int? QuantityMin { get; set; }
        public int? QuantityMax { get; set; }
        public decimal? TotalAmountMin { get; set; }
        public decimal? TotalAmountMax { get; set; }
        public decimal? DiscountAmountMin { get; set; }
        public decimal? DiscountAmountMax { get; set; }
        public decimal? ActualPayMin { get; set; }
        public decimal? ActualPayMax { get; set; }
        /// <summary>
        /// 退货筛选：null 不限；any 含退货商品的订单（旧 POS 退款单 Status=3，或新 POS 订单里夹带的退货记录）；
        /// partial 含退货但整单金额不为负（橙）；full 含退货且整单金额为负（红）。取值见 <c>PosmSalesOrderListRules.NormalizeRefundFilter</c>。
        /// </summary>
        public string? RefundFilter { get; set; }
        public string? SortField { get; set; }
        public string? SortDirection { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }

    /// <summary>
    /// POSM 销售订单 DTO
    /// </summary>
    public class PosmSalesOrderDto
    {
        public string? OrderGuid { get; set; }
        public string? BranchCode { get; set; }
        public string? BranchName { get; set; }
        public string? ABN { get; set; }
        public string? BrandName { get; set; }
        public string? DeviceCode { get; set; }
        public DateTime? OrderTime { get; set; }
        public int? SkuCount { get; set; }
        /// <summary>POS 写入的 ItemCount 实际是明细行数，不是件数。</summary>
        public int? ItemCount { get; set; }
        /// <summary>件数：明细数量之和，仅列表查询聚合返回。</summary>
        public int? QuantityTotal { get; set; }
        public decimal? TotalAmount { get; set; }
        public decimal? DiscountAmount { get; set; }
        public decimal? ActualAmount { get; set; }
        public int? Status { get; set; }
        /// <summary>
        /// 订单是否含退货商品：旧 POS 的退款单（Status=3），或新 POS 订单里夹带了退货记录（sales_return_record）。
        /// 仅列表当前页与订单详情返回；整单金额为正是「部分退款」（橙），为负是「整单退款」（红）。
        /// </summary>
        public bool HasRefundItems { get; set; }
        /// <summary>支付方式（去重、升序），仅列表当前页返回：1 现金、2 刷卡、3 代金券。</summary>
        public List<int>? PaymentMethods { get; set; }
        /// <summary>关键词命中的明细商品，仅带关键词的列表查询返回。</summary>
        public List<PosmSalesOrderMatchedProductDto>? MatchedProducts { get; set; }
    }

    /// <summary>按订单状态汇总的单数与金额；汇总不受状态筛选影响，页面据此展示各状态并切换。</summary>
    public class PosmSalesOrderStatusSummaryDto
    {
        public int? Status { get; set; }
        public int OrderCount { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal DiscountAmount { get; set; }
    }

    /// <summary>收银记录列表结果：分页数据加按状态汇总（Total 为当前状态筛选下的单数）。</summary>
    public class PosmSalesOrderListResultDto : PagedListReactDto<PosmSalesOrderDto>
    {
        public List<PosmSalesOrderStatusSummaryDto> Summary { get; set; } = new();
    }

    /// <summary>
    /// POSM 销售订单明细 DTO
    /// </summary>
    public class PosmSalesOrderDetailDto
    {
        public string? ProductImage { get; set; }
        public string? ProductCode { get; set; }
        /// <summary>货号来自商品主档，POSM 明细本身不存；主档没有对应商品时为空。</summary>
        public string? ItemNumber { get; set; }
        public string? ProductName { get; set; }
        public int? Quantity { get; set; }
        public decimal? UnitPrice { get; set; }
        public decimal? DiscountAmount { get; set; }
        public decimal? ActualAmount { get; set; }
    }

    /// <summary>
    /// POSM 支付明细 DTO
    /// </summary>
    public class PosmPaymentDetailDto
    {
        public DateTime? PaymentTime { get; set; }
        public int? PaymentMethod { get; set; }
        public string? PaymentMethodName { get; set; }
        public decimal? Amount { get; set; }
    }

    /// <summary>
    /// POSM 销售订单详情（含明细和支付）
    /// </summary>
    public class PosmSalesOrderDetailResponse
    {
        public PosmSalesOrderDto? Order { get; set; }
        public List<PosmSalesOrderDetailDto>? OrderDetails { get; set; }
        public List<PosmPaymentDetailDto>? PaymentDetails { get; set; }
    }

    /// <summary>
    /// 移动端销售订单查询参数：区间必填并受 30 天上限约束，只保留移动端页面用到的筛选项。
    /// 日期使用 YYYY-MM-DD 字符串，避免设备时区把 DateTime 反序列化成前一天。
    /// </summary>
    public class PosmSalesOrderMobileQueryDto
    {
        public string? StartDate { get; set; }
        public string? EndDate { get; set; }
        public List<string>? BranchCodes { get; set; }
        public OrderType? OrderType { get; set; }
        public string? Keyword { get; set; }
        /// <summary>退货筛选：any / partial / full，其余按不限处理；与 OrderType 可叠加。</summary>
        public string? RefundFilter { get; set; }
        /// <summary>按下单时间排序：asc / desc，缺省 desc（最新在前）。</summary>
        public string? SortDirection { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }

    /// <summary>关键词命中的明细商品；按货号/条码/商品名搜索时告诉用户这单为什么被搜出来。</summary>
    public class PosmSalesOrderMatchedProductDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? ProductName { get; set; }
        public string? Barcode { get; set; }
        public int Quantity { get; set; }
    }

    public class PosmSalesOrderMobileItemDto
    {
        public string? OrderGuid { get; set; }
        public string? BranchCode { get; set; }
        public string? BranchName { get; set; }
        public string? DeviceCode { get; set; }
        public DateTime? OrderTime { get; set; }
        public int? SkuCount { get; set; }
        public int? ItemCount { get; set; }
        public int? QuantityTotal { get; set; }
        public decimal? TotalAmount { get; set; }
        public decimal? DiscountAmount { get; set; }
        public decimal? ActualAmount { get; set; }
        public int? Status { get; set; }
        /// <summary>订单含退货商品（退款单，或订单里夹带了退货记录）；颜色由整单金额正负决定。</summary>
        public bool HasRefundItems { get; set; }
        public List<PosmSalesOrderMatchedProductDto> MatchedProducts { get; set; } = new();

        public static PosmSalesOrderMobileItemDto From(
            PosmSalesOrderDto source,
            List<PosmSalesOrderMatchedProductDto>? matchedProducts
        ) =>
            new()
            {
                OrderGuid = source.OrderGuid,
                BranchCode = source.BranchCode,
                BranchName = source.BranchName,
                DeviceCode = source.DeviceCode,
                OrderTime = source.OrderTime,
                SkuCount = source.SkuCount,
                ItemCount = source.ItemCount,
                QuantityTotal = source.QuantityTotal,
                TotalAmount = source.TotalAmount,
                DiscountAmount = source.DiscountAmount,
                ActualAmount = source.ActualAmount,
                Status = source.Status,
                HasRefundItems = source.HasRefundItems,
                MatchedProducts = matchedProducts ?? new(),
            };
    }

    public class PosmSalesOrderMobileRangeDto
    {
        public string StartDate { get; set; } = string.Empty;
        public string EndDate { get; set; } = string.Empty;
        public int DayCount { get; set; }
    }

    public class PosmSalesOrderMobileListDto
    {
        public List<PosmSalesOrderMobileItemDto> Items { get; set; } = new();
        public int Total { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        /// <summary>all-stores / authorized-stores，与仓库商品进销查询的口径一致。</summary>
        public string Scope { get; set; } = "authorized-stores";
        public PosmSalesOrderMobileRangeDto Range { get; set; } = new();
        public string SortDirection { get; set; } = "desc";
    }

    public class PosmSalesOrderBranchDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
    }

    public class PosmSalesOrderMobileBranchesDto
    {
        public string Scope { get; set; } = "authorized-stores";
        public List<PosmSalesOrderBranchDto> Branches { get; set; } = new();
    }
}
