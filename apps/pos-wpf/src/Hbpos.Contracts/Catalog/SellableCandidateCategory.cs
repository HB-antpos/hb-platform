namespace Hbpos.Contracts.Catalog;

/// <summary>
/// 同一个查询码下，一个候选属于哪一类来源：主条码（含货号）、套装码，或其它（清货价、普通多码等）。
/// 用来区分"同一商品、同一个码"的两条候选：套装码与主条码撞码且价格不同时，收银员需要选。
/// </summary>
public enum SellableCandidateCategory
{
    /// <summary>主条码 / 货号：价格来源是商品基础价或门店零售价。</summary>
    Main = 0,

    /// <summary>套装码：价格来源是套装码价，或"套装码 + 门店多码价"。</summary>
    Set = 1,

    /// <summary>清货价、普通多码等：有意覆盖主条码价格，不参与"主条码 vs 套装"的选择。</summary>
    Other = 2
}

public static class SellableCandidateCategories
{
    private const string SetLabelPrefix = "set";

    /// <summary>
    /// 按价格来源与来源标签判定类别。标签前缀 <c>set</c> 覆盖服务端写的三种套装标签：
    /// set、set-follow-main、set-store-multi-code（后者的 PriceSource 是 StoreMultiCodeProduct，不能只看枚举）。
    /// </summary>
    public static SellableCandidateCategory Of(PriceSourceKind source, string? priceSourceLabel)
    {
        if (source is PriceSourceKind.ProductBase or PriceSourceKind.StoreRetailPrice)
        {
            return SellableCandidateCategory.Main;
        }

        if (source == PriceSourceKind.ProductSetCode)
        {
            return SellableCandidateCategory.Set;
        }

        if (source == PriceSourceKind.StoreMultiCodeProduct &&
            (priceSourceLabel ?? string.Empty).TrimStart().StartsWith(SetLabelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return SellableCandidateCategory.Set;
        }

        return SellableCandidateCategory.Other;
    }

    public static SellableCandidateCategory Of(SellableItemDto item) =>
        Of(item.PriceSource, item.PriceSourceLabel);

    /// <summary>
    /// 两个候选是否"同一商品的主条码 vs 套装码"这对组合（顺序无关）。
    /// 购物车合并行、远程价格回写都只对这一对做额外区分，清货价覆盖等原有口径保持不变。
    /// </summary>
    public static bool IsMainSetPair(SellableCandidateCategory left, SellableCandidateCategory right) =>
        (left == SellableCandidateCategory.Main && right == SellableCandidateCategory.Set) ||
        (left == SellableCandidateCategory.Set && right == SellableCandidateCategory.Main);

    /// <summary>
    /// 两个候选的有效价格是否不同：标价或折扣率任一不同都算。
    /// 折扣率 null 与 0 等价（都表示没有目录折扣）。
    /// </summary>
    public static bool HasDifferentEffectivePrice(SellableItemDto left, SellableItemDto right) =>
        left.RetailPrice != right.RetailPrice ||
        (left.DiscountRate ?? 0m) != (right.DiscountRate ?? 0m);
}
