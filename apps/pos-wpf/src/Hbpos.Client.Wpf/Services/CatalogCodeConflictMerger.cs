using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 把服务端下发的码冲突候选并入本地目录。主目录每个查询码只有一个商品，
/// 冲突码补上其它商品后，扫码精确命中多条即走已有的"多结果选择"弹窗，由收银员决定卖哪个。
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
        var presentProducts = new HashSet<(string StoreCode, string LookupCode, string ProductCode)>();
        foreach (var item in catalogItems)
        {
            var storeCode = Normalize(item.StoreCode);
            var lookupCode = Normalize(item.LookupCode);
            catalogCodes.Add((storeCode, lookupCode));
            presentProducts.Add((storeCode, lookupCode, Normalize(item.ProductCode)));
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

            // 主目录已有的商品（通常是胜出项）保留目录版本，只补其它商品。
            if (presentProducts.Add((storeCode, lookupCode, Normalize(item.ProductCode))))
            {
                additions.Add(item);
            }
        }

        return additions.Count == 0
            ? catalogItems
            : [.. catalogItems, .. additions];
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }
}
