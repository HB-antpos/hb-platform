using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 员工当班期间的用餐/休息记录（一键开始、一键结束，不扫码）。
/// 独立建表而不新增 PunchType：班段计算、班段上限、跨店重叠检查和定位轨迹启停都只认 ClockIn / ClockOut，
/// 把休息做成新的打卡类型会波及这些地方。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移 20261007.002-attendance-meal-break 创建。
/// </summary>
[SugarTable("AttendanceMealBreak")]
public sealed class AttendanceMealBreak
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string BreakGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(IsNullable = false, Length = 50)]
    public string UserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>门店本地的工作日（与 AttendancePunch.WorkDate 同口径）。</summary>
    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime WorkDate { get; set; }

    /// <summary>开始休息时所在班段对应的排班；当班打卡没有排班时不允许开始休息，故正常情况下非空。</summary>
    [SugarColumn(IsNullable = true, Length = 50)]
    public string? ScheduleGuid { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime StartUtc { get; set; }

    /// <summary>NULL＝进行中。每个员工同一时刻最多一条进行中的休息（数据库过滤唯一索引兜底）。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? EndUtc { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime CreatedAtUtc { get; set; }
}
