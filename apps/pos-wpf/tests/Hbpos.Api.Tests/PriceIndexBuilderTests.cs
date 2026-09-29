using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Api.Tests;

public sealed class PriceIndexBuilderTests
{
    private readonly PriceIndexBuilder _builder = new();

    [Fact]
    public void Build_UsesStoreRetailPriceBeforeProductBasePrice()
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple", "ITEM01", "BAR01", 10m, null)],
            [new StoreRetailPriceRecord("P01", 8.5m, null, ReferenceCode: "SRP-UUID-01")],
            [],
            [],
            []));

        var barcodeItem = Assert.Single(items, x => x.LookupCode == "BAR01");
        Assert.Equal(8.5m, barcodeItem.RetailPrice);
        Assert.Equal(PriceSourceKind.StoreRetailPrice, barcodeItem.PriceSource);
        Assert.Equal("SRP-UUID-01", barcodeItem.ReferenceCode);
    }

    [Theory]
    [InlineData(20, 0.2)]
    [InlineData(0.2, 0.2)]
    [InlineData(100, 1)]
    public void Build_NormalizesStoreRetailDiscountRate(decimal sourceDiscountRate, decimal expectedDiscountRate)
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple", "ITEM01", "BAR01", 10m, null)],
            [new StoreRetailPriceRecord("P01", 8.5m, null, ReferenceCode: "SRP-UUID-01", DiscountRate: sourceDiscountRate)],
            [],
            [],
            []));

        var barcodeItem = Assert.Single(items, x => x.LookupCode == "BAR01");
        Assert.Equal(expectedDiscountRate, barcodeItem.DiscountRate);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Build_IgnoresInvalidStoreRetailDiscountRate(decimal sourceDiscountRate)
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple", "ITEM01", "BAR01", 10m, null)],
            [new StoreRetailPriceRecord("P01", 8.5m, null, ReferenceCode: "SRP-UUID-01", DiscountRate: sourceDiscountRate)],
            [],
            [],
            []));

        var barcodeItem = Assert.Single(items, x => x.LookupCode == "BAR01");
        Assert.Null(barcodeItem.DiscountRate);
    }

    [Theory]
    [InlineData(20, 0.2)]
    [InlineData(0.2, 0.2)]
    public void Build_NormalizesStoreMultiCodeDiscountRate(decimal sourceDiscountRate, decimal expectedDiscountRate)
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple", null, null, 10m, null)],
            [],
            [new StoreMultiCodeProductRecord("P01", "M01", "MULTI01", 6m, null, ReferenceCode: "SMCP-UUID-01", DiscountRate: sourceDiscountRate)],
            [],
            []));

        var item = Assert.Single(items);
        Assert.Equal(expectedDiscountRate, item.DiscountRate);
    }

    [Fact]
    public void Build_CarriesProductImageFromProductRecord()
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple", "ITEM01", "BAR01", 10m, null, "https://images.example/P01.jpg")],
            [],
            [],
            [],
            []));

        var item = Assert.Single(items, x => x.LookupCode == "BAR01");
        Assert.Equal("https://images.example/P01.jpg", item.ProductImage);
    }

    [Fact]
    public void Build_CarriesSpecialProductFlagFromStoreRetailPrice()
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple", "ITEM01", "BAR01", 10m, null)],
            [new StoreRetailPriceRecord("P01", 8.5m, null, IsSpecialProduct: true)],
            [],
            [],
            []));

        Assert.All(items, item => Assert.True(item.IsSpecialProduct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_FallsBackToItemNumberWhenDisplayNameIsBlank(string displayName)
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", displayName, "ITEM01", "BAR01", 10m, null)],
            [],
            [],
            [],
            []));

        var item = Assert.Single(items, x => x.LookupCode == "BAR01");
        Assert.Equal("ITEM01", item.DisplayName);
    }

    [Fact]
    public void Build_FallsBackToProductCodeWhenDisplayNameAndItemNumberAreBlank()
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "   ", null, "BAR01", 10m, null)],
            [],
            [],
            [],
            []));

        var item = Assert.Single(items);
        Assert.Equal("P01", item.DisplayName);
    }

    [Fact]
    public void Build_FallsBackToProductBasePriceWhenStoreRetailPriceMissing()
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple", null, "BAR01", 10m, null, ReferenceCode: "PRODUCT-UUID-01")],
            [],
            [],
            [],
            []));

        var item = Assert.Single(items);
        Assert.Equal(10m, item.RetailPrice);
        Assert.Equal(PriceSourceKind.ProductBase, item.PriceSource);
        Assert.Equal("PRODUCT-UUID-01", item.ReferenceCode);
    }

    [Fact]
    public void Build_UsesClearanceBarcodePrice()
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple", null, "BAR01", 10m, null)],
            [],
            [],
            [new StoreClearancePriceRecord("P01", "CLR01", 3m, null, ReferenceCode: "CLR-UUID-01")],
            []));

        var clearanceItem = Assert.Single(items, x => x.LookupCode == "CLR01");
        Assert.Equal(3m, clearanceItem.RetailPrice);
        Assert.Equal(PriceSourceKind.StoreClearancePrice, clearanceItem.PriceSource);
        Assert.Equal("CLR-UUID-01", clearanceItem.ReferenceCode);
    }

    [Fact]
    public void Build_UsesStoreMultiCodePriceBeforeSetRetailPrice()
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple Set", null, null, 10m, null)],
            [],
            [new StoreMultiCodeProductRecord("P01", "SET-P01", null, 7m, null, ReferenceCode: "SMCP-UUID-01")],
            [],
            [new ProductSetCodeRecord("P01", "SET-P01", "SETBAR01", 12m, null, ReferenceCode: "SET-UUID-01")]));

        var setItem = Assert.Single(items);
        Assert.Equal(7m, setItem.RetailPrice);
        Assert.Equal(PriceSourceKind.StoreMultiCodeProduct, setItem.PriceSource);
        Assert.Equal("SMCP-UUID-01", setItem.ReferenceCode);
    }

    [Fact]
    public void Build_FallsBackToSetRetailPriceWhenStoreMultiCodePriceMissing()
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple Set", null, null, 10m, null)],
            [],
            [],
            [],
            [new ProductSetCodeRecord("P01", "SET-P01", "SETBAR01", 12m, null, ReferenceCode: "SET-UUID-01")]));

        var setItem = Assert.Single(items);
        Assert.Equal(12m, setItem.RetailPrice);
        Assert.Equal(PriceSourceKind.ProductSetCode, setItem.PriceSource);
        Assert.Equal("SET-UUID-01", setItem.ReferenceCode);
    }

    [Fact]
    public void Build_UsesMultiBarcodePrice()
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple", null, null, 10m, null)],
            [],
            [new StoreMultiCodeProductRecord("P01", "M01", "MULTI01", 6m, null, ReferenceCode: "SMCP-UUID-01")],
            [],
            []));

        var item = Assert.Single(items);
        Assert.Equal(6m, item.RetailPrice);
        Assert.Equal(PriceSourceKind.StoreMultiCodeProduct, item.PriceSource);
        Assert.Equal("SMCP-UUID-01", item.ReferenceCode);
    }

    [Fact]
    public void Build_NormalizesLookupCodesBeforeDeduplicating()
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple", "ABC01", " abc01 ", 10m, null)],
            [],
            [],
            [],
            []));

        var item = Assert.Single(items);
        Assert.Equal("abc01", item.LookupCode);
    }

    [Fact]
    public void BuildWithCodeConflicts_ReturnsEachProductBestCandidateWhenSetCodeCollidesWithOtherProductBarcode()
    {
        // 生产案例：flower 的主条码被另一个商品（Fly Swatter）登记成了套装条码。
        var input = new PriceIndexInput(
            null,
            [
                new ProductPriceRecord("P-FLOWER", "flower", "9040147", "6405090401470", 2.99m, null),
                new ProductPriceRecord("P-FLY", "EXTENSION Fly Swatter", "HB294-002", null, 3m, null)
            ],
            [new StoreRetailPriceRecord("P-FLOWER", 3.49m, null, ReferenceCode: "SRP-FLOWER")],
            [],
            [],
            [new ProductSetCodeRecord("P-FLY", "HB294-002-5363DA", "6405090401470", 8.99m, null, ReferenceCode: "SET-FLY")]);

        var output = _builder.BuildWithCodeConflicts("S01", input);

        var winner = Assert.Single(output.Items, x => x.LookupCode == "6405090401470");
        Assert.Equal("P-FLY", winner.ProductCode);
        Assert.Collection(
            output.CodeConflicts,
            first =>
            {
                Assert.Same(winner, first);
                Assert.Equal(8.99m, first.RetailPrice);
            },
            second =>
            {
                Assert.Equal("P-FLOWER", second.ProductCode);
                Assert.Equal("6405090401470", second.LookupCode);
                Assert.Equal(3.49m, second.RetailPrice);
                Assert.Equal(PriceSourceKind.StoreRetailPrice, second.PriceSource);
            });
    }

    [Fact]
    public void BuildWithCodeConflicts_IgnoresMultipleSourcesOfTheSameProduct()
    {
        var output = _builder.BuildWithCodeConflicts("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple", "ITEM01", "BAR01", 10m, null)],
            [],
            [],
            [new StoreClearancePriceRecord("p01", "bar01", 5m, null)],
            []));

        var item = Assert.Single(output.Items, x => x.LookupCode.Equals("BAR01", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PriceSourceKind.StoreClearancePrice, item.PriceSource);
        Assert.Empty(output.CodeConflicts);
    }

    [Fact]
    public void BuildWithCodeConflicts_TreatsNormalizedLookupCodesAsTheSameCode()
    {
        var output = _builder.BuildWithCodeConflicts("S01", new PriceIndexInput(
            null,
            [
                new ProductPriceRecord("P01", "Apple", null, " abc01 ", 10m, DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
                new ProductPriceRecord("P02", "Banana", null, "ABC01", 12m, DateTimeOffset.Parse("2026-09-02T00:00:00Z"))
            ],
            [],
            [],
            [],
            []));

        var winner = Assert.Single(output.Items);
        Assert.Equal("P02", winner.ProductCode);
        Assert.Equal(["P02", "P01"], output.CodeConflicts.Select(x => x.ProductCode).ToArray());
    }

    [Fact]
    public void BuildWithCodeConflicts_ItemsAreIdenticalToBuild()
    {
        var input = new PriceIndexInput(
            null,
            [
                new ProductPriceRecord("P01", "Apple", "ITEM01", "BAR01", 10m, DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
                new ProductPriceRecord("P02", "Banana", "ITEM02", "BAR01", 11m, DateTimeOffset.Parse("2026-09-03T00:00:00Z")),
                new ProductPriceRecord("P03", "Cherry", "BAR02", "BAR03", 12m, null)
            ],
            [new StoreRetailPriceRecord("P01", 9m, DateTimeOffset.Parse("2026-09-02T00:00:00Z"))],
            [new StoreMultiCodeProductRecord("P03", "M03", "ITEM01", 6m, null)],
            [new StoreClearancePriceRecord("P02", "BAR02", 4m, null)],
            [new ProductSetCodeRecord("P01", "SET-P01", "ITEM02", 20m, null)]);

        var output = _builder.BuildWithCodeConflicts("S01", input);

        // 冲突收集只能附加信息，决胜结果必须与原 Build 完全一致，目录分页和 checksum 才不会变化。
        Assert.Equal(_builder.Build("S01", input), output.Items);
        Assert.Equal(
            ["BAR01", "BAR02", "ITEM01", "ITEM02"],
            output.CodeConflicts.Select(x => x.LookupCode).Distinct().ToArray());
    }
}
