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

    [Fact]
    public void Exact_lookup_returns_both_main_and_set_candidates_of_the_same_product()
    {
        var index = new LocalSellableItemIndex();
        var set = CreateItem("P-SET", "Hot Stamp Set", ConflictCode, PriceSourceKind.ProductSetCode, 7.5m);
        var main = CreateItem("P-SET", "Hot Stamp Set", ConflictCode, PriceSourceKind.StoreRetailPrice, 85m);

        index.ReplaceAll([set, main]);

        // 扫码精确命中两条，PosTerminalWorkflowService 据此进入"多结果选择"。
        var matches = index.FindExactMatches("S001", ConflictCode);
        Assert.Equal(2, matches.Count);
        Assert.Contains(set, matches);
        Assert.Contains(main, matches);
    }

    [Fact]
    public void Upsert_of_set_winner_keeps_the_same_product_main_candidate_when_prices_differ()
    {
        // 服务端回查只返回胜出的套装价；不能因此把收银员可选的主条码候选冲掉，否则下次扫码不再弹出选择。
        var index = new LocalSellableItemIndex();
        var set = CreateItem("P-SET", "Hot Stamp Set", ConflictCode, PriceSourceKind.ProductSetCode, 7.5m);
        var main = CreateItem("P-SET", "Hot Stamp Set", ConflictCode, PriceSourceKind.StoreRetailPrice, 85m);
        index.ReplaceAll([set, main]);
        var refreshedSet = set with { RetailPrice = 8m };

        index.Upsert(refreshedSet);

        var matches = index.FindExactMatches("S001", ConflictCode);
        Assert.Equal(2, matches.Count);
        Assert.Contains(refreshedSet, matches);
        Assert.Contains(main, matches);
        Assert.DoesNotContain(set, matches);
    }

    [Fact]
    public void Upsert_replaces_the_whole_code_when_same_product_candidates_have_equal_prices()
    {
        // 主条码与套装码价格相同不是需要选择的冲突（目录与冲突候选来自不同版本时可能暂时并存），
        // 回写按原语义整码替换，不能留着两张一模一样的卡片。
        var index = new LocalSellableItemIndex();
        var set = CreateItem("P-SET", "Hot Stamp Set", ConflictCode, PriceSourceKind.ProductSetCode, 40m);
        var main = CreateItem("P-SET", "Hot Stamp Set", ConflictCode, PriceSourceKind.StoreRetailPrice, 40m);
        index.ReplaceAll([set, main]);
        var refreshedSet = set with { UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(1) };

        index.Upsert(refreshedSet);

        Assert.Same(refreshedSet, Assert.Single(index.FindExactMatches("S001", ConflictCode)));
    }

    [Fact]
    public void Upsert_of_clearance_price_replaces_every_entry_of_that_product_but_keeps_other_products()
    {
        // 清货价有意覆盖本身条码，不属于"主条码 vs 套装"：与原来一样替换该商品的全部条目、保留其它商品。
        var index = new LocalSellableItemIndex();
        var set = CreateItem("P-SET", "Hot Stamp Set", ConflictCode, PriceSourceKind.ProductSetCode, 7.5m);
        var main = CreateItem("P-SET", "Hot Stamp Set", ConflictCode, PriceSourceKind.StoreRetailPrice, 85m);
        var other = CreateItem("P-OTHER", "Other", ConflictCode, PriceSourceKind.ProductBase, 3m);
        index.ReplaceAll([set, main, other]);
        var clearance = CreateItem("P-SET", "Hot Stamp Set", ConflictCode, PriceSourceKind.StoreClearancePrice, 5m);

        index.Upsert(clearance);

        var matches = index.FindExactMatches("S001", ConflictCode);
        Assert.Equal(2, matches.Count);
        Assert.Contains(clearance, matches);
        Assert.Contains(other, matches);
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
