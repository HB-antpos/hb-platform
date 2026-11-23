using System.Globalization;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;

namespace BlazorApp.Api.Services.React
{
    // partial：工时统计、工资表、个人时薪表、发薪周期提交 / 解锁（规格第 2、3 节）。
    // 计算口径全部在纯逻辑类 PayrollCalculator，这里只负责读库、组装输入与权限 / 锁定校验。
    public partial class AttendanceReactService
    {
        internal const string PeriodLockedErrorCode = "PERIOD_LOCKED";
        private const string PeriodLockedMessage = "该分店这一期工资已提交锁定，不能修改；需要修改请先解锁";
        private const string PayPeriodStatusOpen = "Open";
        private const string PayPeriodStatusSubmitted = "Submitted";
        /// <summary>双周起始日没设置时的默认锚点（业务方示例，周一）。</summary>
        internal static readonly DateTime DefaultFortnightAnchorDate = new(2026, 10, 5);

        private static readonly string[] AnnualLeaveTypes = { PaidAnnualLeaveType, AnnualCashOutLeaveType };

        // ---------------- 周期与锁定 ----------------

        /// <summary>
        /// 期起始日：1 周＝该日所在周的周一；2 周＝从双周起始日（取其所在周一）起每 14 天一期，取包含该日的那期。
        /// </summary>
        internal static DateTime ResolvePayPeriodStart(DateTime date, int weeks, DateTime? fortnightAnchor)
        {
            var monday = GetWeekStart(date.Date);
            if (weeks != 2)
            {
                return monday;
            }
            var anchor = GetWeekStart((fortnightAnchor ?? DefaultFortnightAnchorDate).Date);
            var diff = (monday - anchor).Days;
            var offset = ((diff % 14) + 14) % 14;
            return monday.AddDays(-offset);
        }

        private static int ResolvePeriodWeeks(int? requested, AttendanceSettings settings)
        {
            var weeks = requested ?? settings.DefaultPeriodWeeks;
            return weeks == 2 ? 2 : 1;
        }

        private static ApiResponse<T> PayPeriodLocked<T>() =>
            ApiResponse<T>.Error(PeriodLockedMessage, PeriodLockedErrorCode);

        /// <summary>
        /// 该店 [fromDate, toDate] 是否落在任何已提交（锁定）的周期里。周期行很少，取出后在内存里判断区间相交，
        /// 避免各数据库日期运算方言不同。storeCode 为空时不锁（调用方已校验过分店）。
        /// </summary>
        private async Task<bool> IsPayPeriodLockedAsync(string? storeCode, DateTime fromDate, DateTime toDate)
        {
            if (string.IsNullOrWhiteSpace(storeCode))
            {
                return false;
            }
            var code = storeCode.Trim();
            var from = fromDate.Date;
            var to = toDate.Date < from ? from : toDate.Date;
            var submitted = await _db.Queryable<AttendancePayPeriod>()
                .Where(item => item.StoreCode == code && item.Status == PayPeriodStatusSubmitted && item.PeriodStart <= to)
                .ToListAsync();
            return submitted.Any(item => item.PeriodStart.Date.AddDays(Math.Max(1, item.Weeks) * 7) > from);
        }

        private Task<bool> IsPayPeriodLockedAsync(string? storeCode, DateTime date) =>
            IsPayPeriodLockedAsync(storeCode, date, date);

        /// <summary>审批涉及的日期（用于锁定判断与工时统计的待处理角标）。</summary>
        private async Task<Dictionary<string, (DateTime From, DateTime To)>> ResolveApprovalDateRangesAsync(
            IReadOnlyCollection<AttendanceApproval> approvals)
        {
            var result = new Dictionary<string, (DateTime From, DateTime To)>(StringComparer.OrdinalIgnoreCase);
            if (approvals.Count == 0)
            {
                return result;
            }
            List<string> GuidsOf(params string[] types) => approvals
                .Where(item => types.Contains(item.SourceType, StringComparer.OrdinalIgnoreCase))
                .Select(item => item.SourceGuid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var punchGuids = GuidsOf("Punch");
            var punchDates = punchGuids.Count == 0
                ? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
                : (await _db.Queryable<AttendancePunch>().Where(item => punchGuids.Contains(item.PunchGuid)).ToListAsync())
                    .GroupBy(item => item.PunchGuid, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().WorkDate.Date, StringComparer.OrdinalIgnoreCase);
            var adjustmentGuids = GuidsOf("PunchAdjustment");
            var adjustmentDates = adjustmentGuids.Count == 0
                ? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
                : (await _db.Queryable<AttendancePunchAdjustment>().Where(item => adjustmentGuids.Contains(item.AdjustmentGuid)).ToListAsync())
                    .GroupBy(item => item.AdjustmentGuid, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().RequestedPunchTimeLocal.Date, StringComparer.OrdinalIgnoreCase);
            var scheduleGuids = GuidsOf("Overtime", "MissingClockOut");
            var scheduleDates = scheduleGuids.Count == 0
                ? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
                : (await _db.Queryable<AttendanceSchedule>().Where(item => scheduleGuids.Contains(item.ScheduleGuid)).ToListAsync())
                    .GroupBy(item => item.ScheduleGuid, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().WorkDate.Date, StringComparer.OrdinalIgnoreCase);
            var claimGuids = GuidsOf("MealBreak");
            var claimDates = claimGuids.Count == 0
                ? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
                : (await _db.Queryable<AttendanceMealClaim>().Where(item => claimGuids.Contains(item.ClaimGuid)).ToListAsync())
                    .ToDictionary(item => item.ClaimGuid, item => item.WorkDate.Date, StringComparer.OrdinalIgnoreCase);
            var leaveGuids = GuidsOf("Leave");
            var leaveRanges = leaveGuids.Count == 0
                ? new Dictionary<string, (DateTime, DateTime)>(StringComparer.OrdinalIgnoreCase)
                : (await _db.Queryable<AttendanceLeaveRequest>().Where(item => leaveGuids.Contains(item.LeaveGuid)).ToListAsync())
                    .GroupBy(item => item.LeaveGuid, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => (group.First().StartDate.Date, group.First().EndDate.Date), StringComparer.OrdinalIgnoreCase);
            var holidayPayGuids = GuidsOf(PublicHolidayPaySourceType);
            var holidayPayDates = holidayPayGuids.Count == 0
                ? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
                : (await _db.Queryable<AttendancePublicHolidayPay>().Where(item => holidayPayGuids.Contains(item.PayGuid)).ToListAsync())
                    .ToDictionary(item => item.PayGuid, item => item.HolidayDate.Date, StringComparer.OrdinalIgnoreCase);

            foreach (var approval in approvals)
            {
                DateTime? single = approval.SourceType switch
                {
                    "Punch" => punchDates.TryGetValue(approval.SourceGuid, out var d1) ? d1 : null,
                    "PunchAdjustment" => adjustmentDates.TryGetValue(approval.SourceGuid, out var d2) ? d2 : null,
                    "Overtime" or "MissingClockOut" => scheduleDates.TryGetValue(approval.SourceGuid, out var d3) ? d3 : null,
                    "MealBreak" => claimDates.TryGetValue(approval.SourceGuid, out var d4) ? d4 : null,
                    PublicHolidayPaySourceType => holidayPayDates.TryGetValue(approval.SourceGuid, out var d5) ? d5 : null,
                    _ => null,
                };
                if (single.HasValue)
                {
                    result[approval.ApprovalGuid] = (single.Value, single.Value);
                }
                else if (approval.SourceType == "Leave" && leaveRanges.TryGetValue(approval.SourceGuid, out var range))
                {
                    result[approval.ApprovalGuid] = range;
                }
            }
            return result;
        }

        // ---------------- 工时统计快照（工时统计与工资表共用） ----------------

        private sealed class PayrollEmployeeSnapshot
        {
            public string UserGuid { get; init; } = string.Empty;
            public string UserName { get; set; } = string.Empty;
            public string? EmploymentType { get; set; }
            public bool IsManager { get; set; }
            public int? AgeYears { get; set; }
            public DateTime? Birthday { get; set; }
            public int SegmentLimit { get; set; }
            public bool HasOtherStores { get; set; }
            public int PendingCount { get; set; }
            public SortedDictionary<DateTime, AttendancePayrollEmployeeDayDto> Days { get; } = new();
            /// <summary>本店（mergeStores 时含其他分店）计薪分钟，按天、档位。</summary>
            public List<PayrollWorkItem> WorkItems { get; } = new();
            public List<PayrollLeaveItem> SickItems { get; } = new();
            public List<PayrollLeaveItem> PublicHolidayPayItems { get; } = new();
            public SortedDictionary<DateTime, decimal> AnnualByDay { get; } = new();
            public SortedDictionary<DateTime, decimal> CashOutByDay { get; } = new();
            public bool HasAnyData { get; set; }
        }

        private sealed class PayrollPeriodSnapshot
        {
            public string StoreCode { get; init; } = string.Empty;
            public string? StoreName { get; init; }
            public DateTime PeriodStart { get; init; }
            public int Weeks { get; init; }
            public DateTime PeriodEnd => PeriodStart.AddDays(Weeks * 7 - 1);
            public AttendancePayPeriod? Period { get; init; }
            public string PeriodStatus => Period?.Status == PayPeriodStatusSubmitted ? PayPeriodStatusSubmitted : PayPeriodStatusOpen;
            public int PendingCount { get; set; }
            public Dictionary<DateTime, string> Holidays { get; init; } = new();
            public List<PayrollEmployeeSnapshot> Employees { get; } = new();
            public List<AttendancePayrollAlertDto> Alerts { get; } = new();
            public AttendanceSettings Settings { get; init; } = new();
        }

        /// <summary>某个排班的计薪结果与当天打卡段（工时统计格子展示用）。</summary>
        private sealed record SchedulePayResult(
            AttendanceSchedule Schedule,
            PayrollDayResult Pay,
            List<AttendancePayrollSegmentDto> Segments,
            bool MissingPunch = false,
            AttendancePayrollDayManualEntryDto? ManualEntry = null);

        /// <summary>门店本地日期的公共假期（IsPaidHoliday 或自动同步来的公共假期）：日期 → 名称。</summary>
        private async Task<Dictionary<DateTime, string>> LoadStorePublicHolidaysAsync(
            string storeCode,
            DateTime fromDate,
            DateTime toDate)
        {
            var from = fromDate.Date;
            var until = toDate.Date.AddDays(1);
            var rows = await _db.Queryable<AttendanceStoreHoliday>()
                .Where(item => !item.IsDeleted && item.StoreCode == storeCode && item.HolidayDate >= from && item.HolidayDate < until)
                .ToListAsync();
            return rows
                .Where(item => item.IsPaidHoliday
                    || item.Remark?.StartsWith("Auto public holiday sync", StringComparison.OrdinalIgnoreCase) == true)
                .GroupBy(item => item.HolidayDate.Date)
                .ToDictionary(group => group.Key, group => group.First().HolidayName);
        }

        /// <summary>
        /// 批量算排班的计薪分钟（规格第 3 节）：只计已闭合段与排班窗口的重叠 + 已批准加班 − 扣餐 + 已批准加回；
        /// includePending 时把待审的加班候选与用餐加回也算进去（仅预览）。
        /// </summary>
        private async Task<Dictionary<string, SchedulePayResult>> ComputeSchedulePayAsync(
            List<AttendanceSchedule> schedules,
            AttendanceSettings settings,
            bool includePending)
        {
            var result = new Dictionary<string, SchedulePayResult>(StringComparer.OrdinalIgnoreCase);
            if (schedules.Count == 0)
            {
                return result;
            }
            var scheduleGuids = schedules.Select(item => item.ScheduleGuid).Distinct().ToList();
            var punches = await _db.Queryable<AttendancePunch>()
                .Where(item => !item.IsDeleted && item.ScheduleGuid != null && scheduleGuids.Contains(item.ScheduleGuid))
                .ToListAsync();
            var overtimeApprovals = (await _db.Queryable<AttendanceApproval>()
                    .Where(item => !item.IsDeleted && item.SourceType == "Overtime" && scheduleGuids.Contains(item.SourceGuid))
                    .ToListAsync())
                .GroupBy(item => item.SourceGuid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            var mealClaims = (await _db.Queryable<AttendanceMealClaim>()
                    .Where(item => scheduleGuids.Contains(item.ScheduleGuid))
                    .ToListAsync())
                .GroupBy(item => item.ScheduleGuid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            var segmentLimitMap = await ResolveSegmentLimitMapAsync(schedules);
            var timeZones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var storeCode in schedules.Select(item => item.StoreCode).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                timeZones[storeCode] = await ResolveStoreTimeZoneAsync(storeCode, null);
            }
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

            foreach (var schedule in schedules)
            {
                var nowLocal = ConvertUtcToStoreLocal(nowUtc, timeZones.GetValueOrDefault(schedule.StoreCode, DefaultStoreTimeZone));
                var session = AttendanceWorkSessionCalculator.Calculate(
                    schedule,
                    punches,
                    segmentLimitMap.GetValueOrDefault(schedule.UserGuid, ResolveEmployeeSegmentLimit(settings)),
                    nowLocal,
                    settings.EarlyLeaveGraceMinutes,
                    settings.LateGraceMinutes,
                    settings.OvertimeThresholdMinutes);
                var segments = session.Segments
                    .Where(item => item.ClockIn != null && item.ClockOut != null)
                    .Select(item => new PayrollSegment(
                        item.ClockIn!.PunchTimeLocal,
                        item.ClockOut!.PunchTimeLocal,
                        item.ClockOut.IsSystemAutoClockOut))
                    .ToList();
                var approvals = overtimeApprovals.GetValueOrDefault(schedule.ScheduleGuid) ?? new List<AttendanceApproval>();
                var overtime = Math.Min(
                    session.CandidateOvertimeMinutes,
                    approvals.Where(item => item.ReviewStatus == "Approved").Sum(item => item.ApprovedOvertimeMinutes ?? 0));
                var claims = mealClaims.GetValueOrDefault(schedule.ScheduleGuid) ?? new List<AttendanceMealClaim>();
                var addBack = claims
                    .Where(item => item.Status == AttendanceMealBreakRules.ClaimStatusApproved)
                    .Sum(item => item.ApprovedMinutes ?? 0);
                if (includePending)
                {
                    overtime = Math.Min(
                        session.CandidateOvertimeMinutes,
                        overtime + approvals.Where(item => item.ReviewStatus == "Pending").Sum(item => item.CandidateOvertimeMinutes ?? 0));
                    addBack += claims
                        .Where(item => item.Status == AttendanceMealBreakRules.ClaimStatusPending)
                        .Sum(item => item.ClaimedMinutes);
                }
                var windowStart = schedule.WorkDate.Date.Add(schedule.StartTime);
                var windowEnd = schedule.WorkDate.Date.Add(schedule.EndTime);
                if (windowEnd <= windowStart)
                {
                    windowEnd = windowEnd.AddDays(1);
                }
                var pay = PayrollCalculator.CalculateDay(new PayrollDayInput(
                    windowStart,
                    windowEnd,
                    segments,
                    AttendanceMealBreakRules.EffectiveCount(schedule.MealBreakCount, schedule.StartTime, schedule.EndTime),
                    overtime,
                    addBack));
                var segmentDtos = session.Segments
                    .Where(item => item.ClockIn != null)
                    .Select(item => new AttendancePayrollSegmentDto
                    {
                        Start = item.ClockIn!.PunchTimeLocal.ToString("HH:mm", CultureInfo.InvariantCulture),
                        End = item.ClockOut == null
                            ? null
                            : item.ClockOut.IsSystemAutoClockOut
                                ? "24:00"
                                : item.ClockOut.PunchTimeLocal.ToString("HH:mm", CultureInfo.InvariantCulture),
                        IsAutoClockOut = item.ClockOut?.IsSystemAutoClockOut == true,
                    })
                    .ToList();
                // 未打卡班：排班窗口已过去却一次打卡都没有（目前按 0 工时计），页面据此提示并可补录。
                var schedulePunches = punches.Where(item => item.ScheduleGuid == schedule.ScheduleGuid).ToList();
                var missingPunch = schedulePunches.Count == 0 && windowEnd <= nowLocal;
                // 手动补录留痕：补录打卡的 Source 是 ManualEntry，原因在打卡备注里。
                var manualPunches = schedulePunches.Where(item => item.Source == ManualEntryPunchSource).ToList();
                var manualEntry = manualPunches.Count == 0
                    ? null
                    : new AttendancePayrollDayManualEntryDto
                    {
                        Reason = manualPunches[0].Remark,
                        CreatedBy = manualPunches[0].CreatedBy,
                        CreatedAt = manualPunches.Min(item => item.CreatedAt),
                        CreatedSchedule = schedule.Remark?.StartsWith(ManualEntryScheduleRemarkPrefix, StringComparison.Ordinal) == true,
                    };
                result[schedule.ScheduleGuid] = new SchedulePayResult(schedule, pay, segmentDtos, missingPunch, manualEntry);
            }
            return result;
        }

        /// <summary>补录建的假（已批准且备注带「补录：」前缀）的留痕；员工自己申请的返回 null。</summary>
        private static AttendancePayrollDayManualEntryDto? ManualLeaveTrace(AttendanceLeaveRequest leave) =>
            IsManualLeave(leave)
                ? new AttendancePayrollDayManualEntryDto
                {
                    Reason = leave.Reason,
                    CreatedBy = leave.CreatedBy,
                    CreatedAt = leave.CreatedAt,
                }
                : null;

        /// <summary>病假当天带薪工时 = 排班工时扣餐（按排班时长推算的用餐次数全扣）。</summary>
        internal static decimal SickLeaveHoursForSchedule(AttendanceSchedule schedule)
        {
            var duration = AttendanceMealBreakRules.ScheduledDurationMinutes(schedule.StartTime, schedule.EndTime);
            var effective = AttendanceMealBreakRules.EffectiveCount(schedule.MealBreakCount, schedule.StartTime, schedule.EndTime);
            return PayrollCalculator.RoundHours(
                Math.Max(0, duration - AttendanceMealBreakRules.DeductionMinutes(effective, duration)));
        }

        private async Task<PayrollPeriodSnapshot> BuildPayrollSnapshotAsync(
            string storeCode,
            DateTime periodStart,
            int weeks,
            bool includePending,
            bool mergeStores,
            bool onlyWithHours = false)
        {
            var settings = await GetOrCreateSettingsModelAsync();
            var store = await _db.Queryable<Store>().FirstAsync(item => !item.IsDeleted && item.StoreCode == storeCode);
            var periodEnd = periodStart.AddDays(weeks * 7 - 1);
            var until = periodEnd.AddDays(1);
            var period = await _db.Queryable<AttendancePayPeriod>()
                .FirstAsync(item => item.StoreCode == storeCode && item.PeriodStart == periodStart && item.Weeks == weeks);
            var snapshot = new PayrollPeriodSnapshot
            {
                StoreCode = storeCode,
                StoreName = store?.StoreName,
                PeriodStart = periodStart,
                Weeks = weeks,
                Period = period,
                Holidays = await LoadStorePublicHolidaysAsync(storeCode, periodStart, periodEnd),
                Settings = settings,
            };
            var publicHolidaySet = snapshot.Holidays.Keys.ToHashSet();

            var schedules = await _db.Queryable<AttendanceSchedule>()
                .Where(item => !item.IsDeleted && item.Status == "Active" && item.StoreCode == storeCode
                    && item.WorkDate >= periodStart && item.WorkDate < until)
                .ToListAsync();
            var leaves = await _db.Queryable<AttendanceLeaveRequest>()
                .Where(item => !item.IsDeleted && item.StoreCode == storeCode && item.Status == "Approved"
                    && item.StartDate < until && item.EndDate >= periodStart)
                .ToListAsync();
            var holidayPays = await _db.Queryable<AttendancePublicHolidayPay>()
                .Where(item => item.StoreCode == storeCode && item.HolidayDate >= periodStart && item.HolidayDate < until
                    && item.Status != PublicHolidayPayStatusCancelled)
                .ToListAsync();
            var holidayPayApprovalGuids = await LoadPublicHolidayPayApprovalGuidsAsync(holidayPays.Select(item => item.PayGuid).ToList());
            var pendingApprovals = await _db.Queryable<AttendanceApproval>()
                .Where(item => !item.IsDeleted && item.StoreCode == storeCode && item.ReviewStatus == "Pending")
                .ToListAsync();
            var approvedApprovals = await _db.Queryable<AttendanceApproval>()
                .Where(item => !item.IsDeleted && item.StoreCode == storeCode && item.ReviewStatus == "Approved"
                    && item.ReviewedAt >= periodStart.AddDays(-1))
                .ToListAsync();
            var approvalDates = await ResolveApprovalDateRangesAsync(pendingApprovals.Concat(approvedApprovals).ToList());

            var boundUsers = await LoadStoreUsersAsync(storeCode);
            var userGuids = boundUsers.Keys
                .Concat(schedules.Select(item => item.UserGuid))
                .Concat(leaves.Select(item => item.UserGuid))
                .Concat(holidayPays.Select(item => item.UserGuid))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var names = await ResolveUserDisplayNamesAsync(userGuids);
            var profiles = (await _db.Queryable<EmployeeProfile>()
                    .Where(item => !item.IsDeleted && userGuids.Contains(item.UserGUID))
                    .ToListAsync())
                .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var minorBirthdays = (await _db.Queryable<EmployeeMinorCompliance>()
                    .Where(item => !item.IsDeleted && userGuids.Contains(item.UserGUID))
                    .ToListAsync())
                .Where(item => item.DateOfBirth.HasValue)
                .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().DateOfBirth!.Value, StringComparer.OrdinalIgnoreCase);
            var segmentLimits = await ResolveSegmentLimitMapAsync(
                userGuids.Select(item => new AttendanceSchedule { UserGuid = item }).ToList());
            var managerLimit = ResolveManagerSegmentLimit(settings);
            var unavailable = await _db.Queryable<AttendanceAvailability>()
                .Where(item => !item.IsDeleted && item.Status == "Active" && item.IsUnavailable && item.StoreCode == storeCode
                    && item.AvailableDate >= periodStart && item.AvailableDate < until && userGuids.Contains(item.UserGuid))
                .ToListAsync();

            var schedulePay = await ComputeSchedulePayAsync(schedules, settings, includePending);

            // 其他分店：同一批员工本期在别的店的已发布排班，算出工时用于角标；mergeStores 时并进本表。
            var otherSchedules = userGuids.Count == 0
                ? new List<AttendanceSchedule>()
                : await _db.Queryable<AttendanceSchedule>()
                    .Where(item => !item.IsDeleted && item.Status == "Active" && item.StoreCode != storeCode
                        && item.WorkDate >= periodStart && item.WorkDate < until && userGuids.Contains(item.UserGuid))
                    .ToListAsync();
            var otherPay = await ComputeSchedulePayAsync(otherSchedules, settings, includePending);
            var otherStoreCodes = otherSchedules.Select(item => item.StoreCode).Distinct().ToList();
            var otherStoreNames = otherStoreCodes.Count == 0
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : (await _db.Queryable<Store>().Where(item => otherStoreCodes.Contains(item.StoreCode)).ToListAsync())
                    .GroupBy(item => item.StoreCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().StoreName ?? group.Key, StringComparer.OrdinalIgnoreCase);

            var employeeMap = new Dictionary<string, PayrollEmployeeSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var userGuid in userGuids)
            {
                profiles.TryGetValue(userGuid, out var profile);
                var birthday = profile?.Birthday ?? (minorBirthdays.TryGetValue(userGuid, out var dob) ? dob : null);
                var limit = segmentLimits.GetValueOrDefault(userGuid, ResolveEmployeeSegmentLimit(settings));
                var employee = new PayrollEmployeeSnapshot
                {
                    UserGuid = userGuid,
                    UserName = names.GetValueOrDefault(userGuid) ?? userGuid,
                    EmploymentType = ToEmploymentTypeCode(profile?.EmployeeType),
                    IsManager = limit == managerLimit && managerLimit != ResolveEmployeeSegmentLimit(settings),
                    AgeYears = ResolveMinorAge(birthday, periodEnd),
                    Birthday = birthday?.Date,
                    SegmentLimit = limit,
                    HasAnyData = profile?.EmployeeType != null,
                };
                for (var day = periodStart; day <= periodEnd; day = day.AddDays(1))
                {
                    employee.Days[day] = new AttendancePayrollEmployeeDayDto
                    {
                        Date = day,
                        IsPublicHoliday = snapshot.Holidays.ContainsKey(day),
                        HolidayName = snapshot.Holidays.GetValueOrDefault(day),
                        IsBirthday = birthday.HasValue && birthday.Value.Month == day.Month && birthday.Value.Day == day.Day,
                        IsUnavailable = unavailable.Any(item =>
                            item.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase) && item.AvailableDate.Date == day),
                    };
                }
                employeeMap[userGuid] = employee;
            }

            foreach (var pay in schedulePay.Values)
            {
                if (!employeeMap.TryGetValue(pay.Schedule.UserGuid, out var employee)
                    || !employee.Days.TryGetValue(pay.Schedule.WorkDate.Date, out var day))
                {
                    continue;
                }
                employee.HasAnyData = true;
                var bucket = PayrollCalculator.ResolveTier(day.Date, day.IsPublicHoliday);
                AddPaidMinutes(day, bucket, pay.Pay.PaidMinutes);
                day.HasSchedule = true;
                day.IsScheduled = true;
                day.Segments.AddRange(pay.Segments);
                if (pay.MissingPunch)
                {
                    day.MissingPunch = true;
                    day.ScheduleStart = pay.Schedule.StartTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                    day.ScheduleEnd = pay.Schedule.EndTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                }
                day.ManualEntry ??= pay.ManualEntry;
                employee.WorkItems.Add(new PayrollWorkItem(day.Date, bucket, pay.Pay.PaidMinutes));
            }

            foreach (var pay in otherPay.Values)
            {
                if (!employeeMap.TryGetValue(pay.Schedule.UserGuid, out var employee)
                    || !employee.Days.TryGetValue(pay.Schedule.WorkDate.Date, out var day))
                {
                    continue;
                }
                employee.HasOtherStores = true;
                var existing = day.OtherStores.FirstOrDefault(item => item.StoreCode.Equals(pay.Schedule.StoreCode, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new AttendancePayrollOtherStoreHoursDto
                    {
                        StoreCode = pay.Schedule.StoreCode,
                        StoreName = otherStoreNames.GetValueOrDefault(pay.Schedule.StoreCode),
                    };
                    day.OtherStores.Add(existing);
                }
                existing.Hours = PayrollCalculator.Round2(existing.Hours + PayrollCalculator.RoundHours(pay.Pay.PaidMinutes));
                if (mergeStores)
                {
                    var bucket = PayrollCalculator.ResolveTier(day.Date, day.IsPublicHoliday);
                    AddPaidMinutes(day, bucket, pay.Pay.PaidMinutes);
                    employee.WorkItems.Add(new PayrollWorkItem(day.Date, bucket, pay.Pay.PaidMinutes));
                }
            }

            // 请假：病假按所选排班的「排班工时扣餐」；年假休息 / 发放按区间内工作日（排除公假）× 8 h；旧的无薪类型只标记。
            var sickScheduleGuids = leaves
                .Where(item => item.LeaveType == PaidSickLeaveType)
                .SelectMany(item => ParseJsonStringList(item.ScheduleGuids))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var sickSchedules = sickScheduleGuids.Count == 0
                ? new Dictionary<string, AttendanceSchedule>(StringComparer.OrdinalIgnoreCase)
                : (await _db.Queryable<AttendanceSchedule>().Where(item => sickScheduleGuids.Contains(item.ScheduleGuid)).ToListAsync())
                    .ToDictionary(item => item.ScheduleGuid, StringComparer.OrdinalIgnoreCase);
            foreach (var leave in leaves)
            {
                if (!employeeMap.TryGetValue(leave.UserGuid, out var employee))
                {
                    continue;
                }
                employee.HasAnyData = true;
                var hasCertificate = ParseAttachmentUrls(leave).Count > 0;
                if (leave.LeaveType == PaidSickLeaveType)
                {
                    var perDay = ParseJsonStringList(leave.ScheduleGuids)
                        .Select(guid => sickSchedules.GetValueOrDefault(guid))
                        .Where(item => item != null)
                        .GroupBy(item => item!.WorkDate.Date)
                        .ToDictionary(group => group.Key, group => group.Sum(item => SickLeaveHoursForSchedule(item!)));
                    if (perDay.Count == 0 && leave.PaidHours is { } total)
                    {
                        perDay[leave.StartDate.Date] = total;
                    }
                    foreach (var (date, hours) in perDay)
                    {
                        if (employee.Days.TryGetValue(date, out var day))
                        {
                            day.Leave = new AttendancePayrollDayLeaveDto
                            {
                                Type = leave.LeaveType,
                                Hours = hours,
                                HasCertificate = hasCertificate,
                                LeaveGuid = leave.LeaveGuid,
                                ManualEntry = ManualLeaveTrace(leave),
                            };
                            // 这天的班已经用病假处理了，不再算「有排班没打卡」（否则补录后还会挂在未打卡提示里）。
                            day.MissingPunch = false;
                            employee.SickItems.Add(new PayrollLeaveItem(date, hours));
                        }
                    }
                    continue;
                }

                var isAnnual = leave.LeaveType is PaidAnnualLeaveType or AnnualCashOutLeaveType;
                // 年假的合计小时数摊到整段区间的工作日（区间可能跨出本期，公共假期要按整段区间取，才能和保存时算的一致）。
                // 老数据合计 = 工作日 × 8，摊出来仍是每天 8。
                IReadOnlyDictionary<DateTime, decimal> annualSpread = new Dictionary<DateTime, decimal>();
                if (isAnnual)
                {
                    var leaveStart = leave.StartDate.Date;
                    var leaveEnd = leave.EndDate.Date;
                    var leaveHolidays = leaveStart >= periodStart && leaveEnd <= periodEnd
                        ? publicHolidaySet
                        : (await LoadStorePublicHolidaysAsync(storeCode, leaveStart, leaveEnd)).Keys.ToHashSet();
                    annualSpread = PayrollCalculator.SpreadAnnualLeaveHours(leaveStart, leaveEnd, leaveHolidays, leave.PaidHours);
                }
                for (var date = Max(leave.StartDate.Date, periodStart); date <= Min(leave.EndDate.Date, periodEnd); date = date.AddDays(1))
                {
                    var day = employee.Days[date];
                    var hours = annualSpread.GetValueOrDefault(date);
                    // 年假发放照常上班：当天已有病假 / 年假标记时不覆盖，只另计工时。
                    day.Leave ??= new AttendancePayrollDayLeaveDto
                    {
                        Type = leave.LeaveType,
                        Hours = hours,
                        HasCertificate = hasCertificate,
                        LeaveGuid = leave.LeaveGuid,
                        ManualEntry = ManualLeaveTrace(leave),
                    };
                    // 带薪年假休息的工作日同样不再算「有排班没打卡」；年假发放、补录时选了「不休息」的年假照常上班，不动。
                    if (leave.LeaveType == PaidAnnualLeaveType && hours > 0 && !IsManualNoRestAnnual(leave))
                    {
                        day.MissingPunch = false;
                    }
                    if (hours > 0)
                    {
                        var target = leave.LeaveType == PaidAnnualLeaveType ? employee.AnnualByDay : employee.CashOutByDay;
                        target[date] = target.GetValueOrDefault(date) + hours;
                    }
                }
            }

            foreach (var holidayPay in holidayPays)
            {
                if (!employeeMap.TryGetValue(holidayPay.UserGuid, out var employee)
                    || !employee.Days.TryGetValue(holidayPay.HolidayDate.Date, out var day))
                {
                    continue;
                }
                employee.HasAnyData = true;
                day.PublicHolidayPay = new AttendancePayrollDayPublicHolidayPayDto
                {
                    State = holidayPay.Status,
                    Hours = holidayPay.Status == PublicHolidayPayStatusConfirmed ? holidayPay.Hours : holidayPay.SuggestedHours,
                    Count = holidayPay.OrdinaryCount,
                    ApprovalGuid = holidayPayApprovalGuids.GetValueOrDefault(holidayPay.PayGuid),
                };
                if (holidayPay.Status == PublicHolidayPayStatusConfirmed && holidayPay.Hours is > 0)
                {
                    employee.PublicHolidayPayItems.Add(new PayrollLeaveItem(day.Date, holidayPay.Hours.Value));
                }
                if (holidayPay.Status == PublicHolidayPayStatusPending)
                {
                    snapshot.Alerts.Add(new AttendancePayrollAlertDto
                    {
                        Type = "PublicHolidayPay",
                        UserGuid = employee.UserGuid,
                        UserName = employee.UserName,
                        EmploymentType = employee.EmploymentType,
                        Date = day.Date,
                        HolidayName = day.HolidayName,
                        OrdinaryCount = holidayPay.OrdinaryCount,
                        LookbackWeeks = holidayPay.LookbackWeeks,
                        MinCount = settings.PublicHolidayMinCount,
                        Hours = holidayPay.SuggestedHours,
                        ApprovalGuid = day.PublicHolidayPay.ApprovalGuid,
                    });
                }
            }

            // 待处理 / 已批准角标：审批按来源映射到日期；本期内的待处理计入提交前必须清零的数量。
            foreach (var approval in pendingApprovals)
            {
                if (!approvalDates.TryGetValue(approval.ApprovalGuid, out var range)
                    || range.To < periodStart || range.From > periodEnd)
                {
                    continue;
                }
                snapshot.PendingCount++;
                if (!employeeMap.TryGetValue(approval.ApplicantUserGuid, out var employee))
                {
                    continue;
                }
                employee.PendingCount++;
                employee.HasAnyData = true;
                for (var date = Max(range.From, periodStart); date <= Min(range.To, periodEnd); date = date.AddDays(1))
                {
                    if (!employee.Days[date].Pending.Contains(approval.SourceType))
                    {
                        employee.Days[date].Pending.Add(approval.SourceType);
                    }
                }
            }
            foreach (var approval in approvedApprovals)
            {
                if (!approvalDates.TryGetValue(approval.ApprovalGuid, out var range)
                    || range.To < periodStart || range.From > periodEnd
                    || !employeeMap.TryGetValue(approval.ApplicantUserGuid, out var employee))
                {
                    continue;
                }
                for (var date = Max(range.From, periodStart); date <= Min(range.To, periodEnd); date = date.AddDays(1))
                {
                    if (!employee.Days[date].ApprovedMarks.Contains(approval.SourceType))
                    {
                        employee.Days[date].ApprovedMarks.Add(approval.SourceType);
                    }
                }
            }

            var roster = employeeMap.Values
                .Where(item => item.HasAnyData || boundUsers.ContainsKey(item.UserGuid) && item.EmploymentType != null)
                .OrderByDescending(item => item.IsManager)
                .ThenBy(item => item.UserName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            // 工时统计页只列「本期有工时」的人（含带薪假工时；有待处理项的也列，否则店长找不到要处理的人）。
            // 只有工时统计页传 true；工资表、提交锁定、MYOB 推送仍用完整名单（零工时的人可能有固定转账工时 / 津贴，不能漏）。
            snapshot.Employees.AddRange(onlyWithHours ? roster.Where(HasHoursOrPending) : roster);

            var otherStoreItems = snapshot.Employees
                .SelectMany(employee => employee.Days.Values.SelectMany(day => day.OtherStores.Select(other => new AttendancePayrollOtherStoreItemDto
                {
                    UserGuid = employee.UserGuid,
                    UserName = employee.UserName,
                    Date = day.Date,
                    StoreCode = other.StoreCode,
                    StoreName = other.StoreName,
                    Hours = other.Hours,
                })))
                .ToList();
            if (otherStoreItems.Count > 0)
            {
                snapshot.Alerts.Insert(0, new AttendancePayrollAlertDto { Type = "MultiStore", OtherStoreItems = otherStoreItems });
            }

            // 生日提醒：生日落在本期内，或在今天起 BirthdayRemindDays 天内。
            var storeToday = ConvertUtcToStoreLocal(
                _timeProvider.GetUtcNow().UtcDateTime,
                await ResolveStoreTimeZoneAsync(storeCode, null)).Date;
            // 按完整名单出：这是提前提醒核对时薪，不能因为这一期没工时就不提醒。
            foreach (var employee in roster.Where(item => item.Birthday.HasValue))
            {
                var birthdayDate = employee.Days.Values.FirstOrDefault(item => item.IsBirthday)?.Date
                    ?? NextBirthday(employee.Birthday!.Value, storeToday, Math.Max(0, settings.BirthdayRemindDays));
                if (birthdayDate.HasValue)
                {
                    var ageOnBirthday = birthdayDate.Value.Year - employee.Birthday!.Value.Year;
                    snapshot.Alerts.Add(new AttendancePayrollAlertDto
                    {
                        Type = "Birthday",
                        UserGuid = employee.UserGuid,
                        UserName = employee.UserName,
                        Date = birthdayDate,
                        AgeYears = ageOnBirthday,
                        // 生日当天未满 21 岁 = 未成年（合规文案用）。
                        IsMinor = ageOnBirthday < JuniorAgeLimit,
                        // 青少年时薪按年龄逐年变，到满 21 岁那天转成人档：所以满 21 岁也要提示核对时薪（IsMinor 此时已是 false）。
                        RateReview = ageOnBirthday <= JuniorAgeLimit,
                    });
                }
            }
            return snapshot;
        }

        /// <summary>本期有工时（含病假 / 年假 / 带薪公假工时，与工时表合计同口径），或有待处理项。</summary>
        private static bool HasHoursOrPending(PayrollEmployeeSnapshot employee) =>
            employee.PendingCount > 0
            || BuildTotals(employee).Total > 0
            // 只有「未打卡班」的人也要列出来，否则店长看不到该补录的人。
            || employee.Days.Values.Any(day => day.MissingPunch);

        /// <summary>生日已过多少天内仍提示「时薪没按新年龄更新」；超过就不再提示（避免历史生日永远挂着）。</summary>
        internal const int BirthdayRateReviewLookbackDays = 60;

        /// <summary>
        /// 生日后「该核对时薪」的判定（纯规则，便于单测）：
        /// 最近一次已过的生日，生日当天年龄 ≤ <see cref="JuniorAgeLimit"/>（含刚满 21 岁——青少年档转成人档就在这天），
        /// 且在 <see cref="BirthdayRateReviewLookbackDays"/> 天内，而当前生效的时薪记录早于那次生日（= 还没按新年龄更新）。
        /// 生日是 2 月 29 日时，非闰年按 2 月 28 日算。返回那次生日的日期与当天年龄；不需要核对返回 null。
        /// </summary>
        internal static (DateTime Date, int Age)? ResolveBirthdayRateReview(DateTime birthday, DateTime today, DateTime currentRateEffectiveFrom)
        {
            if (birthday.Date > today.Date)
            {
                return null;
            }
            DateTime OnYear(int year) =>
                new(year, birthday.Month, Math.Min(birthday.Day, DateTime.DaysInMonth(year, birthday.Month)));

            var last = OnYear(today.Year);
            if (last > today.Date)
            {
                last = OnYear(today.Year - 1);
            }
            var age = last.Year - birthday.Year;
            if (age <= 0 || age > JuniorAgeLimit)
            {
                return null;
            }
            if (last <= today.Date.AddDays(-BirthdayRateReviewLookbackDays))
            {
                return null;
            }
            // 已有生效日 ≥ 这次生日的记录 = 已按新年龄更新过
            return currentRateEffectiveFrom.Date < last ? (last, age) : null;
        }

        private static DateTime? NextBirthday(DateTime birthday, DateTime today, int withinDays)
        {
            for (var offset = 0; offset <= withinDays; offset++)
            {
                var date = today.AddDays(offset);
                if (date.Month == birthday.Month && date.Day == birthday.Day)
                {
                    return date;
                }
            }
            return null;
        }

        private static void AddPaidMinutes(AttendancePayrollEmployeeDayDto day, PayrollBucket bucket, int minutes)
        {
            day.PaidMinutes = (day.PaidMinutes ?? 0) + minutes;
            day.PaidHours = PayrollCalculator.RoundHours(day.PaidMinutes.Value);
            var hours = PayrollCalculator.RoundHours(minutes);
            switch (bucket)
            {
                case PayrollBucket.Saturday: day.ByTier.Saturday += hours; break;
                case PayrollBucket.Sunday: day.ByTier.Sunday += hours; break;
                case PayrollBucket.PublicHoliday: day.ByTier.PublicHoliday += hours; break;
                default: day.ByTier.Weekday += hours; break;
            }
        }

        private static DateTime Max(DateTime left, DateTime right) => left > right ? left : right;
        private static DateTime Min(DateTime left, DateTime right) => left < right ? left : right;

        internal static string? ToEmploymentTypeCode(EmployeeType? type) => type switch
        {
            EmployeeType.FullTime => "FullTime",
            EmployeeType.PartTime => "PartTime",
            EmployeeType.Temporary => "Casual",
            _ => null,
        };

        /// <summary>绑定该店的有效账号：UserGuid → 是否主分店。</summary>
        private async Task<Dictionary<string, bool>> LoadStoreUsersAsync(string storeCode)
        {
            var rows = await _db.Queryable<User>()
                .InnerJoin<UserStore>((u, us) => u.UserGUID == us.UserGUID)
                .InnerJoin<Store>((u, us, s) => us.StoreGUID == s.StoreGUID)
                .Where((u, us, s) => !u.IsDeleted && !us.IsDeleted && !s.IsDeleted && s.StoreCode == storeCode)
                .Select((u, us, s) => new { u.UserGUID, us.IsPrimary })
                .ToListAsync();
            return rows
                .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Any(item => item.IsPrimary), StringComparer.OrdinalIgnoreCase);
        }

        private static AttendancePayrollHoursTotalsDto BuildTotals(PayrollEmployeeSnapshot employee)
        {
            // 按分钟合计再舍 2 位小数，不是把每天的小时相加。
            int Minutes(PayrollBucket bucket) => employee.WorkItems.Where(item => item.Bucket == bucket).Sum(item => item.Minutes);
            var sick = PayrollCalculator.Round2(employee.SickItems.Sum(item => item.Hours));
            var annual = PayrollCalculator.Round2(employee.AnnualByDay.Values.Sum());
            var cashOut = PayrollCalculator.Round2(employee.CashOutByDay.Values.Sum());
            var publicHolidayPay = PayrollCalculator.Round2(employee.PublicHolidayPayItems.Sum(item => item.Hours));
            var totals = new AttendancePayrollHoursTotalsDto
            {
                Weekday = PayrollCalculator.RoundHours(Minutes(PayrollBucket.Weekday)),
                Saturday = PayrollCalculator.RoundHours(Minutes(PayrollBucket.Saturday)),
                Sunday = PayrollCalculator.RoundHours(Minutes(PayrollBucket.Sunday)),
                PublicHoliday = PayrollCalculator.RoundHours(Minutes(PayrollBucket.PublicHoliday)),
                Sick = sick,
                Annual = annual,
                CashOut = cashOut,
                PublicHolidayPay = publicHolidayPay,
                Leave = PayrollCalculator.Round2(sick + annual + cashOut + publicHolidayPay),
            };
            totals.Total = PayrollCalculator.Round2(
                totals.Weekday + totals.Saturday + totals.Sunday + totals.PublicHoliday + totals.Leave);
            return totals;
        }

        private async Task<(ApiResponse<T>? Error, string StoreCode, DateTime PeriodStart, int Weeks, AttendanceSettings Settings)>
            ResolvePayrollQueryAsync<T>(string? storeCode, DateTime? periodStart, int? weeks)
        {
            var settings = await GetOrCreateSettingsModelAsync();
            if (string.IsNullOrWhiteSpace(storeCode))
            {
                return (ApiResponse<T>.Error("分店代码不能为空", "STORE_REQUIRED"), string.Empty, default, 0, settings);
            }
            var code = storeCode.Trim();
            var access = await ResolveManagedStoreAccessAsync(code);
            if (!access.Success)
            {
                return (ApiResponse<T>.Error(access.Message, access.ErrorCode), code, default, 0, settings);
            }
            var resolvedWeeks = ResolvePeriodWeeks(weeks, settings);
            var anchorDate = periodStart?.Date ?? ConvertUtcToStoreLocal(
                _timeProvider.GetUtcNow().UtcDateTime,
                await ResolveStoreTimeZoneAsync(code, null)).Date;
            return (null, code, ResolvePayPeriodStart(anchorDate, resolvedWeeks, settings.FortnightAnchorDate), resolvedWeeks, settings);
        }

        /// <summary>写接口的期参数必须是一个合法的期起点（1 周＝周一，2 周＝与双周起始日对齐）。</summary>
        private async Task<(ApiResponse<T>? Error, string StoreCode)> ValidatePayPeriodKeyAsync<T>(AttendancePayPeriodKeyDto key)
        {
            if (string.IsNullOrWhiteSpace(key.StoreCode))
            {
                return (ApiResponse<T>.Error("分店代码不能为空", "STORE_REQUIRED"), string.Empty);
            }
            var code = key.StoreCode.Trim();
            var access = await ResolveManagedStoreAccessAsync(code);
            if (!access.Success)
            {
                return (ApiResponse<T>.Error(access.Message, access.ErrorCode), code);
            }
            var settings = await GetOrCreateSettingsModelAsync();
            if (key.Weeks is not 1 and not 2
                || key.PeriodStart == default
                || ResolvePayPeriodStart(key.PeriodStart.Date, key.Weeks, settings.FortnightAnchorDate) != key.PeriodStart.Date)
            {
                return (ApiResponse<T>.Error("发薪周期起始日或周数无效", "INVALID_PERIOD"), code);
            }
            return (null, code);
        }

        // ---------------- 接口 ----------------

        public async Task<ApiResponse<AttendancePayrollHoursDto>> GetPayrollHoursAsync(AttendancePayrollQueryDto query)
        {
            var (error, storeCode, periodStart, weeks, _) =
                await ResolvePayrollQueryAsync<AttendancePayrollHoursDto>(query.StoreCode, query.PeriodStart, query.Weeks);
            if (error != null)
            {
                return error;
            }
            // 带薪公假确认在店长打开工时统计时生成；失败只记日志，不阻断统计。
            await GeneratePublicHolidayPayApprovalsAsync(storeCode, periodStart, periodStart.AddDays(weeks * 7 - 1));
            var snapshot = await BuildPayrollSnapshotAsync(
                storeCode, periodStart, weeks, query.IncludePending, query.MergeStores, onlyWithHours: true);
            var result = new AttendancePayrollHoursDto
            {
                StoreCode = storeCode,
                StoreName = snapshot.StoreName,
                PeriodStart = periodStart,
                PeriodEnd = snapshot.PeriodEnd,
                Weeks = weeks,
                WeekNumber = ISOWeek.GetWeekOfYear(periodStart),
                PeriodStatus = snapshot.PeriodStatus,
                PendingCount = snapshot.PendingCount,
                IncludePending = query.IncludePending,
                MergeStores = query.MergeStores,
                Alerts = snapshot.Alerts,
            };
            for (var day = periodStart; day <= snapshot.PeriodEnd; day = day.AddDays(1))
            {
                result.Days.Add(new AttendancePayrollPeriodDayDto
                {
                    Date = day,
                    IsoWeekday = ((int)day.DayOfWeek + 6) % 7 + 1,
                    IsPublicHoliday = snapshot.Holidays.ContainsKey(day),
                    HolidayName = snapshot.Holidays.GetValueOrDefault(day),
                });
            }
            foreach (var employee in snapshot.Employees)
            {
                var totals = BuildTotals(employee);
                result.Employees.Add(new AttendancePayrollHoursEmployeeDto
                {
                    UserGuid = employee.UserGuid,
                    UserName = employee.UserName,
                    EmploymentType = employee.EmploymentType,
                    IsManager = employee.IsManager,
                    IsStoreManager = employee.IsManager,
                    AgeYears = employee.AgeYears,
                    Birthday = employee.Birthday,
                    SegmentLimit = employee.SegmentLimit,
                    HasOtherStores = employee.HasOtherStores,
                    PendingCount = employee.PendingCount,
                    LeaveHours = totals.Leave,
                    Days = employee.Days.Values.ToList(),
                    Totals = totals,
                });
            }
            var all = result.Employees.Select(item => item.Totals).ToList();
            result.Totals = new AttendancePayrollHoursTotalsDto
            {
                Weekday = all.Sum(item => item.Weekday),
                Saturday = all.Sum(item => item.Saturday),
                Sunday = all.Sum(item => item.Sunday),
                PublicHoliday = all.Sum(item => item.PublicHoliday),
                Leave = all.Sum(item => item.Leave),
                Total = all.Sum(item => item.Total),
                Sick = all.Sum(item => item.Sick),
                Annual = all.Sum(item => item.Annual),
                CashOut = all.Sum(item => item.CashOut),
                PublicHolidayPay = all.Sum(item => item.PublicHolidayPay),
            };
            return ApiResponse<AttendancePayrollHoursDto>.OK(result);
        }

        public async Task<ApiResponse<AttendancePayrollPayDto>> GetPayrollPayAsync(AttendancePayrollQueryDto query)
        {
            var (error, storeCode, periodStart, weeks, _) =
                await ResolvePayrollQueryAsync<AttendancePayrollPayDto>(query.StoreCode, query.PeriodStart, query.Weeks);
            if (error != null)
            {
                return error;
            }
            // 工资表永远只计已审核（忽略 includePending）。
            var snapshot = await BuildPayrollSnapshotAsync(storeCode, periodStart, weeks, includePending: false, query.MergeStores);
            // 工资表只列本期「有记录」的人：整行全是「—」的账号（没工时、没津贴、没年假申请、没备注）没有信息量，
            // 反而把真正要看的人挤到下面。只在这里过滤：提交锁定、MYOB 推送走 snapshot，仍用完整名单，不会漏人。
            var rows = (await BuildPayRowsAsync(snapshot)).Where(HasPayRecord).ToList();
            var submittedByName = snapshot.Period?.SubmittedBy == null
                ? null
                : (await ResolveUserDisplayNamesAsync(new[] { snapshot.Period.SubmittedBy })).GetValueOrDefault(snapshot.Period.SubmittedBy);
            return ApiResponse<AttendancePayrollPayDto>.OK(new AttendancePayrollPayDto
            {
                StoreCode = storeCode,
                PeriodStart = periodStart,
                PeriodEnd = snapshot.PeriodEnd,
                Weeks = weeks,
                PeriodStatus = snapshot.PeriodStatus,
                PendingCount = snapshot.PendingCount,
                SubmittedAt = snapshot.PeriodStatus == PayPeriodStatusSubmitted ? snapshot.Period?.SubmittedAt : null,
                SubmittedBy = snapshot.PeriodStatus == PayPeriodStatusSubmitted ? submittedByName ?? snapshot.Period?.SubmittedBy : null,
                Rows = rows,
                TotalGrossPay = PayrollCalculator.Round2(rows.Sum(item => item.GrossPay)),
            });
        }

        /// <summary>
        /// 这一行在工资表里是否「有记录」：有工时，或算出了转账 / 现金金额，或有每期津贴（可正可负，与工时无关，
        /// 零工时也要发 / 扣），或有年假 / 年假折现申请待财务核定，或财务写过备注。固定转账工时不算——它只是转账容量上限，
        /// 实际转账工时仍从工时里装填，零工时时就是 0。
        /// </summary>
        private static bool HasPayRecord(AttendancePayrollPayRowDto row) =>
            row.Hours.Total != 0
            || row.Transfer.Hours != 0 || row.Transfer.Amount != 0
            || row.Cash.Hours != 0 || row.Cash.Amount != 0
            || row.Allowance != 0
            || row.GrossPay != 0
            || row.LeaveDetail.Annual.RequestedHours > 0
            || row.LeaveDetail.CashOut.RequestedHours > 0
            || !string.IsNullOrWhiteSpace(row.Note);

        private async Task<List<AttendancePayrollPayRowDto>> BuildPayRowsAsync(PayrollPeriodSnapshot snapshot)
        {
            var userGuids = snapshot.Employees.Select(item => item.UserGuid).ToList();
            var periodEnd = snapshot.PeriodEnd;
            var rates = userGuids.Count == 0
                ? new List<EmployeePayRate>()
                : await _db.Queryable<EmployeePayRate>()
                    .Where(item => userGuids.Contains(item.UserGuid) && item.EffectiveFrom <= periodEnd)
                    .ToListAsync();
            var notes = await _db.Queryable<AttendancePayPeriodNote>()
                .Where(item => item.StoreCode == snapshot.StoreCode && item.PeriodStart == snapshot.PeriodStart && item.Weeks == snapshot.Weeks)
                .ToListAsync();
            var approvals = await _db.Queryable<AttendancePayPeriodLeaveApproval>()
                .Where(item => item.StoreCode == snapshot.StoreCode && item.PeriodStart == snapshot.PeriodStart && item.Weeks == snapshot.Weeks)
                .ToListAsync();
            // 公假 / 带薪假现金金额手填：每人每期每档一条，覆盖该档的系统现金金额（只在该档有现金工时时生效）。
            var cashOverrides = await _db.Queryable<AttendancePayPeriodCashOverride>()
                .Where(item => item.StoreCode == snapshot.StoreCode && item.PeriodStart == snapshot.PeriodStart && item.Weeks == snapshot.Weeks)
                .ToListAsync();
            var cashOverrideEditors = await ResolveUsernamesToDisplayAsync(cashOverrides.Select(item => item.UpdatedBy));

            var rows = new List<AttendancePayrollPayRowDto>();
            foreach (var employee in snapshot.Employees)
            {
                var publicHolidayOverride = FindCashOverride(cashOverrides, employee.UserGuid, PayrollBucket.PublicHoliday);
                var leaveCashOverride = FindCashOverride(cashOverrides, employee.UserGuid, PayrollBucket.Leave);
                var annual = BuildAnnualDetail(employee, approvals, PaidAnnualLeaveType);
                var cashOut = BuildAnnualDetail(employee, approvals, AnnualCashOutLeaveType);
                var leaveItems = employee.SickItems
                    .Concat(employee.PublicHolidayPayItems)
                    .ToList();
                if (annual.ApprovedHours > 0)
                {
                    leaveItems.Add(new PayrollLeaveItem(employee.AnnualByDay.Keys.FirstOrDefault(snapshot.PeriodStart), annual.ApprovedHours));
                }
                if (cashOut.ApprovedHours > 0)
                {
                    leaveItems.Add(new PayrollLeaveItem(employee.CashOutByDay.Keys.FirstOrDefault(snapshot.PeriodStart), cashOut.ApprovedHours));
                }
                var pay = PayrollCalculator.CalculatePay(new PayrollPayInput
                {
                    PeriodStart = snapshot.PeriodStart,
                    Weeks = snapshot.Weeks,
                    Rates = rates
                        .Where(item => item.UserGuid.Equals(employee.UserGuid, StringComparison.OrdinalIgnoreCase))
                        .Select(PayrollRate.From)
                        .ToList(),
                    WorkItems = employee.WorkItems,
                    LeaveItems = leaveItems,
                    PublicHolidayCashOverride = publicHolidayOverride?.Amount,
                    LeaveCashOverride = leaveCashOverride?.Amount,
                });
                rows.Add(new AttendancePayrollPayRowDto
                {
                    UserGuid = employee.UserGuid,
                    UserName = employee.UserName,
                    EmploymentType = employee.EmploymentType,
                    AgeYears = employee.AgeYears,
                    PayMode = pay.PayMode,
                    FixedTransferHoursPerWeek = pay.FixedTransferHoursPerWeek,
                    Hours = new AttendancePayrollPayHoursDto
                    {
                        Weekday = pay.Hours.Weekday,
                        Saturday = pay.Hours.Saturday,
                        Sunday = pay.Hours.Sunday,
                        PublicHoliday = pay.Hours.PublicHoliday,
                        Leave = pay.Hours.Leave,
                        Total = PayrollCalculator.Round2(pay.Hours.Total),
                    },
                    LeaveDetail = new AttendancePayrollLeaveDetailDto
                    {
                        Sick = PayrollCalculator.Round2(employee.SickItems.Sum(item => item.Hours)),
                        PublicHolidayPay = PayrollCalculator.Round2(employee.PublicHolidayPayItems.Sum(item => item.Hours)),
                        Annual = annual,
                        CashOut = cashOut,
                    },
                    Transfer = new AttendancePayrollTransferDto
                    {
                        Buckets = ToBucketDto(pay.TransferHours),
                        Hours = PayrollCalculator.Round2(pay.TransferHours.Total),
                        Amount = pay.TransferAmount,
                    },
                    Cash = new AttendancePayrollCashDto
                    {
                        Buckets = ToBucketDto(pay.CashHours),
                        Hours = PayrollCalculator.Round2(pay.CashHours.Total),
                        Rate = pay.CashRate,
                        Amount = pay.CashAmount,
                        PublicHoliday = BuildCashBucketAmount(
                            pay.CashHours.PublicHoliday, pay.PublicHolidayCashSystemAmount, pay.PublicHolidayCashOverridden,
                            publicHolidayOverride, cashOverrideEditors),
                        Leave = BuildCashBucketAmount(
                            pay.CashHours.Leave, pay.LeaveCashSystemAmount, pay.LeaveCashOverridden,
                            leaveCashOverride, cashOverrideEditors),
                    },
                    Allowance = pay.Allowance,
                    GrossPay = pay.GrossPay,
                    Note = notes.FirstOrDefault(item => item.UserGuid.Equals(employee.UserGuid, StringComparison.OrdinalIgnoreCase))?.Note,
                    Warnings = pay.Warnings,
                });
            }
            return rows;
        }

        private static AttendancePayPeriodCashOverride? FindCashOverride(
            IEnumerable<AttendancePayPeriodCashOverride> overrides, string userGuid, PayrollBucket bucket) =>
            overrides.FirstOrDefault(item =>
                item.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Bucket, bucket.ToString(), StringComparison.Ordinal));

        /// <summary>
        /// 公假 / 带薪假某一档的现金金额明细。手填是否生效由计算器决定（该档有现金工时才生效），这里只如实带出：
        /// Amount = 生效则手填值，否则系统值；有手填但没生效时 ManualAmount 仍带出，前端据此提示。
        /// </summary>
        private static AttendancePayrollCashBucketAmountDto BuildCashBucketAmount(
            decimal cashHours,
            decimal systemAmount,
            bool applied,
            AttendancePayPeriodCashOverride? stored,
            IReadOnlyDictionary<string, string> editors) => new()
        {
            Hours = PayrollCalculator.Round2(cashHours),
            SystemAmount = systemAmount,
            IsManual = applied,
            ManualAmount = stored?.Amount,
            Amount = applied && stored != null ? stored.Amount : systemAmount,
            // 只有该档有现金工时才允许手填（工时全在转账里的档位没有现金部分，避免同一笔钱付两次）。
            Editable = cashHours > 0,
            UpdatedBy = stored?.UpdatedBy == null ? null : editors.GetValueOrDefault(stored.UpdatedBy) ?? stored.UpdatedBy,
            UpdatedAt = stored?.UpdatedAt,
        };

        /// <summary>年假核定：没有财务记录时等于申请工时；有记录时取 min(核定, 申请)，申请变少时不会多发。</summary>
        private static AttendancePayrollAnnualDetailDto BuildAnnualDetail(
            PayrollEmployeeSnapshot employee,
            IEnumerable<AttendancePayPeriodLeaveApproval> approvals,
            string leaveType)
        {
            var requested = PayrollCalculator.Round2(
                (leaveType == PaidAnnualLeaveType ? employee.AnnualByDay : employee.CashOutByDay).Values.Sum());
            var approval = approvals.FirstOrDefault(item =>
                item.UserGuid.Equals(employee.UserGuid, StringComparison.OrdinalIgnoreCase) && item.LeaveType == leaveType);
            return new AttendancePayrollAnnualDetailDto
            {
                LeaveType = leaveType,
                RequestedHours = requested,
                ApprovedHours = approval == null ? requested : Math.Min(approval.ApprovedHours, requested),
                IsFinanceApproved = approval != null,
            };
        }

        private static AttendancePayrollBucketHoursDto ToBucketDto(PayrollBucketHours hours) => new()
        {
            Sunday = hours.Sunday,
            Saturday = hours.Saturday,
            Leave = hours.Leave,
            PublicHoliday = hours.PublicHoliday,
            Weekday = hours.Weekday,
        };

        public async Task<ApiResponse<List<AttendancePayRateDto>>> GetPayRatesAsync(AttendancePayRateQueryDto query)
        {
            if (string.IsNullOrWhiteSpace(query.StoreCode))
            {
                return ApiResponse<List<AttendancePayRateDto>>.Error("分店代码不能为空", "STORE_REQUIRED");
            }
            var storeCode = query.StoreCode.Trim();
            var access = await ResolveManagedStoreAccessAsync(storeCode);
            if (!access.Success)
            {
                return ApiResponse<List<AttendancePayRateDto>>.Error(access.Message, access.ErrorCode);
            }
            var userGuids = (await LoadStoreUsersAsync(storeCode)).Keys.ToList();
            var names = await ResolveUserDisplayNamesAsync(userGuids);
            var keyword = query.Keyword?.Trim();
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                userGuids = userGuids
                    .Where(item => names.GetValueOrDefault(item)?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();
            }
            var storeToday = ConvertUtcToStoreLocal(
                _timeProvider.GetUtcNow().UtcDateTime,
                await ResolveStoreTimeZoneAsync(storeCode, null)).Date;
            // 按周期看时薪：AsOf = 所选工资周期的起始日，取那天适用的记录；不传仍按今天（MYOB 同步等调用方不受影响）。
            var asOf = query.AsOf?.Date ?? storeToday;
            var rates = userGuids.Count == 0
                ? new List<EmployeePayRate>()
                : await _db.Queryable<EmployeePayRate>().Where(item => userGuids.Contains(item.UserGuid)).ToListAsync();
            var result = await BuildPayRateDtosAsync(userGuids, names, storeToday, userGuid =>
            {
                var own = rates.Where(item => item.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase)).ToList();
                // asOf 当天有效的记录；还没生效的未来记录不显示为当前（都在未来时退回最早一条，便于看到已录入的价）。
                return own.Where(item => item.EffectiveFrom.Date <= asOf).OrderByDescending(item => item.EffectiveFrom).FirstOrDefault()
                    ?? own.OrderBy(item => item.EffectiveFrom).FirstOrDefault();
            });
            foreach (var dto in result)
            {
                // 「即将变化」：所显示那条之后最近的一条已录入记录（周期内或之后生效）
                var shownFrom = dto.EffectiveFrom?.Date ?? asOf;
                dto.NextEffectiveFrom = rates
                    .Where(item => item.UserGuid.Equals(dto.UserGuid, StringComparison.OrdinalIgnoreCase)
                        && item.EffectiveFrom.Date > asOf && item.EffectiveFrom.Date > shownFrom)
                    .OrderBy(item => item.EffectiveFrom)
                    .Select(item => (DateTime?)item.EffectiveFrom.Date)
                    .FirstOrDefault();
            }
            if (query.PublicHolidayMismatchOnly)
            {
                result = result.Where(item => item.PublicHolidayRateMismatch).ToList();
            }
            return ApiResponse<List<AttendancePayRateDto>>.OK(result
                .OrderByDescending(item => item.IsManager)
                .ThenBy(item => item.UserName, StringComparer.OrdinalIgnoreCase)
                .ToList());
        }

        /// <summary>
        /// 分店侧栏用：一批分店在所选工资周期是否已提交锁定（与 IsPayPeriodLockedAsync 同一判断：已提交的期与周期有重叠即算锁定）。
        /// 没有权限的分店直接略过，不整批报错——侧栏只是提示，不能因为一家店没权限就全部不显示。
        /// </summary>
        public async Task<ApiResponse<List<AttendancePayPeriodStoreStatusDto>>> GetPayPeriodStatusesAsync(AttendancePayPeriodStatusQueryDto query)
        {
            if (query.PeriodStart is null)
            {
                return ApiResponse<List<AttendancePayPeriodStoreStatusDto>>.Error("周期起始日不能为空", "PERIOD_REQUIRED");
            }
            var scope = await _scopeService.GetScopeAsync();
            if (!scope.IsAllowed)
            {
                return ApiResponse<List<AttendancePayPeriodStoreStatusDto>>.Error(scope.Message, "FORBIDDEN");
            }
            var codes = (query.StoreCodes ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(scope.CanAccessStoreCode)
                .Take(200)
                .ToList();
            if (codes.Count == 0)
            {
                return ApiResponse<List<AttendancePayPeriodStoreStatusDto>>.OK(new List<AttendancePayPeriodStoreStatusDto>());
            }
            var from = query.PeriodStart.Value.Date;
            var weeks = query.Weeks is 2 ? 2 : 1;
            var to = from.AddDays(weeks * 7 - 1);
            var submitted = await _db.Queryable<AttendancePayPeriod>()
                .Where(item => codes.Contains(item.StoreCode) && item.Status == PayPeriodStatusSubmitted && item.PeriodStart <= to)
                .ToListAsync();
            var locked = submitted
                .Where(item => item.PeriodStart.Date.AddDays(Math.Max(1, item.Weeks) * 7) > from)
                .Select(item => item.StoreCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return ApiResponse<List<AttendancePayPeriodStoreStatusDto>>.OK(codes
                .Select(code => new AttendancePayPeriodStoreStatusDto { StoreCode = code, Locked = locked.Contains(code) })
                .ToList());
        }

        public async Task<ApiResponse<List<AttendancePayRateDto>>> GetPayRateHistoryAsync(string userGuid, string? storeCode)
        {
            var target = userGuid?.Trim() ?? string.Empty;
            var accessError = await ResolvePayRateUserAccessAsync<List<AttendancePayRateDto>>(target, storeCode);
            if (accessError != null)
            {
                return accessError;
            }
            var rows = await _db.Queryable<EmployeePayRate>()
                .Where(item => item.UserGuid == target)
                .OrderByDescending(item => item.EffectiveFrom)
                .ToListAsync();
            var names = await ResolveUserDisplayNamesAsync(new[] { target });
            var creatorNames = await ResolveUsernamesToDisplayAsync(rows.Select(item => item.CreatedBy));
            return ApiResponse<List<AttendancePayRateDto>>.OK(rows.Select(item =>
            {
                var dto = ToPayRateDto(item, target);
                dto.UserName = names.GetValueOrDefault(target);
                dto.CreatedBy = item.CreatedBy == null ? null : creatorNames.GetValueOrDefault(item.CreatedBy) ?? item.CreatedBy;
                return dto;
            }).ToList());
        }

        public async Task<ApiResponse<AttendancePayRateDto>> CreatePayRateAsync(CreateEmployeePayRateDto request)
        {
            if (string.IsNullOrWhiteSpace(request.StoreCode) || string.IsNullOrWhiteSpace(request.UserGuid))
            {
                return ApiResponse<AttendancePayRateDto>.Error("分店与员工不能为空", "INVALID_REQUEST");
            }
            var storeCode = request.StoreCode.Trim();
            var userGuid = request.UserGuid.Trim();
            var accessError = await ResolvePayRateUserAccessAsync<AttendancePayRateDto>(userGuid, storeCode);
            if (accessError != null)
            {
                return accessError;
            }
            // 公假转账价按 MYOB 公共假期类别的倍数设置（2026-11-17 决定，取代原先「恒等于平日价」的 11.4）：
            // 请求带了公假价就按带的值保存；没带才默认等于平日价（保持手填时的旧体验）；全现金模式不用转账价，存空。
            request.TransferPublicHolidayRate = request.PayMode?.Trim() == PayrollCalculator.PayModeCash
                ? null
                : request.TransferPublicHolidayRate ?? request.TransferWeekdayRate;
            var validation = ValidatePayRate(request);
            if (!validation.Success)
            {
                return ApiResponse<AttendancePayRateDto>.Error(validation.Message, validation.ErrorCode);
            }
            var effectiveFrom = request.EffectiveFrom.Date;
            // 时薪按人不按店：员工所在的任一分店里，只要有已锁定的期结束在生效日之后，新记录就会改动那期工资。
            foreach (var code in await GetRelatedStoreCodesAsync(userGuid))
            {
                if (await IsPayPeriodLockedAsync(code, effectiveFrom, DateTime.MaxValue.Date))
                {
                    return PayPeriodLocked<AttendancePayRateDto>();
                }
            }
            if (await _db.Queryable<EmployeePayRate>().AnyAsync(item => item.UserGuid == userGuid && item.EffectiveFrom == effectiveFrom))
            {
                return ApiResponse<AttendancePayRateDto>.Error("该生效日期已有时薪记录，请换一个生效日期", "RATE_EFFECTIVE_DATE_EXISTS");
            }
            var model = new EmployeePayRate
            {
                RateGuid = Guid.NewGuid().ToString(),
                UserGuid = userGuid,
                PayMode = request.PayMode.Trim(),
                FixedTransferHoursPerWeek = request.PayMode.Trim() == PayrollCalculator.PayModeMixed
                    ? PayrollCalculator.Round2(request.FixedTransferHoursPerWeek)
                    : 0m,
                TransferWeekdayRate = RoundRate(request.TransferWeekdayRate),
                TransferSaturdayRate = RoundRate(request.TransferSaturdayRate),
                TransferSundayRate = RoundRate(request.TransferSundayRate),
                TransferPublicHolidayRate = RoundRate(request.TransferPublicHolidayRate),
                CashRate = RoundRate(request.CashRate),
                CashAllowancePerPeriod = PayrollCalculator.Round2(request.CashAllowancePerPeriod),
                MinimumRate = RoundRate(request.MinimumRate),
                EffectiveFrom = effectiveFrom,
                Remark = NormalizeOptional(request.Remark, 500),
                CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                CreatedBy = _currentUserService.GetCurrentUsername(),
            };
            try
            {
                await _db.Insertable(model).ExecuteCommandAsync();
            }
            catch (Exception exception) when (AttendancePunchPersistenceException.IsUniqueConstraintViolation(exception))
            {
                return ApiResponse<AttendancePayRateDto>.Error("该生效日期已有时薪记录，请换一个生效日期", "RATE_EFFECTIVE_DATE_EXISTS");
            }
            var dto = ToPayRateDto(model, userGuid);
            dto.UserName = (await ResolveUserDisplayNamesAsync(new[] { userGuid })).GetValueOrDefault(userGuid);
            return ApiResponse<AttendancePayRateDto>.OK(dto, "时薪生效记录已保存");
        }

        private static decimal? RoundRate(decimal? value) => value.HasValue ? PayrollCalculator.Round2(value.Value) : null;

        private static ValidationResult ValidatePayRate(CreateEmployeePayRateDto request)
        {
            var mode = request.PayMode?.Trim();
            if (!PayrollCalculator.IsValidPayMode(mode))
            {
                return ValidationResult.Error("发放方式只能是全转账、全现金或转账 + 现金", "INVALID_PAY_MODE");
            }
            if (request.EffectiveFrom == default)
            {
                return ValidationResult.Error("生效日期不能为空", "EFFECTIVE_FROM_REQUIRED");
            }
            var rates = new[]
            {
                request.TransferWeekdayRate, request.TransferSaturdayRate, request.TransferSundayRate,
                request.TransferPublicHolidayRate, request.CashRate, request.MinimumRate,
            };
            if (rates.Any(item => item is < 0 or > 999999.99m))
            {
                return ValidationResult.Error("时薪必须在 0 到 999999.99 之间", "INVALID_RATE");
            }
            if (request.CashAllowancePerPeriod is < -999999.99m or > 999999.99m)
            {
                return ValidationResult.Error("现金补贴超出范围", "INVALID_ALLOWANCE");
            }
            if (mode != PayrollCalculator.PayModeCash && request.TransferWeekdayRate == null)
            {
                return ValidationResult.Error("转账发放必须填写平日转账时薪", "TRANSFER_RATE_REQUIRED");
            }
            if (mode != PayrollCalculator.PayModeTransfer && request.CashRate == null)
            {
                return ValidationResult.Error("现金发放必须填写现金时薪", "CASH_RATE_REQUIRED");
            }
            if (mode == PayrollCalculator.PayModeMixed
                && request.FixedTransferHoursPerWeek is <= 0 or > 168)
            {
                return ValidationResult.Error("转账 + 现金必须填写每周固定转账工时（0–168）", "INVALID_FIXED_TRANSFER_HOURS");
            }
            return ValidationResult.OK();
        }

        /// <summary>时薪表按人：指定分店时须能管理该店且员工绑定该店；不指定时员工所在分店至少有一家在当前用户范围内。</summary>
        private async Task<ApiResponse<T>?> ResolvePayRateUserAccessAsync<T>(string userGuid, string? storeCode)
        {
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return ApiResponse<T>.Error("员工不能为空", "USER_REQUIRED");
            }
            if (!string.IsNullOrWhiteSpace(storeCode))
            {
                var access = await ResolveManagedStoreAccessAsync(storeCode.Trim());
                if (!access.Success)
                {
                    return ApiResponse<T>.Error(access.Message, access.ErrorCode);
                }
                var bound = await LoadStoreBoundUserGuidsAsync(storeCode.Trim(), new[] { userGuid });
                return bound.Contains(userGuid)
                    ? null
                    : ApiResponse<T>.Error("员工不存在或不属于该分店", "EMPLOYEE_NOT_IN_STORE");
            }
            var scope = await _scopeService.GetScopeAsync();
            if (!scope.IsAllowed)
            {
                return ApiResponse<T>.Error(scope.Message, "FORBIDDEN");
            }
            var related = await GetRelatedStoreCodesAsync(userGuid);
            return related.Any(scope.CanAccessStoreCode)
                ? null
                : ApiResponse<T>.Error("没有权限查看该员工的时薪", "FORBIDDEN_STORE");
        }

        private async Task<List<AttendancePayRateDto>> BuildPayRateDtosAsync(
            List<string> userGuids,
            Dictionary<string, string> names,
            DateTime storeToday,
            Func<string, EmployeePayRate?> currentRate)
        {
            var profiles = (await _db.Queryable<EmployeeProfile>()
                    .Where(item => !item.IsDeleted && userGuids.Contains(item.UserGUID))
                    .ToListAsync())
                .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var minorBirthdays = (await _db.Queryable<EmployeeMinorCompliance>()
                    .Where(item => !item.IsDeleted && userGuids.Contains(item.UserGUID))
                    .ToListAsync())
                .Where(item => item.DateOfBirth.HasValue)
                .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().DateOfBirth!.Value, StringComparer.OrdinalIgnoreCase);
            var settings = await GetOrCreateSettingsModelAsync();
            var limits = await ResolveSegmentLimitMapAsync(userGuids.Select(item => new AttendanceSchedule { UserGuid = item }).ToList());
            var managerLimit = ResolveManagerSegmentLimit(settings);
            var employeeLimit = ResolveEmployeeSegmentLimit(settings);
            var result = new List<AttendancePayRateDto>();
            foreach (var userGuid in userGuids)
            {
                profiles.TryGetValue(userGuid, out var profile);
                var birthday = profile?.Birthday ?? (minorBirthdays.TryGetValue(userGuid, out var dob) ? dob : null);
                var rate = currentRate(userGuid);
                var dto = rate == null ? new AttendancePayRateDto { UserGuid = userGuid } : ToPayRateDto(rate, userGuid);
                dto.UserName = names.GetValueOrDefault(userGuid) ?? userGuid;
                dto.EmploymentType = ToEmploymentTypeCode(profile?.EmployeeType);
                dto.IsManager = managerLimit != employeeLimit && limits.GetValueOrDefault(userGuid) == managerLimit;
                dto.AgeYears = ResolveMinorAge(birthday, storeToday);
                dto.Birthday = birthday?.Date;
                // 最近一次生日已过、生日当天年龄 ≤ 21、而当前时薪记录生效早于那次生日 → 提示核对按年龄计的时薪。
                // 不能再要求「现在还未满 21 岁」：刚满 21 岁那天 AgeYears 已是 null，但那正是青少年档转成人档的一天。
                if (birthday.HasValue && rate != null)
                {
                    var review = ResolveBirthdayRateReview(birthday.Value, storeToday, rate.EffectiveFrom);
                    if (review != null)
                    {
                        dto.BirthdayReviewHint = true;
                        dto.BirthdayReviewDate = review.Value.Date;
                        dto.BirthdayReviewAge = review.Value.Age;
                    }
                }
                result.Add(dto);
            }
            return result;
        }

        private static AttendancePayRateDto ToPayRateDto(EmployeePayRate item, string userGuid) => new()
        {
            RateGuid = item.RateGuid,
            UserGuid = userGuid,
            PayMode = item.PayMode,
            FixedTransferHoursPerWeek = item.FixedTransferHoursPerWeek,
            TransferWeekdayRate = item.TransferWeekdayRate,
            TransferSaturdayRate = item.TransferSaturdayRate,
            TransferSundayRate = item.TransferSundayRate,
            TransferPublicHolidayRate = item.TransferPublicHolidayRate,
            PublicHolidayRateMismatch = HasPublicHolidayRateMismatch(item),
            CashRate = item.CashRate,
            CashAllowancePerPeriod = item.CashAllowancePerPeriod,
            MinimumRate = item.MinimumRate,
            EffectiveFrom = item.EffectiveFrom.Date,
            Remark = item.Remark,
            CreatedAt = item.CreatedAt,
            CreatedBy = item.CreatedBy,
        };

        /// <summary>
        /// 恒为 false：原先公假转账价必须等于平日转账价（docs/plans/2026-10-27-myob-payroll-integration.md 第 11.4 节），
        /// 不相等就标「不一致」。2026-11-17 起公假价按 MYOB 公共假期类别的倍数设置（docs/plans/2026-11-16-myob-hourly-rate-sync.md 第 3.5 节），
        /// 与平日价不同是合法的，所以不再标记。字段与列表筛选保留（旧版 Web 缓存还在读），Web 退役相关提示后再删。
        /// </summary>
        internal static bool HasPublicHolidayRateMismatch(EmployeePayRate item) => false;

        /// <summary>CreatedBy 存的是用户名；历史列表显示成姓名。</summary>
        private async Task<Dictionary<string, string>> ResolveUsernamesToDisplayAsync(IEnumerable<string?> usernames)
        {
            var list = usernames.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).Distinct().ToList();
            if (list.Count == 0)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            var users = await _db.Queryable<User>()
                .Where(item => list.Contains(item.Username))
                .Select(item => new { item.Username, item.FullName })
                .ToListAsync();
            return users
                .GroupBy(item => item.Username, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => string.IsNullOrWhiteSpace(group.First().FullName) ? group.Key : group.First().FullName!,
                    StringComparer.OrdinalIgnoreCase);
        }

        public async Task<ApiResponse<AttendancePayPeriodNoteDto>> UpsertPayPeriodNoteAsync(UpsertAttendancePayPeriodNoteDto request)
        {
            var (error, storeCode) = await ValidatePayPeriodKeyAsync<AttendancePayPeriodNoteDto>(request);
            if (error != null)
            {
                return error;
            }
            var userGuid = request.UserGuid?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return ApiResponse<AttendancePayPeriodNoteDto>.Error("员工不能为空", "USER_REQUIRED");
            }
            var periodStart = request.PeriodStart.Date;
            if (await IsPayPeriodLockedAsync(storeCode, periodStart, periodStart.AddDays(request.Weeks * 7 - 1)))
            {
                return PayPeriodLocked<AttendancePayPeriodNoteDto>();
            }
            var note = (request.Note ?? string.Empty).Trim();
            if (note.Length > 1000)
            {
                return ApiResponse<AttendancePayPeriodNoteDto>.Error("备注不能超过 1000 字", "NOTE_TOO_LONG");
            }
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var username = _currentUserService.GetCurrentUsername();
            var existing = await _db.Queryable<AttendancePayPeriodNote>().FirstAsync(item =>
                item.StoreCode == storeCode && item.PeriodStart == periodStart && item.Weeks == request.Weeks && item.UserGuid == userGuid);
            if (existing == null)
            {
                if (note.Length > 0)
                {
                    await _db.Insertable(new AttendancePayPeriodNote
                    {
                        NoteGuid = Guid.NewGuid().ToString(),
                        StoreCode = storeCode,
                        PeriodStart = periodStart,
                        Weeks = request.Weeks,
                        UserGuid = userGuid,
                        Note = note,
                        UpdatedAt = now,
                        UpdatedBy = username,
                    }).ExecuteCommandAsync();
                }
            }
            else if (note.Length == 0)
            {
                await _db.Deleteable<AttendancePayPeriodNote>().Where(item => item.NoteGuid == existing.NoteGuid).ExecuteCommandAsync();
            }
            else
            {
                existing.Note = note;
                existing.UpdatedAt = now;
                existing.UpdatedBy = username;
                await _db.Updateable(existing).ExecuteCommandAsync();
            }
            return ApiResponse<AttendancePayPeriodNoteDto>.OK(new AttendancePayPeriodNoteDto
            {
                StoreCode = storeCode,
                PeriodStart = periodStart,
                Weeks = request.Weeks,
                UserGuid = userGuid,
                Note = note,
            }, "备注已保存");
        }

        /// <summary>允许手填现金金额的档位：只有公假与带薪假（与 PayrollBucket 枚举名一致，也是库里 Bucket 列的取值）。</summary>
        private static readonly HashSet<string> CashOverrideBuckets = new(StringComparer.Ordinal)
        {
            nameof(PayrollBucket.PublicHoliday),
            nameof(PayrollBucket.Leave),
        };

        /// <summary>手填现金金额的上限，防止多敲一位数；远低于 decimal(10,2) 的物理上限。</summary>
        private const decimal MaxCashOverrideAmount = 100000m;

        /// <summary>
        /// 手填 / 清除某人某期「公假」或「带薪假」的现金金额。手填值覆盖该档的系统现金金额，不是叠加；
        /// Amount 为 null = 清除、恢复系统值。与备注、年假核定同一套规则：周期锁定后拒绝；权限在控制器上（编辑工资）。
        /// 设置时服务端重算一遍工资，确认这一档确实有现金工时（工时全在转账里的档位没有现金部分，不允许手填，避免同一笔钱付两次），
        /// 并把当时的系统值存下来留痕。
        /// </summary>
        public async Task<ApiResponse<AttendancePayPeriodCashOverrideDto>> UpsertPayPeriodCashOverrideAsync(
            UpsertAttendancePayPeriodCashOverrideDto request)
        {
            var (error, storeCode) = await ValidatePayPeriodKeyAsync<AttendancePayPeriodCashOverrideDto>(request);
            if (error != null)
            {
                return error;
            }
            var userGuid = request.UserGuid?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return ApiResponse<AttendancePayPeriodCashOverrideDto>.Error("员工不能为空", "USER_REQUIRED");
            }
            var bucketName = request.Bucket?.Trim() ?? string.Empty;
            if (!CashOverrideBuckets.Contains(bucketName))
            {
                return ApiResponse<AttendancePayPeriodCashOverrideDto>.Error("只能手填公假或带薪假的现金金额", "INVALID_CASH_OVERRIDE_BUCKET");
            }
            var periodStart = request.PeriodStart.Date;
            if (await IsPayPeriodLockedAsync(storeCode, periodStart, periodStart.AddDays(request.Weeks * 7 - 1)))
            {
                return PayPeriodLocked<AttendancePayPeriodCashOverrideDto>();
            }
            if (request.Amount is { } rawAmount
                && (rawAmount < 0 || rawAmount > MaxCashOverrideAmount || PayrollCalculator.Round2(rawAmount) != rawAmount))
            {
                return ApiResponse<AttendancePayPeriodCashOverrideDto>.Error(
                    $"金额必须在 0 到 {MaxCashOverrideAmount:0} 之间，最多两位小数",
                    "INVALID_CASH_OVERRIDE_AMOUNT");
            }

            var existing = await _db.Queryable<AttendancePayPeriodCashOverride>().FirstAsync(item =>
                item.StoreCode == storeCode && item.PeriodStart == periodStart && item.Weeks == request.Weeks
                && item.UserGuid == userGuid && item.Bucket == bucketName);

            // 清除：恢复系统值。幂等——本来就没有手填也算成功；不需要重算工资。
            if (request.Amount is null)
            {
                if (existing != null)
                {
                    await _db.Deleteable<AttendancePayPeriodCashOverride>().Where(item => item.OverrideGuid == existing.OverrideGuid).ExecuteCommandAsync();
                }
                return ApiResponse<AttendancePayPeriodCashOverrideDto>.OK(new AttendancePayPeriodCashOverrideDto
                {
                    StoreCode = storeCode,
                    PeriodStart = periodStart,
                    Weeks = request.Weeks,
                    UserGuid = userGuid,
                    Bucket = bucketName,
                    Amount = null,
                    SystemAmount = existing?.SystemAmount ?? 0m,
                }, "已恢复系统值");
            }

            // 设置：重算工资，确认这一档有现金工时，并取当时的系统值留痕。
            var snapshot = await BuildPayrollSnapshotAsync(storeCode, periodStart, request.Weeks, includePending: false, mergeStores: false);
            var row = (await BuildPayRowsAsync(snapshot))
                .FirstOrDefault(item => item.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase));
            var bucket = row == null
                ? null
                : bucketName == nameof(PayrollBucket.PublicHoliday) ? row.Cash.PublicHoliday : row.Cash.Leave;
            if (bucket == null || !bucket.Editable)
            {
                return ApiResponse<AttendancePayPeriodCashOverrideDto>.Error(
                    "该员工这一档本期没有现金工时（工时已全部在转账里），不能手填现金金额",
                    "NO_CASH_HOURS_IN_BUCKET");
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var username = _currentUserService.GetCurrentUsername();
            var amount = request.Amount.Value;
            if (existing == null)
            {
                await _db.Insertable(new AttendancePayPeriodCashOverride
                {
                    OverrideGuid = Guid.NewGuid().ToString(),
                    StoreCode = storeCode,
                    PeriodStart = periodStart,
                    Weeks = request.Weeks,
                    UserGuid = userGuid,
                    Bucket = bucketName,
                    Amount = amount,
                    SystemAmount = bucket.SystemAmount,
                    UpdatedAt = now,
                    UpdatedBy = username,
                }).ExecuteCommandAsync();
            }
            else
            {
                existing.Amount = amount;
                existing.SystemAmount = bucket.SystemAmount;
                existing.UpdatedAt = now;
                existing.UpdatedBy = username;
                await _db.Updateable(existing).ExecuteCommandAsync();
            }
            return ApiResponse<AttendancePayPeriodCashOverrideDto>.OK(new AttendancePayPeriodCashOverrideDto
            {
                StoreCode = storeCode,
                PeriodStart = periodStart,
                Weeks = request.Weeks,
                UserGuid = userGuid,
                Bucket = bucketName,
                Amount = amount,
                SystemAmount = bucket.SystemAmount,
            }, "现金金额已保存");
        }

        public async Task<ApiResponse<AttendancePayPeriodLeaveApprovalDto>> UpsertPayPeriodLeaveApprovalAsync(
            UpsertAttendancePayPeriodLeaveApprovalDto request)
        {
            var (error, storeCode) = await ValidatePayPeriodKeyAsync<AttendancePayPeriodLeaveApprovalDto>(request);
            if (error != null)
            {
                return error;
            }
            var userGuid = request.UserGuid?.Trim() ?? string.Empty;
            var leaveType = request.LeaveType?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(userGuid) || !AnnualLeaveTypes.Contains(leaveType))
            {
                return ApiResponse<AttendancePayPeriodLeaveApprovalDto>.Error("只能核定年假休息或年假发放的工时", "INVALID_LEAVE_TYPE");
            }
            var periodStart = request.PeriodStart.Date;
            if (await IsPayPeriodLockedAsync(storeCode, periodStart, periodStart.AddDays(request.Weeks * 7 - 1)))
            {
                return PayPeriodLocked<AttendancePayPeriodLeaveApprovalDto>();
            }
            var snapshot = await BuildPayrollSnapshotAsync(storeCode, periodStart, request.Weeks, includePending: false, mergeStores: false);
            var employee = snapshot.Employees.FirstOrDefault(item => item.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase));
            var requested = employee == null
                ? 0m
                : PayrollCalculator.Round2((leaveType == PaidAnnualLeaveType ? employee.AnnualByDay : employee.CashOutByDay).Values.Sum());
            if (requested <= 0)
            {
                return ApiResponse<AttendancePayPeriodLeaveApprovalDto>.Error("该员工本期没有已批准的年假申请", "NO_ANNUAL_LEAVE_IN_PERIOD");
            }
            var approved = PayrollCalculator.Round2(request.ApprovedHours);
            if (approved < 0 || approved > requested)
            {
                return ApiResponse<AttendancePayPeriodLeaveApprovalDto>.Error(
                    $"核定工时必须在 0 到申请工时 {requested:0.##} h 之间",
                    "INVALID_APPROVED_HOURS");
            }
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var reviewer = ResolveCurrentUserGuid();
            var existing = await _db.Queryable<AttendancePayPeriodLeaveApproval>().FirstAsync(item =>
                item.StoreCode == storeCode && item.PeriodStart == periodStart && item.Weeks == request.Weeks
                && item.UserGuid == userGuid && item.LeaveType == leaveType);
            if (existing == null)
            {
                await _db.Insertable(new AttendancePayPeriodLeaveApproval
                {
                    ApprovalGuid = Guid.NewGuid().ToString(),
                    StoreCode = storeCode,
                    PeriodStart = periodStart,
                    Weeks = request.Weeks,
                    UserGuid = userGuid,
                    LeaveType = leaveType,
                    RequestedHours = requested,
                    ApprovedHours = approved,
                    ApprovedBy = reviewer,
                    ApprovedAt = now,
                }).ExecuteCommandAsync();
            }
            else
            {
                existing.RequestedHours = requested;
                existing.ApprovedHours = approved;
                existing.ApprovedBy = reviewer;
                existing.ApprovedAt = now;
                await _db.Updateable(existing).ExecuteCommandAsync();
            }
            return ApiResponse<AttendancePayPeriodLeaveApprovalDto>.OK(new AttendancePayPeriodLeaveApprovalDto
            {
                StoreCode = storeCode,
                PeriodStart = periodStart,
                Weeks = request.Weeks,
                UserGuid = userGuid,
                LeaveType = leaveType,
                RequestedHours = requested,
                ApprovedHours = approved,
            }, "年假核定工时已保存");
        }

        public async Task<ApiResponse<AttendancePayPeriodDto>> SubmitPayPeriodAsync(AttendancePayPeriodKeyDto request)
        {
            var (error, storeCode) = await ValidatePayPeriodKeyAsync<AttendancePayPeriodDto>(request);
            if (error != null)
            {
                return error;
            }
            var periodStart = request.PeriodStart.Date;
            var periodEnd = periodStart.AddDays(request.Weeks * 7 - 1);
            // 提交前也补一次带薪公假确认，避免漏掉还没生成的待处理就锁了期。
            await GeneratePublicHolidayPayApprovalsAsync(storeCode, periodStart, periodEnd);
            var snapshot = await BuildPayrollSnapshotAsync(storeCode, periodStart, request.Weeks, includePending: false, mergeStores: false);
            if (snapshot.PeriodStatus == PayPeriodStatusSubmitted)
            {
                return ApiResponse<AttendancePayPeriodDto>.Error("本期已提交", "PERIOD_ALREADY_SUBMITTED");
            }
            if (snapshot.PendingCount > 0)
            {
                return ApiResponse<AttendancePayPeriodDto>.Error(
                    $"本期还有 {snapshot.PendingCount} 项待处理，处理完才能提交",
                    "PERIOD_HAS_PENDING");
            }
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var submitter = ResolveCurrentUserGuid();
            var username = _currentUserService.GetCurrentUsername();
            var period = snapshot.Period;
            if (period == null)
            {
                period = new AttendancePayPeriod
                {
                    PeriodGuid = Guid.NewGuid().ToString(),
                    StoreCode = storeCode,
                    PeriodStart = periodStart,
                    Weeks = request.Weeks,
                    Status = PayPeriodStatusSubmitted,
                    SubmittedAt = now,
                    SubmittedBy = submitter,
                    CreatedAt = now,
                    UpdatedAt = now,
                    UpdatedBy = username,
                };
                try
                {
                    await _db.Insertable(period).ExecuteCommandAsync();
                }
                catch (Exception exception) when (AttendancePunchPersistenceException.IsUniqueConstraintViolation(exception))
                {
                    return ApiResponse<AttendancePayPeriodDto>.Error("本期已提交", "PERIOD_ALREADY_SUBMITTED");
                }
            }
            else
            {
                // 条件更新是并发提交的原子领取点。
                var updated = await _db.Updateable<AttendancePayPeriod>()
                    .SetColumns(item => item.Status == PayPeriodStatusSubmitted)
                    .SetColumns(item => item.SubmittedAt == now)
                    .SetColumns(item => item.SubmittedBy == submitter)
                    .SetColumns(item => item.UpdatedAt == now)
                    .SetColumns(item => item.UpdatedBy == username)
                    .Where(item => item.PeriodGuid == period.PeriodGuid && item.Status != PayPeriodStatusSubmitted)
                    .ExecuteCommandAsync();
                if (updated != 1)
                {
                    return ApiResponse<AttendancePayPeriodDto>.Error("本期已提交", "PERIOD_ALREADY_SUBMITTED");
                }
                period.Status = PayPeriodStatusSubmitted;
                period.SubmittedAt = now;
                period.SubmittedBy = submitter;
            }
            _logger.LogInformation(
                "发薪周期已提交锁定: StoreCode={StoreCode}, PeriodStart={PeriodStart:yyyy-MM-dd}, Weeks={Weeks}, By={User}",
                storeCode, periodStart, request.Weeks, username);
            return ApiResponse<AttendancePayPeriodDto>.OK(ToPayPeriodDto(period), "本期已提交并锁定");
        }

        public async Task<ApiResponse<AttendancePayPeriodDto>> UnlockPayPeriodAsync(UnlockAttendancePayPeriodDto request)
        {
            var (error, storeCode) = await ValidatePayPeriodKeyAsync<AttendancePayPeriodDto>(request);
            if (error != null)
            {
                return error;
            }
            var reason = NormalizeOptional(request.Reason, 500);
            if (string.IsNullOrWhiteSpace(reason))
            {
                return ApiResponse<AttendancePayPeriodDto>.Error("解锁必须填写原因", "UNLOCK_REASON_REQUIRED");
            }
            var periodStart = request.PeriodStart.Date;
            var period = await _db.Queryable<AttendancePayPeriod>().FirstAsync(item =>
                item.StoreCode == storeCode && item.PeriodStart == periodStart && item.Weeks == request.Weeks);
            if (period == null || period.Status != PayPeriodStatusSubmitted)
            {
                return ApiResponse<AttendancePayPeriodDto>.Error("本期未提交，无需解锁", "PERIOD_NOT_SUBMITTED");
            }
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var unlocker = ResolveCurrentUserGuid();
            var username = _currentUserService.GetCurrentUsername();
            var updated = await _db.Updateable<AttendancePayPeriod>()
                .SetColumns(item => item.Status == PayPeriodStatusOpen)
                .SetColumns(item => item.UnlockedAt == now)
                .SetColumns(item => item.UnlockedBy == unlocker)
                .SetColumns(item => item.UnlockReason == reason)
                .SetColumns(item => item.UpdatedAt == now)
                .SetColumns(item => item.UpdatedBy == username)
                .Where(item => item.PeriodGuid == period.PeriodGuid && item.Status == PayPeriodStatusSubmitted)
                .ExecuteCommandAsync();
            if (updated != 1)
            {
                return ApiResponse<AttendancePayPeriodDto>.Error("本期未提交，无需解锁", "PERIOD_NOT_SUBMITTED");
            }
            period.Status = PayPeriodStatusOpen;
            period.UnlockedAt = now;
            period.UnlockedBy = unlocker;
            period.UnlockReason = reason;
            // 操作记录：周期行保留最后一次解锁人 / 时间 / 原因，另写一条应用日志备查。
            _logger.LogWarning(
                "发薪周期已解锁: StoreCode={StoreCode}, PeriodStart={PeriodStart:yyyy-MM-dd}, Weeks={Weeks}, By={User}, Reason={Reason}",
                storeCode, periodStart, request.Weeks, username, reason);
            return ApiResponse<AttendancePayPeriodDto>.OK(ToPayPeriodDto(period), "本期已解锁");
        }

        private static AttendancePayPeriodDto ToPayPeriodDto(AttendancePayPeriod item) => new()
        {
            StoreCode = item.StoreCode,
            PeriodStart = item.PeriodStart.Date,
            Weeks = item.Weeks,
            Status = item.Status,
            SubmittedAt = item.SubmittedAt,
            SubmittedBy = item.SubmittedBy,
            UnlockedAt = item.UnlockedAt,
            UnlockedBy = item.UnlockedBy,
            UnlockReason = item.UnlockReason,
        };
    }
}
