using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

public sealed class CatalogPageChecksumsTests
{
    [Fact]
    public void Sellable_page_v2_matches_server_cross_platform_vector()
    {
        // 与 Hbpos.Api.Tests 的 Page_checksum_v2_uses_binary64_big_endian_cross_platform_vector 同一组输入与期望值。
        var item = new CatalogLookupItemDto(
            "S01",
            "P-001",
            ReferenceCode: null,
            "牛奶🥛",
            "930000000001",
            "930000000001",
            ItemNumber: "I-001",
            Barcode: "source-barcode-is-not-the-offline-lookup",
            RetailPrice: 12.34m,
            PriceSourceKind.ProductBase,
            "product",
            QuantityFactor: 1m,
            UpdatedAt: new DateTimeOffset(2026, 7, 28, 1, 2, 3, 456, TimeSpan.Zero),
            RowVersion: "ignored-by-checksum",
            ProductImage: null,
            DiscountRate: null,
            IsSpecialProduct: true);

        Assert.Equal(
            "sha256-catalog-page-v2:22181273b9791ad9664ad4f30ca2cddd3916ad9a012851490db28f7e1b229c27",
            CatalogPageChecksums.ComputeSellablePageV2([item]));
    }

    [Fact]
    public void Sellable_page_v2_matches_server_vector_for_extreme_numbers()
    {
        var item = new CatalogLookupItemDto(
            "S01",
            "P-EDGE",
            ReferenceCode: string.Empty,
            "边界",
            "EDGE",
            "EDGE",
            ItemNumber: string.Empty,
            Barcode: string.Empty,
            RetailPrice: decimal.MaxValue,
            PriceSourceKind.ProductBase,
            "product",
            QuantityFactor: 0.0000000000000000000000000001m,
            UpdatedAt: null,
            RowVersion: null,
            ProductImage: string.Empty,
            DiscountRate: 0.1000000000000000000000000001m,
            IsSpecialProduct: false);

        Assert.Equal(
            "sha256-catalog-page-v2:c0b3e647f427c35d369335b42c512dc1b1e56fb31c1abc4154bfc1fc2498afba",
            CatalogPageChecksums.ComputeSellablePageV2([item]));
    }

    [Fact]
    public void Sellable_page_v2_changes_when_any_price_changes()
    {
        var item = Item("P-FLY", "6405090401470", 8.99m);

        Assert.NotEqual(
            CatalogPageChecksums.ComputeSellablePageV2([item]),
            CatalogPageChecksums.ComputeSellablePageV2([item with { RetailPrice = 2.99m }]));
    }

    [Fact]
    public void Delta_page_v1_orders_operations_by_normalized_lookup_regardless_of_input_order()
    {
        var upserts = new[] { Item("P-B", "B", 2m), Item("P-D", "D", 4m) };
        var deletes = new[]
        {
            new DeletedLookupDto("S01", "c", "C", new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero)),
            new DeletedLookupDto("S01", "a", "A", new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero))
        };

        var checksum = CatalogPageChecksums.ComputeDeltaPageV1("catalog-v1:base", "catalog-v1:target", upserts, deletes);

        Assert.StartsWith(CatalogPageChecksums.DeltaPageV1Prefix, checksum, StringComparison.Ordinal);
        Assert.Equal(
            checksum,
            CatalogPageChecksums.ComputeDeltaPageV1("catalog-v1:base", "catalog-v1:target", upserts.Reverse().ToArray(), deletes.Reverse().ToArray()));
        // 版本属于摘要输入：同样的操作换一对版本必须得到不同摘要，防止把别的版本增量套进来。
        Assert.NotEqual(
            checksum,
            CatalogPageChecksums.ComputeDeltaPageV1("catalog-v1:other", "catalog-v1:target", upserts, deletes));
        Assert.NotEqual(
            checksum,
            CatalogPageChecksums.ComputeDeltaPageV1("catalog-v1:base", "catalog-v1:target", upserts, deletes[..1]));
    }

    private static CatalogLookupItemDto Item(string productCode, string lookupCode, decimal price)
    {
        return new CatalogLookupItemDto(
            "S01",
            productCode,
            ReferenceCode: null,
            productCode,
            lookupCode,
            lookupCode.Trim().ToUpperInvariant(),
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSourceKind.StoreRetailPrice,
            "store-retail",
            QuantityFactor: 1m,
            UpdatedAt: new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero),
            RowVersion: null);
    }
}
