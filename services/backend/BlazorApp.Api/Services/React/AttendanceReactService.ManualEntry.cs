using System.Data;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React
{
    // partial：店长 / Admin 在「工时统计 / 工资表」为当天没有任何打卡的员工补录整天工时。
    // 不新建表：沿用「代员工补卡」的写法——每个补录打卡对应一条已生效的 AttendancePunchAdjustment（留痕：补录人 / 原因 / 时间），
    // 打卡来源标成 ManualEntry 以便页面识别「补」角标与撤销；当天没有排班时同时新建一条当天排班（备注「补录：原因」）。
    // 校验规则全部沿用代补卡：权限与管理分店、周期锁定、可改窗口、不能补未来、班段数上限、与其他排班 / 分店班段重叠。
    public partial class AttendanceReactService
    {
        /// <summary>补录打卡的来源标记（区别于员工申请 / 店长修改单个时间点的 ManualAdjustment）。</summary>
        internal const string ManualEntryPunchSource = "ManualEntry";
        /// <summary>补录新建的排班备注前缀；撤销时据此判断排班是不是补录建的。</summary>
        internal const string ManualEntryScheduleRemarkPrefix = "补录：";
        private const string ManualEntryAdjustmentRevoked = "Revoked";
        /// <summary>原因最长 400：补录排班备注要加前缀，备注列上限 500。</summary>
        private const int ManualEntryReasonMaxLength = 400;

        private sealed class ManualEntryContext
        {
            public bool Success { get; private init; }
            public string Message { get; private init; } = string.Empty;
            public string? ErrorCode { get; private init; }
            public AttendanceSchedule? Schedule { get; private init; }
            public bool WillCreateSchedule { get; private init; }
            public List<AttendancePunch> Punches { get; private init; } = new();
            public AttendanceManualEntryPreviewDto? Preview { get; private init; }
            public string UserGuid { get; private init; } = string.Empty;

            public static ManualEntryContext Error(string message, string errorCode) =>
                new() { Success = false, Message = message, ErrorCode = errorCode };

            public static ManualEntryContext OK(
                AttendanceSchedule schedule,
                bool willCreateSchedule,
                List<AttendancePunch> punches,
                AttendanceManualEntryPreviewDto preview,
                string userGuid) => new()
                {
                    Success = true,
                    Schedule = schedule,
                    WillCreateSchedule = willCreateSchedule,
                    Punches = punches,
                    Preview = preview,
                    UserGuid = userGuid,
                };
        }

        public async Task<ApiResponse<AttendanceManualEntryPreviewDto>> PreviewManagedManualEntryAsync(
            CreateAttendanceManualEntryDto request)
        {
            var context = await BuildManualEntryContextAsync(request);
            return context.Success
                ? ApiResponse<AttendanceManualEntryPreviewDto>.OK(context.Preview!)
                : ApiResponse<AttendanceManualEntryPreviewDto>.Error(context.Message, context.ErrorCode ?? "MANUAL_ENTRY_INVALID");
        }

        public async Task<ApiResponse<AttendanceManualEntryResultDto>> CreateManagedManualEntryAsync(
            CreateAttendanceManualEntryDto request)
        {
            var context = await BuildManualEntryContextAsync(request);
            if (!context.Success)
            {
                return ApiResponse<AttendanceManualEntryResultDto>.Error(context.Message, context.ErrorCode ?? "MANUAL_ENTRY_INVALID");
            }
            if (string.IsNullOrWhiteSpace(request.PreviewRevision))
            {
                return ApiResponse<AttendanceManualEntryResultDto>.Error("补录预览已失效，请刷新后重新提交", "MANUAL_ENTRY_PREVIEW_STALE");
            }

            var storeCode = request.StoreCode.Trim();
            var requesterUserGuid = ResolveCurrentUserGuid();
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var mutationResource = AttendanceDailyMutationLock.BuildResource(
                context.UserGuid, storeCode, request.WorkDate.Date);
            await using var processLock = await AttendanceDailyMutationLock.AcquireProcessAsync(mutationResource);

            AttendanceSchedule schedule;
            var createdSchedule = false;
            AttendanceManualEntryPreviewDto preview;
            // Serializable：预览版本里的「当天无打卡、无排班」输入快照要保持到写入提交。
            await _db.Ado.BeginTranAsync(IsolationLevel.Serializable);
            try
            {
                await AttendanceDailyMutationLock.AcquireDatabaseAsync(_db, mutationResource);
                // 锁内重算：并发补录 / 员工刚好打了卡，都会让预览版本变化。
                var locked = await BuildManualEntryContextAsync(request);
                if (!locked.Success)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendanceManualEntryResultDto>.Error(locked.Message, locked.ErrorCode ?? "MANUAL_ENTRY_INVALID");
                }
                if (!string.Equals(request.PreviewRevision, locked.Preview!.PreviewRevision, StringComparison.Ordinal))
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendanceManualEntryResultDto>.Error("补录预览已失效，请刷新后重新提交", "MANUAL_ENTRY_PREVIEW_STALE");
                }

                schedule = locked.Schedule!;
                preview = locked.Preview;
                createdSchedule = locked.WillCreateSchedule;
                if (createdSchedule)
                {
                    schedule.CreatedAt = now;
                    schedule.CreatedBy = _currentUserService.GetCurrentUsername();
                    schedule.UpdatedAt = now;
                    schedule.UpdatedBy = schedule.CreatedBy;
                    await _db.Insertable(schedule).ExecuteCommandAsync();
                }

                foreach (var punch in locked.Punches)
                {
                    var adjustment = new AttendancePunchAdjustment
                    {
                        AdjustmentGuid = Guid.NewGuid().ToString(),
                        StoreCode = storeCode,
                        UserGuid = context.UserGuid,
                        ScheduleGuid = schedule.ScheduleGuid,
                        OriginalPunchGuid = null,
                        PunchType = punch.PunchType,
                        RequestedPunchTimeLocal = punch.PunchTimeLocal,
                        RequestedPunchTimeUtc = punch.PunchTimeUtc,
                        Reason = request.Reason.Trim(),
                        Status = "Applied",
                        // 补录一律是店长 / Admin 代员工做，不是「店长自补」。
                        IsManagerSelfDirect = false,
                        RequestedByUserGuid = requesterUserGuid,
                        ReviewedByUserGuid = requesterUserGuid,
                        ReviewedAt = now,
                        CreatedAt = now,
                        CreatedBy = _currentUserService.GetCurrentUsername(),
                    };
                    await _db.Insertable(adjustment).ExecuteCommandAsync();
                    var applied = await ApplyPunchAdjustmentAsync(adjustment, punch);
                    adjustment.AppliedPunchGuid = applied.PunchGuid;
                    await _db.Updateable(adjustment).ExecuteCommandAsync();
                }

                // 超出排班窗口的部分生成加班待审（不自动批准）；漏下班 / 终卡异常等派生审批一并对账。
                await ReconcileDerivedApprovalsForScheduleAsync(schedule.ScheduleGuid, 0);
                await _db.Ado.CommitTranAsync();
            }
            catch (Exception exception) when (AttendancePunchPersistenceException.IsUniqueConstraintViolation(exception))
            {
                await _db.Ado.RollbackTranAsync();
                return ApiResponse<AttendanceManualEntryResultDto>.Error("这一天刚刚已有打卡记录，请刷新后重试", "MANUAL_ENTRY_PREVIEW_STALE");
            }
            catch
            {
                await _db.Ado.RollbackTranAsync();
                throw;
            }

            return ApiResponse<AttendanceManualEntryResultDto>.OK(
                new AttendanceManualEntryResultDto
                {
                    ScheduleGuid = schedule.ScheduleGuid,
                    CreatedSchedule = createdSchedule,
                    PunchCount = request.Segments.Count * 2,
                    PaidHours = preview.PaidHours,
                    CandidateOvertimeMinutes = preview.CandidateOvertimeMinutes,
                },
                "补录已生效");
        }

        /// <summary>
        /// 撤销某员工某天的补录：删除补录打卡（软删除，留痕保留）、把对应补卡记录标成已撤销；
        /// 补录新建的排班一并取消，已有排班则只对账派生审批。当天另有非补录打卡时拒绝（走「修改打卡」）。
        /// </summary>
        public async Task<ApiResponse<bool>> RevokeManagedManualEntryAsync(RevokeAttendanceManualEntryDto request)
        {
            if (string.IsNullOrWhiteSpace(request.StoreCode))
            {
                return ApiResponse<bool>.Error("分店代码不能为空", "STORE_REQUIRED");
            }
            if (string.IsNullOrWhiteSpace(request.UserGuid))
            {
                return ApiResponse<bool>.Error("员工不能为空", "USER_REQUIRED");
            }
            var storeCode = request.StoreCode.Trim();
            var userGuid = request.UserGuid.Trim();
            var workDate = request.WorkDate.Date;

            var access = await ResolveManagedStoreAccessAsync(storeCode);
            if (!access.Success)
            {
                return ApiResponse<bool>.Error(access.Message, access.ErrorCode);
            }
            if (!await IsCurrentUserManagerForStoreAsync(storeCode))
            {
                return ApiResponse<bool>.Error("只能修改自己管理分店的打卡", "FORBIDDEN_STORE");
            }
            if (await IsPayPeriodLockedAsync(storeCode, workDate))
            {
                return PayPeriodLocked<bool>();
            }
            var storeTimeZone = await ResolveStoreTimeZoneAsync(storeCode, null);
            var nowLocal = ConvertUtcToStoreLocal(_timeProvider.GetUtcNow().UtcDateTime, storeTimeZone);
            if (!IsAdmin() && !IsWithinManagedAdjustmentWindow(workDate, nowLocal.Date))
            {
                return ApiResponse<bool>.Error("只能修改本周的打卡；周一、周二仍可修改上周", "ADJUSTMENT_WINDOW_EXPIRED");
            }

            var mutationResource = AttendanceDailyMutationLock.BuildResource(userGuid, storeCode, workDate);
            await using var processLock = await AttendanceDailyMutationLock.AcquireProcessAsync(mutationResource);
            var cancelledSources = CancelledScheduleSources.Empty;
            await _db.Ado.BeginTranAsync();
            try
            {
                await AttendanceDailyMutationLock.AcquireDatabaseAsync(_db, mutationResource);
                var dayEnd = workDate.AddDays(1);
                var dayPunches = await _db.Queryable<AttendancePunch>()
                    .Where(item => !item.IsDeleted && item.UserGuid == userGuid && item.StoreCode == storeCode
                        && item.WorkDate >= workDate && item.WorkDate < dayEnd)
                    .ToListAsync();
                var manualPunches = dayPunches
                    .Where(item => item.Source == ManualEntryPunchSource)
                    .ToList();
                if (manualPunches.Count == 0)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<bool>.Error("这一天没有可撤销的补录", "MANUAL_ENTRY_NOT_FOUND");
                }
                if (dayPunches.Count != manualPunches.Count)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<bool>.Error("这一天还有其他打卡记录，请用「修改打卡」处理", "MANUAL_ENTRY_HAS_OTHER_PUNCHES");
                }

                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var updatedBy = _currentUserService.GetCurrentUsername();
                foreach (var punch in manualPunches)
                {
                    punch.IsDeleted = true;
                    punch.UpdatedAt = now;
                    punch.UpdatedBy = updatedBy;
                }
                await _db.Updateable(manualPunches).ExecuteCommandAsync();

                var adjustmentGuids = manualPunches
                    .Select(item => item.AdjustmentGuid)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToList();
                if (adjustmentGuids.Count > 0)
                {
                    await _db.Updateable<AttendancePunchAdjustment>()
                        .SetColumns(item => item.Status == ManualEntryAdjustmentRevoked)
                        .SetColumns(item => item.UpdatedAt == now)
                        .SetColumns(item => item.UpdatedBy == updatedBy)
                        .Where(item => adjustmentGuids.Contains(item.AdjustmentGuid))
                        .ExecuteCommandAsync();
                }

                var scheduleGuids = manualPunches
                    .Select(item => item.ScheduleGuid)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var scheduleGuid in scheduleGuids)
                {
                    var schedule = await _db.Queryable<AttendanceSchedule>()
                        .FirstAsync(item => !item.IsDeleted && item.ScheduleGuid == scheduleGuid);
                    if (schedule == null)
                    {
                        continue;
                    }
                    if (schedule.Remark?.StartsWith(ManualEntryScheduleRemarkPrefix, StringComparison.Ordinal) == true)
                    {
                        // 排班是补录新建的：与「删除排班」同一套软删除 + 取消派生审批。
                        schedule.Status = "Cancelled";
                        schedule.IsDeleted = true;
                        schedule.DeletedAt = now;
                        schedule.DeletedBy = updatedBy;
                        schedule.UpdatedAt = now;
                        schedule.UpdatedBy = updatedBy;
                        await _db.Updateable(schedule).ExecuteCommandAsync();
                        cancelledSources = await CancelScheduleDerivedApprovalsAsync(
                            schedule.ScheduleGuid, "补录已撤销，相关待审考勤已自动取消");
                    }
                    else
                    {
                        await ReconcileDerivedApprovalsForScheduleAsync(schedule.ScheduleGuid, 0);
                    }
                }
                await _db.Ado.CommitTranAsync();
            }
            catch
            {
                await _db.Ado.RollbackTranAsync();
                throw;
            }
            await RevokeCancelledScheduleSourceNotificationsAsync(cancelledSources);
            return ApiResponse<bool>.OK(true, "补录已撤销");
        }

        /// <summary>
        /// 补录的校验与预览（保存前会在员工日锁内再算一遍）：返回要写入的排班 / 打卡草稿与工时预览。
        /// 不写库；新建排班用 Active 状态草稿，ScheduleGuid 已生成，保存时原样插入。
        /// </summary>
        private async Task<ManualEntryContext> BuildManualEntryContextAsync(CreateAttendanceManualEntryDto request)
        {
            if (string.IsNullOrWhiteSpace(request.StoreCode))
            {
                return ManualEntryContext.Error("分店代码不能为空", "STORE_REQUIRED");
            }
            if (string.IsNullOrWhiteSpace(request.UserGuid))
            {
                return ManualEntryContext.Error("员工不能为空", "USER_REQUIRED");
            }
            var reason = request.Reason?.Trim() ?? string.Empty;
            if (reason.Length == 0)
            {
                return ManualEntryContext.Error("补录原因不能为空", "REASON_REQUIRED");
            }
            if (reason.Length > ManualEntryReasonMaxLength)
            {
                return ManualEntryContext.Error($"补录原因不能超过 {ManualEntryReasonMaxLength} 个字", "REASON_TOO_LONG");
            }
            if (!IsValidMealBreakCount(request.MealBreakCount))
            {
                return ManualEntryContext.Error("用餐次数必须在 0 到 3 之间", "INVALID_MEAL_BREAK_COUNT");
            }

            var storeCode = request.StoreCode.Trim();
            var userGuid = request.UserGuid.Trim();
            var workDate = request.WorkDate.Date;

            // 班段：同一天内、先后有序、互不重叠；至少一段。
            var segments = (request.Segments ?? new List<AttendanceManualEntrySegmentDto>())
                .Select(item => new
                {
                    Start = DateTime.SpecifyKind(item.StartLocal, DateTimeKind.Unspecified),
                    End = DateTime.SpecifyKind(item.EndLocal, DateTimeKind.Unspecified),
                })
                .OrderBy(item => item.Start)
                .ToList();
            if (segments.Count == 0)
            {
                return ManualEntryContext.Error("请至少填一段上下班时间", "SEGMENTS_REQUIRED");
            }
            foreach (var segment in segments)
            {
                if (segment.Start.Date != workDate || segment.End.Date != workDate)
                {
                    return ManualEntryContext.Error("补录时间必须在所选日期当天（跨午夜的班请分两天补）", "PUNCH_TIME_INVALID");
                }
                if (segment.End <= segment.Start)
                {
                    return ManualEntryContext.Error("下班时间必须晚于上班时间", "INVALID_TIME_RANGE");
                }
            }
            for (var index = 1; index < segments.Count; index++)
            {
                if (segments[index].Start <= segments[index - 1].End)
                {
                    return ManualEntryContext.Error("班段之间不能重叠，下一段要晚于上一段的下班时间", "SEGMENT_OVERLAP");
                }
            }

            // 权限与范围：与代员工补卡一致（接口策略已校验权限码，这里校验管理分店）。
            var access = await ResolveManagedStoreAccessAsync(storeCode);
            if (!access.Success)
            {
                return ManualEntryContext.Error(access.Message, access.ErrorCode ?? "FORBIDDEN_STORE");
            }
            if (!await IsCurrentUserManagerForStoreAsync(storeCode))
            {
                return ManualEntryContext.Error("只能修改自己管理分店的打卡", "FORBIDDEN_STORE");
            }
            // 员工必须是绑定本店的有效账号：防止误传 GUID 在库里建出无主排班 / 打卡。
            var boundUsers = await LoadStoreBoundUserGuidsAsync(storeCode, new[] { userGuid });
            if (!boundUsers.Contains(userGuid))
            {
                return ManualEntryContext.Error("员工不存在或不属于该分店", "EMPLOYEE_NOT_IN_STORE");
            }
            if (await IsPayPeriodLockedAsync(storeCode, workDate))
            {
                return ManualEntryContext.Error(PeriodLockedMessage, PeriodLockedErrorCode);
            }

            var storeTimeZone = await ResolveStoreTimeZoneAsync(storeCode, null);
            TimeZoneInfo timezone;
            try
            {
                timezone = TimeZoneInfo.FindSystemTimeZoneById(storeTimeZone);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                timezone = TimeZoneInfo.FindSystemTimeZoneById(DefaultStoreTimeZone);
            }
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var nowLocal = ConvertUtcToStoreLocal(nowUtc, storeTimeZone);
            if (!IsAdmin() && !IsWithinManagedAdjustmentWindow(workDate, nowLocal.Date))
            {
                return ManualEntryContext.Error("只能修改本周的打卡；周一、周二仍可修改上周", "ADJUSTMENT_WINDOW_EXPIRED");
            }
            foreach (var segment in segments)
            {
                foreach (var moment in new[] { segment.Start, segment.End })
                {
                    if (timezone.IsInvalidTime(moment) || timezone.IsAmbiguousTime(moment))
                    {
                        return ManualEntryContext.Error("该门店本地时间无效或存在歧义（夏令时切换时段），请换一个时间", "PUNCH_TIME_INVALID");
                    }
                }
            }
            // 补录只能补已经发生的时间：包括 Admin 在内一律禁止未来。
            if (ConvertStoreLocalToUtc(segments[^1].End, storeTimeZone) > nowUtc)
            {
                return ManualEntryContext.Error("补录时间不能晚于当前时间", "PUNCH_TIME_IN_FUTURE");
            }
            if (await IsBlockedByAnnualLeaveAsync(storeCode, userGuid, workDate))
            {
                return ManualEntryContext.Error("员工这天休带薪年假（已批准），不能补录工时", "ANNUAL_LEAVE_UNAVAILABLE");
            }

            // 当天已有任何打卡（含未挂排班的）就不在这里改：避免与「修改打卡」两套入口互相覆盖。
            var dayEnd = workDate.AddDays(1);
            var hasPunches = await _db.Queryable<AttendancePunch>().AnyAsync(item =>
                !item.IsDeleted && item.UserGuid == userGuid && item.StoreCode == storeCode
                && item.WorkDate >= workDate && item.WorkDate < dayEnd);
            if (hasPunches)
            {
                return ManualEntryContext.Error("这一天已有打卡记录，请用「修改打卡」处理", "ALREADY_HAS_PUNCHES");
            }

            var schedules = await _db.Queryable<AttendanceSchedule>()
                .Where(item => !item.IsDeleted && item.Status == "Active" && item.StoreCode == storeCode
                    && item.UserGuid == userGuid && item.WorkDate >= workDate && item.WorkDate < dayEnd)
                .ToListAsync();
            if (schedules.Count > 1)
            {
                return ManualEntryContext.Error("这一天有多个排班，请先在排班页处理后再补录", "MULTIPLE_SCHEDULES");
            }
            var willCreateSchedule = schedules.Count == 0;
            var schedule = willCreateSchedule
                ? new AttendanceSchedule
                {
                    ScheduleGuid = Guid.NewGuid().ToString(),
                    StoreCode = storeCode,
                    UserGuid = userGuid,
                    WorkDate = workDate,
                    StartTime = segments[0].Start.TimeOfDay,
                    EndTime = segments[^1].End.TimeOfDay,
                    Status = "Active",
                    Remark = ManualEntryScheduleRemarkPrefix + reason,
                    MealBreakCount = request.MealBreakCount,
                }
                : schedules[0];

            var punches = new List<AttendancePunch>();
            foreach (var segment in segments)
            {
                foreach (var (type, local) in new[] { ("ClockIn", segment.Start), ("ClockOut", segment.End) })
                {
                    punches.Add(new AttendancePunch
                    {
                        PunchGuid = Guid.NewGuid().ToString(),
                        ScheduleGuid = schedule.ScheduleGuid,
                        StoreCode = storeCode,
                        UserGuid = userGuid,
                        WorkDate = workDate,
                        StoreTimeZone = storeTimeZone,
                        PunchType = type,
                        PunchTimeLocal = local,
                        PunchTimeUtc = ConvertStoreLocalToUtc(local, storeTimeZone),
                        Status = "Normal",
                        Source = ManualEntryPunchSource,
                    });
                }
            }

            var segmentLimit = await ResolveSegmentLimitAsync(userGuid, storeCode);
            var sequenceValidation = ValidateEffectivePunchSequence(punches, segmentLimit);
            if (!sequenceValidation.Success)
            {
                return ManualEntryContext.Error(sequenceValidation.Message, sequenceValidation.ErrorCode ?? "INVALID_PUNCH_SEQUENCE");
            }
            var adjacent = await GetAdjacentBusinessDayPunchesAsync(userGuid, workDate);
            var sameStoreDay = adjacent
                .Where(item => item.WorkDate >= workDate && item.WorkDate < dayEnd
                    && item.StoreCode.Equals(storeCode, StringComparison.OrdinalIgnoreCase))
                .Concat(punches)
                .ToList();
            var dailyLimit = ValidateDailyStoreSegmentLimit(sameStoreDay, segmentLimit);
            if (!dailyLimit.Success)
            {
                return ManualEntryContext.Error(dailyLimit.Message, dailyLimit.ErrorCode ?? "SEGMENT_LIMIT_REACHED");
            }
            adjacent.AddRange(punches);
            if (HasCrossScheduleOverlap(adjacent))
            {
                return ManualEntryContext.Error("补录后与其他排班或分店的班段重叠", "PUNCH_TIME_OVERLAP");
            }

            // 工时预览：与工时统计同一套引擎（会话计算 + 计薪），预览与保存后的结果一致。
            var settings = await GetOrCreateSettingsModelAsync();
            var session = AttendanceWorkSessionCalculator.Calculate(
                schedule, punches, segmentLimit, nowLocal, settings.EarlyLeaveGraceMinutes, settings.LateGraceMinutes);
            var payrollSegments = session.Segments
                .Where(item => item.ClockIn != null && item.ClockOut != null)
                .Select(item => new PayrollSegment(item.ClockIn!.PunchTimeLocal, item.ClockOut!.PunchTimeLocal))
                .ToList();
            var windowStart = schedule.WorkDate.Date.Add(schedule.StartTime);
            var windowEnd = schedule.WorkDate.Date.Add(schedule.EndTime);
            if (windowEnd <= windowStart)
            {
                windowEnd = windowEnd.AddDays(1);
            }
            var effectiveMeal = AttendanceMealBreakRules.EffectiveCount(schedule.MealBreakCount, schedule.StartTime, schedule.EndTime);
            var pay = PayrollCalculator.CalculateDay(new PayrollDayInput(windowStart, windowEnd, payrollSegments, effectiveMeal));

            var preview = new AttendanceManualEntryPreviewDto
            {
                WillCreateSchedule = willCreateSchedule,
                ScheduleGuid = schedule.ScheduleGuid,
                ScheduleStart = schedule.StartTime.ToString(@"hh\:mm"),
                ScheduleEnd = schedule.EndTime.ToString(@"hh\:mm"),
                EffectiveMealBreakCount = effectiveMeal,
                WorkedMinutes = pay.WorkedMinutes,
                PaidMinutes = pay.PaidMinutes,
                PaidHours = PayrollCalculator.RoundHours(pay.PaidMinutes),
                CandidateOvertimeMinutes = session.CandidateOvertimeMinutes,
            };
            // 版本只取「决定这次补录结果的输入」：当天状态（无打卡、排班 / 无排班）+ 请求内容。
            // 新建排班的 Guid 每次预览都是新的，不进版本，否则预览与保存永远对不上。
            var revisionSource = string.Join(
                "|",
                userGuid,
                storeCode,
                workDate.ToString("yyyy-MM-dd"),
                willCreateSchedule ? "new" : schedule.ScheduleGuid,
                willCreateSchedule ? "" : schedule.UpdatedAt?.Ticks.ToString() ?? "",
                request.MealBreakCount?.ToString() ?? "auto",
                reason,
                string.Join(",", segments.Select(item => item.Start.ToString("HH:mm") + "-" + item.End.ToString("HH:mm"))));
            preview.PreviewRevision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionSource)));

            return ManualEntryContext.OK(schedule, willCreateSchedule, punches, preview, userGuid);
        }
    }
}
