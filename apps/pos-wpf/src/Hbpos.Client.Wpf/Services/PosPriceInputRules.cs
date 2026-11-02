namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 收银员手工输入单价的统一上限：开价商品、购物车改价、特殊商品改零售价、分期单改商品单价共用。
/// 2026-10-31 / 11-02 1014 店把开价商品输成 1 亿、10 亿，操作日志入库时超出 POSM decimal(10,2) 而卡住重传，
/// 因此按业务要求限制在 9999 以内（含 9999）；只拦新输入，不影响已在购物车、挂单或快照里的行。
/// </summary>
public static class PosPriceInputRules
{
    public const decimal MaxUnitPrice = 9999m;

    public static bool IsWithinMaximum(decimal unitPrice) => unitPrice <= MaxUnitPrice;
}
