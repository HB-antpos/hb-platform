namespace BlazorApp.Shared.DTOs
{
    public class AttendanceScheduleQueryDto
    {
        public string? StoreCode { get; set; }
        public string? UserGuid { get; set; }
        public DateTime? WeekStartDate { get; set; }
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }

    public class CreateAttendanceScheduleDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string UserGuid { get; set; } = string.Empty;
        public DateTime WorkDate { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string? Status { get; set; }
        public string? Remark { get; set; }
        /// <summary>用餐次数覆盖值（0–3）；null 表示按班次时长自动计算。</summary>
        public int? MealBreakCount { get; set; }
    }

    public class PublishAttendanceWeekDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public DateTime WeekStartDate { get; set; }
    }

    /// <summary>未成年用工规则只提供可继续操作的提醒，不改变排班或打卡成功状态。</summary>
    public class MinorEmploymentComplianceFindingDto
    {
        public string RuleId { get; set; } = string.Empty;
        public string Severity { get; set; } = "review";
        public string Message { get; set; } = string.Empty;
        public DateTime? WorkDate { get; set; }
        public int? ActualMinutes { get; set; }
        public int? LimitMinutes { get; set; }
        public string RuleCategory { get; set; } = "incomplete";
        public string? SourceUrl { get; set; }
    }

    public class MinorEmploymentComplianceEvaluationDto
    {
        public List<MinorEmploymentComplianceFindingDto> Findings { get; set; } = new();
        public bool HasFindings => Findings.Count > 0;
    }

    public class UpdateAttendanceScheduleDto
    {
        public DateTime WorkDate { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string? Status { get; set; }
        public string? Remark { get; set; }
        /// <summary>
        /// 用餐次数覆盖值（0–3）；null 表示不修改、保持原值，
        /// 避免旧版 App 不传该字段时把店长已设置的次数冲掉。
        /// </summary>
        public int? MealBreakCount { get; set; }
        /// <summary>true 表示恢复按时长自动计算（写回 null），优先级高于 MealBreakCount。</summary>
        public bool ResetMealBreakCount { get; set; }
    }

    public class AttendanceScheduleDto
    {
        public string ScheduleGuid { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public string UserGuid { get; set; } = string.Empty;
        public string? EmployeeName { get; set; }
        public bool IsMine { get; set; }
        public DateTime WorkDate { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Remark { get; set; }
        /// <summary>软删除时间（UTC），仅「已删除排班」列表返回。</summary>
        public DateTime? DeletedAt { get; set; }
        /// <summary>软删除操作者，仅「已删除排班」列表返回。</summary>
        public string? DeletedBy { get; set; }
        /// <summary>店长指定的用餐次数；null 表示按班次时长自动计算。</summary>
        public int? MealBreakCount { get; set; }
        /// <summary>排班的有效用餐次数：店长指定值，否则按班次时长默认（超 4.5 小时 1 次、超 9 小时 2 次）。后端统一计算，移动端以此为准。</summary>
        public int EffectiveMealBreakCount { get; set; }
        /// <summary>计薪时从已闭合班段工时里扣除的用餐分钟数（是否休息过都按排班扣）。</summary>
        public int MealDeductionMinutes { get; set; }
        /// <summary>声明没休息且店长已批准、加回工时的分钟数。</summary>
        public int ApprovedMealAddBackMinutes { get; set; }
        /// <summary>声明没休息、等待店长审核的分钟数（只展示，未计入 PaidMinutes）。</summary>
        public int PendingMealAddBackMinutes { get; set; }
        /// <summary>计薪工时 = 已闭合班段工时 − 用餐扣除 + 已批准加回；WorkedMinutes 含义不变（仍是原始班段时长）。</summary>
        public int PaidMinutes { get; set; }
        /// <summary>用餐状态（休息横幅、计时、下班前确认）；仅「我的今日」接口且排班有用餐时返回。</summary>
        public AttendanceMealStateDto? Meal { get; set; }
        /// <summary>只读合规提醒；存在提醒时排班仍可保存和发布。</summary>
        public MinorEmploymentComplianceEvaluationDto? MinorCompliance { get; set; }
        public string ScheduleState { get; set; } = "NotStarted";
        public int SegmentLimit { get; set; } = 2;
        public int CompletedSegmentCount { get; set; }
        public int WorkedMinutes { get; set; }
        public int BreakMinutes { get; set; }
        public bool HasOpenSegment { get; set; }
        public bool HasMissingClockOut { get; set; }
        public int EarlyOvertimeMinutes { get; set; }
        public int LateOvertimeMinutes { get; set; }
        public int CandidateOvertimeMinutes { get; set; }
        public int ApprovedOvertimeMinutes { get; set; }
        public string? OvertimeApprovalStatus { get; set; }
        /// <summary>当天已批准的请假类型；有值表示该班次员工请假，不计工时、不计缺卡。</summary>
        public string? LeaveType { get; set; }
        public string? LeaveGuid { get; set; }
        public List<AttendanceShiftSegmentDto> Segments { get; set; } = new();
    }

    /// <summary>把来源周的排班复制到目标周（作为草稿），已有排班的员工当天跳过。</summary>
    public class CopyAttendanceScheduleWeekDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public DateTime SourceWeekStartDate { get; set; }
        public DateTime TargetWeekStartDate { get; set; }
    }

    public class CopyAttendanceScheduleWeekResultDto
    {
        public int CreatedCount { get; set; }
        public int SkippedCount { get; set; }
    }

    public class AttendanceShiftSegmentDto
    {
        public int SegmentIndex { get; set; }
        public AttendancePunchDto? ClockIn { get; set; }
        public AttendancePunchDto? ClockOut { get; set; }
        public int? DurationMinutes { get; set; }
        public string Status { get; set; } = "Open";
    }

    public class AttendanceWorkSessionDto
    {
        public string ScheduleState { get; set; } = "NotStarted";
        public int SegmentLimit { get; set; }
        public int CompletedSegmentCount { get; set; }
        public int WorkedMinutes { get; set; }
        public int BreakMinutes { get; set; }
        public bool HasOpenSegment { get; set; }
        public bool HasMissingClockOut { get; set; }
        public int EarlyOvertimeMinutes { get; set; }
        public int LateOvertimeMinutes { get; set; }
        public int CandidateOvertimeMinutes { get; set; }
        public List<AttendanceShiftSegmentDto> Segments { get; set; } = new();
    }

    public class AttendanceAvailabilityQueryDto
    {
        public string? StoreCode { get; set; }
        public string? UserGuid { get; set; }
        public DateTime? WeekStartDate { get; set; }
        /// <summary>按可上班日期过滤的起止（含当天）；可与 WeekStartDate 同时使用。</summary>
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
    }

    public class AttendanceAvailabilitySegmentDto
    {
        public DateTime AvailableDate { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string? Remark { get; set; }
        // true＝不能上班；旧客户端不传即 false＝可上班。
        public bool IsUnavailable { get; set; }
    }

    public class CreateAttendanceAvailabilityDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public DateTime WeekStartDate { get; set; }
        public List<AttendanceAvailabilitySegmentDto> Segments { get; set; } = new();
    }

    public class UpdateAttendanceAvailabilityDto
    {
        public DateTime AvailableDate { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string? Remark { get; set; }
        // null＝保持原类型（兼容不传该字段的旧 App），有值才覆盖。
        public bool? IsUnavailable { get; set; }
    }

    public class AttendanceAvailabilityDto
    {
        public string AvailabilityGuid { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string UserGuid { get; set; } = string.Empty;
        /// <summary>员工显示名（全名，缺省用户名）；管理端列表填充。</summary>
        public string? UserName { get; set; }
        public DateTime WeekStartDate { get; set; }
        public DateTime AvailableDate { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public bool IsUnavailable { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Remark { get; set; }
    }

    public class AttendancePunchRequestDto
    {
        public string? QrToken { get; set; }
        public string? PunchAuthorizationToken { get; set; }
        public string StoreCode { get; set; } = string.Empty;
        public string PunchType { get; set; } = "ClockIn";
        public string? StoreTimeZone { get; set; }
        public DateTime? PunchTimeUtc { get; set; }
        public string? DeviceId { get; set; }
        public double? LocationLatitude { get; set; }
        public double? LocationLongitude { get; set; }
        public double? LocationAccuracy { get; set; }
        public string? LocationPermissionStatus { get; set; }
        public DateTime? LocationCapturedAtUtc { get; set; }
        public string? Remark { get; set; }
        /// <summary>下班时对「缺休息」的声明；旧版 App 不带，服务端按「已休息」处理。</summary>
        public AttendanceMealDeclarationDto? MealDeclaration { get; set; }
        /// <summary>
        /// 客户端按今日状态期望的打卡类型（ClockIn / ClockOut）；null＝旧客户端，按服务端判定。
        /// 与服务端判定不一致时拒绝，错误码 PUNCH_TYPE_MISMATCH。
        /// </summary>
        public string? ExpectedPunchType { get; set; }
    }

    public class AttendanceQrResolveRequestDto
    {
        public string? QrToken { get; set; }
    }

    public class AttendanceQrResolveDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string DeviceCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string PunchAuthorizationToken { get; set; } = string.Empty;
        public DateTime PunchAuthorizationExpiresAtUtc { get; set; }
    }

    public class AttendancePunchQueryDto
    {
        public string? StoreCode { get; set; }
        public string? UserGuid { get; set; }
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public string? Status { get; set; }
    }

    public class AttendancePunchDto
    {
        public string PunchGuid { get; set; } = string.Empty;
        public string? ScheduleGuid { get; set; }
        public string StoreCode { get; set; } = string.Empty;
        public string UserGuid { get; set; } = string.Empty;
        public DateTime WorkDate { get; set; }
        public string StoreTimeZone { get; set; } = string.Empty;
        public string PunchType { get; set; } = string.Empty;
        public DateTime PunchTimeUtc { get; set; }
        public DateTime PunchTimeLocal { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? DeviceId { get; set; }
        public double? LocationLatitude { get; set; }
        public double? LocationLongitude { get; set; }
        public double? LocationAccuracy { get; set; }
        public string? LocationPermissionStatus { get; set; }
        public DateTime? LocationCapturedAtUtc { get; set; }
        public string Source { get; set; } = string.Empty;
        public string? QrTokenId { get; set; }
        public string? PosDeviceCode { get; set; }
        public string? SigningKeyId { get; set; }
        public string? EmployeeName { get; set; }
        public string? StoreName { get; set; }
        public DateTime? ServerTimeUtc { get; set; }
        public string? Remark { get; set; }
        /// <summary>打卡已保存后的未成年用工提醒，不能作为打卡失败条件。</summary>
        public MinorEmploymentComplianceEvaluationDto? MinorCompliance { get; set; }
        /// <summary>下班打卡时产生的用餐声明；声明没休息时同时有一条待审的 MealBreak 审批。</summary>
        public AttendanceMealClaimDto? MealClaim { get; set; }
        public int? SegmentIndex { get; set; }
        public string? SegmentStatus { get; set; }
        public bool IsBreakBoundary { get; set; }
        public string? SupersedesPunchGuid { get; set; }
        public string? AdjustmentGuid { get; set; }
        /// <summary>系统在门店时区 00:00 补的自动下班卡；它闭合的班段不计薪，等店长填实际下班时间。</summary>
        public bool IsSystemAutoClockOut { get; set; }
        public int? EarlyArrivalMinutes { get; set; }
        public int? LateMinutes { get; set; }
        public int? EarlyLeaveMinutes { get; set; }
        public int? LateDepartureMinutes { get; set; }
    }

    public class AttendanceLocationSampleRequestDto
    {
        public string? StoreCode { get; set; }
        public string? HardwareId { get; set; }
        public string? SystemDeviceNumber { get; set; }
        public string? DeviceSystem { get; set; }
        public string EventType { get; set; } = "ShiftInterval";
        public double? LocationLatitude { get; set; }
        public double? LocationLongitude { get; set; }
        public double? LocationAccuracy { get; set; }
        public string? LocationPermissionStatus { get; set; }
        public DateTime? LocationCapturedAtUtc { get; set; }
    }

    /// <summary>
    /// 班中定位批量上传请求（POST api/react/v1/attendance/location-samples/batch）。
    /// 客户端本地攒点后批量提交，可补传；门店与设备信息整单共用，逐点字段在 Samples 里。
    /// 引用类型字段全部声明为可空：缺失/为 null 时由服务端按逐点错误码处理，而不是被模型绑定整单 400。
    /// </summary>
    public class AttendanceLocationSampleBatchRequestDto
    {
        public string? StoreCode { get; set; }
        public string? HardwareId { get; set; }
        public string? SystemDeviceNumber { get; set; }
        public string? DeviceSystem { get; set; }

        /// <summary>1～200 个采样点；为空或超过 200 个整单报 TOO_MANY_SAMPLES。</summary>
        public List<AttendanceLocationSampleBatchItemDto?>? Samples { get; set; } = new();
    }

    public class AttendanceLocationSampleBatchItemDto
    {
        /// <summary>客户端生成的采样点 ID（GUID，1～50 字符），是幂等键：同一用户同一 ID 只入库一次。</summary>
        public string? ClientSampleId { get; set; }

        /// <summary>ShiftSample（默认）或 ShiftHeartbeat。</summary>
        public string? EventType { get; set; } = "ShiftSample";

        public double? LocationLatitude { get; set; }
        public double? LocationLongitude { get; set; }
        public double? LocationAccuracy { get; set; }
        public bool? IsMocked { get; set; }
        public string? LocationPermissionStatus { get; set; }
        public DateTime? LocationCapturedAtUtc { get; set; }
    }

    /// <summary>
    /// 批量上传结果。Accepted / Duplicates / Rejected 里的点客户端都会从本地队列删除；
    /// 整单失败（ApiResponse.Error）、网络错误、5xx 则保留重试。
    /// </summary>
    public class AttendanceLocationSampleBatchResultDto
    {
        /// <summary>本次新入库的 ClientSampleId。</summary>
        public List<string> Accepted { get; set; } = new();

        /// <summary>该用户下已存在（含同请求内重复）的 ClientSampleId，视为幂等成功。</summary>
        public List<string> Duplicates { get; set; } = new();

        /// <summary>逐点拒绝（这些点重试也不会成功，客户端应丢弃）。</summary>
        public List<AttendanceLocationSampleBatchRejectedDto> Rejected { get; set; } = new();
    }

    public class AttendanceLocationSampleBatchRejectedDto
    {
        public string ClientSampleId { get; set; } = string.Empty;

        /// <summary>
        /// LOCATION_REQUIRED / SAMPLE_TOO_OLD / SAMPLE_IN_FUTURE / ACTIVE_SHIFT_REQUIRED / CLIENT_SAMPLE_ID_INVALID。
        /// </summary>
        public string ErrorCode { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;
    }

    public class AttendanceLocationSampleQueryDto
    {
        public string? StoreCode { get; set; }
        public string? UserGuid { get; set; }
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public string? StoreTimeZone { get; set; }
        // 分页偏移：单日样本超过单次上限（1000）时，管理端按偏移继续取下一页，直到取完。
        public int? Offset { get; set; }
    }

    public class AttendanceLocationSampleDto
    {
        public string SampleGuid { get; set; } = string.Empty;
        public string UserGuid { get; set; } = string.Empty;
        public string? StoreCode { get; set; }
        public string? HardwareId { get; set; }
        public string? SystemDeviceNumber { get; set; }
        public string? DeviceSystem { get; set; }
        public string EventType { get; set; } = string.Empty;
        public double LocationLatitude { get; set; }
        public double LocationLongitude { get; set; }
        public double? LocationAccuracy { get; set; }
        public string? LocationPermissionStatus { get; set; }
        public DateTime LocationCapturedAtUtc { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class AttendanceTodayDto
    {
        public DateTime WorkDate { get; set; }
        public MinorEmploymentComplianceEvaluationDto? MinorCompliance { get; set; }
        public List<AttendanceScheduleDto> Schedules { get; set; } = new();
        public List<AttendancePunchDto> Punches { get; set; } = new();
        public List<AttendanceStoreHolidayDto> Holidays { get; set; } = new();
        public bool CanRequestAdjustment { get; set; }
        public List<AttendanceStorePunchStateDto> StorePunchStates { get; set; } = new();
    }

    public class AttendanceStorePunchStateDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public string State { get; set; } = "NotStarted";
        public bool HasOpenSegment { get; set; }
        public bool HasMissingClockOut { get; set; }
        public string? ScheduleGuid { get; set; }
    }

    public class AttendanceApprovalQueryDto
    {
        public string? StoreCode { get; set; }
        public string? SourceType { get; set; }
        public string? ReviewStatus { get; set; }
        /// <summary>只看（true）或排除（false）本人审批的记录；null 不过滤。</summary>
        public bool? IsSelfReview { get; set; }
    }

    public class ReviewAttendanceApprovalDto
    {
        public string? ReviewRemark { get; set; }
        public int? ApprovedOvertimeMinutes { get; set; }
    }

    public class AttendanceApprovalDto
    {
        public string ApprovalGuid { get; set; } = string.Empty;
        public string SourceType { get; set; } = string.Empty;
        public string SourceGuid { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public string ApplicantUserGuid { get; set; } = string.Empty;
        public string? EmployeeName { get; set; }
        public string? ReviewerUserGuid { get; set; }
        public DateTime? WorkDate { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Detail { get; set; }
        public string ReviewStatus { get; set; } = string.Empty;
        public string? ReviewRemark { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? CandidateOvertimeMinutes { get; set; }
        public int? ApprovedOvertimeMinutes { get; set; }
        /// <summary>本人审批：审核人持 Attendance.Approval.ReviewSelf 处理了自己的申请。</summary>
        public bool IsSelfReview { get; set; }
        public AttendancePunchAdjustmentDto? Adjustment { get; set; }
        /// <summary>SourceType = MealBreak 时的用餐声明（缺几次、申请加回多少分钟）。</summary>
        public AttendanceMealClaimDto? MealClaim { get; set; }
    }

    public class CreateAttendancePunchAdjustmentDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? ScheduleGuid { get; set; }
        public string? OriginalPunchGuid { get; set; }
        // Preview 与提交之间的服务端状态版本；旧客户端可忽略返回字段，但新提交流程必须带回。
        public string? PreviewRevision { get; set; }
        public string PunchType { get; set; } = "ClockIn";
        public DateTime RequestedPunchTimeLocal { get; set; }
        public DateTimeOffset? RequestedPunchTimeUtc { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// 店长代员工补录/修改打卡：在本人补卡字段基础上指定被修改的员工。
    /// </summary>
    public class CreateManagedAttendancePunchAdjustmentDto : CreateAttendancePunchAdjustmentDto
    {
        public string UserGuid { get; set; } = string.Empty;
    }

    public class AttendancePunchAdjustmentDto
    {
        public string AdjustmentGuid { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string UserGuid { get; set; } = string.Empty;
        public string? ScheduleGuid { get; set; }
        public string? OriginalPunchGuid { get; set; }
        public DateTime? OriginalPunchTimeLocal { get; set; }
        public string PunchType { get; set; } = string.Empty;
        public DateTime RequestedPunchTimeLocal { get; set; }
        public DateTime RequestedPunchTimeUtc { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? AppliedPunchGuid { get; set; }
        public bool IsManagerSelfDirect { get; set; }
        public string RequestedByUserGuid { get; set; } = string.Empty;
        public string? ReviewedByUserGuid { get; set; }
        /// <summary>审核人显示名，仅「我的补卡」列表填充。</summary>
        public string? ReviewedByName { get; set; }
        /// <summary>审核备注（驳回原因），来自对应审核记录，仅「我的补卡」列表填充。</summary>
        public string? ReviewRemark { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>考勤用的本店员工精简信息：排班与登记请假只需要身份和用工类型。</summary>
    public class AttendanceEmployeeDto
    {
        public string UserGuid { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public string? GivenName { get; set; }
        public string? MiddleName { get; set; }
        public string? FamilyName { get; set; }
        public string? Nickname { get; set; }
        public string? EmploymentType { get; set; }
        /// <summary>仅未满 18 岁时返回年龄（排班需关注未成年工时），成年员工为空，不下发生日。</summary>
        public int? Age { get; set; }
        /// <summary>是否店长角色（店长也要排班）。</summary>
        public bool IsStoreManager { get; set; }
        /// <summary>店长且本店是其主分店（管理本店）；排班表默认只显示管理本店的店长，仅关联本店的店长需筛选后显示。</summary>
        public bool ManagesStore { get; set; }
    }

    public class AttendancePunchAdjustmentPreviewDto
    {
        public bool IsValid { get; set; }
        public string PreviewRevision { get; set; } = string.Empty;
        public string? ValidationErrorCode { get; set; }
        public string? ValidationMessage { get; set; }
        public AttendanceWorkSessionDto? ExistingSession { get; set; }
        public AttendanceWorkSessionDto? ProposedSession { get; set; }
        public int WorkedMinutesDelta { get; set; }
        public int CandidateOvertimeMinutesDelta { get; set; }
        public bool WouldAutoApprove { get; set; }
    }

    public class AttendanceStoreHolidayQueryDto
    {
        public string? StoreCode { get; set; }
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
    }

    public class CreateAttendanceStoreHolidayDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public DateTime HolidayDate { get; set; }
        public string HolidayName { get; set; } = string.Empty;
        public string BusinessStatus { get; set; } = "Open";
        public TimeSpan? OpenTime { get; set; }
        public TimeSpan? CloseTime { get; set; }
        public bool IsPaidHoliday { get; set; }
        public string? Remark { get; set; }
    }

    public class UpdateAttendanceStoreHolidayDto : CreateAttendanceStoreHolidayDto { }

    public class BatchUpsertAttendanceStoreHolidayDto
    {
        public List<string> StoreCodes { get; set; } = new();
        public DateTime HolidayDate { get; set; }
        public string HolidayName { get; set; } = string.Empty;
        public string BusinessStatus { get; set; } = "Open";
        public TimeSpan? OpenTime { get; set; }
        public TimeSpan? CloseTime { get; set; }
        public bool IsPaidHoliday { get; set; }
        public string? Remark { get; set; }
    }

    public class BatchUpsertAttendanceStoreHolidayResultDto
    {
        public int CreatedCount { get; set; }
        public int UpdatedCount { get; set; }
        public List<AttendanceStoreHolidayDto> Items { get; set; } = new();
    }

    public class SyncAttendanceStoreHolidayDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? Postcode { get; set; }
        public string? StateCode { get; set; }
        public string? Jurisdiction { get; set; }
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public int? DaysAhead { get; set; } = 30;
    }

    public class SyncAttendanceStoreHolidayResultDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? Jurisdiction { get; set; }
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public int SyncedCount { get; set; }
        public int CreatedCount { get; set; }
        public int UpdatedCount { get; set; }
        public int SkippedCount { get; set; }
        public List<string> SkippedStores { get; set; } = new();
        public List<AttendanceStoreHolidayDto> Holidays { get; set; } = new();
        public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
    }

    public class AttendanceStoreHolidayDto
    {
        public string HolidayGuid { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public DateTime HolidayDate { get; set; }
        public string HolidayName { get; set; } = string.Empty;
        public string BusinessStatus { get; set; } = string.Empty;
        public TimeSpan? OpenTime { get; set; }
        public TimeSpan? CloseTime { get; set; }
        public bool IsPaidHoliday { get; set; }
        public string? Remark { get; set; }
    }

    public class CreateAttendanceLeaveRequestDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string LeaveType { get; set; } = "AnnualLeave";
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
        public string? Reason { get; set; }
        public string? AttachmentUrl { get; set; }
    }

    public class CreateManagedAttendanceLeaveRequestDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string UserGuid { get; set; } = string.Empty;
        public string LeaveType { get; set; } = "AnnualLeave";
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
        public string? Reason { get; set; }
        public string? AttachmentUrl { get; set; }
    }

    public class AttendanceLeaveRequestDto
    {
        public string LeaveGuid { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string UserGuid { get; set; } = string.Empty;
        public string LeaveType { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
        public string? Reason { get; set; }
        public string? AttachmentUrl { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? ReviewedBy { get; set; }
        /// <summary>审核人显示名，仅「我的请假」列表填充。</summary>
        public string? ReviewedByName { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? ReviewRemark { get; set; }
    }

    public class UpdateAttendanceSettingsDto
    {
        public int LateGraceMinutes { get; set; } = 5;
        public int EarlyLeaveGraceMinutes { get; set; } = 5;
        public bool AllowNoSchedulePunch { get; set; } = true;
        public bool RequireApprovalForLate { get; set; } = true;
        public bool RequireApprovalForEarlyLeave { get; set; } = true;
        public bool RequireApprovalForNoSchedule { get; set; } = true;
        public bool RequireApprovalForDuplicate { get; set; } = true;
        /// <summary>员工每日每店段数上限；null＝不修改（兼容不带该字段的旧客户端）。</summary>
        public int? EmployeeSegmentLimit { get; set; }
        /// <summary>店长每日每店段数上限；null＝不修改。</summary>
        public int? ManagerSegmentLimit { get; set; }
        /// <summary>午夜自动下班开关；null＝不修改。</summary>
        public bool? AutoClockOutAtMidnight { get; set; }
    }

    public class AttendanceSettingsDto : UpdateAttendanceSettingsDto { }
}
