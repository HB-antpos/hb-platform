namespace BlazorApp.Shared.DTOs;

/// <summary>
/// 老系统操作日志查询条件（数据源 POSM.dbo.EmployeeLogs）。
/// From / To 是门店本地墙钟时间，按 [From, To) 半开区间比较，不做时区换算。
/// </summary>
public sealed class LegacyEmployeeLogQueryDto
{
    /// <summary>单店旧参数，与 StoreCodes 合并。</summary>
    public string? StoreCode { get; set; }

    /// <summary>分店，可多选；至少一个。</summary>
    public List<string>? StoreCodes { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    public string? DeviceCode { get; set; }

    public List<string>? EmployeeIds { get; set; }

    public List<string>? Operations { get; set; }

    /// <summary>在操作详情文本中做包含匹配，如订单号、条码、商品编码、金额。</summary>
    public string? Keyword { get; set; }

    public int PageNumber { get; set; } = 1;

    public int PageSize { get; set; } = 50;

    /// <summary>只支持按操作时间排序：asc / desc，其余值按 desc 处理。</summary>
    public string? SortOrder { get; set; }

    /// <summary>风险入口：all（默认）/ danger（危险操作类型）/ abnormal（命中异常规则）。</summary>
    public string? RiskLens { get; set; }

    /// <summary>仅 abnormal 入口生效：只看命中这些规则的记录。</summary>
    public List<string>? RuleCodes { get; set; }

    /// <summary>仅 abnormal 入口生效：all（默认）/ pending（待核查）/ reviewed（已核查，含需跟进）/ followUp（需跟进）。</summary>
    public string? ReviewStatus { get; set; }
}

public sealed class LegacyEmployeeLogItemDto
{
    public string Id { get; set; } = string.Empty;

    public string? EmployeeId { get; set; }

    public string? EmployeeName { get; set; }

    public string? Operation { get; set; }

    public string? OperationDetail { get; set; }

    /// <summary>门店本地墙钟时间（DateTimeKind.Unspecified，序列化不带时区后缀）。</summary>
    public DateTime OperationTime { get; set; }

    public string? DeviceCode { get; set; }

    public string? StoreCode { get; set; }

    /// <summary>收银机上传该条日志的时间，与 OperationTime 同为墙钟口径。</summary>
    public DateTime LastUploadTime { get; set; }

    /// <summary>操作类型属于危险操作（直接影响收款或现金）。</summary>
    public bool IsDanger { get; set; }

    /// <summary>当前有效（未撤回）的异常规则命中。</summary>
    public List<LegacyEmployeeLogFlagDto> Flags { get; set; } = new();

    /// <summary>核查结论；未核查或已撤销核查时为 null。</summary>
    public LegacyEmployeeLogReviewDto? Review { get; set; }

    /// <summary>应收减少金额（删除、改价、整单折扣、无小票退货）；无法从详情算出时为 null。</summary>
    public decimal? AmountImpact { get; set; }
}

/// <summary>一条异常规则命中。Evidence 是按规则约定键名的依据（时间为门店墙钟 HH:mm:ss），由前端按语言组织文案。</summary>
public sealed class LegacyEmployeeLogFlagDto
{
    public string RuleCode { get; set; } = string.Empty;

    public Dictionary<string, string> Evidence { get; set; } = new();

    public DateTime DetectedAtUtc { get; set; }
}

public sealed class LegacyEmployeeLogReviewDto
{
    /// <summary>normal（确认正常）/ followUp（需跟进）。</summary>
    public string Result { get; set; } = string.Empty;

    public string? Note { get; set; }

    public string ReviewedByName { get; set; } = string.Empty;

    public DateTime ReviewedAtUtc { get; set; }

    /// <summary>乐观并发版本号，提交核查时原样带回。</summary>
    public int Version { get; set; }
}

public sealed class LegacyEmployeeLogOperationCountDto
{
    public string? Operation { get; set; }

    public int Count { get; set; }
}

public sealed class LegacyEmployeeLogEmployeeOptionDto
{
    public string? EmployeeId { get; set; }

    public string? EmployeeName { get; set; }

    public int Count { get; set; }
}

public sealed class LegacyEmployeeLogDeviceOptionDto
{
    public string? DeviceCode { get; set; }

    public int Count { get; set; }
}

public sealed class LegacyEmployeeLogListResultDto
{
    public List<LegacyEmployeeLogItemDto> Items { get; set; } = new();

    /// <summary>符合全部条件的记录数。</summary>
    public int Total { get; set; }

    public int PageNumber { get; set; }

    public int PageSize { get; set; }

    /// <summary>按操作类型计数：套用除「操作类型」以外的全部条件，供汇总条与快捷筛选显示基数。</summary>
    public List<LegacyEmployeeLogOperationCountDto> OperationCounts { get; set; } = new();

    /// <summary>该分店、该时间范围内出现过的员工（不受员工、设备、关键字条件影响），供筛选下拉。</summary>
    public List<LegacyEmployeeLogEmployeeOptionDto> Employees { get; set; } = new();

    /// <summary>该分店、该时间范围内出现过的设备（不受员工、设备、关键字条件影响），供筛选下拉。</summary>
    public List<LegacyEmployeeLogDeviceOptionDto> Devices { get; set; } = new();

    /// <summary>三个风险入口的计数，与 OperationCounts 同口径（套用除操作类型、风险入口以外的全部条件）。</summary>
    public LegacyEmployeeLogRiskSummaryDto RiskSummary { get; set; } = new();
}

public sealed class LegacyEmployeeLogRiskSummaryDto
{
    public int DangerTotal { get; set; }

    public int AbnormalTotal { get; set; }

    /// <summary>命中异常规则、且没有「确认正常 / 需跟进」结论的记录数。</summary>
    public int PendingReview { get; set; }

    public int AbnormalEmployees { get; set; }

    /// <summary>按规则计数；同一条记录命中多条规则时各计一次。</summary>
    public List<LegacyEmployeeLogRuleCountDto> AbnormalByRule { get; set; } = new();
}

public sealed class LegacyEmployeeLogRuleCountDto
{
    public string RuleCode { get; set; } = string.Empty;

    public int Count { get; set; }
}

/// <summary>按员工汇总的查询条件：分店、时间与列表相同，可选设备；不接受员工、操作类型、关键字条件。</summary>
public sealed class LegacyEmployeeLogEmployeeSummaryQueryDto
{
    public List<string>? StoreCodes { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    public string? DeviceCode { get; set; }
}

public sealed class LegacyEmployeeLogEmployeeSummaryDto
{
    public string? EmployeeId { get; set; }

    public string? EmployeeName { get; set; }

    /// <summary>出现过的分店（升序）。</summary>
    public List<string> StoreCodes { get; set; } = new();

    public List<string> DeviceCodes { get; set; } = new();

    public int Total { get; set; }

    public int DangerCount { get; set; }

    public int AbnormalCount { get; set; }

    public int PendingReview { get; set; }

    public List<LegacyEmployeeLogRuleCountDto> AbnormalByRule { get; set; } = new();

    /// <summary>应收减少合计（只含能从详情算出金额的危险操作）。</summary>
    public decimal AmountImpact { get; set; }
}

public sealed class LegacyEmployeeLogEmployeeSummaryResultDto
{
    public List<LegacyEmployeeLogEmployeeSummaryDto> Employees { get; set; } = new();

    public int Total { get; set; }

    public int DangerTotal { get; set; }
}

public sealed class LegacyEmployeeLogReviewRequestDto
{
    public string? LogId { get; set; }

    /// <summary>normal / followUp / revoked（撤销，回到待核查）。</summary>
    public string? Result { get; set; }

    public string? Note { get; set; }

    /// <summary>首次核查传 null 或 0；改判或撤销须带上次读到的版本号，被别人改过时返回冲突。</summary>
    public int? ExpectedVersion { get; set; }
}

/// <summary>一条日志及其同设备前后若干分钟内的操作，供详情抽屉还原操作过程。</summary>
public sealed class LegacyEmployeeLogContextDto
{
    public LegacyEmployeeLogItemDto Target { get; set; } = new();

    public int WindowMinutes { get; set; }

    /// <summary>按操作时间升序，包含 Target 本身；超过上限时截断。</summary>
    public List<LegacyEmployeeLogItemDto> Neighbors { get; set; } = new();

    public bool Truncated { get; set; }
}
