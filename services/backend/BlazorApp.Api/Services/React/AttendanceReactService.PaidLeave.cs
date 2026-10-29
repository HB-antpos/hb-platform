using System.Text.Json;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;

namespace BlazorApp.Api.Services.React
{
    // partial：带薪假（病假 / 年假休息 / 年假发放）与带薪公假确认（规格第 0.2 节）。
    public partial class AttendanceReactService
    {
        internal const string PaidSickLeaveType = "PaidSick";
        internal const string PaidAnnualLeaveType = "PaidAnnual";
        internal const string AnnualCashOutLeaveType = "AnnualCashOut";
        internal const string PublicHolidayPaySourceType = "PublicHolidayPay";
        internal const string PublicHolidayPayStatusPending = "Pending";
        internal const string PublicHolidayPayStatusConfirmed = "Confirmed";
        internal const string PublicHolidayPayStatusNotApplicable = "NotApplicable";
        internal const string PublicHolidayPayStatusCancelled = "Cancelled";
        internal const int MaxLeaveCertificatePhotos = 3;
        private const string LeaveAttachmentPathPrefix = "attendance/leave-attachments/";
        /// <summary>年假休息自动写入的「不能上班」全天时段，与移动端 ALL_DAY_END 一致。</summary>
        private static readonly TimeSpan AllDayEnd = new(23, 59, 59);

        internal static bool IsPaidLeaveType(string? leaveType) =>
            leaveType is PaidSickLeaveType or PaidAnnualLeaveType or AnnualCashOutLeaveType;

        internal static List<string> ParseJsonStringList(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<string>();
            }
            try
            {
                return (JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToList();
            }
            catch (JsonException)
            {
                return new List<string>();
            }
        }

        private static string? SerializeStringList(IReadOnlyCollection<string> values) =>
            values.Count == 0 ? null : JsonSerializer.Serialize(values);

        /// <summary>附件列表：新列 AttachmentUrls 优先，没有时退回旧单字段 AttachmentUrl。</summary>
        internal static List<string> ParseAttachmentUrls(AttendanceLeaveRequest leave)
        {
            var urls = ParseJsonStringList(leave.AttachmentUrls);
            if (urls.Count == 0 && !string.IsNullOrWhiteSpace(leave.AttachmentUrl))
            {
                urls.Add(leave.AttachmentUrl.Trim());
            }
            return urls;
        }

        private static List<string> CollectAttachmentUrls(CreateAttendanceLeaveRequestDto request)
        {
            var urls = (request.AttachmentUrls ?? new List<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .ToList();
            if (urls.Count == 0 && !string.IsNullOrWhiteSpace(request.AttachmentUrl))
            {
                urls.Add(request.AttachmentUrl.Trim());
            }
            return urls.Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>员工本人的附件必须落在本人目录 attendance/leave-attachments/{userGuid}/ 下。</summary>
        internal static bool IsOwnLeaveAttachmentUrl(string? attachmentUrl, string userGuid)
        {
            if (string.IsNullOrWhiteSpace(userGuid)
                || !Uri.TryCreate(attachmentUrl?.Trim(), UriKind.Absolute, out var uri)
                || uri.AbsolutePath.Contains("..", StringComparison.Ordinal))
            {
                return false;
            }
            return uri.AbsolutePath.Contains(
                $"/{LeaveAttachmentPathPrefix}{userGuid}/",
                StringComparison.OrdinalIgnoreCase);
        }

        private sealed record PaidLeaveValidation(
            ApiResponse<AttendanceLeaveRequestDto>? Error,
            DateTime StartDate,
            DateTime EndDate,
            decimal? PaidHours,
            List<string> ScheduleGuids,
            List<string> AttachmentUrls);

        /// <summary>
        /// 带薪假按设置参数校验并算好带薪工时：
        /// - 只有全职 / 兼职可以申请，临时工（或员工资料没填类型）返回 PAID_LEAVE_NOT_ELIGIBLE；
        /// - 病假：只能选本人在该店、有排班没打卡、生病当天起 N 天内的排班；必须有 1–3 张本人目录下的医嘱；工时 = 排班工时扣餐；
        /// - 年假休息：提前 ≥ N 周、单次 ≤ M 周；年假发放：提前 ≥ N 天、单次 ≤ M 周；工时 = 区间内工作日（排除公假）× 8 h。
        /// </summary>
        private async Task<PaidLeaveValidation> ValidatePaidLeaveAsync(
            CreateAttendanceLeaveRequestDto request,
            string userGuid,
            string storeCode,
            string leaveType)
        {
            PaidLeaveValidation Fail(string message, string code) =>
                new(ApiResponse<AttendanceLeaveRequestDto>.Error(message, code), default, default, null, new(), new());

            var employeeType = await _db.Queryable<EmployeeProfile>()
                .Where(item => !item.IsDeleted && item.UserGUID == userGuid)
                .Select(item => item.EmployeeType)
                .FirstAsync();
            if (employeeType is not EmployeeType.FullTime and not EmployeeType.PartTime)
            {
                return Fail("只有全职或兼职员工可以申请带薪假", "PAID_LEAVE_NOT_ELIGIBLE");
            }

            var settings = await GetOrCreateSettingsModelAsync();
            var storeToday = ConvertUtcToStoreLocal(
                _timeProvider.GetUtcNow().UtcDateTime,
                await ResolveStoreTimeZoneAsync(storeCode, null)).Date;
            var attachments = CollectAttachmentUrls(request);
            if (attachments.Any(item => !IsOwnLeaveAttachmentUrl(item, userGuid)))
            {
                return Fail("请假附件地址无效", "INVALID_ATTACHMENT_URL");
            }

            if (leaveType == PaidSickLeaveType)
            {
                var scheduleGuids = (request.ScheduleGuids ?? new List<string>())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (scheduleGuids.Count == 0)
                {
                    return Fail("请选择生病当天的排班", "SICK_LEAVE_SCHEDULE_REQUIRED");
                }
                if (attachments.Count == 0)
                {
                    return Fail("带薪病假必须拍医嘱", "ATTACHMENT_REQUIRED");
                }
                if (attachments.Count > MaxLeaveCertificatePhotos)
                {
                    return Fail($"医嘱照片最多 {MaxLeaveCertificatePhotos} 张", "TOO_MANY_ATTACHMENTS");
                }
                var schedules = await _db.Queryable<AttendanceSchedule>()
                    .Where(item => scheduleGuids.Contains(item.ScheduleGuid) && !item.IsDeleted && item.Status == "Active")
                    .ToListAsync();
                if (schedules.Count != scheduleGuids.Count
                    || schedules.Any(item => !item.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase)
                        || !item.StoreCode.Equals(storeCode, StringComparison.OrdinalIgnoreCase)))
                {
                    return Fail("只能选本人在该店已发布的排班", "SICK_LEAVE_SCHEDULE_INVALID");
                }
                var earliest = storeToday.AddDays(-Math.Max(0, settings.SickLeaveSubmitDays));
                if (schedules.Any(item => item.WorkDate.Date < earliest || item.WorkDate.Date > storeToday))
                {
                    return Fail($"带薪病假须在生病当天起 {settings.SickLeaveSubmitDays} 天内提交", "SICK_LEAVE_SUBMIT_WINDOW");
                }
                var punched = await _db.Queryable<AttendancePunch>()
                    .AnyAsync(item => !item.IsDeleted && item.ScheduleGuid != null && scheduleGuids.Contains(item.ScheduleGuid));
                if (punched)
                {
                    return Fail("已打卡的排班不能申请病假", "SICK_LEAVE_SCHEDULE_PUNCHED");
                }
                var hours = PayrollCalculator.Round2(schedules.Sum(SickLeaveHoursForSchedule));
                return new PaidLeaveValidation(
                    null,
                    schedules.Min(item => item.WorkDate.Date),
                    schedules.Max(item => item.WorkDate.Date),
                    hours,
                    scheduleGuids,
                    attachments);
            }

            var start = request.StartDate.Date;
            var end = request.EndDate.Date;
            var days = (end - start).Days + 1;
            if (leaveType == PaidAnnualLeaveType)
            {
                if (start < storeToday.AddDays(Math.Max(0, settings.AnnualLeaveLeadWeeks) * 7))
                {
                    return Fail($"带薪年假须提前 {settings.AnnualLeaveLeadWeeks} 周以上申请", "ANNUAL_LEAVE_LEAD_TIME");
                }
                if (days > Math.Max(1, settings.AnnualLeaveMaxWeeks) * 7)
                {
                    return Fail($"带薪年假单次最多 {settings.AnnualLeaveMaxWeeks} 周", "ANNUAL_LEAVE_TOO_LONG");
                }
            }
            else
            {
                if (start < storeToday.AddDays(Math.Max(0, settings.AnnualCashOutLeadDays)))
                {
                    return Fail($"年假发放须提前 {settings.AnnualCashOutLeadDays} 天以上申请", "ANNUAL_CASH_OUT_LEAD_TIME");
                }
                if (days > Math.Max(1, settings.AnnualCashOutMaxWeeks) * 7)
                {
                    return Fail($"年假发放单次最多 {settings.AnnualCashOutMaxWeeks} 周", "ANNUAL_CASH_OUT_TOO_LONG");
                }
            }
            if (attachments.Count > MaxLeaveCertificatePhotos)
            {
                return Fail($"附件最多 {MaxLeaveCertificatePhotos} 张", "TOO_MANY_ATTACHMENTS");
            }
            var holidays = (await LoadStorePublicHolidaysAsync(storeCode, start, end)).Keys.ToHashSet();
            var requested = PayrollCalculator.AnnualLeaveRequestedHours(start, end, holidays);
            if (requested <= 0)
            {
                return Fail("所选日期没有工作日（周末和公共假期不计年假）", "ANNUAL_LEAVE_NO_WORKDAY");
            }
            return new PaidLeaveValidation(null, start, end, requested, new List<string>(), attachments);
        }

        /// <summary>年假休息批准后：区间内每天写一条本店全天「不能上班」，SourceLeaveGuid 指回请假，排班时据此拦截。</summary>
        private async Task ApplyAnnualLeaveUnavailabilityAsync(AttendanceLeaveRequest leave, DateTime nowUtc, string? username)
        {
            await ClearLeaveUnavailabilityAsync(leave.LeaveGuid, nowUtc, username);
            var rows = new List<AttendanceAvailability>();
            for (var day = leave.StartDate.Date; day <= leave.EndDate.Date; day = day.AddDays(1))
            {
                rows.Add(new AttendanceAvailability
                {
                    AvailabilityGuid = Guid.NewGuid().ToString(),
                    StoreCode = leave.StoreCode,
                    UserGuid = leave.UserGuid,
                    WeekStartDate = GetWeekStart(day),
                    AvailableDate = day,
                    StartTime = TimeSpan.Zero,
                    EndTime = AllDayEnd,
                    IsUnavailable = true,
                    Status = "Active",
                    Remark = "带薪年假",
                    SourceLeaveGuid = leave.LeaveGuid,
                    CreatedAt = nowUtc,
                    CreatedBy = username,
                    UpdatedAt = nowUtc,
                    UpdatedBy = username,
                });
            }
            if (rows.Count > 0)
            {
                await _db.Insertable(rows).ExecuteCommandAsync();
            }
        }

        /// <summary>撤销请假时清除它自动写入的「不能上班」（员工自己上报的行 SourceLeaveGuid 为空，不受影响）。</summary>
        private async Task ClearLeaveUnavailabilityAsync(string leaveGuid, DateTime nowUtc, string? username)
        {
            await _db.Updateable<AttendanceAvailability>()
                .SetColumns(item => item.Status == "Cancelled")
                .SetColumns(item => item.IsDeleted == true)
                .SetColumns(item => item.UpdatedAt == nowUtc)
                .SetColumns(item => item.UpdatedBy == username)
                .Where(item => item.SourceLeaveGuid == leaveGuid && !item.IsDeleted)
                .ExecuteCommandAsync();
        }

        /// <summary>某天是否被已批准的带薪年假（休息）占用：自动写入的不能上班行仍有效。</summary>
        private async Task<bool> IsBlockedByAnnualLeaveAsync(string storeCode, string userGuid, DateTime workDate)
        {
            var day = workDate.Date;
            var next = day.AddDays(1);
            return await _db.Queryable<AttendanceAvailability>().AnyAsync(item =>
                !item.IsDeleted
                && item.Status == "Active"
                && item.IsUnavailable
                && item.SourceLeaveGuid != null
                && item.StoreCode == storeCode
                && item.UserGuid == userGuid
                && item.AvailableDate >= day
                && item.AvailableDate < next);
        }

        /// <summary>
        /// 店长撤销请假：待审的直接取消，已批准的改为 Revoked；两种都清除自动写入的「不能上班」。锁定期内拒绝。
        /// </summary>
        public async Task<ApiResponse<bool>> RevokeManagedLeaveRequestAsync(string leaveGuid, ReviewAttendanceApprovalDto request)
        {
            var model = await _db.Queryable<AttendanceLeaveRequest>()
                .FirstAsync(item => item.LeaveGuid == leaveGuid && !item.IsDeleted);
            if (model == null)
            {
                return ApiResponse<bool>.Error("请假申请不存在", "NOT_FOUND");
            }
            var access = await ResolveManagedStoreAccessAsync(model.StoreCode);
            if (!access.Success)
            {
                return ApiResponse<bool>.Error(access.Message, access.ErrorCode);
            }
            if (model.Status is not "Pending" and not "Approved")
            {
                return ApiResponse<bool>.Error("只能撤销待审核或已批准的请假", "LEAVE_NOT_REVOCABLE");
            }
            if (await IsPayPeriodLockedAsync(model.StoreCode, model.StartDate, model.EndDate))
            {
                return PayPeriodLocked<bool>();
            }
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var username = _currentUserService.GetCurrentUsername();
            var reviewer = ResolveCurrentUserGuid();
            var remark = NormalizeOptional(request?.ReviewRemark, 500);
            var mutationResource = AttendanceDailyMutationLock.BuildResource(model.UserGuid, string.Empty, default);
            await using (await AttendanceDailyMutationLock.AcquireProcessAsync(mutationResource))
            {
                await _db.Ado.BeginTranAsync();
                try
                {
                    await AttendanceDailyMutationLock.AcquireDatabaseAsync(_db, mutationResource);
                    var newStatus = model.Status == "Pending" ? "Cancelled" : "Revoked";
                    var updated = await _db.Updateable<AttendanceLeaveRequest>()
                        .SetColumns(item => item.Status == newStatus)
                        .SetColumns(item => item.ReviewRemark == remark)
                        .SetColumns(item => item.UpdatedAt == now)
                        .SetColumns(item => item.UpdatedBy == username)
                        .Where(item => item.LeaveGuid == leaveGuid && !item.IsDeleted && item.Status == model.Status)
                        .ExecuteCommandAsync();
                    if (updated != 1)
                    {
                        await _db.Ado.RollbackTranAsync();
                        return ApiResponse<bool>.Error("请假状态已变化，请刷新后重试", "LEAVE_STATE_CHANGED");
                    }
                    await _db.Updateable<AttendanceApproval>()
                        .SetColumns(item => item.ReviewStatus == "Cancelled")
                        .SetColumns(item => item.ReviewerUserGuid == reviewer)
                        .SetColumns(item => item.ReviewedAt == now)
                        .SetColumns(item => item.UpdatedAt == now)
                        .SetColumns(item => item.UpdatedBy == username)
                        .Where(item => item.SourceType == "Leave" && item.SourceGuid == leaveGuid && item.ReviewStatus == "Pending")
                        .ExecuteCommandAsync();
                    await ClearLeaveUnavailabilityAsync(leaveGuid, now, username);
                    await _db.Ado.CommitTranAsync();
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            await RevokeApprovalSubmittedNotificationsForSourcesAsync("Leave", new[] { leaveGuid });
            return ApiResponse<bool>.OK(true, "请假已撤销");
        }

        /// <summary>
        /// 带薪公假确认：门店公假当天（且已到当天）全职 / 兼职没排班，但过去 N 周同一星期几排班 ≥ M 次 → 生成待处理。
        /// 当天后来排了班、或次数不够了 → 系统取消还没处理的那条。处理过（确认 / 不适用）的不会再生成。任何失败只记日志。
        /// </summary>
        private async Task GeneratePublicHolidayPayApprovalsAsync(string storeCode, DateTime fromDate, DateTime toDate)
        {
            try
            {
                var settings = await GetOrCreateSettingsModelAsync();
                var storeToday = ConvertUtcToStoreLocal(
                    _timeProvider.GetUtcNow().UtcDateTime,
                    await ResolveStoreTimeZoneAsync(storeCode, null)).Date;
                var to = toDate.Date > storeToday ? storeToday : toDate.Date;
                if (to < fromDate.Date)
                {
                    return;
                }
                var holidays = await LoadStorePublicHolidaysAsync(storeCode, fromDate, to);
                if (holidays.Count == 0)
                {
                    return;
                }
                var userGuids = (await LoadStoreUsersAsync(storeCode)).Keys.ToList();
                var eligible = userGuids.Count == 0
                    ? new List<string>()
                    : await _db.Queryable<EmployeeProfile>()
                        .Where(item => !item.IsDeleted && userGuids.Contains(item.UserGUID)
                            && (item.EmployeeType == EmployeeType.FullTime || item.EmployeeType == EmployeeType.PartTime))
                        .Select(item => item.UserGUID)
                        .ToListAsync();
                if (eligible.Count == 0)
                {
                    return;
                }
                var lookbackWeeks = Math.Max(1, settings.PublicHolidayLookbackWeeks);
                var minCount = Math.Max(1, settings.PublicHolidayMinCount);
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                foreach (var holidayDate in holidays.Keys.OrderBy(item => item))
                {
                    var windowStart = holidayDate.AddDays(-7 * lookbackWeeks);
                    var windowEnd = holidayDate.AddDays(1);
                    var schedules = await _db.Queryable<AttendanceSchedule>()
                        .Where(item => !item.IsDeleted && item.Status == "Active" && item.StoreCode == storeCode
                            && item.WorkDate >= windowStart && item.WorkDate < windowEnd && eligible.Contains(item.UserGuid))
                        .ToListAsync();
                    var existing = await _db.Queryable<AttendancePublicHolidayPay>()
                        .Where(item => item.StoreCode == storeCode && item.HolidayDate == holidayDate)
                        .ToListAsync();
                    foreach (var userGuid in eligible)
                    {
                        var own = schedules.Where(item => item.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase)).ToList();
                        var record = existing.FirstOrDefault(item => item.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase));
                        var workedThatDay = own.Any(item => item.WorkDate.Date == holidayDate);
                        var ordinary = own
                            .Where(item => item.WorkDate.Date < holidayDate && item.WorkDate.DayOfWeek == holidayDate.DayOfWeek)
                            .GroupBy(item => item.WorkDate.Date)
                            .ToList();
                        var qualifies = !workedThatDay && ordinary.Count >= minCount;
                        if (!qualifies)
                        {
                            if (record?.Status == PublicHolidayPayStatusPending)
                            {
                                await _db.Updateable<AttendancePublicHolidayPay>()
                                    .SetColumns(item => item.Status == PublicHolidayPayStatusCancelled)
                                    .Where(item => item.PayGuid == record.PayGuid && item.Status == PublicHolidayPayStatusPending)
                                    .ExecuteCommandAsync();
                                await CancelPendingApprovalsAsync(PublicHolidayPaySourceType, record.PayGuid, "公假当天已有排班或排班次数不再满足，系统已取消");
                            }
                            continue;
                        }
                        if (record != null && record.Status != PublicHolidayPayStatusCancelled)
                        {
                            continue;
                        }
                        var suggested = PayrollCalculator.Round2(
                            ordinary.Average(group => group.Sum(SickLeaveHoursForSchedule)));
                        if (record != null)
                        {
                            // 之前被系统取消、现在又满足条件：复用同一行（(UserGuid, StoreCode, HolidayDate) 唯一）重新待处理。
                            record.Status = PublicHolidayPayStatusPending;
                            record.OrdinaryCount = ordinary.Count;
                            record.LookbackWeeks = lookbackWeeks;
                            record.SuggestedHours = suggested;
                            record.Hours = null;
                            await _db.Updateable(record).ExecuteCommandAsync();
                            await InsertPendingApprovalWithConflictGuardAsync(PublicHolidayPaySourceType, record.PayGuid, storeCode, userGuid, null);
                            continue;
                        }
                        var model = new AttendancePublicHolidayPay
                        {
                            PayGuid = Guid.NewGuid().ToString(),
                            UserGuid = userGuid,
                            StoreCode = storeCode,
                            HolidayDate = holidayDate,
                            OrdinaryCount = ordinary.Count,
                            LookbackWeeks = lookbackWeeks,
                            SuggestedHours = suggested,
                            Status = PublicHolidayPayStatusPending,
                            CreatedAt = now,
                        };
                        try
                        {
                            await _db.Insertable(model).ExecuteCommandAsync();
                        }
                        catch (Exception exception) when (AttendancePunchPersistenceException.IsUniqueConstraintViolation(exception))
                        {
                            continue; // 并发请求已生成同一条。
                        }
                        await CreatePendingApprovalAsync(PublicHolidayPaySourceType, model.PayGuid, storeCode, userGuid);
                    }
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "生成带薪公假确认失败，列表照常返回: StoreCode={StoreCode}, From={From:yyyy-MM-dd}, To={To:yyyy-MM-dd}",
                    storeCode, fromDate, toDate);
            }
        }

        /// <summary>店长打开审核列表时：对近 14 天的公假生成带薪公假确认（与漏下班 / 加班对账同一个回看窗口）。</summary>
        private async Task GenerateRecentPublicHolidayPayApprovalsAsync(IReadOnlyCollection<string> storeCodes)
        {
            foreach (var storeCode in storeCodes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var storeToday = ConvertUtcToStoreLocal(
                    _timeProvider.GetUtcNow().UtcDateTime,
                    await ResolveStoreTimeZoneAsync(storeCode, null)).Date;
                await GeneratePublicHolidayPayApprovalsAsync(
                    storeCode,
                    storeToday.AddDays(-(PendingReviewReconcileDays - 1)),
                    storeToday);
            }
        }

        private async Task<Dictionary<string, string>> LoadPublicHolidayPayApprovalGuidsAsync(List<string> payGuids)
        {
            if (payGuids.Count == 0)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            var approvals = await _db.Queryable<AttendanceApproval>()
                .Where(item => !item.IsDeleted && item.SourceType == PublicHolidayPaySourceType && payGuids.Contains(item.SourceGuid))
                .OrderByDescending(item => item.CreatedAt)
                .ToListAsync();
            return approvals
                .GroupBy(item => item.SourceGuid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().ApprovalGuid, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>审批列表补充：请假的结构化信息（病假所选排班、医嘱、年假区间内的排班冲突），带薪公假确认信息。</summary>
        private async Task PopulatePaidLeaveApprovalFieldsAsync(List<AttendanceApprovalDto> approvals)
        {
            var leaveGuids = approvals
                .Where(item => item.SourceType == "Leave")
                .Select(item => item.SourceGuid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (leaveGuids.Count > 0)
            {
                var leaves = (await _db.Queryable<AttendanceLeaveRequest>().Where(item => leaveGuids.Contains(item.LeaveGuid)).ToListAsync())
                    .GroupBy(item => item.LeaveGuid, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                var sickScheduleGuids = leaves.Values.SelectMany(item => ParseJsonStringList(item.ScheduleGuids)).Distinct().ToList();
                var sickSchedules = sickScheduleGuids.Count == 0
                    ? new List<AttendanceSchedule>()
                    : await _db.Queryable<AttendanceSchedule>().Where(item => sickScheduleGuids.Contains(item.ScheduleGuid)).ToListAsync();
                var punchedScheduleGuids = sickScheduleGuids.Count == 0
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : (await _db.Queryable<AttendancePunch>()
                            .Where(item => !item.IsDeleted && item.ScheduleGuid != null && sickScheduleGuids.Contains(item.ScheduleGuid))
                            .Select(item => item.ScheduleGuid)
                            .ToListAsync())
                        .Where(item => item != null)
                        .Select(item => item!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var approval in approvals.Where(item => item.SourceType == "Leave"))
                {
                    if (!leaves.TryGetValue(approval.SourceGuid, out var leave))
                    {
                        continue;
                    }
                    var dto = new AttendanceApprovalLeaveDto
                    {
                        LeaveType = leave.LeaveType,
                        StartDate = leave.StartDate.Date,
                        EndDate = leave.EndDate.Date,
                        DayCount = (leave.EndDate.Date - leave.StartDate.Date).Days + 1,
                        PaidHours = leave.PaidHours,
                        Reason = leave.Reason,
                        AttachmentUrls = ParseAttachmentUrls(leave),
                    };
                    var ownGuids = ParseJsonStringList(leave.ScheduleGuids);
                    dto.Schedules = sickSchedules
                        .Where(item => ownGuids.Contains(item.ScheduleGuid, StringComparer.OrdinalIgnoreCase))
                        .OrderBy(item => item.WorkDate)
                        .Select(item => new AttendanceLeaveScheduleDto
                        {
                            ScheduleGuid = item.ScheduleGuid,
                            WorkDate = item.WorkDate.Date,
                            StartTime = item.StartTime,
                            EndTime = item.EndTime,
                            PaidHours = SickLeaveHoursForSchedule(item),
                            HasPunch = punchedScheduleGuids.Contains(item.ScheduleGuid),
                        })
                        .ToList();
                    if (leave.LeaveType == PaidAnnualLeaveType && approval.ReviewStatus == "Pending")
                    {
                        var until = leave.EndDate.Date.AddDays(1);
                        var conflicts = await _db.Queryable<AttendanceSchedule>()
                            .Where(item => !item.IsDeleted && item.Status != "Cancelled" && item.StoreCode == leave.StoreCode
                                && item.UserGuid == leave.UserGuid && item.WorkDate >= leave.StartDate && item.WorkDate < until)
                            .OrderBy(item => item.WorkDate)
                            .ToListAsync();
                        dto.ScheduleConflicts = conflicts
                            .Select(item => $"{item.WorkDate:yyyy-MM-dd} {item.StartTime:hh\\:mm}-{item.EndTime:hh\\:mm}")
                            .ToList();
                    }
                    approval.Leave = dto;
                }
            }

            var payGuids = approvals
                .Where(item => item.SourceType == PublicHolidayPaySourceType)
                .Select(item => item.SourceGuid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (payGuids.Count == 0)
            {
                return;
            }
            var pays = await _db.Queryable<AttendancePublicHolidayPay>().Where(item => payGuids.Contains(item.PayGuid)).ToListAsync();
            var holidayNames = new Dictionary<(string, DateTime), string>();
            foreach (var group in pays.GroupBy(item => item.StoreCode, StringComparer.OrdinalIgnoreCase))
            {
                var holidays = await LoadStorePublicHolidaysAsync(group.Key, group.Min(item => item.HolidayDate), group.Max(item => item.HolidayDate));
                foreach (var (date, name) in holidays)
                {
                    holidayNames[(group.Key.ToUpperInvariant(), date)] = name;
                }
            }
            foreach (var approval in approvals.Where(item => item.SourceType == PublicHolidayPaySourceType))
            {
                var pay = pays.FirstOrDefault(item => item.PayGuid.Equals(approval.SourceGuid, StringComparison.OrdinalIgnoreCase));
                if (pay == null)
                {
                    continue;
                }
                var name = holidayNames.GetValueOrDefault((pay.StoreCode.ToUpperInvariant(), pay.HolidayDate.Date));
                approval.WorkDate = pay.HolidayDate.Date;
                approval.Title = "带薪公假确认";
                approval.Detail = $"{pay.HolidayDate:yyyy-MM-dd} {name} · 过去 {pay.LookbackWeeks} 周同一星期几排班 {pay.OrdinaryCount} 次"
                    + (pay.SuggestedHours.HasValue ? $" · 建议 {pay.SuggestedHours:0.##} h" : string.Empty);
                approval.PublicHolidayPay = new AttendanceApprovalPublicHolidayPayDto
                {
                    HolidayDate = pay.HolidayDate.Date,
                    HolidayName = name,
                    OrdinaryCount = pay.OrdinaryCount,
                    LookbackWeeks = pay.LookbackWeeks,
                    SuggestedHours = pay.SuggestedHours,
                    Hours = pay.Hours,
                    Status = pay.Status,
                };
            }
        }

        /// <summary>员工申请带薪假前读取规则参数、公假、本人已发布排班与可选的病假排班（员工读不到考勤设置接口）。</summary>
        public async Task<ApiResponse<AttendanceLeaveOptionsDto>> GetMyLeaveOptionsAsync(string? storeCode)
        {
            var userGuid = ResolveCurrentUserGuid();
            if (string.IsNullOrWhiteSpace(storeCode))
            {
                return ApiResponse<AttendanceLeaveOptionsDto>.Error("分店代码不能为空", "STORE_REQUIRED");
            }
            var code = storeCode.Trim();
            var access = await ResolveRelatedStoreAccessAsync(userGuid, code);
            if (!access.Success)
            {
                return ApiResponse<AttendanceLeaveOptionsDto>.Error(access.Message, access.ErrorCode);
            }
            var settings = await GetOrCreateSettingsModelAsync();
            var storeToday = ConvertUtcToStoreLocal(
                _timeProvider.GetUtcNow().UtcDateTime,
                await ResolveStoreTimeZoneAsync(code, null)).Date;
            var store = await _db.Queryable<Store>().FirstAsync(item => !item.IsDeleted && item.StoreCode == code);
            var employeeType = await _db.Queryable<EmployeeProfile>()
                .Where(item => !item.IsDeleted && item.UserGUID == userGuid)
                .Select(item => item.EmployeeType)
                .FirstAsync();
            // 年假最长可提前到 lead + max 周之后，日历多给一点余量。
            var horizonDays = (Math.Max(settings.AnnualLeaveLeadWeeks, 0) + Math.Max(settings.AnnualCashOutMaxWeeks, settings.AnnualLeaveMaxWeeks) + 8) * 7;
            var horizon = storeToday.AddDays(horizonDays);
            var holidays = await LoadStorePublicHolidaysAsync(code, storeToday.AddDays(-Math.Max(0, settings.SickLeaveSubmitDays)), horizon);
            var schedules = await _db.Queryable<AttendanceSchedule>()
                .Where(item => !item.IsDeleted && item.Status == "Active" && item.StoreCode == code && item.UserGuid == userGuid
                    && item.WorkDate >= storeToday.AddDays(-Math.Max(0, settings.SickLeaveSubmitDays)) && item.WorkDate < horizon)
                .OrderBy(item => item.WorkDate)
                .ToListAsync();
            var sickCandidates = schedules
                .Where(item => item.WorkDate.Date <= storeToday)
                .ToList();
            var candidateGuids = sickCandidates.Select(item => item.ScheduleGuid).ToList();
            var punched = candidateGuids.Count == 0
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : (await _db.Queryable<AttendancePunch>()
                        .Where(item => !item.IsDeleted && item.ScheduleGuid != null && candidateGuids.Contains(item.ScheduleGuid))
                        .Select(item => item.ScheduleGuid)
                        .ToListAsync())
                    .Where(item => item != null)
                    .Select(item => item!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var names = await ResolveUserDisplayNamesAsync(new[] { userGuid });
            return ApiResponse<AttendanceLeaveOptionsDto>.OK(new AttendanceLeaveOptionsDto
            {
                Today = storeToday,
                StoreCode = code,
                StoreName = store?.StoreName,
                EmployeeName = names.GetValueOrDefault(userGuid),
                EmploymentType = ToEmploymentTypeCode(employeeType),
                PaidLeaveEligible = employeeType is EmployeeType.FullTime or EmployeeType.PartTime,
                Rules = new AttendanceLeaveRulesDto
                {
                    SickLeaveSubmitDays = settings.SickLeaveSubmitDays,
                    AnnualLeaveLeadWeeks = settings.AnnualLeaveLeadWeeks,
                    AnnualLeaveMaxWeeks = settings.AnnualLeaveMaxWeeks,
                    AnnualCashOutLeadDays = settings.AnnualCashOutLeadDays,
                    AnnualCashOutMaxWeeks = settings.AnnualCashOutMaxWeeks,
                    AnnualLeaveHoursPerWorkday = PayrollCalculator.AnnualLeaveHoursPerWorkday,
                    MaxCertificatePhotos = MaxLeaveCertificatePhotos,
                },
                PublicHolidays = holidays
                    .Where(item => item.Key >= storeToday)
                    .OrderBy(item => item.Key)
                    .Select(item => new AttendanceLeaveHolidayDto { Date = item.Key, Name = item.Value })
                    .ToList(),
                PublishedScheduleDates = schedules
                    .Where(item => item.WorkDate.Date >= storeToday)
                    .Select(item => item.WorkDate.Date)
                    .Distinct()
                    .ToList(),
                SickLeaveCandidates = sickCandidates.Select(item => new AttendanceLeaveScheduleDto
                {
                    ScheduleGuid = item.ScheduleGuid,
                    WorkDate = item.WorkDate.Date,
                    StartTime = item.StartTime,
                    EndTime = item.EndTime,
                    PaidHours = SickLeaveHoursForSchedule(item),
                    HasPunch = punched.Contains(item.ScheduleGuid),
                }).ToList(),
            });
        }
    }
}
