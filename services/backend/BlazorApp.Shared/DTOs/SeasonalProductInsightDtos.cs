namespace BlazorApp.Shared.DTOs;

/// <summary>季节商品查询的一组日期区间（YYYY-MM-DD，含首尾）。</summary>
public sealed class SeasonalInsightRangesDto
{
    public ProductInsightRangeDto Inbound { get; set; } = new();
    public ProductInsightRangeDto Sales { get; set; } = new();
}

public sealed class SeasonalInsightProductDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public string? ProductImage { get; set; }
    /// <summary>warehouse：进货取仓库出库送货；local：进货取分店本地进货单。</summary>
    public string SourceType { get; set; } = "local";
}

/// <summary>GET /api/react/v1/seasonal-product-insights/lookup 的候选商品。</summary>
public sealed class SeasonalInsightCandidateDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public string? ProductImage { get; set; }
    /// <summary>当前分店在请求区间内的理论存货（累计进货 − 累计销量）。</summary>
    public decimal TheoreticalStock { get; set; }

    /// <summary>进货区间内当前分店的累计进货（本地进货单或仓库出库配货），与理论存货同口径。</summary>
    public decimal InboundQuantity { get; set; }

    /// <summary>销售区间内当前分店的累计销量。</summary>
    public decimal SalesQuantity { get; set; }

    /// <summary>进货区间内最早一笔进货的日期，没有进货时为空；前端据此判断是否「到货满 28 天仍没卖动」。</summary>
    public DateTime? FirstInboundDate { get; set; }
}

public sealed class SeasonalInsightLookupDto
{
    /// <summary>barcode：条码精确命中；itemNumber：货号包含匹配。</summary>
    public string MatchMode { get; set; } = "itemNumber";
    public bool Truncated { get; set; }
    public SeasonalInsightRangesDto Ranges { get; set; } = new();
    public List<SeasonalInsightCandidateDto> Items { get; set; } = [];
}

public sealed class SeasonalInsightInboundRecordDto
{
    public string Id { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string DocumentNo { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
}

public sealed class SeasonalInsightInboundDto
{
    public decimal Quantity { get; set; }
    public int DocumentCount { get; set; }
    public List<SeasonalInsightInboundRecordDto> Records { get; set; } = [];
}

public sealed class SeasonalInsightSalesDto
{
    public decimal Quantity { get; set; }
    public decimal Amount { get; set; }
    /// <summary>只含有销售记录的日期，按日期升序；无记录的日期不补 0。</summary>
    public List<ProductInsightDailySalesDto> Daily { get; set; } = [];
}

public sealed class SeasonalInsightBranchDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public decimal InboundQuantity { get; set; }
    public decimal SalesQuantity { get; set; }
    public decimal TheoreticalStock { get; set; }
}

/// <summary>GET /api/react/v1/seasonal-product-insights/store 的数据载荷。</summary>
public sealed class SeasonalProductInsightDto
{
    public DateTime GeneratedAt { get; set; }
    public ProductInsightStoreDto Store { get; set; } = new();
    public SeasonalInsightProductDto Product { get; set; } = new();
    public SeasonalInsightRangesDto Ranges { get; set; } = new();
    public SeasonalInsightInboundDto Inbound { get; set; } = new();
    public SeasonalInsightSalesDto Sales { get; set; } = new();
    /// <summary>当前分店理论存货 = 进货区间累计进货 − 销售区间累计销量。</summary>
    public decimal TheoreticalStock { get; set; }
    /// <summary>进货区间内有进货记录的其他分店，同口径计算理论存货。</summary>
    public List<SeasonalInsightBranchDto> Branches { get; set; } = [];
}
