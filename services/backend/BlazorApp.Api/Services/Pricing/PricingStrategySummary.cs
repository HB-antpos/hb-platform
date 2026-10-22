using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;

namespace BlazorApp.Api.Services.Pricing;

/// <summary>把策略明细压缩成列表展示用的摘要：定价方式、成率区间、区间与缩略图节点。</summary>
public static class PricingStrategySummary
{
    public static void Apply(PricingStrategyListDto dto, IEnumerable<PricingStrategyDetail> source)
    {
        var rules = source.OrderBy(r => r.MinPrice).ToList();
        if (rules.Count == 0)
        {
            dto.PricingMode = "Empty";
            return;
        }

        dto.StartRate = rules[0].StartRate;
        dto.EndRate = rules[^1].EndRate;
        dto.Intervals = rules.Select(r => new PricingStrategyIntervalDto
        {
            MinPrice = r.MinPrice,
            MaxPrice = r.MaxPrice,
            Type = IntervalType(r),
            Rate = PricingCurveMath.IsFixed(r.Algorithm) ? r.StartRate : null,
            StartPrice = PricingCurveMath.Start(r),
            EndPrice = PricingCurveMath.End(r),
        }).ToList();

        // 单个固定区间：只返回统一成率，不需要缩略图节点。
        if (rules.Count == 1 && PricingCurveMath.IsFixed(rules[0].Algorithm))
        {
            dto.PricingMode = "Fixed";
            dto.FixedRate = rules[0].StartRate;
            return;
        }

        // 含固定区间的多区间策略为「分段」；其余与前端一致：每个区间都有节点零售价且为直线/弧线才算曲线，否则视为旧版区间规则。
        var hasFixed = rules.Any(r => PricingCurveMath.IsFixed(r.Algorithm));
        var isCurve = rules.All(r => IntervalType(r) == "Curve");
        dto.PricingMode = hasFixed ? "Mixed" : isCurve ? "Curve" : "Legacy";

        // 节点沿用旧字段，兼容尚未升级的前端。
        dto.Nodes = new List<PricingStrategyNodeDto>
        {
            new() { Cost = rules[0].MinPrice, Price = PricingCurveMath.Start(rules[0]) },
        };
        dto.Nodes.AddRange(rules.Select(r => new PricingStrategyNodeDto { Cost = r.MaxPrice, Price = PricingCurveMath.End(r) }));
    }

    private static string IntervalType(PricingStrategyDetail r)
    {
        if (PricingCurveMath.IsFixed(r.Algorithm)) return "Fixed";
        return PricingCurveMath.IsCurve(r.Algorithm) && r.StartRetailPrice.HasValue && r.EndRetailPrice.HasValue ? "Curve" : "Legacy";
    }
}
