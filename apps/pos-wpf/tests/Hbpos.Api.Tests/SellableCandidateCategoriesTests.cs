using Hbpos.Contracts.Catalog;

namespace Hbpos.Api.Tests;

/// <summary>
/// 候选类别判定是服务端冲突收集与客户端（合并器、内存索引、购物车）共用的口径，这里钉住它。
/// </summary>
public sealed class SellableCandidateCategoriesTests
{
    [Theory]
    [InlineData(PriceSourceKind.ProductBase, "product", SellableCandidateCategory.Main)]
    [InlineData(PriceSourceKind.StoreRetailPrice, "store-retail", SellableCandidateCategory.Main)]
    [InlineData(PriceSourceKind.ProductSetCode, "set", SellableCandidateCategory.Set)]
    [InlineData(PriceSourceKind.ProductSetCode, "set-follow-main", SellableCandidateCategory.Set)]
    // 套装码带门店多码价时 PriceSource 是 StoreMultiCodeProduct，只能靠标签前缀识别。
    [InlineData(PriceSourceKind.StoreMultiCodeProduct, "set-store-multi-code", SellableCandidateCategory.Set)]
    [InlineData(PriceSourceKind.StoreMultiCodeProduct, "multi-code", SellableCandidateCategory.Other)]
    [InlineData(PriceSourceKind.StoreMultiCodeProduct, null, SellableCandidateCategory.Other)]
    [InlineData(PriceSourceKind.StoreClearancePrice, "clearance", SellableCandidateCategory.Other)]
    public void Of_classifies_price_source_and_label(PriceSourceKind source, string? label, SellableCandidateCategory expected)
    {
        Assert.Equal(expected, SellableCandidateCategories.Of(source, label));
    }

    [Fact]
    public void IsMainSetPair_is_order_independent_and_excludes_other_categories()
    {
        Assert.True(SellableCandidateCategories.IsMainSetPair(SellableCandidateCategory.Main, SellableCandidateCategory.Set));
        Assert.True(SellableCandidateCategories.IsMainSetPair(SellableCandidateCategory.Set, SellableCandidateCategory.Main));
        Assert.False(SellableCandidateCategories.IsMainSetPair(SellableCandidateCategory.Main, SellableCandidateCategory.Main));
        Assert.False(SellableCandidateCategories.IsMainSetPair(SellableCandidateCategory.Set, SellableCandidateCategory.Set));
        Assert.False(SellableCandidateCategories.IsMainSetPair(SellableCandidateCategory.Main, SellableCandidateCategory.Other));
        Assert.False(SellableCandidateCategories.IsMainSetPair(SellableCandidateCategory.Other, SellableCandidateCategory.Set));
    }

    [Fact]
    public void HasDifferentEffectivePrice_compares_price_and_treats_null_discount_as_zero()
    {
        var item = new SellableItemDto("S01", "P01", null, "Item", "BAR", null, "BAR", 40m, PriceSourceKind.StoreRetailPrice, "store-retail", 1m, null);

        Assert.False(SellableCandidateCategories.HasDifferentEffectivePrice(item, item with { DiscountRate = 0m }));
        Assert.True(SellableCandidateCategories.HasDifferentEffectivePrice(item, item with { RetailPrice = 7.5m }));
        // 标价相同但折扣率不同，收银员实际收到的价格不同，也要选。
        Assert.True(SellableCandidateCategories.HasDifferentEffectivePrice(item, item with { DiscountRate = 0.2m }));
    }
}
