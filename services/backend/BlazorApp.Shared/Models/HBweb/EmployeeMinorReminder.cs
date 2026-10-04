using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>排班及实际工时提醒的经理待办；确认只记录知悉，不豁免规则或修改考勤。</summary>
[SugarTable("EmployeeMinorReminder")]
public sealed class EmployeeMinorReminder : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public int Id { get; set; }
    [SugarColumn(Length = 64, UniqueGroupNameList = new[] { "UX_MinorReminder_Fingerprint" })] public string Fingerprint { get; set; } = string.Empty;
    [SugarColumn(Length = 50)] public string UserGUID { get; set; } = string.Empty;
    [SugarColumn(Length = 50)] public string StoreCode { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true, Length = 50)] public string? ScheduleGuid { get; set; }
    [SugarColumn(Length = 80)] public string RuleId { get; set; } = string.Empty;
    [SugarColumn(Length = 30)] public string Severity { get; set; } = string.Empty;
    [SugarColumn(Length = 40)] public string RuleCategory { get; set; } = string.Empty;
    [SugarColumn(Length = 1000)] public string Message { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true, Length = 500)] public string? SourceUrl { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? WorkDate { get; set; }
    [SugarColumn(IsNullable = true)] public int? ActualMinutes { get; set; }
    [SugarColumn(IsNullable = true)] public int? LimitMinutes { get; set; }
    [SugarColumn(Length = 40)] public string Trigger { get; set; } = string.Empty;
    [SugarColumn(Length = 20)] public string Status { get; set; } = "open";
    public int Revision { get; set; } = 1;
    [SugarColumn(IsNullable = true, Length = 200)] public string? ActionActor { get; set; }
    [SugarColumn(IsNullable = true, Length = 1000)] public string? ActionComment { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? ActionAtUtc { get; set; }
}

[SugarTable("EmployeeMinorReminderEvent")]
public sealed class EmployeeMinorReminderEvent : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public int Id { get; set; }
    public int ReminderId { get; set; }
    [SugarColumn(Length = 30)] public string Action { get; set; } = string.Empty;
    [SugarColumn(Length = 200)] public string Actor { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true, Length = 1000)] public string? Comment { get; set; }
}
