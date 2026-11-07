using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 把服务端下发的码冲突候选并入本地目录。主目录每个查询码只有一个候选，
/// 冲突码补上其它候选后，扫码精确命中多条即走已有的"多结果选择"弹窗，由收银员决定卖哪个。
/// 候选有两种：落到不同商品的；以及同一商品的"主条码 vs 套装码"撞码且价格不同的。
/// </summary>
public static class CatalogCodeConflictMerger
{
    public static IReadOnlyList<SellableItemDto> Merge(
        IReadOnlyList<SellableItemDto> catalogItems,
        IReadOnlyList<SellableItemDto> codeConflictItems)
    {
        if (codeConflictItems.Count == 0)
        {
            return catalogItems;
        }

        var catalogCodes = new HashSet<(string StoreCode, string LookupCode)>();
        // 已在索引里的候选：以（门店、码、商品、类别）识别，同一商品的主条码与套装码是两个候选。
        var presentCandidates = new HashSet<(string StoreCode, string LookupCode, string ProductCode, SellableCandidateCategory Category)>();
        var presentByProduct = new Dictionary<(string StoreCode, string LookupCode, string ProductCode), List<SellableItemDto>>();
        foreach (var item in catalogItems)
        {
            var storeCode = Normalize(item.StoreCode);
            var lookupCode = Normalize(item.LookupCode);
            catalogCodes.Add((storeCode, lookupCode));
            RecordPresent(presentCandidates, presentByProduct, storeCode, lookupCode, item);
        }

        var additions = new List<SellableItemDto>();
        foreach (var item in codeConflictItems)
        {
            var storeCode = Normalize(item.StoreCode);
            var lookupCode = Normalize(item.LookupCode);
            // 只给主目录里仍存在的码补候选：码已被删除或本地还没同步到时，不能凭旧冲突数据"复活"商品。
            if (!catalogCodes.Contains((storeCode, lookupCode)))
            {
                continue;
            }

            var category = SellableCandidateCategories.Of(item);
            var productCode = Normalize(item.ProductCode);

            // 主目录已有的同类候选（通常是胜出项）保留目录版本，只补其它候选。
            if (presentCandidates.Contains((storeCode, lookupCode, productCode, category)))
            {
                continue;
            }

            // 该商品在这个码上已经有候选时：只有"主条码 vs 套装码"且有效价格不同才补；
            // 其它组合（如清货价覆盖本身条码）沿用"商品已在就不补"的老口径，
            // 也避免目录与冲突候选来自不同版本时凭空多出一张价格相同的重复卡片。
            if (presentByProduct.TryGetValue((storeCode, lookupCode, productCode), out var sameProduct) &&
                !sameProduct.Any(existing =>
                    SellableCandidateCategories.IsMainSetPair(SellableCandidateCategories.Of(existing), category) &&
                    SellableCandidateCategories.HasDifferentEffectivePrice(existing, item)))
            {
                continue;
            }

            RecordPresent(presentCandidates, presentByProduct, storeCode, lookupCode, item);
            additions.Add(item);
        }

        return additions.Count == 0
            ? catalogItems
            : [.. catalogItems, .. additions];
    }

    private static void RecordPresent(
        HashSet<(string StoreCode, string LookupCode, string ProductCode, SellableCandidateCategory Category)> presentCandidates,
        Dictionary<(string StoreCode, string LookupCode, string ProductCode), List<SellableItemDto>> presentByProduct,
        string storeCode,
        string lookupCode,
        SellableItemDto item)
    {
        var productCode = Normalize(item.ProductCode);
        presentCandidates.Add((storeCode, lookupCode, productCode, SellableCandidateCategories.Of(item)));
        var key = (storeCode, lookupCode, productCode);
        if (!presentByProduct.TryGetValue(key, out var list))
        {
            list = [];
            presentByProduct[key] = list;
        }

        list.Add(item);
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }
}
