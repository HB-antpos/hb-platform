using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

public sealed class LocalSellableItemIndexCodeConflictTests
{
    private const string ConflictCode = "6405090401470";

    [Fact]
    public void Upsert_of_conflicting_code_replaces_only_the_same_product()
    {
        // 码冲突：同一条码下有两个不同商品（套装码与另一商品主条码），服务端回查只会返回胜出商品。
        var index = new LocalSellableItemIndex();
        var fly = CreateItem("P-FLY", "EXTENSION Fly Swatter", ConflictCode, PriceSourceKind.ProductSetCode, 8.99m);
        var flower = CreateItem("P-FLOWER", "flower", ConflictCode, PriceSourceKind.ProductBase, 2.99m, itemNumber: "9040147");
        index.ReplaceAll([fly, flower]);
        var refreshedFly = fly with { RetailPrice = 9.49m };

        index.Upsert(refreshedFly);

        var matches = index.FindExactMatches("S001", ConflictCode);
        Assert.Equal(2, matches.Count);
        Assert.Contains(refreshedFly, matches);
        Assert.Contains(flower, matches);
        Assert.DoesNotContain(fly, matches);
        Assert.Same(flower, Assert.Single(index.FindMetadataExactMatches("S001", "9040147")));
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void Upsert_of_single_product_code_still_replaces_the_whole_code()
    {
        // 非冲突码保持原语义：服务端把码改挂到另一个商品时，旧商品必须被替换掉。
        var index = new LocalSellableItemIndex();
        var oldOwner = CreateItem("P-OLD", "Old owner", ConflictCode, PriceSourceKind.ProductBase, 1m);
        index.ReplaceAll([oldOwner]);
        var newOwner = CreateItem("P-NEW", "New owner", ConflictCode, PriceSourceKind.ProductSetCode, 5m);

        index.Upsert(newOwner);

        Assert.Same(newOwner, Assert.Single(index.FindExactMatches("S001", ConflictCode)));
        Assert.Equal(1, index.Count);
    }

    private static SellableItemDto CreateItem(
        string productCode,
        string name,
        string lookupCode,
        PriceSourceKind priceSource,
        decimal price,
        string? itemNumber = null)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: name,
            LookupCode: lookupCode,
            ItemNumber: itemNumber ?? productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSource: priceSource,
            PriceSourceLabel: priceSource.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: null);
    }
}
