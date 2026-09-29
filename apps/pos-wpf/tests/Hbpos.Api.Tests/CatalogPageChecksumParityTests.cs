using System.Text.Json;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Api.Tests;

/// <summary>
/// WPF 客户端用 Contracts 里的 CatalogPageChecksums 复算服务端页摘要。这里把服务端分页结果先按客户端方式
/// 走一遍 JSON 序列化/反序列化，再与客户端算法比较，锁定两端逐字节一致。
/// </summary>
public sealed class CatalogPageChecksumParityTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 9, 26, 3, 4, 5, TimeSpan.Zero);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Client_sellable_page_v2_checksum_matches_server_after_json_round_trip()
    {
        var index = new CatalogSellableIndex("S01", GeneratedAt, CreateEdgeItems(), catalogVersion: "catalog-v1:parity");

        foreach (var pageSize in new[] { 1, 3, 5000 })
        {
            string? cursor = null;
            do
            {
                var serverPage = index.GetPage(cursor, pageSize, checksumVersion: 2);
                var clientPage = RoundTrip(serverPage);

                Assert.StartsWith(CatalogPageChecksums.SellablePageV2Prefix, serverPage.PageChecksum, StringComparison.Ordinal);
                Assert.Equal(serverPage.PageChecksum, CatalogPageChecksums.ComputeSellablePageV2(clientPage.Items));
                cursor = serverPage.NextCursor;
            }
            while (cursor is not null);
        }
    }

    [Fact]
    public void Client_delta_page_v1_checksum_matches_server_for_interleaved_upserts_and_deletes()
    {
        var edgeItems = CreateEdgeItems();
        var baseline = new CatalogSellableIndex(
            "S01",
            GeneratedAt,
            [.. edgeItems, Item("P-GONE-1", "0001", "即将删除 1", 1m), Item("P-GONE-2", "ZZZ-LAST", "即将删除 2", 2m)],
            catalogVersion: "catalog-v1:base");
        var target = new CatalogSellableIndex(
            "S01",
            GeneratedAt.AddMinutes(20),
            [
                .. edgeItems.Select(item => item.LookupCode == "930000000001" ? item with { RetailPrice = 13.5m } : item),
                Item("P-NEW", "5000000000000", "新增", 0.3m)
            ],
            catalogVersion: "catalog-v1:target");

        foreach (var pageSize in new[] { 1, 2, 5000 })
        {
            string? cursor = null;
            do
            {
                var serverPage = target.GetDeltaPage(baseline, cursor, pageSize);
                var clientPage = RoundTrip(serverPage);

                Assert.Equal(
                    serverPage.PageChecksum,
                    CatalogPageChecksums.ComputeDeltaPageV1(
                        clientPage.BaseCatalogVersion,
                        clientPage.TargetCatalogVersion,
                        clientPage.Items,
                        clientPage.DeletedLookups));
                cursor = serverPage.NextCursor;
            }
            while (cursor is not null);
        }
    }

    [Fact]
    public void Client_delta_checksum_detects_a_dropped_delete()
    {
        var baseline = new CatalogSellableIndex(
            "S01",
            GeneratedAt,
            [Item("P1", "A", "保留", 1m), Item("P2", "B", "删除", 2m)],
            catalogVersion: "catalog-v1:base");
        var target = new CatalogSellableIndex(
            "S01",
            GeneratedAt,
            [Item("P1", "A", "保留但改价", 1.5m)],
            catalogVersion: "catalog-v1:target");

        var page = RoundTrip(target.GetDeltaPage(baseline, cursor: null, pageSize: 5000));

        Assert.Single(page.DeletedLookups);
        Assert.NotEqual(
            page.PageChecksum,
            CatalogPageChecksums.ComputeDeltaPageV1(page.BaseCatalogVersion, page.TargetCatalogVersion, page.Items, []));
    }

    private static T RoundTrip<T>(T value)
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, WebJson), WebJson)!;
    }

    private static SellableItemDto[] CreateEdgeItems()
    {
        return
        [
            new SellableItemDto(
                "S01",
                "P-001",
                ReferenceCode: null,
                "牛奶🥛",
                "930000000001",
                ItemNumber: "I-001",
                Barcode: "source-barcode",
                RetailPrice: 12.34m,
                PriceSourceKind.ProductBase,
                "product",
                QuantityFactor: 1m,
                UpdatedAt: new DateTimeOffset(2026, 7, 28, 11, 2, 3, 456, TimeSpan.FromHours(10)).AddTicks(7_891),
                ProductImage: null,
                DiscountRate: null,
                IsSpecialProduct: true),
            new SellableItemDto(
                "S01",
                "P-FLY",
                ReferenceCode: "REF-SET",
                "EXTENSION Fly Swatter",
                "6405090401470",
                ItemNumber: "HB294-002",
                Barcode: "6405090401470",
                RetailPrice: 8.990m,
                PriceSourceKind.ProductSetCode,
                "set",
                QuantityFactor: 1m,
                UpdatedAt: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                ProductImage: "https://images.example/225/#0065-6759#XRU.jpg",
                DiscountRate: 0.15m,
                IsSpecialProduct: false),
            new SellableItemDto(
                "S01",
                "P-EDGE",
                ReferenceCode: string.Empty,
                "边界",
                "edge-lower",
                ItemNumber: string.Empty,
                Barcode: string.Empty,
                RetailPrice: decimal.MaxValue,
                PriceSourceKind.StoreClearancePrice,
                "clearance",
                QuantityFactor: 0.0000000000000000000000000001m,
                UpdatedAt: null,
                ProductImage: string.Empty,
                DiscountRate: 0.1000000000000000000000000001m,
                IsSpecialProduct: false),
            Item("P-ZERO", "ZERO", "零价", 0m),
            Item("P-TINY", "TINY", "小数", 0.0000001m)
        ];
    }

    private static SellableItemDto Item(string productCode, string lookupCode, string displayName, decimal price)
    {
        return new SellableItemDto(
            "S01",
            productCode,
            ReferenceCode: null,
            displayName,
            lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSourceKind.StoreRetailPrice,
            "store-retail",
            QuantityFactor: 1m,
            UpdatedAt: GeneratedAt,
            ProductImage: null,
            DiscountRate: null,
            IsSpecialProduct: false);
    }
}
