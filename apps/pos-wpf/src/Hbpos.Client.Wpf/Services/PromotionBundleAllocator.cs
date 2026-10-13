namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 自动促销「凑够 ApplyQuantity 件」后展开出的一个单位。
/// TLine 是行的标识：购物车引擎用 CartLine，共享挂单重演用行 id 字符串。
/// SortOrder = 该单位属于本行第几件实物（同一件实物按 UnitWeight 展开的多个单位 SortOrder 相同）。
/// </summary>
internal readonly record struct PromotionBundleUnit<TLine>(TLine Line, decimal UnitPrice, long SortOrder)
    where TLine : notnull;

/// <summary>
/// 自动促销「固定总价」一组（bundle）的折扣计算与逐行分摊。
/// 三处共用同一份实现，不得各写一份，否则分摊到分会再次出现逐行差几分：
///   - PromotionEvaluationService（新引擎，规则来自 /promotions/rules）；
///   - PosCartService.RefreshAutomaticPromotionDiscounts（旧引擎，规则来自目录同步）；
///   - SharedHeldOrderMapper（共享挂单发布前用冻结规则重演，要求与购物车上的折扣逐行精确相等）。
/// </summary>
internal static class PromotionBundleAllocator
{
    /// <summary>
    /// 计算一组的折扣并累加到 <paramref name="discountsByLine"/>。
    /// getRemainingCapacity 返回某行还能再吃多少折扣（新引擎、共享挂单重演 = 行原价 - 已累计折扣）；
    /// 传 null 表示入账时不按行截断（旧引擎：多条规则先累加，最后由调用方整体截断到行原价）。
    /// 叠加封顶（D3）三处口径一致：超出行原价的份额直接丢弃，不转给其它还有容量的行，
    /// 逐行份额始终按「未封顶」口径算出，容量只决定入账多少。
    /// 因此传容量等价于旧引擎的「先累加、再截断到行原价」：min(原价, min(原价, a) + b) = min(原价, a + b)。
    /// </summary>
    internal static void AllocateBundle<TLine>(
        IReadOnlyList<PromotionBundleUnit<TLine>> selectedUnits,
        decimal fixedPrice,
        Dictionary<TLine, decimal> discountsByLine,
        Func<TLine, decimal>? getRemainingCapacity)
        where TLine : notnull
    {
        // 关键逻辑：UnitWeight 只用于凑够 ApplyQuantity 的阈值计数，不参与组内金额累加。
        // 同一件实物按权重展开成多个单位，必须按 (行, 实物序号) 折叠回去让单价只计一次；
        // 否则权重大于 1 的商品单价会被重复累加，固定总价折扣随之放大，直接少收钱。
        var physicalUnits = selectedUnits
            .GroupBy(unit => (unit.Line, unit.SortOrder))
            .Select(group => group.First())
            .OrderBy(unit => unit.SortOrder)
            .ToArray();

        // 行金额 = 本组内该行实物的原价（不先舍入）之和，只在「行」这一层舍入到分一次。
        // 这与购物车行原价（CartLine.GrossAmount = Round(数量 × 单价, 2)）的舍入口径一致，
        // 所以单价带亚分位（如 3.335×3 凑 3 件 9.00）时，折扣 = 行原价 10.01 - 9.00 = 1.01，凑组实收恰为规则目标价 9.00；
        // 若先把每个单位舍入到分再求和（3.34×3 = 10.02），会多折 1 分、实收 8.99。
        // 同一行跨多个组时，行原价是整行一次舍入、各组分别舍入后之和可能差 1 分，这是既有行为，保持不变。
        var groupedLines = physicalUnits
            .GroupBy(unit => unit.Line)
            .Select(group => new GroupedLine<TLine>(
                group.Key,
                Round2(group.Sum(unit => unit.UnitPrice)),
                group.Min(unit => unit.SortOrder)))
            .ToArray();

        var groupTotal = Round2(groupedLines.Sum(line => line.Amount));
        var groupDiscount = Round2(groupTotal - fixedPrice);
        if (groupDiscount <= 0m)
        {
            return;
        }

        // 叠加封顶（D3）：已封顶的行仍占着它的份额和分母，超出行原价的部分丢弃（见方法注释），
        // 既不把这一行从分母里剔除，也不把它的份额转给别的行——这与旧引擎（不传容量、最后统一截断到行原价）口径一致；
        // 若剔除/转移，同一购物车在新旧引擎里的逐行折扣与总折扣就会不同。
        // 容量只在组开始时取一次（每行在一个组里只出现一次，循环中不会变）。
        var candidates = groupedLines
            .Select(line => (Line: line, Capacity: getRemainingCapacity?.Invoke(line.Line) ?? decimal.MaxValue))
            .OrderBy(candidate => candidate.Line.SortOrder)
            .ToArray();

        // 逐行分摊：每行按「剩余折扣 × 本行金额 / 剩余行金额」取整，最后一行吃掉余数，保证未封顶时总折扣不变。
        // 分母随已分摊的行递减（而不是始终用整组原价），与 iPad 的 pricing-cart.ts 一致；
        // Amount 均已舍入到分，递减与逐轮重新求和结果相同，且避免 O(n²)。
        var remainingDiscount = groupDiscount;
        var remainingAmount = Round2(candidates.Sum(candidate => candidate.Line.Amount));
        for (var index = 0; index < candidates.Length && remainingDiscount > 0m; index++)
        {
            var (line, capacity) = candidates[index];
            if (remainingAmount <= 0m)
            {
                break;
            }

            var share = index == candidates.Length - 1
                ? remainingDiscount
                : Round2(remainingDiscount * line.Amount / remainingAmount);
            share = Math.Clamp(share, 0m, remainingDiscount);
            remainingDiscount -= share;

            // 入账 = 份额与剩余容量取小，超出容量的部分直接丢弃。
            var applied = Math.Min(share, Math.Max(0m, capacity));
            if (applied > 0m)
            {
                discountsByLine[line.Line] = discountsByLine.TryGetValue(line.Line, out var currentDiscount)
                    ? Round2(currentDiscount + applied)
                    : applied;
            }

            remainingAmount = Round2(remainingAmount - line.Amount);
        }
    }

    private static decimal Round2(decimal value)
    {
        return decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    private readonly record struct GroupedLine<TLine>(TLine Line, decimal Amount, long SortOrder)
        where TLine : notnull;
}
