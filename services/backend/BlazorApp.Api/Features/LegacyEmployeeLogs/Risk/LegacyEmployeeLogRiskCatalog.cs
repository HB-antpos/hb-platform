namespace BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;

/// <summary>
/// 老收银操作日志的风险口径：操作名称、危险操作清单与异常规则编号。
/// 操作名称来自旧收银写入的中文常量（2026-10-01 生产抽样 1003/1005/1008 近 7 天核对）；
/// 旧收银不写「支付完成」，结账只记「结账」（开始结账），正常收款开钱箱不单独记录。
/// </summary>
public static class LegacyEmployeeLogRiskCatalog
{
    /// <summary>规则判定逻辑或依据字段变化时递增，存进标记表便于区分旧口径的命中。</summary>
    public const int RuleVersion = 1;

    public const string OpenDrawer = "开钱箱";
    public const string IdentityConfirm = "身份确认";
    public const string Checkout = "结账";
    public const string DeleteItem = "删除商品";
    public const string AddItem = "添加商品";
    public const string AddNoCodeItem = "添加无码商品";
    public const string Hold = "挂单";
    public const string ResumeHold = "恢复挂单";
    public const string Reprint = "重打印";
    public const string ChangePrice = "修改商品价格";
    public const string ChangeDiscount = "修改商品折扣";
    public const string ChangeAllDiscount = "修改所有商品折扣";
    public const string NoReceiptReturn = "无小票退货成功";
    public const string ReturnAuthorization = "退货授权";

    /// <summary>
    /// 危险操作：直接改变应收金额或现金。重打印不在其中——生产一周约 2,500 次，是正常收银动作，
    /// 留在清单里会让危险计数八成都是重打印；同一订单反复重打印由「重复重打印」规则兜底。
    /// </summary>
    public static readonly IReadOnlyList<string> DangerOperations =
    [
        DeleteItem,
        ChangePrice,
        ChangeDiscount,
        ChangeAllDiscount,
        OpenDrawer,
        NoReceiptReturn,
        ReturnAuthorization,
    ];

    /// <summary>判定开钱箱、结账后删除、频繁删除所需的操作序列（只读索引列，不回表）。</summary>
    public static readonly IReadOnlyList<string> SequenceOperations =
    [
        OpenDrawer,
        IdentityConfirm,
        Checkout,
        DeleteItem,
        AddItem,
        AddNoCodeItem,
        Hold,
        ResumeHold,
    ];

    /// <summary>需要读详情文本的操作：算金额或判断折扣幅度。生产每店每天几十条，回表代价可控。</summary>
    public static readonly IReadOnlyList<string> DetailOperations =
    [
        DeleteItem,
        ChangePrice,
        ChangeDiscount,
        ChangeAllDiscount,
        NoReceiptReturn,
    ];

    public static class Rules
    {
        public const string NoSaleDrawer = "noSaleDrawer";
        public const string DeleteAfterCheckout = "deleteAfterCheckout";
        public const string BigDiscount = "bigDiscount";
        public const string BurstDelete = "burstDelete";
        public const string RepeatReprint = "repeatReprint";
        public const string OffHours = "offHours";
    }

    public static readonly IReadOnlyList<string> AllRules =
    [
        Rules.NoSaleDrawer,
        Rules.DeleteAfterCheckout,
        Rules.BigDiscount,
        Rules.BurstDelete,
        Rules.RepeatReprint,
        Rules.OffHours,
    ];

    private static readonly HashSet<string> DangerSet = new(DangerOperations, StringComparer.Ordinal);

    public static bool IsDanger(string? operation) =>
        operation != null && DangerSet.Contains(operation.Trim());
}
