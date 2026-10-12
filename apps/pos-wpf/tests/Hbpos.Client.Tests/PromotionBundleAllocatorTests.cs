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
        // 旧引擎的用法：不按行封顶（D3 暂不统一），累加后由调用方整体截断到行原价。
        var discounts = Allocate([Unit("A", 1m, 0), Unit("A", 1m, 1), Unit("B", 1m, 0)], 2.90m, capacity: null);

        Assert.Equal(0.07m, discounts["A"]);
        Assert.Equal(0.03m, discounts["B"]);
    }

    [Fact]
    public void With_a_capacity_function_the_capped_share_moves_to_lines_that_still_have_room()
    {
        // 新引擎的用法（D3）：A 只剩 0.02 容量 -> A 封顶 0.02，多出来的份额由后面还有容量的 B 接走（0.08）。
        var discounts = Allocate(
            [Unit("A", 1m, 0), Unit("A", 1m, 1), Unit("B", 1m, 0)],
            2.90m,
            capacity: line => line == "A" ? 0.02m : 100m);

        Assert.Equal(0.02m, discounts["A"]);
        Assert.Equal(0.08m, discounts["B"]);
    }

    [Fact]
    public void A_line_without_capacity_is_left_out_of_the_allocation()
    {
        var discounts = Allocate(
            [Unit("A", 1m), Unit("B", 1m)],
            1.50m,
            capacity: line => line == "B" ? 0m : 100m);

        // 组折扣仍按两行原价算（2.00 - 1.50 = 0.50），B 没有容量 -> 全部落在 A。
        Assert.Equal(0.50m, Assert.Single(discounts).Value);
        Assert.Equal("A", Assert.Single(discounts).Key);
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
