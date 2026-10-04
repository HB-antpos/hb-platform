using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs;

public sealed class EmployeeMinorReminderDto
{
    public int Id { get; set; }
    public string UserGUID { get; set; } = string.Empty;
    public string? EmployeeName { get; set; }
    public string StoreCode { get; set; } = string.Empty;
    public string? ScheduleGuid { get; set; }
    public string RuleId { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string RuleCategory { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public DateTime? WorkDate { get; set; }
    public int? ActualMinutes { get; set; }
    public int? LimitMinutes { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Revision { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ActionActor { get; set; }
    public string? ActionComment { get; set; }
    public DateTime? ActionAtUtc { get; set; }
    public bool CanContinuePublishing => true;
}

public sealed class EmployeeMinorReminderActionDto
{
    [Range(1, int.MaxValue)] public int ExpectedRevision { get; set; }
    [Required, RegularExpression("^(acknowledge|escalate)$")] public string Action { get; set; } = string.Empty;
    [StringLength(1000)] public string? Comment { get; set; }
}
