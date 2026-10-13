using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Promotions;

namespace Hbpos.Client.Wpf.Services;

public interface IPromotionEvaluationService
{
    Task<IReadOnlyList<PromotionLineDiscount>> EvaluateAsync(
        IReadOnlyList<CartLine> lines,
        string storeCode,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default);
}

public sealed record PromotionLineDiscount(
    CartLine Line,
    decimal DiscountAmount);

public sealed class PromotionEvaluationService(ILocalPromotionRepository localPromotionRepository) : IPromotionEvaluationService
{
    public async Task<IReadOnlyList<PromotionLineDiscount>> EvaluateAsync(
        IReadOnlyList<CartLine> lines,
        string storeCode,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
    {
        if (lines.Count == 0 || string.IsNullOrWhiteSpace(storeCode))
        {
            return [];
        }

        var activeRules = await localPromotionRepository.GetActiveRulesAsync(storeCode, asOf, cancellationToken);
        if (activeRules.Count == 0)
        {
            return [];
        }

        var eligibleLines = lines
            .Where(IsEligibleSaleLine)
            .ToArray();
        if (eligibleLines.Length == 0)
        {
            return [];
        }

        // 固定价必须 >= 0：负价规则整条忽略（也不参与互斥选择），与旧引擎 rule.FixedPrice >= 0m、
        // 目录源服务端过滤、iPad 契约校验一致；否则负价会把整组折成 0 元甚至倒贴。
        var applicableRules = activeRules
            .Where(rule =>
                rule.ApplyQuantity > 0 &&
                rule.FixedPrice >= 0m &&
                rule.Products.Count > 0 &&
                RuleHasEligibleProduct(rule, eligibleLines))
            .ToArray();
        if (applicableRules.Length == 0)
        {
            return [];
        }

        var rulesToEvaluate = SelectRulesToEvaluate(applicableRules);
        var plans = new List<PromotionEvaluationPlan>();
        foreach (var rule in rulesToEvaluate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ruleProductWeights = BuildRuleProductWeights(rule);
            if (ruleProductWeights.Count == 0)
            {
                continue;
            }

            var budget = PromotionComputationBudget.CalculateRule(
                rule.Id,
                eligibleLines.Select(line => new PromotionBudgetLine(
                    NormalizeProductCode(line.ProductCode),
                    line.Quantity)),
                ruleProductWeights,
                rule.ApplyQuantity,
                rule.MaxApplicationsPerOrder);
            plans.Add(new PromotionEvaluationPlan(rule, ruleProductWeights, budget));
        }

        // 所有选中规则先统一预检；任一超限都不允许留下部分自动促销结果。
        PromotionComputationBudget.EnsureOrderLimit(plans.Select(plan => plan.Budget));
        var discountsByLine = new Dictionary<CartLine, decimal>();
        // 折扣容量 = 行原价 - 该行已累计的自动促销折扣；多条规则叠加不得超过行原价。
        // D3：超出容量的份额直接丢弃，不转给其它还有容量的行（与旧引擎「先累加、再截断到行原价」同口径）。
        Func<CartLine, decimal> getRemainingCapacity =
            line => GetRemainingLineDiscountCapacity(discountsByLine, line);

        foreach (var plan in plans)
        {
            if (plan.Budget.WorkUnits <= 0)
            {
                continue;
            }

            // 与 Web 端评估保持一致：每条非排他规则独立按购物车顺序分组，不跨规则消费同一份展开单位。
            var selectedUnits = new List<PromotionBundleUnit<CartLine>>(
                Math.Min(plan.Rule.ApplyQuantity, checked((int)plan.Budget.WorkUnits)));
            foreach (var unit in EnumerateRuleUnits(
                         eligibleLines,
                         plan.ProductWeights,
                         plan.Budget.WorkUnits))
            {
                selectedUnits.Add(unit);
                if (selectedUnits.Count != plan.Rule.ApplyQuantity)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                // 一组的折扣计算与逐行分摊走共享实现（旧引擎、共享挂单重演同用），保证三处逐行一致。
                PromotionBundleAllocator.AllocateBundle(
                    selectedUnits,
                    plan.Rule.FixedPrice,
                    discountsByLine,
                    getRemainingCapacity);
                selectedUnits.Clear();
            }
        }

        return discountsByLine
            .Where(entry => entry.Value > 0m)
            .Select(entry => new PromotionLineDiscount(
                entry.Key,
                decimal.Round(entry.Value, 2, MidpointRounding.AwayFromZero)))
            .ToArray();
    }

    private static bool IsEligibleSaleLine(CartLine line)
    {
        return !line.IsReturnLine &&
            !line.IsOpenItem &&
            line.DiscountSource != CartLineDiscountSource.Manual &&
            // 不依赖金额舍入后的来源：只要有 catalog baseline 就不能参与固定总价分组。
            line.CatalogDiscountBasisPoints <= 0 &&
            line.UnitPrice > 0m &&
            line.GrossAmount > 0m &&
            PosCartService.IsPositiveIntegerQuantity(line.Quantity);
    }

    private static bool RuleHasEligibleProduct(PromotionRuleDto rule, IReadOnlyList<CartLine> lines)
    {
        var productCodes = rule.Products
            .Select(product => NormalizeProductCode(product.ProductCode))
            .Where(productCode => !string.IsNullOrEmpty(productCode))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return lines.Any(line => productCodes.Contains(NormalizeProductCode(line.ProductCode)));
    }

    private static IReadOnlyList<PromotionRuleDto> SelectRulesToEvaluate(IReadOnlyList<PromotionRuleDto> rules)
    {
        var exclusiveRule = rules
            .Where(rule => rule.IsExclusive)
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (exclusiveRule is not null)
        {
            return [exclusiveRule];
        }

        return rules
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, int> BuildRuleProductWeights(PromotionRuleDto rule)
    {
        var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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

    private static IEnumerable<PromotionBundleUnit<CartLine>> EnumerateRuleUnits(
        IReadOnlyList<CartLine> lines,
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
                // Web 端的权重语义是按 qty * UnitWeight 展开同价单位，每个展开单位自身权重都视为 1。
                for (var weightIndex = 0; weightIndex < unitWeight; weightIndex++)
                {
                    yield return new PromotionBundleUnit<CartLine>(
                        line,
                        line.UnitPrice,
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
        IReadOnlyDictionary<CartLine, decimal> discountsByLine,
        CartLine line)
    {
        var currentDiscount = discountsByLine.TryGetValue(line, out var value)
            ? value
            : 0m;
        // 中文注释：自动促销对同一行的累计折扣不能超过该行真实金额，避免 UnitWeight 把折扣上限放大。
        return Math.Max(0m, decimal.Round(line.GrossAmount - currentDiscount, 2, MidpointRounding.AwayFromZero));
    }

    private static string NormalizeProductCode(string? productCode)
    {
        return (productCode ?? string.Empty).Trim();
    }

    private sealed record PromotionEvaluationPlan(
        PromotionRuleDto Rule,
        IReadOnlyDictionary<string, int> ProductWeights,
        PromotionRuleBudget Budget);
}
