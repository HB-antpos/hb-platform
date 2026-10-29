using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

/// <summary>
/// 目录折扣保护（1002 店 10-24 / 10-25）：分店价的折扣率被写成 1（或 100）时，旧代码把它夹成 100% 折扣，
/// 扫码加购就是 $0。目录折扣是自动给的，不能全免：≥ 1 视为脏数据忽略，(0,1) 之间正常打折，且至少留 1 分。
/// </summary>
public sealed class CatalogDiscountGuardTests
{
    private const string StoreCode = "1002";

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(30.0)]
    [InlineData(100.0)]
    [InlineData(-0.2)]
    public void AddItem_ignores_catalog_discount_rate_that_is_100_percent_or_more_or_negative(double rate)
    {
        var cart = new PosCartService();

        var line = cart.AddItem(CreateItem(price: 3.99m, discountRate: (decimal)rate));

        Assert.Equal(0, line.CatalogDiscountBasisPoints);
        Assert.Equal(0m, line.DiscountAmount);
        Assert.False(line.IsCatalogDiscount);
        Assert.Equal(3.99m, line.ActualAmount);
        Assert.Equal(3.99m, cart.ActualAmount);
    }

    [Fact]
    public void AddItem_keeps_a_normal_catalog_discount()
    {
        var cart = new PosCartService();

        var line = cart.AddItem(CreateItem(price: 10m, discountRate: 0.2m));

        Assert.Equal(2_000, line.CatalogDiscountBasisPoints);
        Assert.Equal(2m, line.DiscountAmount);
        Assert.True(line.IsCatalogDiscount);
        Assert.Equal(8m, line.ActualAmount);
    }

    [Fact]
    public void AddItem_caps_a_rate_that_rounds_up_to_100_percent_below_the_free_line()
    {
        // 0.99996 四舍五入到 10000 基点 = 100%：必须压到 9999，行不能变成 $0。
        var cart = new PosCartService();

        var line = cart.AddItem(CreateItem(price: 100m, discountRate: 0.99996m));

        Assert.Equal(PosCartService_MaxBasisPoints, line.CatalogDiscountBasisPoints);
        Assert.True(line.ActualAmount > 0m);
        Assert.Equal(99.99m, line.DiscountAmount);
        Assert.Equal(0.01m, line.ActualAmount);
    }

    [Fact]
    public void AddItem_never_discounts_a_one_cent_line_to_zero()
    {
        var cart = new PosCartService();

        var line = cart.AddItem(CreateItem(price: 0.01m, discountRate: 0.99m));

        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal(0.01m, line.ActualAmount);
    }

    [Fact]
    public void UpdateLineFromRemote_refresh_replaces_a_stale_100_percent_baseline_with_the_clean_rate()
    {
        // 本机旧目录里存着 1.0（以前同步下来的脏值）→ 扫码后远程复核返回干净数据，行必须恢复原价。
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 2.99m, discountRate: 1.0m));
        Assert.Equal(2.99m, line.ActualAmount);

        var applied = cart.UpdateLineFromRemote(line, CreateItem(price: 2.99m, discountRate: null));

        Assert.True(applied);
        Assert.Equal(0, line.CatalogDiscountBasisPoints);
        Assert.Equal(2.99m, line.ActualAmount);
    }

    private const int PosCartService_MaxBasisPoints = CartLine.MaxCatalogDiscountBasisPoints;

    private static SellableItemDto CreateItem(
        string productCode = "G016694",
        string lookupCode = "9311192250656",
        decimal price = 10m,
        decimal? discountRate = null)
    {
        return new SellableItemDto(
            StoreCode: StoreCode,
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: $"Item {productCode}",
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: null,
            DiscountRate: discountRate,
            IsSpecialProduct: false);
    }
}
