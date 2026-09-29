using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class PosCartServiceTests
{
    [Fact]
    public void AddItem_merges_same_store_lookup_with_different_price_and_preserves_quantity()
    {
        var cart = new PosCartService();

        cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "abc-001", price: 10m));
        cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: " ABC-001 ", price: 12m));

        var line = Assert.Single(cart.Lines);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal("ABC-001", line.LookupCodeNormalized);
    }

    [Fact]
    public void AddItem_keeps_separate_lines_for_different_products_sharing_a_lookup_code()
    {
        // 条码 6405090401470 同时是 Fly Swatter 套装码与 flower 主条码：选不同商品必须分行、各按各的价。
        var cart = new PosCartService();

        var fly = cart.AddItem(CreateItem(productCode: "P-FLY", lookupCode: "6405090401470", displayName: "EXTENSION Fly Swatter", price: 8.99m));
        var flower = cart.AddItem(CreateItem(productCode: "P-FLOWER", lookupCode: "6405090401470", displayName: "flower", price: 2.99m));
        var secondFly = cart.AddItem(CreateItem(productCode: "P-FLY", lookupCode: "6405090401470", displayName: "EXTENSION Fly Swatter", price: 8.99m));

        Assert.Equal(2, cart.Lines.Count);
        Assert.NotSame(fly, flower);
        Assert.Same(fly, secondFly);
        Assert.Equal(("P-FLY", "EXTENSION Fly Swatter", 8.99m, 2m), (fly.ProductCode, fly.DisplayName, fly.UnitPrice, fly.Quantity));
        Assert.Equal(("P-FLOWER", "flower", 2.99m, 1m), (flower.ProductCode, flower.DisplayName, flower.UnitPrice, flower.Quantity));
        Assert.Equal(20.97m, cart.TotalAmount);
    }

    [Fact]
    public void AddItem_applies_catalog_discount_in_away_from_zero_cents_and_recalculates_quantity()
    {
        var cart = new PosCartService();

        var line = cart.AddItem(CreateItem(price: 6.99m, discountRate: 0.2m));

        Assert.Equal(2000, (int?)typeof(CartLine)
            .GetProperty("CatalogDiscountBasisPoints")
            ?.GetValue(line));
        Assert.Equal(3, (int)line.DiscountSource);
        Assert.Equal(6.99m, line.UnitPrice);
        Assert.Equal(1.40m, line.DiscountAmount);
        Assert.Equal(5.59m, line.ActualAmount);

        Assert.True(cart.SetLineQuantity(line, 2m));

        Assert.Equal(2.80m, line.DiscountAmount);
        Assert.Equal(11.18m, line.ActualAmount);
        Assert.Equal(11.18m, cart.ActualAmount);
    }

    [Fact]
    public void Catalog_discount_is_independent_of_special_product_flag()
    {
        var cart = new PosCartService();

        var line = cart.AddItem(CreateItem(price: 6.99m, discountRate: 0.2m, isSpecialProduct: true));

        Assert.Equal(1.40m, line.DiscountAmount);
        Assert.Equal(5.59m, line.ActualAmount);
    }

    [Fact]
    public void Repeated_search_and_scan_additions_preserve_manual_price_and_catalog_baseline()
    {
        var searchCart = new PosCartService();
        var searchLine = searchCart.AddItem(CreateItem(price: 10m, discountRate: 0.2m));
        Assert.True(searchCart.SetLineUnitPrice(searchLine, 5m));

        searchCart.AddItem(CreateItem(
            referenceCode: "REF-SEARCH-NEW",
            displayName: "Fresh Search Milk",
            itemNumber: "ITEM-SEARCH-NEW",
            price: 20m,
            priceSource: PriceSourceKind.StoreClearancePrice,
            productImage: "https://images.example/search-new.jpg",
            discountRate: 0.1m));

        Assert.Equal(2m, searchLine.Quantity);
        Assert.Equal(5m, searchLine.UnitPrice);
        Assert.True(searchLine.IsManualPrice);
        Assert.Equal("SKU-001", searchLine.ProductCode);
        Assert.Equal("REF-SEARCH-NEW", searchLine.ReferenceCode);
        Assert.Equal("Fresh Search Milk", searchLine.DisplayName);
        Assert.Equal("ITEM-SEARCH-NEW", searchLine.ItemNumber);
        Assert.Equal("https://images.example/search-new.jpg", searchLine.ProductImage);
        Assert.Equal(PriceSourceKind.StoreRetailPrice, searchLine.PriceSource);
        Assert.Equal(1000, (int?)typeof(CartLine)
            .GetProperty("CatalogDiscountBasisPoints")
            ?.GetValue(searchLine));
        Assert.Equal(1m, searchLine.DiscountAmount);

        var scanCart = new PosCartService();
        var scanLine = scanCart.AddConsecutiveItem(CreateItem(price: 10m, discountRate: 0.2m));
        Assert.True(scanCart.SetLineUnitPrice(scanLine, 5m));

        scanCart.AddConsecutiveItem(CreateItem(
            referenceCode: "REF-SCAN-NEW",
            displayName: "Fresh Scan Milk",
            itemNumber: "ITEM-SCAN-NEW",
            price: 20m,
            priceSource: PriceSourceKind.StoreClearancePrice,
            productImage: "https://images.example/scan-new.jpg",
            discountRate: 0.1m));

        Assert.Equal(2m, scanLine.Quantity);
        Assert.Equal(5m, scanLine.UnitPrice);
        Assert.True(scanLine.IsManualPrice);
        Assert.Equal("SKU-001", scanLine.ProductCode);
        Assert.Equal("REF-SCAN-NEW", scanLine.ReferenceCode);
        Assert.Equal("Fresh Scan Milk", scanLine.DisplayName);
        Assert.Equal("ITEM-SCAN-NEW", scanLine.ItemNumber);
        Assert.Equal("https://images.example/scan-new.jpg", scanLine.ProductImage);
        Assert.Equal(PriceSourceKind.StoreRetailPrice, scanLine.PriceSource);
        Assert.Equal(1000, (int?)typeof(CartLine)
            .GetProperty("CatalogDiscountBasisPoints")
            ?.GetValue(scanLine));
        Assert.Equal(1m, scanLine.DiscountAmount);
    }

    [Fact]
    public void AddItem_after_shared_snapshot_restore_does_not_refresh_frozen_catalog_values()
    {
        var sourceCart = new PosCartService();
        sourceCart.AddItem(CreateItem(price: 10m, discountRate: 0.1m));

        var restoredCart = new PosCartService();
        restoredCart.RestoreSharedSaleSnapshot(sourceCart.CreateSnapshot());

        restoredCart.AddItem(CreateItem(price: 99m, discountRate: 0.2m));

        var line = Assert.Single(restoredCart.Lines);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(10m, line.UnitPrice);
        Assert.Equal(2m, line.DiscountAmount);
        Assert.Equal(1000, (int?)typeof(CartLine)
            .GetProperty("CatalogDiscountBasisPoints")
            ?.GetValue(line));
    }

    [Fact]
    public void UpdateLineFromRemote_refreshes_catalog_discount_when_price_is_unchanged_and_removes_it()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m, discountRate: 0.1m));

        Assert.Equal(1m, line.DiscountAmount);

        Assert.True(cart.UpdateLineFromRemote(CreateItem(price: 10m, discountRate: 0.2m)));

        Assert.Equal(2m, line.DiscountAmount);
        Assert.Equal(2000, (int?)typeof(CartLine)
            .GetProperty("CatalogDiscountBasisPoints")
            ?.GetValue(line));

        Assert.True(cart.UpdateLineFromRemote(CreateItem(price: 10m, discountRate: null)));

        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal(0, (int)line.DiscountSource);
    }

    [Fact]
    public void Manual_discount_override_zero_restores_catalog_and_catalog_does_not_stack_with_promotion()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-CATALOG", price: 10m, discountRate: 0.2m));

        cart.SetAutomaticPromotionRules(
        [
            CreatePromotionRule(
                applyQuantity: 1,
                fixedPrice: 5m,
                products: [new CatalogPromotionProductDto("SKU-CATALOG", 1)])
        ]);

        Assert.Equal(2m, line.DiscountAmount);
        Assert.Equal(3, (int)line.DiscountSource);

        Assert.True(cart.SetLineDiscountAmount(line, 3m));
        Assert.Equal(3m, line.DiscountAmount);
        Assert.Equal(1, (int)line.DiscountSource);

        Assert.True(cart.SetLineDiscountAmount(line, 0m));

        Assert.Equal(2m, line.DiscountAmount);
        Assert.Equal(3, (int)line.DiscountSource);
    }

    [Fact]
    public void Automatic_fixed_price_excludes_catalog_baseline_even_when_its_cent_discount_rounds_to_zero()
    {
        var cart = new PosCartService();
        var catalogLine = cart.AddItem(CreateItem(
            productCode: "SKU-CATALOG-TINY",
            lookupCode: "CATALOG-TINY-001",
            price: 0.01m,
            discountRate: 0.0001m));
        var normalLine = cart.AddItem(CreateItem(
            productCode: "SKU-NORMAL-TINY",
            lookupCode: "NORMAL-TINY-001",
            price: 0.01m));

        cart.SetAutomaticPromotionRules(
        [
            CreatePromotionRule(
                applyQuantity: 2,
                fixedPrice: 0.01m,
                products:
                [
                    new CatalogPromotionProductDto("SKU-CATALOG-TINY", 1),
                    new CatalogPromotionProductDto("SKU-NORMAL-TINY", 1)
                ])
        ]);

        Assert.Equal(1, catalogLine.CatalogDiscountBasisPoints);
        Assert.Equal(0m, catalogLine.DiscountAmount);
        Assert.Equal(0m, normalLine.DiscountAmount);
        Assert.False(catalogLine.IsAutomaticPromotionDiscount);
        Assert.False(normalLine.IsAutomaticPromotionDiscount);
    }

    [Fact]
    public void Automatic_fixed_price_uses_unit_weight_only_for_threshold_count_not_group_total()
    {
        var cart = new PosCartService();
        var weightedLine = cart.AddItem(CreateItem(
            productCode: "SKU-WEIGHTED",
            lookupCode: "WEIGHTED-001",
            price: 12m));
        var regularLine = cart.AddItem(CreateItem(
            productCode: "SKU-REGULAR",
            lookupCode: "REGULAR-001",
            price: 6m));

        cart.SetAutomaticPromotionRules(
        [
            CreatePromotionRule(
                applyQuantity: 3,
                fixedPrice: 15m,
                products:
                [
                    new CatalogPromotionProductDto("SKU-WEIGHTED", 2),
                    new CatalogPromotionProductDto("SKU-REGULAR", 1)
                ])
        ]);

        // UnitWeight=2 只让 SKU-WEIGHTED 在阈值计数里占 2 件、凑满 ApplyQuantity=3，
        // 组内原价仍只能计一次 12.00：18.00 - 15.00 = 3.00 折扣，按原价比例分摊为 2.00 / 1.00。
        // 若权重展开单位被重复计价，原价会变成 30.00、折扣放大到 15.00，应收从 15.00 掉到 3.00。
        Assert.Equal(2m, weightedLine.DiscountAmount);
        Assert.Equal(1m, regularLine.DiscountAmount);
        Assert.Equal(3m, weightedLine.DiscountAmount + regularLine.DiscountAmount);
        Assert.True(weightedLine.IsAutomaticPromotionDiscount);
        Assert.True(regularLine.IsAutomaticPromotionDiscount);
    }

    [Fact]
    public void Order_discount_zero_allocation_overrides_catalog_and_snapshot_preserves_the_override()
    {
        var cart = new PosCartService();
        var first = cart.AddItem(CreateItem(productCode: "SKU-ORDER-1", lookupCode: "ORDER-1", price: 0.01m));
        var second = cart.AddItem(CreateItem(productCode: "SKU-ORDER-2", lookupCode: "ORDER-2", price: 0.01m));
        var catalog = cart.AddItem(CreateItem(
            productCode: "SKU-ORDER-CATALOG",
            lookupCode: "ORDER-CATALOG",
            price: 0.01m,
            discountRate: 1m));

        Assert.Equal(0.01m, catalog.DiscountAmount);
        Assert.True(cart.SetOrderDiscountPercent(50m));

        Assert.Equal(0.01m, first.DiscountAmount);
        Assert.Equal(0.01m, second.DiscountAmount);
        Assert.Equal(0m, catalog.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Manual, catalog.DiscountSource);
        Assert.Equal(0.02m, cart.DiscountAmount);

        var snapshot = cart.CreateSnapshot();

        var restoredCart = new PosCartService();
        restoredCart.RestoreSnapshot(snapshot);
        var restoredCatalog = restoredCart.Lines.Single(line => line.ProductCode == "SKU-ORDER-CATALOG");

        Assert.Equal(0m, restoredCatalog.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Manual, restoredCatalog.DiscountSource);
        Assert.Equal(0.02m, restoredCart.DiscountAmount);

        // 用户主动清零单行手工折扣时，目录基线仍应恢复。
        Assert.True(restoredCart.SetLineDiscountAmount(restoredCatalog, 0m));
        Assert.Equal(0.01m, restoredCatalog.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Catalog, restoredCatalog.DiscountSource);
    }

    [Fact]
    public void Manual_percent_that_rounds_to_zero_keeps_manual_provenance_and_recalculates_after_quantity_change()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-MANUAL-TINY", lookupCode: "MANUAL-TINY", price: 0.01m));

        Assert.True(cart.SetLineDiscountPercent(line, 1m));
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal(1m, line.DiscountPercent);
        Assert.Equal(CartLineDiscountSource.Manual, line.DiscountSource);

        cart.SetAutomaticPromotionRules(
        [
            CreatePromotionRule(
                applyQuantity: 1,
                fixedPrice: 0m,
                products: [new CatalogPromotionProductDto("SKU-MANUAL-TINY", 1)])
        ]);

        Assert.Equal(CartLineDiscountSource.Manual, line.DiscountSource);
        Assert.Equal(0m, line.DiscountAmount);

        Assert.True(cart.SetLineQuantity(line, 200m));
        Assert.Equal(0.02m, line.DiscountAmount);
        Assert.Equal(1m, line.DiscountPercent);
        Assert.Equal(CartLineDiscountSource.Manual, line.DiscountSource);
    }

    [Fact]
    public void AddConsecutiveItem_merges_only_last_matching_sale_line()
    {
        var cart = new PosCartService();

        var firstApple = cart.AddConsecutiveItem(CreateItem(productCode: "SKU-A", lookupCode: "apple-001", displayName: "Apple", price: 10m));
        var banana = cart.AddConsecutiveItem(CreateItem(productCode: "SKU-B", lookupCode: "banana-001", displayName: "Banana", price: 12m));
        var secondApple = cart.AddConsecutiveItem(CreateItem(productCode: "SKU-A", lookupCode: " apple-001 ", displayName: "Apple", price: 11m));
        var mergedApple = cart.AddConsecutiveItem(CreateItem(productCode: "SKU-A", lookupCode: "APPLE-001", displayName: "Apple", price: 13m));

        Assert.Equal(3, cart.Lines.Count);
        Assert.Same(firstApple, cart.Lines[0]);
        Assert.Same(banana, cart.Lines[1]);
        Assert.Same(secondApple, cart.Lines[2]);
        Assert.NotSame(firstApple, secondApple);
        Assert.Same(secondApple, mergedApple);
        Assert.Equal(1m, firstApple.Quantity);
        Assert.Equal(1m, banana.Quantity);
        Assert.Equal(2m, secondApple.Quantity);
        Assert.Equal("APPLE-001", secondApple.LookupCodeNormalized);
    }

    [Fact]
    public void AddConsecutiveItem_does_not_merge_a_different_product_with_the_same_lookup_code()
    {
        var cart = new PosCartService();

        var fly = cart.AddConsecutiveItem(CreateItem(productCode: "P-FLY", lookupCode: "6405090401470", displayName: "EXTENSION Fly Swatter", price: 8.99m));
        var flower = cart.AddConsecutiveItem(CreateItem(productCode: "P-FLOWER", lookupCode: "6405090401470", displayName: "flower", price: 2.99m));
        var mergedFlower = cart.AddConsecutiveItem(CreateItem(productCode: "P-FLOWER", lookupCode: "6405090401470", displayName: "flower", price: 2.99m));

        Assert.Equal(2, cart.Lines.Count);
        Assert.NotSame(fly, flower);
        Assert.Same(flower, mergedFlower);
        Assert.Equal(("P-FLY", 8.99m, 1m), (fly.ProductCode, fly.UnitPrice, fly.Quantity));
        Assert.Equal(("P-FLOWER", 2.99m, 2m), (flower.ProductCode, flower.UnitPrice, flower.Quantity));
    }

    [Fact]
    public void UpdateLineFromRemote_refreshes_display_price_source_and_totals()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem(
            productCode: "SKU-001",
            lookupCode: "690001",
            displayName: "Old Milk",
            price: 10m,
            priceSource: PriceSourceKind.ProductBase,
            productImage: "https://images.example/old-milk.jpg"));
        cart.AddItem(CreateItem(
            productCode: "SKU-001",
            lookupCode: "690001",
            displayName: "Old Milk",
            price: 10m,
            priceSource: PriceSourceKind.ProductBase));

        var updated = CreateItem(
            productCode: "SKU-001",
            lookupCode: "REMOTE-690001",
            displayName: "Fresh Milk",
            price: 12.5m,
            priceSource: PriceSourceKind.StoreClearancePrice,
            productImage: "https://images.example/fresh-milk.jpg");

        Assert.True(cart.UpdateLineFromRemote("S001", "690001", updated));

        var line = Assert.Single(cart.Lines);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal("Fresh Milk", line.DisplayName);
        Assert.Equal("https://images.example/fresh-milk.jpg", line.ProductImage);
        Assert.Equal("REMOTE-690001", line.LookupCodeNormalized);
        Assert.Equal(12.5m, line.UnitPrice);
        Assert.Equal(PriceSourceKind.StoreClearancePrice, line.PriceSource);
        Assert.Equal(nameof(PriceSourceKind.StoreClearancePrice), line.PriceSourceLabel);
        Assert.Equal(25m, cart.TotalAmount);
        Assert.Equal(25m, cart.ActualAmount);
    }

    [Fact]
    public void RemoveLineByLookupCode_removes_only_matching_store_lookup()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem(storeCode: "S001", productCode: "SKU-001", lookupCode: "690001", price: 10m));
        cart.AddItem(CreateItem(storeCode: "S001", productCode: "SKU-002", lookupCode: "690002", price: 20m));
        cart.AddItem(CreateItem(storeCode: "S002", productCode: "SKU-003", lookupCode: "690001", price: 30m));

        Assert.True(cart.RemoveLineByLookupCode("S001", " 690001 "));

        Assert.Equal(2, cart.Lines.Count);
        Assert.Null(cart.FindLineByLookupCode("S001", "690001"));
        Assert.NotNull(cart.FindLineByLookupCode("S001", "690002"));
        Assert.NotNull(cart.FindLineByLookupCode("S002", "690001"));
        Assert.Equal(50m, cart.TotalAmount);
    }

    [Fact]
    public void RemoveLine_removes_only_the_given_cart_line()
    {
        var cart = new PosCartService();
        var first = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));
        var second = cart.AddItem(CreateItem(productCode: "SKU-002", lookupCode: "690002", price: 20m));

        Assert.True(cart.RemoveLine(first));

        var line = Assert.Single(cart.Lines);
        Assert.Same(second, line);
        Assert.Equal(20m, cart.TotalAmount);
    }

    [Fact]
    public void RemoveLine_removes_the_entire_line_when_quantity_is_greater_than_one()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));
        cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));

        Assert.Equal(2m, line.Quantity);
        Assert.True(cart.RemoveLine(line));

        Assert.Empty(cart.Lines);
        Assert.Equal(0m, cart.TotalAmount);
    }

    [Fact]
    public void AddItem_does_not_merge_same_product_with_different_lookup()
    {
        var cart = new PosCartService();

        cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));
        cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690002", price: 10m));

        Assert.Equal(2, cart.Lines.Count);
        Assert.Contains(cart.Lines, line => line.LookupCodeNormalized == "690001");
        Assert.Contains(cart.Lines, line => line.LookupCodeNormalized == "690002");
    }

    [Fact]
    public void AddOpenItem_always_creates_independent_editable_lines()
    {
        var cart = new PosCartService();
        var openItem = CreateItem(productCode: "OPEN-SKU", lookupCode: "OPENITEM", price: 0m);

        var first = cart.AddOpenItem(openItem, 12.34m);
        var second = cart.AddOpenItem(openItem, 12.34m);

        Assert.NotSame(first, second);
        Assert.Equal(2, cart.Lines.Count);
        Assert.All(cart.Lines, line =>
        {
            Assert.Equal(CartLineKind.OpenItem, line.Kind);
            Assert.False(line.IsReturnLine);
            Assert.False(line.IsLocked);
            Assert.Equal("OPENITEM", line.LookupCodeNormalized);
            Assert.Equal(1m, line.Quantity);
            Assert.Equal(12.34m, line.UnitPrice);
        });
    }

    [Fact]
    public void Snapshot_and_restore_preserve_open_item_kind()
    {
        var cart = new PosCartService();
        var openItem = CreateItem(productCode: "OPEN-SKU", lookupCode: "OPENITEM", price: 0m);
        cart.AddOpenItem(openItem, 7.89m);
        var snapshot = cart.CreateSnapshot();
        cart.Clear();

        cart.RestoreSnapshot(snapshot);
        cart.AddOpenItem(openItem, 7.89m);

        Assert.Equal(2, cart.Lines.Count);
        Assert.All(cart.Lines, line => Assert.Equal(CartLineKind.OpenItem, line.Kind));
    }

    [Fact]
    public void IncreaseLine_adds_one_unit_and_recalculates_totals()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));

        Assert.True(cart.IncreaseLine(line));

        Assert.Equal(2m, line.Quantity);
        Assert.Equal(20m, cart.TotalAmount);
        Assert.Equal(20m, cart.ActualAmount);
    }

    [Fact]
    public void DecreaseLine_removes_one_unit_and_recalculates_totals()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));
        cart.IncreaseLine(line);

        Assert.True(cart.DecreaseLine(line));

        line = Assert.Single(cart.Lines);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(10m, cart.TotalAmount);
        Assert.Equal(10m, cart.ActualAmount);
    }

    [Fact]
    public void DecreaseLine_keeps_the_line_when_quantity_is_one()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));

        Assert.False(cart.DecreaseLine(line));

        Assert.Same(line, Assert.Single(cart.Lines));
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(10m, cart.TotalAmount);
        Assert.Equal(10m, cart.ActualAmount);
    }

    [Fact]
    public void SetLineQuantity_and_unit_price_recalculate_totals()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));

        Assert.True(cart.SetLineQuantity(line, 2m));
        Assert.True(cart.SetLineUnitPrice(line, 4.2m));

        Assert.Equal(2m, line.Quantity);
        Assert.Equal(4.2m, line.UnitPrice);
        Assert.Equal(8.4m, line.GrossAmount);
        Assert.Equal(8.4m, cart.TotalAmount);
        Assert.Equal(8.4m, cart.ActualAmount);
    }

    [Fact]
    public void Cart_detects_zero_price_lines_and_updates_after_price_edit()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 0m));

        Assert.True(line.HasZeroUnitPrice);
        Assert.True(cart.HasZeroPriceLine);

        Assert.True(cart.SetLineUnitPrice(line, 4.2m));

        Assert.False(line.HasZeroUnitPrice);
        Assert.False(cart.HasZeroPriceLine);
    }

    [Fact]
    public void SetLineQuantity_rejects_non_integer_quantity()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));

        Assert.False(cart.SetLineQuantity(line, 2.5m));

        Assert.Equal(1m, line.Quantity);
        Assert.False(cart.HasNonIntegerQuantity);
    }

    [Fact]
    public void AddItem_rejects_non_integer_quantity_factor()
    {
        var cart = new PosCartService();

        Assert.Throws<InvalidOperationException>(() => cart.AddItem(CreateItem(quantityFactor: 1.5m)));

        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void SetLineDiscountAmount_clamps_to_current_gross_amount()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));
        cart.SetLineQuantity(line, 2m);

        Assert.True(cart.SetLineDiscountAmount(line, 5m));
        Assert.Equal(5m, line.DiscountAmount);
        Assert.Equal(15m, line.ActualAmount);

        Assert.True(cart.SetLineUnitPrice(line, 2m));

        Assert.Equal(4m, line.GrossAmount);
        Assert.Equal(4m, line.DiscountAmount);
        Assert.Equal(0m, line.ActualAmount);
        Assert.Equal(4m, cart.DiscountAmount);
        Assert.Equal(0m, cart.ActualAmount);
    }

    [Fact]
    public void ApplyPromotionDiscounts_preserves_manual_discounts_and_replaces_old_promotion_discounts()
    {
        var cart = new PosCartService();
        var manualLine = cart.AddItem(CreateItem(productCode: "SKU-MANUAL", lookupCode: "MANUAL-001", price: 20m));
        var stalePromotionLine = cart.AddItem(CreateItem(productCode: "SKU-PROMO-OLD", lookupCode: "PROMO-OLD-001", price: 10m));
        var freshPromotionLine = cart.AddItem(CreateItem(productCode: "SKU-PROMO-NEW", lookupCode: "PROMO-NEW-001", price: 8m));

        Assert.True(cart.SetLineDiscountAmount(manualLine, 3m));
        stalePromotionLine.SetPromotionDiscountAmount(2m);

        cart.ApplyPromotionDiscounts(
        [
            new PromotionLineDiscount(manualLine, 9m),
            new PromotionLineDiscount(freshPromotionLine, 20m)
        ]);

        Assert.Equal(3m, manualLine.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Manual, manualLine.DiscountSource);

        Assert.Equal(0m, stalePromotionLine.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.None, stalePromotionLine.DiscountSource);

        Assert.Equal(8m, freshPromotionLine.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Promotion, freshPromotionLine.DiscountSource);
    }

    [Fact]
    public void SetLineDiscountAmount_zero_resets_manual_source_and_allows_future_promotion()
    {
        var cart = new PosCartService();
        var amountLine = cart.AddItem(CreateItem(productCode: "SKU-RESET-AMOUNT", lookupCode: "RESET-001", price: 10m));
        var percentLine = cart.AddItem(CreateItem(productCode: "SKU-RESET-PERCENT", lookupCode: "RESET-002", price: 10m));

        Assert.True(cart.SetLineDiscountAmount(amountLine, 2m));
        Assert.Equal(CartLineDiscountSource.Manual, amountLine.DiscountSource);

        Assert.True(cart.SetLineDiscountAmount(amountLine, 0m));
        Assert.Equal(CartLineDiscountSource.None, amountLine.DiscountSource);

        Assert.True(cart.SetLineDiscountPercent(percentLine, 10m));
        Assert.Equal(CartLineDiscountSource.Manual, percentLine.DiscountSource);

        Assert.True(cart.SetLineDiscountPercent(percentLine, 0m));
        Assert.Equal(CartLineDiscountSource.None, percentLine.DiscountSource);

        cart.ApplyPromotionDiscounts(
        [
            new PromotionLineDiscount(amountLine, 4m),
            new PromotionLineDiscount(percentLine, 5m)
        ]);

        Assert.Equal(4m, amountLine.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Promotion, amountLine.DiscountSource);
        Assert.Equal(5m, percentLine.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Promotion, percentLine.DiscountSource);
    }

    [Fact]
    public void SetLineDiscountPercent_recalculates_after_quantity_changes()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));
        cart.SetLineQuantity(line, 2m);

        Assert.True(cart.SetLineDiscountPercent(line, 8.5m));

        Assert.Equal(1.70m, line.DiscountAmount);
        Assert.Equal(18.30m, line.ActualAmount);
        Assert.Equal("-8.5%", line.DiscountRateText);

        Assert.True(cart.SetLineQuantity(line, 3m));

        Assert.Equal(2.55m, line.DiscountAmount);
        Assert.Equal(27.45m, line.ActualAmount);
        Assert.Equal(2.55m, cart.DiscountAmount);
        Assert.Equal(27.45m, cart.ActualAmount);
    }

    [Fact]
    public void Line_edit_methods_reject_zero_quantity_and_unreasonable_discounts()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));

        Assert.False(cart.SetLineQuantity(line, 0m));
        Assert.False(cart.SetLineDiscountAmount(line, 10.01m));
        Assert.False(cart.SetLineDiscountPercent(line, 100.01m));

        Assert.Equal(1m, line.Quantity);
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal(10m, cart.ActualAmount);
    }

    [Fact]
    public void Automatic_promotion_rules_apply_quantity_fixed_price_discount()
    {
        var cart = new PosCartService();
        cart.SetAutomaticPromotionRules(
        [
            CreatePromotionRule(
                applyQuantity: 2,
                fixedPrice: 15m,
                products:
                [
                    new CatalogPromotionProductDto("SKU-001", 1)
                ])
        ]);

        var line = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));
        cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));

        Assert.Equal(2m, line.Quantity);
        Assert.True(line.IsAutomaticPromotionDiscount);
        Assert.Equal(5m, line.DiscountAmount);
        Assert.Equal(15m, line.ActualAmount);
        Assert.Equal(5m, cart.DiscountAmount);
        Assert.Equal(15m, cart.ActualAmount);

        Assert.True(cart.DecreaseLine(line));

        line = Assert.Single(cart.Lines);
        Assert.False(line.IsAutomaticPromotionDiscount);
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal(10m, cart.ActualAmount);
    }

    [Fact]
    public void Automatic_promotion_rules_skip_manual_discount_lines()
    {
        var cart = new PosCartService();
        var first = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));
        var second = cart.AddItem(CreateItem(productCode: "SKU-002", lookupCode: "690002", price: 10m));

        Assert.True(cart.SetLineDiscountAmount(first, 2m));
        cart.SetAutomaticPromotionRules(
        [
            CreatePromotionRule(
                applyQuantity: 2,
                fixedPrice: 15m,
                products:
                [
                    new CatalogPromotionProductDto("SKU-001", 1),
                    new CatalogPromotionProductDto("SKU-002", 1)
                ])
        ]);

        Assert.False(first.IsAutomaticPromotionDiscount);
        Assert.Equal(2m, first.DiscountAmount);
        Assert.False(second.IsAutomaticPromotionDiscount);
        Assert.Equal(0m, second.DiscountAmount);
        Assert.Equal(18m, cart.ActualAmount);

        Assert.True(cart.SetLineDiscountAmount(first, 0m));

        Assert.True(first.IsAutomaticPromotionDiscount);
        Assert.Equal(2.5m, first.DiscountAmount);
        Assert.True(second.IsAutomaticPromotionDiscount);
        Assert.Equal(2.5m, second.DiscountAmount);
        Assert.Equal(15m, cart.ActualAmount);
    }

    [Fact]
    public void Automatic_promotion_rules_skip_open_and_return_lines()
    {
        var cart = new PosCartService();
        cart.SetAutomaticPromotionRules(
        [
            CreatePromotionRule(
                applyQuantity: 2,
                fixedPrice: 15m,
                products:
                [
                    new CatalogPromotionProductDto("SKU-001", 1)
                ])
        ]);

        var sale = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));
        var openItem = cart.AddOpenItem(CreateItem(productCode: "SKU-001", lookupCode: "OPEN-001", price: 10m), 10m);
        var returnLine = cart.AddReturnLine(CreateReturnLine("receipt-order-line-1"));

        Assert.Equal(0m, sale.DiscountAmount);
        Assert.Equal(0m, openItem.DiscountAmount);
        Assert.Equal(0m, returnLine.DiscountAmount);
        Assert.Equal(10m, cart.ActualAmount);
    }

    [Fact]
    public void SetOrderDiscountAmount_prorates_one_time_discount_across_lines()
    {
        var cart = new PosCartService();
        var first = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));
        var second = cart.AddItem(CreateItem(productCode: "SKU-002", lookupCode: "690002", price: 30m));

        Assert.True(cart.SetOrderDiscountAmount(8m));

        Assert.Equal(2m, first.DiscountAmount);
        Assert.Equal(6m, second.DiscountAmount);
        Assert.Equal(8m, cart.DiscountAmount);
        Assert.Equal(32m, cart.ActualAmount);
    }

    [Fact]
    public void SetOrderDiscountPercent_applies_one_time_line_amount_discounts()
    {
        var cart = new PosCartService();
        var first = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));
        var second = cart.AddItem(CreateItem(productCode: "SKU-002", lookupCode: "690002", price: 30m));

        Assert.True(cart.SetOrderDiscountPercent(10m));

        Assert.Equal(1m, first.DiscountAmount);
        Assert.Equal(3m, second.DiscountAmount);
        Assert.Equal(36m, cart.ActualAmount);

        Assert.True(cart.SetLineUnitPrice(first, 20m));

        Assert.Equal(1m, first.DiscountAmount);
        Assert.Equal(3m, second.DiscountAmount);
        Assert.Equal(4m, cart.DiscountAmount);
        Assert.Equal(46m, cart.ActualAmount);
    }

    [Fact]
    public void Nonzero_order_percent_rounding_to_zero_still_overrides_catalog_until_cleared()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(
            productCode: "SKU-CATALOG-CENT",
            lookupCode: "CATALOG-CENT",
            price: 0.01m,
            discountRate: 1m));

        Assert.Equal(0.01m, line.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Catalog, line.DiscountSource);

        Assert.True(cart.SetOrderDiscountPercent(0.01m));
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Manual, line.DiscountSource);
        Assert.Equal(0.01m, cart.ActualAmount);

        Assert.True(cart.SetOrderDiscountPercent(0m));
        Assert.Equal(0.01m, line.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Catalog, line.DiscountSource);
        Assert.Equal(0m, cart.ActualAmount);
    }

    [Fact]
    public void Order_discount_methods_reject_unreasonable_discounts()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem(price: 10m));

        Assert.False(cart.SetOrderDiscountAmount(10.01m));
        Assert.False(cart.SetOrderDiscountPercent(100.01m));

        var line = Assert.Single(cart.Lines);
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal(10m, cart.ActualAmount);
    }

    [Fact]
    public void AddReturnLine_creates_locked_negative_line_without_merging_with_sale_line()
    {
        var cart = new PosCartService();
        var sale = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));

        var returned = cart.AddReturnLine(CreateReturnLine("receipt-order-line-1"));

        Assert.Equal(2, cart.Lines.Count);
        Assert.Same(sale, cart.FindLineByLookupCode("S001", "690001"));
        Assert.True(returned.IsReturnLine);
        Assert.True(returned.IsLocked);
        Assert.Equal(-1m, returned.SignedQuantity);
        Assert.Equal(-10m, returned.GrossAmount);
        Assert.Equal(0m, cart.ActualAmount);
        Assert.True(cart.HasReturnLine);
    }

    [Fact]
    public void ReturnLine_merges_only_by_return_source_key_and_can_be_removed()
    {
        var cart = new PosCartService();

        var first = cart.AddReturnLine(CreateReturnLine("receipt-order-line-1"));
        var second = cart.AddReturnLine(CreateReturnLine("receipt-order-line-1"));

        Assert.Same(first, second);
        Assert.Single(cart.Lines);
        Assert.Equal(2m, first.Quantity);
        Assert.Equal(-20m, cart.ActualAmount);

        Assert.False(cart.IncreaseLine(first));
        Assert.False(cart.DecreaseLine(first));
        Assert.False(cart.SetLineQuantity(first, 3m));
        Assert.False(cart.SetLineUnitPrice(first, 9m));
        Assert.False(cart.SetLineDiscountAmount(first, 1m));
        Assert.Same(first, Assert.Single(cart.Lines));
        Assert.Equal(2m, first.Quantity);
        Assert.True(cart.RemoveLine(first));
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void ReturnLine_decrease_at_quantity_one_keeps_line_until_explicit_remove()
    {
        var cart = new PosCartService();
        var line = cart.AddReturnLine(CreateReturnLine("receipt-order-line-1"));
        cart.AddReturnPaymentCapacities(
        [
            new OrderReturnPaymentCapacityDto(
                PaymentMethodKind.Card,
                OriginalAmount: 10m,
                RefundedAmount: 0m,
                RemainingAmount: 10m,
                Reference: "SQ:return-capacity")
        ]);

        Assert.False(cart.DecreaseLine(line));

        Assert.Same(line, Assert.Single(cart.Lines));
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(-10m, cart.ActualAmount);
        Assert.True(cart.HasReturnLine);
        Assert.Single(cart.ReturnPaymentCapacities);

        Assert.True(cart.RemoveLine(line));
        Assert.Empty(cart.Lines);
        Assert.False(cart.HasReturnLine);
        Assert.Empty(cart.ReturnPaymentCapacities);
    }

    [Fact]
    public void Snapshot_and_restore_preserve_return_line_metadata()
    {
        var cart = new PosCartService();
        var originalOrderGuid = Guid.NewGuid();
        var originalLineGuid = Guid.NewGuid();
        cart.AddReturnLine(CreateReturnLine(
            "receipt-order-line-1",
            originalOrderGuid,
            originalLineGuid));

        var snapshot = cart.CreateSnapshot();
        cart.Clear();

        cart.RestoreSnapshot(snapshot);

        var line = Assert.Single(cart.Lines);
        Assert.True(line.IsReturnLine);
        Assert.Equal("receipt-order-line-1", line.ReturnSourceKey);
        Assert.Equal(originalOrderGuid, line.OriginalOrderGuid);
        Assert.Equal(originalLineGuid, line.OriginalOrderLineGuid);
        Assert.Equal(-10m, line.ActualAmount);
    }

    [Fact]
    public void Snapshot_and_restore_preserve_percent_discount_behavior_and_line_metadata()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(
            productCode: "SKU-RESTORE-01",
            lookupCode: " look-01 ",
            itemNumber: "ITEM-01",
            price: 12m,
            priceSource: PriceSourceKind.StoreClearancePrice,
            productImage: "https://images.example/item-01.jpg"));
        Assert.True(cart.SetLineQuantity(line, 3m));
        Assert.True(cart.SetLineDiscountPercent(line, 10.5m));

        var snapshot = cart.CreateSnapshot();
        cart.Clear();

        cart.RestoreSnapshot(snapshot);

        line = Assert.Single(cart.Lines);
        Assert.Equal(3m, line.Quantity);
        Assert.Equal(12m, line.UnitPrice);
        Assert.Equal(3.78m, line.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Manual, line.DiscountSource);
        Assert.Equal("ITEM-01", line.ItemNumber);
        Assert.Equal(" look-01 ", line.LookupCode);
        Assert.Equal("LOOK-01", line.LookupCodeNormalized);
        Assert.Equal("https://images.example/item-01.jpg", line.ProductImage);
        Assert.Equal(PriceSourceKind.StoreClearancePrice, line.PriceSource);

        Assert.True(cart.SetLineQuantity(line, 4m));
        Assert.Equal(5.04m, line.DiscountAmount);
    }

    [Fact]
    public void Snapshot_and_restore_preserve_fixed_discount_amount_behavior()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-RESTORE-02", lookupCode: "look-02", price: 10m));
        Assert.True(cart.SetLineQuantity(line, 2m));
        Assert.True(cart.SetLineDiscountAmount(line, 4m));

        var snapshot = cart.CreateSnapshot();
        cart.Clear();

        cart.RestoreSnapshot(snapshot);

        line = Assert.Single(cart.Lines);
        Assert.Equal(4m, line.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Manual, line.DiscountSource);

        Assert.True(cart.SetLineQuantity(line, 3m));
        Assert.Equal(4m, line.DiscountAmount);
    }

    [Fact]
    public void Snapshot_and_restore_preserve_automatic_promotion_discount_without_current_rules()
    {
        var cart = new PosCartService();
        cart.SetAutomaticPromotionRules(
        [
            CreatePromotionRule(
                applyQuantity: 2,
                fixedPrice: 15m,
                products:
                [
                    new CatalogPromotionProductDto("SKU-RESTORE-03", 1)
                ])
        ]);
        var line = cart.AddItem(CreateItem(productCode: "SKU-RESTORE-03", lookupCode: "look-03", price: 10m));
        cart.AddItem(CreateItem(productCode: "SKU-RESTORE-03", lookupCode: "look-03", price: 10m));
        var snapshot = cart.CreateSnapshot();
        var restoredCart = new PosCartService();

        restoredCart.RestoreSnapshot(snapshot);

        line = Assert.Single(restoredCart.Lines);
        Assert.True(line.IsAutomaticPromotionDiscount);
        Assert.Equal(5m, line.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Promotion, line.DiscountSource);
        Assert.Equal(15m, restoredCart.ActualAmount);
    }

    [Fact]
    public void Snapshot_and_restore_keep_promotion_discount_source_so_reapply_replaces_old_promotion_discount()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-RESTORE-PROMO", lookupCode: "look-promo", price: 10m));
        Assert.True(cart.SetLineQuantity(line, 2m));
        line.SetPromotionDiscountAmount(4m);

        var snapshot = cart.CreateSnapshot();
        cart.Clear();

        cart.RestoreSnapshot(snapshot);

        line = Assert.Single(cart.Lines);
        Assert.Equal(4m, line.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Promotion, line.DiscountSource);

        cart.ApplyPromotionDiscounts(
        [
            new PromotionLineDiscount(line, 6m)
        ]);

        Assert.Equal(6m, line.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Promotion, line.DiscountSource);
    }

    [Fact]
    public void SetLineUnitPrice_marks_line_as_manual_price_provenance()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m, priceSource: PriceSourceKind.StoreRetailPrice));

        Assert.False(line.IsManualPrice);

        Assert.True(cart.SetLineUnitPrice(line, 4.2m));

        Assert.True(line.IsManualPrice);
        Assert.Equal(4.2m, line.UnitPrice);
        // 底层目录来源种类仍保留，仅 provenance 标记为手工改价。
        Assert.Equal(PriceSourceKind.StoreRetailPrice, line.PriceSource);
    }

    [Fact]
    public void UpdateLineFromRemote_preserves_manual_price_and_refreshes_catalog_baseline()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m, priceSource: PriceSourceKind.StoreClearancePrice));
        Assert.True(cart.SetLineUnitPrice(line, 4.2m));
        Assert.True(line.IsManualPrice);

        Assert.True(cart.UpdateLineFromRemote(
            "S001",
            "690001",
            CreateItem(
                productCode: "SKU-REMOTE",
                referenceCode: "REF-REMOTE",
                lookupCode: "REMOTE-690001",
                displayName: "Fresh Milk",
                itemNumber: "ITEM-REMOTE",
                price: 9.5m,
                priceSource: PriceSourceKind.StoreRetailPrice,
                productImage: "https://images.example/fresh-milk.jpg",
                discountRate: 0.2m)));

        Assert.True(line.IsManualPrice);
        Assert.Equal(4.2m, line.UnitPrice);
        Assert.Equal("SKU-REMOTE", line.ProductCode);
        Assert.Equal("REF-REMOTE", line.ReferenceCode);
        Assert.Equal("Fresh Milk", line.DisplayName);
        Assert.Equal("ITEM-REMOTE", line.ItemNumber);
        Assert.Equal("https://images.example/fresh-milk.jpg", line.ProductImage);
        Assert.Equal("REMOTE-690001", line.LookupCode);
        Assert.Equal("REMOTE-690001", line.LookupCodeNormalized);
        Assert.Equal(PriceSourceKind.StoreClearancePrice, line.PriceSource);
        Assert.Equal(nameof(PriceSourceKind.StoreClearancePrice), line.PriceSourceLabel);
        Assert.Equal(2000, (int?)typeof(CartLine)
            .GetProperty("CatalogDiscountBasisPoints")
            ?.GetValue(line));
        Assert.Equal(0.84m, line.DiscountAmount);

        Assert.True(cart.UpdateLineFromRemote(
            line,
            CreateItem(
                productCode: "SKU-REMOTE",
                referenceCode: "REF-REMOTE",
                lookupCode: "REMOTE-690001",
                displayName: "Fresh Milk",
                itemNumber: "ITEM-REMOTE",
                price: 9.5m,
                priceSource: PriceSourceKind.StoreRetailPrice,
                productImage: "https://images.example/fresh-milk.jpg",
                discountRate: null)));

        Assert.True(line.IsManualPrice);
        Assert.Equal(4.2m, line.UnitPrice);
        Assert.Equal(0, (int?)typeof(CartLine)
            .GetProperty("CatalogDiscountBasisPoints")
            ?.GetValue(line));
        Assert.Equal(0m, line.DiscountAmount);
    }

    [Fact]
    public void Snapshot_and_restore_preserve_manual_and_catalog_price_provenance()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem(productCode: "SKU-CATALOG", lookupCode: "catalog-01", price: 10m));
        var manualLine = cart.AddItem(CreateItem(productCode: "SKU-MANUAL", lookupCode: "manual-01", price: 10m));
        Assert.True(cart.SetLineUnitPrice(manualLine, 7.7m));

        var snapshot = cart.CreateSnapshot();
        var manualSnapshotLine = snapshot.Lines.Single(snapshotLine => snapshotLine.ProductCode == "SKU-MANUAL");
        var catalogSnapshotLine = snapshot.Lines.Single(snapshotLine => snapshotLine.ProductCode == "SKU-CATALOG");
        Assert.True(manualSnapshotLine.IsManualPrice);
        Assert.False(catalogSnapshotLine.IsManualPrice);

        var restoredCart = new PosCartService();
        restoredCart.RestoreSnapshot(snapshot);

        var restoredCatalog = restoredCart.Lines.Single(restored => restored.ProductCode == "SKU-CATALOG");
        var restoredManual = restoredCart.Lines.Single(restored => restored.ProductCode == "SKU-MANUAL");
        Assert.False(restoredCatalog.IsManualPrice);
        Assert.True(restoredManual.IsManualPrice);
        Assert.Equal(7.7m, restoredManual.UnitPrice);
        Assert.Equal(10m, restoredCatalog.UnitPrice);
    }

    [Fact]
    public void RestoreSharedSaleSnapshot_restores_manual_price_provenance()
    {
        var cart = new PosCartService();
        var snapshot = new PosCartSnapshot(
        [
            new PosCartLineSnapshot(
                "S001",
                "P-1",
                null,
                "Product 1",
                "CODE-1",
                null,
                null,
                1m,
                8.8m,
                0m,
                null,
                PriceSourceKind.StoreRetailPrice,
                "Store Retail Price",
                IsManualPrice: true)
        ]);

        cart.RestoreSharedSaleSnapshot(snapshot);

        var line = Assert.Single(cart.Lines);
        Assert.True(line.IsManualPrice);
        Assert.Equal(8.8m, line.UnitPrice);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    public void RestoreSnapshot_rejects_corrupt_catalog_discount_basis_points(int basisPoints)
    {
        var cart = new PosCartService();
        var snapshot = new PosCartSnapshot(
        [
            new PosCartLineSnapshot(
                "S001",
                "P-1",
                null,
                "Product 1",
                "CODE-1",
                null,
                null,
                1m,
                6.99m,
                0m,
                null,
                PriceSourceKind.StoreRetailPrice,
                "Store Retail Price",
                CatalogDiscountBasisPoints: basisPoints)
        ]);

        Assert.Throws<InvalidOperationException>(() => cart.RestoreSnapshot(snapshot));
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void RestoreSnapshot_rejects_catalog_discount_with_promotion_state()
    {
        var cart = new PosCartService();
        var snapshot = new PosCartSnapshot(
        [
            new PosCartLineSnapshot(
                "S001",
                "P-1",
                null,
                "Product 1",
                "CODE-1",
                null,
                null,
                1m,
                6.99m,
                1m,
                null,
                PriceSourceKind.StoreRetailPrice,
                "Store Retail Price",
                DiscountSource: PosCartLineDiscountSource.Promotion,
                CatalogDiscountBasisPoints: 2_000)
        ]);

        Assert.Throws<InvalidOperationException>(() => cart.RestoreSnapshot(snapshot));
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Automatic_promotion_budget_exceeded_clears_only_promotion_discounts_and_keeps_cart_usable()
    {
        var cart = new PosCartService();
        var promotionLine = cart.AddItem(CreateItem(
            productCode: "SKU-BUDGET-PROMO",
            lookupCode: "SKU-BUDGET-PROMO",
            price: 10m));
        var manualLine = cart.AddItem(CreateItem(
            productCode: "SKU-BUDGET-MANUAL",
            lookupCode: "SKU-BUDGET-MANUAL",
            price: 10m));
        var catalogLine = cart.AddItem(CreateItem(
            productCode: "SKU-BUDGET-CATALOG",
            lookupCode: "SKU-BUDGET-CATALOG",
            price: 10m,
            discountRate: 0.2m));
        Assert.True(cart.SetLineDiscountAmount(manualLine, 2m));
        cart.SetAutomaticPromotionRules(
        [
            CreatePromotionRule(
                applyQuantity: 1,
                fixedPrice: 5m,
                products: [new CatalogPromotionProductDto("SKU-BUDGET-PROMO", 1)])
        ]);
        Assert.True(promotionLine.IsAutomaticPromotionDiscount);

        Assert.True(cart.SetLineQuantity(
            promotionLine,
            PromotionComputationBudget.MaxWorkUnitsPerRule + 1));

        Assert.False(promotionLine.IsAutomaticPromotionDiscount);
        Assert.Equal(0m, promotionLine.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Manual, manualLine.DiscountSource);
        Assert.Equal(2m, manualLine.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Catalog, catalogLine.DiscountSource);
        Assert.Equal(2m, catalogLine.DiscountAmount);
        Assert.False(cart.IsEmpty);
        Assert.False(cart.HasNonIntegerQuantity);
    }

    [Fact]
    public void Automatic_promotion_large_quantity_with_max_applications_only_consumes_required_bundle()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(
            productCode: "SKU-BUDGET-CAPPED",
            lookupCode: "SKU-BUDGET-CAPPED",
            price: 10m));
        Assert.True(cart.SetLineQuantity(line, 1_000_000m));

        cart.SetAutomaticPromotionRules(
        [
            CreatePromotionRule(
                applyQuantity: 2,
                fixedPrice: 15m,
                products: [new CatalogPromotionProductDto("SKU-BUDGET-CAPPED", 1)],
                maxApplicationsPerOrder: 1)
        ]);

        Assert.True(line.IsAutomaticPromotionDiscount);
        Assert.Equal(5m, line.DiscountAmount);
    }

    [Fact]
    public void Recovery_publication_requires_empty_cart_and_matching_revision()
    {
        var cart = new PosCartService();
        var staleRevision = cart.Revision;
        cart.AddItem(CreateItem(productCode: "CURRENT", lookupCode: "CURRENT"));

        var nonEmpty = cart.TryPublishRecoverySnapshot(
            Guid.NewGuid(),
            cart.Revision,
            CreateRecoverySnapshot());
        Assert.False(nonEmpty.Succeeded);
        Assert.Single(cart.Lines);

        cart.Clear();
        var stale = cart.TryPublishRecoverySnapshot(
            Guid.NewGuid(),
            staleRevision,
            CreateRecoverySnapshot());
        Assert.False(stale.Succeeded);
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public void Recovery_publication_blocks_normal_mutations_until_matching_owner_completes()
    {
        var cart = new PosCartService();
        var attemptGuid = Guid.NewGuid();

        var published = cart.TryPublishRecoverySnapshot(
            attemptGuid,
            cart.Revision,
            CreateRecoverySnapshot());

        Assert.True(published.Succeeded);
        Assert.Equal(attemptGuid, cart.RecoveryOwnerAttemptGuid);
        Assert.Throws<InvalidOperationException>(() =>
            cart.AddItem(CreateItem(productCode: "NEW", lookupCode: "NEW")));
        Assert.False(cart.CompleteRecoveryPublication(Guid.NewGuid()));
        Assert.Equal(attemptGuid, cart.RecoveryOwnerAttemptGuid);

        Assert.True(cart.CompleteRecoveryPublication(attemptGuid));
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        cart.AddItem(CreateItem(productCode: "NEW", lookupCode: "NEW"));
        Assert.Equal(2, cart.Lines.Count);
    }

    [Fact]
    public void Recovery_publication_rollback_only_clears_matching_owner_snapshot()
    {
        var cart = new PosCartService();
        var attemptGuid = Guid.NewGuid();
        Assert.True(cart.TryPublishRecoverySnapshot(
            attemptGuid,
            cart.Revision,
            CreateRecoverySnapshot()).Succeeded);

        Assert.False(cart.RollbackRecoveryPublication(Guid.NewGuid()).Succeeded);
        Assert.Single(cart.Lines);
        Assert.Equal(attemptGuid, cart.RecoveryOwnerAttemptGuid);

        var rolledBack = cart.RollbackRecoveryPublication(attemptGuid);
        Assert.True(rolledBack.Succeeded);
        Assert.True(cart.IsEmpty);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
    }

    [Fact]
    public void Recovery_publication_validates_snapshot_before_exposing_any_lines()
    {
        var cart = new PosCartService();
        var validLine = CreateRecoverySnapshot().Lines[0];
        var invalidSnapshot = new PosCartSnapshot(
        [
            validLine,
            validLine with { ProductCode = "INVALID", LookupCode = "INVALID", Quantity = 0.5m }
        ]);

        Assert.Throws<InvalidOperationException>(() =>
            cart.TryPublishRecoverySnapshot(Guid.NewGuid(), cart.Revision, invalidSnapshot));
        Assert.True(cart.IsEmpty);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
    }

    [Fact]
    public void Recovery_publication_treats_non_fatal_subscriber_failure_as_warning()
    {
        var cart = new PosCartService();
        cart.CartChanged += (_, _) => throw new InvalidOperationException("subscriber failed");

        var published = cart.TryPublishRecoverySnapshot(
            Guid.NewGuid(),
            cart.Revision,
            CreateRecoverySnapshot());

        Assert.True(published.Succeeded);
        Assert.True(published.NotificationWarning);
        Assert.Single(cart.Lines);
    }

    [Fact]
    public void Recovery_publication_propagates_out_of_memory_from_subscriber()
    {
        var cart = new PosCartService();
        cart.CartChanged += (_, _) => throw new OutOfMemoryException("fatal");

        Assert.Throws<OutOfMemoryException>(() =>
            cart.TryPublishRecoverySnapshot(
                Guid.NewGuid(),
                cart.Revision,
                CreateRecoverySnapshot()));
    }

    private static PosCartSnapshot CreateRecoverySnapshot()
    {
        var stagingCart = new PosCartService();
        stagingCart.AddItem(CreateItem(productCode: "RECOVERY", lookupCode: "RECOVERY"));
        return stagingCart.CreateSnapshot();
    }

    private static SellableItemDto CreateItem(
        string storeCode = "S001",
        string productCode = "SKU-001",
        string lookupCode = "690001",
        string displayName = "Milk 1L",
        string? itemNumber = null,
        decimal price = 10m,
        PriceSourceKind priceSource = PriceSourceKind.StoreRetailPrice,
        string? productImage = null,
        decimal quantityFactor = 1m,
        decimal? discountRate = null,
        bool isSpecialProduct = false,
        string? referenceCode = null)
    {
        return new SellableItemDto(
            StoreCode: storeCode,
            ProductCode: productCode,
            ReferenceCode: referenceCode,
            DisplayName: displayName,
            LookupCode: lookupCode,
            ItemNumber: itemNumber ?? productCode,
            Barcode: lookupCode.Trim(),
            RetailPrice: price,
            PriceSource: priceSource,
            PriceSourceLabel: priceSource.ToString(),
            QuantityFactor: quantityFactor,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: productImage,
            DiscountRate: discountRate,
            IsSpecialProduct: isSpecialProduct);
    }

    private static CatalogPromotionRuleDto CreatePromotionRule(
        int applyQuantity,
        decimal fixedPrice,
        IReadOnlyList<CatalogPromotionProductDto> products,
        string promotionId = "PROMO-001",
        bool isExclusive = true,
        int priority = 0,
        int? maxApplicationsPerOrder = null)
    {
        return new CatalogPromotionRuleDto(
            promotionId,
            "Quantity discount",
            isExclusive,
            priority,
            applyQuantity,
            fixedPrice,
            maxApplicationsPerOrder,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            DateTimeOffset.UtcNow,
            products);
    }

    private static ReturnCartLineRequest CreateReturnLine(
        string sourceKey,
        Guid? originalOrderGuid = null,
        Guid? originalOrderLineGuid = null)
    {
        return new ReturnCartLineRequest(
            "S001",
            "SKU-001",
            "REF-001",
            "Milk 1L",
            "690001",
            "SKU-001",
            null,
            1m,
            10m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            sourceKey,
            originalOrderGuid,
            originalOrderLineGuid);
    }
}
