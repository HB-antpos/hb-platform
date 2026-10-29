using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 午夜自动下班：门店时区过了 00:00，排班日仍未闭合的班段由系统补一张下班卡（IsSystemAutoClockOut，时间 = 排班日 24:00）。
    /// 该段不计薪，并生成「漏下班」待处理；店长「填实际下班时间」＝补卡覆盖这张自动卡，补卡生效时漏下班以已批准结案、不再重开。
    /// 由 AttendanceAutoClockOutWorker 定时调用，也可被测试直接调用；整体幂等，重复跑只会处理仍未闭合的班段。
    /// </summary>
    public partial class AttendanceReactService
    {
        /// <summary>回看几天的排班：后台停机一两天后恢复，也能把漏掉的午夜补上。</summary>
        internal const int AutoClockOutLookbackDays = 3;

        internal const string AutoClockOutSource = "SystemAutoClockOut";

        internal const string AutoClockOutStatus = "AutoClockOut";

        /// <summary>补自动下班卡，返回本轮补了几张。单个员工失败只记日志，不影响其他员工。</summary>
        public async Task<int> RunMidnightAutoClockOutAsync(CancellationToken cancellationToken = default)
        {
            var settings = await GetOrCreateSettingsModelAsync();
            if (!settings.AutoClockOutAtMidnight)
            {
                return 0;
            }

            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            // 澳洲门店本地日期比 UTC 早一天开始；排班日 < 门店今天才算「过了午夜」，下面逐店再精确判断。
            var from = nowUtc.Date.AddDays(-AutoClockOutLookbackDays);
            var until = nowUtc.Date.AddDays(1);
            var schedules = await _db.Queryable<AttendanceSchedule>()
                .Where(item =>
                    !item.IsDeleted
                    && item.Status == "Active"
                    && item.WorkDate >= from
                    && item.WorkDate < until)
                .ToListAsync();
            if (schedules.Count == 0)
            {
                return 0;
            }

            var scheduleGuids = schedules.Select(item => item.ScheduleGuid).ToList();
            var punches = await _db.Queryable<AttendancePunch>()
                .Where(item => !item.IsDeleted
                    && item.ScheduleGuid != null
                    && scheduleGuids.Contains(item.ScheduleGuid))
                .ToListAsync();
            var segmentLimitMap = await ResolveSegmentLimitMapAsync(schedules);
            var timeZoneCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<(AttendanceSchedule Schedule, string TimeZone)>();
            foreach (var schedule in schedules)
            {
                if (!timeZoneCache.TryGetValue(schedule.StoreCode, out var timeZone))
                {
                    timeZone = await ResolveStoreTimeZoneAsync(schedule.StoreCode, null);
                    timeZoneCache[schedule.StoreCode] = timeZone;
                }
                var nowLocal = ConvertUtcToStoreLocal(nowUtc, timeZone);
                if (schedule.WorkDate.Date >= nowLocal.Date)
                {
                    continue;
                }
                var session = AttendanceWorkSessionCalculator.Calculate(
                    schedule,
                    punches,
                    segmentLimitMap.GetValueOrDefault(schedule.UserGuid, ResolveEmployeeSegmentLimit(settings)),
                    nowLocal,
                    settings.EarlyLeaveGraceMinutes,
                    settings.LateGraceMinutes);
                if (session.HasOpenSegment)
                {
                    candidates.Add((schedule, timeZone));
                }
            }

            var created = 0;
            foreach (var userCandidates in candidates.GroupBy(
                item => item.Schedule.UserGuid,
                StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    created += await AutoClockOutEmployeeAsync(userCandidates.Key, userCandidates.ToList());
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(
                        exception,
                        "午夜自动下班失败，下一轮重试: UserGuid={UserGuid}",
                        userCandidates.Key);
                }
            }

            return created;
        }

        private async Task<int> AutoClockOutEmployeeAsync(
            string userGuid,
            List<(AttendanceSchedule Schedule, string TimeZone)> candidates)
        {
            // 与打卡、补卡、审批同一把员工锁：锁内重读，员工或店长刚好在这一刻补了下班卡就不再重复补。
            var mutationResource = AttendanceDailyMutationLock.BuildResource(userGuid, string.Empty, default);
            await using var processLock = await AttendanceDailyMutationLock.AcquireProcessAsync(mutationResource);
            var created = 0;
            await _db.Ado.BeginTranAsync();
            try
            {
                await AttendanceDailyMutationLock.AcquireDatabaseAsync(_db, mutationResource);
                foreach (var (snapshot, timeZone) in candidates)
                {
                    var schedule = await _db.Queryable<AttendanceSchedule>().FirstAsync(item =>
                        !item.IsDeleted
                        && item.Status == "Active"
                        && item.ScheduleGuid == snapshot.ScheduleGuid);
                    if (schedule == null)
                    {
                        continue;
                    }

                    var session = await BuildWorkSessionAsync(schedule);
                    if (!session.HasOpenSegment)
                    {
                        continue;
                    }

                    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    // 排班日的 24:00（即次日 00:00）按门店时区换成 UTC。
                    var autoLocal = schedule.WorkDate.Date.AddDays(1);
                    await _db.Insertable(new AttendancePunch
                    {
                        PunchGuid = Guid.NewGuid().ToString(),
                        ScheduleGuid = schedule.ScheduleGuid,
                        StoreCode = schedule.StoreCode,
                        UserGuid = schedule.UserGuid,
                        WorkDate = schedule.WorkDate.Date,
                        StoreTimeZone = timeZone,
                        PunchType = "ClockOut",
                        PunchTimeLocal = autoLocal,
                        PunchTimeUtc = DateTime.SpecifyKind(
                            ConvertStoreLocalToUtc(autoLocal, timeZone),
                            DateTimeKind.Utc),
                        Status = AutoClockOutStatus,
                        Source = AutoClockOutSource,
                        IsSystemAutoClockOut = true,
                        Remark = "门店时区 00:00 仍未下班，系统自动补下班卡（该段不计薪，待店长填实际下班时间）",
                        CreatedAt = nowUtc,
                        CreatedBy = "system",
                    }).ExecuteCommandAsync();

                    // 忘记结束的休息按零时长结束，不算一次用餐，也不挡住之后开始新的休息。
                    var openBreak = await _db.Queryable<AttendanceMealBreak>().FirstAsync(item =>
                        item.UserGuid == schedule.UserGuid
                        && item.ScheduleGuid == schedule.ScheduleGuid
                        && item.EndUtc == null);
                    if (openBreak != null)
                    {
                        openBreak.EndUtc = openBreak.StartUtc;
                        await _db.Updateable(openBreak).ExecuteCommandAsync();
                    }

                    // 自动卡闭合后计算器视为漏下班：这里生成漏下班待处理，并撤掉不再成立的加班 / 打卡异常待审。
                    await ReconcileDerivedApprovalsForScheduleAsync(schedule.ScheduleGuid);
                    created++;
                }

                await _db.Ado.CommitTranAsync();
            }
            catch
            {
                await _db.Ado.RollbackTranAsync();
                throw;
            }

            if (created > 0)
            {
                _logger.LogInformation(
                    "已为员工补午夜自动下班卡 {Count} 张: UserGuid={UserGuid}",
                    created,
                    userGuid);
            }
            return created;
        }
    }
}
