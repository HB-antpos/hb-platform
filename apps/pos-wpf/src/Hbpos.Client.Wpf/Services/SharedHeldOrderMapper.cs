using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.HeldOrders;

namespace Hbpos.Client.Wpf.Services;

public interface ISharedHeldOrderMapper
{
    /// <summary>
    /// 把普通 sale 挂单快照映射为共享挂单 canonical。
    /// return/open-item 行拒绝；自动促销折扣只有在调用方提供完整冻结规则集合且
    /// 与 WPF 评估结果精确对应时才发布，否则返回稳定阻断原因并保留本地。
    /// </summary>
    SharedHeldOrderMappingResult Map(
        SuspendedOrder order,
        IReadOnlyList<CatalogPromotionRuleDto>? frozenPromotionRules,
        int revision);
}

public sealed class SharedHeldOrderMapper : ISharedHeldOrderMapper
{
    public SharedHeldOrderMappingResult Map(
        SuspendedOrder order,
        IReadOnlyList<CatalogPromotionRuleDto>? frozenPromotionRules,
        int revision)
    {
        foreach (var line in order.Lines)
        {
            if (line.Kind == CartLineKind.Return)
            {
                return Block(
                    SharedHeldOrderMappingReasons.ReturnLine,
                    $"挂单行 {line.SuspendedOrderLineGuid:D} 是退货行，共享挂单只支持普通 sale。");
            }

            if (line.Kind == CartLineKind.OpenItem)
            {
                return Block(
                    SharedHeldOrderMappingReasons.OpenItemLine,
                    $"挂单行 {line.SuspendedOrderLineGuid:D} 是 open-item，共享挂单拒绝。");
            }

            // 普通 sale 允许称重小数数量（契约 line.quantity 为 decimal，但仍有统一上限）。
            if (line.Quantity <= 0m
                || line.Quantity > SharedHeldOrderCanonicalConstants.MaxQuantity)
            {
                throw new ArgumentException("挂单行数量必须是有界正数。", nameof(order));
            }

            if (line.CatalogDiscountBasisPoints is < 0 or > SharedHeldOrderCanonicalConstants.MaxBasisPoints)
            {
                throw new ArgumentException("挂单行目录折扣基线必须是 0..10000。", nameof(order));
            }

            if (line.CatalogDiscountBasisPoints > 0
                && line.DiscountSource == PosCartLineDiscountSource.Promotion)
            {
                return Block(
                    SharedHeldOrderMappingReasons.CatalogDiscountPromotionConflict,
                    $"挂单行 {line.SuspendedOrderLineGuid:D} 同时存在目录折扣基线与促销折扣，保留本地。");
            }
        }

        var lines = order.Lines.Select(ToCanonicalLine).ToArray();
        var promotionLines = lines
            .Where(line => line.DiscountState.Mode == SharedHeldOrderCanonicalConstants.DiscountPromotion)
            .ToArray();

        IReadOnlyList<SharedHeldOrderPromotionDefinition> promotions = [];
        if (promotionLines.Length > 0 && (frozenPromotionRules is null || frozenPromotionRules.Count == 0))
        {
            return Block(
                SharedHeldOrderMappingReasons.PromotionRulesMissing,
                "存在自动促销折扣但未提供完整冻结促销规则集合，无法精确对应，保留本地。");
        }

        if (frozenPromotionRules is { Count: > 0 } frozenRules)
        {
            if (TryFindDuplicatePromotionRuleId(frozenRules) is { } duplicateId)
            {
                return Block(
                    SharedHeldOrderMappingReasons.PromotionRulesMismatch,
                    $"冻结促销规则包含重复 definition id: {duplicateId}，无法精确对应，保留本地。");
            }

            Dictionary<string, IReadOnlyList<string>> promotionIdsByLine;
            try
            {
                if (!TryMatchPromotionDiscounts(lines, frozenRules, out var mismatchDetail, out var matchedPromotionIds))
                {
                    return Block(SharedHeldOrderMappingReasons.PromotionRulesMismatch, mismatchDetail!);
                }

                promotionIdsByLine = matchedPromotionIds!;
            }
            catch (PromotionComputationBudgetExceededException ex)
            {
                ConsoleLog.Write(
                    "Promotion",
                    $"shared held order mapping blocked budget-exceeded {ex.ToDiagnosticText()}");
                return Block(
                    SharedHeldOrderMappingReasons.PromotionBudgetExceeded,
                    "数量超出自动促销计算上限，本地挂单已保留，未生成共享数据。");
            }

            // 精确对应确认后，把每条促销行命中的冻结规则 id 写回 canonical。
            lines = lines
                .Select(line => line.DiscountState.Mode == SharedHeldOrderCanonicalConstants.DiscountPromotion
                    ? line with
                    {
                        DiscountState = line.DiscountState with
                        {
                            PromotionIds = promotionIdsByLine[line.LineId]
                        }
                    }
                    : line)
                .ToArray();
            // 只输出实际被促销行贡献/引用的冻结规则定义，避免向目标端泄露无关目录规则。
            var contributingRuleIds = promotionIdsByLine
                .Values
                .SelectMany(ids => ids)
                .ToHashSet(StringComparer.Ordinal);
            promotions = frozenRules
                .Where(rule => contributingRuleIds.Contains(rule.PromotionId))
                .Select(ToPromotionDefinition)
                .ToArray();
        }

        var pricingState = new SharedHeldOrderPricingState(
            revision,
            SharedHeldOrderCanonicalConstants.SaleMode,
            FormatIso(order.SuspendedAt),
            promotions,
            lines);
        var payloadVersion = lines.Any(line => line.CatalogDiscountBasisPoints > 0)
            ? SharedHeldOrderCanonicalPayload.VersionV2
            : SharedHeldOrderCanonicalPayload.VersionV1;
        return new SharedHeldOrderMappingResult(
            new SharedHeldOrderCanonicalPayload(payloadVersion, pricingState),
            null);
    }

    private static SharedHeldOrderMappingResult Block(string reason, string detail)
    {
        return new SharedHeldOrderMappingResult(null, new SharedHeldOrderMappingBlock(reason, detail));
    }

    private static SharedHeldOrderPricingLine ToCanonicalLine(SuspendedOrderLine line)
    {
        var discountAmountCents = MoneyToCents(line.DiscountAmount);
        SharedHeldOrderDiscountState discountState;
        if (line.DiscountSource == PosCartLineDiscountSource.Manual)
        {
            // Manual + 0 是合法覆盖状态：整单分摊为零或极小手工百分比都不能
            // 降级成 none，否则另一终端会重新启用 catalog 折扣。
            // 百分比只有能无损表示为整数 basisPoints（percent*100 为整数且在合法区间）
            // 才冻结为百分比；否则（例如 10.555%）发布冻结的手工金额 cents。
            discountState = line.DiscountPercent is decimal percent
                && TryGetExactBasisPoints(percent, out var basisPoints)
                ? new SharedHeldOrderDiscountState(
                    SharedHeldOrderCanonicalConstants.DiscountManualPercent,
                    BasisPoints: basisPoints)
                : new SharedHeldOrderDiscountState(
                    SharedHeldOrderCanonicalConstants.DiscountManualAmount,
                    Cents: discountAmountCents);
        }
        else if (line.CatalogDiscountBasisPoints > 0
            && line.DiscountSource is PosCartLineDiscountSource.Catalog or PosCartLineDiscountSource.None)
        {
            // 目录折扣金额由 baseline 表达；不能降级成 manual，否则取单后会失去 Catalog 来源。
            discountState = new SharedHeldOrderDiscountState(SharedHeldOrderCanonicalConstants.DiscountNone);
        }
        else if (discountAmountCents == 0)
        {
            discountState = new SharedHeldOrderDiscountState(SharedHeldOrderCanonicalConstants.DiscountNone);
        }
        else if (line.DiscountSource == PosCartLineDiscountSource.Promotion)
        {
            discountState = new SharedHeldOrderDiscountState(
                SharedHeldOrderCanonicalConstants.DiscountPromotion,
                Cents: discountAmountCents);
        }
        else
        {
            // None 来源却带折扣的旧数据按手工金额无损保留，避免静默丢失。
            discountState = new SharedHeldOrderDiscountState(
                SharedHeldOrderCanonicalConstants.DiscountManualAmount,
                Cents: discountAmountCents);
        }

        return new SharedHeldOrderPricingLine(
            line.SuspendedOrderLineGuid.ToString("D"),
            line.ProductCode,
            line.ItemNumber,
            line.LookupCode,
            line.DisplayName,
            line.Quantity,
            MoneyToCents(line.UnitPrice),
            line.IsManualPrice
                ? SharedHeldOrderCanonicalConstants.BasePriceSourceManual
                : SharedHeldOrderCanonicalConstants.BasePriceSourceCatalog,
            new SharedHeldOrderLineSyncProvenance(line.ReferenceCode, (int)line.PriceSource),
            SharedHeldOrderCanonicalConstants.LineKindSale,
            null,
            null,
            null,
            discountState,
            line.CatalogDiscountBasisPoints);
    }

    /// <summary>
    /// 冻结规则 definition id 必须唯一：重复 id 会产生非法 canonical
    /// （validator 也会拒绝），挂单数据属于损坏，fail-closed Blocked。
    /// </summary>
    private static string? TryFindDuplicatePromotionRuleId(IReadOnlyList<CatalogPromotionRuleDto> rules)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (!ids.Add(rule.PromotionId))
            {
                return rule.PromotionId;
            }
        }

        return null;
    }

    /// <summary>
    /// 用冻结规则按 WPF PromotionEvaluationService 相同的确定性算法重演，并要求
    /// 每个促销行折扣完全等于冻结结果、非促销行不产生任何促销折扣。
    /// </summary>
    private static bool TryMatchPromotionDiscounts(
        IReadOnlyList<SharedHeldOrderPricingLine> lines,
        IReadOnlyList<CatalogPromotionRuleDto> frozenRules,
        out string? mismatchDetail,
        out Dictionary<string, IReadOnlyList<string>>? promotionIdsByLine)
    {
        var eligibleLines = lines
            .Where(line =>
                line.DiscountState.Mode != SharedHeldOrderCanonicalConstants.DiscountManualAmount
                && line.DiscountState.Mode != SharedHeldOrderCanonicalConstants.DiscountManualPercent
                && line.CatalogDiscountBasisPoints == 0
                && line.UnitPriceCents > 0
                && line.Quantity > 0)
            .ToArray();
        // 固定价 >= 0 的过滤与 PromotionEvaluationService 一致：负价规则整条忽略，不参与互斥选择，也不产生折扣。
        var applicableRules = frozenRules
            .Where(rule =>
                rule.ApplyQuantity > 0 &&
                rule.FixedPrice >= 0m &&
                rule.Products.Count > 0 &&
                RuleHasEligibleProduct(rule, eligibleLines))
            .ToArray();
        var rulesToEvaluate = SelectRulesToEvaluate(applicableRules);

        var plans = new List<SharedPromotionPlan>();
        foreach (var rule in rulesToEvaluate)
        {
            var weights = BuildRuleProductWeights(rule);
            if (weights.Count == 0)
            {
                continue;
            }

            var budget = PromotionComputationBudget.CalculateRule(
                rule.PromotionId,
                eligibleLines.Select(line => new PromotionBudgetLine(
                    NormalizeProductCode(line.ProductCode),
                    line.Quantity)),
                weights,
                rule.ApplyQuantity,
                rule.MaxApplicationsPerOrder);
            plans.Add(new SharedPromotionPlan(rule, weights, budget));
        }

        PromotionComputationBudget.EnsureOrderLimit(plans.Select(plan => plan.Budget));
        var cumulative = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var contributionsByRule = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            var before = new Dictionary<string, decimal>(cumulative, StringComparer.Ordinal);
            ApplyRule(cumulative, eligibleLines, plan);
            contributionsByRule[plan.Rule.PromotionId] = cumulative
                .Where(entry => !before.TryGetValue(entry.Key, out var previous) || entry.Value != previous)
                .ToDictionary(entry => entry.Key, entry => entry.Value - (before.TryGetValue(entry.Key, out var previous) ? previous : 0m), StringComparer.Ordinal);
        }

        var promotionIdsByLineResult = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var expectedCents = MoneyToCents(cumulative.TryGetValue(line.LineId, out var amount) ? amount : 0m);
            if (line.DiscountState.Mode == SharedHeldOrderCanonicalConstants.DiscountPromotion)
            {
                if (expectedCents <= 0 || line.DiscountState.Cents != expectedCents)
                {
                    mismatchDetail = $"行 {line.LineId} 冻结规则应产生 {expectedCents} cents，挂单记录 {line.DiscountState.Cents} cents。";
                    promotionIdsByLine = null;
                    return false;
                }

                var contributingRuleIds = contributionsByRule
                    .Where(entry => entry.Value.TryGetValue(line.LineId, out var contribution) && contribution > 0m)
                    .Select(entry => entry.Key)
                    .ToArray();
                if (contributingRuleIds.Length == 0)
                {
                    mismatchDetail = $"行 {line.LineId} 无贡献规则，无法写入 promotionIds。";
                    promotionIdsByLine = null;
                    return false;
                }

                promotionIdsByLineResult[line.LineId] = contributingRuleIds;
            }
            else if (expectedCents != 0)
            {
                mismatchDetail = $"行 {line.LineId} 未记录促销折扣，但冻结规则会产生 {expectedCents} cents。";
                promotionIdsByLine = null;
                return false;
            }
        }

        mismatchDetail = null;
        promotionIdsByLine = promotionIdsByLineResult;
        return true;
    }

    private static bool RuleHasEligibleProduct(
        CatalogPromotionRuleDto rule,
        IReadOnlyList<SharedHeldOrderPricingLine> lines)
    {
        var productCodes = rule.Products
            .Select(product => NormalizeProductCode(product.ProductCode))
            .Where(productCode => !string.IsNullOrEmpty(productCode))
            .ToHashSet(StringComparer.Ordinal);
        return lines.Any(line => productCodes.Contains(NormalizeProductCode(line.ProductCode)));
    }

    private static IReadOnlyList<CatalogPromotionRuleDto> SelectRulesToEvaluate(
        IReadOnlyList<CatalogPromotionRuleDto> applicableRules)
    {
        var exclusiveRule = applicableRules
            .Where(rule => rule.IsExclusive)
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.PromotionId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (exclusiveRule is not null)
        {
            return [exclusiveRule];
        }

        return applicableRules
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.PromotionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ApplyRule(
        Dictionary<string, decimal> discountsByLine,
        IReadOnlyList<SharedHeldOrderPricingLine> lines,
        SharedPromotionPlan plan)
    {
        if (plan.Budget.WorkUnits <= 0)
        {
            return;
        }

        var selectedUnits = new List<PromotionBundleUnit<string>>(
            Math.Min(plan.Rule.ApplyQuantity, checked((int)plan.Budget.WorkUnits)));
        foreach (var unit in EnumerateRuleUnits(lines, plan.ProductWeights, plan.Budget.WorkUnits))
        {
            selectedUnits.Add(unit);
            if (selectedUnits.Count != plan.Rule.ApplyQuantity)
            {
                continue;
            }

            // 重演必须与购物车引擎逐行精确相等，所以一组的折扣计算与分摊直接用同一份共享实现，
            // 不在这里另写一份（分摊到分、亚分位单价的行金额舍入口径都随共享实现一起变）。
            // 叠加封顶（D3）同理：容量 = 行原价 - 已累计折扣，超出的份额丢弃（不转给其它行），与购物车引擎一致。
            PromotionBundleAllocator.AllocateBundle(
                selectedUnits,
                plan.Rule.FixedPrice,
                discountsByLine,
                lineId => GetRemainingLineDiscountCapacity(discountsByLine, lines, lineId));
            selectedUnits.Clear();
        }
    }

    private static Dictionary<string, int> BuildRuleProductWeights(CatalogPromotionRuleDto rule)
    {
        var weights = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var product in rule.Products)
        {
            var productCode = NormalizeProductCode(product.ProductCode);
            if (string.IsNullOrEmpty(productCode))
            {
                continue;
            }

            weights[productCode] = product.UnitWeight > 0 ? product.UnitWeight : 1;
        }

        return weights;
    }

    private static IEnumerable<PromotionBundleUnit<string>> EnumerateRuleUnits(
        IReadOnlyList<SharedHeldOrderPricingLine> lines,
        IReadOnlyDictionary<string, int> ruleProductWeights,
        long workUnits)
    {
        long emittedUnits = 0;
        foreach (var line in lines)
        {
            var productCode = NormalizeProductCode(line.ProductCode);
            if (!ruleProductWeights.TryGetValue(productCode, out var unitWeight))
            {
                continue;
            }

            var quantity = decimal.ToInt64(line.Quantity);
            for (long quantityIndex = 0; quantityIndex < quantity; quantityIndex++)
            {
                for (var weightIndex = 0; weightIndex < unitWeight; weightIndex++)
                {
                    // 与 PromotionEvaluationService 对齐：同一实物数量的权重单位在 bundle 内只计一次金额。
                    yield return new PromotionBundleUnit<string>(
                        line.LineId,
                        line.UnitPriceCents / 100m,
                        quantityIndex);
                    emittedUnits++;
                    if (emittedUnits >= workUnits)
                    {
                        yield break;
                    }
                }
            }
        }
    }

    private static decimal GetRemainingLineDiscountCapacity(
        IReadOnlyDictionary<string, decimal> discountsByLine,
        IReadOnlyList<SharedHeldOrderPricingLine> lines,
        string lineId)
    {
        var line = lines.First(candidate => candidate.LineId == lineId);
        var currentDiscount = discountsByLine.TryGetValue(lineId, out var value) ? value : 0m;
        var gross = Round2(line.UnitPriceCents / 100m * line.Quantity);
        return Math.Max(0m, Round2(gross - currentDiscount));
    }

    private static SharedHeldOrderPromotionDefinition ToPromotionDefinition(CatalogPromotionRuleDto rule)
    {
        return new SharedHeldOrderPromotionDefinition(
            rule.PromotionId,
            rule.Name,
            FormatIso(rule.EffectiveStart),
            FormatIso(rule.EffectiveEnd),
            rule.IsExclusive,
            rule.Priority,
            rule.ApplyQuantity,
            MoneyToCents(rule.FixedPrice),
            rule.MaxApplicationsPerOrder,
            rule.Products
                .Select(product => new SharedHeldOrderPromotionProduct(product.ProductCode, product.UnitWeight))
                .ToArray());
    }

    private static string FormatIso(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static string NormalizeProductCode(string? productCode)
    {
        return (productCode ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static long MoneyToCents(decimal amount)
    {
        return checked((long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
    }

    private static int BasisPointsFromPercent(decimal percent)
    {
        return checked((int)Math.Round(percent * 100m, 0, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// 只有 percent*100 为整数（可无损用 basisPoints 精确表示）且在合法区间时返回 true。
    /// 例如 10% -> 1000、10.55% -> 1055；10.555% -> 1055.5 不是整数，必须回退金额。
    /// </summary>
    private static bool TryGetExactBasisPoints(decimal percent, out int basisPoints)
    {
        var scaled = percent * 100m;
        if (decimal.Truncate(scaled) != scaled)
        {
            basisPoints = 0;
            return false;
        }

        basisPoints = BasisPointsFromPercent(percent);
        return basisPoints is >= 1 and <= SharedHeldOrderCanonicalConstants.MaxBasisPoints;
    }

    private static decimal Round2(decimal value)
    {
        return decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    private sealed record SharedPromotionPlan(
        CatalogPromotionRuleDto Rule,
        IReadOnlyDictionary<string, int> ProductWeights,
        PromotionRuleBudget Budget);
}

/// <summary>
/// 服务端 SharedSaleCartV1 与本地 canonical 的双向显式字段映射（仅集成所需）。
/// 不依赖 JSON 往返：strict canonical 解析器会拒绝 discount union 的 null 字段
/// （如 promotion 带 basisPoints:null），显式映射逐字段构造，天然避免该问题。
/// </summary>
public static class SharedHeldOrderContractMapper
{
    /// <summary>按 wire version 分派；API DTO 的 object payload 只能接受 V1/V2。</summary>
    public static SharedHeldOrderCanonicalPayload ToCanonical(object cart)
    {
        return cart switch
        {
            SharedSaleCartV1 v1 => ToCanonical(v1),
            SharedSaleCartV2 v2 => ToCanonical(v2),
            _ => throw new SharedSaleCartValidationException(
                "Shared sale cart payload must be SharedSaleCartV1 or SharedSaleCartV2.")
        };
    }

    public static SharedHeldOrderCanonicalPayload ToCanonical(SharedSaleCartV1 cart)
    {
        SharedSaleCartV1Validator.Validate(cart);
        return BuildCanonical(
            SharedHeldOrderCanonicalPayload.VersionV1,
            cart.PricingState.Revision,
            cart.PricingState.Mode,
            cart.PricingState.AsOfIso,
            cart.PricingState.Promotions,
            cart.PricingState.Lines.Select(line => ToCanonicalLine(line, 0)));
    }

    public static SharedHeldOrderCanonicalPayload ToCanonical(SharedSaleCartV2 cart)
    {
        SharedSaleCartV2Validator.Validate(cart);
        return BuildCanonical(
            SharedHeldOrderCanonicalPayload.VersionV2,
            cart.PricingState.Revision,
            cart.PricingState.Mode,
            cart.PricingState.AsOfIso,
            cart.PricingState.Promotions,
            cart.PricingState.Lines.Select(line => ToCanonicalLine(line, line.CatalogDiscountBasisPoints)));
    }

    private static SharedHeldOrderCanonicalPayload BuildCanonical(
        int payloadVersion,
        int revision,
        string mode,
        string asOfIso,
        IReadOnlyList<SharedPromotionV1> contractPromotions,
        IEnumerable<SharedHeldOrderPricingLine> contractLines)
    {
        var promotions = contractPromotions
            .Select(promotion => new SharedHeldOrderPromotionDefinition(
                promotion.Id,
                promotion.Name,
                promotion.EffectiveStartIso,
                promotion.EffectiveEndIso,
                promotion.IsExclusive,
                promotion.Priority,
                promotion.ApplyQuantity,
                promotion.FixedPriceCents,
                promotion.MaxApplicationsPerOrder,
                promotion.Products
                    .Select(product => new SharedHeldOrderPromotionProduct(
                        product.ProductCode,
                        product.UnitWeight))
                    .ToArray()))
            .ToArray();
        var payload = new SharedHeldOrderCanonicalPayload(
            payloadVersion,
            new SharedHeldOrderPricingState(
                revision,
                mode,
                asOfIso,
                promotions,
                contractLines.ToArray()));
        SharedHeldOrderCanonicalValidator.Validate(payload);
        return payload;
    }

    private static SharedHeldOrderPricingLine ToCanonicalLine(SharedSaleLineV1 line, int catalogDiscountBasisPoints)
    {
        return new SharedHeldOrderPricingLine(
            line.LineId,
            line.ProductCode,
            line.ItemNumber,
            line.LookupCode,
            line.DisplayName,
            line.Quantity,
            line.UnitPriceCents,
            line.BasePriceSource,
            line.SyncProvenance is { } provenance
                ? new SharedHeldOrderLineSyncProvenance(provenance.ReferenceCode, (int)provenance.PriceSource)
                : null,
            line.Kind,
            line.ReturnSourceKey,
            line.OriginalOrderGuid,
            line.OriginalOrderDetailGuid,
            ToCanonicalDiscount(line.DiscountState),
            catalogDiscountBasisPoints);
    }

    private static SharedHeldOrderPricingLine ToCanonicalLine(SharedSaleLineV2 line, int catalogDiscountBasisPoints)
    {
        return new SharedHeldOrderPricingLine(
            line.LineId,
            line.ProductCode,
            line.ItemNumber,
            line.LookupCode,
            line.DisplayName,
            line.Quantity,
            line.UnitPriceCents,
            line.BasePriceSource,
            line.SyncProvenance is { } provenance
                ? new SharedHeldOrderLineSyncProvenance(provenance.ReferenceCode, (int)provenance.PriceSource)
                : null,
            line.Kind,
            line.ReturnSourceKey,
            line.OriginalOrderGuid,
            line.OriginalOrderDetailGuid,
            ToCanonicalDiscount(line.DiscountState),
            catalogDiscountBasisPoints);
    }

    public static SharedSaleCartV1 ToContract(SharedHeldOrderCanonicalPayload payload)
    {
        return ToContractV1(payload);
    }

    public static object ToContract(SharedHeldOrderCanonicalPayload payload, int payloadVersion)
    {
        return payloadVersion switch
        {
            SharedSaleCartV1Constants.PayloadVersion => ToContractV1(payload),
            SharedSaleCartV2Constants.PayloadVersion => ToContractV2(payload),
            _ => throw new SharedSaleCartValidationException(
                $"Unsupported shared sale cart payload version: {payloadVersion}.")
        };
    }

    public static SharedSaleCartV2 ToContractV2(SharedHeldOrderCanonicalPayload payload)
    {
        SharedHeldOrderCanonicalValidator.Validate(payload);
        var pricing = payload.PricingState;
        var cart = new SharedSaleCartV2(
            SharedSaleCartV2Constants.PayloadVersion,
            new SharedPricingStateV2(
                pricing.Revision,
                pricing.Mode,
                pricing.AsOfIso,
                ToContractPromotions(pricing.Promotions),
                pricing.Lines.Select(line => new SharedSaleLineV2(
                    line.LineId,
                    line.ProductCode,
                    line.ItemNumber,
                    line.LookupCode,
                    line.DisplayName,
                    line.Quantity,
                    line.UnitPriceCents,
                    line.BasePriceSource,
                    line.SyncProvenance is { } provenance
                        ? new SharedLineSyncProvenanceV1(
                            provenance.ReferenceCode,
                            (PriceSourceKind)provenance.PriceSource)
                        : null,
                    line.Kind,
                    line.ReturnSourceKey,
                    line.OriginalOrderGuid,
                    line.OriginalOrderDetailGuid,
                    ToContractDiscount(line.DiscountState),
                    line.CatalogDiscountBasisPoints)).ToArray()));
        return SharedSaleCartV2Validator.Validate(cart);
    }

    private static SharedSaleCartV1 ToContractV1(SharedHeldOrderCanonicalPayload payload)
    {
        SharedHeldOrderCanonicalValidator.Validate(payload);
        var pricing = payload.PricingState;
        if (pricing.Lines.Any(line => line.CatalogDiscountBasisPoints > 0))
        {
            throw new SharedSaleCartValidationException(
                "Cannot downgrade a shared sale cart with catalog discount baseline to V1.");
        }

        var lines = pricing.Lines
            .Select(line => new SharedSaleLineV1(
                line.LineId,
                line.ProductCode,
                line.ItemNumber,
                line.LookupCode,
                line.DisplayName,
                line.Quantity,
                line.UnitPriceCents,
                line.BasePriceSource,
                line.SyncProvenance is { } provenance
                    ? new SharedLineSyncProvenanceV1(
                        provenance.ReferenceCode,
                        (PriceSourceKind)provenance.PriceSource)
                    : null,
                line.Kind,
                line.ReturnSourceKey,
                line.OriginalOrderGuid,
                line.OriginalOrderDetailGuid,
                ToContractDiscount(line.DiscountState)))
            .ToArray();
        var cart = new SharedSaleCartV1(
            SharedSaleCartV1Constants.PayloadVersion,
            new SharedPricingStateV1(
                pricing.Revision,
                pricing.Mode,
                pricing.AsOfIso,
                ToContractPromotions(pricing.Promotions),
                lines));
        return SharedSaleCartV1Validator.Validate(cart);
    }

    private static SharedPromotionV1[] ToContractPromotions(
        IReadOnlyList<SharedHeldOrderPromotionDefinition> promotions)
    {
        return promotions
            .Select(promotion => new SharedPromotionV1(
                promotion.Id,
                promotion.Name,
                promotion.EffectiveStartIso,
                promotion.EffectiveEndIso,
                promotion.IsExclusive,
                promotion.Priority,
                promotion.ApplyQuantity,
                promotion.FixedPriceCents,
                promotion.MaxApplicationsPerOrder,
                promotion.Products
                    .Select(product => new SharedPromotionProductV1(product.ProductCode, product.UnitWeight))
                    .ToArray()))
            .ToArray();
    }

    private static SharedHeldOrderDiscountState ToCanonicalDiscount(SharedLineDiscountStateV1 discount)
    {
        return discount.Mode switch
        {
            SharedSaleCartV1Constants.DiscountModeNone =>
                new SharedHeldOrderDiscountState(SharedHeldOrderCanonicalConstants.DiscountNone),
            SharedSaleCartV1Constants.DiscountModeManualAmount =>
                new SharedHeldOrderDiscountState(
                    SharedHeldOrderCanonicalConstants.DiscountManualAmount,
                    Cents: discount.Cents),
            SharedSaleCartV1Constants.DiscountModeManualPercent =>
                new SharedHeldOrderDiscountState(
                    SharedHeldOrderCanonicalConstants.DiscountManualPercent,
                    BasisPoints: discount.BasisPoints),
            SharedSaleCartV1Constants.DiscountModePromotion =>
                new SharedHeldOrderDiscountState(
                    SharedHeldOrderCanonicalConstants.DiscountPromotion,
                    Cents: discount.Cents,
                    PromotionIds: discount.PromotionIds),
            _ => throw new SharedHeldOrderCanonicalValidationException(
                $"未知折扣类型: {discount.Mode}")
        };
    }

    private static SharedLineDiscountStateV1 ToContractDiscount(SharedHeldOrderDiscountState discount)
    {
        return discount.Mode switch
        {
            SharedHeldOrderCanonicalConstants.DiscountNone =>
                new SharedLineDiscountStateV1(SharedSaleCartV1Constants.DiscountModeNone),
            SharedHeldOrderCanonicalConstants.DiscountManualAmount =>
                new SharedLineDiscountStateV1(
                    SharedSaleCartV1Constants.DiscountModeManualAmount,
                    Cents: discount.Cents),
            SharedHeldOrderCanonicalConstants.DiscountManualPercent =>
                new SharedLineDiscountStateV1(
                    SharedSaleCartV1Constants.DiscountModeManualPercent,
                    BasisPoints: discount.BasisPoints),
            SharedHeldOrderCanonicalConstants.DiscountPromotion =>
                new SharedLineDiscountStateV1(
                    SharedSaleCartV1Constants.DiscountModePromotion,
                    Cents: discount.Cents,
                    PromotionIds: discount.PromotionIds),
            _ => throw new SharedHeldOrderCanonicalValidationException(
                $"未知折扣类型: {discount.Mode}")
        };
    }
}
