using BlazorApp.Shared.Models.HBweb;

namespace BlazorApp.Api.Services.Pricing;

/// <summary>定价曲线与尾数规则的共同约束。理论成率不升，最终尾数价格不降。</summary>
public static class PricingCurveMath
{
    public static bool IsCurve(string? algorithm) => algorithm?.ToLowerInvariant() is "linear" or "arcup" or "arcdown";
    /// <summary>固定成率：售价 = 进货价 × 成率，四舍五入到分，不做 .50/.99 尾数调整。</summary>
    public static bool IsFixed(string? algorithm) => string.Equals(algorithm, "Fixed", StringComparison.OrdinalIgnoreCase);
    public static decimal Start(PricingStrategyDetail r) => r.StartRetailPrice ?? r.MinPrice * r.StartRate;
    public static decimal End(PricingStrategyDetail r) => r.EndRetailPrice ?? r.MaxPrice * r.EndRate;

    public static decimal TheoreticalRetail(decimal cost, PricingStrategyDetail r)
    {
        var algorithm = r.Algorithm?.ToLowerInvariant();
        if (algorithm == "step" || algorithm == "fixed") return cost * r.StartRate;
        if (algorithm == "exponential")
        {
            if (r.StartRate <= 0 || r.EndRate <= 0 || r.MaxPrice <= r.MinPrice)
                throw new ArgumentException("指数规则的成本区间和成率必须为正");
            var t = (double)((cost - r.MinPrice) / (r.MaxPrice - r.MinPrice));
            return cost * (decimal)((double)r.StartRate * Math.Pow((double)(r.EndRate / r.StartRate), t));
        }
        if (!IsCurve(algorithm)) throw new ArgumentException("不支持的定价算法");
        var start = Start(r); var end = End(r);
        if (cost == r.MinPrice) return start;
        if (cost == r.MaxPrice) return end;
        var offset = cost - r.MinPrice; var width = r.MaxPrice - r.MinPrice;
        var delta = end - start;
        // 先乘后除，避免 2/3 等重复小数把精确 .50 推过分档边界。
        return start + delta * offset / width
            + delta * (r.CurveBend ?? 0m) * offset / width * (width - offset) / width;
    }

    public static void Validate(IEnumerable<PricingStrategyDetail>? source, bool allowLegacyZero = false)
    {
        var rules = source?.OrderBy(r => r.MinPrice).ToList();
        if (rules == null || rules.Count == 0) throw new ArgumentException("请至少设置一个定价区间");
        PricingStrategyDetail? previous = null;
        foreach (var r in rules)
        {
            if (decimal.Round(r.MinPrice, 4) != r.MinPrice || decimal.Round(r.MaxPrice, 4) != r.MaxPrice)
                throw new ArgumentException("成本节点最多支持四位小数");
            var legacyZero = allowLegacyZero && previous == null && r.MinPrice == 0 && !r.StartRetailPrice.HasValue;
            if ((!legacyZero && r.MinPrice < 0.1m) || r.MaxPrice <= r.MinPrice)
                throw new ArgumentException("成本区间必须从 0.10 或以上开始，且结束成本大于开始成本");
            if (r.StartRate < 1.5m || r.StartRate > 5m || r.EndRate < 1.5m || r.EndRate > 5m)
                throw new ArgumentException("成率必须在 1.5～5 之间");
            var a = r.MinPrice; var b = r.MaxPrice; var h = b - a;
            var pa = Start(r); var pb = End(r); var d = pb - pa;
            if (pa < 1.5m * a || pa > 5m * a || pb < 1.5m * b || pb > 5m * b)
                throw new ArgumentException("节点零售价对应成率必须在 1.5～5 之间");
            if (d < 0 || pa * b < pb * a)
                throw new ArgumentException("成本增加时节点零售价不得下降，理论成率不得增加");
            if ((r.StartRetailPrice.HasValue && !IsLegalTail(pa)) || (r.EndRetailPrice.HasValue && !IsLegalTail(pb)))
                throw new ArgumentException("指定的节点零售价必须使用 .50/.99 尾数或整数 1、2");
            if (previous != null && (previous.MaxPrice != a || End(previous) != pa))
                throw new ArgumentException("相邻成本区间必须连续，且交界节点零售价必须相同");
            var bend = r.CurveBend ?? 0m;
            if (IsCurve(r.Algorithm))
            {
                if (bend < -1 || bend > 1 || (r.Algorithm.Equals("Linear", StringComparison.OrdinalIgnoreCase) && bend != 0)
                    || (r.Algorithm.Equals("ArcUp", StringComparison.OrdinalIgnoreCase) && bend < 0)
                    || (r.Algorithm.Equals("ArcDown", StringComparison.OrdinalIgnoreCase) && bend > 0))
                    throw new ArgumentException("曲率必须在 -1～1 且与所选弧线方向一致");
                var bulge = d * bend;
                // 两端斜率控制价格不降；xP'-P 对二次曲线单调，因此只需验证两端。
                if (d + bulge < 0 || d - bulge < 0 || a * (d + bulge) > h * pa || b * (d - bulge) > h * pb)
                    throw new ArgumentException("弧度过大，会导致零售价下降或成率上升，请减小弧度");
            }
            else if (r.Algorithm?.ToLowerInvariant() is "step" or "exponential")
            {
                if (bend != 0 || r.StartRetailPrice.HasValue || r.EndRetailPrice.HasValue)
                    throw new ArgumentException("旧阶梯或指数算法不能指定曲线节点零售价或弧度，请先转换为直线");
                if (r.Algorithm.Equals("Step", StringComparison.OrdinalIgnoreCase) && r.StartRate != r.EndRate)
                    throw new ArgumentException("阶梯区间的开始和结束成率必须相同");
                if (r.Algorithm.Equals("Exponential", StringComparison.OrdinalIgnoreCase)
                    && 1 + (double)(b / h) * Math.Log((double)(r.EndRate / r.StartRate)) < 0)
                    throw new ArgumentException("指数规则会导致零售价倒挂，请改用直线");
            }
            else if (IsFixed(r.Algorithm))
            {
                // 固定成率不参与曲线连续性约束，只允许单个区间覆盖全部成本，避免与其他算法在交界处价格跳变。
                if (rules.Count != 1)
                    throw new ArgumentException("固定成率策略只能包含一个进货价区间");
                if (r.StartRate != r.EndRate)
                    throw new ArgumentException("固定成率的开始和结束成率必须相同");
                if (bend != 0 || r.StartRetailPrice.HasValue || r.EndRetailPrice.HasValue)
                    throw new ArgumentException("固定成率不能指定曲线节点零售价或弧度");
            }
            else throw new ArgumentException("不支持的定价算法");
            previous = r;
        }
    }

    public static bool IsLegalTail(decimal value) => value >= 0.5m &&
        (value is 1m or 2m || value - decimal.Floor(value) is 0.5m or 0.99m);

    private static decimal TailBound(decimal value, bool ceiling)
    {
        var n = decimal.Floor(value);
        var candidates = new[] { n - 0.5m, n - 0.01m, n + 0.5m, n + 0.99m, n + 1.5m, 1m, 2m };
        var valid = candidates.Where(IsLegalTail).Where(p => ceiling ? p >= value : p <= value);
        return ceiling ? valid.DefaultIfEmpty(decimal.MaxValue).Min() : valid.DefaultIfEmpty(decimal.MinValue).Max();
    }

    public static decimal AdjustTail(decimal cost, decimal theoretical)
    {
        if (cost < 0.1m) throw new ArgumentException("进货价必须至少为 0.10，才能满足尾数及 1.5～5 成率限制");
        var lower = TailBound(1.5m * cost, true);
        var upper = TailBound(5m * cost, false);
        if (lower > upper) throw new ArgumentException("当前成本不存在满足 1.5～5 成率的合法尾数价格");
        var integer = decimal.Floor(theoretical);
        // 与浏览器预览使用同一极窄边界容差，消除浮点/decimal 重复除法误差。
        foreach (var boundary in new[] { integer, integer + 0.5m, integer + 0.99m, integer + 1m })
            if (Math.Abs(theoretical - boundary) < 0.0000000001m) { theoretical = boundary; break; }
        var n = decimal.Floor(theoretical);
        var fraction = theoretical - n;
        // 合法节点保持原值；其它数值沿用原来的 .50/.99 分档，确保整个映射单调。
        var candidate = IsLegalTail(theoretical) ? theoretical : theoretical <= 0.5m ? 0.5m
            : fraction == 0m ? n - 0.01m : fraction <= 0.5m ? n + 0.5m : n + 0.99m;
        var retail = Math.Min(upper, Math.Max(lower, candidate));
        // 先保留原有分档和成率限幅，再将两个小额尾数归整；仅这两档允许比上限多一分钱。
        return retail switch { 0.99m => 1m, 1.99m => 2m, _ => retail };
    }
}
