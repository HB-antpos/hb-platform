using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Promotions;

namespace Hbpos.Client.Tests;

public sealed class PromotionEvaluationServiceTests
{
    [Fact]
    public async Task EvaluateAsync_applies_fixed_price_discount_for_two_identical_items()
    {
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule("PROMO-2FOR15", asOf, ["SKU-001"], applyQuantity: 2, fixedPrice: 15m)
        ]);

        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "SKU-001-A", price: 10m));
        cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "SKU-001-A", price: 10m));

        var service = new PromotionEvaluationService(repository);

        var discounts = await service.EvaluateAsync(cart.Lines, "S001", asOf);

        var discount = Assert.Single(discounts);
        Assert.Same(line, discount.Line);
        Assert.Equal(5m, discount.DiscountAmount);
    }

    [Fact]
    public async Task EvaluateAsync_uses_unit_weight_only_for_threshold_count_not_group_total()
    {
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule(
                "PROMO-WEIGHTED",
                asOf,
                [new PromotionRuleProductDto("SKU-A", 2), new PromotionRuleProductDto("SKU-B", 1)],
                applyQuantity: 3,
                fixedPrice: 15m)
        ]);

        var cart = new PosCartService();
        var weightedLine = cart.AddItem(CreateItem(productCode: "SKU-A", lookupCode: "SKU-A-001", price: 12m));
        var regularLine = cart.AddItem(CreateItem(productCode: "SKU-B", lookupCode: "SKU-B-001", price: 6m));
        var service = new PromotionEvaluationService(repository);

        var discounts = await service.EvaluateAsync(cart.Lines, "S001", asOf);

        Assert.Equal(2, discounts.Count);
        Assert.Equal(2m, Assert.Single(discounts, item => ReferenceEquals(item.Line, weightedLine)).DiscountAmount);
        Assert.Equal(1m, Assert.Single(discounts, item => ReferenceEquals(item.Line, regularLine)).DiscountAmount);
        Assert.Equal(3m, discounts.Sum(item => item.DiscountAmount));
    }

    [Fact]
    public async Task EvaluateAsync_consumes_expanded_weight_units_one_by_one_across_multiple_bundles()
    {
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule(
                "PROMO-EXPANDED-CONSUME",
                asOf,
                [new PromotionRuleProductDto("SKU-WEIGHT-2", 2)],
                applyQuantity: 1,
                fixedPrice: 8m,
                maxApplicationsPerOrder: null)
        ]);

        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-WEIGHT-2", lookupCode: "SKU-WEIGHT-2-001", price: 10m));
        var service = new PromotionEvaluationService(repository);

        var discounts = await service.EvaluateAsync(cart.Lines, "S001", asOf);

        var discount = Assert.Single(discounts);
        Assert.Same(line, discount.Line);
        Assert.Equal(4m, discount.DiscountAmount);
    }

    [Fact]
    public async Task EvaluateAsync_non_exclusive_rules_evaluate_independently_like_web()
    {
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule("PROMO-A", asOf, ["SKU-REUSE"], priority: 20, applyQuantity: 2, fixedPrice: 15m),
            CreateRule("PROMO-B", asOf, ["SKU-REUSE"], priority: 10, applyQuantity: 2, fixedPrice: 16m)
        ]);

        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-REUSE", lookupCode: "SKU-REUSE-001", price: 10m));
        Assert.True(cart.SetLineQuantity(line, 2m));
        var service = new PromotionEvaluationService(repository);

        var discounts = await service.EvaluateAsync(cart.Lines, "S001", asOf);

        var discount = Assert.Single(discounts);
        Assert.Same(line, discount.Line);
        Assert.Equal(9m, discount.DiscountAmount);
    }

    [Fact]
    public async Task EvaluateAsync_groups_units_in_cart_order_like_web()
    {
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule("PROMO-CART-ORDER", asOf, ["SKU-ORDER"], applyQuantity: 2, fixedPrice: 10m)
        ]);

        var cart = new PosCartService();
        var lowPriceLine = cart.AddItem(CreateItem(productCode: "SKU-ORDER", lookupCode: "SKU-ORDER-LOW", price: 4m));
        var highPriceLine = cart.AddItem(CreateItem(productCode: "SKU-ORDER", lookupCode: "SKU-ORDER-HIGH", price: 10m));
        Assert.True(cart.SetLineQuantity(highPriceLine, 2m));
        var service = new PromotionEvaluationService(repository);

        var discounts = await service.EvaluateAsync(cart.Lines, "S001", asOf);

        Assert.Equal(2, discounts.Count);
        Assert.Equal(1.14m, Assert.Single(discounts, item => ReferenceEquals(item.Line, lowPriceLine)).DiscountAmount);
        Assert.Equal(2.86m, Assert.Single(discounts, item => ReferenceEquals(item.Line, highPriceLine)).DiscountAmount);
        Assert.Equal(4m, discounts.Sum(item => item.DiscountAmount));
    }

    [Fact]
    public async Task EvaluateAsync_honors_max_applications_per_order()
    {
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule("PROMO-LIMIT-1", asOf, ["SKU-001"], applyQuantity: 2, fixedPrice: 15m, maxApplicationsPerOrder: 1)
        ]);

        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "SKU-001-LIMIT", price: 10m));
        Assert.True(cart.SetLineQuantity(line, 5m));

        var service = new PromotionEvaluationService(repository);

        var discounts = await service.EvaluateAsync(cart.Lines, "S001", asOf);

        var discount = Assert.Single(discounts);
        Assert.Equal(5m, discount.DiscountAmount);
    }

    [Fact]
    public async Task EvaluateAsync_only_uses_highest_priority_exclusive_rule_when_present()
    {
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule("PROMO-NORMAL", asOf, ["SKU-001"], priority: 5, applyQuantity: 2, fixedPrice: 1m),
            CreateRule("PROMO-EXCLUSIVE-LOW", asOf, ["SKU-001"], priority: 10, isExclusive: true, applyQuantity: 2, fixedPrice: 15m),
            CreateRule("PROMO-EXCLUSIVE-HIGH", asOf, ["SKU-001"], priority: 20, isExclusive: true, applyQuantity: 2, fixedPrice: 18m)
        ]);

        var cart = new PosCartService();
        cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "SKU-001-EXCLUSIVE", price: 10m));
        cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "SKU-001-EXCLUSIVE", price: 10m));
        var service = new PromotionEvaluationService(repository);

        var discounts = await service.EvaluateAsync(cart.Lines, "S001", asOf);

        Assert.Equal(2m, Assert.Single(discounts).DiscountAmount);
    }

    [Fact]
    public async Task EvaluateAsync_excludes_manual_discount_lines_and_reads_rules_from_local_repository()
    {
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule("PROMO-SKU-001", asOf, ["SKU-001"], applyQuantity: 2, fixedPrice: 15m),
            CreateRule("PROMO-SKU-002", asOf, ["SKU-002"], applyQuantity: 2, fixedPrice: 15m)
        ]);

        var cart = new PosCartService();
        var manualLine = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "SKU-001-MANUAL", price: 10m));
        Assert.True(cart.SetLineQuantity(manualLine, 2m));
        Assert.True(cart.SetLineDiscountAmount(manualLine, 1m));

        var eligibleLine = cart.AddItem(CreateItem(productCode: "SKU-002", lookupCode: "SKU-002-ELIGIBLE", price: 10m));
        Assert.True(cart.SetLineQuantity(eligibleLine, 2m));

        var service = new PromotionEvaluationService(repository);

        var discounts = await service.EvaluateAsync(cart.Lines, "S001", asOf);

        var discount = Assert.Single(discounts);
        Assert.Same(eligibleLine, discount.Line);
        Assert.Equal(5m, discount.DiscountAmount);
        Assert.Equal(CartLineDiscountSource.Manual, manualLine.DiscountSource);
    }

    [Fact]
    public async Task EvaluateAsync_excludes_catalog_baseline_even_when_its_cent_discount_rounds_to_zero()
    {
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule("PROMO-CATALOG-BASELINE", asOf, ["SKU-CATALOG", "SKU-NORMAL"], applyQuantity: 2, fixedPrice: 0.01m)
        ]);

        var cart = new PosCartService();
        var catalogLine = cart.AddItem(CreateItem(
            productCode: "SKU-CATALOG",
            lookupCode: "CATALOG-001",
            price: 0.01m,
            discountRate: 0.0001m));
        cart.AddItem(CreateItem(productCode: "SKU-NORMAL", lookupCode: "NORMAL-001", price: 0.01m));

        // 1 个基点的 0.01 元目录折扣会舍入为 0，不能因此重新成为固定总价候选。
        Assert.Equal(1, catalogLine.CatalogDiscountBasisPoints);
        Assert.Equal(0m, catalogLine.DiscountAmount);

        var discounts = await new PromotionEvaluationService(repository).EvaluateAsync(cart.Lines, "S001", asOf);

        Assert.Empty(discounts);
    }

    [Fact]
    public async Task EvaluateAsync_rejects_selected_rule_over_budget_before_expansion()
    {
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule("PROMO-OVER-BUDGET", asOf, ["SKU-HUGE"], applyQuantity: 1, fixedPrice: 9m)
        ]);
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-HUGE", lookupCode: "SKU-HUGE", price: 10m));
        Assert.True(cart.SetLineQuantity(line, PromotionComputationBudget.MaxWorkUnitsPerRule + 1));

        var exception = await Assert.ThrowsAsync<PromotionComputationBudgetExceededException>(() =>
            new PromotionEvaluationService(repository).EvaluateAsync(cart.Lines, "S001", asOf));

        Assert.Equal("PROMO-OVER-BUDGET", exception.RuleId);
        Assert.Equal("rule", exception.Scope);
    }

    [Fact]
    public async Task EvaluateAsync_honors_max_applications_without_expanding_the_full_cart()
    {
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule(
                "PROMO-LARGE-CAPPED",
                asOf,
                ["SKU-LARGE-CAPPED"],
                applyQuantity: 2,
                fixedPrice: 15m,
                maxApplicationsPerOrder: 1)
        ]);
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(
            productCode: "SKU-LARGE-CAPPED",
            lookupCode: "SKU-LARGE-CAPPED",
            price: 10m));
        Assert.True(cart.SetLineQuantity(line, 1_000_000m));

        var discounts = await new PromotionEvaluationService(repository)
            .EvaluateAsync(cart.Lines, "S001", asOf);

        Assert.Equal(5m, Assert.Single(discounts).DiscountAmount);
    }

    [Fact]
    public async Task EvaluateAsync_rejects_aggregate_order_budget_before_partial_results()
    {
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore(
            "S001",
            Enumerable.Range(1, 6)
                .Select(index => CreateRule(
                    $"PROMO-ORDER-{index}",
                    asOf,
                    ["SKU-ORDER-BUDGET"],
                    applyQuantity: 1,
                    fixedPrice: 9m))
                .ToArray());
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(
            productCode: "SKU-ORDER-BUDGET",
            lookupCode: "SKU-ORDER-BUDGET",
            price: 10m));
        Assert.True(cart.SetLineQuantity(line, PromotionComputationBudget.MaxWorkUnitsPerRule));

        var exception = await Assert.ThrowsAsync<PromotionComputationBudgetExceededException>(() =>
            new PromotionEvaluationService(repository).EvaluateAsync(cart.Lines, "S001", asOf));

        Assert.Equal("order", exception.Scope);
        Assert.Equal(PromotionComputationBudget.MaxWorkUnitsPerOrder + PromotionComputationBudget.MaxWorkUnitsPerRule, exception.CalculatedWorkUnits);
    }

    [Fact]
    public async Task EvaluateAsync_ignores_a_negative_fixed_price_rule_entirely()
    {
        // 固定价必须 >= 0：负价规则整条忽略，不会把整组折成 0 元甚至倒贴（与旧引擎、目录源服务端、iPad 一致）。
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule("PROMO-NEGATIVE", asOf, ["SKU-001"], applyQuantity: 2, fixedPrice: -1m)
        ]);
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "SKU-001-A", price: 10m));
        Assert.True(cart.SetLineQuantity(line, 2m));

        var discounts = await new PromotionEvaluationService(repository).EvaluateAsync(cart.Lines, "S001", asOf);

        Assert.Empty(discounts);
    }

    [Fact]
    public async Task EvaluateAsync_negative_fixed_price_exclusive_rule_does_not_suppress_other_rules()
    {
        // 被忽略的负价规则也不参与互斥选择：否则它会作为最高优先级的互斥规则把真正有效的规则整条压住。
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule("PROMO-NEGATIVE", asOf, ["SKU-001"], priority: 99, isExclusive: true, applyQuantity: 2, fixedPrice: -1m),
            CreateRule("PROMO-VALID", asOf, ["SKU-001"], priority: 1, applyQuantity: 2, fixedPrice: 15m)
        ]);
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "SKU-001-A", price: 10m));
        Assert.True(cart.SetLineQuantity(line, 2m));

        var discounts = await new PromotionEvaluationService(repository).EvaluateAsync(cart.Lines, "S001", asOf);

        Assert.Equal(5m, Assert.Single(discounts).DiscountAmount);
    }

    [Fact]
    public async Task EvaluateAsync_zero_fixed_price_is_still_a_valid_free_bundle()
    {
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule("PROMO-FREE", asOf, ["SKU-001"], applyQuantity: 2, fixedPrice: 0m)
        ]);
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "SKU-001-A", price: 10m));
        Assert.True(cart.SetLineQuantity(line, 2m));

        var discounts = await new PromotionEvaluationService(repository).EvaluateAsync(cart.Lines, "S001", asOf);

        Assert.Equal(20m, Assert.Single(discounts).DiscountAmount);
    }

    [Fact]
    public async Task EvaluateAsync_sub_cent_unit_price_bundle_actual_equals_the_fixed_price()
    {
        // 单价 3.335×3 凑 3 件 9.00：行原价 10.01（整行一次舍入），折 1.01，实收恰为 9.00；
        // 若先把每个单位舍入到 3.34 再求和（10.02），会多折 1 分、实收 8.99。
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule("PROMO-SUBCENT", asOf, ["SKU-001"], applyQuantity: 3, fixedPrice: 9m)
        ]);
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "SKU-001-A", price: 3.335m));
        Assert.True(cart.SetLineQuantity(line, 3m));

        var discounts = await new PromotionEvaluationService(repository).EvaluateAsync(cart.Lines, "S001", asOf);
        cart.ApplyPromotionDiscounts(discounts);

        Assert.Equal(1.01m, Assert.Single(discounts).DiscountAmount);
        Assert.Equal(9.00m, line.ActualAmount);
    }

    [Fact]
    public async Task EvaluateAsync_sub_cent_unit_price_across_lines_rounds_each_line_once()
    {
        // 两行各 1 件 3.335：行原价各 3.34（与购物车行原价同口径），组原价 6.68，凑 2 件 5.00 折 1.68，均分 0.84/0.84，整组实收 5.00。
        var repository = new FakeLocalPromotionRepository();
        var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
        repository.SeedStore("S001",
        [
            CreateRule("PROMO-SUBCENT-PAIR", asOf, ["SKU-A", "SKU-B"], applyQuantity: 2, fixedPrice: 5m)
        ]);
        var cart = new PosCartService();
        var first = cart.AddItem(CreateItem(productCode: "SKU-A", lookupCode: "SKU-A-001", price: 3.335m));
        var second = cart.AddItem(CreateItem(productCode: "SKU-B", lookupCode: "SKU-B-001", price: 3.335m));

        var discounts = await new PromotionEvaluationService(repository).EvaluateAsync(cart.Lines, "S001", asOf);
        cart.ApplyPromotionDiscounts(discounts);

        Assert.Equal(0.84m, first.DiscountAmount);
        Assert.Equal(0.84m, second.DiscountAmount);
        Assert.Equal(5.00m, first.ActualAmount + second.ActualAmount);
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
        decimal? discountRate = null)
    {
        return new SellableItemDto(
            StoreCode: storeCode,
            ProductCode: productCode,
            ReferenceCode: null,
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
            DiscountRate: discountRate);
    }

    private static PromotionRuleDto CreateRule(
        string id,
        DateTimeOffset asOf,
        IEnumerable<string> productCodes,
        int priority = 10,
        bool isExclusive = false,
        int applyQuantity = 2,
        decimal fixedPrice = 9.99m,
        int? maxApplicationsPerOrder = null)
    {
        return CreateRule(
            id,
            asOf,
            productCodes.Select(productCode => new PromotionRuleProductDto(productCode, 1)).ToArray(),
            priority,
            isExclusive,
            applyQuantity,
            fixedPrice,
            maxApplicationsPerOrder);
    }

    private static PromotionRuleDto CreateRule(
        string id,
        DateTimeOffset asOf,
        IReadOnlyList<PromotionRuleProductDto> products,
        int priority = 10,
        bool isExclusive = false,
        int applyQuantity = 2,
        decimal fixedPrice = 9.99m,
        int? maxApplicationsPerOrder = null)
    {
        return new PromotionRuleDto(
            id,
            $"Rule {id}",
            asOf.AddDays(-1).UtcDateTime,
            asOf.AddDays(1).UtcDateTime,
            isExclusive,
            priority,
            applyQuantity,
            fixedPrice,
            maxApplicationsPerOrder,
            products);
    }

    private sealed class FakeLocalPromotionRepository : ILocalPromotionRepository
    {
        private readonly Dictionary<string, IReadOnlyList<PromotionRuleDto>> _rulesByStore = new(StringComparer.OrdinalIgnoreCase);

        public Task ReplaceStoreRulesAsync(
            string storeCode,
            PromotionRulesResponse response,
            CancellationToken cancellationToken = default)
        {
            _rulesByStore[storeCode] = response.Rules;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PromotionRuleDto>> GetActiveRulesAsync(
            string storeCode,
            DateTimeOffset asOf,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                _rulesByStore.TryGetValue(storeCode, out var rules)
                    ? rules
                    : (IReadOnlyList<PromotionRuleDto>)[]);
        }

        public void SeedStore(string storeCode, IReadOnlyList<PromotionRuleDto> rules)
        {
            _rulesByStore[storeCode] = rules;
        }
    }
}
