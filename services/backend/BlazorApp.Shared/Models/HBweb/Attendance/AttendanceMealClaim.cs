using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 下班打卡时的用餐声明。排班要求有用餐、却没有足够的休息记录时，新版 App 在下班前询问员工是否休息：
/// 声明“休息了”只留痕（Status = None）；声明“没休息”则生成店长审核（Status = Pending），批准后把
/// ApprovedMinutes 加回工时。每次下班打卡最多一条（ClockOutPunchGuid 唯一，也是重复提交的幂等键）。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移 20261007.002-attendance-meal-break 创建。
/// </summary>
[SugarTable("AttendanceMealClaim")]
public sealed class AttendanceMealClaim
{
    /// <summary>审批记录（AttendanceApproval.SourceType = MealBreak）的 SourceGuid。</summary>
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string ClaimGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(IsNullable = false, Length = 50)]
    public string ScheduleGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string UserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime WorkDate { get; set; }

    /// <summary>触发本次声明的下班打卡；唯一。</summary>
    [SugarColumn(IsNullable = false, Length = 50)]
    public string ClockOutPunchGuid { get; set; } = string.Empty;

    /// <summary>下班时按排班和实际工时应有的用餐次数。</summary>
    [SugarColumn(IsNullable = false)]
    public int ExpectedCount { get; set; }

    /// <summary>下班时已有的休息（休息记录、班段间空档、此前已声明的次数）。</summary>
    [SugarColumn(IsNullable = false)]
    public int RecordedCount { get; set; }

    /// <summary>应有但没有记录的次数；员工本次需要回答的就是这几次。</summary>
    [SugarColumn(IsNullable = false)]
    public int MissingCount { get; set; }

    /// <summary>员工声明没休息的次数（0 ≤ NotTakenCount ≤ MissingCount）。</summary>
    [SugarColumn(IsNullable = false)]
    public int NotTakenCount { get; set; }

    /// <summary>申请加回的分钟数 = NotTakenCount × 30。</summary>
    [SugarColumn(IsNullable = false)]
    public int ClaimedMinutes { get; set; }

    /// <summary>店长批准的分钟数；仅 Approved 时有值。</summary>
    [SugarColumn(IsNullable = true)]
    public int? ApprovedMinutes { get; set; }

    /// <summary>None（声明已休息，无需审核）/ Pending / Approved / Rejected / Cancelled（打卡被补卡替换等原因失效）。</summary>
    [SugarColumn(IsNullable = false, Length = 20)]
    public string Status { get; set; } = "None";

    [SugarColumn(IsNullable = true, Length = 500)]
    public string? Reason { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? ReviewedAtUtc { get; set; }
}
