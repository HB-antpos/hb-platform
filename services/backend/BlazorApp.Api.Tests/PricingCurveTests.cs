using BlazorApp.Api.Services.Pricing;
using BlazorApp.Shared.Models.HBweb;
using Xunit;

namespace BlazorApp.Api.Tests;

public class PricingCurveTests
{
    private readonly AutoPricingService _service = new(null!);
    private static PricingStrategyDetail Rule(decimal a, decimal b, decimal ra, decimal rb,
        string algorithm = "Linear", decimal bend = 0) => new()
        { MinPrice = a, MaxPrice = b, StartRate = ra, EndRate = rb, Algorithm = algorithm, CurveBend = bend };
    private static PricingStrategy Strategy(params PricingStrategyDetail[] rules) => new() { Details = rules.ToList() };

    [Fact]
    public void 示例全区间99801点_尾数合法成率有界售价不降()
    {
        var strategy = Strategy(Rule(1, 50, 4, 3.5m), Rule(50, 200, 3.5m, 3), Rule(200, 999, 3, 2));
        var previous = 0m; var previousRate = 5m;
        for (var cents = 100; cents <= 99900; cents++)
        {
            var cost = cents / 100m;
            var retail = _service.CalculateRetailPrice(cost, strategy);
            var rate = _service.CalculateRate(cost, strategy);
            Assert.True(retail >= previous, $"成本{cost}零售价倒挂");
            Assert.InRange(retail / cost, 1.5m, 5m);
            Assert.True(PricingCurveMath.IsLegalTail(retail));
            Assert.True(rate <= previousRate);
            previous = retail; previousRate = rate;
        }
        Assert.Equal(316.67m, Math.Round(PricingCurveMath.TheoreticalRetail(100, strategy.Details[1]), 2));
    }

    [Fact]
    public void 原反例十元四倍二十元二点一倍_中段不再先涨后降()
    {
        var strategy = Strategy(Rule(10, 20, 4, 2.1m));
        Assert.Equal(39.99m, _service.CalculateRetailPrice(10, strategy));
        Assert.Equal(40.99m, _service.CalculateRetailPrice(15, strategy));
        Assert.Equal(41.99m, _service.CalculateRetailPrice(20, strategy));
    }

    [Theory]
    [InlineData("ArcUp", 0.1)]
    [InlineData("ArcDown", -0.1)]
    public void 合法弧线全段满足约束(string algorithm, double bend)
    {
        var rule = Rule(10, 20, 4, 2.5m, algorithm, (decimal)bend);
        PricingCurveMath.Validate(new[] { rule });
        var previous = 0m; var previousRate = 5m;
        for (var cents = 1000; cents <= 2000; cents++)
        {
            var x = cents / 100m; var p = _service.CalculateRetailPrice(x, Strategy(rule));
            var rate = _service.CalculateRate(x, Strategy(rule));
            Assert.True(p >= previous); Assert.True(rate <= previousRate);
            previous = p; previousRate = rate;
        }
    }

    [Fact]
    public void 旧零成本起点可在有效成本计算但新保存拒绝零起点()
    {
        var rule = Rule(0, 10, 4, 3);
        Assert.Equal(3m, _service.CalculateRate(1, Strategy(rule)));
        Assert.Equal(2.99m, _service.CalculateRetailPrice(1, Strategy(rule)));
        Assert.Throws<ArgumentException>(() => PricingCurveMath.Validate(new[] { rule }));
    }

    [Fact]
    public void 过大弧度和不连续节点拒绝()
    {
        Assert.Throws<ArgumentException>(() => PricingCurveMath.Validate(new[] { Rule(10, 20, 3, 3, "ArcUp", .1m) }));
        Assert.Throws<ArgumentException>(() => PricingCurveMath.Validate(new[] { Rule(10, 20, 4, 3), Rule(21, 30, 3, 2) }));
        Assert.Throws<ArgumentException>(() => _service.CalculateRetailPrice(15, Strategy(Rule(10, 20, 2, 3))));
    }

    [Fact]
    public void 重复小数不把精确尾数推入下一档且节点端点直返()
    {
        var rule = Rule(.5m, 2m, 3m, 2.25m);
        rule.StartRetailPrice = 1.5m; rule.EndRetailPrice = 4.5m;
        Assert.Equal(3.5m, PricingCurveMath.TheoreticalRetail(1.5m, rule));
        Assert.Equal(3.5m, _service.CalculateRetailPrice(1.5m, Strategy(rule)));
        Assert.Equal(3.5m, PricingCurveMath.AdjustTail(1.5m, 3.5000000000000000000000000001m));
        Assert.Equal(3.99m, PricingCurveMath.AdjustTail(1.5m, 3.50000001m));
        Assert.Equal(1.5m, PricingCurveMath.TheoreticalRetail(.5m, rule));
        Assert.Equal(4.5m, PricingCurveMath.TheoreticalRetail(2m, rule));
    }

    [Fact]
    public void 显式节点和其他合法尾数保持原值()
    {
        foreach (var price in new[] { .5m, 1m, 1.5m, 2m, 2.5m, 2.99m, 49.99m })
            Assert.Equal(price, PricingCurveMath.AdjustTail(price / 2m, price));
        var rule = Rule(10, 20, 4, 2.5m); rule.StartRetailPrice = 39.99m; rule.EndRetailPrice = 49.99m;
        Assert.Equal(39.99m, _service.CalculateRetailPrice(10, Strategy(rule)));
        Assert.Equal(49.99m, _service.CalculateRetailPrice(20, Strategy(rule)));
    }

    [Theory]
    [InlineData(.3, .99, 1)]
    [InlineData(.7, 1.99, 2)]
    [InlineData(.3, .75, 1)]
    [InlineData(.7, 1.75, 2)]
    [InlineData(.198, 1.5, 1)]
    [InlineData(.398, 2.5, 2)]
    [InlineData(.6, .5, 1)]
    [InlineData(1.2, .5, 2)]
    public void 小额尾数在分档和上下限处理后归整(double cost, double theoretical, double expected)
    {
        Assert.Equal((decimal)expected, PricingCurveMath.AdjustTail((decimal)cost, (decimal)theoretical));
    }

    [Fact]
    public void 自动定价仅将零点九九和一点九九归整()
    {
        Assert.Equal(1m, _service.CalculateRetailPrice(.3m, null));
        Assert.Equal(2m, _service.CalculateRetailPrice(.7m, null));
        Assert.Equal(1m, _service.CalculateRetailPrice(.4m, null));
        Assert.Equal(2m, _service.CalculateRetailPrice(.8m, null));
        Assert.Equal(2.99m, _service.CalculateRetailPrice(1.1m, null));
        Assert.Equal(4.99m, _service.CalculateRetailPrice(2m, null));

        // 兼容已保存的小额曲线节点，归整只作用于最终自动价。
        var rule = Rule(.3m, .7m, 3.3m, 1.99m / .7m);
        rule.StartRetailPrice = .99m;
        rule.EndRetailPrice = 1.99m;
        PricingCurveMath.Validate(new[] { rule });
        Assert.Equal(.99m, PricingCurveMath.TheoreticalRetail(.3m, rule));
        Assert.Equal(1.99m, PricingCurveMath.TheoreticalRetail(.7m, rule));
        Assert.Equal(1m, _service.CalculateRetailPrice(.3m, Strategy(rule)));
        Assert.Equal(2m, _service.CalculateRetailPrice(.7m, Strategy(rule)));
    }

    [Theory]
    [InlineData("10.00", "25.00")]
    [InlineData("10.10", "25.25")]
    [InlineData("10.30", "25.75")]
    [InlineData("3.33", "8.33")]   // 8.325 按四舍五入进位到分
    [InlineData("0.10", "0.25")]   // 低价也不被吸附到 .50
    public void 固定成率直接取进货价乘成率且不做尾数调整(string cost, string expected)
    {
        var strategy = Strategy(Rule(0.1m, 99999m, 2.5m, 2.5m, "Fixed"));
        PricingCurveMath.Validate(strategy.Details);

        var price = _service.CalculateRetailPrice(decimal.Parse(cost), strategy);

        Assert.Equal(decimal.Parse(expected), price);
        // 展示用的上浮率保持精确的 2.5，不受分位取整影响。
        Assert.Equal(2.5m, _service.CalculateRate(decimal.Parse(cost), strategy));
    }

    [Fact]
    public void 固定成率要求起止成率相同并保持在一点五到五之间且不带节点价和弧度()
    {
        Assert.Throws<ArgumentException>(() => PricingCurveMath.Validate(new[] { Rule(0.1m, 50m, 2.5m, 3m, "Fixed") }));
        Assert.Throws<ArgumentException>(() => PricingCurveMath.Validate(new[] { Rule(0.1m, 50m, 1.4m, 1.4m, "Fixed") }));
        Assert.Throws<ArgumentException>(() => PricingCurveMath.Validate(new[] { Rule(0.1m, 50m, 5.1m, 5.1m, "Fixed") }));
        Assert.Throws<ArgumentException>(() => PricingCurveMath.Validate(new[] { Rule(0.1m, 50m, 2.5m, 2.5m, "Fixed", .1m) }));
        var withNode = Rule(0.1m, 50m, 2.5m, 2.5m, "Fixed");
        withNode.StartRetailPrice = .5m;
        Assert.Throws<ArgumentException>(() => PricingCurveMath.Validate(new[] { withNode }));
    }

    private static PricingStrategyDetail CurveRule(decimal a, decimal b, decimal pa, decimal pb) => new()
    {
        MinPrice = a, MaxPrice = b, Algorithm = "Linear", CurveBend = 0,
        StartRetailPrice = pa, EndRetailPrice = pb,
        StartRate = decimal.Round(pa / a, 4), EndRate = decimal.Round(pb / b, 4),
    };

    [Fact]
    public void 固定与曲线可以按区间混合且曲线端点跟随固定交界价时不要求合法尾数()
    {
        // ≤10 固定 2.5×，10～50 曲线（起点 25.00 = 10×2.5，终点 100.00 = 50×2.0），>50 固定 2.0×。
        var rules = new[] { Rule(0.1m, 10m, 2.5m, 2.5m, "Fixed"), CurveRule(10m, 50m, 25.00m, 100.00m), Rule(50m, 99999m, 2m, 2m, "Fixed") };
        PricingCurveMath.Validate(rules);

        var strategy = Strategy(rules);
        Assert.Equal(25.00m, _service.CalculateRetailPrice(10m, strategy));      // 交界值属于上一个（固定）区间
        Assert.Equal(25.03m, _service.CalculateRetailPrice(10.01m, Strategy(Rule(0.1m, 10m, 2.5m, 2.5m, "Fixed"), Rule(10m, 50m, 2.5m, 2.5m, "Fixed"))));
        // 50 属于曲线区间的上限；曲线区间的实际价仍做尾数调整，理论 100.00 → 99.99，与相邻固定区间相差一分。
        Assert.Equal(99.99m, _service.CalculateRetailPrice(50m, strategy));
        Assert.Equal(100.02m, _service.CalculateRetailPrice(50.01m, strategy));   // 50.01 起按固定 2.0×
    }

    [Fact]
    public void 曲线端点不跟随相邻固定区间时仍必须使用合法尾数()
    {
        // 25.00 不是合法尾数；左侧是曲线（非固定）时不能豁免。
        Assert.Throws<ArgumentException>(() => PricingCurveMath.Validate(new[] { CurveRule(1m, 10m, 2.5m, 24.99m), CurveRule(10m, 50m, 25.00m, 100.00m) }));
        // 左侧是固定区间但价格对不上（固定算出 25.00，曲线起点取 26.00）也不能豁免。
        Assert.Throws<ArgumentException>(() => PricingCurveMath.Validate(new[] { Rule(0.1m, 10m, 2.5m, 2.5m, "Fixed"), CurveRule(10m, 50m, 26.00m, 100.00m) }));
    }

    [Fact]
    public void 区间交界允许售价回落以支持阶梯定价但必须首尾相接()
    {
        // 3× 与 2.5× 相邻：10 处 30.00 → 25.03，成本变大售价反而降低，属于有意的阶梯。
        var tiers = new[] { Rule(0.1m, 10m, 3m, 3m, "Fixed"), Rule(10m, 50m, 2.5m, 2.5m, "Fixed") };
        PricingCurveMath.Validate(tiers);
        Assert.Equal(30.00m, _service.CalculateRetailPrice(10m, Strategy(tiers)));
        Assert.Equal(25.03m, _service.CalculateRetailPrice(10.01m, Strategy(tiers)));

        // 空隙与重叠都不允许。
        Assert.Throws<ArgumentException>(() => PricingCurveMath.Validate(new[] { Rule(0.1m, 10m, 3m, 3m, "Fixed"), Rule(11m, 50m, 2.5m, 2.5m, "Fixed") }));
        Assert.Throws<ArgumentException>(() => PricingCurveMath.Validate(new[] { Rule(0.1m, 10m, 3m, 3m, "Fixed"), Rule(9m, 50m, 2.5m, 2.5m, "Fixed") }));
    }

    [Fact]
    public void 区间内部仍要求售价不降成率不升()
    {
        // 曲线区间内起点 100.00、终点 90.00：成本增加售价下降。
        Assert.Throws<ArgumentException>(() => PricingCurveMath.Validate(new[] { CurveRule(10m, 50m, 24.99m, 24.50m) }));
    }

    [Fact]
    public void 最多十个区间且运行时计价不受该限制()
    {
        PricingStrategyDetail[] Build(int count) => Enumerable.Range(0, count)
            .Select(i => Rule(0.1m + i * 10m, 10.1m + i * 10m, 2.5m, 2.5m, "Fixed")).ToArray();
        PricingCurveMath.Validate(Build(10));
        var ex = Assert.Throws<ArgumentException>(() => PricingCurveMath.Validate(Build(11)));
        Assert.Contains("10", ex.Message);
        // 历史数据超过 10 个区间时，运行时校验（enforceIntervalLimit:false）不应抛出。
        PricingCurveMath.Validate(Build(11), enforceIntervalLimit: false);
    }

    [Fact]
    public void 固定成率区间外的进货价明确失败且其他算法仍走尾数调整()
    {
        var fixedStrategy = Strategy(Rule(0.1m, 100m, 2.5m, 2.5m, "Fixed"));
        Assert.Throws<ArgumentException>(() => _service.CalculateRetailPrice(100.01m, fixedStrategy));
        Assert.Throws<ArgumentException>(() => _service.CalculateRetailPrice(.09m, fixedStrategy));

        // 已有的 Step 算法行为不变：10.00 × 2.5 = 25.00 仍被吸附为 24.99。
        Assert.Equal(24.99m, _service.CalculateRetailPrice(10m, Strategy(Rule(0.1m, 100m, 2.5m, 2.5m, "Step"))));
    }

    [Fact]
    public void 成率上下限只使用合法尾数且极低成本明确失败()
    {
        Assert.Throws<ArgumentException>(() => _service.CalculateRetailPrice(20.01m, Strategy(Rule(10, 20, 4, 3))));
        Assert.Throws<ArgumentException>(() => _service.CalculateRetailPrice(.09m, null));
        Assert.Equal(.5m, _service.CalculateRetailPrice(.1m, null));
        for (var cents = 10; cents <= 10000; cents++)
        {
            var cost = cents / 100m;
            foreach (var rate in new[] { 1.5m, 5m })
            {
                var p = PricingCurveMath.AdjustTail(cost, cost * rate);
                Assert.True(p >= 1.5m * cost);
                // 只允许 0.99→1、1.99→2 的一分钱归整突破原上限，其他价格严格保持。
                Assert.True(p <= 5m * cost || ((p is 1m or 2m) && p - .01m <= 5m * cost));
                Assert.True(PricingCurveMath.IsLegalTail(p));
            }
        }
    }
}
