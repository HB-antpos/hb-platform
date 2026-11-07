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
    [InlineData(50, 0.5)]
    [InlineData(99, 0.99)]
    [InlineData(0.99, 0.99)]
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

    // 折扣保护：1（歧义：1% 还是 100%）与 100（100%）都会把整行折成 $0，一律不下发（1002 店 10-24/25 事故）。
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(100)]
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
    [InlineData(99, 0.99)]
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

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Build_IgnoresFullDiscountRateOnStoreMultiCodeProduct(decimal sourceDiscountRate)
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple", null, null, 10m, null)],
            [],
            [new StoreMultiCodeProductRecord("P01", "M01", "MULTI01", 6m, null, ReferenceCode: "SMCP-UUID-01", DiscountRate: sourceDiscountRate)],
            [],
            []));

        var item = Assert.Single(items);
        Assert.Null(item.DiscountRate);
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
    public void Build_SetCodeWithoutOwnPriceFollowsMainStoreRetailPrice()
    {
        // 复现 SW0126：副条码 SetRetailPrice 为 NULL、门店多码价也为 NULL，应按主条码门店价走，而不是 $0.00。
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Wrapping Paper", "SW0126", "MAIN01", 4m, null)],
            [new StoreRetailPriceRecord("P01", 3.5m, null, ReferenceCode: "SRP-UUID-01", DiscountRate: 0.1m)],
            [new StoreMultiCodeProductRecord("P01", "SET-P01", "SETBAR01", null, null, ReferenceCode: "SMCP-UUID-01")],
            [],
            [new ProductSetCodeRecord("P01", "SET-P01", "SETBAR01", null, null, ReferenceCode: "SET-UUID-01")]));

        var setItem = Assert.Single(items, x => x.LookupCode == "SETBAR01");
        Assert.Equal(3.5m, setItem.RetailPrice);
        Assert.Equal(PriceSourceKind.ProductSetCode, setItem.PriceSource);
        Assert.Equal("SET-UUID-01", setItem.ReferenceCode);
        Assert.Equal(0.1m, setItem.DiscountRate);
        Assert.Equal("P01", setItem.ProductCode);
        Assert.False(setItem.IsSpecialProduct);
    }

    [Fact]
    public void Build_SetCodeWithNullPriceFallsBackToProductBasePrice() => AssertSetFallsBackToProductBasePrice(null);

    [Fact]
    public void Build_SetCodeWithZeroPriceFallsBackToProductBasePrice() => AssertSetFallsBackToProductBasePrice(0m);

    private void AssertSetFallsBackToProductBasePrice(decimal? setRetailPrice)
    {
        var items = _builder.Build("S01", new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Apple Set", null, null, 10m, null)],
            [],
            [],
            [],
            [new ProductSetCodeRecord("P01", "SET-P01", "SETBAR01", setRetailPrice, null, ReferenceCode: "SET-UUID-01")]));

        var setItem = Assert.Single(items);
        Assert.Equal(10m, setItem.RetailPrice);
        Assert.Equal(PriceSourceKind.ProductSetCode, setItem.PriceSource);
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
    public void BuildWithCodeConflicts_ReturnsMainAndSetCandidatesWhenSameProductSetCodeEqualsMainBarcodeWithDifferentPrice()
    {
        // 生产案例（SN81796 一类）：套装码登记成了商品自己的主条码，套装价 7.50 与主条码店价 85.00 不同，
        // 套装码优先级更高，WPF 会静默按 7.50 收；必须把两条都作为候选下发让收银员选。
        var input = new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Hot Stamp Set", "HB258-354", "9527825800277", 85m, null)],
            [new StoreRetailPriceRecord("P01", 85m, null)],
            [],
            [],
            [new ProductSetCodeRecord("P01", "SET-P01", "9527825800277", 7.5m, null)]);

        var output = _builder.BuildWithCodeConflicts("S01", input);

        var winner = Assert.Single(output.Items, x => x.LookupCode == "9527825800277");
        Assert.Equal(PriceSourceKind.ProductSetCode, winner.PriceSource);
        Assert.Collection(
            output.CodeConflicts,
            first =>
            {
                // 首条仍是目录胜出项（套装价），与目录一致。
                Assert.Same(winner, first);
                Assert.Equal(7.5m, first.RetailPrice);
            },
            second =>
            {
                Assert.Equal("P01", second.ProductCode);
                Assert.Equal(PriceSourceKind.StoreRetailPrice, second.PriceSource);
                Assert.Equal(85m, second.RetailPrice);
            });
    }

    [Fact]
    public void BuildWithCodeConflicts_IgnoresSameProductMainAndSetCodeWhenPricesAreEqual()
    {
        // 本次截图里的 9527825800277：主条码与套装码都是 40.00，弹窗只会多出两张一模一样的卡片，不弹。
        var input = new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Hot Stamp Gift Box Set Of 10", "HB258-354", "9527825800277", 40m, null)],
            [new StoreRetailPriceRecord("P01", 40m, null)],
            [],
            [],
            [new ProductSetCodeRecord("P01", "SET-P01", "9527825800277", 40m, null)]);

        var output = _builder.BuildWithCodeConflicts("S01", input);

        Assert.Empty(output.CodeConflicts);
    }

    [Fact]
    public void BuildWithCodeConflicts_IgnoresSetCodeThatFollowsMainPrice()
    {
        // 套装码自己没有价格时跟随主条码价（set-follow-main），两条价格天然一致，不是冲突。
        var input = new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Set", "ITEM01", "BAR01", 12m, null)],
            [new StoreRetailPriceRecord("P01", 12m, null)],
            [],
            [],
            [new ProductSetCodeRecord("P01", "SET-P01", "BAR01", null, null)]);

        var output = _builder.BuildWithCodeConflicts("S01", input);

        Assert.Empty(output.CodeConflicts);
    }

    [Fact]
    public void BuildWithCodeConflicts_KeepsClearancePriceOverridingSameProductMainAndSetCode()
    {
        // 清货价是有意覆盖本身条码的价格，不能因为主条码、套装码价格不同就弹窗。
        var input = new PriceIndexInput(
            null,
            [new ProductPriceRecord("P01", "Set", "ITEM01", "BAR01", 85m, null)],
            [new StoreRetailPriceRecord("P01", 85m, null)],
            [],
            [new StoreClearancePriceRecord("P01", "BAR01", 5m, null)],
            [new ProductSetCodeRecord("P01", "SET-P01", "BAR01", 7.5m, null)]);

        var output = _builder.BuildWithCodeConflicts("S01", input);

        var winner = Assert.Single(output.Items, x => x.LookupCode == "BAR01");
        Assert.Equal(PriceSourceKind.StoreClearancePrice, winner.PriceSource);
        Assert.Empty(output.CodeConflicts);
    }

    [Fact]
    public void BuildWithCodeConflicts_AddsSameProductAlternativeNextToItsBestCandidateAlongsideOtherProducts()
    {
        // 既有"另一个商品也用这个码"，又有"同商品主条码 vs 套装价格不同"：每个商品最优在前，同商品的另一条紧跟。
        var input = new PriceIndexInput(
            null,
            [
                new ProductPriceRecord("P01", "Set One", "ITEM01", "BAR01", 85m, null),
                new ProductPriceRecord("P02", "Other", "ITEM02", null, 3m, null)
            ],
            [new StoreRetailPriceRecord("P01", 85m, null)],
            [],
            [],
            [
                new ProductSetCodeRecord("P01", "SET-P01", "BAR01", 7.5m, null),
                new ProductSetCodeRecord("P02", "SET-P02", "BAR01", 2m, null)
            ]);

        var output = _builder.BuildWithCodeConflicts("S01", input);

        var winner = Assert.Single(output.Items, x => x.LookupCode == "BAR01");
        Assert.Same(winner, output.CodeConflicts[0]);
        Assert.Equal(3, output.CodeConflicts.Count);
        // P01 的两条候选相邻、套装在前主条码在后。
        var p01 = output.CodeConflicts.Where(x => x.ProductCode == "P01").ToArray();
        Assert.Equal(2, p01.Length);
        Assert.Equal(
            [PriceSourceKind.ProductSetCode, PriceSourceKind.StoreRetailPrice],
            p01.Select(x => x.PriceSource).ToArray());
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
