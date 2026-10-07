using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 考勤用餐休息：当班期间的一键开始/结束休息、下班打卡时的用餐声明与店长审核、工时扣除与加回。
    /// 用餐只是工时扣除规则，不改变班段/打卡类型；所有写入都在员工日锁内，与打卡串行。
    /// </summary>
    public partial class AttendanceReactService
    {
        private sealed record MealBreakRequestContext(
            string UserGuid,
            string StoreCode,
            DateTime ServerNow,
            DateTime WorkDate);

        private sealed record MealScheduleContext(
            AttendanceSchedule Schedule,
            AttendanceWorkSessionDto Session,
            int SegmentLimit,
            DateTime NowLocal,
            int EarlyLeaveGraceMinutes);

        public async Task<ApiResponse<AttendanceMealStateDto>> StartMyMealBreakAsync(
            AttendanceMealBreakRequestDto request)
        {
            var (context, error) = await PrepareMealBreakRequestAsync(request);
            if (context == null)
            {
                return error!;
            }

            var resource = AttendanceDailyMutationLock.BuildResource(
                context.UserGuid,
                context.StoreCode,
                context.WorkDate);
            await using var processLock = await AttendanceDailyMutationLock.AcquireProcessAsync(resource);
            await _db.Ado.BeginTranAsync();
            MealScheduleContext? meal;
            try
            {
                await AttendanceDailyMutationLock.AcquireDatabaseAsync(_db, resource);
                meal = await LoadMealScheduleContextAsync(
                    context.UserGuid,
                    context.StoreCode,
                    context.WorkDate,
                    context.ServerNow);
                // 休息只能发生在进行中的班段里，并且该班段必须挂在排班上（用餐规则按排班走）。
                if (meal == null || !meal.Session.HasOpenSegment)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendanceMealStateDto>.Error(
                        "当前没有进行中的班段，不能开始休息",
                        "NO_OPEN_SEGMENT");
                }

                var openBreak = await _db.Queryable<AttendanceMealBreak>().FirstAsync(item =>
                    item.UserGuid == context.UserGuid && item.EndUtc == null);
                var alreadyOpen = openBreak != null
                    && openBreak.ScheduleGuid == meal.Schedule.ScheduleGuid
                    && string.Equals(openBreak.StoreCode, context.StoreCode, StringComparison.OrdinalIgnoreCase);
                if (openBreak != null && !alreadyOpen)
                {
                    // 前一天忘记结束的休息：按零时长结束，不算一次用餐，也不挡住今天开始新的休息。
                    openBreak.EndUtc = openBreak.StartUtc;
                    await _db.Updateable(openBreak).ExecuteCommandAsync();
                }

                if (!alreadyOpen)
                {
                    await _db.Insertable(new AttendanceMealBreak
                    {
                        BreakGuid = Guid.NewGuid().ToString(),
                        UserGuid = context.UserGuid,
                        StoreCode = context.StoreCode,
                        WorkDate = context.WorkDate,
                        ScheduleGuid = meal.Schedule.ScheduleGuid,
                        StartUtc = context.ServerNow,
                        CreatedAtUtc = context.ServerNow,
                    }).ExecuteCommandAsync();
                }

                await _db.Ado.CommitTranAsync();
            }
            catch (Exception exception) when (
                AttendancePunchPersistenceException.IsUniqueConstraintViolation(exception))
            {
                await _db.Ado.RollbackTranAsync();
                return ApiResponse<AttendanceMealStateDto>.Error(
                    "休息已在进行中，请刷新后重试",
                    "MEAL_BREAK_ALREADY_OPEN");
            }
            catch
            {
                await _db.Ado.RollbackTranAsync();
                throw;
            }

            return ApiResponse<AttendanceMealStateDto>.OK(
                await BuildMealStateAsync(context.UserGuid, meal, context.ServerNow),
                "已开始休息");
        }

        public async Task<ApiResponse<AttendanceMealStateDto>> EndMyMealBreakAsync(
            AttendanceMealBreakRequestDto request)
        {
            var (context, error) = await PrepareMealBreakRequestAsync(request);
            if (context == null)
            {
                return error!;
            }

            var resource = AttendanceDailyMutationLock.BuildResource(
                context.UserGuid,
                context.StoreCode,
                context.WorkDate);
            await using var processLock = await AttendanceDailyMutationLock.AcquireProcessAsync(resource);
            await _db.Ado.BeginTranAsync();
            AttendanceMealBreak? openBreak;
            try
            {
                await AttendanceDailyMutationLock.AcquireDatabaseAsync(_db, resource);
                openBreak = await _db.Queryable<AttendanceMealBreak>().FirstAsync(item =>
                    item.UserGuid == context.UserGuid
                    && item.StoreCode == context.StoreCode
                    && item.EndUtc == null);
                if (openBreak == null)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendanceMealStateDto>.Error(
                        "没有进行中的休息",
                        "NO_OPEN_MEAL_BREAK");
                }

                openBreak.EndUtc = context.ServerNow > openBreak.StartUtc
                    ? context.ServerNow
                    : openBreak.StartUtc;
                await _db.Updateable(openBreak).ExecuteCommandAsync();
                await _db.Ado.CommitTranAsync();
            }
            catch
            {
                await _db.Ado.RollbackTranAsync();
                throw;
            }

            var meal = await LoadMealScheduleContextAsync(
                context.UserGuid,
                context.StoreCode,
                context.WorkDate,
                context.ServerNow);
            if (meal == null)
            {
                // 排班已被取消：休息本身已结束，只返回一个最小状态。
                return ApiResponse<AttendanceMealStateDto>.OK(
                    new AttendanceMealStateDto
                    {
                        ScheduleGuid = openBreak.ScheduleGuid ?? string.Empty,
                        StoreCode = context.StoreCode,
                        ServerTimeUtc = context.ServerNow,
                    },
                    "已结束休息");
            }

            return ApiResponse<AttendanceMealStateDto>.OK(
                await BuildMealStateAsync(context.UserGuid, meal, context.ServerNow),
                "已结束休息");
        }

        private async Task<(MealBreakRequestContext? Context, ApiResponse<AttendanceMealStateDto>? Error)>
            PrepareMealBreakRequestAsync(AttendanceMealBreakRequestDto? request)
        {
            var userGuid = ResolveCurrentUserGuid();
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return (null, ApiResponse<AttendanceMealStateDto>.Error("无法识别当前员工", "USER_NOT_FOUND"));
            }

            var storeCode = request?.StoreCode?.Trim();
            if (string.IsNullOrEmpty(storeCode))
            {
                return (null, ApiResponse<AttendanceMealStateDto>.Error("请选择门店", "STORE_REQUIRED"));
            }

            var access = await ResolveRelatedStoreAccessAsync(userGuid, storeCode);
            if (!access.Success)
            {
                return (null, ApiResponse<AttendanceMealStateDto>.Error(access.Message, access.ErrorCode));
            }

            var serverNow = _timeProvider.GetUtcNow().UtcDateTime;
            var timeZone = await ResolveStoreTimeZoneAsync(storeCode, null);
            var workDate = ConvertUtcToStoreLocal(serverNow, timeZone).Date;
            return (new MealBreakRequestContext(userGuid, storeCode, serverNow, workDate), null);
        }

        /// <summary>
        /// 取员工在本店当天的排班和班段状态：优先返回有进行中班段的排班，否则返回第一条排班；没有排班返回 null。
        /// </summary>
        private async Task<MealScheduleContext?> LoadMealScheduleContextAsync(
            string userGuid,
            string storeCode,
            DateTime workDate,
            DateTime nowUtc)
        {
            var schedules = await _db.Queryable<AttendanceSchedule>()
                .Where(item =>
                    !item.IsDeleted
                    && item.Status == "Active"
                    && item.UserGuid == userGuid
                    && item.StoreCode == storeCode
                    && item.WorkDate >= workDate
                    && item.WorkDate < workDate.AddDays(1))
                .ToListAsync();
            if (schedules.Count == 0)
            {
                return null;
            }

            var punches = await _db.Queryable<AttendancePunch>()
                .Where(item =>
                    !item.IsDeleted
                    && item.UserGuid == userGuid
                    && item.StoreCode == storeCode
                    && item.WorkDate >= workDate
                    && item.WorkDate < workDate.AddDays(1))
                .ToListAsync();
            var settings = await GetOrCreateSettingsModelAsync();
            var segmentLimit = await ResolveSegmentLimitAsync(userGuid, storeCode);
            var timeZone = await ResolveStoreTimeZoneAsync(storeCode, null);
            var nowLocal = ConvertUtcToStoreLocal(nowUtc, timeZone);

            MealScheduleContext? fallback = null;
            foreach (var schedule in schedules.OrderBy(item => item.StartTime))
            {
                var session = AttendanceWorkSessionCalculator.Calculate(
                    schedule,
                    punches,
                    segmentLimit,
                    nowLocal,
                    settings.EarlyLeaveGraceMinutes,
                    settings.LateGraceMinutes);
                var context = new MealScheduleContext(
                    schedule,
                    session,
                    segmentLimit,
                    nowLocal,
                    settings.EarlyLeaveGraceMinutes);
                if (session.HasOpenSegment)
                {
                    return context;
                }
                fallback ??= context;
            }

            return fallback;
        }

        private async Task<AttendanceMealStateDto> BuildMealStateAsync(
            string userGuid,
            MealScheduleContext meal,
            DateTime nowUtc)
        {
            var breaks = await LoadMealBreaksAsync(meal.Schedule.ScheduleGuid, userGuid);
            var claimedHandled = await CountClaimedHandledAsync(meal.Schedule.ScheduleGuid);
            var effective = AttendanceMealBreakRules.EffectiveCount(
                meal.Schedule.MealBreakCount,
                meal.Schedule.StartTime,
                meal.Schedule.EndTime);
            var snapshot = AttendanceMealBreakRules.Evaluate(
                effective,
                meal.Session,
                breaks,
                claimedHandled,
                nowUtc);
            var isFinal = WouldBeFinalClockOut(
                meal.Schedule,
                meal.Session,
                meal.SegmentLimit,
                meal.NowLocal,
                meal.EarlyLeaveGraceMinutes);
            return ToMealStateDto(meal.Schedule, snapshot, breaks, nowUtc, isFinal);
        }

        /// <summary>
        /// 此刻下班会不会被判为「最后一次下班」。与 PunchAsync 的 isFinalClockOut 同口径：
        /// 班段数已到上限，或已到排班结束前的早退宽限。只有最后一次下班才检查用餐，
        /// 班段中间的下班（中途离店再回来）不追问，否则员工去休息时还被问有没有休息。
        /// </summary>
        private static bool WouldBeFinalClockOut(
            AttendanceSchedule schedule,
            AttendanceWorkSessionDto session,
            int segmentLimit,
            DateTime nowLocal,
            int earlyLeaveGraceMinutes) =>
            session.HasOpenSegment
            && (session.Segments.Count >= segmentLimit
                || nowLocal.TimeOfDay >= schedule.EndTime.Subtract(
                    TimeSpan.FromMinutes(earlyLeaveGraceMinutes)));

        private async Task<List<AttendanceMealBreak>> LoadMealBreaksAsync(string scheduleGuid, string userGuid) =>
            await _db.Queryable<AttendanceMealBreak>()
                .Where(item => item.ScheduleGuid == scheduleGuid && item.UserGuid == userGuid)
                .OrderBy(item => item.StartUtc)
                .ToListAsync();

        /// <summary>此前下班声明已处理的用餐次数（不含已失效的）；声明“休息了”和“没休息”都算已处理。</summary>
        private async Task<int> CountClaimedHandledAsync(string scheduleGuid)
        {
            var claims = await _db.Queryable<AttendanceMealClaim>()
                .Where(item => item.ScheduleGuid == scheduleGuid
                    && item.Status != AttendanceMealBreakRules.ClaimStatusCancelled)
                .ToListAsync();
            return claims.Sum(item => item.MissingCount);
        }

        /// <summary>
        /// 下班打卡落库后在同一事务里调用：先结束仍在进行的休息，再按排班检查缺几次休息；
        /// 新版 App 带了声明才记录声明，声明“没休息”时同时生成待审的 MealBreak 审批。
        /// 旧版 App 不带声明：只结束休息，不记录、不审批，按「已休息」处理（工时仍按排班扣除）。
        /// </summary>
        private async Task<AttendanceMealClaim?> ApplyClockOutMealCheckAsync(
            AttendanceSchedule schedule,
            AttendancePunch clockOut,
            IEnumerable<AttendancePunch> schedulePunches,
            int segmentLimit,
            DateTime punchLocal,
            AttendanceSettings settings,
            bool isFinalClockOut,
            AttendanceMealDeclarationDto? declaration)
        {
            var punchUtc = clockOut.PunchTimeUtc;
            // 休息中直接下班：自动结束休息，不要求员工再点一次“结束休息”。
            var openBreak = await _db.Queryable<AttendanceMealBreak>().FirstAsync(item =>
                item.UserGuid == clockOut.UserGuid && item.EndUtc == null);
            if (openBreak != null)
            {
                openBreak.EndUtc = punchUtc > openBreak.StartUtc ? punchUtc : openBreak.StartUtc;
                await _db.Updateable(openBreak).ExecuteCommandAsync();
            }

            var effective = AttendanceMealBreakRules.EffectiveCount(
                schedule.MealBreakCount,
                schedule.StartTime,
                schedule.EndTime);
            // 班段中间的下班不检查；旧 App 不带声明也不检查。
            if (effective <= 0 || !isFinalClockOut || declaration == null)
            {
                return null;
            }

            var session = AttendanceWorkSessionCalculator.Calculate(
                schedule,
                schedulePunches,
                segmentLimit,
                punchLocal,
                settings.EarlyLeaveGraceMinutes,
                settings.LateGraceMinutes);
            var breaks = await LoadMealBreaksAsync(schedule.ScheduleGuid, clockOut.UserGuid);
            var claimedHandled = await CountClaimedHandledAsync(schedule.ScheduleGuid);
            // 缺口由服务端自己重算，客户端声明只用来确认其中几次没休息。
            var snapshot = AttendanceMealBreakRules.Evaluate(
                effective,
                session,
                breaks,
                claimedHandled,
                punchUtc);
            if (snapshot.MissingCount <= 0)
            {
                return null;
            }

            var notTaken = Math.Clamp(declaration.NotTakenCount, 0, snapshot.MissingCount);
            var claim = new AttendanceMealClaim
            {
                ClaimGuid = Guid.NewGuid().ToString(),
                ScheduleGuid = schedule.ScheduleGuid,
                UserGuid = clockOut.UserGuid,
                StoreCode = clockOut.StoreCode,
                WorkDate = clockOut.WorkDate,
                ClockOutPunchGuid = clockOut.PunchGuid,
                ExpectedCount = snapshot.RequiredCount,
                RecordedCount = snapshot.HandledCount,
                MissingCount = snapshot.MissingCount,
                NotTakenCount = notTaken,
                ClaimedMinutes = notTaken * AttendanceMealBreakRules.MealBreakMinutes,
                Status = notTaken > 0
                    ? AttendanceMealBreakRules.ClaimStatusPending
                    : AttendanceMealBreakRules.ClaimStatusNone,
                Reason = NormalizeOptional(declaration.Reason, 500),
                CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
            };
            await _db.Insertable(claim).ExecuteCommandAsync();
            if (notTaken > 0)
            {
                await CreatePendingApprovalAsync(
                    "MealBreak",
                    claim.ClaimGuid,
                    claim.StoreCode,
                    claim.UserGuid);
            }

            return claim;
        }

        /// <summary>
        /// 审核 MealBreak 审批前的校验（在员工日锁内、审批 Serializable 事务里调用）。
        /// 返回 Error 时事务已提交或回滚；返回 Claim 时调用方继续走统一的审批领取与落库。
        /// </summary>
        private async Task<(AttendanceMealClaim? Claim, ApiResponse<AttendanceApprovalDto>? Error)>
            ValidateMealClaimReviewAsync(
                AttendanceApproval approval,
                string reviewStatus,
                string? reviewRemark)
        {
            var claim = await _db.Queryable<AttendanceMealClaim>().FirstAsync(item =>
                item.ClaimGuid == approval.SourceGuid);
            var stale = claim == null
                || claim.Status != AttendanceMealBreakRules.ClaimStatusPending;
            if (!stale)
            {
                // 排班已取消、或这一天已经没有有效的下班打卡，申请就失去依据。
                // 下班卡被店长补卡修改后仍会有新的有效下班卡，所以不因“被替换”失效。
                var scheduleAlive = await _db.Queryable<AttendanceSchedule>().AnyAsync(item =>
                    !item.IsDeleted
                    && item.Status == "Active"
                    && item.ScheduleGuid == claim!.ScheduleGuid);
                var clockOutAlive = scheduleAlive
                    && await _db.Queryable<AttendancePunch>().AnyAsync(item =>
                        !item.IsDeleted
                        && item.ScheduleGuid == claim!.ScheduleGuid
                        && item.PunchType == "ClockOut");
                stale = !clockOutAlive;
            }

            if (stale)
            {
                if (claim != null && claim.Status == AttendanceMealBreakRules.ClaimStatusPending)
                {
                    claim.Status = AttendanceMealBreakRules.ClaimStatusCancelled;
                    await _db.Updateable(claim).ExecuteCommandAsync();
                }
                await CancelPendingApprovalAsync(
                    approval.ApprovalGuid,
                    "排班或下班打卡已失效，用餐加工时申请已自动取消");
                await _db.Ado.CommitTranAsync();
                return (null, ApiResponse<AttendanceApprovalDto>.Error(
                    "用餐加工时申请已失效，不能处理",
                    "MEAL_CLAIM_STALE"));
            }

            if (reviewStatus == "Rejected" && string.IsNullOrWhiteSpace(reviewRemark))
            {
                await _db.Ado.RollbackTranAsync();
                return (null, ApiResponse<AttendanceApprovalDto>.Error(
                    "拒绝用餐加工时必须填写审核备注",
                    "REVIEW_REMARK_REQUIRED"));
            }

            return (claim, null);
        }

        /// <summary>审批领取成功后把审核结果写回声明；批准时全额加回申请的分钟数。</summary>
        private async Task ApplyMealClaimReviewAsync(
            AttendanceMealClaim claim,
            string reviewStatus,
            DateTime reviewedAt)
        {
            var approved = reviewStatus == "Approved";
            claim.Status = approved
                ? AttendanceMealBreakRules.ClaimStatusApproved
                : AttendanceMealBreakRules.ClaimStatusRejected;
            claim.ApprovedMinutes = approved ? claim.ClaimedMinutes : null;
            claim.ReviewedAtUtc = reviewedAt;
            await _db.Updateable(claim).ExecuteCommandAsync();
        }

        /// <summary>排班被取消/删除时，同步取消它名下待审的用餐加工时申请。</summary>
        private async Task CancelMealClaimsForScheduleAsync(string scheduleGuid, string reason)
        {
            var claims = await _db.Queryable<AttendanceMealClaim>()
                .Where(item => item.ScheduleGuid == scheduleGuid
                    && item.Status == AttendanceMealBreakRules.ClaimStatusPending)
                .ToListAsync();
            foreach (var claim in claims)
            {
                claim.Status = AttendanceMealBreakRules.ClaimStatusCancelled;
                await _db.Updateable(claim).ExecuteCommandAsync();
                await CancelPendingApprovalsAsync("MealBreak", claim.ClaimGuid, reason);
            }
        }

        /// <summary>今日接口：为有用餐要求的排班附上用餐状态（休息横幅、计时、下班前确认都读它）。</summary>
        private async Task PopulateMealStatesAsync(
            List<AttendanceScheduleDto> scheduleDtos,
            List<AttendanceSchedule> schedules,
            List<AttendancePunch> punches,
            string userGuid)
        {
            var candidates = schedules
                .Where(item => AttendanceMealBreakRules.EffectiveCount(
                    item.MealBreakCount,
                    item.StartTime,
                    item.EndTime) > 0)
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            var scheduleGuids = candidates.Select(item => item.ScheduleGuid).ToList();
            var breaks = await _db.Queryable<AttendanceMealBreak>()
                .Where(item => item.UserGuid == userGuid
                    && item.ScheduleGuid != null
                    && scheduleGuids.Contains(item.ScheduleGuid!))
                .ToListAsync();
            var claims = await _db.Queryable<AttendanceMealClaim>()
                .Where(item => scheduleGuids.Contains(item.ScheduleGuid)
                    && item.Status != AttendanceMealBreakRules.ClaimStatusCancelled)
                .ToListAsync();
            var settings = await GetOrCreateSettingsModelAsync();
            var segmentLimitMap = await ResolveSegmentLimitMapAsync(candidates);
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var dtoMap = scheduleDtos.ToDictionary(
                item => item.ScheduleGuid,
                StringComparer.OrdinalIgnoreCase);
            var timeZones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var schedule in candidates)
            {
                if (!dtoMap.TryGetValue(schedule.ScheduleGuid, out var dto))
                {
                    continue;
                }

                if (!timeZones.TryGetValue(schedule.StoreCode, out var timeZone))
                {
                    timeZone = await ResolveStoreTimeZoneAsync(schedule.StoreCode, null);
                    timeZones[schedule.StoreCode] = timeZone;
                }

                var session = AttendanceWorkSessionCalculator.Calculate(
                    schedule,
                    punches,
                    segmentLimitMap.GetValueOrDefault(schedule.UserGuid, 2),
                    ConvertUtcToStoreLocal(nowUtc, timeZone),
                    settings.EarlyLeaveGraceMinutes,
                    settings.LateGraceMinutes);
                var scheduleBreaks = breaks
                    .Where(item => string.Equals(
                        item.ScheduleGuid,
                        schedule.ScheduleGuid,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item.StartUtc)
                    .ToList();
                var claimedHandled = claims
                    .Where(item => string.Equals(
                        item.ScheduleGuid,
                        schedule.ScheduleGuid,
                        StringComparison.OrdinalIgnoreCase))
                    .Sum(item => item.MissingCount);
                var snapshot = AttendanceMealBreakRules.Evaluate(
                    dto.EffectiveMealBreakCount,
                    session,
                    scheduleBreaks,
                    claimedHandled,
                    nowUtc);
                var nowLocal = ConvertUtcToStoreLocal(nowUtc, timeZone);
                var isFinal = WouldBeFinalClockOut(
                    schedule,
                    session,
                    segmentLimitMap.GetValueOrDefault(schedule.UserGuid, 2),
                    nowLocal,
                    settings.EarlyLeaveGraceMinutes);
                dto.Meal = ToMealStateDto(schedule, snapshot, scheduleBreaks, nowUtc, isFinal);
            }
        }

        /// <summary>
        /// 排班工时的用餐扣除与加回：扣除按排班有效次数和实际工时算（是否休息过都扣），
        /// 声明没休息且店长批准的分钟数加回；待审的只展示不计入。PaidMinutes 不改变 WorkedMinutes 的含义。
        /// </summary>
        private static void ApplyMealPayFields(
            AttendanceScheduleDto dto,
            AttendanceSchedule schedule,
            AttendanceWorkSessionDto session,
            IReadOnlyCollection<AttendanceMealClaim> scheduleClaims)
        {
            var effective = AttendanceMealBreakRules.EffectiveCount(
                schedule.MealBreakCount,
                schedule.StartTime,
                schedule.EndTime);
            var deduction = AttendanceMealBreakRules.DeductionMinutes(effective, session.WorkedMinutes);
            // 加回不能超过当前扣除：批准后店长又把排班用餐改少（甚至改成 0）时，
            // 计薪工时不能因此超过实际工时（与加班的批准分钟封顶到候选分钟同一做法）。
            var approvedAddBack = Math.Min(
                deduction,
                scheduleClaims
                    .Where(item => item.Status == AttendanceMealBreakRules.ClaimStatusApproved)
                    .Sum(item => item.ApprovedMinutes ?? 0));
            var pendingAddBack = Math.Min(
                deduction - approvedAddBack,
                scheduleClaims
                    .Where(item => item.Status == AttendanceMealBreakRules.ClaimStatusPending)
                    .Sum(item => item.ClaimedMinutes));
            dto.EffectiveMealBreakCount = effective;
            dto.MealDeductionMinutes = deduction;
            dto.ApprovedMealAddBackMinutes = approvedAddBack;
            dto.PendingMealAddBackMinutes = pendingAddBack;
            dto.PaidMinutes = Math.Max(0, session.WorkedMinutes - deduction + approvedAddBack);
        }

        private static AttendanceMealStateDto ToMealStateDto(
            AttendanceSchedule schedule,
            AttendanceMealSnapshot snapshot,
            IEnumerable<AttendanceMealBreak> breaks,
            DateTime nowUtc,
            bool isFinalClockOut) => new()
            {
                ScheduleGuid = schedule.ScheduleGuid,
                StoreCode = schedule.StoreCode,
                EffectiveMealBreakCount = snapshot.EffectiveCount,
                HandledCount = snapshot.HandledCount,
                RequiredCount = snapshot.RequiredCount,
                ClockOutWouldBeFinal = isFinalClockOut,
                // 只有最后一次下班才需要回答；班段中间的下班不追问。
                MissingCountIfClockOutNow = isFinalClockOut ? snapshot.MissingCount : 0,
                HasOpenBreak = snapshot.HasOpenBreak,
                OpenBreakStartedAtUtc = snapshot.OpenBreakStartUtc,
                NextReminderAtUtc = snapshot.NextReminderAtUtc,
                ServerTimeUtc = nowUtc,
                Breaks = breaks
                    .OrderBy(item => item.StartUtc)
                    .Select(item => new AttendanceMealBreakDto
                    {
                        BreakGuid = item.BreakGuid,
                        StartUtc = item.StartUtc,
                        EndUtc = item.EndUtc,
                    })
                    .ToList(),
            };

        private static AttendanceMealClaimDto ToDto(AttendanceMealClaim item) => new()
        {
            ClaimGuid = item.ClaimGuid,
            ScheduleGuid = item.ScheduleGuid,
            WorkDate = item.WorkDate,
            ExpectedCount = item.ExpectedCount,
            RecordedCount = item.RecordedCount,
            MissingCount = item.MissingCount,
            NotTakenCount = item.NotTakenCount,
            ClaimedMinutes = item.ClaimedMinutes,
            ApprovedMinutes = item.ApprovedMinutes,
            Status = item.Status,
            Reason = item.Reason,
        };
    }
}
