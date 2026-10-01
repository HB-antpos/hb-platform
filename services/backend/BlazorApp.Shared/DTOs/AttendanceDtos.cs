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
    }

    public class PublishAttendanceWeekDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public DateTime WeekStartDate { get; set; }
    }

    public class UpdateAttendanceScheduleDto
    {
        public DateTime WorkDate { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string? Status { get; set; }
        public string? Remark { get; set; }
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
        public List<AttendanceShiftSegmentDto> Segments { get; set; } = new();
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
    }

    public class AttendanceAvailabilitySegmentDto
    {
        public DateTime AvailableDate { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string? Remark { get; set; }
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
    }

    public class AttendanceAvailabilityDto
    {
        public string AvailabilityGuid { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string UserGuid { get; set; } = string.Empty;
        public DateTime WeekStartDate { get; set; }
        public DateTime AvailableDate { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
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
        public int? SegmentIndex { get; set; }
        public string? SegmentStatus { get; set; }
        public bool IsBreakBoundary { get; set; }
        public string? SupersedesPunchGuid { get; set; }
        public string? AdjustmentGuid { get; set; }
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

    public class AttendanceLocationSampleQueryDto
    {
        public string? StoreCode { get; set; }
        public string? UserGuid { get; set; }
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public string? StoreTimeZone { get; set; }
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
        public AttendancePunchAdjustmentDto? Adjustment { get; set; }
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
        public string? EmploymentType { get; set; }
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
    }

    public class AttendanceSettingsDto : UpdateAttendanceSettingsDto { }
}
