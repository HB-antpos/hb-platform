using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs;

public sealed class EmployeeMinorComplianceContactDto
{
    [Required, StringLength(200)] public string FullName { get; set; } = string.Empty;
    [Required, StringLength(50)] public string Phone { get; set; } = string.Empty;
    [EmailAddress, StringLength(254)] public string? Email { get; set; }
    [StringLength(500)] public string? Address { get; set; }
    [StringLength(20)] public string? Postcode { get; set; }
    [StringLength(50)] public string? Mobile { get; set; }
    [StringLength(100)] public string? Relationship { get; set; }
}

public sealed class EmployeeMinorSchoolCalendarDto
{
    public string? SchoolProvider { get; set; }
    public string? TimeZoneId { get; set; }
    public string? SchoolContactName { get; set; }
    public string? SchoolContactEmail { get; set; }
    public string? SchoolContactPhone { get; set; }
    public string? SchoolContactPosition { get; set; }
    public string? SchoolContactMobile { get; set; }
    public List<EmployeeMinorDateRangeDto> TermRanges { get; set; } = new();
    public List<EmployeeMinorDateRangeDto> Holidays { get; set; } = new();
    public List<EmployeeMinorDateRangeDto> PupilFreeDays { get; set; } = new();
    public List<EmployeeMinorWeeklyEducationDto> WeeklySchedule { get; set; } = new();
}

public sealed class EmployeeMinorDateRangeDto { public DateTime StartDate { get; set; } public DateTime EndDate { get; set; } public string? Label { get; set; } }
public sealed class EmployeeMinorWeeklyEducationDto { public DayOfWeek DayOfWeek { get; set; } public bool MustAttend { get; set; } public string? StartLocalTime { get; set; } public string? EndLocalTime { get; set; } }
public sealed class EmployeeMinorOtherWorkDto
{
    public bool HasOtherWork { get; set; }
    public bool HoursUnknown { get; set; }
    public List<EmployeeMinorOtherEmployerDto> Employers { get; set; } = new();
    public List<EmployeeMinorWorkIntervalDto> PlannedIntervals { get; set; } = new();
    public List<EmployeeMinorWorkIntervalDto> ActualIntervals { get; set; } = new();
}
public sealed class EmployeeMinorOtherEmployerDto
{
    public string? CompanyName { get; set; }
    public string? TradingName { get; set; }
    public string? Address { get; set; }
    public string? Postcode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public Dictionary<DayOfWeek, decimal> WeeklyHours { get; set; } = new();
}
public sealed class EmployeeMinorWorkIntervalDto { public DateTime StartUtc { get; set; } public DateTime EndUtc { get; set; } public decimal Hours { get; set; } public string? Source { get; set; } }
public sealed class EmployeeMinorCommuteDto
{
    public int AfterSchoolToStoreMinutes { get; set; }
    public int HomewardMinutes { get; set; }
    public string? TransportMode { get; set; }
    public string? PickupPerson { get; set; }
    public string? LatestTransportLocalTime { get; set; }
    public string? LatestWorkEndLocalTime { get; set; }
}
public sealed class EmployeeMinorCe1FormDto
{
    public string? GuardianAddress { get; set; }
    public string? GuardianPostcode { get; set; }
    public string? ChildGivenName { get; set; }
    public string? ChildFamilyName { get; set; }
    public string? ChildAddress { get; set; }
    public string? ChildPostcode { get; set; }
    public string? ChildPhone { get; set; }
    public string? ChildEmail { get; set; }
    public string? EmployerCompanyName { get; set; }
    public string? EmployerTradingName { get; set; }
    public string? EmployerAddress { get; set; }
    public string? EmployerPostcode { get; set; }
    public string? EmployerPhone { get; set; }
    public string? EmployerMobile { get; set; }
    public string? EmployerEmail { get; set; }
    public bool? FlexibleSchoolingQualifiedTeacher { get; set; }
    public Dictionary<string, string?> Fields { get; set; } = new();
}

public sealed class EmployeeMinorComplianceUpsertDto
{
    public int? ExpectedVersion { get; set; }
    /// <summary>当前版本草稿的修改序号；独立于签署档案的版本号，用于并发校验。</summary>
    public int? ExpectedRevision { get; set; }
    [Required] public string StateCode { get; set; } = "QLD";
    [Required] public string FormType { get; set; } = "QLD_CE1";
    public DateTime? DateOfBirth { get; set; }
    [StringLength(200)] public string? SchoolName { get; set; }
    [StringLength(100)] public string? YearLevel { get; set; }
    public bool? CompletedYear10 { get; set; }
    public string? EducationStatus { get; set; }
    public bool? RequiredToBeEnrolled { get; set; }
    public bool? EducationExemptionVerified { get; set; }
    public DateTime? ParticipationEndDate { get; set; }
    public EmployeeMinorSchoolCalendarDto? SchoolCalendar { get; set; }
    public EmployeeMinorOtherWorkDto? OtherWork { get; set; }
    public EmployeeMinorCommuteDto? Commute { get; set; }
    public EmployeeMinorCe1FormDto FormData { get; set; } = new();
    [StringLength(200)] public string? GuardianName { get; set; }
    [StringLength(50)] public string? GuardianPhone { get; set; }
    [EmailAddress, StringLength(254)] public string? GuardianEmail { get; set; }
    [StringLength(100)] public string? GuardianRelationship { get; set; }
    [MaxLength(2)] public List<EmployeeMinorComplianceContactDto> Contacts { get; set; } = new();
}

public sealed class EmployeeMinorComplianceDto
{
    public int Id { get; set; }
    public string UserGUID { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string? StoreGUID { get; set; }
    public string? StoreCode { get; set; }
    public string? StoreTimeZoneId { get; set; }
    public int Version { get; set; }
    public int Revision { get; set; }
    public string Status { get; set; } = string.Empty;
    public string StateCode { get; set; } = string.Empty;
    public string FormType { get; set; } = string.Empty;
    public DateTime? DateOfBirth { get; set; }
    public string? SchoolName { get; set; }
    public string? YearLevel { get; set; }
    public bool? CompletedYear10 { get; set; }
    public string? EducationStatus { get; set; }
    public bool? RequiredToBeEnrolled { get; set; }
    public bool? EducationExemptionVerified { get; set; }
    public DateTime? ParticipationEndDate { get; set; }
    public EmployeeMinorSchoolCalendarDto? SchoolCalendar { get; set; }
    public EmployeeMinorOtherWorkDto? OtherWork { get; set; }
    public EmployeeMinorCommuteDto? Commute { get; set; }
    public EmployeeMinorCe1FormDto? FormData { get; set; }
    public string GuardianName { get; set; } = string.Empty;
    public string? GuardianPhone { get; set; }
    public string? GuardianEmail { get; set; }
    public string? GuardianRelationship { get; set; }
    public DateTime? GuardianSignedAtUtc { get; set; }
    public string? GuardianSignedName { get; set; }
    public string ConsentScope { get; set; } = string.Empty;
    public bool GuardianTokenActive { get; set; }
    /// <summary>签署链接送达方式：email / share；未发起时为空。</summary>
    public string? GuardianInviteChannel { get; set; }
    public DateTime? GuardianInviteEmailSentAtUtc { get; set; }
    /// <summary>监护人通过邮箱验证码核验身份的时间；签署证据的一部分。</summary>
    public DateTime? GuardianEmailVerifiedAtUtc { get; set; }
    public string? DocumentSha256 { get; set; }
    public string? ReviewActor { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public string? ReviewComment { get; set; }
    public List<string> ReturnFields { get; set; } = new();
    public List<EmployeeMinorComplianceContactDto> Contacts { get; set; } = new();
    public DateTime? SubmittedAtUtc { get; set; }
}

public sealed class EmployeeMinorComplianceGuardianSignDto
{
    [Required] public int Version { get; set; }
    [Required, StringLength(200)] public string SignedName { get; set; } = string.Empty;
    [Required, StringLength(400000)] public string SignatureData { get; set; } = string.Empty;
    [Required] public bool ConfirmRelationship { get; set; }
    [Required] public bool ConfirmConsent { get; set; }
    [Required] public bool ConfirmBackupContact { get; set; }
    [Required, StringLength(1000)] public string ConsentScope { get; set; } = string.Empty;
}

public sealed class EmployeeMinorComplianceGuardianPreviewDto
{
    [Required, StringLength(500)] public string Token { get; set; } = string.Empty;
    /// <summary>邮箱验证码通过后签发的会话密钥；会话查询时可空。</summary>
    [StringLength(200)] public string? SessionKey { get; set; }
}

public sealed class EmployeeMinorComplianceGuardianSignRequestDto
{
    [Required, StringLength(500)] public string Token { get; set; } = string.Empty;
    [StringLength(200)] public string? SessionKey { get; set; }
    [Required] public int Version { get; set; }
    [Required, StringLength(200)] public string SignedName { get; set; } = string.Empty;
    [Required, StringLength(400000)] public string SignatureData { get; set; } = string.Empty;
    [Required] public bool ConfirmRelationship { get; set; }
    [Required] public bool ConfirmConsent { get; set; }
    [Required] public bool ConfirmBackupContact { get; set; }
    [Required, StringLength(1000)] public string ConsentScope { get; set; } = string.Empty;

    public EmployeeMinorComplianceGuardianSignDto ToSignDto() => new()
    {
        Version = Version, SignedName = SignedName, SignatureData = SignatureData,
        ConfirmRelationship = ConfirmRelationship, ConfirmConsent = ConfirmConsent,
        ConfirmBackupContact = ConfirmBackupContact, ConsentScope = ConsentScope
    };
}

public sealed class EmployeeMinorComplianceReviewDto
{
    [Required] public int Version { get; set; }
    [StringLength(1000)] public string? Comment { get; set; }
    public List<string> ReturnFields { get; set; } = new();
}

public sealed class EmployeeMinorComplianceInviteDto
{
    [Required] public int Version { get; set; }
    [Required] public int Revision { get; set; }
    public int ExpiryMinutes { get; set; } = 60 * 24;
    /// <summary>默认由后端直接把签署链接发到监护人邮箱；false 时只返回链接供员工转发（备用方式）。</summary>
    public bool DeliverByEmail { get; set; } = true;
}

public sealed class EmployeeMinorComplianceInviteResultDto
{
    public int Id { get; set; }
    public int Version { get; set; }
    public int Revision { get; set; }
    public string SigningUrl { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public string DeliveryChannel { get; set; } = "share";
    public bool EmailSent { get; set; }
    /// <summary>打码后的监护人邮箱，例如 m***@example.com；只用于提示员工发到了哪里。</summary>
    public string? MaskedGuardianEmail { get; set; }
    /// <summary>邮件发送失败原因；失败时链接仍有效，员工可改用转发。</summary>
    public string? EmailError { get; set; }
}

public sealed class EmployeeMinorComplianceDocumentDto
{
    public int Id { get; set; }
    public int Version { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class EmployeeMinorComplianceHistoryItemDto
{
    public int Id { get; set; }
    public int Version { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public string? ReviewComment { get; set; }
    public string? DocumentSha256 { get; set; }
    public List<EmployeeMinorComplianceAuditDto> Audits { get; set; } = new();
}

public sealed class EmployeeMinorComplianceAuditDto
{
    public string Action { get; set; } = string.Empty;
    public string? ActorLabel { get; set; }
    public DateTime CreatedAt { get; set; }
    public object? Metadata { get; set; }
}

/// <summary>监护人打开链接后看到的会话状态：未验证邮箱前不返回孩子的任何资料。</summary>
public sealed class EmployeeMinorComplianceGuardianSessionDto
{
    public string MaskedGuardianEmail { get; set; } = string.Empty;
    public bool EmailVerified { get; set; }
    public DateTime LinkExpiresAtUtc { get; set; }
    public DateTime? CodeSentAtUtc { get; set; }
    public DateTime? CodeExpiresAtUtc { get; set; }
    public DateTime? ResendAvailableAtUtc { get; set; }
    public int RemainingSends { get; set; }
}

public sealed class EmployeeMinorComplianceGuardianVerifyDto
{
    [Required, StringLength(500)] public string Token { get; set; } = string.Empty;
    [Required, StringLength(10)] public string Code { get; set; } = string.Empty;
}

/// <summary>店长发起的资料填写请求。</summary>
public sealed class EmployeeMinorComplianceRequestDto
{
    public int Id { get; set; }
    public string UserGUID { get; set; } = string.Empty;
    public string? EmployeeName { get; set; }
    public string? StoreCode { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTime? DueDate { get; set; }
    public string? RequestedByName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed class EmployeeMinorComplianceRequestCreateDto
{
    [Required, StringLength(50)] public string UserGUID { get; set; } = string.Empty;
    [StringLength(500)] public string? Note { get; set; }
    public DateTime? DueDate { get; set; }
}

/// <summary>店长视角的未成年员工：按员工资料生日现算年龄，附最新档案状态与未完成请求。</summary>
public sealed class EmployeeMinorManagerCandidateDto
{
    public string UserGUID { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string? StoreCode { get; set; }
    public DateTime Birthday { get; set; }
    public int Age { get; set; }
    public string? ComplianceStatus { get; set; }
    public int? ComplianceVersion { get; set; }
    public string? StateCode { get; set; }
    public EmployeeMinorComplianceRequestDto? OpenRequest { get; set; }
}
