using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>未成年用工合规档案的不可变版本。业务审核只改变状态，不覆盖已签版本。</summary>
[SugarTable("EmployeeMinorCompliance")]
public sealed class EmployeeMinorCompliance : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [SugarColumn(IsNullable = false, Length = 50, UniqueGroupNameList = new[] { "UX_MinorCompliance_UserVersion" })]
    public string UserGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, Length = 50)] public string? StoreGUID { get; set; }
    [SugarColumn(IsNullable = true, Length = 50)] public string? StoreCode { get; set; }
    [SugarColumn(IsNullable = true, Length = 80)] public string? StoreTimeZoneId { get; set; }

    [SugarColumn(IsNullable = false, UniqueGroupNameList = new[] { "UX_MinorCompliance_UserVersion" })]
    public int Version { get; set; }

    /// <summary>同一草稿的实体并发修订号；每次保存递增，和业务版本号分开。</summary>
    [SugarColumn(IsNullable = false)] public int Revision { get; set; } = 1;

    [SugarColumn(IsNullable = false, Length = 30)]
    public string Status { get; set; } = "draft";

    [SugarColumn(IsNullable = false, Length = 8)]
    public string StateCode { get; set; } = "QLD";

    [SugarColumn(IsNullable = false, Length = 80)]
    public string FormType { get; set; } = "QLD_CE1";

    [SugarColumn(IsNullable = true)] public DateTime? DateOfBirth { get; set; }
    [SugarColumn(IsNullable = true, Length = 200)] public string? SchoolName { get; set; }
    [SugarColumn(IsNullable = true, Length = 100)] public string? YearLevel { get; set; }
    [SugarColumn(IsNullable = true)] public bool? CompletedYear10 { get; set; }
    [SugarColumn(IsNullable = true, Length = 40)] public string? EducationStatus { get; set; }
    [SugarColumn(IsNullable = true)] public bool? RequiredToBeEnrolled { get; set; }
    [SugarColumn(IsNullable = true)] public bool? EducationExemptionVerified { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? ParticipationEndDate { get; set; }
    [SugarColumn(IsNullable = true)] public string? SchoolCalendarJson { get; set; }
    [SugarColumn(IsNullable = true)] public string? OtherWorkJson { get; set; }
    [SugarColumn(IsNullable = true)] public string? CommuteJson { get; set; }
    [SugarColumn(IsNullable = true)] public string? FormDataJson { get; set; }

    [SugarColumn(IsNullable = false, Length = 200)] public string GuardianName { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true, Length = 50)] public string? GuardianPhone { get; set; }
    [SugarColumn(IsNullable = true, Length = 254)] public string? GuardianEmail { get; set; }
    [SugarColumn(IsNullable = true, Length = 100)] public string? GuardianRelationship { get; set; }

    [SugarColumn(IsNullable = true, Length = 500)] public string? GuardianTokenHash { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? GuardianTokenExpiresAtUtc { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? GuardianSignedAtUtc { get; set; }
    [SugarColumn(IsNullable = true, Length = 200)] public string? GuardianSignedName { get; set; }
    [SugarColumn(IsNullable = true, Length = 80)] public string? GuardianSignatureHash { get; set; }
    [SugarColumn(IsNullable = true)] public bool GuardianTokenUsed { get; set; }

    /// <summary>签署链接送达方式：email＝后端直发监护人邮箱，share＝员工自行转发（备用）。</summary>
    [SugarColumn(IsNullable = true, Length = 20)] public string? GuardianInviteChannel { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? GuardianInviteEmailSentAtUtc { get; set; }

    // 监护人邮箱验证码：只存哈希，和当前签署 token 绑定；重新发起邀请时清空，验证通过后才能查看与签署。
    [SugarColumn(IsNullable = true, Length = 64)] public string? GuardianOtpHash { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? GuardianOtpExpiresAtUtc { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? GuardianOtpSentAtUtc { get; set; }
    [SugarColumn(IsNullable = false)] public int GuardianOtpSendCount { get; set; }
    [SugarColumn(IsNullable = false)] public int GuardianOtpFailedAttempts { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? GuardianEmailVerifiedAtUtc { get; set; }
    [SugarColumn(IsNullable = true, Length = 64)] public string? GuardianSessionHash { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? GuardianSessionExpiresAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 500)] public string? DocumentObjectKey { get; set; }
    [SugarColumn(IsNullable = true, Length = 64)] public string? DocumentSha256 { get; set; }
    [SugarColumn(IsNullable = true, Length = 100)] public string? ReviewActor { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? ReviewedAtUtc { get; set; }
    [SugarColumn(IsNullable = true, Length = 1000)] public string? ReviewComment { get; set; }
    [SugarColumn(IsNullable = true, Length = 1000)] public string? ReturnFieldsJson { get; set; }
    [SugarColumn(IsNullable = true, Length = 100)] public string? SubmittedBy { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? SubmittedAtUtc { get; set; }
}

[SugarTable("EmployeeMinorComplianceContact")]
public sealed class EmployeeMinorComplianceContact : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public int Id { get; set; }
    [SugarColumn(IsNullable = false)] public int ComplianceId { get; set; }
    [SugarColumn(IsNullable = false, Length = 30)] public string ContactType { get; set; } = "backup";
    [SugarColumn(IsNullable = false, Length = 200)] public string FullName { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false, Length = 50)] public string Phone { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true, Length = 50)] public string? Mobile { get; set; }
    [SugarColumn(IsNullable = true, Length = 254)] public string? Email { get; set; }
    [SugarColumn(IsNullable = true, Length = 500)] public string? Address { get; set; }
    [SugarColumn(IsNullable = true, Length = 20)] public string? Postcode { get; set; }
    [SugarColumn(IsNullable = true, Length = 100)] public string? Relationship { get; set; }
}

[SugarTable("EmployeeMinorComplianceAudit")]
public sealed class EmployeeMinorComplianceAudit : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public int Id { get; set; }
    [SugarColumn(IsNullable = false)] public int ComplianceId { get; set; }
    [SugarColumn(IsNullable = false, Length = 50)] public string Action { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true, Length = 50)] public string? ActorUserGuid { get; set; }
    [SugarColumn(IsNullable = true, Length = 200)] public string? ActorLabel { get; set; }
    [SugarColumn(IsNullable = true, Length = 1000)] public string? MetadataJson { get; set; }
}

/// <summary>店长发起的未成年用工资料填写请求；员工提交 HR 审核后自动完成。</summary>
[SugarTable("EmployeeMinorComplianceRequest")]
public sealed class EmployeeMinorComplianceRequest : BaseEntity
{
    public const string StatusOpen = "open";
    public const string StatusCompleted = "completed";
    public const string StatusCancelled = "cancelled";

    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public int Id { get; set; }
    [SugarColumn(IsNullable = false, Length = 50)] public string UserGUID { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true, Length = 50)] public string? StoreGUID { get; set; }
    [SugarColumn(IsNullable = true, Length = 50)] public string? StoreCode { get; set; }
    [SugarColumn(IsNullable = false, Length = 20)] public string Status { get; set; } = StatusOpen;
    [SugarColumn(IsNullable = true, Length = 500)] public string? Note { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? DueDate { get; set; }
    [SugarColumn(IsNullable = true, Length = 50)] public string? RequestedByUserGuid { get; set; }
    [SugarColumn(IsNullable = true, Length = 200)] public string? RequestedByName { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? CompletedAtUtc { get; set; }
    [SugarColumn(IsNullable = true)] public int? CompletedComplianceId { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? CancelledAtUtc { get; set; }
    [SugarColumn(IsNullable = true, Length = 200)] public string? CancelledBy { get; set; }
}
