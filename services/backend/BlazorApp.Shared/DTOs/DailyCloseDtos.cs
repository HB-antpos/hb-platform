namespace BlazorApp.Shared.DTOs;

/// <summary>
/// 日结记录列表查询参数（GET api/react/v1/pos-daily-closes）。
/// 日期为 yyyy-MM-dd；storeCodes 为逗号分隔；缺省日期取最近 7 天，区间最长 93 天。
/// </summary>
public sealed class DailyCloseQueryDto
{
    public string? BusinessDateFrom { get; set; }
    public string? BusinessDateTo { get; set; }

    /// <summary>逗号分隔的分店编号；与当前账号可见分店取交集。</summary>
    public string? StoreCodes { get; set; }

    public string? DeviceCode { get; set; }

    /// <summary>Wpf | Handheld | Ipad。</summary>
    public string? ClientKind { get; set; }

    /// <summary>收银员姓名或编号包含。</summary>
    public string? Keyword { get; set; }

    /// <summary>all | short | over | even | none，默认 all。</summary>
    public string? Status { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>各现金差异状态的记录数；不受 status 参数影响，其余筛选条件生效。</summary>
public sealed class DailyCloseCountsDto
{
    public int All { get; set; }
    public int Short { get; set; }
    public int Over { get; set; }
    public int Even { get; set; }
    public int None { get; set; }
}

/// <summary>当前筛选下（含 status）现金差异非空的记录的金额合计。</summary>
public sealed class DailyCloseTotalsDto
{
    public decimal ExpectedCash { get; set; }
    public decimal CountedCash { get; set; }
    public decimal Difference { get; set; }
}

public sealed class DailyCloseListResultDto
{
    public List<DailyCloseListItemDto> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public DailyCloseCountsDto Counts { get; set; } = new();
    public DailyCloseTotalsDto Totals { get; set; } = new();
}

public class DailyCloseListItemDto
{
    public Guid DailyCloseGuid { get; set; }
    public string StoreCode { get; set; } = string.Empty;
    public string? StoreName { get; set; }
    public string? StoreTimeZoneId { get; set; }
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>Wpf | Handheld | Ipad。</summary>
    public string ClientKind { get; set; } = string.Empty;

    /// <summary>Full（客户端完整上传）| CashOnly（回填且有现金三项）| TraceOnly（回填且无金额）。</summary>
    public string DetailLevel { get; set; } = string.Empty;

    /// <summary>ClientUpload | AuditBackfill。</summary>
    public string DataSource { get; set; } = string.Empty;

    /// <summary>序列化为 yyyy-MM-dd。</summary>
    public DateOnly BusinessDate { get; set; }

    public bool BusinessDateInferred { get; set; }
    public string CashierId { get; set; } = string.Empty;
    public string CashierName { get; set; } = string.Empty;
    public DateTime SavedAtUtc { get; set; }
    public int? OrderCount { get; set; }
    public decimal? ExpectedCashAmount { get; set; }
    public decimal? CountedCashAmount { get; set; }
    public decimal? CashDifference { get; set; }
    public decimal? CardNetAmount { get; set; }

    /// <summary>short（短款）| over（长款）| even（持平）| none（无金额），按 CashDifference 符号，NULL 为 none。</summary>
    public string DifferenceKind { get; set; } = "none";

    /// <summary>同一分店、设备、营业日内按保存时间升序的第几次日结（从 1 起，不落库，查询时计算）。</summary>
    public int SaveSequence { get; set; }

    /// <summary>该分店、设备、营业日内的日结总次数。</summary>
    public int SaveCountInDay { get; set; }
}

public sealed class DailyCloseTenderDto
{
    /// <summary>Cash | Card | Voucher。</summary>
    public string Method { get; set; } = string.Empty;
    public decimal SalesAmount { get; set; }
    public decimal RefundAmount { get; set; }
    public decimal NetAmount { get; set; }
}

public sealed class DailyCloseCashCountDto
{
    /// <summary>面额（分），例如 10000 表示 100 元。</summary>
    public int DenominationCents { get; set; }
    public int Quantity { get; set; }
    public decimal SubtotalAmount { get; set; }

    /// <summary>Note（纸币，面额 ≥ 5 元）| Coin（硬币）。</summary>
    public string Kind { get; set; } = string.Empty;
}

public sealed class DailyCloseDetailDto : DailyCloseListItemDto
{
    public DateTime? PeriodFromUtc { get; set; }
    public DateTime? PeriodToUtc { get; set; }
    public string? AppVersion { get; set; }
    public decimal? ReturnQuantity { get; set; }
    public decimal? RefundAmount { get; set; }

    /// <summary>仅 Full 记录有值；非 Full 为空数组。</summary>
    public List<DailyCloseTenderDto> Tenders { get; set; } = [];

    /// <summary>仅 Full 记录有值（按面额降序）；非 Full 为空数组。</summary>
    public List<DailyCloseCashCountDto> CashCounts { get; set; } = [];

    public decimal? NoteSubtotal { get; set; }
    public decimal? CoinSubtotal { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
}
