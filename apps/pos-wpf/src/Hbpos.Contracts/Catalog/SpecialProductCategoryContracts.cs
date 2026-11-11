namespace Hbpos.Contracts.Catalog;

/// <summary>
/// 分店级特殊商品分类（收银端只读展示，有 SpecialProductsManage 权限的收银员可调整归属与分类内顺序）。
/// 分类的新建 / 改名 / 删除 / 排序 / 同步到其他分店只在手机 App（主后端）里做。
/// 数据权威在 HBweb 库 StoreSpecialProductCategory / StoreSpecialProductCategoryItem。
/// </summary>
public sealed record SpecialProductCategoryDto(
    string CategoryId,
    string Name,
    string? Color,
    int SortOrder);

/// <summary>
/// 商品的分类归属。只包含「当前仍是特殊商品」的商品；没出现的特殊商品 = 未分类。
/// SortOrder 是分类内的服务器统一顺序（从 0 开始）；客户端按 SortOrder、商品编码稳定排序。
/// </summary>
public sealed record SpecialProductCategoryAssignmentDto(
    string ProductCode,
    string CategoryId,
    int SortOrder);

/// <summary>
/// GET /api/v1/catalog/special-products/categories?storeCode=
/// 一次返回本店全部分类与全部归属（不分页：一家店的特殊商品通常在千级以内）。
/// Categories 按 SortOrder 升序；没建任何分类时 Categories 与 Assignments 都为空，客户端据此不显示分类栏。
/// Revision 是分类数据的版本戳（取分类与归属的最大更新时间 ticks 的字符串），便于客户端判断有没有变化。
/// </summary>
public sealed record CatalogSpecialProductCategoriesResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    string Revision,
    IReadOnlyList<SpecialProductCategoryDto> Categories,
    IReadOnlyList<SpecialProductCategoryAssignmentDto> Assignments);

/// <summary>
/// POST /api/v1/catalog/special-products/categories/assign
/// 把一个特殊商品移到某分类（追加到该分类末尾）；CategoryId 为 null = 移出分类（回到「未分类」）。
/// 商品必须当前就是本店的特殊商品，分类必须属于本店，否则返回业务错误。
/// 返回最新的全量分类快照，客户端整体覆盖本地。
/// </summary>
public sealed record CatalogSpecialProductCategoryAssignRequest(
    string StoreCode,
    string ProductCode,
    string? CategoryId);

/// <summary>
/// POST /api/v1/catalog/special-products/categories/order
/// 重排某个分类内的商品顺序。ProductCodes 必须恰好是该分类当前全部（仍是特殊商品的）商品的一个排列，
/// 缺一个、多一个或重复都视为冲突（说明别的收银机或手机刚改过），服务端拒绝并要求客户端先刷新。
/// 返回最新的全量分类快照。
/// </summary>
public sealed record CatalogSpecialProductCategoryOrderRequest(
    string StoreCode,
    string CategoryId,
    IReadOnlyList<string> ProductCodes);
