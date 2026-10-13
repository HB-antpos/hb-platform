using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>
/// 自动促销「一组」折扣计算与分摊的共享实现。新引擎、旧引擎、共享挂单重演三处共用，
/// 所以这里直接钉死它的数值语义；三处各自的接入见 PromotionEngineParityMatrixTests。
/// </summary>
public sealed class PromotionBundleAllocatorTests
{
    private static PromotionBundleUnit<string> Unit(string line, decimal unitPrice, long sortOrder = 0)
    {
        return new PromotionBundleUnit<string>(line, unitPrice, sortOrder);
    }

    private static Dictionary<string, decimal> Allocate(
        IReadOnlyList<PromotionBundleUnit<string>> units,
        decimal fixedPrice,
        Func<string, decimal>? capacity = null,
        Dictionary<string, decimal>? existing = null)
    {
        var discounts = existing ?? new Dictionary<string, decimal>(StringComparer.Ordinal);
        PromotionBundleAllocator.AllocateBundle(units, fixedPrice, discounts, capacity);
        return discounts;
    }

    [Fact]
    public void Single_line_bundle_gets_the_whole_discount()
    {
        var discounts = Allocate([Unit("A", 10m, 0), Unit("A", 10m, 1)], 15m);

        Assert.Equal(5m, Assert.Single(discounts).Value);
    }

    [Fact]
    public void Cent_allocation_merges_units_per_line_and_uses_remaining_denominators()
    {
        // 3 件 2.90：A(1.00)×2 + B(1.00)×1，折扣 0.10。
        // 按行合并后：A 金额 2.00 -> round(0.10 × 2.00 / 3.00) = 0.07；B 吃余数 0.03。
        // （旧引擎原先按实物单位逐个分摊：A 0.03 + 0.03、B 余数 0.04，得到 0.06 / 0.04。）
        var discounts = Allocate([Unit("A", 1m, 0), Unit("A", 1m, 1), Unit("B", 1m, 0)], 2.90m);

        Assert.Equal(0.07m, discounts["A"]);
        Assert.Equal(0.03m, discounts["B"]);
    }

    [Fact]
    public void Three_single_unit_lines_use_decreasing_denominators()
    {
        // 0.10 分给三行各 1.00：第 1 行 round(0.10 × 1/3) = 0.03；第 2 行 round(0.07 × 1/2) = 0.04（0.035 进位）；第 3 行余 0.03。
        var discounts = Allocate([Unit("A", 1m), Unit("B", 1m), Unit("C", 1m)], 2.90m);

        Assert.Equal(0.03m, discounts["A"]);
        Assert.Equal(0.04m, discounts["B"]);
        Assert.Equal(0.03m, discounts["C"]);
    }

    [Fact]
    public void Weighted_units_of_the_same_physical_item_count_the_unit_price_once()
    {
        // A 权重 2：同一件实物展开成 2 个单位（SortOrder 相同），价格只计一次。
        // 组 = A(12.00) + B(6.00) = 18.00，凑 3 个单位 15.00 -> 折 3.00，按行金额分摊 2.00 / 1.00。
        var discounts = Allocate([Unit("A", 12m, 0), Unit("A", 12m, 0), Unit("B", 6m, 0)], 15m);

        Assert.Equal(2m, discounts["A"]);
        Assert.Equal(1m, discounts["B"]);
    }

    [Fact]
    public void Sub_cent_unit_prices_round_once_per_line_so_the_bundle_lands_on_the_fixed_price()
    {
        // 3.335 × 3 = 10.005 -> 行金额 10.01（与购物车行原价同口径），折 1.01，实收 10.01 - 1.01 = 9.00。
        // 若每个单位先舍入成 3.34 再求和 = 10.02，会折 1.02、实收 8.99。
        var discounts = Allocate([Unit("A", 3.335m, 0), Unit("A", 3.335m, 1), Unit("A", 3.335m, 2)], 9m);

        Assert.Equal(1.01m, Assert.Single(discounts).Value);
    }

    [Fact]
    public void Sub_cent_unit_prices_across_lines_round_each_line_once()
    {
        // 两行各 3.335 -> 行金额各 3.34，组 6.68，折 1.68，均分 0.84 / 0.84，整组实收 5.00。
        var discounts = Allocate([Unit("A", 3.335m), Unit("B", 3.335m)], 5m);

        Assert.Equal(0.84m, discounts["A"]);
        Assert.Equal(0.84m, discounts["B"]);
    }

    [Theory]
    [InlineData(10, 20)]
    [InlineData(10, 25)]
    public void Fixed_price_at_or_above_the_bundle_total_gives_no_discount(int unitPrice, int fixedPrice)
    {
        var discounts = Allocate([Unit("A", unitPrice, 0), Unit("A", unitPrice, 1)], fixedPrice);

        Assert.Empty(discounts);
    }

    [Fact]
    public void Without_a_capacity_function_nothing_is_capped_and_the_caller_truncates_afterwards()
    {
        // 旧引擎的用法：入账时不按行截断，累加后由调用方整体截断到行原价。
        var discounts = Allocate([Unit("A", 1m, 0), Unit("A", 1m, 1), Unit("B", 1m, 0)], 2.90m, capacity: null);

        Assert.Equal(0.07m, discounts["A"]);
        Assert.Equal(0.03m, discounts["B"]);
    }

    [Fact]
    public void With_a_capacity_function_the_share_beyond_capacity_is_dropped_and_not_moved_to_other_lines()
    {
        // 新引擎的用法（D3，用户拍板「超出的份额丢弃」）：A 只剩 0.02 容量 -> A 入账 0.02，
        // 多出来的 0.05 直接丢弃，B 仍只得自己的份额 0.03（不是 0.08）；本组实际折扣 0.05 小于「组原价 - 固定价」0.10。
        var discounts = Allocate(
            [Unit("A", 1m, 0), Unit("A", 1m, 1), Unit("B", 1m, 0)],
            2.90m,
            capacity: line => line == "A" ? 0.02m : 100m);

        Assert.Equal(0.02m, discounts["A"]);
        Assert.Equal(0.03m, discounts["B"]);
    }

    [Fact]
    public void A_line_without_capacity_keeps_its_share_and_denominator_but_receives_nothing()
    {
        var discounts = Allocate(
            [Unit("A", 1m), Unit("B", 1m)],
            1.50m,
            capacity: line => line == "B" ? 0m : 100m);

        // 组折扣按两行原价算（2.00 - 1.50 = 0.50），A/B 各占 0.25；B 没有容量 -> B 的 0.25 丢弃，不转给 A。
        Assert.Equal(0.25m, Assert.Single(discounts).Value);
        Assert.Equal("A", Assert.Single(discounts).Key);
    }

    [Fact]
    public void A_line_without_capacity_does_not_change_the_split_of_the_lines_after_it()
    {
        // 三行各 2.00 凑 3 件 3.00：折扣 3.00，每行 1.00。第一行已封顶也不影响后两行（原：把它剔除，后两行各 1.50）。
        var discounts = Allocate(
            [Unit("A", 2m), Unit("B", 2m), Unit("C", 2m)],
            3m,
            capacity: line => line == "A" ? 0m : 100m);

        Assert.False(discounts.ContainsKey("A"));
        Assert.Equal(1.00m, discounts["B"]);
        Assert.Equal(1.00m, discounts["C"]);
    }

    [Fact]
    public void Capacity_equals_accumulating_uncapped_and_truncating_to_the_line_amount_afterwards()
    {
        // 等价性（D3 三处一致的依据）：多个组、多条规则依次入账，「传容量（每组之后容量随累计折扣递减）」
        // 与「不传容量、全部累加完再整体截断到行原价」逐行结果完全相同。
        var random = new Random(20261017);
        decimal[] prices = [0.99m, 1.00m, 1.25m, 3.33m, 5m, 7.77m, 10m, 12.5m, 19.99m];
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            var lineCount = random.Next(1, 5);
            var linePrice = Enumerable.Range(0, lineCount).Select(_ => prices[random.Next(prices.Length)]).ToArray();
            var lineQuantity = Enumerable.Range(0, lineCount).Select(_ => random.Next(1, 4)).ToArray();
            var gross = Enumerable.Range(0, lineCount)
                .ToDictionary(line => $"L{line}", line => decimal.Round(linePrice[line] * lineQuantity[line], 2, MidpointRounding.AwayFromZero), StringComparer.Ordinal);

            var withCapacity = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var uncapped = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var bundleCount = random.Next(1, 6);
            for (var bundle = 0; bundle < bundleCount; bundle++)
            {
                var units = new List<PromotionBundleUnit<string>>();
                var unitCount = random.Next(1, 5);
                for (var unit = 0; unit < unitCount; unit++)
                {
                    var line = random.Next(lineCount);
                    units.Add(Unit($"L{line}", linePrice[line], units.Count(candidate => candidate.Line == $"L{line}")));
                }

                var fixedPrice = Math.Round(random.Next(0, 1500) / 100m, 2);
                PromotionBundleAllocator.AllocateBundle(
                    units,
                    fixedPrice,
                    withCapacity,
                    line => Math.Max(0m, gross[line] - (withCapacity.TryGetValue(line, out var current) ? current : 0m)));
                PromotionBundleAllocator.AllocateBundle(units, fixedPrice, uncapped, getRemainingCapacity: null);
            }

            var truncated = uncapped
                .Select(entry => (entry.Key, Value: Math.Min(entry.Value, gross[entry.Key])))
                .Where(entry => entry.Value > 0m)
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .ToArray();
            var capped = withCapacity
                .Where(entry => entry.Value > 0m)
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => (entry.Key, entry.Value))
                .ToArray();
            Assert.Equal(truncated, capped);
        }
    }

    [Fact]
    public void Discounts_accumulate_on_top_of_what_the_line_already_has()
    {
        var existing = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["A"] = 1.25m };

        var discounts = Allocate([Unit("A", 10m, 0), Unit("A", 10m, 1)], 15m, existing: existing);

        Assert.Equal(6.25m, discounts["A"]);
    }

    [Fact]
    public void Total_allocated_discount_always_equals_the_bundle_discount_when_uncapped()
    {
        // 不封顶时，逐行取整的误差全部由最后一行吸收，总折扣恒等于「组原价 - 固定价」。
        var random = new Random(20261016);
        decimal[] prices = [0.99m, 1.00m, 1.25m, 3.33m, 5m, 7.77m, 10m, 12.5m, 19.99m];
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            var units = new List<PromotionBundleUnit<string>>();
            var lineCount = random.Next(1, 5);
            var total = 0m;
            for (var line = 0; line < lineCount; line++)
            {
                var price = prices[random.Next(prices.Length)];
                var quantity = random.Next(1, 4);
                for (var index = 0; index < quantity; index++)
                {
                    units.Add(Unit($"L{line}", price, index));
                    total += price;
                }
            }

            var fixedPrice = Math.Round(random.Next(0, 2000) / 100m, 2);
            var discounts = Allocate(units, fixedPrice);

            var expected = Math.Max(0m, total - fixedPrice);
            Assert.Equal(expected, discounts.Values.Sum());
        }
    }
}
