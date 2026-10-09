using SqlSugar;

namespace BlazorApp.Api.Models.Linkly;

/// <summary>
/// 卡付款对账异常表。建表与写入都在 Hbpos.Api（启动时幂等迁移），后台只读。
/// </summary>
[SugarTable("POSM_CardTenderReconciliationIssue")]
internal sealed class PosmCardTenderReconciliationIssue
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    public string DedupKey { get; set; } = string.Empty;
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
    public DateTime FirstDetectedAt { get; set; }
    public DateTime LastDetectedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
