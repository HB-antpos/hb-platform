using Hbpos.Contracts.Catalog;

namespace Hbpos.Api.Services;

public interface IPriceIndexBuilder
{
    IReadOnlyList<SellableItemDto> Build(string storeCode, PriceIndexInput input);

    /// <summary>
    /// 与 <see cref="Build"/> 产出相同的决胜结果，并在同一次遍历中收集码冲突候选，避免完整目录重复展开全部候选。
    /// </summary>
    PriceIndexBuildOutput BuildWithCodeConflicts(string storeCode, PriceIndexInput input);
}

public sealed class PriceIndexBuilder : IPriceIndexBuilder
{
    public IReadOnlyList<SellableItemDto> Build(string storeCode, PriceIndexInput input)
    {
        return BuildCore(storeCode, input, codeConflicts: null);
    }

    public PriceIndexBuildOutput BuildWithCodeConflicts(string storeCode, PriceIndexInput input)
    {
        var codeConflicts = new List<SellableItemDto>();
        var items = BuildCore(storeCode, input, codeConflicts);
        // OrderBy 是稳定排序：按码归组后，同码内仍保持决胜顺序。
        return new PriceIndexBuildOutput(
            items,
            codeConflicts
                .OrderBy(x => NormalizeLookupKey(x.LookupCode), StringComparer.Ordinal)
                .ToList());
    }

    private static List<SellableItemDto> BuildCore(
        string storeCode,
        PriceIndexInput input,
        List<SellableItemDto>? codeConflicts)
    {
        var storePrices = input.StoreRetailPrices
            .Where(x => HasText(x.ProductCode))
            .GroupBy(x => x.ProductCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(p => p.UpdatedAt).First(), StringComparer.OrdinalIgnoreCase);

        var multiBySetCode = input.StoreMultiCodeProducts
            .Where(x => HasText(x.MultiCodeProductCode) && x.MultiCodeRetailPrice.HasValue)
            .GroupBy(x => x.MultiCodeProductCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(p => p.UpdatedAt).First(), StringComparer.OrdinalIgnoreCase);

        var products = input.Products
            .Where(x => HasText(x.ProductCode))
            .GroupBy(x => x.ProductCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var items = new List<SellableItemDto>();

        foreach (var clearance in input.StoreClearancePrices.Where(x => HasText(x.ClearanceBarcode) && x.ClearancePrice.HasValue))
        {
            products.TryGetValue(clearance.ProductCode ?? string.Empty, out var product);
            items.Add(CreateItem(
                storeCode,
                product,
                clearance.ProductCode,
                clearance.ClearanceBarcode!,
                clearance.ClearancePrice!.Value,
                PriceSourceKind.StoreClearancePrice,
                "clearance",
                clearance.UpdatedAt,
                clearance.ReferenceCode,
                discountRate: null,
                isSpecialProduct: false));
        }

        foreach (var multi in input.StoreMultiCodeProducts.Where(x => HasText(x.MultiBarcode) && x.MultiCodeRetailPrice.HasValue))
        {
            products.TryGetValue(multi.ProductCode ?? string.Empty, out var product);
            items.Add(CreateItem(
                storeCode,
                product,
                multi.ProductCode,
                multi.MultiBarcode!,
                multi.MultiCodeRetailPrice!.Value,
                PriceSourceKind.StoreMultiCodeProduct,
                "multi-code",
                multi.UpdatedAt,
                multi.ReferenceCode,
                multi.DiscountRate,
                isSpecialProduct: false));
        }

        foreach (var set in input.ProductSetCodes.Where(x => HasText(x.SetBarcode)))
        {
            products.TryGetValue(set.ProductCode, out var product);
            var hasStoreMultiPrice = multiBySetCode.TryGetValue(set.SetProductCode, out var storeMultiPrice);
            // 多码自身没有有效价格（门店多码价缺失，且 SetRetailPrice 为 NULL 或 0）时，按主条码价格走，
            // 否则会被 ?? 0m 兜成 $0.00（如 SW0126 的 9333527365828）。
            var followsMainPrice = !hasStoreMultiPrice && set.SetRetailPrice is not > 0m && product is not null;
            if (followsMainPrice)
            {
                var main = ResolveProductPrice(product!, storePrices);
                items.Add(CreateItem(
                    storeCode,
                    product,
                    set.ProductCode,
                    set.SetBarcode!,
                    main.Price,
                    // 来源与引用码保持多码自身：目录决胜与冲突候选按 PriceSource 排序，不能因取价口径而改变顺序。
                    PriceSourceKind.ProductSetCode,
                    "set-follow-main",
                    Latest(set.UpdatedAt, main.UpdatedAt),
                    set.ReferenceCode,
                    main.DiscountRate,
                    isSpecialProduct: false));
                continue;
            }

            var price = hasStoreMultiPrice
                ? storeMultiPrice!.MultiCodeRetailPrice!.Value
                : set.SetRetailPrice ?? 0m;
            var source = hasStoreMultiPrice
                ? PriceSourceKind.StoreMultiCodeProduct
                : PriceSourceKind.ProductSetCode;
            var updatedAt = Latest(set.UpdatedAt, storeMultiPrice?.UpdatedAt);
            var referenceCode = hasStoreMultiPrice
                ? storeMultiPrice?.ReferenceCode
                : set.ReferenceCode;
            var discountRate = hasStoreMultiPrice
                ? storeMultiPrice?.DiscountRate
                : null;

            items.Add(CreateItem(
                storeCode,
                product,
                set.ProductCode,
                set.SetBarcode!,
                price,
                source,
                hasStoreMultiPrice ? "set-store-multi-code" : "set",
                updatedAt,
                referenceCode,
                discountRate,
                isSpecialProduct: false));
        }

        foreach (var product in input.Products.Where(x => HasText(x.ProductCode)))
        {
            var main = ResolveProductPrice(product, storePrices);

            AddProductLookup(items, storeCode, product, product.Barcode, main.Price, main.Source, main.UpdatedAt, main.ReferenceCode, main.DiscountRate, main.IsSpecialProduct);
            if (!StringComparer.OrdinalIgnoreCase.Equals(product.Barcode, product.ItemNumber))
            {
                AddProductLookup(items, storeCode, product, product.ItemNumber, main.Price, main.Source, main.UpdatedAt, main.ReferenceCode, main.DiscountRate, main.IsSpecialProduct);
            }
        }

        var winners = new List<SellableItemDto>();
        foreach (var group in items
            .Where(x => input.Since is null || x.UpdatedAt is null || x.UpdatedAt >= input.Since)
            .GroupBy(x => NormalizeLookupKey(x.LookupCode), StringComparer.Ordinal))
        {
            winners.Add(OrderByPriority(group).First());
            if (codeConflicts is not null)
            {
                AppendCodeConflicts(codeConflicts, group);
            }
        }

        return winners
            .OrderBy(x => NormalizeLookupKey(x.LookupCode), StringComparer.Ordinal)
            .ToList();
    }

    private static IOrderedEnumerable<SellableItemDto> OrderByPriority(IEnumerable<SellableItemDto> items)
    {
        return items
            .OrderByDescending(i => i.PriceSource)
            .ThenByDescending(i => i.UpdatedAt ?? DateTimeOffset.MinValue);
    }

    private static void AppendCodeConflicts(List<SellableItemDto> codeConflicts, IEnumerable<SellableItemDto> group)
    {
        // 同一商品的多个来源（如清货价覆盖本身条码）按原优先级决胜即可，只有落到不同商品时才是冲突。
        string? firstProductCode = null;
        var hasMultipleProducts = false;
        foreach (var item in group)
        {
            if (firstProductCode is null)
            {
                firstProductCode = item.ProductCode;
            }
            else if (!StringComparer.OrdinalIgnoreCase.Equals(firstProductCode, item.ProductCode))
            {
                hasMultipleProducts = true;
                break;
            }
        }

        if (!hasMultipleProducts)
        {
            return;
        }

        // 整组按目录决胜顺序排好后每个商品取首条：得到各商品自己的最优价，且首条就是目录胜出项。
        codeConflicts.AddRange(OrderByPriority(group)
            .DistinctBy(item => item.ProductCode, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 解析主条码的有效价格：门店零售价优先，缺失时回退商品基础价。
    /// 主条码、货号以及"跟随主条码"的多码共用这一套口径。
    /// </summary>
    private static ResolvedProductPrice ResolveProductPrice(
        ProductPriceRecord product,
        IReadOnlyDictionary<string, StoreRetailPriceRecord> storePrices)
    {
        storePrices.TryGetValue(product.ProductCode!, out var storePrice);
        var price = storePrice?.StoreRetailPriceValue ?? product.RetailPrice ?? 0m;
        var source = storePrice?.StoreRetailPriceValue is null
            ? PriceSourceKind.ProductBase
            : PriceSourceKind.StoreRetailPrice;
        var updatedAt = Latest(product.UpdatedAt, storePrice?.UpdatedAt);
        var referenceCode = source == PriceSourceKind.StoreRetailPrice
            ? storePrice?.ReferenceCode
            : product.ReferenceCode;
        var discountRate = source == PriceSourceKind.StoreRetailPrice
            ? storePrice?.DiscountRate
            : null;
        return new ResolvedProductPrice(
            price,
            source,
            updatedAt,
            referenceCode,
            discountRate,
            storePrice?.IsSpecialProduct ?? false);
    }

    private readonly record struct ResolvedProductPrice(
        decimal Price,
        PriceSourceKind Source,
        DateTimeOffset? UpdatedAt,
        string? ReferenceCode,
        decimal? DiscountRate,
        bool IsSpecialProduct);

    private static void AddProductLookup(
        List<SellableItemDto> items,
        string storeCode,
        ProductPriceRecord product,
        string? lookupCode,
        decimal price,
        PriceSourceKind source,
        DateTimeOffset? updatedAt,
        string? referenceCode,
        decimal? discountRate,
        bool isSpecialProduct)
    {
        if (!HasText(lookupCode))
        {
            return;
        }

        items.Add(CreateItem(
            storeCode,
            product,
            product.ProductCode,
            lookupCode!,
            price,
            source,
            source == PriceSourceKind.StoreRetailPrice ? "store-retail" : "product",
            updatedAt,
            referenceCode,
            discountRate,
            isSpecialProduct));
    }

    private static SellableItemDto CreateItem(
        string storeCode,
        ProductPriceRecord? product,
        string? productCode,
        string lookupCode,
        decimal retailPrice,
        PriceSourceKind source,
        string label,
        DateTimeOffset? updatedAt,
        string? referenceCode,
        decimal? discountRate,
        bool isSpecialProduct)
    {
        var trimmedLookupCode = lookupCode.Trim();
        // 商品名缺失时优先使用货号，货号也缺失才继续回退商品编码。
        var displayName = HasText(product?.DisplayName)
            ? product!.DisplayName!
            : HasText(product?.ItemNumber)
                ? product!.ItemNumber!
                : productCode ?? product?.ProductCode ?? trimmedLookupCode;

        return new SellableItemDto(
            storeCode,
            productCode ?? product?.ProductCode ?? string.Empty,
            NormalizeReferenceCode(referenceCode),
            displayName,
            trimmedLookupCode,
            product?.ItemNumber,
            product?.Barcode,
            retailPrice,
            source,
            label,
            1m,
            updatedAt,
            product?.ProductImage,
            NormalizeDiscountRate(discountRate),
            isSpecialProduct);
    }

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string NormalizeLookupKey(string value) => value.Trim().ToUpperInvariant();

    private static string? NormalizeReferenceCode(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static decimal? NormalizeDiscountRate(decimal? value)
    {
        if (value is null || value < 0m || value > 100m)
        {
            return null;
        }

        // 中文注释：折扣保护。分店价里折扣率有两种写法：0~1 小数，或 >1 的百分数（50 = 50%）。
        // 原来 1 当成「100%」、100 当成「100%」，结果该行 $0（1002 店 10-24/25：admin 写入的 1.0）。
        // 目录折扣是自动给的，不能全免：值恰好为 1 本身有歧义（1% 还是 100%），连同 100 一律不下发，
        // 客户端按原价卖；真要打折用 (0,1) 之间的小数或 (1,100) 之间的百分数。
        if (value == 1m || value >= 100m)
        {
            return null;
        }

        return value <= 1m
            ? value.Value
            : value.Value / 100m;
    }

    private static DateTimeOffset? Latest(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left > right ? left : right;
    }
}

/// <summary>
/// Items 为每个查询码的决胜结果；CodeConflicts 为落到多个不同商品的查询码的全部商品候选（每商品一条）。
/// </summary>
public sealed record PriceIndexBuildOutput(
    IReadOnlyList<SellableItemDto> Items,
    IReadOnlyList<SellableItemDto> CodeConflicts);

public sealed record PriceIndexInput(
    DateTimeOffset? Since,
    IReadOnlyList<ProductPriceRecord> Products,
    IReadOnlyList<StoreRetailPriceRecord> StoreRetailPrices,
    IReadOnlyList<StoreMultiCodeProductRecord> StoreMultiCodeProducts,
    IReadOnlyList<StoreClearancePriceRecord> StoreClearancePrices,
    IReadOnlyList<ProductSetCodeRecord> ProductSetCodes);

public sealed record ProductPriceRecord(
    string? ProductCode,
    string DisplayName,
    string? ItemNumber,
    string? Barcode,
    decimal? RetailPrice,
    DateTimeOffset? UpdatedAt,
    string? ProductImage = null,
    string? ReferenceCode = null);

public sealed record StoreRetailPriceRecord(
    string? ProductCode,
    decimal? StoreRetailPriceValue,
    DateTimeOffset? UpdatedAt,
    string? ReferenceCode = null,
    decimal? DiscountRate = null,
    bool IsSpecialProduct = false);

public sealed record StoreMultiCodeProductRecord(
    string? ProductCode,
    string? MultiCodeProductCode,
    string? MultiBarcode,
    decimal? MultiCodeRetailPrice,
    DateTimeOffset? UpdatedAt,
    string? ReferenceCode = null,
    decimal? DiscountRate = null);

public sealed record StoreClearancePriceRecord(
    string? ProductCode,
    string? ClearanceBarcode,
    decimal? ClearancePrice,
    DateTimeOffset? UpdatedAt,
    string? ReferenceCode = null);

public sealed record ProductSetCodeRecord(
    string ProductCode,
    string SetProductCode,
    string? SetBarcode,
    decimal? SetRetailPrice,
    DateTimeOffset? UpdatedAt,
    string? ReferenceCode = null);
