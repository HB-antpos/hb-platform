using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Api.Tests;

/// <summary>
/// 码冲突响应的旧版兼容视图：旧版 WPF 的本地冲突表主键只到商品，收到"同商品两条"会写入失败，
/// 所以不带 includeSameProductCandidates 的请求必须拿到与此前版本一致的内容。
/// </summary>
public sealed class CodeConflictResponseCompatTests
{
    [Fact]
    public void ToLegacyView_drops_codes_that_only_conflict_within_one_product()
    {
        var response = CreateResponse(
            // 同商品主条码 vs 套装：新增候选，旧客户端不该看到。
            Item("P01", "BAR01", PriceSourceKind.ProductSetCode, 7.5m),
            Item("P01", "BAR01", PriceSourceKind.StoreRetailPrice, 85m));

        var legacy = CodeConflictResponseCompat.ToLegacyView(response);

        Assert.Empty(legacy.Items);
        Assert.True(legacy.Available);
        Assert.Equal(response.StoreCode, legacy.StoreCode);
    }

    [Fact]
    public void ToLegacyView_keeps_only_each_products_best_candidate_for_multi_product_codes()
    {
        var response = CreateResponse(
            Item("P01", "BAR01", PriceSourceKind.ProductSetCode, 7.5m),
            Item("P01", "BAR01", PriceSourceKind.StoreRetailPrice, 85m),
            Item("P02", "BAR01", PriceSourceKind.StoreRetailPrice, 3m),
            // 另一个码只有一个商品的两条：整组丢弃。
            Item("P03", "BAR02", PriceSourceKind.ProductSetCode, 9m),
            Item("P03", "BAR02", PriceSourceKind.ProductBase, 1m));

        var legacy = CodeConflictResponseCompat.ToLegacyView(response);

        Assert.Collection(
            legacy.Items,
            first =>
            {
                Assert.Equal("P01", first.ProductCode);
                Assert.Equal(PriceSourceKind.ProductSetCode, first.PriceSource);
            },
            second =>
            {
                Assert.Equal("P02", second.ProductCode);
                Assert.Equal(PriceSourceKind.StoreRetailPrice, second.PriceSource);
            });
    }

    [Fact]
    public void ToLegacyView_matches_previous_output_for_ordinary_multi_product_conflicts()
    {
        var response = CreateResponse(
            Item("P-FLY", "6405090401470", PriceSourceKind.ProductSetCode, 8.99m),
            Item("P-FLOWER", "6405090401470", PriceSourceKind.StoreRetailPrice, 3.49m));

        var legacy = CodeConflictResponseCompat.ToLegacyView(response);

        Assert.Equal(response.Items, legacy.Items);
    }

    [Fact]
    public void ToLegacyView_returns_same_instance_when_there_is_nothing_to_filter()
    {
        var response = CreateResponse();

        Assert.Same(response, CodeConflictResponseCompat.ToLegacyView(response));
    }

    private static CatalogCodeConflictsResponse CreateResponse(params CatalogLookupItemDto[] items) =>
        new("S01", DateTimeOffset.UnixEpoch, true, items);

    private static CatalogLookupItemDto Item(
        string productCode,
        string lookupCode,
        PriceSourceKind source,
        decimal price) =>
        new(
            StoreCode: "S01",
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: productCode,
            LookupCode: lookupCode,
            LookupCodeNormalized: lookupCode.ToUpperInvariant(),
            ItemNumber: null,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSource: source,
            PriceSourceLabel: source == PriceSourceKind.ProductSetCode ? "set" : "store-retail",
            QuantityFactor: 1m,
            UpdatedAt: null,
            RowVersion: null);
}
