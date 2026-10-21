namespace BlazorApp.Shared.DTOs;

/// <summary>
/// Linkly 结算列表与导出的公共筛选、排序参数。
/// </summary>
public sealed class LinklySettlementQueryDto
{
    public string? BusinessDateFrom { get; set; }
    public string? BusinessDateTo { get; set; }
    public string? StoreCode { get; set; }
    public string? DeviceCode { get; set; }
    public string? ConnectionMode { get; set; }
    public string? Environment { get; set; }
    public string? Status { get; set; }
    public string? ProviderSubmissionState { get; set; }
    public string? Keyword { get; set; }
    public string? SortBy { get; set; }
    public string? SortOrder { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// 结算筛选用的「门店 + 设备」候选，来自已同步到服务器的结算记录。
/// </summary>
public sealed class LinklySettlementDeviceOptionDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
}

public class LinklySettlementAmountDto
{
    public string CurrencyCode { get; set; } = "AUD";
    public long PurchaseAmountMinor { get; set; }
    public long PurchaseCount { get; set; }
    public long CashOutAmountMinor { get; set; }
    public long CashOutCount { get; set; }
    public long RefundAmountMinor { get; set; }
    public long RefundCount { get; set; }
    public long TotalAmountMinor { get; set; }
    public long TotalCount { get; set; }
}

public sealed class LinklySettlementCardTotalDto : LinklySettlementAmountDto
{
    public string CardName { get; set; } = string.Empty;
}

public class LinklySettlementListItemDto
{
    public string Id { get; set; } = string.Empty;
    public Guid SettlementGuid { get; set; }
    public string StoreCode { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public DateOnly BusinessDate { get; set; }
    public string ConnectionMode { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ProviderSubmissionState { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? ResponseCode { get; set; }
    public string? ResponseText { get; set; }
    public int ReceiptCount { get; set; }
    public int PrintCount { get; set; }
    public string? LastPrintError { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string AmountParseStatus { get; set; } = "Missing";
    public LinklySettlementAmountDto? AmountSummary { get; set; }
}

public sealed class LinklySettlementDetailDto : LinklySettlementListItemDto
{
    public string? ProviderSessionId { get; set; }
    public string? CloudBackendSessionId { get; set; }
    public DateTime? FirstPrintedAtUtc { get; set; }
    public DateTime? LastPrintedAtUtc { get; set; }
    public string ClientRevision { get; set; } = string.Empty;
    public List<LinklySettlementCardTotalDto> CardTotals { get; set; } = [];
    public string[] Receipts { get; set; } = [];

    /// <summary>
    /// 后端按结算数据生成的 GRAND TOTALS 汇总文本（非终端原文）；金额未能解析时为 null。
    /// </summary>
    public string? GeneratedSummary { get; set; }
}
