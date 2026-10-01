namespace BlazorApp.Shared.DTOs;

public sealed class OperationAuditQueryDto
{
    public DateTimeOffset? FromUtc { get; set; }

    public DateTimeOffset? ToUtc { get; set; }

    public string? StoreCode { get; set; }

    /// <summary>分店，可多选；与 StoreCode 合并。为空时按权限范围内全部分店。</summary>
    public List<string>? StoreCodes { get; set; }

    public string? CashierKeyword { get; set; }

    /// <summary>按收银员编号精确筛选（从「按员工汇总」下钻时使用）。</summary>
    public string? CashierId { get; set; }

    public string? DeviceCode { get; set; }

    public string? DeviceSystem { get; set; }

    public string? OperationType { get; set; }

    /// <summary>操作类型，可多选；与 OperationType 合并。</summary>
    public List<string>? OperationTypes { get; set; }

    public string? Outcome { get; set; }

    /// <summary>为 null 时不过滤；true/false 时只返回紧急覆盖标记等于该值的记录。</summary>
    public bool? IsEmergencyOverride { get; set; }

    /// <summary>为 null 时不过滤；true/false 时只返回离线缓存标记等于该值的记录。</summary>
    public bool? IsOfflineCached { get; set; }

    public string? ProductKeyword { get; set; }

    public string? OrderGuid { get; set; }

    public string? Keyword { get; set; }

    public string? SortBy { get; set; }

    public string? SortOrder { get; set; }

    public int PageNumber { get; set; } = 1;

    public int PageSize { get; set; } = 20;

    /// <summary>风险入口：all（默认）/ danger（危险操作）/ abnormal（命中异常规则）。</summary>
    public string? RiskLens { get; set; }

    /// <summary>仅 abnormal 入口生效：只看命中这些规则的记录。</summary>
    public List<string>? RuleCodes { get; set; }

    /// <summary>仅 abnormal 入口生效：all（默认）/ pending（待核查）/ reviewed（已核查，含需跟进）/ followUp（需跟进）。</summary>
    public string? ReviewStatus { get; set; }
}

/// <summary>
/// 操作日志汇总计数。移动端用它作为“快捷过滤”入口，
/// 因此计数只按门店权限与基础筛选条件统计，不受 Outcome / 布尔标记过滤影响。
/// </summary>
public sealed class OperationAuditSummaryDto
{
    public int Total { get; set; }

    public int Succeeded { get; set; }

    public int Denied { get; set; }

    public int Failed { get; set; }

    public int EmergencyOverride { get; set; }

    public int OfflineCached { get; set; }

    /// <summary>危险操作数：套用除操作类型、风险入口、结果类以外的全部条件。</summary>
    public int DangerTotal { get; set; }

    /// <summary>命中异常规则（未撤回）的事件数，口径同 DangerTotal。</summary>
    public int AbnormalTotal { get; set; }

    /// <summary>命中异常规则、且没有「确认正常 / 需跟进」结论的事件数。</summary>
    public int PendingReview { get; set; }

    public int AbnormalEmployees { get; set; }

    /// <summary>按规则计数；同一事件命中多条规则时各计一次。</summary>
    public List<OperationAuditRuleCountDto> AbnormalByRule { get; set; } = [];
}

public sealed class OperationAuditRuleCountDto
{
    public string RuleCode { get; set; } = string.Empty;

    public int Count { get; set; }
}

/// <summary>一条异常规则命中。Evidence 是按规则约定键名的依据（时刻为门店墙钟 HH:mm:ss），键名与老收银一致。</summary>
public sealed class OperationAuditFlagDto
{
    public string RuleCode { get; set; } = string.Empty;

    public Dictionary<string, string> Evidence { get; set; } = new();

    public DateTime DetectedAtUtc { get; set; }
}

public sealed class OperationAuditReviewDto
{
    /// <summary>normal（确认正常）/ followUp（需跟进）。</summary>
    public string Result { get; set; } = string.Empty;

    public string? Note { get; set; }

    public string ReviewedByName { get; set; } = string.Empty;

    public DateTime ReviewedAtUtc { get; set; }

    /// <summary>乐观并发版本号，提交核查时原样带回。</summary>
    public int Version { get; set; }
}

public sealed class OperationAuditReviewRequestDto
{
    public Guid? EventId { get; set; }

    /// <summary>normal / followUp / revoked（撤销，回到待核查）。</summary>
    public string? Result { get; set; }

    public string? Note { get; set; }

    /// <summary>首次核查传 null 或 0；改判或撤销须带上次读到的版本号，被别人改过时返回冲突。</summary>
    public int? ExpectedVersion { get; set; }
}

public sealed class OperationAuditEmployeeSummaryDto
{
    public string? CashierId { get; set; }

    public string? CashierName { get; set; }

    public List<string> StoreCodes { get; set; } = [];

    public List<string> DeviceCodes { get; set; } = [];

    public int Total { get; set; }

    public int DangerCount { get; set; }

    public int AbnormalCount { get; set; }

    public int PendingReview { get; set; }

    public List<OperationAuditRuleCountDto> AbnormalByRule { get; set; } = [];

    /// <summary>应收减少合计（危险操作中能算出金额的部分）。</summary>
    public decimal AmountImpact { get; set; }
}

public sealed class OperationAuditEmployeeSummaryResultDto
{
    public List<OperationAuditEmployeeSummaryDto> Employees { get; set; } = [];

    public int Total { get; set; }

    public int DangerTotal { get; set; }
}

/// <summary>一条审计事件及其同设备前后若干分钟内的操作，供详情还原操作过程。</summary>
public sealed class OperationAuditContextDto
{
    public OperationAuditListItemDto Target { get; set; } = new();

    public int WindowMinutes { get; set; }

    /// <summary>按发生时间升序，包含 Target 本身；超过上限时截断。</summary>
    public List<OperationAuditListItemDto> Neighbors { get; set; } = [];

    public bool Truncated { get; set; }
}

public class OperationAuditListItemDto
{
    public Guid EventId { get; set; }

    public int SchemaVersion { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public DateTime ReceivedAtUtc { get; set; }

    public string OperationType { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string? CashierId { get; set; }

    public string? UserGuid { get; set; }

    public string? CashierName { get; set; }

    public bool IsOfflineCached { get; set; }

    public bool IsEmergencyOverride { get; set; }

    public string StoreCode { get; set; } = string.Empty;

    public string DeviceCode { get; set; } = string.Empty;

    public string? DeviceSystem { get; set; }

    public string? AppVersion { get; set; }

    public string? InstanceId { get; set; }

    public string? OrderGuid { get; set; }

    public string? ReceiptNumber { get; set; }

    public string? CorrelationId { get; set; }

    public string? TraceId { get; set; }

    public string? PaymentMethod { get; set; }

    public string? ReasonCode { get; set; }

    public string? SafeMessage { get; set; }

    public string CurrencyCode { get; set; } = "AUD";

    public decimal? PaymentAmount { get; set; }

    public decimal? BeforeGross { get; set; }

    public decimal? AfterGross { get; set; }

    public decimal? BeforeDiscount { get; set; }

    public decimal? AfterDiscount { get; set; }

    public decimal? BeforeActual { get; set; }

    public decimal? AfterActual { get; set; }

    public decimal? AmountDelta { get; set; }

    public int ProductCount { get; set; }

    public string? PrimaryProduct { get; set; }

    /// <summary>危险操作（直接影响收款、现金或收银环境）；手动开钱箱算，随收款自动开钱箱不算。</summary>
    public bool IsDanger { get; set; }

    /// <summary>当前有效（未撤回）的异常规则命中。</summary>
    public List<OperationAuditFlagDto> Flags { get; set; } = [];

    /// <summary>核查结论；未核查或已撤销核查时为 null。</summary>
    public OperationAuditReviewDto? Review { get; set; }

    /// <summary>应收减少金额（删除、改价、折扣、退款）；非危险操作或算不出时为 null。</summary>
    public decimal? AmountImpact { get; set; }
}

public sealed class OperationAuditDetailDto : OperationAuditListItemDto
{
    public string? PropertiesJson { get; set; }

    public List<OperationAuditDetailItemDto> Items { get; set; } = [];
}

public sealed class OperationAuditDetailItemDto
{
    public Guid EventId { get; set; }

    public int LineIndex { get; set; }

    public string? ProductCode { get; set; }

    public string? ItemNumber { get; set; }

    public string? ReferenceCode { get; set; }

    public string? LookupCode { get; set; }

    public string? DisplayName { get; set; }

    public string? LineKind { get; set; }

    public decimal? BeforeQuantity { get; set; }

    public decimal? AfterQuantity { get; set; }

    public decimal? QuantityDelta { get; set; }

    public decimal? BeforeUnitPrice { get; set; }

    public decimal? AfterUnitPrice { get; set; }

    public decimal? UnitPriceDelta { get; set; }

    public decimal? BeforeDiscountAmount { get; set; }

    public decimal? AfterDiscountAmount { get; set; }

    public decimal? DiscountAmountDelta { get; set; }

    public decimal? BeforeGrossAmount { get; set; }

    public decimal? AfterGrossAmount { get; set; }

    public decimal? GrossAmountDelta { get; set; }

    public decimal? BeforeActualAmount { get; set; }

    public decimal? AfterActualAmount { get; set; }

    public decimal? ActualAmountDelta { get; set; }
}
