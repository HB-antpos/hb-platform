using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 某家店的特殊商品分类快照（本机覆盖式落库、联网时整体刷新）。
/// Categories 按服务器 SortOrder 升序；Assignments 只含「当前仍是特殊商品」的归属，没出现的特殊商品 = 未分类。
/// Categories 为空 = 这家店没建分类，收银端整个分类功能不可见、行为与旧版完全一致。
/// </summary>
public sealed record SpecialProductCategorySnapshot(
    string StoreCode,
    IReadOnlyList<SpecialProductCategoryDto> Categories,
    IReadOnlyList<SpecialProductCategoryAssignmentDto> Assignments)
{
    /// <summary>「未分类」视图在本机与 mobile 接口里共用的固定标识（与真实分类 Id 不会冲突）。</summary>
    public const string UncategorizedId = "uncategorized";

    public static SpecialProductCategorySnapshot Empty(string storeCode) => new(storeCode, [], []);

    public bool HasCategories => Categories.Count > 0;

    public static SpecialProductCategorySnapshot FromResponse(
        string storeCode,
        CatalogSpecialProductCategoriesResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new SpecialProductCategorySnapshot(
            storeCode,
            response.Categories ?? [],
            response.Assignments ?? []);
    }

    /// <summary>
    /// 整理成可信的形态：分类按（SortOrder，Id）排序并按 Id 去重；归属只保留指向存在分类的行，
    /// 一个商品只认第一条（服务端保证唯一，这里只是兜底，避免坏数据让同一商品出现在两个分类）。
    /// 落库前与读库后都过一遍，保证内存里的快照在任何来源下口径一致。
    /// </summary>
    public SpecialProductCategorySnapshot Normalize()
    {
        var categories = Categories
            .Where(category => !string.IsNullOrWhiteSpace(category.CategoryId))
            .Select(category => category with { CategoryId = category.CategoryId.Trim(), Name = category.Name?.Trim() ?? string.Empty })
            .GroupBy(category => category.CategoryId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.CategoryId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var knownIds = new HashSet<string>(categories.Select(category => category.CategoryId), StringComparer.OrdinalIgnoreCase);
        var assignments = Assignments
            .Where(assignment => !string.IsNullOrWhiteSpace(assignment.ProductCode) &&
                                 !string.IsNullOrWhiteSpace(assignment.CategoryId) &&
                                 knownIds.Contains(assignment.CategoryId.Trim()))
            .Select(assignment => assignment with
            {
                ProductCode = assignment.ProductCode.Trim(),
                CategoryId = categories.First(category => string.Equals(
                    category.CategoryId, assignment.CategoryId.Trim(), StringComparison.OrdinalIgnoreCase)).CategoryId,
            })
            .GroupBy(assignment => assignment.ProductCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return new SpecialProductCategorySnapshot(StoreCode, categories, assignments);
    }

    /// <summary>某分类里的商品编码，按服务器顺序（SortOrder，再按商品编码稳定排序）。</summary>
    public IReadOnlyList<string> GetOrderedProductCodes(string categoryId)
    {
        return Assignments
            .Where(assignment => string.Equals(assignment.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(assignment => assignment.SortOrder)
            .ThenBy(assignment => assignment.ProductCode, StringComparer.OrdinalIgnoreCase)
            .Select(assignment => assignment.ProductCode)
            .ToArray();
    }

    /// <summary>商品所属分类 Id；未分类（或指向不存在的分类）返回 null。</summary>
    public string? FindCategoryId(string productCode)
    {
        var assignment = Assignments.FirstOrDefault(item => string.Equals(
            item.ProductCode?.Trim(), productCode?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (assignment is null)
        {
            return null;
        }

        return Categories.Any(category => string.Equals(category.CategoryId, assignment.CategoryId, StringComparison.OrdinalIgnoreCase))
            ? assignment.CategoryId
            : null;
    }
}
