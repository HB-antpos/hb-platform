using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 未挂排班打卡的「挂回」。
    /// 打卡只在写入那一刻匹配一次已发布排班；排班晚建或晚发布时，之前的打卡 ScheduleGuid 为空，
    /// 之后既不计入排班工时（计薪只认挂在排班上的打卡），审核时还会被判成「状态已变化」。
    /// 这里把这些打卡按「排班在打卡当时就已发布」的口径逐张重放，结论与实时打卡一致的才挂回去。
    /// </summary>
    public partial class AttendanceReactService
    {
        /// <summary>
        /// 实时打卡与重放共用的排班选择规则：优先仍有未下班班段的排班；否则在「未完成、班段未到上限、
        /// 且不是已经过了下班时间还没开始」的排班里取离打卡时间最近的一个。
        /// schedulePunches 只需包含这些排班名下的打卡；重放时传「截至该打卡时刻」的打卡。
        /// </summary>
        internal static AttendanceSchedule? SelectScheduleForPunch(
            IReadOnlyCollection<AttendanceSchedule> activeSchedules,
            IReadOnlyCollection<AttendancePunch> schedulePunches,
            DateTime workDate,
            TimeSpan localTime,
            int segmentLimit,
            AttendanceSettings settings)
        {
            if (activeSchedules.Count == 0)
            {
                return null;
            }

            var nowLocal = workDate.Date.Add(localTime);
            var sessions = activeSchedules.Select(schedule => new
            {
                Schedule = schedule,
                Session = AttendanceWorkSessionCalculator.Calculate(
                    schedule,
                    schedulePunches,
                    segmentLimit,
                    nowLocal,
                    settings.EarlyLeaveGraceMinutes,
                    settings.LateGraceMinutes,
                    settings.OvertimeThresholdMinutes),
            }).ToList();

            var open = sessions
                .Where(item => item.Session.HasOpenSegment)
                .OrderByDescending(item => item.Session.Segments.Last().ClockIn?.PunchTimeLocal)
                .FirstOrDefault();
            if (open != null)
            {
                return open.Schedule;
            }

            return sessions
                .Where(item => item.Session.ScheduleState != "Completed")
                .Where(item => item.Session.Segments.Count < segmentLimit)
                .Where(item => item.Session.ScheduleState != "NotStarted"
                    || localTime < item.Schedule.EndTime)
                .OrderBy(item => localTime < item.Schedule.StartTime
                    ? (item.Schedule.StartTime - localTime).TotalMinutes
                    : localTime > item.Schedule.EndTime
                        ? (localTime - item.Schedule.EndTime).TotalMinutes
                        : 0)
                .ThenBy(item => item.Schedule.StartTime)
                .Select(item => item.Schedule)
                .FirstOrDefault();
        }

        /// <summary>
        /// 批量入口：对一批已发布排班（同一员工，或发布整周时的多名员工）所在的「员工 + 店 + 日」做挂回。
        /// 先用一次查询找出哪些「员工 + 店 + 日」真的有未挂排班的打卡——绝大多数时候没有，
        /// 这样读接口顺带的对账不会多出逐日查询。调用方须已持有这些员工的考勤锁并在事务内。
        /// </summary>
        private async Task<List<string>> AdoptUnscheduledPunchesForSchedulesAsync(
            IReadOnlyCollection<AttendanceSchedule> activeSchedules,
            AttendanceSettings settings)
        {
            var adoptedScheduleGuids = new List<string>();
            if (activeSchedules.Count == 0)
            {
                return adoptedScheduleGuids;
            }

            var userGuids = activeSchedules.Select(item => item.UserGuid).Distinct().ToList();
            var storeCodes = activeSchedules.Select(item => item.StoreCode).Distinct().ToList();
            var from = activeSchedules.Min(item => item.WorkDate).Date;
            var until = activeSchedules.Max(item => item.WorkDate).Date.AddDays(1);
            var orphanDays = (await _db.Queryable<AttendancePunch>()
                    .Where(item =>
                        !item.IsDeleted
                        && userGuids.Contains(item.UserGuid)
                        && storeCodes.Contains(item.StoreCode)
                        && item.WorkDate >= from
                        && item.WorkDate < until
                        && SqlSugar.SqlFunc.IsNullOrEmpty(item.ScheduleGuid))
                    .Select(item => new { item.UserGuid, item.StoreCode, item.WorkDate })
                    .ToListAsync())
                .Select(item => BuildAdoptionDayKey(item.UserGuid, item.StoreCode, item.WorkDate))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (orphanDays.Count == 0)
            {
                return adoptedScheduleGuids;
            }

            foreach (var day in activeSchedules
                .Where(item => orphanDays.Contains(BuildAdoptionDayKey(item.UserGuid, item.StoreCode, item.WorkDate)))
                .GroupBy(
                    item => BuildAdoptionDayKey(item.UserGuid, item.StoreCode, item.WorkDate),
                    StringComparer.OrdinalIgnoreCase))
            {
                var sample = day.First();
                adoptedScheduleGuids.AddRange(await AdoptUnscheduledPunchesAsync(
                    sample.StoreCode,
                    sample.UserGuid,
                    sample.WorkDate,
                    settings));
            }
            return adoptedScheduleGuids
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 整周发布之后：只挑出「发布前员工就已经打过卡」的那些排班，逐员工加锁做挂回 + 派生审批对账。
        /// 排班已经发布成功，这里失败只记日志——之后员工打开今日打卡、店长打开审核列表时还会再对账一次。
        /// </summary>
        private async Task AdoptUnscheduledPunchesAfterPublishAsync(
            string storeCode,
            List<AttendanceSchedule> publishedSchedules)
        {
            if (publishedSchedules.Count == 0)
            {
                return;
            }

            try
            {
                var from = publishedSchedules.Min(item => item.WorkDate).Date;
                var until = publishedSchedules.Max(item => item.WorkDate).Date.AddDays(1);
                var orphanDays = (await _db.Queryable<AttendancePunch>()
                        .Where(item =>
                            !item.IsDeleted
                            && item.StoreCode == storeCode
                            && item.WorkDate >= from
                            && item.WorkDate < until
                            && SqlSugar.SqlFunc.IsNullOrEmpty(item.ScheduleGuid))
                        .Select(item => new { item.UserGuid, item.StoreCode, item.WorkDate })
                        .ToListAsync())
                    .Select(item => BuildAdoptionDayKey(item.UserGuid, item.StoreCode, item.WorkDate))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var affected = publishedSchedules
                    .Where(item => orphanDays.Contains(
                        BuildAdoptionDayKey(item.UserGuid, item.StoreCode, item.WorkDate)))
                    .ToList();
                if (affected.Count > 0)
                {
                    await ReconcileDerivedApprovalsUnderEmployeeLocksAsync(affected);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "发布排班后挂回未排班打卡失败，排班照常发布: StoreCode={StoreCode}",
                    storeCode);
            }
        }

        private static string BuildAdoptionDayKey(string userGuid, string storeCode, DateTime workDate) =>
            $"{userGuid}|{storeCode}|{workDate:yyyyMMdd}";

        /// <summary>
        /// 把该员工在该店该日尚未挂排班的打卡挂回已发布排班，返回有打卡被挂上的排班 GUID。
        /// 必须在员工考勤锁和事务内调用；本方法只改打卡的 ScheduleGuid / Status，
        /// 派生审批（打卡异常、漏下班、加班）由调用方随后对这些排班做对账。
        /// 保守原则——以下情况整天或单张跳过，留给店长补卡处理：
        /// 工资周期已锁；涉及补卡替换或系统自动下班卡；重放得到的上班/下班类型与实际记录不一致；
        /// 目标排班名下已有比这张卡更晚的打卡（挂回会打乱已有班段顺序）。
        /// </summary>
        private async Task<List<string>> AdoptUnscheduledPunchesAsync(
            string storeCode,
            string userGuid,
            DateTime workDate,
            AttendanceSettings settings)
        {
            var adoptedScheduleGuids = new List<string>();
            var dayStart = workDate.Date;
            var dayEnd = dayStart.AddDays(1);
            var orphans = (await _db.Queryable<AttendancePunch>()
                    .Where(item =>
                        !item.IsDeleted
                        && item.UserGuid == userGuid
                        && item.StoreCode == storeCode
                        && item.WorkDate >= dayStart
                        && item.WorkDate < dayEnd
                        && SqlSugar.SqlFunc.IsNullOrEmpty(item.ScheduleGuid))
                    .ToListAsync())
                // 与班段计算一致：先后顺序只认真实 UTC 时间线，同一时刻用 Id 兜底。
                .OrderBy(item => item.PunchTimeUtc)
                .ThenBy(item => item.Id)
                .ToList();
            if (orphans.Count == 0)
            {
                return adoptedScheduleGuids;
            }

            var activeSchedules = await _db.Queryable<AttendanceSchedule>()
                .Where(item =>
                    !item.IsDeleted
                    && item.Status == "Active"
                    && item.StoreCode == storeCode
                    && item.UserGuid == userGuid
                    && item.WorkDate >= dayStart
                    && item.WorkDate < dayEnd)
                .ToListAsync();
            if (activeSchedules.Count == 0)
            {
                return adoptedScheduleGuids;
            }
            if (await IsPayPeriodLockedAsync(storeCode, dayStart))
            {
                return adoptedScheduleGuids;
            }

            var orphanGuids = orphans.Select(item => item.PunchGuid).ToList();
            var touchedByAdjustment = orphans.Any(item =>
                    item.IsSystemAutoClockOut
                    || !string.IsNullOrWhiteSpace(item.SupersedesPunchGuid)
                    || !string.IsNullOrWhiteSpace(item.AdjustmentGuid))
                || await _db.Queryable<AttendancePunch>().AnyAsync(item =>
                    !item.IsDeleted
                    && item.SupersedesPunchGuid != null
                    && orphanGuids.Contains(item.SupersedesPunchGuid));
            if (touchedByAdjustment)
            {
                return adoptedScheduleGuids;
            }

            var scheduleGuids = activeSchedules.Select(item => item.ScheduleGuid).ToList();
            var working = await _db.Queryable<AttendancePunch>()
                .Where(item => !item.IsDeleted
                    && item.ScheduleGuid != null
                    && scheduleGuids.Contains(item.ScheduleGuid))
                .ToListAsync();
            var segmentLimit = await ResolveSegmentLimitAsync(userGuid, storeCode);
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            foreach (var orphan in orphans)
            {
                // 还原「这张卡打下去那一刻」排班名下已有的打卡，再套用与实时打卡完全相同的选择规则。
                var asOf = working
                    .Where(item => item.PunchTimeUtc < orphan.PunchTimeUtc
                        || (item.PunchTimeUtc == orphan.PunchTimeUtc && item.Id < orphan.Id))
                    .ToList();
                var localTime = orphan.PunchTimeLocal.TimeOfDay;
                var schedule = SelectScheduleForPunch(
                    activeSchedules,
                    asOf,
                    dayStart,
                    localTime,
                    segmentLimit,
                    settings);
                if (schedule == null)
                {
                    continue;
                }
                // 目标排班名下已有更晚的打卡：把这张卡插到前面会改写已有班段，不自动处理。
                if (working.Any(item =>
                        string.Equals(item.ScheduleGuid, schedule.ScheduleGuid, StringComparison.OrdinalIgnoreCase)
                        && !asOf.Contains(item)))
                {
                    continue;
                }

                var session = AttendanceWorkSessionCalculator.Calculate(
                    schedule,
                    asOf,
                    segmentLimit,
                    orphan.PunchTimeLocal,
                    settings.EarlyLeaveGraceMinutes,
                    settings.LateGraceMinutes,
                    settings.OvertimeThresholdMinutes);
                if (!session.HasOpenSegment
                    && (session.Segments.Count >= segmentLimit || session.ScheduleState == "Completed"))
                {
                    continue;
                }
                var replayedType = session.HasOpenSegment ? "ClockOut" : "ClockIn";
                if (!string.Equals(replayedType, orphan.PunchType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var isFirstClockIn = replayedType == "ClockIn" && session.Segments.Count == 0;
                var isFinalClockOut = replayedType == "ClockOut"
                    && (session.Segments.Count >= segmentLimit
                        || localTime >= schedule.EndTime.Subtract(
                            TimeSpan.FromMinutes(settings.EarlyLeaveGraceMinutes)));
                // 只改写仍是「未排班」的状态；已被店长审过（Approved / Rejected）的保留审核结论。
                var status = string.Equals(orphan.Status, "NoSchedule", StringComparison.OrdinalIgnoreCase)
                    ? ResolveSegmentPunchStatus(
                        schedule,
                        replayedType,
                        localTime,
                        settings,
                        isFirstClockIn,
                        isFinalClockOut)
                    : orphan.Status;
                var scheduleGuid = schedule.ScheduleGuid;
                var punchId = orphan.Id;
                await _db.Updateable<AttendancePunch>()
                    .SetColumns(item => item.ScheduleGuid == scheduleGuid)
                    .SetColumns(item => item.Status == status)
                    .SetColumns(item => item.UpdatedAt == now)
                    .SetColumns(item => item.UpdatedBy == "system")
                    .Where(item => item.Id == punchId)
                    .ExecuteCommandAsync();
                orphan.ScheduleGuid = scheduleGuid;
                orphan.Status = status;
                working.Add(orphan);
                if (!adoptedScheduleGuids.Contains(scheduleGuid, StringComparer.OrdinalIgnoreCase))
                {
                    adoptedScheduleGuids.Add(scheduleGuid);
                }
            }

            if (adoptedScheduleGuids.Count > 0)
            {
                _logger.LogInformation(
                    "未挂排班的打卡已挂回排班: StoreCode={StoreCode}, UserGuid={UserGuid}, WorkDate={WorkDate}, Schedules={Schedules}",
                    storeCode,
                    userGuid,
                    dayStart.ToString("yyyy-MM-dd"),
                    string.Join(",", adoptedScheduleGuids));
            }
            return adoptedScheduleGuids;
        }
    }
}
