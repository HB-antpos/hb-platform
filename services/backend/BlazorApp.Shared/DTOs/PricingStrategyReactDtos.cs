namespace BlazorApp.Shared.DTOs
{
    public class PricingStrategyListDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Level { get; set; } = "Global";
        public int Priority { get; set; }
        public bool IsEnabled { get; set; }
        public List<string> StoreCodes { get; set; } = new();
        public List<string> SupplierCodes { get; set; } = new();
        public int DetailsCount { get; set; }
        public int TargetsCount { get; set; }

        /// <summary>定价方式：Fixed 固定成率 / Curve 曲线 / Legacy 旧版区间规则 / Empty 无区间。</summary>
        public string PricingMode { get; set; } = "Empty";
        /// <summary>仅 Fixed 有值：统一成率。</summary>
        public decimal? FixedRate { get; set; }
        /// <summary>最低进货价区间的起始成率，列表用于展示「成率 a → b」。</summary>
        public decimal? StartRate { get; set; }
        /// <summary>最高进货价区间的结束成率。</summary>
        public decimal? EndRate { get; set; }
        /// <summary>曲线/旧版规则的节点（成本 → 零售价），用于列表里的缩略图；Fixed 为空。</summary>
        public List<PricingStrategyNodeDto> Nodes { get; set; } = new();
    }

    public class PricingStrategyNodeDto
    {
        public decimal Cost { get; set; }
        public decimal Price { get; set; }
    }

    public class PricingStrategyRuleDto
    {
        public string? Id { get; set; }
        public decimal MinPrice { get; set; }
        public decimal MaxPrice { get; set; }
        public decimal StartRate { get; set; }
        public decimal EndRate { get; set; }
        public string? Algorithm { get; set; }
        public decimal? StartRetailPrice { get; set; }
        public decimal? EndRetailPrice { get; set; }
        public decimal? CurveBend { get; set; }
    }

    public class PricingStrategyTargetDto
    {
        public string? Id { get; set; }
        public string TargetType { get; set; } = "Global";
        public string? TargetCode { get; set; }
    }

    public class PricingStrategyDetailDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Level { get; set; } = "Global";
        public int Priority { get; set; }
        public bool IsEnabled { get; set; }
        public List<PricingStrategyRuleDto> Details { get; set; } = new();
        public List<PricingStrategyTargetDto> Targets { get; set; } = new();
    }

    public class CreatePricingStrategyDto
    {
        public string Name { get; set; } = string.Empty;
        public string Level { get; set; } = "Global";
        public int Priority { get; set; }
        public bool IsEnabled { get; set; } = true;
        public List<PricingStrategyRuleDto> Details { get; set; } = new();
        public List<PricingStrategyTargetDto> Targets { get; set; } = new();
    }

    public class UpdatePricingStrategyDto
    {
        public string Name { get; set; } = string.Empty;
        public string Level { get; set; } = "Global";
        public int Priority { get; set; }
        public bool IsEnabled { get; set; } = true;
        public List<PricingStrategyRuleDto> Details { get; set; } = new();
        public List<PricingStrategyTargetDto> Targets { get; set; } = new();
    }

    public class PricingEvaluateRequest
    {
        public decimal PurchasePrice { get; set; }
        public string? StoreCode { get; set; }
        public string? SupplierCode { get; set; }
    }

    public class PricingEvaluateResponse
    {
        public decimal RetailPrice { get; set; }
        public decimal Rate { get; set; }
        public decimal EffectiveRate { get; set; }
        public string? StrategyId { get; set; }
        public string? StrategyName { get; set; }
        public PricingEvaluateRuleInfo? Rule { get; set; }
    }

    public class PricingEvaluateRuleInfo
    {
        public decimal MinPrice { get; set; }
        public decimal MaxPrice { get; set; }
        public string? Algorithm { get; set; }
        public decimal? StartRetailPrice { get; set; }
        public decimal? EndRetailPrice { get; set; }
        public decimal? CurveBend { get; set; }
        public decimal StartRate { get; set; }
        public decimal EndRate { get; set; }
    }
}
