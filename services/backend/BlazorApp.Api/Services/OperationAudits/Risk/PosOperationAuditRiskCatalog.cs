using LegacyRules = BlazorApp.Api.Features.LegacyEmployeeLogs.Risk.LegacyEmployeeLogRiskCatalog.Rules;

namespace BlazorApp.Api.Services.OperationAudits.Risk;

/// <summary>
/// 新收银（WPF / 手持 / iPad）操作审计的风险口径：危险操作、金额让利与异常规则编号。
/// 规则编号与老收银共用（前端共用一套文案与筛选），另加新收银独有的「紧急覆盖」。
/// 口径按 2026-10-01 生产抽样（1013 约 7 天：3,173 笔销售、31 次手动开钱箱、279 次删除、64 次单品折扣）校准：
/// 新收银每次现金收款都会自动开钱箱（reason PAYMENT_COMPLETE、带 order_guid），只有不带订单的手动开钱箱才算危险；
/// 单品折扣常用 50% 快捷折扣，所以大额折扣另加让利金额下限。
/// </summary>
public static class PosOperationAuditRiskCatalog
{
    /// <summary>规则判定逻辑或依据字段变化时递增，存进标记表便于区分旧口径的命中。</summary>
    public const int RuleVersion = 1;

    public const string Succeeded = "Succeeded";

    public const string CashDrawerOpen = "CASH_DRAWER_OPEN";
    public const string SaleComplete = "SALE_COMPLETE";
    public const string ReturnRefundComplete = "RETURN_REFUND_COMPLETE";
    public const string PaymentTenderAdd = "PAYMENT_TENDER_ADD";
    public const string CartItemRemove = "CART_ITEM_REMOVE";
    public const string CartClear = "CART_CLEAR";
    public const string CartItemQuantityChange = "CART_ITEM_QUANTITY_CHANGE";
    public const string CartItemPriceChange = "CART_ITEM_PRICE_CHANGE";
    public const string CartLineDiscountChange = "CART_LINE_DISCOUNT_CHANGE";
    public const string CartOrderDiscountChange = "CART_ORDER_DISCOUNT_CHANGE";
    public const string OrderHold = "ORDER_HOLD";
    public const string OrderRecall = "ORDER_RECALL";
    public const string OrderCancel = "ORDER_CANCEL";
    public const string SaleVoid = "SALE_VOID";
    public const string ReceiptReprint = "RECEIPT_REPRINT";

    /// <summary>
    /// 危险操作：直接改变应收金额、现金或收银环境。重打印、自动开钱箱不在其中（日常动作，会淹没真正的危险操作）；
    /// 手动开钱箱单独判断（<see cref="IsDanger"/>）。
    /// </summary>
    public static readonly IReadOnlyList<string> DangerOperations =
    [
        CartItemRemove,
        CartClear,
        CartItemPriceChange,
        CartLineDiscountChange,
        CartOrderDiscountChange,
        ReturnRefundComplete,
        SaleVoid,
        OrderCancel,
        "CARD_PAYMENT_SUPERVISOR_RESOLUTION",
        "PERMISSION_OVERRIDE",
        "API_SERVER_CHANGE",
        "DEVICE_REREGISTER",
        "REMOTE_MAINTENANCE_INSTALL",
        "CATALOG_RESET",
        "TEST_SALES_DATA_RESET",
    ];

    /// <summary>应收减少 = 操作前后实收差额的操作；退款另按退款金额计。</summary>
    public static readonly IReadOnlyList<string> CartAmountOperations =
    [
        CartItemRemove,
        CartClear,
        CartItemPriceChange,
        CartLineDiscountChange,
        CartOrderDiscountChange,
    ];

    /// <summary>判定开钱箱、收款后删除、折扣、频繁删除所需的操作序列（扫描读取口径）。</summary>
    public static readonly IReadOnlyList<string> SequenceOperations =
    [
        CashDrawerOpen,
        SaleComplete,
        ReturnRefundComplete,
        PaymentTenderAdd,
        CartItemRemove,
        CartClear,
        CartItemQuantityChange,
        CartItemPriceChange,
        CartLineDiscountChange,
        CartOrderDiscountChange,
        OrderHold,
        OrderRecall,
        OrderCancel,
        SaleVoid,
    ];

    /// <summary>需要商品行明细（单价、折扣额）才能判定幅度的操作。</summary>
    public static readonly IReadOnlyList<string> ItemDetailOperations =
    [
        CartItemPriceChange,
        CartLineDiscountChange,
    ];

    public static class Rules
    {
        public const string NoSaleDrawer = LegacyRules.NoSaleDrawer;
        public const string DeleteAfterCheckout = LegacyRules.DeleteAfterCheckout;
        public const string BigDiscount = LegacyRules.BigDiscount;
        public const string BurstDelete = LegacyRules.BurstDelete;
        public const string RepeatReprint = LegacyRules.RepeatReprint;
        public const string OffHours = LegacyRules.OffHours;

        /// <summary>新收银独有：紧急覆盖（离线或权限受阻时的应急放行）下的操作，每条都需要核查。</summary>
        public const string EmergencyOverride = "emergencyOverride";
    }

    public static readonly IReadOnlyList<string> AllRules =
    [
        Rules.NoSaleDrawer,
        Rules.DeleteAfterCheckout,
        Rules.BigDiscount,
        Rules.BurstDelete,
        Rules.RepeatReprint,
        Rules.OffHours,
        Rules.EmergencyOverride,
    ];

    private static readonly HashSet<string> DangerSet = new(DangerOperations, StringComparer.Ordinal);
    private static readonly HashSet<string> CartAmountSet = new(CartAmountOperations, StringComparer.Ordinal);

    /// <summary>手动开钱箱（不关联订单）也算危险；随收款自动打开的钱箱不算。</summary>
    public static bool IsDanger(string? operationType, string? orderGuid)
    {
        var type = operationType?.Trim();
        if (string.IsNullOrEmpty(type))
        {
            return false;
        }
        return DangerSet.Contains(type) || (type == CashDrawerOpen && string.IsNullOrWhiteSpace(orderGuid));
    }

    /// <summary>
    /// 应收减少金额（正数 = 少收）：购物车改动按操作前后实收差额，退款按退款金额。
    /// 只对危险操作给出；算不出或为 0 时返回 null。
    /// </summary>
    public static decimal? AmountImpact(
        string? operationType,
        string? outcome,
        decimal? beforeActual,
        decimal? afterActual,
        decimal? paymentAmount
    )
    {
        if (!string.Equals(outcome, Succeeded, StringComparison.Ordinal))
        {
            return null;
        }
        var type = operationType?.Trim() ?? string.Empty;
        decimal? amount = null;
        if (CartAmountSet.Contains(type) && beforeActual is { } before && afterActual is { } after)
        {
            amount = before - after;
        }
        else if (type == ReturnRefundComplete && paymentAmount is { } refund)
        {
            // 退款金额记为负数（-5.00），应收减少取绝对值。
            amount = Math.Abs(refund);
        }
        return amount is { } value && value != 0m ? decimal.Round(value, 2) : null;
    }
}
