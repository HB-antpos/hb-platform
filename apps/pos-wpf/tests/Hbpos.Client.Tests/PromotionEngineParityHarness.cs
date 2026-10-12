using System.Globalization;
using System.Text;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Promotions;

namespace Hbpos.Client.Tests;

// 两套自动促销引擎的对照工具（只给 PromotionEngine*Tests 用）：
//   旧引擎 = PosCartService 内的 RefreshAutomaticPromotionDiscounts（规则来自目录同步的 CatalogPromotionRuleDto，
//            加购/改数量/改价/删除/远程复核时在购物车内同步重算）；
//   新引擎 = PromotionEvaluationService + PosCartService.ApplyPromotionDiscounts（规则来自 /promotions/rules 的 PromotionRuleDto，
//            由 PosTerminalViewModel 在购物车变更后排队异步重算）。
// 同一份中立描述（ParityRule / ParityLine）分别喂给两套引擎，只比较最终落在购物车行上的结果。

/// <summary>中立促销规则描述：同时映射为旧引擎的 CatalogPromotionRuleDto 与新引擎的 PromotionRuleDto。</summary>
internal sealed record ParityRule(
    string Id,
    int ApplyQuantity,
    decimal FixedPrice,
    IReadOnlyList<(string Code, int Weight)> Products,
    bool IsExclusive = false,
    int Priority = 0,
    int? MaxApplicationsPerOrder = null,
    int StartDaysFromNow = -1,
    int EndDaysFromNow = 1)
{
    public static ParityRule Of(
        string id,
        int applyQuantity,
        decimal fixedPrice,
        string[] productCodes,
        bool isExclusive = false,
        int priority = 0,
        int? maxApplicationsPerOrder = null,
        int startDaysFromNow = -1,
        int endDaysFromNow = 1)
    {
        return new ParityRule(
            id,
            applyQuantity,
            fixedPrice,
            productCodes.Select(code => (code, 1)).ToArray(),
            isExclusive,
            priority,
            maxApplicationsPerOrder,
            startDaysFromNow,
            endDaysFromNow);
    }

    public CatalogPromotionRuleDto ToLegacy(DateTimeOffset now)
    {
        return new CatalogPromotionRuleDto(
            Id,
            $"Rule {Id}",
            IsExclusive,
            Priority,
            ApplyQuantity,
            FixedPrice,
            MaxApplicationsPerOrder,
            now.AddDays(StartDaysFromNow),
            now.AddDays(EndDaysFromNow),
            now,
            Products.Select(product => new CatalogPromotionProductDto(product.Code, product.Weight)).ToArray());
    }

    public PromotionRuleDto ToUnified(DateTimeOffset now)
    {
        return new PromotionRuleDto(
            Id,
            $"Rule {Id}",
            now.AddDays(StartDaysFromNow).UtcDateTime,
            now.AddDays(EndDaysFromNow).UtcDateTime,
            IsExclusive,
            Priority,
            ApplyQuantity,
            FixedPrice,
            MaxApplicationsPerOrder,
            Products.Select(product => new PromotionRuleProductDto(product.Code, product.Weight)).ToArray());
    }
}

internal enum ParityLineKind
{
    /// <summary>普通销售行，按目录价。</summary>
    Sale,
    /// <summary>普通行 + 行手工折扣金额（Parameter = 折扣金额）。</summary>
    ManualDiscountAmount,
    /// <summary>普通行 + 行手工折扣百分比（Parameter = 百分比）。</summary>
    ManualDiscountPercent,
    /// <summary>普通行 + 手工改价（Parameter = 改后的单价）。</summary>
    ManualPrice,
    /// <summary>带目录折扣基线的普通行（Parameter = DiscountRate，0.2 = 8 折）。</summary>
    CatalogDiscount,
    /// <summary>开放商品行。</summary>
    OpenItem,
    /// <summary>退货行。</summary>
    Return
}

internal sealed record ParityLine(
    string Product,
    decimal Price,
    int Quantity = 1,
    ParityLineKind Kind = ParityLineKind.Sale,
    decimal Parameter = 0m,
    string? Lookup = null);

internal sealed record ParityLineOutcome(
    string Product,
    decimal Quantity,
    decimal UnitPrice,
    decimal Gross,
    decimal Discount,
    decimal Actual,
    int Source)
{
    public override string ToString()
    {
        var source = Source switch
        {
            0 => "-",
            1 => "M",
            2 => "P",
            3 => "C",
            _ => "?"
        };
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Product}[{Quantity:0.##}x{UnitPrice:0.####}] gross={Gross:0.00} disc={Discount:0.00}{source} actual={Actual:0.00}");
    }
}

internal sealed record ParityOutcome(
    IReadOnlyList<ParityLineOutcome> Lines,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount)
{
    /// <summary>逐行折扣金额，便于断言与差异表。</summary>
    public decimal[] Discounts => Lines.Select(line => line.Discount).ToArray();

    public int[] Sources => Lines.Select(line => line.Source).ToArray();

    public bool SameAs(ParityOutcome other)
    {
        return ToString() == other.ToString();
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var line in Lines)
        {
            builder.Append(line).Append(" | ");
        }

        builder.Append(string.Create(
            CultureInfo.InvariantCulture,
            $"total={TotalAmount:0.00} disc={DiscountAmount:0.00} actual={ActualAmount:0.00}"));
        return builder.ToString();
    }
}

/// <summary>带「按生效时间过滤」的内存规则仓储：口径与 LocalPromotionRepository.GetActiveRulesAsync 一致（起止含边界）。</summary>
internal sealed class WindowedPromotionRepository : ILocalPromotionRepository
{
    private readonly Dictionary<string, IReadOnlyList<PromotionRuleDto>> _rulesByStore = new(StringComparer.OrdinalIgnoreCase);

    public void SeedStore(string storeCode, IReadOnlyList<PromotionRuleDto> rules)
    {
        _rulesByStore[storeCode] = rules;
    }

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
        if (!_rulesByStore.TryGetValue(storeCode, out var rules))
        {
            return Task.FromResult<IReadOnlyList<PromotionRuleDto>>([]);
        }

        var asOfUtc = asOf.UtcDateTime;
        IReadOnlyList<PromotionRuleDto> active = rules
            .Where(rule => rule.EffectiveStart <= asOfUtc && rule.EffectiveEnd >= asOfUtc)
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.EffectiveStart)
            .ThenBy(rule => rule.Id, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(active);
    }
}

internal static class PromotionEngineParityHarness
{
    public const string StoreCode = "S001";

    /// <summary>按描述向购物车加行；每行默认独立 lookup，避免被加购合并。返回创建出的行（与描述一一对应）。</summary>
    public static IReadOnlyList<CartLine> AddLines(PosCartService cart, IReadOnlyList<ParityLine> lines)
    {
        var created = new List<CartLine>();
        for (var index = 0; index < lines.Count; index++)
        {
            var spec = lines[index];
            var lookup = spec.Lookup ?? $"LK-{index}-{spec.Product}";
            CartLine line;
            switch (spec.Kind)
            {
                case ParityLineKind.OpenItem:
                    line = cart.AddOpenItem(CreateItem(spec.Product, lookup, spec.Price), spec.Price);
                    break;
                case ParityLineKind.Return:
                    line = cart.AddReturnLine(new ReturnCartLineRequest(
                        StoreCode,
                        spec.Product,
                        null,
                        $"Return {spec.Product}",
                        lookup,
                        spec.Product,
                        null,
                        spec.Quantity,
                        spec.Price,
                        PriceSourceKind.StoreRetailPrice,
                        PriceSourceKind.StoreRetailPrice.ToString(),
                        $"return-{index}",
                        Guid.NewGuid(),
                        Guid.NewGuid()));
                    break;
                case ParityLineKind.CatalogDiscount:
                    line = cart.AddItem(CreateItem(spec.Product, lookup, spec.Price, discountRate: spec.Parameter));
                    break;
                default:
                    line = cart.AddItem(CreateItem(spec.Product, lookup, spec.Price));
                    break;
            }

            if (spec.Kind != ParityLineKind.Return && spec.Quantity != 1)
            {
                Assert.True(cart.SetLineQuantity(line, spec.Quantity));
            }

            switch (spec.Kind)
            {
                case ParityLineKind.ManualDiscountAmount:
                    Assert.True(cart.SetLineDiscountAmount(line, spec.Parameter));
                    break;
                case ParityLineKind.ManualDiscountPercent:
                    Assert.True(cart.SetLineDiscountPercent(line, spec.Parameter));
                    break;
                case ParityLineKind.ManualPrice:
                    Assert.True(cart.SetLineUnitPrice(line, spec.Parameter));
                    break;
            }

            created.Add(line);
        }

        return created;
    }

    /// <summary>
    /// 旧引擎：规则先挂到购物车（SetAutomaticPromotionRules），再逐行加购；每次编辑都在购物车内同步重算。
    /// rulesFirst=false 时先加完行再挂规则（模拟目录重载后 ShellCatalogService.ApplyPromotionRules 触发的重算）。
    /// </summary>
    public static ParityOutcome RunLegacy(
        IReadOnlyList<ParityRule> rules,
        IReadOnlyList<ParityLine> lines,
        bool rulesFirst = true)
    {
        var now = DateTimeOffset.UtcNow;
        var cart = new PosCartService();
        // 旧规则在生产里经 LocalCatalogRepository.LoadPromotionRulesAsync 载入，顺序固定为
        // ORDER BY IsExclusive DESC, Priority DESC, PromotionId（BINARY 序数）；这里按同一口径排序，避免对照受输入顺序影响。
        var legacyRules = rules
            .OrderByDescending(rule => rule.IsExclusive)
            .ThenByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.Id, StringComparer.Ordinal)
            .Select(rule => rule.ToLegacy(now))
            .ToArray();
        if (rulesFirst)
        {
            cart.SetAutomaticPromotionRules(legacyRules);
            AddLines(cart, lines);
        }
        else
        {
            AddLines(cart, lines);
            cart.SetAutomaticPromotionRules(legacyRules);
        }

        return Capture(cart);
    }

    /// <summary>新引擎：购物车不挂旧规则；行加完后由 PromotionEvaluationService 评估，再经 ApplyPromotionDiscounts 落到行上（与 VM 的排队重算同一路径）。</summary>
    public static async Task<ParityOutcome> RunUnifiedAsync(
        IReadOnlyList<ParityRule> rules,
        IReadOnlyList<ParityLine> lines,
        ILocalPromotionRepository? repositoryOverride = null)
    {
        var now = DateTimeOffset.UtcNow;
        var cart = new PosCartService();
        AddLines(cart, lines);
        await ApplyUnifiedAsync(cart, rules, now, repositoryOverride);
        return Capture(cart);
    }

    public static async Task ApplyUnifiedAsync(
        PosCartService cart,
        IReadOnlyList<ParityRule> rules,
        DateTimeOffset now,
        ILocalPromotionRepository? repositoryOverride = null)
    {
        var repository = repositoryOverride;
        if (repository is null)
        {
            var windowed = new WindowedPromotionRepository();
            windowed.SeedStore(StoreCode, rules.Select(rule => rule.ToUnified(now)).ToArray());
            repository = windowed;
        }

        var service = new PromotionEvaluationService(repository);
        var discounts = await service.EvaluateAsync(cart.Lines.ToArray(), StoreCode, DateTimeOffset.UtcNow);
        cart.ApplyPromotionDiscounts(discounts);
    }

    public static ParityOutcome Capture(PosCartService cart)
    {
        return new ParityOutcome(
            cart.Lines
                .Select(line => new ParityLineOutcome(
                    line.ProductCode,
                    line.Quantity,
                    line.UnitPrice,
                    line.GrossAmount,
                    line.DiscountAmount,
                    line.ActualAmount,
                    (int)line.DiscountSource))
                .ToArray(),
            cart.TotalAmount,
            cart.DiscountAmount,
            cart.ActualAmount);
    }

    public static SellableItemDto CreateItem(
        string productCode,
        string lookupCode,
        decimal price,
        decimal? discountRate = null)
    {
        return new SellableItemDto(
            StoreCode: StoreCode,
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: $"Item {productCode}",
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode.Trim(),
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: null,
            DiscountRate: discountRate);
    }
}
