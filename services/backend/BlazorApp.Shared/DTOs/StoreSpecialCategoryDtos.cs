namespace BlazorApp.Shared.DTOs;

// 分店级「特殊商品分类」手机 App 接口的请求 / 响应（api/react/v1/store-special-product-categories）。
// JSON 一律 camelCase（全局序列化策略），字段名与统一契约第 3 节一致。

/// <summary>一个分类（列表、新建、改名、排序都返回这个形状）。</summary>
public sealed class StoreSpecialCategoryDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>形如 #E65A2F；没设置为 null（客户端用默认灰）。</summary>
    public string? Color { get; set; }

    public int SortOrder { get; set; }

    /// <summary>该分类里「仍是特殊商品」的商品数（取消标记但保留归属的不计入）。</summary>
    public int ItemCount { get; set; }
}

/// <summary>GET ?storeCode=：本店分类栏与计数。</summary>
public sealed class StoreSpecialCategoryListDto
{
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>本店当前特殊商品总数。</summary>
    public int TotalCount { get; set; }

    /// <summary>没有归属分类的特殊商品数。</summary>
    public int UncategorizedCount { get; set; }

    public List<StoreSpecialCategoryDto> Categories { get; set; } = new();
}

/// <summary>POST / PUT 的请求：新建与改名共用。</summary>
public sealed class StoreSpecialCategorySaveRequestDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
}

public sealed class StoreSpecialCategoryDeleteResultDto
{
    /// <summary>被删分类里「仍是特殊商品」而回到未分类的商品数（归属行已删除，特殊标记不变）。</summary>
    public int RemovedItemCount { get; set; }
}

public sealed class StoreSpecialCategoryOrderRequestDto
{
    public string StoreCode { get; set; } = string.Empty;
    public List<string> CategoryIds { get; set; } = new();
}

public sealed class StoreSpecialCategoryOrderResultDto
{
    public List<StoreSpecialCategoryDto> Categories { get; set; } = new();
}

/// <summary>分类里 / 全部 / 未分类视图的一条特殊商品。</summary>
public sealed class StoreSpecialCategoryProductDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public decimal? RetailPrice { get; set; }
    public string? ProductImage { get; set; }

    /// <summary>所属分类 id；未分类为 null。</summary>
    public string? CategoryId { get; set; }
    public string? CategoryName { get; set; }

    /// <summary>分类内顺序（从 0 开始）；未分类时为 0，以 categoryId 为 null 判断未分类。</summary>
    public int SortOrder { get; set; }
}

public sealed class StoreSpecialCategoryProductPageDto
{
    public List<StoreSpecialCategoryProductDto> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
}

/// <summary>「添加商品」搜索 / 扫码的候选商品（本店有有效 StoreRetailPrice 行的商品）。</summary>
public sealed class StoreSpecialCategoryCandidateDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public decimal? RetailPrice { get; set; }
    public string? ProductImage { get; set; }
    public bool IsSpecialProduct { get; set; }
    public string? CategoryId { get; set; }
    public string? CategoryName { get; set; }
}

public sealed class StoreSpecialCategoryCandidateResultDto
{
    public List<StoreSpecialCategoryCandidateDto> Items { get; set; } = new();
}

public sealed class StoreSpecialCategoryAssignRequestDto
{
    public string StoreCode { get; set; } = string.Empty;
    public List<string> ProductCodes { get; set; } = new();

    /// <summary>目标分类 id；null = 移到「未分类」（删除归属行）。</summary>
    public string? CategoryId { get; set; }

    /// <summary>true：尚不是特殊商品的先标记为特殊；false：要求商品已是特殊商品。</summary>
    public bool MarkAsSpecial { get; set; }
}

/// <summary>被跳过的商品及稳定英文原因码（NO_STORE_PRICE / NOT_SPECIAL_PRODUCT），中文由客户端映射。</summary>
public sealed class StoreSpecialCategorySkippedDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class StoreSpecialCategoryAssignResultDto
{
    /// <summary>已落到目标分类（或移到未分类）的商品数。</summary>
    public int AssignedCount { get; set; }

    /// <summary>本次新标记为特殊商品的商品数。</summary>
    public int MarkedCount { get; set; }

    public List<StoreSpecialCategorySkippedDto> Skipped { get; set; } = new();
}

public sealed class StoreSpecialCategoryUnmarkRequestDto
{
    public string StoreCode { get; set; } = string.Empty;
    public List<string> ProductCodes { get; set; } = new();
}

public sealed class StoreSpecialCategoryUnmarkResultDto
{
    public int UnmarkedCount { get; set; }
}

public sealed class StoreSpecialCategoryProductOrderRequestDto
{
    public string StoreCode { get; set; } = string.Empty;
    public List<string> ProductCodes { get; set; } = new();
}

public sealed class StoreSpecialCategoryOkDto
{
    public bool Ok { get; set; } = true;
}

// ------------------------------------------------------------------ 同步到其他分店

public sealed class StoreSpecialCategorySyncSourceDto
{
    public string StoreCode { get; set; } = string.Empty;
    public int CategoryCount { get; set; }

    /// <summary>来源店「仍是特殊商品且已归类」的商品数，即 scope=categories-and-products 会同步的商品数。</summary>
    public int ProductCount { get; set; }
}

public sealed class StoreSpecialCategorySyncTargetDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public int CategoryCount { get; set; }

    /// <summary>目标店当前特殊商品总数。</summary>
    public int SpecialProductCount { get; set; }
}

public sealed class StoreSpecialCategorySyncTargetsDto
{
    public StoreSpecialCategorySyncSourceDto Source { get; set; } = new();
    public List<StoreSpecialCategorySyncTargetDto> Targets { get; set; } = new();
}

/// <summary>同步预览 / 执行请求；scope：categories | categories-and-products；mode：merge | replace。</summary>
public sealed class StoreSpecialCategorySyncRequestDto
{
    public string SourceStoreCode { get; set; } = string.Empty;
    public List<string> TargetStoreCodes { get; set; } = new();
    public string Scope { get; set; } = StoreSpecialCategorySyncScopes.Categories;
    public string Mode { get; set; } = StoreSpecialCategorySyncModes.Merge;

    /// <summary>仅 execute 使用：mode=replace 时必须为 true，否则拒绝。</summary>
    public bool ConfirmReplace { get; set; }
}

public static class StoreSpecialCategorySyncScopes
{
    public const string Categories = "categories";
    public const string CategoriesAndProducts = "categories-and-products";
}

public static class StoreSpecialCategorySyncModes
{
    public const string Merge = "merge";
    public const string Replace = "replace";
}

public sealed class StoreSpecialCategorySyncSkippedDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>稳定英文码，目前只有 NO_STORE_PRICE（目标店没有该商品的有效门店价行）。</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>一家目标店的同步计数（预览与执行共用）。</summary>
public class StoreSpecialCategorySyncTargetResultDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;

    /// <summary>目标店新建的分类数。</summary>
    public int CreatedCategories { get; set; }

    /// <summary>目标店按名称对上的已有分类数（颜色 / 顺序跟来源）。</summary>
    public int MergedCategories { get; set; }

    /// <summary>replace 时删除的目标店独有分类数（其商品回到未分类）。</summary>
    public int RemovedCategories { get; set; }

    /// <summary>目标店原来不是特殊商品、本次新标记的商品数。</summary>
    public int NewlyMarkedProducts { get; set; }

    /// <summary>目标店原来归属别的分类、本次改到同名分类的商品数。</summary>
    public int ReassignedProducts { get; set; }

    /// <summary>目标店本来就是特殊商品且已在同名分类的商品数。</summary>
    public int UnchangedProducts { get; set; }

    /// <summary>replace + categories-and-products 时被取消特殊标记的商品数（来源店并非特殊商品）。</summary>
    public int UnmarkedProducts { get; set; }

    public List<StoreSpecialCategorySyncSkippedDto> Skipped { get; set; } = new();

    /// <summary>
    /// 这家店无法同步的原因（如分类数会超过上限）；null 表示可以同步。
    /// 这是对契约的补充字段：预览里提前暴露，避免执行时才发现。
    /// </summary>
    public string? Error { get; set; }
}

public sealed class StoreSpecialCategorySyncPreviewDto
{
    public List<StoreSpecialCategorySyncTargetResultDto> Targets { get; set; } = new();
}

/// <summary>执行结果：每家店一个事务，某店失败（ok=false）不影响其他店。</summary>
public sealed class StoreSpecialCategorySyncExecuteTargetDto : StoreSpecialCategorySyncTargetResultDto
{
    public bool Ok { get; set; }
}

public sealed class StoreSpecialCategorySyncExecuteDto
{
    public List<StoreSpecialCategorySyncExecuteTargetDto> Results { get; set; } = new();
}
