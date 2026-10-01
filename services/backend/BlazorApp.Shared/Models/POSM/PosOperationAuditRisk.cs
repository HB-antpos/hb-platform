using SqlSugar;

namespace BlazorApp.Shared.Models.POSM;

/// <summary>
/// 新收银操作审计的异常规则命中：每个事件每条规则一行，撤回只写 RetractedAtUtc 不删行。
/// 表由 BlazorApp.Api 的 POSM 迁移 20261002.001 显式创建，不进 SqlSugar 自动建表清单。
/// </summary>
[SugarTable("PosOperationAuditFlags"), Tenant("HBPOSM")]
public sealed class PosOperationAuditFlag
{
    [SugarColumn(ColumnName = "EventId", IsPrimaryKey = true, IsNullable = false)]
    public Guid EventId { get; set; }

    [SugarColumn(ColumnName = "RuleCode", IsPrimaryKey = true, Length = 32, IsNullable = false)]
    public string RuleCode { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "RuleVersion", IsNullable = false)]
    public int RuleVersion { get; set; }

    [SugarColumn(ColumnName = "StoreCode", Length = 50, IsNullable = false)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "DeviceCode", Length = 64, IsNullable = true)]
    public string? DeviceCode { get; set; }

    [SugarColumn(ColumnName = "CashierId", Length = 100, IsNullable = true)]
    public string? CashierId { get; set; }

    [SugarColumn(ColumnName = "CashierName", Length = 128, IsNullable = true)]
    public string? CashierName { get; set; }

    [SugarColumn(ColumnName = "OperationType", Length = 64, IsNullable = false)]
    public string OperationType { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "OccurredAtUtc", IsNullable = false)]
    public DateTime OccurredAtUtc { get; set; }

    [SugarColumn(ColumnName = "EvidenceJson", Length = 2000, IsNullable = false)]
    public string EvidenceJson { get; set; } = "{}";

    [SugarColumn(ColumnName = "DetectedAtUtc", IsNullable = false)]
    public DateTime DetectedAtUtc { get; set; }

    [SugarColumn(ColumnName = "UpdatedAtUtc", IsNullable = false)]
    public DateTime UpdatedAtUtc { get; set; }

    [SugarColumn(ColumnName = "RetractedAtUtc", IsNullable = true)]
    public DateTime? RetractedAtUtc { get; set; }
}

/// <summary>每个事件当前的核查结论：Result 0 已撤销（视同待核查）、1 确认正常、2 需跟进；Version 做乐观并发。</summary>
[SugarTable("PosOperationAuditReviews"), Tenant("HBPOSM")]
public sealed class PosOperationAuditReview
{
    [SugarColumn(ColumnName = "EventId", IsPrimaryKey = true, IsNullable = false)]
    public Guid EventId { get; set; }

    [SugarColumn(ColumnName = "StoreCode", Length = 50, IsNullable = false)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "OccurredAtUtc", IsNullable = false)]
    public DateTime OccurredAtUtc { get; set; }

    [SugarColumn(ColumnName = "Result", IsNullable = false)]
    public byte Result { get; set; }

    [SugarColumn(ColumnName = "Note", Length = 500, IsNullable = true)]
    public string? Note { get; set; }

    [SugarColumn(ColumnName = "ReviewedByUserId", Length = 50, IsNullable = false)]
    public string ReviewedByUserId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "ReviewedByName", Length = 100, IsNullable = false)]
    public string ReviewedByName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "ReviewedAtUtc", IsNullable = false)]
    public DateTime ReviewedAtUtc { get; set; }

    [SugarColumn(ColumnName = "Version", IsNullable = false)]
    public int Version { get; set; }
}

/// <summary>只追加的核查流水，任何结论变更都新增一行。</summary>
[SugarTable("PosOperationAuditReviewHistory"), Tenant("HBPOSM")]
public sealed class PosOperationAuditReviewHistory
{
    [SugarColumn(ColumnName = "Id", IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "EventId", IsNullable = false)]
    public Guid EventId { get; set; }

    [SugarColumn(ColumnName = "Result", IsNullable = false)]
    public byte Result { get; set; }

    [SugarColumn(ColumnName = "Note", Length = 500, IsNullable = true)]
    public string? Note { get; set; }

    [SugarColumn(ColumnName = "ActorUserId", Length = 50, IsNullable = false)]
    public string ActorUserId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "ActorName", Length = 100, IsNullable = false)]
    public string ActorName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "CreatedAtUtc", IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }
}
