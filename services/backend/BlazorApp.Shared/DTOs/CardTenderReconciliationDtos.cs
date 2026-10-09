namespace BlazorApp.Shared.DTOs;

/// <summary>
/// 卡付款对账异常查询参数。异常由 Hbpos.Api 的订单同步核对与后台对账作业写入
/// POSM_CardTenderReconciliationIssue；后台只读。
/// </summary>
public sealed class CardTenderReconciliationQueryDto
{
    /// <summary>Open / Resolved / Dismissed；缺省为 Open。传 All 不过滤状态。</summary>
    public string? Status { get; set; }
    public string? IssueType { get; set; }
    public string? StoreCode { get; set; }
    public string? SessionId { get; set; }
    public string? OrderGuid { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public sealed class CardTenderReconciliationIssueDto
{
    public string Id { get; set; } = string.Empty;
    public string IssueType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public string? DeviceCode { get; set; }
    public string? Environment { get; set; }
    public string? SessionId { get; set; }
    public string? TxnRef { get; set; }
    public string? OrderGuid { get; set; }
    public string? PaymentGuid { get; set; }
    public decimal? Amount { get; set; }
    public string? Detail { get; set; }
    public int OccurrenceCount { get; set; }
    public DateTime FirstDetectedAtUtc { get; set; }
    public DateTime LastDetectedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
}
