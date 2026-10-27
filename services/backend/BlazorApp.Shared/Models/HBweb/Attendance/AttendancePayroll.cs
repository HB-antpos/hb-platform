using SqlSugar;

namespace BlazorApp.Shared.Models;

// 工时统计与工资（规格 docs/plans/2026-10-21-attendance-payroll-leave.md 第 1 节）的五张新表。
// 实体都不进 SqlSugarContext 的自动建表清单，由版本号迁移 20261026.002-attendance-payroll-leave 创建。
// 日期列（EffectiveFrom / PeriodStart / HolidayDate）存门店本地日期，没有时分。

/// <summary>
/// 个人时薪表：按生效日期留历史，只新增不改；工资按工作日当天有效的那条算。(UserGuid, EffectiveFrom) 唯一。
/// PayMode：Transfer 全转账 / Cash 全现金 / Mixed 转账 + 现金。转账分平日、周六、周日、公假四档，现金只有一个价。
/// </summary>
[SugarTable("EmployeePayRate")]
public sealed class EmployeePayRate
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string RateGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(IsNullable = false, Length = 50)]
    public string UserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 20)]
    public string PayMode { get; set; } = "Transfer";

    /// <summary>转账 + 现金时每周固定转账工时；转账容量 = 本值 × 周数。</summary>
    [SugarColumn(IsNullable = false, Length = 6, DecimalDigits = 2)]
    public decimal FixedTransferHoursPerWeek { get; set; }

    [SugarColumn(IsNullable = true, Length = 8, DecimalDigits = 2)]
    public decimal? TransferWeekdayRate { get; set; }

    [SugarColumn(IsNullable = true, Length = 8, DecimalDigits = 2)]
    public decimal? TransferSaturdayRate { get; set; }

    [SugarColumn(IsNullable = true, Length = 8, DecimalDigits = 2)]
    public decimal? TransferSundayRate { get; set; }

    [SugarColumn(IsNullable = true, Length = 8, DecimalDigits = 2)]
    public decimal? TransferPublicHolidayRate { get; set; }

    /// <summary>现金单一时薪，不分周末 / 公假（10-26 定）。</summary>
    [SugarColumn(IsNullable = true, Length = 8, DecimalDigits = 2)]
    public decimal? CashRate { get; set; }

    /// <summary>每期现金补贴，负数＝扣回。</summary>
    [SugarColumn(IsNullable = false, Length = 8, DecimalDigits = 2)]
    public decimal CashAllowancePerPeriod { get; set; }

    /// <summary>自填最低时薪：任一用到的时薪低于它就提示。</summary>
    [SugarColumn(IsNullable = true, Length = 8, DecimalDigits = 2)]
    public decimal? MinimumRate { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "date")]
    public DateTime EffectiveFrom { get; set; }

    [SugarColumn(IsNullable = true, Length = 500)]
    public string? Remark { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime CreatedAt { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? CreatedBy { get; set; }
}

/// <summary>
/// 发薪周期状态：(StoreCode, PeriodStart, Weeks) 唯一；没有行等同 Open。
/// Submitted 后该店该期的排班、打卡、补卡、审批、请假、时薪、备注、年假核定都不能再改（写接口返回 PERIOD_LOCKED）。
/// 解锁把状态改回 Open，并记录最后一次解锁人、时间、原因。
/// </summary>
[SugarTable("AttendancePayPeriod")]
public sealed class AttendancePayPeriod
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string PeriodGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, ColumnDataType = "date")]
    public DateTime PeriodStart { get; set; }

    /// <summary>1 或 2 周。</summary>
    [SugarColumn(IsNullable = false)]
    public int Weeks { get; set; } = 1;

    [SugarColumn(IsNullable = false, Length = 20)]
    public string Status { get; set; } = "Open";

    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? SubmittedAt { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? SubmittedBy { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? UnlockedAt { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? UnlockedBy { get; set; }

    [SugarColumn(IsNullable = true, Length = 500)]
    public string? UnlockReason { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime CreatedAt { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? UpdatedAt { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? UpdatedBy { get; set; }
}

/// <summary>工资表备注：每人每期一条，(StoreCode, PeriodStart, Weeks, UserGuid) 唯一。</summary>
[SugarTable("AttendancePayPeriodNote")]
public sealed class AttendancePayPeriodNote
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string NoteGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, ColumnDataType = "date")]
    public DateTime PeriodStart { get; set; }

    [SugarColumn(IsNullable = false)]
    public int Weeks { get; set; } = 1;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string UserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 1000)]
    public string Note { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime UpdatedAt { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// 年假（休息 PaidAnnual / 发放 AnnualCashOut）财务核定工时：每人每期每类一条，
/// (StoreCode, PeriodStart, Weeks, UserGuid, LeaveType) 唯一；没有记录时工资按申请工时算。
/// </summary>
[SugarTable("AttendancePayPeriodLeaveApproval")]
public sealed class AttendancePayPeriodLeaveApproval
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string ApprovalGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, ColumnDataType = "date")]
    public DateTime PeriodStart { get; set; }

    [SugarColumn(IsNullable = false)]
    public int Weeks { get; set; } = 1;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string UserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 30)]
    public string LeaveType { get; set; } = string.Empty;

    /// <summary>核定时的申请工时快照（本期内）。</summary>
    [SugarColumn(IsNullable = false, Length = 6, DecimalDigits = 2)]
    public decimal RequestedHours { get; set; }

    [SugarColumn(IsNullable = false, Length = 6, DecimalDigits = 2)]
    public decimal ApprovedHours { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? ApprovedBy { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime ApprovedAt { get; set; }
}

/// <summary>
/// 带薪公假确认：全职 / 兼职在公假当天没排班，但过去 N 周同一星期几排班 ≥ M 次时生成（店长打开审核列表或工时统计时），
/// 进审批中心（AttendanceApproval.SourceType = PublicHolidayPay，SourceGuid = PayGuid）。
/// Status：Pending 待确认 / Confirmed 已确认（Hours 为确认工时）/ NotApplicable 不适用（驳回）/ Cancelled 条件消失被系统取消。
/// (UserGuid, StoreCode, HolidayDate) 唯一，处理过的不会再生成。
/// </summary>
[SugarTable("AttendancePublicHolidayPay")]
public sealed class AttendancePublicHolidayPay
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string PayGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(IsNullable = false, Length = 50)]
    public string UserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, ColumnDataType = "date")]
    public DateTime HolidayDate { get; set; }

    /// <summary>回看窗口内同一星期几的排班次数。</summary>
    [SugarColumn(IsNullable = false)]
    public int OrdinaryCount { get; set; }

    [SugarColumn(IsNullable = false)]
    public int LookbackWeeks { get; set; }

    /// <summary>建议工时：回看窗口内这些排班扣餐后的平均工时。</summary>
    [SugarColumn(IsNullable = true, Length = 6, DecimalDigits = 2)]
    public decimal? SuggestedHours { get; set; }

    [SugarColumn(IsNullable = true, Length = 6, DecimalDigits = 2)]
    public decimal? Hours { get; set; }

    [SugarColumn(IsNullable = false, Length = 20)]
    public string Status { get; set; } = "Pending";

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? ReviewedBy { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? ReviewedAt { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime CreatedAt { get; set; }
}
