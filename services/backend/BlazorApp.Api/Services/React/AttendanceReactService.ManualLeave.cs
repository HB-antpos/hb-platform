using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React
{
    // partial：店长 / Admin 在「工时统计 / 工资表」为「忘记申请」的员工补录带薪假（带薪病假 / 带薪年假）。
    // 年假发放（AnnualCashOut）已取消：不再接收新的补录与员工申请，历史记录保持原计算口径。
    // 不新建表、不加列：补录就是一条直接「已批准」的 AttendanceLeaveRequest，工资计算沿用现成的请假口径；
    // 靠 ReviewRemark 的「补录：」前缀识别它是补录的（页面据此显示「补」角标、允许撤销）；
    // 带薪年假选了「员工当天不休息」时，前缀改为「补录（不休息）：」——同样是补录，但不标「不能上班」、不查打卡。
    // 带薪年假的合计小时数存在 PaidHours，按区间内工作日平均摊（见 PayrollCalculator.SpreadAnnualLeaveHours）。
    // 与员工自己申请的区别：事后记录、直接生效（不走审批）、不受「提前申请 / 生病后 N 天内提交」限制、医嘱可不传（原因必填）。
    // 其余校验沿用代补卡 / 补录工时：权限与管理分店、周期锁定、可改窗口、不能补未来、全职 / 兼职才有带薪假。
    public partial class AttendanceReactService
    {
        /// <summary>补录请假的审批备注前缀；撤销与页面角标据此判断「这条假是补录建的」。</summary>
        internal const string ManualLeaveRemarkPrefix = "补录：";
        /// <summary>补录带薪年假且员工当天不休息（照常上班）的备注前缀；其余行为同补录。</summary>
        internal const string ManualLeaveNoRestRemarkPrefix = "补录（不休息）：";
        /// <summary>原因最长 400：审批备注要加前缀，备注列上限 500。</summary>
        private const int ManualLeaveReasonMaxLength = 400;
        /// <summary>带薪年假合计小时数的上限：区间自然日数 × 24，挡住明显填错（比如多打一个 0）。</summary>
        private const int ManualLeaveMaxHoursPerCalendarDay = 24;

        /// <summary>这条假是不是补录建的（休息 / 不休息两种前缀都算）。</summary>
        private static bool IsManualLeave(AttendanceLeaveRequest leave) =>
            leave.ReviewRemark is { } remark
            && (remark.StartsWith(ManualLeaveRemarkPrefix, StringComparison.Ordinal)
                || remark.StartsWith(ManualLeaveNoRestRemarkPrefix, StringComparison.Ordinal));

        /// <summary>补录的带薪年假且员工当天不休息：不写「不能上班」、不检查打卡、不清「未打卡」提示。</summary>
        private static bool IsManualNoRestAnnual(AttendanceLeaveRequest leave) =>
            leave.LeaveType == PaidAnnualLeaveType
            && leave.ReviewRemark?.StartsWith(ManualLeaveNoRestRemarkPrefix, StringComparison.Ordinal) == true;

        private sealed class ManualLeaveContext
        {
            public bool Success { get; private init; }
            public string Message { get; private init; } = string.Empty;
            public string? ErrorCode { get; private init; }
            public AttendanceLeaveRequest? Leave { get; private init; }
            public AttendanceManualLeavePreviewDto? Preview { get; private init; }

            public static ManualLeaveContext Error(string message, string errorCode) =>
                new() { Success = false, Message = message, ErrorCode = errorCode };

            public static ManualLeaveContext OK(AttendanceLeaveRequest leave, AttendanceManualLeavePreviewDto preview) =>
                new() { Success = true, Leave = leave, Preview = preview };
        }

        public async Task<ApiResponse<AttendanceManualLeavePreviewDto>> PreviewManagedManualLeaveAsync(
            CreateAttendanceManualLeaveDto request)
        {
            var context = await BuildManualLeaveContextAsync(request);
            return context.Success
                ? ApiResponse<AttendanceManualLeavePreviewDto>.OK(context.Preview!)
                : ApiResponse<AttendanceManualLeavePreviewDto>.Error(context.Message, context.ErrorCode ?? "MANUAL_LEAVE_INVALID");
        }

        public async Task<ApiResponse<AttendanceManualLeaveResultDto>> CreateManagedManualLeaveAsync(
            CreateAttendanceManualLeaveDto request)
        {
            var context = await BuildManualLeaveContextAsync(request);
            if (!context.Success)
            {
                return ApiResponse<AttendanceManualLeaveResultDto>.Error(context.Message, context.ErrorCode ?? "MANUAL_LEAVE_INVALID");
            }
            if (string.IsNullOrWhiteSpace(request.PreviewRevision))
            {
                return ApiResponse<AttendanceManualLeaveResultDto>.Error("补录预览已失效，请刷新后重新提交", "MANUAL_LEAVE_PREVIEW_STALE");
            }

            var userGuid = request.UserGuid.Trim();
            // 与审批 / 撤销请假同一把员工锁：补录与撤销、审批并发时只能有一方生效。
            var mutationResource = AttendanceDailyMutationLock.BuildResource(userGuid, string.Empty, default);
            await using var processLock = await AttendanceDailyMutationLock.AcquireProcessAsync(mutationResource);

            await _db.Ado.BeginTranAsync();
            try
            {
                await AttendanceDailyMutationLock.AcquireDatabaseAsync(_db, mutationResource);
                // 锁内重算：并发补录 / 员工刚好自己提交了请假，都会让预览版本变化或直接冲突。
                var locked = await BuildManualLeaveContextAsync(request);
                if (!locked.Success)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendanceManualLeaveResultDto>.Error(locked.Message, locked.ErrorCode ?? "MANUAL_LEAVE_INVALID");
                }
                if (!string.Equals(request.PreviewRevision, locked.Preview!.PreviewRevision, StringComparison.Ordinal))
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendanceManualLeaveResultDto>.Error("补录预览已失效，请刷新后重新提交", "MANUAL_LEAVE_PREVIEW_STALE");
                }

                var leave = locked.Leave!;
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var username = _currentUserService.GetCurrentUsername();
                leave.ReviewedBy = ResolveCurrentUserGuid();
                leave.ReviewedAt = now;
                leave.CreatedAt = now;
                leave.CreatedBy = username;
                await _db.Insertable(leave).ExecuteCommandAsync();
                if (leave.LeaveType == PaidAnnualLeaveType && !IsManualNoRestAnnual(leave))
                {
                    // 与批准带薪年假（休息）一致：区间内每天自动标「不能上班」，排班时被拦；选了「不休息」的不标。
                    await ApplyAnnualLeaveUnavailabilityAsync(leave, now, username);
                }
                await _db.Ado.CommitTranAsync();

                return ApiResponse<AttendanceManualLeaveResultDto>.OK(
                    new AttendanceManualLeaveResultDto { LeaveGuid = leave.LeaveGuid, PaidHours = leave.PaidHours ?? 0m },
                    "补录已生效");
            }
            catch
            {
                await _db.Ado.RollbackTranAsync();
                throw;
            }
        }

        /// <summary>
        /// 撤销补录建的假：只认「已批准且备注带补录前缀」的；员工自己申请、审批通过的假走审批页的撤销。
        /// 其余（周期锁定、清除自动写入的「不能上班」、状态改 Revoked）复用店长撤销请假。
        /// </summary>
        public async Task<ApiResponse<bool>> RevokeManagedManualLeaveAsync(RevokeAttendanceManualLeaveDto request)
        {
            if (string.IsNullOrWhiteSpace(request.StoreCode))
            {
                return ApiResponse<bool>.Error("分店代码不能为空", "STORE_REQUIRED");
            }
            if (string.IsNullOrWhiteSpace(request.LeaveGuid))
            {
                return ApiResponse<bool>.Error("请假记录不能为空", "LEAVE_REQUIRED");
            }
            var storeCode = request.StoreCode.Trim();
            var leaveGuid = request.LeaveGuid.Trim();

            var access = await ResolveManagedStoreAccessAsync(storeCode);
            if (!access.Success)
            {
                return ApiResponse<bool>.Error(access.Message, access.ErrorCode);
            }
            if (!await IsCurrentUserManagerForStoreAsync(storeCode))
            {
                return ApiResponse<bool>.Error("只能修改自己管理分店的考勤", "FORBIDDEN_STORE");
            }
            var leave = await _db.Queryable<AttendanceLeaveRequest>()
                .FirstAsync(item => !item.IsDeleted && item.LeaveGuid == leaveGuid && item.StoreCode == storeCode);
            if (leave == null
                || leave.Status != "Approved"
                || !IsManualLeave(leave))
            {
                return ApiResponse<bool>.Error("这一条不是补录生效的假，不能在这里撤销", "MANUAL_LEAVE_NOT_FOUND");
            }
            var storeTimeZone = await ResolveStoreTimeZoneAsync(storeCode, null);
            var today = ConvertUtcToStoreLocal(_timeProvider.GetUtcNow().UtcDateTime, storeTimeZone).Date;
            if (!IsAdmin() && !IsWithinManagedAdjustmentWindow(leave.StartDate.Date, today))
            {
                return ApiResponse<bool>.Error("只能修改本周的考勤；周一、周二仍可修改上周", "ADJUSTMENT_WINDOW_EXPIRED");
            }
            return await RevokeManagedLeaveRequestAsync(leaveGuid, new ReviewAttendanceApprovalDto { ReviewRemark = "撤销补录" });
        }

        /// <summary>
        /// 补录请假的校验与预览（保存前会在员工锁内再算一遍）：返回要写入的请假草稿与工时预览，不写库。
        /// </summary>
        private async Task<ManualLeaveContext> BuildManualLeaveContextAsync(CreateAttendanceManualLeaveDto request)
        {
            if (string.IsNullOrWhiteSpace(request.StoreCode))
            {
                return ManualLeaveContext.Error("分店代码不能为空", "STORE_REQUIRED");
            }
            if (string.IsNullOrWhiteSpace(request.UserGuid))
            {
                return ManualLeaveContext.Error("员工不能为空", "USER_REQUIRED");
            }
            var reason = request.Reason?.Trim() ?? string.Empty;
            if (reason.Length == 0)
            {
                return ManualLeaveContext.Error("补录原因不能为空", "REASON_REQUIRED");
            }
            if (reason.Length > ManualLeaveReasonMaxLength)
            {
                return ManualLeaveContext.Error($"补录原因不能超过 {ManualLeaveReasonMaxLength} 个字", "REASON_TOO_LONG");
            }
            var leaveType = NormalizeManagedLeaveType(request.LeaveType);
            if (leaveType == AnnualCashOutLeaveType)
            {
                return ManualLeaveContext.Error("年假发放已取消，不能再补录", "ANNUAL_CASH_OUT_RETIRED");
            }
            if (leaveType is not (PaidSickLeaveType or PaidAnnualLeaveType))
            {
                return ManualLeaveContext.Error("请假类型无效", "INVALID_LEAVE_TYPE");
            }

            var storeCode = request.StoreCode.Trim();
            var userGuid = request.UserGuid.Trim();

            // 权限与范围：与补录工时 / 代员工补卡一致（接口策略已校验权限码，这里校验管理分店）。
            var access = await ResolveManagedStoreAccessAsync(storeCode);
            if (!access.Success)
            {
                return ManualLeaveContext.Error(access.Message, access.ErrorCode ?? "FORBIDDEN_STORE");
            }
            if (!await IsCurrentUserManagerForStoreAsync(storeCode))
            {
                return ManualLeaveContext.Error("只能修改自己管理分店的考勤", "FORBIDDEN_STORE");
            }
            // 员工必须是绑定本店的有效账号：防止误传 GUID 在库里建出无主请假。
            var boundUsers = await LoadStoreBoundUserGuidsAsync(storeCode, new[] { userGuid });
            if (!boundUsers.Contains(userGuid))
            {
                return ManualLeaveContext.Error("员工不存在或不属于该分店", "EMPLOYEE_NOT_IN_STORE");
            }
            var employeeType = await _db.Queryable<EmployeeProfile>()
                .Where(item => !item.IsDeleted && item.UserGUID == userGuid)
                .Select(item => item.EmployeeType)
                .FirstAsync();
            if (employeeType is not EmployeeType.FullTime and not EmployeeType.PartTime)
            {
                return ManualLeaveContext.Error("只有全职或兼职员工有带薪假", "PAID_LEAVE_NOT_ELIGIBLE");
            }

            var storeTimeZone = await ResolveStoreTimeZoneAsync(storeCode, null);
            var today = ConvertUtcToStoreLocal(_timeProvider.GetUtcNow().UtcDateTime, storeTimeZone).Date;
            var settings = await GetOrCreateSettingsModelAsync();

            DateTime start;
            DateTime end;
            decimal paidHours;
            // 带薪年假的默认合计（工作日 × 8）；病假不用。
            var defaultPaidHours = 0m;
            // 病假恒为 true；带薪年假没传按休息（与历史一致）。
            var restsOnLeaveDays = leaveType == PaidSickLeaveType || request.RestsOnLeaveDays != false;
            var days = new List<AttendanceManualLeaveDayDto>();
            var scheduleGuids = new List<string>();
            if (leaveType == PaidSickLeaveType)
            {
                var sickDates = (request.Dates ?? new List<DateTime>())
                    .Where(item => item != default)
                    .Select(item => item.Date)
                    .Distinct()
                    .OrderBy(item => item)
                    .ToList();
                if (sickDates.Count == 0)
                {
                    return ManualLeaveContext.Error("请选择生病当天的日期", "SICK_LEAVE_SCHEDULE_REQUIRED");
                }
                // 每个日期取员工在本店当天已发布的排班（同一天多个班都算）；某天没有排班就没法按排班算病假工时。
                var dayEndExclusive = sickDates[^1].AddDays(1);
                var schedules = (await _db.Queryable<AttendanceSchedule>()
                        .Where(item => !item.IsDeleted && item.Status == "Active" && item.StoreCode == storeCode
                            && item.UserGuid == userGuid && item.WorkDate >= sickDates[0] && item.WorkDate < dayEndExclusive)
                        .ToListAsync())
                    .Where(item => sickDates.Contains(item.WorkDate.Date))
                    .ToList();
                var missingDate = sickDates.FirstOrDefault(date => schedules.All(item => item.WorkDate.Date != date));
                if (missingDate != default)
                {
                    return ManualLeaveContext.Error($"{missingDate:MM-dd} 员工在本店没有排班，病假要按排班补录", "SICK_LEAVE_SCHEDULE_INVALID");
                }
                scheduleGuids = schedules.Select(item => item.ScheduleGuid).ToList();
                // 与员工自己申请一致：已有打卡的班不能算病假（要么上班了，要么没来）。
                var punched = await _db.Queryable<AttendancePunch>()
                    .AnyAsync(item => !item.IsDeleted && item.ScheduleGuid != null && scheduleGuids.Contains(item.ScheduleGuid));
                if (punched)
                {
                    return ManualLeaveContext.Error("已有打卡的排班不能补录病假", "SICK_LEAVE_SCHEDULE_PUNCHED");
                }
                days = schedules
                    .GroupBy(item => item.WorkDate.Date)
                    .OrderBy(group => group.Key)
                    .Select(group => new AttendanceManualLeaveDayDto
                    {
                        Date = group.Key,
                        Hours = PayrollCalculator.Round2(group.Sum(SickLeaveHoursForSchedule)),
                    })
                    .ToList();
                start = days[0].Date;
                end = days[^1].Date;
                paidHours = PayrollCalculator.Round2(days.Sum(item => item.Hours));
            }
            else
            {
                if (request.StartDate == default || request.EndDate == default)
                {
                    return ManualLeaveContext.Error("请假日期不能为空", "DATE_REQUIRED");
                }
                start = request.StartDate.Date;
                end = request.EndDate.Date;
                if (start > end)
                {
                    return ManualLeaveContext.Error("结束日期不能早于开始日期", "INVALID_DATE_RANGE");
                }
                // 单次上限沿用设置里的「年假单次最多 N 周」，防止误选一个超长区间。
                var calendarDays = (end - start).Days + 1;
                if (calendarDays > Math.Max(1, settings.AnnualLeaveMaxWeeks) * 7)
                {
                    return ManualLeaveContext.Error($"单次最多补录 {Math.Max(1, settings.AnnualLeaveMaxWeeks)} 周", "ANNUAL_LEAVE_TOO_LONG");
                }
                var holidaySet = (await LoadStorePublicHolidaysAsync(storeCode, start, end)).Keys.ToHashSet();
                defaultPaidHours = PayrollCalculator.AnnualLeaveRequestedHours(start, end, holidaySet);
                if (defaultPaidHours <= 0)
                {
                    return ManualLeaveContext.Error("所选日期没有工作日（周末和公共假期不计年假）", "ANNUAL_LEAVE_NO_WORKDAY");
                }
                // 合计小时数是整段区间的总数（不是每天）：不填默认 工作日 × 8；填了则按工作日平均摊。
                paidHours = request.TotalHours is { } requestedTotal ? PayrollCalculator.Round2(requestedTotal) : defaultPaidHours;
                if (paidHours <= 0)
                {
                    return ManualLeaveContext.Error("年假小时数必须大于 0", "ANNUAL_LEAVE_HOURS_INVALID");
                }
                if (paidHours > calendarDays * ManualLeaveMaxHoursPerCalendarDay)
                {
                    return ManualLeaveContext.Error(
                        $"年假小时数不能超过 {calendarDays * ManualLeaveMaxHoursPerCalendarDay}（所选 {calendarDays} 天 × 24 小时）",
                        "ANNUAL_LEAVE_HOURS_TOO_MANY");
                }
                var spread = PayrollCalculator.SpreadAnnualLeaveHours(start, end, holidaySet, paidHours);
                for (var date = start; date <= end; date = date.AddDays(1))
                {
                    days.Add(new AttendanceManualLeaveDayDto { Date = date, Hours = spread.GetValueOrDefault(date) });
                }
            }

            // 补录只能补已经发生的日期：包括 Admin 在内一律禁止未来。
            if (end > today)
            {
                return ManualLeaveContext.Error("补录日期不能晚于今天", "LEAVE_DATE_IN_FUTURE");
            }
            if (!IsAdmin() && (!IsWithinManagedAdjustmentWindow(start, today) || !IsWithinManagedAdjustmentWindow(end, today)))
            {
                return ManualLeaveContext.Error("只能修改本周的考勤；周一、周二仍可修改上周", "ADJUSTMENT_WINDOW_EXPIRED");
            }
            if (await IsPayPeriodLockedAsync(storeCode, start, end))
            {
                return ManualLeaveContext.Error(PeriodLockedMessage, PeriodLockedErrorCode);
            }

            // 带薪年假（休息）：有工时的工作日不能已有打卡（休了假就不该上班；真上了班应改成补录工时 / 修改打卡）。
            // 选了「不休息」（照常上班、年假另外加算）的不查打卡。
            if (leaveType == PaidAnnualLeaveType && restsOnLeaveDays)
            {
                var workdays = days.Where(item => item.Hours > 0).Select(item => item.Date).ToList();
                var dayEnd = end.AddDays(1);
                var punchedDates = (await _db.Queryable<AttendancePunch>()
                        .Where(item => !item.IsDeleted && item.UserGuid == userGuid && item.StoreCode == storeCode
                            && item.WorkDate >= start && item.WorkDate < dayEnd)
                        .Select(item => item.WorkDate)
                        .ToListAsync())
                    .Select(item => item.Date)
                    .Where(workdays.Contains)
                    .Distinct()
                    .OrderBy(item => item)
                    .ToList();
                if (punchedDates.Count > 0)
                {
                    return ManualLeaveContext.Error(
                        $"{punchedDates[0]:MM-dd} 已有打卡记录，不能同时记年假；请先处理那天的打卡",
                        "ANNUAL_LEAVE_HAS_PUNCHES");
                }
            }

            // 与员工已有的请假冲突：病假 / 带薪年假互相占天（不休息的年假也算，免得同一天被记两份带薪工时）。
            // 历史的年假发放记录不参与冲突判断（它本来就照常上班）。
            var overlapping = await _db.Queryable<AttendanceLeaveRequest>()
                .Where(item => !item.IsDeleted && item.StoreCode == storeCode && item.UserGuid == userGuid
                    && (item.Status == "Pending" || item.Status == "Approved")
                    && item.StartDate <= end && item.EndDate >= start)
                .ToListAsync();
            var coveredDates = days.Select(item => item.Date).ToHashSet();
            foreach (var existing in overlapping)
            {
                if (existing.LeaveType is not (PaidSickLeaveType or PaidAnnualLeaveType))
                {
                    continue;
                }
                var existingDates = await ResolveLeaveCoveredDatesAsync(existing);
                if (existingDates.Overlaps(coveredDates))
                {
                    return ManualLeaveContext.Error(
                        $"{existingDates.Where(coveredDates.Contains).Min():MM-dd} 已有请假记录，请先撤销再补录",
                        "LEAVE_OVERLAP");
                }
            }

            var leave = new AttendanceLeaveRequest
            {
                LeaveGuid = Guid.NewGuid().ToString(),
                StoreCode = storeCode,
                UserGuid = userGuid,
                LeaveType = leaveType,
                StartDate = start,
                EndDate = end,
                Reason = reason,
                Status = "Approved",
                // 前缀只用来识别「补录建的」；理由本身在 Reason 里。
                ReviewRemark = (restsOnLeaveDays ? ManualLeaveRemarkPrefix : ManualLeaveNoRestRemarkPrefix) + reason,
                PaidHours = paidHours,
                ScheduleGuids = leaveType == PaidSickLeaveType ? SerializeStringList(scheduleGuids) : null,
            };
            var preview = new AttendanceManualLeavePreviewDto
            {
                LeaveType = leaveType,
                StartDate = start,
                EndDate = end,
                PaidHours = paidHours,
                DefaultPaidHours = defaultPaidHours,
                RestsOnLeaveDays = restsOnLeaveDays,
                Days = days,
                NoCertificate = leaveType == PaidSickLeaveType,
            };
            // 版本只取「决定这次补录结果的输入」：员工 / 类型 / 日期或所选排班 / 原因 / 合计小时数 / 是否休息。请假 Guid 每次预览都是新的，不进版本。
            var revisionSource = string.Join(
                "|",
                userGuid,
                storeCode,
                leaveType,
                start.ToString("yyyy-MM-dd"),
                end.ToString("yyyy-MM-dd"),
                string.Join(",", scheduleGuids.OrderBy(item => item, StringComparer.OrdinalIgnoreCase)),
                reason,
                paidHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
                restsOnLeaveDays ? "rest" : "work");
            preview.PreviewRevision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionSource)));
            return ManualLeaveContext.OK(leave, preview);
        }

        /// <summary>一条请假实际占用的日期：病假只算所选排班那几天；年假 / 年假发放按整个区间。</summary>
        private async Task<HashSet<DateTime>> ResolveLeaveCoveredDatesAsync(AttendanceLeaveRequest leave)
        {
            if (leave.LeaveType == PaidSickLeaveType)
            {
                var guids = ParseJsonStringList(leave.ScheduleGuids);
                if (guids.Count > 0)
                {
                    var dates = (await _db.Queryable<AttendanceSchedule>()
                            .Where(item => guids.Contains(item.ScheduleGuid))
                            .Select(item => item.WorkDate)
                            .ToListAsync())
                        .Select(item => item.Date)
                        .ToHashSet();
                    if (dates.Count > 0)
                    {
                        return dates;
                    }
                }
            }
            var covered = new HashSet<DateTime>();
            for (var date = leave.StartDate.Date; date <= leave.EndDate.Date; date = date.AddDays(1))
            {
                covered.Add(date);
            }
            return covered;
        }
    }
}
