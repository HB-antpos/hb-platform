using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;

namespace BlazorApp.Api.Services.Pricing;

/// <summary>把策略明细压缩成列表展示用的摘要：定价方式、成率区间与缩略图节点。</summary>
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

        // 固定成率只允许单个区间（见 PricingCurveMath.Validate），因此只看唯一一条规则。
        if (rules.Count == 1 && PricingCurveMath.IsFixed(rules[0].Algorithm))
        {
            dto.PricingMode = "Fixed";
            dto.FixedRate = rules[0].StartRate;
            return;
        }

        // 与前端判定一致：每个区间都有节点零售价且为直线/弧线才算曲线，其余视为旧版区间规则。
        var isCurve = rules.All(r =>
            PricingCurveMath.IsCurve(r.Algorithm) && r.StartRetailPrice.HasValue && r.EndRetailPrice.HasValue);
        dto.PricingMode = isCurve ? "Curve" : "Legacy";

        dto.Nodes = new List<PricingStrategyNodeDto>
        {
            new() { Cost = rules[0].MinPrice, Price = PricingCurveMath.Start(rules[0]) },
        };
        dto.Nodes.AddRange(rules.Select(r => new PricingStrategyNodeDto { Cost = r.MaxPrice, Price = PricingCurveMath.End(r) }));
    }
}
