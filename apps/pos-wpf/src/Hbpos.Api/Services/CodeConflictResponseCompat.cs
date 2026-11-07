using Hbpos.Contracts.Catalog;

namespace Hbpos.Api.Services;

/// <summary>
/// 码冲突响应的旧版兼容视图。
/// 完整候选里，同一商品的"主条码 / 套装码"可能各占一条；旧版 WPF 的本地冲突表主键是（门店、码、商品），
/// 收到同商品两条会插入失败、整批冲突同步中断。所以默认（旧客户端不带参数）只返回"落到多个不同商品"的码、
/// 且每个商品只留最优一条——与此前版本逐条一致；新版客户端显式要求才返回完整候选。
/// </summary>
internal static class CodeConflictResponseCompat
{
    public static CatalogCodeConflictsResponse ToLegacyView(CatalogCodeConflictsResponse response)
    {
        if (response.Items.Count == 0)
        {
            return response;
        }

        var legacyItems = new List<CatalogLookupItemDto>(response.Items.Count);
        // 同一个码的候选在服务端已连续排放；按（码）分组并保持原顺序。
        foreach (var group in response.Items
                     .GroupBy(item => $"{Normalize(item.StoreCode)}|{item.LookupCodeNormalized}", StringComparer.Ordinal))
        {
            var bestPerProduct = group
                .DistinctBy(item => item.ProductCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
            // 只剩一个商品：这是"同商品主条码 vs 套装"的新增候选，旧客户端不该看到。
            if (bestPerProduct.Count > 1)
            {
                legacyItems.AddRange(bestPerProduct);
            }
        }

        return response with { Items = legacyItems };
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}
