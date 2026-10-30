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
    /// <summary>监护人在签署页现场修改过的字段分组：guardianDetails / childDetails / education / schoolCalendar / otherWork / commute / backupContact。</summary>
    public List<string> GuardianAmendedFields { get; set; } = new();
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
    /// <summary>监护人签署前现场修改的资料；为空表示未修改。</summary>
    public EmployeeMinorComplianceGuardianAmendDto? Amendments { get; set; }
}

/// <summary>
/// 监护人在签署页可现场修改的资料：每项为 null 表示不改，非 null 表示以此为准（服务端逐项比对，只记录真正变化的）。
/// 不含监护人邮箱（验证码身份依据）、出生日期（以员工正式资料为准）、雇主信息（公司维护）、州与表单类型。
/// </summary>
public sealed class EmployeeMinorComplianceGuardianAmendDto
{
    [StringLength(200)] public string? GuardianName { get; set; }
    [StringLength(50)] public string? GuardianPhone { get; set; }
    [StringLength(100)] public string? GuardianRelationship { get; set; }
    [StringLength(500)] public string? GuardianAddress { get; set; }
    [StringLength(20)] public string? GuardianPostcode { get; set; }
    [StringLength(200)] public string? ChildGivenName { get; set; }
    [StringLength(200)] public string? ChildFamilyName { get; set; }
    [StringLength(500)] public string? ChildAddress { get; set; }
    [StringLength(20)] public string? ChildPostcode { get; set; }
    [StringLength(50)] public string? ChildPhone { get; set; }
    [StringLength(254)] public string? ChildEmail { get; set; }
    [StringLength(200)] public string? SchoolName { get; set; }
    [StringLength(100)] public string? YearLevel { get; set; }
    public bool? CompletedYear10 { get; set; }
    public bool? FlexibleSchoolingQualifiedTeacher { get; set; }
    public EmployeeMinorSchoolCalendarDto? SchoolCalendar { get; set; }
    public EmployeeMinorOtherWorkDto? OtherWork { get; set; }
    public EmployeeMinorCommuteDto? Commute { get; set; }
    [MaxLength(2)] public List<EmployeeMinorComplianceContactDto>? Contacts { get; set; }
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
    public EmployeeMinorComplianceGuardianAmendDto? Amendments { get; set; }

    public EmployeeMinorComplianceGuardianSignDto ToSignDto() => new()
    {
        Version = Version, SignedName = SignedName, SignatureData = SignatureData,
        ConfirmRelationship = ConfirmRelationship, ConfirmConsent = ConfirmConsent,
        ConfirmBackupContact = ConfirmBackupContact, ConsentScope = ConsentScope, Amendments = Amendments
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
    /// <summary>
    /// true 时改为把签署链接短信发到监护人手机（需澳洲手机号且短信服务已开启），优先于 <see cref="DeliverByEmail"/>。
    /// 选短信后验证码也走短信；旧客户端不传此字段，行为不变。
    /// </summary>
    public bool DeliverBySms { get; set; }
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
    public bool SmsSent { get; set; }
    /// <summary>打码后的监护人手机号，例如 +614*****678；只在短信渠道返回，用于提示员工发到了哪里。</summary>
    public string? MaskedGuardianPhone { get; set; }
    /// <summary>短信发送失败原因；失败时链接仍有效，员工可改用转发。</summary>
    public string? SmsError { get; set; }
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
    /// <summary>验证码发到哪里：email（默认）或 sms。sms 时页面应提示手机号而不是邮箱。</summary>
    public string CodeChannel { get; set; } = "email";
    /// <summary>打码后的监护人手机号；只在 <see cref="CodeChannel"/> 为 sms 时有值。</summary>
    public string? MaskedGuardianPhone { get; set; }
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

/// <summary>
/// 店长 / HR 视角下一名未满 18 岁员工的合规概况（Web「未成年用工合规」页签与手机「人员管理 → 员工详情」共用）。
/// 没有任何合规记录的员工也会返回，<see cref="Stage"/> 为 not_sent。
/// </summary>
public sealed class EmployeeMinorStaffComplianceDto
{
    public string UserGUID { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? StoreCode { get; set; }
    public string? StoreName { get; set; }
    /// <summary>按门店所在州：NSW / QLD；无法判断时为 UNKNOWN。</summary>
    public string StateCode { get; set; } = "UNKNOWN";
    public string FormType { get; set; } = string.Empty;
    public DateTime Birthday { get; set; }
    public int Age { get; set; }
    public int? ComplianceId { get; set; }
    public int? Version { get; set; }
    public int? Revision { get; set; }
    /// <summary>最新版本的原始状态（draft / awaiting_guardian_signature / pending_hr_review / approved / returned …）；没有记录时为空。</summary>
    public string? ComplianceStatus { get; set; }
    /// <summary>
    /// 界面用的处理阶段：not_sent（没发或已作废）/ awaiting_guardian（链接有效，等家长签字）/ link_expired（链接过期）/
    /// pending_review（家长已签，待 HR 审核）/ returned（退回修改）/ approved（审核通过）。
    /// </summary>
    public string Stage { get; set; } = "not_sent";
    public bool NeedsAction { get; set; }
    public bool CanSend { get; set; }
    public bool CanRevoke { get; set; }
    public string? GuardianName { get; set; }
    public string? GuardianRelationship { get; set; }
    /// <summary>未打码的监护人联系方式，只用于发送弹窗预填；列表显示用打码字段。</summary>
    public string? GuardianEmail { get; set; }
    public string? GuardianPhone { get; set; }
    public string? MaskedGuardianEmail { get; set; }
    public string? MaskedGuardianPhone { get; set; }
    public DateTime? LinkExpiresAtUtc { get; set; }
    public DateTime? LastSentAtUtc { get; set; }
    /// <summary>最近一次发送：null = 没选这个渠道，true = 已送达，false = 发送失败。</summary>
    public bool? LastSentEmail { get; set; }
    public bool? LastSentSms { get; set; }
    public DateTime? GuardianSignedAtUtc { get; set; }
    public string? ReviewComment { get; set; }
    public List<string> ReturnFields { get; set; } = new();
}

public sealed class EmployeeMinorStaffComplianceListDto
{
    public List<EmployeeMinorStaffComplianceDto> Items { get; set; } = new();
    /// <summary>需要处理（未发送 / 链接过期 / 退回修改）的人数，用于页签红点。</summary>
    public int NeedsActionCount { get; set; }
    /// <summary>短信服务是否可用；不可用时发送弹窗里的短信开关置灰。</summary>
    public bool SmsAvailable { get; set; }
}

/// <summary>单个员工的合规概况；员工未满 18 岁才有 <see cref="Item"/>。</summary>
public sealed class EmployeeMinorStaffComplianceDetailDto
{
    public EmployeeMinorStaffComplianceDto? Item { get; set; }
    public bool SmsAvailable { get; set; }
}

/// <summary>店长 / HR 发送家长签字链接：邮件、短信各一个开关，至少开一个。</summary>
public sealed class EmployeeMinorGuardianLinkSendDto
{
    [Required, StringLength(200)] public string GuardianName { get; set; } = string.Empty;
    [StringLength(100)] public string? GuardianRelationship { get; set; }
    [StringLength(254)] public string? GuardianEmail { get; set; }
    [StringLength(50)] public string? GuardianPhone { get; set; }
    public bool SendEmail { get; set; }
    public bool SendSms { get; set; }
    /// <summary>链接有效天数，1–7。</summary>
    public int ExpiryDays { get; set; } = 3;
    /// <summary>界面读到的最新版本与修改序号；没有记录时都为空。用于防止两个人同时发送互相覆盖。</summary>
    public int? ExpectedVersion { get; set; }
    public int? ExpectedRevision { get; set; }
}

public sealed class EmployeeMinorGuardianLinkSendResultDto
{
    public EmployeeMinorStaffComplianceDto Item { get; set; } = new();
    public DateTime ExpiresAtUtc { get; set; }
    public bool EmailRequested { get; set; }
    public bool EmailSent { get; set; }
    public string? EmailError { get; set; }
    public bool SmsRequested { get; set; }
    public bool SmsSent { get; set; }
    public string? SmsError { get; set; }
    /// <summary>验证码去向：sms / email。短信没发出去时自动改为 email。</summary>
    public string CodeChannel { get; set; } = "email";
    /// <summary>只要有渠道失败就回传链接，供复制后手动转发；全部成功时为空。</summary>
    public string? SigningUrl { get; set; }
}

public sealed class EmployeeMinorGuardianLinkRevokeDto
{
    public int ExpectedVersion { get; set; }
    public int ExpectedRevision { get; set; }
}

/// <summary>新建员工时一并录入的监护人（员工未满 18 岁时四项必填）。</summary>
public sealed class NewStaffGuardianDto
{
    [StringLength(200)] public string? Name { get; set; }
    /// <summary>英文值：Mother / Father / Legal guardian / Other（会印在英文同意书上）。</summary>
    [StringLength(100)] public string? Relationship { get; set; }
    [StringLength(50)] public string? Phone { get; set; }
    [StringLength(254)] public string? Email { get; set; }
}
