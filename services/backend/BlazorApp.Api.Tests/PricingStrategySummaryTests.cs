using BlazorApp.Api.Services.Pricing;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using Xunit;

namespace BlazorApp.Api.Tests;

public class PricingStrategySummaryTests
{
    private static PricingStrategyDetail Rule(decimal a, decimal b, decimal ra, decimal rb, string algorithm = "Linear",
        decimal? startRetail = null, decimal? endRetail = null) => new()
        { MinPrice = a, MaxPrice = b, StartRate = ra, EndRate = rb, Algorithm = algorithm, StartRetailPrice = startRetail, EndRetailPrice = endRetail };

    private static PricingStrategyListDto Summarize(params PricingStrategyDetail[] rules)
    {
        var dto = new PricingStrategyListDto();
        PricingStrategySummary.Apply(dto, rules);
        return dto;
    }

    [Fact]
    public void 无区间的策略标记为空()
    {
        var dto = Summarize();
        Assert.Equal("Empty", dto.PricingMode);
        Assert.Empty(dto.Nodes);
    }

    [Fact]
    public void 固定成率只返回统一成率且没有节点()
    {
        var dto = Summarize(Rule(0.1m, 99999m, 2.5m, 2.5m, "Fixed"));
        Assert.Equal("Fixed", dto.PricingMode);
        Assert.Equal(2.5m, dto.FixedRate);
        Assert.Equal(2.5m, dto.StartRate);
        Assert.Equal(2.5m, dto.EndRate);
        Assert.Empty(dto.Nodes);
    }

    [Fact]
    public void 曲线策略返回节点并按成本排序且首尾成率取最低与最高区间()
    {
        // 故意乱序传入，摘要必须按进货价区间排序后再取首尾。
        var dto = Summarize(
            Rule(50m, 200m, 3.4998m, 3m, "Linear", 174.99m, 599.99m),
            Rule(1m, 50m, 4.5m, 3.4998m, "Linear", 4.5m, 174.99m));
        Assert.Equal("Curve", dto.PricingMode);
        Assert.Equal(4.5m, dto.StartRate);
        Assert.Equal(3m, dto.EndRate);
        Assert.Equal(new[] { 1m, 50m, 200m }, dto.Nodes.Select(n => n.Cost).ToArray());
        Assert.Equal(new[] { 4.5m, 174.99m, 599.99m }, dto.Nodes.Select(n => n.Price).ToArray());
    }

    [Fact]
    public void 含固定区间的多区间策略为分段并返回区间摘要()
    {
        var dto = Summarize(
            Rule(0.1m, 10m, 2.5m, 2.5m, "Fixed"),
            Rule(10m, 50m, 2.5m, 2m, "Linear", 25m, 100m),
            Rule(50m, 99999m, 2m, 2m, "Fixed"));
        Assert.Equal("Mixed", dto.PricingMode);
        Assert.Null(dto.FixedRate);
        Assert.Equal(new[] { "Fixed", "Curve", "Fixed" }, dto.Intervals.Select(i => i.Type).ToArray());
        Assert.Equal(new decimal?[] { 2.5m, null, 2m }, dto.Intervals.Select(i => i.Rate).ToArray());
        Assert.Equal(25m, dto.Intervals[1].StartPrice);
        Assert.Equal(100m, dto.Intervals[1].EndPrice);
        Assert.Equal(4, dto.Nodes.Count);   // 兼容旧前端的节点字段
    }

    [Fact]
    public void 多个固定区间也是分段而单个固定区间仍是固定成率()
    {
        Assert.Equal("Mixed", Summarize(Rule(0.1m, 10m, 3m, 3m, "Fixed"), Rule(10m, 50m, 2.5m, 2.5m, "Fixed")).PricingMode);
        var single = Summarize(Rule(0.1m, 99999m, 2.5m, 2.5m, "Fixed"));
        Assert.Equal("Fixed", single.PricingMode);
        Assert.Single(single.Intervals);
    }

    [Fact]
    public void 没有节点零售价或使用旧算法的策略视为旧版区间规则()
    {
        var legacy = Summarize(Rule(10m, 100m, 1.5m, 2.5m, "Linear"));
        Assert.Equal("Legacy", legacy.PricingMode);
        Assert.Equal(new[] { 15m, 250m }, legacy.Nodes.Select(n => n.Price).ToArray());

        Assert.Equal("Legacy", Summarize(Rule(10m, 100m, 2m, 2m, "Step")).PricingMode);
    }
}
