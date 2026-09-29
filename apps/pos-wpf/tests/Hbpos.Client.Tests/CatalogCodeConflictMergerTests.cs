using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

public sealed class CatalogCodeConflictMergerTests
{
    [Fact]
    public void Merge_adds_other_products_for_conflicting_code_present_in_catalog()
    {
        var fly = CreateItem("P-FLY", "EXTENSION Fly Swatter", "6405090401470", PriceSourceKind.ProductSetCode, 8.99m);
        var flowerItemNumber = CreateItem("P-FLOWER", "flower", "9040147", PriceSourceKind.ProductBase, 2.99m);
        var flowerAlternate = CreateItem("P-FLOWER", "flower", "6405090401470", PriceSourceKind.ProductBase, 2.99m);

        var merged = CatalogCodeConflictMerger.Merge(
            [fly, flowerItemNumber],
            [fly with { RetailPrice = 1m }, flowerAlternate]);

        // 主目录已有的胜出项保留目录版本（价格 8.99），只补另一个商品。
        Assert.Equal([fly, flowerItemNumber, flowerAlternate], merged);
    }

    [Fact]
    public void Merge_skips_codes_missing_from_catalog_so_stale_conflicts_cannot_resurrect_products()
    {
        var catalogItem = CreateItem("P-ONE", "One", "CODE-1", PriceSourceKind.ProductBase, 1m);
        var staleAlternate = CreateItem("P-TWO", "Two", "CODE-DELETED", PriceSourceKind.ProductBase, 2m);

        var merged = CatalogCodeConflictMerger.Merge([catalogItem], [staleAlternate]);

        Assert.Equal([catalogItem], merged);
    }

    [Fact]
    public void Merge_matches_codes_and_stores_after_normalization()
    {
        var catalogItem = CreateItem("P-ONE", "One", "abc-1", PriceSourceKind.ProductBase, 1m, storeCode: "s001");
        var sameProductDifferentCase = CreateItem("p-one", "One", " ABC-1 ", PriceSourceKind.ProductBase, 1m);
        var otherProduct = CreateItem("P-TWO", "Two", "ABC-1", PriceSourceKind.ProductBase, 2m);
        var otherStore = CreateItem("P-THREE", "Three", "ABC-1", PriceSourceKind.ProductBase, 3m, storeCode: "S002");

        var merged = CatalogCodeConflictMerger.Merge(
            [catalogItem],
            [sameProductDifferentCase, otherProduct, otherStore]);

        Assert.Equal([catalogItem, otherProduct], merged);
    }

    [Fact]
    public void Merge_returns_catalog_instance_when_there_is_nothing_to_add()
    {
        IReadOnlyList<SellableItemDto> catalog = [CreateItem("P-ONE", "One", "CODE-1", PriceSourceKind.ProductBase, 1m)];

        Assert.Same(catalog, CatalogCodeConflictMerger.Merge(catalog, []));
        Assert.Same(catalog, CatalogCodeConflictMerger.Merge(catalog, [catalog[0]]));
    }

    private static SellableItemDto CreateItem(
        string productCode,
        string name,
        string lookupCode,
        PriceSourceKind priceSource,
        decimal price,
        string storeCode = "S001")
    {
        return new SellableItemDto(
            StoreCode: storeCode,
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: name,
            LookupCode: lookupCode,
            ItemNumber: null,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSource: priceSource,
            PriceSourceLabel: priceSource.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: null);
    }
}
