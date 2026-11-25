using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 整班未打卡（缺勤）待审，方案见 docs/plans/2026-11-25-no-show-shift-approval.md。
    /// 排班结束 + 宽限分钟后仍一次卡都没打、也没有请假覆盖，就生成 SourceType = NoShow 的待审（SourceGuid = 排班）给店长；
    /// 店长只能「确认缺勤」（批准），或去补录打卡 / 补录带薪假（自动取消）。待审计入工资期待处理数，清零前不能提交。
    /// 生成入口：后台定时任务（回看 2 天）、店长打开审核列表（近 14 天派生对账）、打开工时统计 / 提交工时（本期）。
    /// </summary>
    public partial class AttendanceReactService
    {
        internal const string NoShowSourceType = "NoShow";

        /// <summary>从这一天（含）起的排班才生成缺勤待审：上线时回溯到上一周（2026-11-16 那周），更早的不翻旧账。</summary>
        internal static readonly DateTime NoShowDetectionStartDate = new(2026, 11, 16);

        internal const int MinNoShowGraceMinutes = 15;
        internal const int MaxNoShowGraceMinutes = 720;

        /// <summary>后台定时任务回看几天：停机一两天后恢复也能补上；更早的由打开工时统计 / 审核列表补生成。</summary>
        private const int NoShowWorkerLookbackDays = 2;

        private const string NoShowResolvedRemark = "已有打卡或请假，缺勤待审已自动取消";

        /// <summary>排班窗口的结束时刻（门店本地时间）；结束 ≤ 开始视为跨午夜，落到次日。</summary>
        internal static DateTime ResolveScheduleWindowEnd(AttendanceSchedule schedule)
        {
            var start = schedule.WorkDate.Date.Add(schedule.StartTime);
            var end = schedule.WorkDate.Date.Add(schedule.EndTime);
            return end <= start ? end.AddDays(1) : end;
        }

        /// <summary>
        /// 到没到该判缺勤的时候（纯规则，便于单测）：开关打开、排班日不早于启用日、
        /// 门店本地时间已过「排班结束 + 宽限分钟」。宽限按 15–720 夹紧，防止库里残留异常值。
        /// </summary>
        internal static bool IsNoShowDue(AttendanceSchedule schedule, AttendanceSettings settings, DateTime nowLocal)
        {
            if (!settings.NoShowApprovalEnabled || schedule.WorkDate.Date < NoShowDetectionStartDate)
            {
                return false;
            }
            var grace = Math.Clamp(settings.NoShowGraceMinutes, MinNoShowGraceMinutes, MaxNoShowGraceMinutes);
            return nowLocal >= ResolveScheduleWindowEnd(schedule).AddMinutes(grace);
        }

        /// <summary>
        /// 这个班是否已被请假覆盖（纯规则，便于单测）。只看已批准 / 待审的假：
        /// - 带薪病假：选了这个排班；老数据没选排班时按日期区间算；
        /// - 带薪年假（休息）：日期落在区间内；补录时选了「不休息」的不算（员工照常上班）；
        /// - 年假发放：不算（照常上班，只是多发钱）；
        /// - 其他旧类型（无薪等）：日期落在区间内就算，缺勤已有交代。
        /// </summary>
        internal static bool IsScheduleCoveredByLeave(AttendanceSchedule schedule, IEnumerable<AttendanceLeaveRequest> leaves)
        {
            var workDate = schedule.WorkDate.Date;
            foreach (var leave in leaves)
            {
                if (leave.IsDeleted
                    || !leave.UserGuid.Equals(schedule.UserGuid, StringComparison.OrdinalIgnoreCase)
                    || leave.Status is not ("Approved" or "Pending"))
                {
                    continue;
                }
                var inRange = leave.StartDate.Date <= workDate && leave.EndDate.Date >= workDate;
                if (leave.LeaveType == PaidSickLeaveType)
                {
                    var scheduleGuids = ParseJsonStringList(leave.ScheduleGuids);
                    if (scheduleGuids.Count == 0
                        ? inRange
                        : scheduleGuids.Contains(schedule.ScheduleGuid, StringComparer.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    continue;
                }
                if (leave.LeaveType == AnnualCashOutLeaveType)
                {
                    continue;
                }
                if (leave.LeaveType == PaidAnnualLeaveType && IsManualNoRestAnnual(leave))
                {
                    continue;
                }
                if (inRange)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 除排班自己的打卡外，这个班还算不算缺勤：当天本店有没挂上排班的打卡（属于收编逻辑，不能误报）、
        /// 或有请假覆盖，都不算。调用方已确认排班名下零打卡。
        /// </summary>
        private async Task<bool> HasNoShowExemptionAsync(AttendanceSchedule schedule)
        {
            var workDate = schedule.WorkDate.Date;
            var nextDay = workDate.AddDays(1);
            var userGuid = schedule.UserGuid;
            var storeCode = schedule.StoreCode;
            var hasLoosePunch = await _db.Queryable<AttendancePunch>().AnyAsync(item =>
                !item.IsDeleted
                && item.ScheduleGuid == null
                && item.UserGuid == userGuid
                && item.StoreCode == storeCode
                && item.WorkDate >= workDate
                && item.WorkDate < nextDay);
            if (hasLoosePunch)
            {
                return true;
            }
            var leaves = await _db.Queryable<AttendanceLeaveRequest>()
                .Where(item => !item.IsDeleted
                    && item.UserGuid == userGuid
                    && (item.Status == "Approved" || item.Status == "Pending")
                    && item.StartDate < nextDay
                    && item.EndDate >= workDate)
                .ToListAsync();
            return IsScheduleCoveredByLeave(schedule, leaves);
        }

        /// <summary>审批时的复核：排班仍有效、名下零打卡、且没有豁免（零散打卡 / 请假）。</summary>
        private async Task<bool> IsScheduleStillNoShowAsync(AttendanceSchedule schedule)
        {
            var scheduleGuid = schedule.ScheduleGuid;
            var hasSchedulePunch = await _db.Queryable<AttendancePunch>().AnyAsync(item =>
                !item.IsDeleted && item.ScheduleGuid == scheduleGuid);
            return !hasSchedulePunch && !await HasNoShowExemptionAsync(schedule);
        }

        /// <summary>
        /// 单个排班的缺勤待审对账（在员工锁 + 事务内调用，与漏下班对账同一位置）：
        /// 有打卡 / 有豁免 → 取消待审；到点且未锁定 → 生成（已批准 / 待审的不重复，系统取消过的可重开）。
        /// hasSchedulePunches 由调用方用锁内重读的打卡给出，避免重复查询。
        /// </summary>
        private async Task ReconcileNoShowApprovalAsync(
            AttendanceSchedule schedule,
            bool hasSchedulePunches,
            AttendanceSettings settings,
            DateTime nowLocal)
        {
            if (hasSchedulePunches)
            {
                await CancelPendingApprovalsAsync(NoShowSourceType, schedule.ScheduleGuid, NoShowResolvedRemark);
                return;
            }
            // 没到点：正常不会有待审（宽限调大的极端情况留给到点后的对账处理），直接跳过省查询。
            if (!IsNoShowDue(schedule, settings, nowLocal))
            {
                return;
            }
            if (await HasNoShowExemptionAsync(schedule))
            {
                await CancelPendingApprovalsAsync(NoShowSourceType, schedule.ScheduleGuid, NoShowResolvedRemark);
                return;
            }
            // 已提交（锁定）的工资期不再塞新待审，否则锁定期里出现解不开的待处理。
            if (await IsPayPeriodLockedAsync(schedule.StoreCode, schedule.WorkDate))
            {
                return;
            }
            await CreatePendingApprovalAsync(
                NoShowSourceType,
                schedule.ScheduleGuid,
                schedule.StoreCode,
                schedule.UserGuid);
        }

        /// <summary>后台定时任务：全公司近 2 天（且不早于启用日）该判缺勤的排班，逐员工加锁对账。返回候选排班数。</summary>
        public async Task<int> RunNoShowDetectionAsync(CancellationToken cancellationToken = default)
        {
            var settings = await GetOrCreateSettingsModelAsync();
            if (!settings.NoShowApprovalEnabled)
            {
                return 0;
            }
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            // 澳洲门店本地日期比 UTC 早，until 放到 UTC 明天，逐店再按门店时区精确判断是否到点。
            var from = Max(NoShowDetectionStartDate, nowUtc.Date.AddDays(-NoShowWorkerLookbackDays));
            var until = nowUtc.Date.AddDays(1);
            var schedules = await _db.Queryable<AttendanceSchedule>()
                .Where(item => !item.IsDeleted
                    && item.Status == "Active"
                    && item.WorkDate >= from
                    && item.WorkDate < until)
                .ToListAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = await FilterNoShowCandidatesAsync(schedules, settings, nowUtc);
            if (candidates.Count == 0)
            {
                return 0;
            }
            await ReconcileDerivedApprovalsUnderEmployeeLocksAsync(candidates);
            return candidates.Count;
        }

        /// <summary>
        /// 打开工时统计 / 提交工时前，给本店本期补生成缺勤待审（回溯旧周靠这里）。
        /// 失败只记日志，不阻断统计；提交路径里新生成的待审会让 PendingCount &gt; 0，自然挡住提交。
        /// </summary>
        private async Task GenerateNoShowApprovalsForStorePeriodAsync(string storeCode, DateTime periodStart, DateTime periodEnd)
        {
            try
            {
                var settings = await GetOrCreateSettingsModelAsync();
                if (!settings.NoShowApprovalEnabled)
                {
                    return;
                }
                var from = Max(NoShowDetectionStartDate, periodStart.Date);
                var until = periodEnd.Date.AddDays(1);
                if (from >= until)
                {
                    return;
                }
                var schedules = await _db.Queryable<AttendanceSchedule>()
                    .Where(item => !item.IsDeleted
                        && item.Status == "Active"
                        && item.StoreCode == storeCode
                        && item.WorkDate >= from
                        && item.WorkDate < until)
                    .ToListAsync();
                var candidates = await FilterNoShowCandidatesAsync(
                    schedules,
                    settings,
                    _timeProvider.GetUtcNow().UtcDateTime);
                if (candidates.Count > 0)
                {
                    await ReconcileDerivedApprovalsUnderEmployeeLocksAsync(candidates);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "工时统计前生成缺勤待审失败，统计照常返回: StoreCode={StoreCode}, PeriodStart={PeriodStart}",
                    storeCode,
                    periodStart);
            }
        }

        /// <summary>
        /// 锁外预筛（只读）：到点、名下零打卡、还没有未取消的缺勤待审的排班才进入加锁对账；
        /// 锁内还会重读打卡与请假再决定，所以这里宁多勿少不影响正确性，只为少开事务。
        /// </summary>
        private async Task<List<AttendanceSchedule>> FilterNoShowCandidatesAsync(
            List<AttendanceSchedule> schedules,
            AttendanceSettings settings,
            DateTime nowUtc)
        {
            if (schedules.Count == 0)
            {
                return schedules;
            }
            var timeZones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var due = new List<AttendanceSchedule>();
            foreach (var schedule in schedules)
            {
                if (!timeZones.TryGetValue(schedule.StoreCode, out var timeZone))
                {
                    timeZone = await ResolveStoreTimeZoneAsync(schedule.StoreCode, null);
                    timeZones[schedule.StoreCode] = timeZone;
                }
                if (IsNoShowDue(schedule, settings, ConvertUtcToStoreLocal(nowUtc, timeZone)))
                {
                    due.Add(schedule);
                }
            }
            if (due.Count == 0)
            {
                return due;
            }
            var dueGuids = due.Select(item => item.ScheduleGuid).ToList();
            var punchedGuids = (await _db.Queryable<AttendancePunch>()
                    .Where(item => !item.IsDeleted && item.ScheduleGuid != null && dueGuids.Contains(item.ScheduleGuid))
                    .Select(item => item.ScheduleGuid)
                    .ToListAsync())
                .Where(item => item != null)
                .Select(item => item!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var handledGuids = (await _db.Queryable<AttendanceApproval>()
                    .Where(item => !item.IsDeleted
                        && item.SourceType == NoShowSourceType
                        && item.ReviewStatus != "Cancelled"
                        && dueGuids.Contains(item.SourceGuid))
                    .Select(item => item.SourceGuid)
                    .ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return due
                .Where(item => !punchedGuids.Contains(item.ScheduleGuid) && !handledGuids.Contains(item.ScheduleGuid))
                .ToList();
        }

        /// <summary>
        /// 请假提交 / 批准 / 补录后，取消被这条假覆盖的排班的缺勤待审（在调用方事务内）。
        /// 请假之后被拒绝 / 撤销时，缺勤会在下一次对账重新生成（系统取消不算结案）。
        /// </summary>
        private async Task CancelNoShowApprovalsCoveredByLeaveAsync(AttendanceLeaveRequest leave)
        {
            var from = leave.StartDate.Date;
            var until = leave.EndDate.Date.AddDays(1);
            var userGuid = leave.UserGuid;
            var schedules = await _db.Queryable<AttendanceSchedule>()
                .Where(item => !item.IsDeleted
                    && item.UserGuid == userGuid
                    && item.WorkDate >= from
                    && item.WorkDate < until)
                .ToListAsync();
            if (schedules.Count == 0)
            {
                return;
            }
            var scheduleGuids = schedules.Select(item => item.ScheduleGuid).ToList();
            var pendingGuids = (await _db.Queryable<AttendanceApproval>()
                    .Where(item => !item.IsDeleted
                        && item.SourceType == NoShowSourceType
                        && item.ReviewStatus == "Pending"
                        && scheduleGuids.Contains(item.SourceGuid))
                    .Select(item => item.SourceGuid)
                    .ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var schedule in schedules.Where(item => pendingGuids.Contains(item.ScheduleGuid)))
            {
                if (IsScheduleCoveredByLeave(schedule, new[] { leave }))
                {
                    await CancelPendingApprovalsAsync(NoShowSourceType, schedule.ScheduleGuid, NoShowResolvedRemark);
                }
            }
        }
    }
}
