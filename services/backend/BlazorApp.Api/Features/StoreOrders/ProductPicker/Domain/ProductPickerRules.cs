using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Features.StoreOrders.ProductPicker.Domain;

internal enum ProductPickerHomePageMode
{
    LightweightWarmUp,
    AccurateCache,
}

internal sealed record ProductPickerPageInput(
    StoreOrderFilterDto Filter,
    IReadOnlyList<string> NormalizedGrades
);

internal sealed record ProductPickerBatchLookupInput(IReadOnlyList<string> Codes);

internal sealed record ProductPickerScanLookupInput(string Barcode, string? StoreCode);

internal sealed record ProductPickerHomePageInput(
    int PageSize,
    ProductPickerHomePageMode Mode
);

internal sealed record ProductPickerHomePageWarmUpInput(
    IReadOnlyList<int> PageSizes,
    TimeSpan Timeout
);

internal sealed record ProductPickerValidationResult<T>(
    bool IsValid,
    T? Value,
    string? ErrorMessage
)
    where T : class
{
    internal static ProductPickerValidationResult<T> Valid(T value) =>
        new(true, value, null);

    internal static ProductPickerValidationResult<T> Invalid(string errorMessage) =>
        new(false, null, errorMessage);
}

internal sealed record ProductPickerSearchFilter(
    string? UnifiedKeyword,
    string? ItemOrBarcodeKeyword,
    string? ProductNameKeyword,
    string Mode
);

internal sealed record ProductPickerScanLookupData(
    string Barcode,
    string? MatchType,
    List<StoreOrderProductDto> Items,
    int RawCount,
    long ExactQueryMs,
    long BuildMs
);

internal static class ProductPickerRules
{
    internal const int BatchLookupMaximumCodes = 500;
    internal const int HomePageQueryCommandTimeoutSeconds = 30;

    // 首页/分类浏览分页缓存与首页预热共用的时长：生产实测跨用户复用极少，
    // 缩到 2 分钟让上下架、改价尽快可见，同时保留同一用户短时间内来回切换的命中。
    internal static readonly TimeSpan HomePageCacheDuration = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan HomePageWarmUpTimeout = TimeSpan.FromSeconds(30);
    internal static readonly IReadOnlyList<int> HomePageWarmUpPageSizes = [50, 18];

    internal static IReadOnlyList<string> NormalizeGrades(string? value)
    {
        return (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static IReadOnlyList<string> NormalizeBatchLookupCodes(
        IEnumerable<string?> codes
    )
    {
        return codes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static ProductPickerSearchFilter CreateProductSearchFilter(
        StoreOrderFilterDto filter
    )
    {
        if (TryGetUnifiedProductSearchKeyword(filter, out var unifiedKeyword))
        {
            return new ProductPickerSearchFilter(
                unifiedKeyword,
                ItemOrBarcodeKeyword: null,
                ProductNameKeyword: null,
                Mode: "unified"
            );
        }

        var itemOrBarcodeKeyword = NormalizeProductSearchKeyword(filter.ItemNumber);
        var productNameKeyword = NormalizeProductSearchKeyword(filter.ProductName);
        var mode = itemOrBarcodeKeyword != null && productNameKeyword != null
            ? "split"
            : itemOrBarcodeKeyword != null
                ? "item-or-barcode"
                : productNameKeyword != null
                    ? "product-name"
                    : "none";

        return new ProductPickerSearchFilter(
            UnifiedKeyword: null,
            itemOrBarcodeKeyword,
            productNameKeyword,
            mode
        );
    }

    internal static bool IsDefaultHomePageProductFilter(
        StoreOrderFilterDto filter,
        IReadOnlyCollection<string> normalizedGrades
    )
    {
        return string.IsNullOrWhiteSpace(filter.CategoryGUID)
            && string.IsNullOrWhiteSpace(filter.LocalSupplierCode)
            && string.IsNullOrWhiteSpace(filter.SupplierCode)
            && string.IsNullOrWhiteSpace(filter.ItemNumber)
            && string.IsNullOrWhiteSpace(filter.ProductName)
            && string.IsNullOrWhiteSpace(filter.ExcludeOrderGUID)
            && !filter.IncludeInactiveWarehouseProducts
            && (
                string.IsNullOrWhiteSpace(filter.SortBy)
                || filter.SortBy.Equals("default", StringComparison.OrdinalIgnoreCase)
            )
            && !filter.SortDescending
            && filter.ColumnFilters == null
            && normalizedGrades.Count == 0;
    }

    internal static bool ShouldIncludeInactiveWarehouseProductsForQuickAdd(
        StoreOrderFilterDto filter
    )
    {
        // 下架商品只对后台订货“货号快速加入”放开，避免共享 DTO 被其它列表误用。
        return filter.IncludeInactiveWarehouseProducts
            && !string.IsNullOrWhiteSpace(filter.ItemNumber)
            && string.IsNullOrWhiteSpace(filter.ProductName)
            && string.IsNullOrWhiteSpace(filter.CategoryGUID)
            && string.IsNullOrWhiteSpace(filter.LocalSupplierCode)
            && string.IsNullOrWhiteSpace(filter.SupplierCode)
            && string.IsNullOrWhiteSpace(filter.ExcludeOrderGUID)
            && !filter.ExcludeExistingWarehouseProducts;
    }

    internal static string? GetManualLocationLookupIdentifier(StoreOrderFilterDto filter)
    {
        // ItemNumber 是订货页主搜索框首选字段；兼容仅传商品名称的旧调用。
        foreach (var candidate in new[] { filter.ItemNumber, filter.ProductName })
        {
            var normalized = candidate?.Trim();
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    internal static List<StoreOrderBatchLookupItemDto> BuildBatchLookupResults(
        IReadOnlyList<string> codes,
        IReadOnlyList<StoreOrderProductDto> products
    )
    {
        return codes
            .Select(code =>
            {
                // 精确匹配优先级是既有契约：货号 > 条码 > 商品编码。
                var match =
                    products.FirstOrDefault(product =>
                        string.Equals(
                            product.ItemNumber,
                            code,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    ?? products.FirstOrDefault(product =>
                        string.Equals(product.Barcode, code, StringComparison.OrdinalIgnoreCase)
                    )
                    ?? products.FirstOrDefault(product =>
                        string.Equals(
                            product.ProductCode,
                            code,
                            StringComparison.OrdinalIgnoreCase
                        )
                    );

                return new StoreOrderBatchLookupItemDto
                {
                    LookupCode = code,
                    Product = match,
                };
            })
            .ToList();
    }

    internal static StoreOrderFilterDto CreateDefaultHomePageFilter(int pageSize)
    {
        return new StoreOrderFilterDto
        {
            StoreCode = null,
            PageNumber = 1,
            PageSize = pageSize,
            ItemNumber = null,
            ProductName = null,
            CategoryGUID = null,
            LocalSupplierCode = null,
            SupplierCode = null,
            ExcludeExistingWarehouseProducts = false,
            IncludeInactiveWarehouseProducts = false,
            ExcludeOrderGUID = null,
            SortBy = "Default",
            SortDescending = false,
            Grade = null,
            ColumnFilters = null,
        };
    }

    internal static string GetBarcodeTail(string? barcode)
    {
        var trimmed = barcode?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "empty";
        }

        return trimmed.Length <= 6 ? trimmed : trimmed[^6..];
    }

    internal static int GetBarcodeLength(string? barcode) => barcode?.Trim().Length ?? 0;

    internal static string? NormalizeMatchKey(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    internal static bool MatchNonEmpty(string? left, string? right)
    {
        var normalizedLeft = NormalizeMatchKey(left);
        var normalizedRight = NormalizeMatchKey(right);
        return normalizedLeft != null
            && normalizedRight != null
            && string.Equals(
                normalizedLeft,
                normalizedRight,
                StringComparison.OrdinalIgnoreCase
            );
    }

    private static string? NormalizeProductSearchKeyword(string? value)
    {
        var keyword = value?.Trim().ToLower();
        return string.IsNullOrWhiteSpace(keyword) ? null : keyword;
    }

    private static bool TryGetUnifiedProductSearchKeyword(
        StoreOrderFilterDto filter,
        out string keyword
    )
    {
        keyword = string.Empty;
        var itemNumberKeyword = filter.ItemNumber?.Trim();
        var productNameKeyword = filter.ProductName?.Trim();

        if (
            string.IsNullOrWhiteSpace(itemNumberKeyword)
            || string.IsNullOrWhiteSpace(productNameKeyword)
            || !string.Equals(
                itemNumberKeyword,
                productNameKeyword,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return false;
        }

        // 前端单搜索框会同时传两字段，必须保持 OR 语义。
        keyword = itemNumberKeyword.ToLower();
        return true;
    }
}
