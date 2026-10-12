using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

/// <summary>
/// SuspendedOrder 普通 sale 快照 -> canonical mapper：
/// 无损映射、return/open-item 拒绝、手工折扣无损、自动促销仅在全量冻结规则精确对应时发布。
/// </summary>
public sealed class SharedHeldOrderMapperTests
{
    private static readonly ISharedHeldOrderMapper Mapper = new SharedHeldOrderMapper();

    private static readonly DateTimeOffset HeldAt = new(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);

    private static SuspendedOrderLine SaleLine(
        string productCode,
        decimal quantity,
        decimal unitPrice,
        decimal discountAmount,
        decimal? discountPercent,
        PosCartLineDiscountSource discountSource = PosCartLineDiscountSource.None,
        CartLineKind kind = CartLineKind.Sale,
        PriceSourceKind priceSource = PriceSourceKind.ProductBase,
        string? referenceCode = null,
        bool isManualPrice = false,
        int catalogDiscountBasisPoints = 0)
    {
        return new SuspendedOrderLine(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "S001",
            productCode,
            referenceCode,
            $"Product {productCode}",
            $"CODE-{productCode}",
            null,
            null,
            quantity,
            unitPrice,
            discountAmount,
            discountPercent,
            quantity * unitPrice - discountAmount,
            priceSource,
            "ProductBase",
            discountSource)
        {
            Kind = kind,
            IsManualPrice = isManualPrice,
            CatalogDiscountBasisPoints = catalogDiscountBasisPoints
        };
    }

    private static SuspendedOrder Order(params SuspendedOrderLine[] lines)
    {
        return new SuspendedOrder(
            Guid.NewGuid(),
            "S001",
            "POS-01",
            "cashier-1",
            "Cashier One",
            HeldAt,
            0m,
            0m,
            0m,
            SuspendedOrderStatus.Pending,
            lines);
    }

    [Fact]
    public void Map_plain_sale_snapshot_is_lossless()
    {
        var order = Order(
            SaleLine("P1", 1, 11.00m, 0m, null, referenceCode: "REF-1"),
            SaleLine("P2", 2, 10.00m, 1.25m, null, PosCartLineDiscountSource.Manual));

        var result = Mapper.Map(order, null, revision: 5);

        Assert.False(result.IsBlocked);
        Assert.NotNull(result.Payload);
        Assert.Equal(1, result.Payload.Version);
        Assert.Equal(5, result.Payload.PricingState.Revision);
        Assert.Equal("sale", result.Payload.PricingState.Mode);
        Assert.Equal("2026-07-28T00:00:00.000Z", result.Payload.PricingState.AsOfIso);
        Assert.Empty(result.Payload.PricingState.Promotions);
        Assert.Equal(2, result.Payload.PricingState.Lines.Count);

        var first = result.Payload.PricingState.Lines[0];
        Assert.Equal(1100L, first.UnitPriceCents);
        Assert.Equal(1m, first.Quantity);
        Assert.Equal("catalog", first.BasePriceSource);
        Assert.Equal("sale", first.Kind);
        Assert.NotNull(first.SyncProvenance);
        Assert.Equal("REF-1", first.SyncProvenance.ReferenceCode);
        Assert.Equal(0, first.SyncProvenance.PriceSource);
        Assert.Equal("none", first.DiscountState.Mode);

        // 手工金额折扣按整数 cents 无损。
        var second = result.Payload.PricingState.Lines[1];
        Assert.Equal(1000L, second.UnitPriceCents);
        Assert.Equal(2m, second.Quantity);
        Assert.Equal("manual-amount", second.DiscountState.Mode);
        Assert.Equal(125L, second.DiscountState.Cents);
        Assert.Null(second.DiscountState.BasisPoints);
    }

    [Fact]
    public void Map_accepts_positive_decimal_weight_quantity()
    {
        // 普通 sale 支持称重小数数量（如 1.5kg），只要求正数，不要求整数。
        var order = Order(
            SaleLine("P1", 1.5m, 12.00m, 0m, null));

        var result = Mapper.Map(order, null, revision: 1);

        Assert.False(result.IsBlocked);
        var line = Assert.Single(result.Payload!.PricingState.Lines);
        Assert.Equal(1.5m, line.Quantity);
        Assert.Equal(1200L, line.UnitPriceCents);
    }

    [Fact]
    public void Map_catalog_discount_uses_v2_and_keeps_price_plus_baseline_separate()
    {
        var order = Order(SaleLine(
            "JM-006",
            1m,
            6.99m,
            1.40m,
            20m,
            PosCartLineDiscountSource.Catalog,
            catalogDiscountBasisPoints: 2000));

        var result = Mapper.Map(order, null, revision: 3);

        Assert.False(result.IsBlocked);
        Assert.Equal(SharedHeldOrderCanonicalPayload.VersionV2, result.Payload!.Version);
        var line = Assert.Single(result.Payload.PricingState.Lines);
        Assert.Equal(699L, line.UnitPriceCents);
        Assert.Equal(2000, line.CatalogDiscountBasisPoints);
        Assert.Equal(SharedHeldOrderCanonicalConstants.DiscountNone, line.DiscountState.Mode);
        var wire = Assert.IsType<Hbpos.Contracts.HeldOrders.SharedSaleCartV2>(
            SharedHeldOrderContractMapper.ToContract(
                result.Payload,
                Hbpos.Contracts.HeldOrders.SharedSaleCartV2Constants.PayloadVersion));
        Assert.Equal(2000, Assert.Single(wire.PricingState.Lines).CatalogDiscountBasisPoints);
        Assert.Throws<Hbpos.Contracts.HeldOrders.SharedSaleCartValidationException>(
            () => SharedHeldOrderContractMapper.ToContract(
                result.Payload,
                Hbpos.Contracts.HeldOrders.SharedSaleCartV1Constants.PayloadVersion));
    }

    [Fact]
    public void Map_catalog_discount_line_is_excluded_from_frozen_fixed_price_promotion_replay()
    {
        var order = Order(SaleLine(
            "JM-006",
            1m,
            6.99m,
            1.40m,
            20m,
            PosCartLineDiscountSource.Catalog,
            catalogDiscountBasisPoints: 2000));
        var frozenRules = new[]
        {
            new CatalogPromotionRuleDto(
                "PROMO-CATALOG",
                "Catalog item fixed price",
                false,
                1,
                1,
                5.00m,
                null,
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 31, 23, 59, 59, TimeSpan.Zero),
                null,
                [new CatalogPromotionProductDto("JM-006", 1)])
        };

        var result = Mapper.Map(order, frozenRules, revision: 3);

        Assert.False(result.IsBlocked);
        Assert.Equal(SharedHeldOrderCanonicalPayload.VersionV2, result.Payload!.Version);
        Assert.Equal(
            SharedHeldOrderCanonicalConstants.DiscountNone,
            Assert.Single(result.Payload.PricingState.Lines).DiscountState.Mode);
    }

    [Fact]
    public void Map_zero_or_negative_quantity_is_rejected()
    {
        // 契约 validator 要求 quantity > 0；保留正数下限，仅去掉整数限制。
        var zeroOrder = Order(SaleLine("P1", 0m, 12.00m, 0m, null));
        var negativeOrder = Order(SaleLine("P1", -1m, 12.00m, 0m, null));
        var tooLargeOrder = Order(SaleLine(
            "P1",
            SharedHeldOrderCanonicalConstants.MaxQuantity + 0.01m,
            12.00m,
            0m,
            null));

        Assert.Throws<ArgumentException>(() => Mapper.Map(zeroOrder, null, revision: 1));
        Assert.Throws<ArgumentException>(() => Mapper.Map(negativeOrder, null, revision: 1));
        Assert.Throws<ArgumentException>(() => Mapper.Map(tooLargeOrder, null, revision: 1));
    }

    [Fact]
    public void Map_never_leaks_internal_money_or_discount_kind_into_wire()
    {
        var order = Order(
            SaleLine("P1", 1, 11.00m, 1.00m, null, PosCartLineDiscountSource.Promotion));
        var frozenRules = new[]
        {
            new CatalogPromotionRuleDto(
                "PROMO-X",
                "Single save 1",
                true,
                100,
                1,
                10.00m,
                null,
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 31, 23, 59, 59, TimeSpan.Zero),
                null,
                [new CatalogPromotionProductDto("P1", 1)])
        };

        var result = Mapper.Map(order, frozenRules, revision: 1);
        var json = new SharedHeldOrderCanonicalJsonSerializer().Serialize(result.Payload!);

        Assert.Contains("\"fixedPriceCents\":1000", json);
        Assert.Contains("\"mode\":\"promotion\"", json);
        Assert.DoesNotContain("\"fixedPrice\":{", json);
        Assert.DoesNotContain("\"currency\"", json);
        Assert.DoesNotContain("\"kind\":\"promotion\"", json);
        Assert.DoesNotContain("\"originalOrderGuid\":\"", json);
        Assert.DoesNotContain("\"returnSourceKey\":\"", json);
    }

    [Fact]
    public void Map_manual_zero_percent_falls_back_to_lossless_amount()
    {
        // 旧数据：带折扣金额但 percent 为 0，不能发布非法 manual-percent（basisPoints=0），
        // 应回退为 manual-amount 整数 cents，保持金额无损。
        var order = Order(
            SaleLine("P1", 1, 11.00m, 1.00m, 0m, PosCartLineDiscountSource.Manual));

        var result = Mapper.Map(order, null, revision: 1);

        Assert.False(result.IsBlocked);
        var line = result.Payload!.PricingState.Lines[0];
        Assert.Equal("manual-amount", line.DiscountState.Mode);
        Assert.Equal(100L, line.DiscountState.Cents);
        Assert.Null(line.DiscountState.BasisPoints);
    }

    [Fact]
    public void Map_manual_percent_discount_is_lossless()
    {
        var order = Order(
            SaleLine("P1", 2, 10.00m, 2.00m, 10m, PosCartLineDiscountSource.Manual));

        var result = Mapper.Map(order, null, revision: 1);

        Assert.False(result.IsBlocked);
        var line = result.Payload!.PricingState.Lines[0];
        Assert.Equal("manual-percent", line.DiscountState.Mode);
        Assert.Equal(1000, line.DiscountState.BasisPoints);
        Assert.Null(line.DiscountState.Cents);
    }

    [Fact]
    public void Map_manual_percent_not_exactly_representable_falls_back_to_frozen_amount_cents()
    {
        // 10.555% 无法用整数 basisPoints 精确表示（10.555 * 100 = 1055.5），
        // 必须发布冻结的手工金额 cents（211），不能舍入为 1056 bps 静默改变恢复金额。
        var order = Order(
            SaleLine("P1", 2, 10.00m, 2.11m, 10.555m, PosCartLineDiscountSource.Manual));

        var result = Mapper.Map(order, null, revision: 1);

        Assert.False(result.IsBlocked);
        var line = result.Payload!.PricingState.Lines[0];
        Assert.Equal("manual-amount", line.DiscountState.Mode);
        Assert.Equal(211L, line.DiscountState.Cents);
        Assert.Null(line.DiscountState.BasisPoints);
    }

    [Fact]
    public void Map_manual_percent_exactly_representable_stays_percent()
    {
        // 10.55% * 100 = 1055 是整数，可无损用 basisPoints 冻结，不应误回退金额。
        var order = Order(
            SaleLine("P1", 2, 10.00m, 2.11m, 10.55m, PosCartLineDiscountSource.Manual));

        var result = Mapper.Map(order, null, revision: 1);

        Assert.False(result.IsBlocked);
        var line = result.Payload!.PricingState.Lines[0];
        Assert.Equal("manual-percent", line.DiscountState.Mode);
        Assert.Equal(1055, line.DiscountState.BasisPoints);
        Assert.Null(line.DiscountState.Cents);
    }

    [Fact]
    public void Map_manual_zero_amount_overrides_catalog_through_v2_wire_round_trip()
    {
        var order = Order(SaleLine(
            "JM-006",
            1m,
            6.99m,
            0m,
            null,
            PosCartLineDiscountSource.Manual,
            catalogDiscountBasisPoints: 2000));

        var mapped = Mapper.Map(order, null, revision: 2);

        Assert.False(mapped.IsBlocked);
        Assert.Equal(SharedHeldOrderCanonicalPayload.VersionV2, mapped.Payload!.Version);
        var mappedLine = Assert.Single(mapped.Payload.PricingState.Lines);
        Assert.Equal(SharedHeldOrderCanonicalConstants.DiscountManualAmount, mappedLine.DiscountState.Mode);
        Assert.Equal(0L, mappedLine.DiscountState.Cents);
        Assert.Equal(2000, mappedLine.CatalogDiscountBasisPoints);

        var wire = Assert.IsType<Hbpos.Contracts.HeldOrders.SharedSaleCartV2>(
            SharedHeldOrderContractMapper.ToContract(
                mapped.Payload,
                Hbpos.Contracts.HeldOrders.SharedSaleCartV2Constants.PayloadVersion));
        var restoredCanonical = SharedHeldOrderContractMapper.ToCanonical(wire);
        var restoredSnapshot = new SharedHeldOrderReverseMapper().Map(restoredCanonical, "S001");
        var restoredCart = new PosCartService();
        restoredCart.RestoreSharedSaleSnapshot(restoredSnapshot);
        var restoredLine = Assert.Single(restoredCart.Lines);
        Assert.Equal(CartLineDiscountSource.Manual, restoredLine.DiscountSource);
        Assert.Equal(0m, restoredLine.DiscountAmount);
        Assert.Equal(2000, restoredLine.CatalogDiscountBasisPoints);
        Assert.Equal(6.99m, restoredLine.ActualAmount);
    }

    [Fact]
    public void Map_manual_percent_rounding_to_zero_survives_v1_wire_round_trip()
    {
        var order = Order(SaleLine(
            "P-CENT",
            1m,
            0.01m,
            0m,
            1m,
            PosCartLineDiscountSource.Manual));

        var mapped = Mapper.Map(order, null, revision: 2);

        Assert.False(mapped.IsBlocked);
        Assert.Equal(SharedHeldOrderCanonicalPayload.VersionV1, mapped.Payload!.Version);
        var mappedLine = Assert.Single(mapped.Payload.PricingState.Lines);
        Assert.Equal(SharedHeldOrderCanonicalConstants.DiscountManualPercent, mappedLine.DiscountState.Mode);
        Assert.Equal(100, mappedLine.DiscountState.BasisPoints);

        var wire = Assert.IsType<Hbpos.Contracts.HeldOrders.SharedSaleCartV1>(
            SharedHeldOrderContractMapper.ToContract(
                mapped.Payload,
                Hbpos.Contracts.HeldOrders.SharedSaleCartV1Constants.PayloadVersion));
        var restoredCanonical = SharedHeldOrderContractMapper.ToCanonical(wire);
        var restoredSnapshot = new SharedHeldOrderReverseMapper().Map(restoredCanonical, "S001");
        var restoredCart = new PosCartService();
        restoredCart.RestoreSharedSaleSnapshot(restoredSnapshot);
        var restoredLine = Assert.Single(restoredCart.Lines);
        Assert.Equal(CartLineDiscountSource.Manual, restoredLine.DiscountSource);
        Assert.Equal(1m, restoredLine.DiscountPercent);
        Assert.Equal(0m, restoredLine.DiscountAmount);
    }

    [Fact]
    public void Map_return_line_is_rejected()
    {
        var order = Order(
            SaleLine("P1", 1, 11.00m, 0m, null),
            SaleLine("P2", 1, 9.00m, 0m, null, kind: CartLineKind.Return));

        var result = Mapper.Map(order, null, revision: 1);

        Assert.True(result.IsBlocked);
        Assert.Null(result.Payload);
        Assert.Equal(SharedHeldOrderMappingReasons.ReturnLine, result.Block!.Reason);
    }

    [Fact]
    public void Map_open_item_line_is_rejected()
    {
        var order = Order(
            SaleLine("P1", 1, 11.00m, 0m, null, kind: CartLineKind.OpenItem));

        var result = Mapper.Map(order, null, revision: 1);

        Assert.True(result.IsBlocked);
        Assert.Equal(SharedHeldOrderMappingReasons.OpenItemLine, result.Block!.Reason);
    }

    [Fact]
    public void Map_promotion_discount_without_frozen_rules_is_blocked()
    {
        var order = Order(
            SaleLine("P1", 1, 11.00m, 1.00m, null, PosCartLineDiscountSource.Promotion));

        var result = Mapper.Map(order, null, revision: 1);

        Assert.True(result.IsBlocked);
        Assert.Equal(SharedHeldOrderMappingReasons.PromotionRulesMissing, result.Block!.Reason);
    }

    [Fact]
    public void Map_promotion_with_exact_frozen_rules_publishes_promotion_ids()
    {
        var order = Order(
            SaleLine("P1", 1, 11.00m, 1.00m, null, PosCartLineDiscountSource.Promotion),
            SaleLine("P1", 1, 11.00m, 1.00m, null, PosCartLineDiscountSource.Promotion));
        var frozenRules = new[]
        {
            new CatalogPromotionRuleDto(
                "PROMO-X",
                "Buy 2 save 10",
                true,
                100,
                2,
                20.00m,
                null,
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 31, 23, 59, 59, TimeSpan.Zero),
                null,
                [new CatalogPromotionProductDto("P1", 1)])
        };

        var result = Mapper.Map(order, frozenRules, revision: 1);

        Assert.False(result.IsBlocked);
        var promotion = Assert.Single(result.Payload!.PricingState.Promotions);
        Assert.Equal("PROMO-X", promotion.Id);
        Assert.Equal(2000L, promotion.FixedPriceCents);
        Assert.Equal(1m, promotion.Products[0].UnitWeight);
        foreach (var line in result.Payload.PricingState.Lines)
        {
            Assert.Equal("promotion", line.DiscountState.Mode);
            Assert.Equal(100L, line.DiscountState.Cents);
            Assert.Equal(["PROMO-X"], line.DiscountState.PromotionIds);
        }
    }

    [Fact]
    public void Map_promotion_with_frozen_rules_mismatch_is_blocked()
    {
        // 冻结规则应产生每行 $1.00 促销折扣，但挂单只存了 $0.50，无法精确对应。
        var order = Order(
            SaleLine("P1", 1, 11.00m, 0.50m, null, PosCartLineDiscountSource.Promotion),
            SaleLine("P1", 1, 11.00m, 0.50m, null, PosCartLineDiscountSource.Promotion));
        var frozenRules = new[]
        {
            new CatalogPromotionRuleDto(
                "PROMO-X",
                "Buy 2 save 10",
                true,
                100,
                2,
                20.00m,
                null,
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 31, 23, 59, 59, TimeSpan.Zero),
                null,
                [new CatalogPromotionProductDto("P1", 1)])
        };

        var result = Mapper.Map(order, frozenRules, revision: 1);

        Assert.True(result.IsBlocked);
        Assert.Equal(SharedHeldOrderMappingReasons.PromotionRulesMismatch, result.Block!.Reason);
    }

    [Fact]
    public void Map_frozen_rules_producing_discount_on_plain_line_is_blocked()
    {
        // 冻结规则会在普通行上产生促销折扣，但挂单未记录该折扣，同样不能精确对应。
        var order = Order(
            SaleLine("P1", 1, 11.00m, 0m, null),
            SaleLine("P1", 1, 11.00m, 0m, null));
        var frozenRules = new[]
        {
            new CatalogPromotionRuleDto(
                "PROMO-X",
                "Buy 2 save 10",
                true,
                100,
                2,
                20.00m,
                null,
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 31, 23, 59, 59, TimeSpan.Zero),
                null,
                [new CatalogPromotionProductDto("P1", 1)])
        };

        var result = Mapper.Map(order, frozenRules, revision: 1);

        Assert.True(result.IsBlocked);
        Assert.Equal(SharedHeldOrderMappingReasons.PromotionRulesMismatch, result.Block!.Reason);
    }

    [Fact]
    public void Map_manual_price_line_uses_canonical_manual_base_price_source()
    {
        var order = Order(
            SaleLine("P1", 1, 7.70m, 0m, null, priceSource: PriceSourceKind.StoreRetailPrice, isManualPrice: true),
            SaleLine("P2", 1, 9.90m, 0m, null, priceSource: PriceSourceKind.StoreClearancePrice));

        var result = Mapper.Map(order, null, revision: 1);

        Assert.False(result.IsBlocked);
        var lines = result.Payload!.PricingState.Lines;
        Assert.Equal("manual", lines[0].BasePriceSource);
        Assert.Equal("catalog", lines[1].BasePriceSource);
        Assert.Equal(770L, lines[0].UnitPriceCents);
        Assert.Equal(990L, lines[1].UnitPriceCents);
        // 底层目录来源种类仍作为 sync provenance 携带，便于目标端展示。
        Assert.Equal((int)PriceSourceKind.StoreRetailPrice, lines[0].SyncProvenance!.PriceSource);
        Assert.Equal((int)PriceSourceKind.StoreClearancePrice, lines[1].SyncProvenance!.PriceSource);
    }

    [Fact]
    public void Map_outputs_only_frozen_rules_that_contributed_to_promotion_lines()
    {
        var order = Order(
            SaleLine("P1", 2, 11.00m, 2.00m, null, PosCartLineDiscountSource.Promotion));
        var frozenRules = new[]
        {
            new CatalogPromotionRuleDto(
                "PROMO-ACTIVE",
                "Buy 2 save 2",
                true,
                100,
                2,
                20.00m,
                null,
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 31, 23, 59, 59, TimeSpan.Zero),
                null,
                [new CatalogPromotionProductDto("P1", 1)]),
            new CatalogPromotionRuleDto(
                "PROMO-UNRELATED",
                "Unrelated product discount",
                false,
                10,
                1,
                1.00m,
                null,
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 31, 23, 59, 59, TimeSpan.Zero),
                null,
                [new CatalogPromotionProductDto("P2", 1)])
        };

        var result = Mapper.Map(order, frozenRules, revision: 1);

        Assert.False(result.IsBlocked);
        var promotion = Assert.Single(result.Payload!.PricingState.Promotions);
        Assert.Equal("PROMO-ACTIVE", promotion.Id);
        Assert.Equal(["PROMO-ACTIVE"], result.Payload.PricingState.Lines[0].DiscountState.PromotionIds);
    }

    [Fact]
    public void Map_blocks_duplicate_frozen_rule_definition_ids()
    {
        var order = Order(
            SaleLine("P1", 2, 11.00m, 2.00m, null, PosCartLineDiscountSource.Promotion));
        var frozenRules = new[]
        {
            new CatalogPromotionRuleDto(
                "PROMO-DUP",
                "First",
                true,
                100,
                2,
                20.00m,
                null,
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 31, 23, 59, 59, TimeSpan.Zero),
                null,
                [new CatalogPromotionProductDto("P1", 1)]),
            new CatalogPromotionRuleDto(
                "PROMO-DUP",
                "Duplicate id",
                true,
                100,
                2,
                20.00m,
                null,
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 31, 23, 59, 59, TimeSpan.Zero),
                null,
                [new CatalogPromotionProductDto("P1", 1)])
        };

        var result = Mapper.Map(order, frozenRules, revision: 1);

        Assert.True(result.IsBlocked);
        Assert.Equal(SharedHeldOrderMappingReasons.PromotionRulesMismatch, result.Block!.Reason);
        Assert.Contains("PROMO-DUP", result.Block.Detail);
    }

    [Fact]
    public void Map_promotion_budget_exceeded_returns_stable_block_and_no_shared_payload()
    {
        var order = Order(SaleLine(
            "P-BUDGET",
            SharedHeldOrderCanonicalConstants.MaxQuantity,
            10m,
            1m,
            null,
            PosCartLineDiscountSource.Promotion));
        var rules = new[]
        {
            new CatalogPromotionRuleDto(
                "PROMO-BUDGET",
                "Budget guard",
                true,
                100,
                1,
                9m,
                null,
                HeldAt.AddDays(-1),
                HeldAt.AddDays(1),
                null,
                [new CatalogPromotionProductDto("P-BUDGET", 1)])
        };

        var result = Mapper.Map(order, rules, revision: 1);

        Assert.True(result.IsBlocked);
        Assert.Null(result.Payload);
        Assert.Equal(SharedHeldOrderMappingReasons.PromotionBudgetExceeded, result.Block!.Reason);
        Assert.Single(order.Lines);
    }

    [Fact]
    public void Map_large_quantity_with_max_applications_replays_only_required_bundle()
    {
        var order = Order(SaleLine(
            "P-CAPPED",
            SharedHeldOrderCanonicalConstants.MaxQuantity,
            10m,
            5m,
            null,
            PosCartLineDiscountSource.Promotion));
        var rules = new[]
        {
            new CatalogPromotionRuleDto(
                "PROMO-CAPPED",
                "One bundle only",
                true,
                100,
                2,
                15m,
                1,
                HeldAt.AddDays(-1),
                HeldAt.AddDays(1),
                null,
                [new CatalogPromotionProductDto("P-CAPPED", 1)])
        };

        var result = Mapper.Map(order, rules, revision: 1);

        Assert.False(result.IsBlocked);
        Assert.Equal(500L, Assert.Single(result.Payload!.PricingState.Lines).DiscountState.Cents);
    }

    [Fact]
    public void Map_weighted_rule_matches_evaluation_grouping_and_rounding()
    {
        var order = Order(
            SaleLine("P-WEIGHTED", 1m, 12m, 2m, null, PosCartLineDiscountSource.Promotion),
            SaleLine("P-REGULAR", 1m, 6m, 1m, null, PosCartLineDiscountSource.Promotion));
        var rules = new[]
        {
            new CatalogPromotionRuleDto(
                "PROMO-WEIGHTED-PARITY",
                "Weighted parity",
                true,
                100,
                3,
                15m,
                null,
                HeldAt.AddDays(-1),
                HeldAt.AddDays(1),
                null,
                [
                    new CatalogPromotionProductDto("P-WEIGHTED", 2),
                    new CatalogPromotionProductDto("P-REGULAR", 1)
                ])
        };

        var result = Mapper.Map(order, rules, revision: 1);

        Assert.False(result.IsBlocked);
        Assert.Equal(200L, result.Payload!.PricingState.Lines[0].DiscountState.Cents);
        Assert.Equal(100L, result.Payload.PricingState.Lines[1].DiscountState.Cents);
    }

    [Fact]
    public void Map_cent_allocation_across_lines_follows_the_shared_remaining_amount_allocation()
    {
        // 3 件 2.90：P1(1.00)×2 + P2(1.00)×1，折扣 0.10 按行合并后用递减的剩余折扣/剩余原价分摊 -> P1 0.07 / P2 0.03。
        // 购物车里的旧引擎与新引擎现在用同一份分摊（PromotionBundleAllocator），重演只认这个结果。
        var rules = new[] { ThreeForTwoNinety() };

        var exact = Mapper.Map(
            Order(
                SaleLine("P1", 2, 1.00m, 0.07m, null, PosCartLineDiscountSource.Promotion),
                SaleLine("P2", 1, 1.00m, 0.03m, null, PosCartLineDiscountSource.Promotion)),
            rules,
            revision: 1);
        Assert.False(exact.IsBlocked);
        Assert.Equal(7L, exact.Payload!.PricingState.Lines[0].DiscountState.Cents);
        Assert.Equal(3L, exact.Payload.PricingState.Lines[1].DiscountState.Cents);

        // 老版本旧引擎按每个实物单位分摊出来的 0.06 / 0.04 对不上 -> 阻断。
        var oldPerUnitAllocation = Mapper.Map(
            Order(
                SaleLine("P1", 2, 1.00m, 0.06m, null, PosCartLineDiscountSource.Promotion),
                SaleLine("P2", 1, 1.00m, 0.04m, null, PosCartLineDiscountSource.Promotion)),
            rules,
            revision: 1);
        Assert.True(oldPerUnitAllocation.IsBlocked);
        Assert.Equal(SharedHeldOrderMappingReasons.PromotionRulesMismatch, oldPerUnitAllocation.Block!.Reason);
    }

    [Fact]
    public void Map_ignores_a_negative_fixed_price_frozen_rule_like_the_evaluation_engine()
    {
        // 固定价必须 >= 0：负价规则整条忽略（与 PromotionEvaluationService、旧引擎一致）。
        // 若重演照单全收，会认为普通行应被折成 0 元而把没有促销折扣的挂单阻断。
        var negative = FrozenRule("PROMO-NEGATIVE", isExclusive: true, priority: 100, applyQuantity: 2, fixedPrice: -1m, "P1");
        var plain = Order(
            SaleLine("P1", 1, 11.00m, 0m, null),
            SaleLine("P1", 1, 11.00m, 0m, null));

        var result = Mapper.Map(plain, [negative], revision: 1);

        Assert.False(result.IsBlocked);
        Assert.Empty(result.Payload!.PricingState.Promotions);
    }

    [Fact]
    public void Map_negative_fixed_price_exclusive_rule_does_not_suppress_the_valid_rule()
    {
        // 负价的互斥高优先级规则被忽略后不参与互斥选择，有效规则照常重演（每行折 $1.00）。
        var negative = FrozenRule("PROMO-NEGATIVE", isExclusive: true, priority: 100, applyQuantity: 2, fixedPrice: -1m, "P1");
        var valid = FrozenRule("PROMO-VALID", isExclusive: false, priority: 1, applyQuantity: 2, fixedPrice: 20.00m, "P1");
        var order = Order(
            SaleLine("P1", 1, 11.00m, 1.00m, null, PosCartLineDiscountSource.Promotion),
            SaleLine("P1", 1, 11.00m, 1.00m, null, PosCartLineDiscountSource.Promotion));

        var result = Mapper.Map(order, [negative, valid], revision: 1);

        Assert.False(result.IsBlocked);
        Assert.Equal("PROMO-VALID", Assert.Single(result.Payload!.PricingState.Promotions).Id);
    }

    private static CatalogPromotionRuleDto ThreeForTwoNinety()
    {
        return FrozenRule("PROMO-3FOR290", isExclusive: true, priority: 100, applyQuantity: 3, fixedPrice: 2.90m, "P1", "P2");
    }

    private static CatalogPromotionRuleDto FrozenRule(
        string id,
        bool isExclusive,
        int priority,
        int applyQuantity,
        decimal fixedPrice,
        params string[] productCodes)
    {
        return new CatalogPromotionRuleDto(
            id,
            $"Rule {id}",
            isExclusive,
            priority,
            applyQuantity,
            fixedPrice,
            null,
            HeldAt.AddDays(-1),
            HeldAt.AddDays(1),
            null,
            productCodes.Select(code => new CatalogPromotionProductDto(code, 1)).ToArray());
    }
}
