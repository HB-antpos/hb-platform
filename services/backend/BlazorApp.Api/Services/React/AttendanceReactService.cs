using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Api.Security;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class AttendanceReactService : IAttendanceReactService
    {
        private const string DefaultStoreTimeZone = StoreTimeZonePolicy.Sydney;

        private readonly ISqlSugarClient _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentUserManageableStoreScopeService _scopeService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<AttendanceReactService> _logger;
        private readonly TencentCloudUploadService _uploadService;
        private readonly IAttendancePosDeviceStatusProvider? _attendancePosDeviceStatusProvider;
        private readonly TimeProvider _timeProvider;
        private readonly AttendanceQrKeyProtector _attendanceQrKeyProtector;
        private readonly AttendancePunchAuthorizationProtector _punchAuthorizationProtector;
        private readonly IAuthorizationService? _authorizationService;

        public AttendanceReactService(
            SqlSugarContext context,
            ICurrentUserService currentUserService,
            ICurrentUserManageableStoreScopeService scopeService,
            IHttpContextAccessor httpContextAccessor,
            ILogger<AttendanceReactService> logger,
            TencentCloudUploadService uploadService,
            IAttendancePosDeviceStatusProvider? attendancePosDeviceStatusProvider = null,
            TimeProvider? timeProvider = null,
            AttendanceQrKeyProtector? attendanceQrKeyProtector = null,
            AttendancePunchAuthorizationProtector? punchAuthorizationProtector = null,
            IAuthorizationService? authorizationService = null
        )
        {
            _db = context.Db;
            _currentUserService = currentUserService;
            _scopeService = scopeService;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
            _uploadService = uploadService;
            _attendancePosDeviceStatusProvider = attendancePosDeviceStatusProvider;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _attendanceQrKeyProtector = attendanceQrKeyProtector
                ?? throw new ArgumentNullException(nameof(attendanceQrKeyProtector));
            _punchAuthorizationProtector = punchAuthorizationProtector
                ?? throw new ArgumentNullException(nameof(punchAuthorizationProtector));
            _authorizationService = authorizationService;
        }

        public async Task<ApiResponse<List<AttendanceScheduleDto>>> GetSchedulesAsync(
            AttendanceScheduleQueryDto query
        )
        {
            var storeAccess = await ResolveManagedStoreAccessAsync(query.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<List<AttendanceScheduleDto>>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            var rows = await _db.Queryable<AttendanceSchedule>()
                .Where(item => !item.IsDeleted)
                .WhereIF(!string.IsNullOrWhiteSpace(query.StoreCode), item => item.StoreCode == query.StoreCode!.Trim())
                .WhereIF(string.IsNullOrWhiteSpace(query.StoreCode) && storeAccess.StoreCodes.Count > 0, item => storeAccess.StoreCodes.Contains(item.StoreCode))
                .WhereIF(!string.IsNullOrWhiteSpace(query.UserGuid), item => item.UserGuid == query.UserGuid!.Trim())
                .WhereIF(query.WeekStartDate.HasValue, item => item.WorkDate >= query.WeekStartDate!.Value.Date && item.WorkDate <= query.WeekStartDate!.Value.Date.AddDays(6))
                .WhereIF(query.FromDate.HasValue, item => item.WorkDate >= query.FromDate!.Value.Date)
                .WhereIF(query.ToDate.HasValue, item => item.WorkDate <= query.ToDate!.Value.Date)
                .OrderBy(item => item.WorkDate)
                .OrderBy(item => item.StartTime)
                .ToListAsync();

            var result = rows.Select(item => ToDto(item)).ToList();
            await PopulateScheduleDisplayFieldsAsync(result);
            await PopulateMinorEmploymentComplianceAsync(result);
            return ApiResponse<List<AttendanceScheduleDto>>.OK(result);
        }

        public Task<ApiResponse<List<AttendanceScheduleDto>>> GetWeekSchedulesAsync(
            AttendanceScheduleQueryDto query
        )
        {
            query.WeekStartDate ??= GetWeekStart(DateTime.Today);
            return GetSchedulesAsync(query);
        }

        public async Task<ApiResponse<PagedResult<AttendanceScheduleDto>>> GetAttendanceRecordsAsync(
            AttendanceScheduleQueryDto query)
        {
            var storeAccess = await ResolveManagedStoreAccessAsync(query.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<PagedResult<AttendanceScheduleDto>>.Error(
                    storeAccess.Message,
                    storeAccess.ErrorCode);
            }

            var page = Math.Max(1, query.Page);
            var pageSize = Math.Clamp(query.PageSize, 1, 200);
            var rowsQuery = _db.Queryable<AttendanceSchedule>()
                .Where(item => !item.IsDeleted)
                .WhereIF(!string.IsNullOrWhiteSpace(query.StoreCode), item => item.StoreCode == query.StoreCode!.Trim())
                .WhereIF(string.IsNullOrWhiteSpace(query.StoreCode) && storeAccess.StoreCodes.Count > 0, item => storeAccess.StoreCodes.Contains(item.StoreCode))
                .WhereIF(!string.IsNullOrWhiteSpace(query.UserGuid), item => item.UserGuid == query.UserGuid!.Trim())
                .WhereIF(query.WeekStartDate.HasValue, item => item.WorkDate >= query.WeekStartDate!.Value.Date && item.WorkDate <= query.WeekStartDate!.Value.Date.AddDays(6))
                .WhereIF(query.FromDate.HasValue, item => item.WorkDate >= query.FromDate!.Value.Date)
                .WhereIF(query.ToDate.HasValue, item => item.WorkDate <= query.ToDate!.Value.Date);
            var total = await rowsQuery.CountAsync();
            // 关键逻辑：先由数据库分页，再批量加载当前页打卡，避免全量记录和逐排班查询。
            var rows = await rowsQuery
                .OrderBy(item => item.WorkDate)
                .OrderBy(item => item.StartTime)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
            var items = rows.Select(item => ToDto(item)).ToList();
            await PopulateScheduleDisplayFieldsAsync(items);
            await PopulateMinorEmploymentComplianceAsync(items);
            await PopulateWorkSessionFieldsAsync(items, rows, reconcileDerivedApprovals: false);
            return ApiResponse<PagedResult<AttendanceScheduleDto>>.OK(new PagedResult<AttendanceScheduleDto>
            {
                Items = items,
                Total = total,
                Page = page,
                PageSize = pageSize,
            });
        }

        public async Task<ApiResponse<MinorEmploymentComplianceEvaluationDto>> PreviewMinorEmploymentComplianceAsync(CreateAttendanceScheduleDto request)
        {
            var validation = ValidateSchedulePayload(request.StoreCode, request.UserGuid, request.StartTime, request.EndTime);
            if (!validation.Success)
                return ApiResponse<MinorEmploymentComplianceEvaluationDto>.Error(validation.Message, validation.ErrorCode);
            var storeAccess = await ResolveManagedStoreAccessAsync(request.StoreCode);
            if (!storeAccess.Success)
                return ApiResponse<MinorEmploymentComplianceEvaluationDto>.Error(storeAccess.Message, storeAccess.ErrorCode);
            var evaluation = await EvaluateMinorEmploymentAsync(request.UserGuid.Trim(), new AttendanceSchedule
            {
                ScheduleGuid = string.Empty, StoreCode = request.StoreCode.Trim(), UserGuid = request.UserGuid.Trim(),
                WorkDate = request.WorkDate.Date, StartTime = request.StartTime, EndTime = request.EndTime, Status = "Draft",
            });
            return ApiResponse<MinorEmploymentComplianceEvaluationDto>.OK(evaluation ?? new(), "预检完成；所有提醒均不阻断排班保存或发布");
        }

        public async Task<ApiResponse<AttendanceScheduleDto>> CreateScheduleAsync(
            CreateAttendanceScheduleDto request
        )
        {
            var validation = ValidateSchedulePayload(request.StoreCode, request.UserGuid, request.StartTime, request.EndTime);
            if (!validation.Success)
            {
                return ApiResponse<AttendanceScheduleDto>.Error(validation.Message, validation.ErrorCode);
            }
            if (!IsValidMealBreakCount(request.MealBreakCount))
            {
                return InvalidMealBreakCountError();
            }

            var storeAccess = await ResolveManagedStoreAccessAsync(request.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<AttendanceScheduleDto>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            // 被排班人必须是绑定本店的有效账号：防止误传或伪造的员工 GUID 在库里建出无主班次。
            var boundUsers = await LoadStoreBoundUserGuidsAsync(request.StoreCode.Trim(), new[] { request.UserGuid.Trim() });
            if (!boundUsers.Contains(request.UserGuid.Trim()))
            {
                return ApiResponse<AttendanceScheduleDto>.Error("员工不存在或不属于该分店", "EMPLOYEE_NOT_IN_STORE");
            }

            if (await HasOverlappingScheduleAsync(null, request.StoreCode, request.UserGuid, request.WorkDate.Date, request.StartTime, request.EndTime))
            {
                return ApiResponse<AttendanceScheduleDto>.Error("同员工同分店同日排班时间不能重叠", "SCHEDULE_OVERLAP");
            }

            var now = DateTime.UtcNow;
            var model = new AttendanceSchedule
            {
                ScheduleGuid = Guid.NewGuid().ToString(),
                StoreCode = request.StoreCode.Trim(),
                UserGuid = request.UserGuid.Trim(),
                WorkDate = request.WorkDate.Date,
                StartTime = request.StartTime,
                EndTime = request.EndTime,
                Status = "Draft",
                Remark = request.Remark,
                MealBreakCount = request.MealBreakCount,
                CreatedAt = now,
                CreatedBy = _currentUserService.GetCurrentUsername(),
                UpdatedAt = now,
                UpdatedBy = _currentUserService.GetCurrentUsername(),
            };

            await _db.Insertable(model).ExecuteCommandAsync();
            var result = ToDto(model);
            result.MinorCompliance = await EvaluateMinorEmploymentAsync(model.UserGuid, model);
            if (result.MinorCompliance != null)
                await RecordMinorEmploymentEvaluationAsync(model.UserGuid, model.StoreCode, model.ScheduleGuid, "schedule_created", result.MinorCompliance);
            return ApiResponse<AttendanceScheduleDto>.OK(result, "排班已创建");
        }

        /// <summary>
        /// 复制上周排班：来源周未取消的排班按星期平移到目标周，新排班为草稿（需再发布）。
        /// 目标周同员工同一天已有排班就整天跳过，避免与店长已手工排的班重叠。
        /// </summary>
        public async Task<ApiResponse<CopyAttendanceScheduleWeekResultDto>> CopyScheduleWeekAsync(
            CopyAttendanceScheduleWeekDto request)
        {
            if (string.IsNullOrWhiteSpace(request.StoreCode))
            {
                return ApiResponse<CopyAttendanceScheduleWeekResultDto>.Error("分店代码不能为空", "STORE_REQUIRED");
            }
            var storeCode = request.StoreCode.Trim();
            var storeAccess = await ResolveManagedStoreAccessAsync(storeCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<CopyAttendanceScheduleWeekResultDto>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            var sourceStart = GetWeekStart(request.SourceWeekStartDate.Date);
            var targetStart = GetWeekStart(request.TargetWeekStartDate.Date);
            if (sourceStart == targetStart)
            {
                return ApiResponse<CopyAttendanceScheduleWeekResultDto>.Error("来源周与目标周不能相同", "SAME_WEEK");
            }

            var sourceRows = await _db.Queryable<AttendanceSchedule>()
                .Where(item =>
                    !item.IsDeleted
                    && item.Status != "Cancelled"
                    && item.StoreCode == storeCode
                    && item.WorkDate >= sourceStart
                    && item.WorkDate < sourceStart.AddDays(7))
                .ToListAsync();
            var targetRows = await _db.Queryable<AttendanceSchedule>()
                .Where(item =>
                    !item.IsDeleted
                    && item.Status != "Cancelled"
                    && item.StoreCode == storeCode
                    && item.WorkDate >= targetStart
                    && item.WorkDate < targetStart.AddDays(7))
                .ToListAsync();
            var occupied = targetRows
                .Select(item => $"{item.UserGuid}|{item.WorkDate:yyyy-MM-dd}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // 来源周里已不属于本店（离职、解绑或无效 GUID）的员工不再复制。
            var boundUsers = await LoadStoreBoundUserGuidsAsync(
                storeCode,
                sourceRows.Select(item => item.UserGuid).Distinct(StringComparer.OrdinalIgnoreCase));

            var now = DateTime.UtcNow;
            var username = _currentUserService.GetCurrentUsername();
            var offset = (targetStart - sourceStart).Days;
            var created = new List<AttendanceSchedule>();
            var skipped = 0;
            foreach (var source in sourceRows.OrderBy(item => item.WorkDate).ThenBy(item => item.StartTime))
            {
                var workDate = source.WorkDate.Date.AddDays(offset);
                if (!boundUsers.Contains(source.UserGuid)
                    || occupied.Contains($"{source.UserGuid}|{workDate:yyyy-MM-dd}"))
                {
                    skipped++;
                    continue;
                }
                created.Add(new AttendanceSchedule
                {
                    ScheduleGuid = Guid.NewGuid().ToString(),
                    StoreCode = storeCode,
                    UserGuid = source.UserGuid,
                    WorkDate = workDate,
                    StartTime = source.StartTime,
                    EndTime = source.EndTime,
                    Status = "Draft",
                    Remark = source.Remark,
                    // 店长对来源班次指定的用餐次数随班次一起复制，null（自动）仍保持自动。
                    MealBreakCount = source.MealBreakCount,
                    CreatedAt = now,
                    CreatedBy = username,
                    UpdatedAt = now,
                    UpdatedBy = username,
                });
            }

            if (created.Count > 0)
            {
                await _db.Insertable(created).ExecuteCommandAsync();
            }
            return ApiResponse<CopyAttendanceScheduleWeekResultDto>.OK(
                new CopyAttendanceScheduleWeekResultDto { CreatedCount = created.Count, SkippedCount = skipped },
                $"已复制 {created.Count} 个班次");
        }

        public async Task<ApiResponse<AttendanceScheduleDto>> UpdateScheduleAsync(
            string scheduleGuid,
            UpdateAttendanceScheduleDto request
        )
        {
            var model = await _db.Queryable<AttendanceSchedule>()
                .FirstAsync(item => item.ScheduleGuid == scheduleGuid && !item.IsDeleted);
            if (model == null)
            {
                return ApiResponse<AttendanceScheduleDto>.Error("排班不存在", "NOT_FOUND");
            }

            var storeAccess = await ResolveManagedStoreAccessAsync(model.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<AttendanceScheduleDto>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            if (request.StartTime >= request.EndTime)
            {
                return ApiResponse<AttendanceScheduleDto>.Error("排班结束时间必须晚于开始时间", "INVALID_TIME_RANGE");
            }
            // 恢复自动时忽略 MealBreakCount，不做范围校验。
            if (!request.ResetMealBreakCount && !IsValidMealBreakCount(request.MealBreakCount))
            {
                return InvalidMealBreakCountError();
            }

            var mutationResource = AttendanceDailyMutationLock.BuildResource(
                model.UserGuid,
                model.StoreCode,
                model.WorkDate);
            await using var processLock = await AttendanceDailyMutationLock.AcquireProcessAsync(
                mutationResource);
            await _db.Ado.BeginTranAsync();
            try
            {
                await AttendanceDailyMutationLock.AcquireDatabaseAsync(_db, mutationResource);
                model = await _db.Queryable<AttendanceSchedule>()
                    .FirstAsync(item => item.ScheduleGuid == scheduleGuid && !item.IsDeleted);
                if (model == null)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendanceScheduleDto>.Error("排班不存在", "NOT_FOUND");
                }
                if (await HasOverlappingScheduleAsync(
                        model.ScheduleGuid,
                        model.StoreCode,
                        model.UserGuid,
                        request.WorkDate.Date,
                        request.StartTime,
                        request.EndTime))
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendanceScheduleDto>.Error(
                        "同员工同分店同日排班时间不能重叠",
                        "SCHEDULE_OVERLAP");
                }

                model.WorkDate = request.WorkDate.Date;
                model.StartTime = request.StartTime;
                model.EndTime = request.EndTime;
                model.Status = NormalizeScheduleStatus(request.Status, defaultStatus: model.Status);
                model.Remark = request.Remark;
                // 用餐次数三态：显式恢复自动 → null；传了次数 → 覆盖；都没传（含旧版 App）→ 保持原值。
                if (request.ResetMealBreakCount)
                {
                    model.MealBreakCount = null;
                }
                else if (request.MealBreakCount.HasValue)
                {
                    model.MealBreakCount = request.MealBreakCount;
                }
                model.UpdatedAt = DateTime.UtcNow;
                model.UpdatedBy = _currentUserService.GetCurrentUsername();
                await _db.Updateable(model).ExecuteCommandAsync();
                if (model.Status == "Active")
                {
                    await ReconcileDerivedApprovalsForScheduleAsync(model.ScheduleGuid);
                }
                else
                {
                    await CancelScheduleDerivedApprovalsAsync(
                        model.ScheduleGuid,
                        "排班已失效，相关待审考勤已自动取消");
                }
                await _db.Ado.CommitTranAsync();
            }
            catch
            {
                await _db.Ado.RollbackTranAsync();
                throw;
            }
            var result = ToDto(model);
            result.MinorCompliance = await EvaluateMinorEmploymentAsync(model.UserGuid, model);
            if (result.MinorCompliance != null)
                await RecordMinorEmploymentEvaluationAsync(model.UserGuid, model.StoreCode, model.ScheduleGuid, "schedule_updated", result.MinorCompliance);
            return ApiResponse<AttendanceScheduleDto>.OK(result, "排班已更新");
        }

        public async Task<ApiResponse<int>> PublishWeekAsync(PublishAttendanceWeekDto request)
        {
            if (string.IsNullOrWhiteSpace(request.StoreCode))
            {
                return ApiResponse<int>.Error("分店代码不能为空", "STORE_REQUIRED");
            }

            var storeCode = request.StoreCode.Trim();
            var storeAccess = await ResolveManagedStoreAccessAsync(storeCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<int>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            var weekStart = request.WeekStartDate.Date;
            var weekEnd = weekStart.AddDays(6);
            var now = DateTime.UtcNow;
            var username = _currentUserService.GetCurrentUsername();
            var affected = await _db.Updateable<AttendanceSchedule>()
                .SetColumns(item => item.Status == "Active")
                .SetColumns(item => item.UpdatedAt == now)
                .SetColumns(item => item.UpdatedBy == username)
                .Where(item =>
                    !item.IsDeleted
                    && item.StoreCode == storeCode
                    && item.Status == "Draft"
                    && item.WorkDate >= weekStart
                    && item.WorkDate <= weekEnd
                )
                .ExecuteCommandAsync();

            var publishedSchedules = await _db.Queryable<AttendanceSchedule>()
                .Where(item => !item.IsDeleted && item.StoreCode == storeCode && item.Status == "Active"
                    && item.WorkDate >= weekStart && item.WorkDate <= weekEnd)
                .ToListAsync();
            var findings = new List<MinorEmploymentComplianceFindingDto>();
            foreach (var schedule in publishedSchedules)
            {
                var evaluation = await EvaluateMinorEmploymentAsync(schedule.UserGuid, schedule);
                if (evaluation != null)
                {
                    findings.AddRange(evaluation.Findings);
                    await RecordMinorEmploymentEvaluationAsync(schedule.UserGuid, schedule.StoreCode, schedule.ScheduleGuid, "schedule_published", evaluation);
                }
            }
            return ApiResponse<int>.OK(affected, findings.Count == 0
                ? "排班已发布"
                : $"排班已发布；保留 {findings.Count} 条未成年用工提醒");
        }

        public async Task<ApiResponse<bool>> DeleteScheduleAsync(string scheduleGuid)
        {
            var model = await _db.Queryable<AttendanceSchedule>()
                .FirstAsync(item => item.ScheduleGuid == scheduleGuid && !item.IsDeleted);
            if (model == null)
            {
                return ApiResponse<bool>.Error("排班不存在", "NOT_FOUND");
            }

            var storeAccess = await ResolveManagedStoreAccessAsync(model.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<bool>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            var mutationResource = AttendanceDailyMutationLock.BuildResource(
                model.UserGuid,
                model.StoreCode,
                model.WorkDate);
            await using var processLock = await AttendanceDailyMutationLock.AcquireProcessAsync(
                mutationResource);
            await _db.Ado.BeginTranAsync();
            try
            {
                await AttendanceDailyMutationLock.AcquireDatabaseAsync(_db, mutationResource);
                model = await _db.Queryable<AttendanceSchedule>()
                    .FirstAsync(item => item.ScheduleGuid == scheduleGuid && !item.IsDeleted);
                if (model == null)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<bool>.Error("排班不存在", "NOT_FOUND");
                }

                model.Status = "Cancelled";
                model.IsDeleted = true;
                model.UpdatedAt = DateTime.UtcNow;
                model.UpdatedBy = _currentUserService.GetCurrentUsername();
                await _db.Updateable(model).ExecuteCommandAsync();
                await CancelScheduleDerivedApprovalsAsync(
                    model.ScheduleGuid,
                    "排班已删除，相关待审考勤已自动取消");
                await _db.Ado.CommitTranAsync();
            }
            catch
            {
                await _db.Ado.RollbackTranAsync();
                throw;
            }
            return ApiResponse<bool>.OK(true, "排班已取消");
        }

        public async Task<ApiResponse<AttendanceTodayDto>> GetMyTodayAsync(
            DateTime? workDate = null,
            string? storeCode = null
        )
        {
            var userGuid = ResolveCurrentUserGuid();
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return ApiResponse<AttendanceTodayDto>.Error("无法识别当前员工", "USER_NOT_FOUND");
            }

            var access = await ResolveRelatedStoreAccessAsync(
                userGuid,
                storeCode,
                includeRelatedStoreCodes: true);
            if (!access.Success)
            {
                return ApiResponse<AttendanceTodayDto>.Error(access.Message, access.ErrorCode);
            }

            var today = (workDate ?? DateTime.Today).Date;
            var tomorrow = today.AddDays(1);
            // 空范围沿用原语义：不加门店条件；否则加载请求门店与关联门店的并集。
            var snapshotStoreCodes = access.StoreCodes.Count == 0 || access.RelatedStoreCodes.Count == 0
                ? new List<string>()
                : access.StoreCodes
                    .Concat(access.RelatedStoreCodes)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            // 先按统一快照范围读取排班，确保一次锁内协调覆盖当前门店和关联门店。
            var scheduleSnapshots = await _db.Queryable<AttendanceSchedule>()
                .Where(item =>
                    !item.IsDeleted
                    && item.Status == "Active"
                    && item.UserGuid == userGuid
                    && item.WorkDate >= today
                    && item.WorkDate < tomorrow
                )
                .WhereIF(snapshotStoreCodes.Count > 0, item => snapshotStoreCodes.Contains(item.StoreCode))
                .ToListAsync();
            var holidays = await _db.Queryable<AttendanceStoreHoliday>()
                .Where(item => !item.IsDeleted && item.HolidayDate >= today && item.HolidayDate < tomorrow)
                .WhereIF(access.StoreCodes.Count > 0, item => access.StoreCodes.Contains(item.StoreCode))
                .ToListAsync();

            // GET 会创建/取消派生审批；协调提交后再读取一次统一快照，供两个门店视图共同投影。
            await ReconcileDerivedApprovalsUnderEmployeeLocksAsync(scheduleSnapshots);
            var schedules = await _db.Queryable<AttendanceSchedule>()
                .Where(item =>
                    !item.IsDeleted
                    && item.Status == "Active"
                    && item.UserGuid == userGuid
                    && item.WorkDate >= today
                    && item.WorkDate < tomorrow
                )
                .WhereIF(snapshotStoreCodes.Count > 0, item => snapshotStoreCodes.Contains(item.StoreCode))
                .ToListAsync();
            var punches = await _db.Queryable<AttendancePunch>()
                .Where(item => !item.IsDeleted
                    && item.UserGuid == userGuid
                    && item.WorkDate >= today
                    && item.WorkDate < tomorrow)
                .WhereIF(snapshotStoreCodes.Count > 0, item => snapshotStoreCodes.Contains(item.StoreCode))
                .ToListAsync();

            var allScheduleDtos = schedules.Select(item => ToDto(item, userGuid)).ToList();
            var punchDtos = punches.Select(item => ToDto(item)).ToList();
            await PopulateScheduleDisplayFieldsAsync(allScheduleDtos);
            await PopulateWorkSessionFieldsAsync(
                allScheduleDtos,
                schedules,
                punches,
                punchDtos,
                reconcileDerivedApprovals: false);
            var scheduleDtos = access.StoreCodes.Count == 0
                ? allScheduleDtos
                : allScheduleDtos.Where(item => access.StoreCodes.Contains(
                    item.StoreCode,
                    StringComparer.OrdinalIgnoreCase)).ToList();
            var requestedStorePunchDtos = access.StoreCodes.Count == 0
                ? punchDtos
                : punchDtos.Where(item => access.StoreCodes.Contains(
                    item.StoreCode,
                    StringComparer.OrdinalIgnoreCase)).ToList();
            // 刷新首页时也重新计算未下班的实际班段；评估和待办归档都是最佳努力，绝不影响考勤查询。
            var minorCompliance = await EvaluateMinorEmploymentAsync(userGuid, referenceDate: today);
            if (minorCompliance != null && !string.IsNullOrWhiteSpace(scheduleDtos.FirstOrDefault()?.StoreCode ?? storeCode))
                await RecordMinorEmploymentReminderAsync(userGuid, scheduleDtos.FirstOrDefault()?.StoreCode ?? storeCode!, scheduleDtos.FirstOrDefault()?.ScheduleGuid, "actual_ongoing", minorCompliance);
            var todayDto = new AttendanceTodayDto
            {
                WorkDate = today,
                MinorCompliance = minorCompliance,
                Schedules = scheduleDtos,
                Punches = requestedStorePunchDtos,
                Holidays = holidays.Select(ToDto).ToList(),
                CanRequestAdjustment = await IsWithinAdjustmentWindowAsync(
                    today,
                    scheduleDtos.FirstOrDefault()?.StoreCode ?? storeCode),
                StorePunchStates = await BuildRelatedStorePunchStatesAsync(
                    allScheduleDtos,
                    punches,
                    access.RelatedStoreCodes),
            };
            return ApiResponse<AttendanceTodayDto>.OK(todayDto);
        }

        public async Task<ApiResponse<List<AttendanceScheduleDto>>> GetMyWeekAsync(
            DateTime? weekStartDate,
            string? storeCode = null
        )
        {
            var userGuid = ResolveCurrentUserGuid();
            var access = await ResolveRelatedStoreAccessAsync(userGuid, storeCode);
            if (!access.Success)
            {
                return ApiResponse<List<AttendanceScheduleDto>>.Error(access.Message, access.ErrorCode);
            }

            var start = (weekStartDate ?? GetWeekStart(DateTime.Today)).Date;
            var end = start.AddDays(6);
            var rows = await _db.Queryable<AttendanceSchedule>()
                .Where(item =>
                    !item.IsDeleted
                    && item.Status == "Active"
                    && item.UserGuid == userGuid
                    && item.WorkDate >= start
                    && item.WorkDate <= end
                )
                .WhereIF(access.StoreCodes.Count > 0, item => access.StoreCodes.Contains(item.StoreCode))
                .OrderBy(item => item.WorkDate)
                .OrderBy(item => item.StartTime)
                .ToListAsync();

            var result = rows.Select(item => ToDto(item, userGuid)).ToList();
            await PopulateScheduleDisplayFieldsAsync(result);
            await PopulateWorkSessionFieldsAsync(result, rows);
            return ApiResponse<List<AttendanceScheduleDto>>.OK(result);
        }

        public async Task<ApiResponse<List<AttendanceAvailabilityDto>>> GetMyAvailabilityAsync(
            DateTime? weekStartDate,
            string? storeCode = null
        )
        {
            var userGuid = ResolveCurrentUserGuid();
            var access = await ResolveRelatedStoreAccessAsync(userGuid, storeCode);
            if (!access.Success)
            {
                return ApiResponse<List<AttendanceAvailabilityDto>>.Error(access.Message, access.ErrorCode);
            }

            var start = (weekStartDate ?? GetWeekStart(DateTime.Today)).Date;
            var nextWeek = start.AddDays(7);
            var rows = await _db.Queryable<AttendanceAvailability>()
                .Where(item => !item.IsDeleted && item.UserGuid == userGuid && item.WeekStartDate >= start && item.WeekStartDate < nextWeek)
                .WhereIF(access.StoreCodes.Count > 0, item => access.StoreCodes.Contains(item.StoreCode))
                .OrderBy(item => item.AvailableDate)
                .OrderBy(item => item.StartTime)
                .ToListAsync();

            return ApiResponse<List<AttendanceAvailabilityDto>>.OK(rows.Select(ToDto).ToList());
        }

        public async Task<ApiResponse<List<AttendanceAvailabilityDto>>> CreateMyAvailabilityAsync(
            CreateAttendanceAvailabilityDto request
        )
        {
            var userGuid = ResolveCurrentUserGuid();
            var access = await ResolveRelatedStoreAccessAsync(userGuid, request.StoreCode);
            if (!access.Success)
            {
                return ApiResponse<List<AttendanceAvailabilityDto>>.Error(access.Message, access.ErrorCode);
            }

            var weekStart = request.WeekStartDate.Date;
            if (request.Segments.Count == 0)
            {
                return ApiResponse<List<AttendanceAvailabilityDto>>.Error("请至少提交一个可上班时间段", "EMPTY_SEGMENTS");
            }

            if (request.Segments.Any(segment => segment.StartTime >= segment.EndTime))
            {
                return ApiResponse<List<AttendanceAvailabilityDto>>.Error("可上班结束时间必须晚于开始时间", "INVALID_TIME_RANGE");
            }

            if (request.Segments.Any(segment => segment.AvailableDate.Date < weekStart || segment.AvailableDate.Date > weekStart.AddDays(6)))
            {
                return ApiResponse<List<AttendanceAvailabilityDto>>.Error("可上班日期必须在指定周内", "DATE_OUT_OF_WEEK");
            }

            var now = DateTime.UtcNow;
            var rows = request.Segments.Select(segment => new AttendanceAvailability
            {
                AvailabilityGuid = Guid.NewGuid().ToString(),
                StoreCode = request.StoreCode.Trim(),
                UserGuid = userGuid,
                WeekStartDate = weekStart,
                AvailableDate = segment.AvailableDate.Date,
                StartTime = segment.StartTime,
                EndTime = segment.EndTime,
                // 同一张表区分可上班与不能上班，旧客户端不传即为可上班。
                IsUnavailable = segment.IsUnavailable,
                Status = "Active",
                Remark = segment.Remark,
                CreatedAt = now,
                CreatedBy = _currentUserService.GetCurrentUsername(),
                UpdatedAt = now,
                UpdatedBy = _currentUserService.GetCurrentUsername(),
            }).ToList();

            await _db.Insertable(rows).ExecuteCommandAsync();
            return ApiResponse<List<AttendanceAvailabilityDto>>.OK(rows.Select(ToDto).ToList(), "可上班时间已提交");
        }

        public async Task<ApiResponse<AttendanceAvailabilityDto>> UpdateMyAvailabilityAsync(
            string availabilityGuid,
            UpdateAttendanceAvailabilityDto request
        )
        {
            var userGuid = ResolveCurrentUserGuid();
            var model = await _db.Queryable<AttendanceAvailability>()
                .FirstAsync(item => item.AvailabilityGuid == availabilityGuid && item.UserGuid == userGuid && !item.IsDeleted);
            if (model == null)
            {
                return ApiResponse<AttendanceAvailabilityDto>.Error("可上班时间不存在", "NOT_FOUND");
            }

            var access = await ResolveRelatedStoreAccessAsync(userGuid, model.StoreCode);
            if (!access.Success)
            {
                return ApiResponse<AttendanceAvailabilityDto>.Error(access.Message, access.ErrorCode);
            }

            model.AvailableDate = request.AvailableDate.Date;
            model.WeekStartDate = GetWeekStart(request.AvailableDate.Date);
            model.StartTime = request.StartTime;
            model.EndTime = request.EndTime;
            model.Remark = request.Remark;
            // 旧 App 更新时不带类型字段，保持原值，避免把不能上班误改回可上班。
            if (request.IsUnavailable.HasValue)
            {
                model.IsUnavailable = request.IsUnavailable.Value;
            }
            model.UpdatedAt = DateTime.UtcNow;
            model.UpdatedBy = _currentUserService.GetCurrentUsername();
            await _db.Updateable(model).ExecuteCommandAsync();
            return ApiResponse<AttendanceAvailabilityDto>.OK(ToDto(model), "可上班时间已更新");
        }

        public async Task<ApiResponse<bool>> CancelMyAvailabilityAsync(string availabilityGuid)
        {
            var userGuid = ResolveCurrentUserGuid();
            var model = await _db.Queryable<AttendanceAvailability>()
                .FirstAsync(item => item.AvailabilityGuid == availabilityGuid && item.UserGuid == userGuid && !item.IsDeleted);
            if (model == null)
            {
                return ApiResponse<bool>.Error("可上班时间不存在", "NOT_FOUND");
            }

            model.Status = "Cancelled";
            model.IsDeleted = true;
            model.UpdatedAt = DateTime.UtcNow;
            model.UpdatedBy = _currentUserService.GetCurrentUsername();
            await _db.Updateable(model).ExecuteCommandAsync();
            return ApiResponse<bool>.OK(true, "可上班时间已取消");
        }

        public async Task<ApiResponse<AttendancePunchDto>> PunchAsync(AttendancePunchRequestDto request)
        {
            var userGuid = ResolveCurrentUserGuid();
            if (string.IsNullOrWhiteSpace(request.QrToken))
            {
                return ApiResponse<AttendancePunchDto>.Error("请扫描 POS 考勤二维码后打卡", "QR_REQUIRED");
            }

            if (!AttendanceQrTokenCodec.TryGetKeyId(request.QrToken, out var signingKeyId, out var tokenError))
            {
                return ApiResponse<AttendancePunchDto>.Error("考勤二维码格式无效", tokenError);
            }

            var signingKey = await _db.Queryable<AttendancePosQrKey>()
                .FirstAsync(item => item.Kid == signingKeyId);
            if (signingKey == null)
            {
                return ApiResponse<AttendancePunchDto>.Error("考勤二维码密钥未知", "ATTENDANCE_QR_KEY_UNKNOWN");
            }
            if (!string.Equals(signingKey.Status, "Active", StringComparison.Ordinal))
            {
                return ApiResponse<AttendancePunchDto>.Error("考勤二维码密钥已撤销", "ATTENDANCE_QR_KEY_REVOKED");
            }
            if (!string.Equals(signingKey.Algorithm, "A256GCM", StringComparison.Ordinal))
            {
                return ApiResponse<AttendancePunchDto>.Error(
                    "考勤二维码密钥算法无效", "ATTENDANCE_QR_KEY_INVALID");
            }

            var serverNow = _timeProvider.GetUtcNow().UtcDateTime;
            byte[] qrKey;
            try
            {
                qrKey = _attendanceQrKeyProtector.Unprotect(signingKey.ProtectedKey);
            }
            catch (Exception exception) when (exception is CryptographicException or FormatException)
            {
                return ApiResponse<AttendancePunchDto>.Error(
                    "考勤二维码密钥无法安全读取",
                    "ATTENDANCE_QR_KEY_DECRYPT_FAILED");
            }

            AttendanceQrTokenPayload? tokenPayload;
            try
            {
                if (!AttendanceQrTokenCodec.TryDecryptIdentity(
                        request.QrToken,
                        qrKey,
                        out tokenPayload,
                        out _,
                        out tokenError))
                {
                    return ApiResponse<AttendancePunchDto>.Error("考勤二维码验证失败", tokenError);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(qrKey);
            }

            // 关键逻辑：门店和设备只信任签名载荷及登记记录，忽略手机请求中的同名字段。
            if (!string.Equals(tokenPayload!.StoreCode, signingKey.StoreCode, StringComparison.Ordinal)
                || !string.Equals(tokenPayload.DeviceCode, signingKey.DeviceCode, StringComparison.Ordinal))
            {
                return ApiResponse<AttendancePunchDto>.Error(
                    "考勤二维码与登记设备不匹配",
                    "ATTENDANCE_QR_DEVICE_MISMATCH");
            }

            if (_attendancePosDeviceStatusProvider == null
                || !await _attendancePosDeviceStatusProvider.IsActiveAsync(
                    signingKey.DeviceCode,
                    signingKey.StoreCode,
                    signingKey.HardwareId))
            {
                return ApiResponse<AttendancePunchDto>.Error("POS 设备已停用", "POS_DEVICE_DISABLED");
            }

            var storeCode = tokenPayload.StoreCode;

            var access = await ResolveRelatedStoreAccessAsync(userGuid, storeCode);
            if (!access.Success)
            {
                return ApiResponse<AttendancePunchDto>.Error(access.Message, access.ErrorCode);
            }

            var employee = await _db.Queryable<User>().FirstAsync(item => item.UserGUID == userGuid);
            var store = await _db.Queryable<Store>().FirstAsync(item => item.StoreCode == storeCode);
            var employeeName = employee?.FullName ?? employee?.Username;
            var storeName = store?.StoreName;

            var tokenId = tokenPayload.TokenId.ToString();
            var existingTokenPunch = await _db.Queryable<AttendancePunch>().FirstAsync(item =>
                item.UserGuid == userGuid && item.QrTokenId == tokenId);
            if (existingTokenPunch != null)
            {
                return ApiResponse<AttendancePunchDto>.OK(
                    ToDto(existingTokenPunch, employeeName, storeName, serverNow),
                    "打卡已保存");
            }

            var hasPunchAuthorization = !string.IsNullOrWhiteSpace(
                request.PunchAuthorizationToken);
            if (hasPunchAuthorization)
            {
                // 关键逻辑：凭证只替代二维码过期检查，原二维码认证和所有设备、门店校验仍照常执行。
                var authorizationValidation = _punchAuthorizationProtector.Validate(
                    request.PunchAuthorizationToken,
                    userGuid,
                    request.QrToken,
                    signingKeyId,
                    tokenPayload,
                    serverNow);
                if (authorizationValidation == AttendancePunchAuthorizationValidationResult.Expired)
                {
                    return ApiResponse<AttendancePunchDto>.Error(
                        "打卡授权凭证已过期，请重新扫码",
                        "ATTENDANCE_PUNCH_AUTHORIZATION_EXPIRED");
                }
                if (authorizationValidation != AttendancePunchAuthorizationValidationResult.Valid)
                {
                    return ApiResponse<AttendancePunchDto>.Error(
                        "打卡授权凭证无效，请重新扫码",
                        "ATTENDANCE_PUNCH_AUTHORIZATION_INVALID");
                }
            }

            if (!AttendanceQrTokenCodec.TryValidateLifetime(tokenPayload, serverNow, out tokenError)
                && (!hasPunchAuthorization
                    || !string.Equals(
                        tokenError,
                        "ATTENDANCE_QR_EXPIRED",
                        StringComparison.Ordinal)))
            {
                return ApiResponse<AttendancePunchDto>.Error("考勤二维码验证失败", tokenError);
            }

            var locationValidation = RequiredLocationValidator.Validate(
                request.LocationLatitude,
                request.LocationLongitude,
                // Expo 仅发送真实 GPS 样本；操作系统权限状态不作为可伪造的网络字段上传。
                "granted",
                request.LocationCapturedAtUtc,
                "打卡需要位置信息",
                TimeSpan.FromMinutes(5)
            );
            if (!locationValidation.Success)
            {
                return ApiResponse<AttendancePunchDto>.Error(
                    locationValidation.Message,
                    locationValidation.ErrorCode
                );
            }

            var storeTimeZone = store == null || store.IsDeleted
                ? DefaultStoreTimeZone
                : ResolveStoreTimeZoneFromStore(store) ?? DefaultStoreTimeZone;
            var punchUtc = DateTime.SpecifyKind(serverNow, DateTimeKind.Utc);
            var punchLocal = ConvertUtcToStoreLocal(punchUtc, storeTimeZone);
            var workDate = punchLocal.Date;
            var observedPunchCount = await _db.Queryable<AttendancePunch>().CountAsync(item =>
                !item.IsDeleted
                && item.UserGuid == userGuid
                && item.StoreCode == storeCode
                && item.WorkDate >= workDate
                && item.WorkDate < workDate.AddDays(1));
            var mutationResource = AttendanceDailyMutationLock.BuildResource(
                userGuid,
                storeCode,
                workDate);
            await using var processLock = await AttendanceDailyMutationLock.AcquireProcessAsync(
                mutationResource);
            await _db.Ado.BeginTranAsync();
            try
            {
                await AttendanceDailyMutationLock.AcquireDatabaseAsync(_db, mutationResource);
                // 进锁后必须重新查 token；不同实例可能已经完成同一二维码请求。
                existingTokenPunch = await _db.Queryable<AttendancePunch>().FirstAsync(item =>
                    item.UserGuid == userGuid && item.QrTokenId == tokenId);
                if (existingTokenPunch != null)
                {
                    await _db.Ado.CommitTranAsync();
                    return ApiResponse<AttendancePunchDto>.OK(
                        ToDto(existingTokenPunch, employeeName, storeName, serverNow),
                        "打卡已保存");
                }
            var settings = await GetOrCreateSettingsModelAsync();
            var scheduleSegmentLimit = await ResolveSegmentLimitAsync(userGuid, storeCode);
            var schedule = await FindScheduleForPunchAsync(
                storeCode,
                userGuid,
                workDate,
                punchLocal.TimeOfDay,
                scheduleSegmentLimit,
                settings);
            var sameDayUserPunches = await _db.Queryable<AttendancePunch>()
                .Where(item =>
                    !item.IsDeleted
                    && item.UserGuid == userGuid
                    && item.WorkDate >= workDate
                    && item.WorkDate < workDate.AddDays(1))
                .ToListAsync();
            var adjacentBusinessDayPunches = await GetAdjacentBusinessDayPunchesAsync(
                userGuid,
                workDate);
            var todayPunches = sameDayUserPunches
                .Where(item => item.StoreCode.Equals(storeCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
            string punchType;
            var segmentLimit = schedule == null ? 1 : scheduleSegmentLimit;
            var isFirstClockIn = false;
            var isFinalClockOut = false;
            if (schedule == null)
            {
                if (todayPunches.Any(item => item.ScheduleGuid != null)
                    && CountEffectiveStoreSegments(todayPunches) >= scheduleSegmentLimit)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendancePunchDto>.Error(
                        "该员工在本店今日班段数量已达到上限",
                        "SEGMENT_LIMIT_REACHED");
                }
                var hasClockIn = todayPunches.Any(item => item.PunchType == "ClockIn");
                var hasClockOut = todayPunches.Any(item => item.PunchType == "ClockOut");
                if (hasClockIn && hasClockOut)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendancePunchDto>.Error("今日打卡已完成", "DAY_COMPLETE");
                }
                punchType = hasClockIn ? "ClockOut" : "ClockIn";
                isFirstClockIn = punchType == "ClockIn";
                isFinalClockOut = punchType == "ClockOut";
            }
            else
            {
                var existingSession = AttendanceWorkSessionCalculator.Calculate(
                    schedule,
                    todayPunches,
                    segmentLimit,
                    punchLocal,
                    settings.EarlyLeaveGraceMinutes,
                    settings.LateGraceMinutes);
                if (!existingSession.HasOpenSegment
                    && existingSession.Segments.Count >= segmentLimit)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendancePunchDto>.Error(
                        "班段数量已达到今日上限",
                        "SEGMENT_LIMIT_REACHED");
                }
                if (!existingSession.HasOpenSegment
                    && existingSession.ScheduleState == "Completed")
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendancePunchDto>.Error(
                        "今日排班打卡已完成",
                        "DAY_COMPLETE");
                }
                punchType = existingSession.HasOpenSegment ? "ClockOut" : "ClockIn";
                isFirstClockIn = punchType == "ClockIn" && existingSession.Segments.Count == 0;
                isFinalClockOut = punchType == "ClockOut"
                    && (existingSession.Segments.Count >= segmentLimit
                        || punchLocal.TimeOfDay >= schedule.EndTime.Subtract(
                            TimeSpan.FromMinutes(settings.EarlyLeaveGraceMinutes)));
            }
            if (punchType == "ClockIn"
                && CountEffectiveStoreSegments(todayPunches) >= segmentLimit)
            {
                await _db.Ado.RollbackTranAsync();
                return ApiResponse<AttendancePunchDto>.Error(
                    "该员工在本店今日班段数量已达到上限",
                    "SEGMENT_LIMIT_REACHED");
            }

            var status = ResolveSegmentPunchStatus(
                schedule,
                punchType,
                punchLocal.TimeOfDay,
                settings,
                isFirstClockIn,
                isFinalClockOut);
            if (status == "NoSchedule" && !settings.AllowNoSchedulePunch)
            {
                await _db.Ado.RollbackTranAsync();
                return ApiResponse<AttendancePunchDto>.Error("当前没有排班，无法打卡", "NO_SCHEDULE");
            }

            var latestPunch = todayPunches
                .OrderByDescending(item => item.PunchTimeUtc)
                .FirstOrDefault();
            if (latestPunch != null
                && latestPunch.QrTokenId != tokenId
                && todayPunches.Count > observedPunchCount)
            {
                await _db.Ado.RollbackTranAsync();
                return ApiResponse<AttendancePunchDto>.Error(
                    "另一次打卡正在处理，请稍后重试",
                    "PUNCH_CONCURRENT_CONFLICT");
            }

            var now = serverNow;
            var punch = new AttendancePunch
            {
                PunchGuid = Guid.NewGuid().ToString(),
                ScheduleGuid = schedule?.ScheduleGuid,
                StoreCode = storeCode,
                UserGuid = userGuid,
                WorkDate = workDate,
                StoreTimeZone = storeTimeZone,
                PunchType = punchType,
                PunchTimeUtc = punchUtc,
                PunchTimeLocal = punchLocal,
                Status = status,
                DeviceId = request.DeviceId,
                LocationLatitude = request.LocationLatitude,
                LocationLongitude = request.LocationLongitude,
                LocationAccuracyMeters = request.LocationAccuracy,
                LocationPermissionStatus = NormalizeOptional(request.LocationPermissionStatus, 30),
                LocationCapturedAtUtc = NormalizeUtc(request.LocationCapturedAtUtc),
                Source = "PosQr",
                QrTokenId = tokenId,
                PosDeviceCode = signingKey.DeviceCode,
                SigningKeyId = signingKey.Kid,
                Remark = request.Remark,
                CreatedAt = now,
                CreatedBy = _currentUserService.GetCurrentUsername(),
            };

            if (punchType == "ClockOut"
                && HasCrossScheduleOverlap(adjacentBusinessDayPunches.Append(punch).ToList()))
            {
                await _db.Ado.RollbackTranAsync();
                return ApiResponse<AttendancePunchDto>.Error(
                    "该下班卡会与其他分店的已完成班段重叠，请通过补卡修正漏打记录",
                    "CROSS_STORE_PUNCH_OVERLAP");
            }

            await _db.Insertable(punch).ExecuteCommandAsync();
            if (RequiresApproval(status, settings))
            {
                await CreatePendingApprovalAsync("Punch", punch.PunchGuid, punch.StoreCode, userGuid);
            }
            if (schedule != null)
            {
                var updatedSession = AttendanceWorkSessionCalculator.Calculate(
                    schedule,
                    todayPunches.Append(punch),
                    segmentLimit,
                    punchLocal,
                    settings.EarlyLeaveGraceMinutes,
                    settings.LateGraceMinutes);
                // 打卡恢复漏下班、覆盖早退边界等状态时，派生审批必须在本次员工锁内同步撤销或更新。
                await ReconcileOvertimeApprovalAsync(schedule, updatedSession);
                await ReconcileFinalPunchApprovalsAsync(schedule, updatedSession, settings);
                await ReconcileMissingClockOutApprovalAsync(schedule, updatedSession);
            }
            await _db.Ado.CommitTranAsync();
            var resultDto = ToDto(punch, employeeName, storeName, serverNow);
            resultDto.MinorCompliance = await EvaluateMinorEmploymentAsync(userGuid, schedule, punch);
            if (resultDto.MinorCompliance != null)
            {
                await RecordMinorEmploymentEvaluationAsync(userGuid, punch.StoreCode, punch.ScheduleGuid, "punch_recorded", resultDto.MinorCompliance);
            }
            if (schedule != null)
            {
                var updatedSession = AttendanceWorkSessionCalculator.Calculate(
                    schedule,
                    todayPunches.Append(punch),
                    segmentLimit,
                    punchLocal,
                    settings.EarlyLeaveGraceMinutes,
                    settings.LateGraceMinutes);
                var segment = updatedSession.Segments.FirstOrDefault(item =>
                    item.ClockIn?.PunchGuid == punch.PunchGuid
                    || item.ClockOut?.PunchGuid == punch.PunchGuid);
                if (segment != null)
                {
                    var projectedPunch = punchType == "ClockIn" ? segment.ClockIn : segment.ClockOut;
                    ApplySegmentMetadata(
                        projectedPunch,
                        segment,
                        new Dictionary<string, AttendancePunchDto>(StringComparer.OrdinalIgnoreCase)
                        {
                            [resultDto.PunchGuid] = resultDto,
                        });
                }
            }
            return ApiResponse<AttendancePunchDto>.OK(
                resultDto,
                "打卡已保存");
            }
            catch (Exception exception) when (
                AttendancePunchPersistenceException.IsUniqueConstraintViolation(exception))
            {
                await _db.Ado.RollbackTranAsync();
                // 关键逻辑：并发重复扫码由数据库唯一索引裁决，输掉竞争的请求返回首次结果，不能翻转动作。
                var firstResult = await _db.Queryable<AttendancePunch>().FirstAsync(item =>
                    item.UserGuid == userGuid && item.QrTokenId == tokenId);
                if (firstResult != null)
                {
                    return ApiResponse<AttendancePunchDto>.OK(
                        ToDto(firstResult, employeeName, storeName, serverNow),
                        "打卡已保存");
                }

                throw;
            }
            catch
            {
                await _db.Ado.RollbackTranAsync();
                throw;
            }
        }

        public Task<ApiResponse<AttendancePunchAdjustmentPreviewDto>> PreviewMyPunchAdjustmentAsync(
            CreateAttendancePunchAdjustmentDto request) =>
            PreviewPunchAdjustmentAsync(request, managedTargetUserGuid: null);

        public async Task<ApiResponse<AttendancePunchAdjustmentPreviewDto>> PreviewManagedPunchAdjustmentAsync(
            CreateManagedAttendancePunchAdjustmentDto request)
        {
            if (string.IsNullOrWhiteSpace(request.UserGuid))
            {
                return ApiResponse<AttendancePunchAdjustmentPreviewDto>.Error("员工不能为空", "USER_REQUIRED");
            }

            return await PreviewPunchAdjustmentAsync(request, request.UserGuid.Trim());
        }

        private async Task<ApiResponse<AttendancePunchAdjustmentPreviewDto>> PreviewPunchAdjustmentAsync(
            CreateAttendancePunchAdjustmentDto request,
            string? managedTargetUserGuid)
        {
            var context = await BuildPunchAdjustmentContextAsync(request, managedTargetUserGuid);
            if (!context.Success)
            {
                return ApiResponse<AttendancePunchAdjustmentPreviewDto>.Error(
                    context.Message,
                    context.ErrorCode);
            }

            return ApiResponse<AttendancePunchAdjustmentPreviewDto>.OK(context.Preview!);
        }

        public async Task<ApiResponse<List<AttendancePunchAdjustmentDto>>> GetMyPunchAdjustmentsAsync()
        {
            var userGuid = ResolveCurrentUserGuid();
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return ApiResponse<List<AttendancePunchAdjustmentDto>>.Error(
                    "无法识别当前员工",
                    "USER_NOT_FOUND");
            }

            var rows = await _db.Queryable<AttendancePunchAdjustment>()
                .Where(item => !item.IsDeleted && item.UserGuid == userGuid)
                .OrderByDescending(item => item.CreatedAt)
                .ToListAsync();
            var adjustmentGuids = rows.Select(item => item.AdjustmentGuid).ToList();
            // 驳回原因记在审核记录上；同一补卡只取最近一次已处理的审核。
            var remarks = adjustmentGuids.Count == 0
                ? new Dictionary<string, string?>()
                : (await _db.Queryable<AttendanceApproval>()
                    .Where(item =>
                        !item.IsDeleted
                        && item.SourceType == "PunchAdjustment"
                        && adjustmentGuids.Contains(item.SourceGuid)
                        && item.ReviewedAt != null)
                    .ToListAsync())
                    .GroupBy(item => item.SourceGuid, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.OrderByDescending(item => item.ReviewedAt).First().ReviewRemark,
                        StringComparer.OrdinalIgnoreCase);
            var reviewerNames = await ResolveUserDisplayNamesAsync(
                rows.Select(item => item.ReviewedByUserGuid));
            return ApiResponse<List<AttendancePunchAdjustmentDto>>.OK(rows.Select(item =>
            {
                var dto = ToDto(item);
                dto.ReviewRemark = remarks.GetValueOrDefault(item.AdjustmentGuid);
                dto.ReviewedByName = item.ReviewedByUserGuid != null
                    ? reviewerNames.GetValueOrDefault(item.ReviewedByUserGuid)
                    : null;
                return dto;
            }).ToList());
        }

        /// <summary>
        /// 考勤排班、登记请假用的本店员工列表。不复用用户管理接口（需要 Users.View 且返回联系方式），
        /// 只返回身份与用工类型；口径与店员列表一致（StoreStaff 角色 + 关联该店）。
        /// </summary>
        public async Task<ApiResponse<List<AttendanceEmployeeDto>>> GetStoreEmployeesAsync(string? storeCode)
        {
            var allowed = IsAdmin();
            foreach (var permission in new[]
            {
                Permissions.Attendance.Schedule.ViewStore,
                Permissions.Attendance.Schedule.EditManagedStore,
                Permissions.Attendance.Leave.ViewManagedStore,
                Permissions.Attendance.Leave.ReviewManagedStore,
                Permissions.Attendance.Admin.View,
            })
            {
                if (allowed) break;
                allowed = await CurrentUserHasPermissionAsync(permission);
            }
            if (!allowed)
            {
                return ApiResponse<List<AttendanceEmployeeDto>>.Error("没有权限查看分店员工", "FORBIDDEN");
            }
            if (string.IsNullOrWhiteSpace(storeCode))
            {
                return ApiResponse<List<AttendanceEmployeeDto>>.Error("分店代码不能为空", "STORE_REQUIRED");
            }

            var normalizedStoreCode = storeCode.Trim();
            var access = await ResolveManagedStoreAccessAsync(normalizedStoreCode);
            if (!access.Success)
            {
                return ApiResponse<List<AttendanceEmployeeDto>>.Error(access.Message, access.ErrorCode);
            }

            // 可排班员工：店员与绑定在本店的店长（店长也要上班，需要给自己排班）。
            // 仓库管理员不排门店班：一并查出其角色，下面整人剔除（即使同时是店长）。
            var schedulableRoleNames = Permissions.StoreManagerRoleNames.Append("StoreStaff").ToArray();
            var queriedRoleNames = schedulableRoleNames.Concat(Permissions.WarehouseManagerRoleNames).ToArray();
            var rows = await _db.Queryable<User>()
                .InnerJoin<UserRole>((u, ur) => u.UserGUID == ur.UserGUID)
                .InnerJoin<Role>((u, ur, r) => ur.RoleGUID == r.RoleGUID)
                .InnerJoin<UserStore>((u, ur, r, us) => u.UserGUID == us.UserGUID)
                .InnerJoin<Store>((u, ur, r, us, s) => us.StoreGUID == s.StoreGUID)
                .LeftJoin<EmployeeProfile>((u, ur, r, us, s, profile) => u.UserGUID == profile.UserGUID)
                .Where((u, ur, r, us, s) =>
                    !u.IsDeleted
                    && !ur.IsDeleted
                    && !r.IsDeleted
                    && !us.IsDeleted
                    && !s.IsDeleted
                    && queriedRoleNames.Contains(r.RoleName)
                    && s.StoreCode == normalizedStoreCode)
                .Select((u, ur, r, us, s, profile) => new
                {
                    u.UserGUID,
                    u.Username,
                    u.FullName,
                    profile.EmployeeType,
                    profile.Birthday,
                    r.RoleName,
                    us.IsPrimary,
                })
                .ToListAsync();
            // 年龄按门店当地日期计算，避免跨时区在生日前后差一天。
            var storeToday = ConvertUtcToStoreLocal(
                _timeProvider.GetUtcNow().UtcDateTime,
                await ResolveStoreTimeZoneAsync(normalizedStoreCode, null)).Date;
            var employees = rows
                .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
                .Where(group =>
                    group.Any(row => schedulableRoleNames.Contains(row.RoleName, StringComparer.OrdinalIgnoreCase))
                    && !group.Any(row => Permissions.WarehouseManagerRoleNames.Contains(
                        row.RoleName,
                        StringComparer.OrdinalIgnoreCase)))
                .Select(group =>
                {
                    var item = group.First();
                    // 店长分两类：本店是其主分店（管理本店）与仅关联本店。口径与三段打卡上限一致（店长角色 + 主分店关系）。
                    // 每行的 IsPrimary 都是该员工与本店的绑定关系（已按本店过滤）。
                    var isStoreManager = group.Any(row => Permissions.StoreManagerRoleNames.Contains(
                        row.RoleName,
                        StringComparer.OrdinalIgnoreCase));
                    return new AttendanceEmployeeDto
                    {
                        UserGuid = item.UserGUID,
                        Username = item.Username,
                        FullName = item.FullName,
                        EmploymentType = item.EmployeeType switch
                        {
                            EmployeeType.FullTime => "fullTime",
                            EmployeeType.PartTime => "partTime",
                            EmployeeType.Temporary => "casual",
                            _ => null,
                        },
                        Age = ResolveMinorAge(item.Birthday, storeToday),
                        IsStoreManager = isStoreManager,
                        ManagesStore = isStoreManager && group.Any(row => row.IsPrimary),
                    };
                })
                .OrderBy(item => item.FullName ?? item.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return ApiResponse<List<AttendanceEmployeeDto>>.OK(employees);
        }

        /// <summary>返回给定员工中「账号有效且绑定该分店」的 GUID 集合（忽略大小写）。</summary>
        private async Task<HashSet<string>> LoadStoreBoundUserGuidsAsync(
            string storeCode,
            IEnumerable<string> userGuids)
        {
            var guids = userGuids
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (guids.Count == 0)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var bound = await _db.Queryable<User>()
                .InnerJoin<UserStore>((u, us) => u.UserGUID == us.UserGUID)
                .InnerJoin<Store>((u, us, s) => us.StoreGUID == s.StoreGUID)
                .Where((u, us, s) =>
                    !u.IsDeleted
                    && !us.IsDeleted
                    && !s.IsDeleted
                    && s.StoreCode == storeCode
                    && guids.Contains(u.UserGUID))
                .Select((u, us, s) => u.UserGUID)
                .ToListAsync();
            return bound.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>未满 18 岁返回周岁，否则（含生日缺失、未来日期）返回 null。</summary>
        internal static int? ResolveMinorAge(DateTime? birthday, DateTime today)
        {
            if (birthday == null || birthday.Value.Date > today.Date)
            {
                return null;
            }

            var age = today.Year - birthday.Value.Year;
            if (birthday.Value.Date > today.AddYears(-age).Date)
            {
                age--;
            }
            return age < 18 ? age : null;
        }

        private async Task<Dictionary<string, string>> ResolveUserDisplayNamesAsync(IEnumerable<string?> userGuids)
        {
            var guids = userGuids
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (guids.Count == 0)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var users = await _db.Queryable<User>()
                .Where(item => guids.Contains(item.UserGUID))
                .Select(item => new { item.UserGUID, item.FullName, item.Username })
                .ToListAsync();
            return users.ToDictionary(
                item => item.UserGUID,
                item => string.IsNullOrWhiteSpace(item.FullName) ? item.Username : item.FullName!,
                StringComparer.OrdinalIgnoreCase);
        }

        public Task<ApiResponse<AttendancePunchAdjustmentDto>> CreateMyPunchAdjustmentAsync(
            CreateAttendancePunchAdjustmentDto request) =>
            CreatePunchAdjustmentAsync(request, managedTargetUserGuid: null);

        public async Task<ApiResponse<AttendancePunchAdjustmentDto>> CreateManagedPunchAdjustmentAsync(
            CreateManagedAttendancePunchAdjustmentDto request)
        {
            if (string.IsNullOrWhiteSpace(request.UserGuid))
            {
                return ApiResponse<AttendancePunchAdjustmentDto>.Error("员工不能为空", "USER_REQUIRED");
            }

            return await CreatePunchAdjustmentAsync(request, request.UserGuid.Trim());
        }

        /// <summary>
        /// 补卡写入的公共实现。managedTargetUserGuid 为空表示员工本人补卡；
        /// 非空表示持「补卡与修改管理分店打卡」权限的店长代员工补录/修改，直接生效。
        /// </summary>
        private async Task<ApiResponse<AttendancePunchAdjustmentDto>> CreatePunchAdjustmentAsync(
            CreateAttendancePunchAdjustmentDto request,
            string? managedTargetUserGuid)
        {
            var context = await BuildPunchAdjustmentContextAsync(request, managedTargetUserGuid);
            if (!context.Success)
            {
                return ApiResponse<AttendancePunchAdjustmentDto>.Error(
                    context.Message,
                    context.ErrorCode);
            }
            if (string.IsNullOrWhiteSpace(request.PreviewRevision))
            {
                return ApiResponse<AttendancePunchAdjustmentDto>.Error(
                    "补卡预览已失效，请刷新后重新提交",
                    "ADJUSTMENT_PREVIEW_STALE");
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var requesterUserGuid = ResolveCurrentUserGuid();
            // 卡序属于被补卡员工，锁与落库都以员工为准；申请人/审核人记录操作者，作为审计依据。
            var userGuid = managedTargetUserGuid ?? requesterUserGuid;
            var mutationResource = AttendanceDailyMutationLock.BuildResource(
                userGuid,
                context.Schedule!.StoreCode,
                context.Schedule.WorkDate);
            await using var processLock = await AttendanceDailyMutationLock.AcquireProcessAsync(
                mutationResource);
            AttendancePunchAdjustment? adjustment = null;

            // SQL Server 的 Serializable 范围锁把 revision 输入快照保持到写入提交；SQLite 也支持该隔离级别。
            await _db.Ado.BeginTranAsync(IsolationLevel.Serializable);
            try
            {
                await AttendanceDailyMutationLock.AcquireDatabaseAsync(_db, mutationResource);
                // 补卡无论待审还是店长直生效，都会改变同一员工的有效卡序，必须锁内重算。
                var lockedContext = await BuildPunchAdjustmentContextAsync(request, managedTargetUserGuid);
                if (!lockedContext.Success)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendancePunchAdjustmentDto>.Error(
                        lockedContext.Message,
                        lockedContext.ErrorCode);
                }
                if (!string.Equals(
                        request.PreviewRevision,
                        lockedContext.Preview!.PreviewRevision,
                        StringComparison.Ordinal))
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendancePunchAdjustmentDto>.Error(
                        "补卡预览已失效，请刷新后重新提交",
                        "ADJUSTMENT_PREVIEW_STALE");
                }
                context = lockedContext;
                adjustment = new AttendancePunchAdjustment
                {
                    AdjustmentGuid = Guid.NewGuid().ToString(),
                    StoreCode = request.StoreCode.Trim(),
                    UserGuid = userGuid,
                    ScheduleGuid = context.Schedule!.ScheduleGuid,
                    OriginalPunchGuid = string.IsNullOrWhiteSpace(request.OriginalPunchGuid)
                        ? null
                        : request.OriginalPunchGuid.Trim(),
                    PunchType = NormalizePunchType(request.PunchType),
                    RequestedPunchTimeLocal = context.ProposedPunch!.PunchTimeLocal,
                    RequestedPunchTimeUtc = context.ProposedPunch!.PunchTimeUtc,
                    Reason = request.Reason.Trim(),
                    Status = context.Preview!.WouldAutoApprove ? "Applied" : "Pending",
                    // 仅本人直生效才算「店长自补」；代员工修改通过 RequestedBy ≠ UserGuid 区分。
                    IsManagerSelfDirect = context.Preview.WouldAutoApprove
                        && string.Equals(userGuid, requesterUserGuid, StringComparison.OrdinalIgnoreCase),
                    RequestedByUserGuid = requesterUserGuid,
                    ReviewedByUserGuid = context.Preview.WouldAutoApprove ? requesterUserGuid : null,
                    ReviewedAt = context.Preview.WouldAutoApprove ? now : null,
                    CreatedAt = now,
                    CreatedBy = _currentUserService.GetCurrentUsername(),
                };
                await _db.Insertable(adjustment).ExecuteCommandAsync();
                if (context.Preview!.WouldAutoApprove)
                {
                    var appliedPunch = await ApplyPunchAdjustmentAsync(adjustment, context.ProposedPunch!);
                    adjustment.AppliedPunchGuid = appliedPunch.PunchGuid;
                    await _db.Updateable(adjustment).ExecuteCommandAsync();
                    await ReconcileDerivedApprovalsForScheduleAsync(
                        context.Schedule!.ScheduleGuid,
                        Math.Max(0, context.Preview.CandidateOvertimeMinutesDelta));
                }
                else
                {
                    await CreatePendingApprovalAsync(
                        "PunchAdjustment",
                        adjustment.AdjustmentGuid,
                        adjustment.StoreCode,
                        userGuid);
                }
                await _db.Ado.CommitTranAsync();
            }
            catch (Exception exception) when (
                AttendancePunchPersistenceException.IsUniqueConstraintViolation(exception))
            {
                await _db.Ado.RollbackTranAsync();
                return ApiResponse<AttendancePunchAdjustmentDto>.Error(
                    "同一原打卡记录已有待审或已生效补卡",
                    "ADJUSTMENT_ALREADY_EXISTS");
            }
            catch
            {
                await _db.Ado.RollbackTranAsync();
                throw;
            }

            return ApiResponse<AttendancePunchAdjustmentDto>.OK(
                ToDto(adjustment!),
                adjustment!.Status == "Applied" ? "补卡已直接生效" : "补卡申请已提交");
        }

        public async Task<ApiResponse<AttendanceQrResolveDto>> ResolveAttendanceQrAsync(
            AttendanceQrResolveRequestDto request)
        {
            var userGuid = ResolveCurrentUserGuid();
            if (string.IsNullOrWhiteSpace(request.QrToken))
            {
                return ApiResponse<AttendanceQrResolveDto>.Error("请扫描 POS 考勤二维码", "QR_REQUIRED");
            }

            if (!AttendanceQrTokenCodec.TryGetKeyId(request.QrToken, out var kid, out var tokenError))
            {
                return ApiResponse<AttendanceQrResolveDto>.Error("考勤二维码格式无效", tokenError);
            }

            var qrKeyRecord = await _db.Queryable<AttendancePosQrKey>()
                .FirstAsync(item => item.Kid == kid);
            if (qrKeyRecord == null)
            {
                return ApiResponse<AttendanceQrResolveDto>.Error(
                    "考勤二维码密钥未知", "ATTENDANCE_QR_KEY_UNKNOWN");
            }
            if (!string.Equals(qrKeyRecord.Status, "Active", StringComparison.Ordinal))
            {
                return ApiResponse<AttendanceQrResolveDto>.Error(
                    "考勤二维码密钥已撤销", "ATTENDANCE_QR_KEY_REVOKED");
            }
            if (!string.Equals(qrKeyRecord.Algorithm, "A256GCM", StringComparison.Ordinal))
            {
                return ApiResponse<AttendanceQrResolveDto>.Error(
                    "考勤二维码密钥算法无效", "ATTENDANCE_QR_KEY_INVALID");
            }

            byte[] qrKey;
            try
            {
                qrKey = _attendanceQrKeyProtector.Unprotect(qrKeyRecord.ProtectedKey);
            }
            catch (Exception exception) when (exception is CryptographicException or FormatException)
            {
                return ApiResponse<AttendanceQrResolveDto>.Error(
                    "考勤二维码密钥无法安全读取", "ATTENDANCE_QR_KEY_DECRYPT_FAILED");
            }

            AttendanceQrTokenPayload? payload;
            try
            {
                if (!AttendanceQrTokenCodec.TryDecryptIdentity(
                        request.QrToken, qrKey, out payload, out _, out tokenError))
                {
                    return ApiResponse<AttendanceQrResolveDto>.Error("考勤二维码验证失败", tokenError);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(qrKey);
            }

            // 关键逻辑：resolve 只返回服务端重验后的设备身份；Punch 仍会对原始令牌完整重验。
            if (!string.Equals(payload!.StoreCode, qrKeyRecord.StoreCode, StringComparison.Ordinal)
                || !string.Equals(payload.DeviceCode, qrKeyRecord.DeviceCode, StringComparison.Ordinal))
            {
                return ApiResponse<AttendanceQrResolveDto>.Error(
                    "考勤二维码与登记设备不匹配", "ATTENDANCE_QR_DEVICE_MISMATCH");
            }

            if (_attendancePosDeviceStatusProvider == null
                || !await _attendancePosDeviceStatusProvider.IsActiveAsync(
                    qrKeyRecord.DeviceCode,
                    qrKeyRecord.StoreCode,
                    qrKeyRecord.HardwareId))
            {
                return ApiResponse<AttendanceQrResolveDto>.Error("POS 设备已停用", "POS_DEVICE_DISABLED");
            }

            var access = await ResolveRelatedStoreAccessAsync(userGuid, payload.StoreCode);
            if (!access.Success)
            {
                return ApiResponse<AttendanceQrResolveDto>.Error(access.Message, access.ErrorCode);
            }

            var serverNow = _timeProvider.GetUtcNow().UtcDateTime;
            if (!AttendanceQrTokenCodec.TryValidateLifetime(payload, serverNow, out tokenError))
            {
                return ApiResponse<AttendanceQrResolveDto>.Error("考勤二维码验证失败", tokenError);
            }

            var store = await _db.Queryable<Store>()
                .FirstAsync(item => item.StoreCode == payload.StoreCode);
            var punchAuthorization = _punchAuthorizationProtector.Issue(
                userGuid,
                request.QrToken,
                kid,
                payload,
                serverNow);
            return ApiResponse<AttendanceQrResolveDto>.OK(new AttendanceQrResolveDto
            {
                StoreCode = payload.StoreCode,
                DeviceCode = payload.DeviceCode,
                StoreName = store?.StoreName,
                ExpiresAtUtc = payload.ExpiresAtUtc,
                PunchAuthorizationToken = punchAuthorization.Token,
                PunchAuthorizationExpiresAtUtc = punchAuthorization.ExpiresAtUtc,
            });
        }

        public async Task<ApiResponse<AttendanceLocationSampleDto>> CreateLocationSampleAsync(
            AttendanceLocationSampleRequestDto request
        )
        {
            var userGuid = ResolveCurrentUserGuid();
            var storeCode = request.StoreCode?.Trim();
            if (string.IsNullOrWhiteSpace(storeCode))
            {
                return ApiResponse<AttendanceLocationSampleDto>.Error("请选择分店后再上传定位", "STORE_REQUIRED");
            }

            var access = await ResolveRelatedStoreAccessAsync(userGuid, storeCode);
            if (!access.Success)
            {
                return ApiResponse<AttendanceLocationSampleDto>.Error(access.Message, access.ErrorCode);
            }

            var locationValidation = RequiredLocationValidator.Validate(
                request.LocationLatitude,
                request.LocationLongitude,
                request.LocationPermissionStatus,
                request.LocationCapturedAtUtc,
                "班中定位需要位置信息",
                TimeSpan.FromMinutes(30)
            );
            if (!locationValidation.Success)
            {
                return ApiResponse<AttendanceLocationSampleDto>.Error(
                    locationValidation.Message,
                    locationValidation.ErrorCode
                );
            }

            var capturedAt = NormalizeUtc(request.LocationCapturedAtUtc)!.Value;
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (!await HasActiveLocationSampleShiftAsync(userGuid, storeCode, capturedAt))
            {
                return ApiResponse<AttendanceLocationSampleDto>.Error(
                    "当前没有有效的班中打卡，无法上传定位",
                    "ACTIVE_SHIFT_REQUIRED"
                );
            }

            var locationLatitude = request.LocationLatitude!.Value;
            var locationLongitude = request.LocationLongitude!.Value;
            var sample = new AttendanceLocationSample
            {
                SampleGuid = Guid.NewGuid().ToString(),
                UserGuid = userGuid,
                StoreCode = storeCode,
                HardwareId = NormalizeOptional(request.HardwareId, 100),
                SystemDeviceNumber = NormalizeOptional(request.SystemDeviceNumber, 100),
                DeviceSystem = NormalizeOptional(request.DeviceSystem, 30),
                EventType = NormalizeOptional(request.EventType, 30) ?? "ShiftInterval",
                LocationLatitude = locationLatitude,
                LocationLongitude = locationLongitude,
                LocationAccuracyMeters = request.LocationAccuracy,
                LocationPermissionStatus = NormalizeOptional(request.LocationPermissionStatus, 30),
                LocationCapturedAtUtc = capturedAt,
                CreatedAt = now,
                CreatedBy = _currentUserService.GetCurrentUsername(),
            };

            // 关键位置：班中定位样本按追加写入，不反向修改打卡记录，避免轨迹和打卡状态耦合。
            await _db.Insertable(sample).ExecuteCommandAsync();
            return ApiResponse<AttendanceLocationSampleDto>.OK(ToDto(sample), "定位已保存");
        }

        private async Task<bool> HasActiveLocationSampleShiftAsync(
            string userGuid,
            string storeCode,
            DateTime capturedAt
        )
        {
            var storeTimeZone = await ResolveStoreTimeZoneAsync(storeCode, null);
            var capturedAtUtc = DateTime.SpecifyKind(capturedAt, DateTimeKind.Utc);
            var localDayStartUtc = ConvertStoreLocalToUtc(
                ConvertUtcToStoreLocal(capturedAtUtc, storeTimeZone).Date,
                storeTimeZone
            );
            var maximumShiftStartUtc = capturedAtUtc.Subtract(TimeSpan.FromHours(10));
            var validWindowStartUtc = localDayStartUtc > maximumShiftStartUtc
                ? localDayStartUtc
                : maximumShiftStartUtc;

            var candidatePunches = await _db.Queryable<AttendancePunch>()
                .Where(item =>
                    !item.IsDeleted
                    && item.UserGuid == userGuid
                    && item.StoreCode == storeCode
                    && item.PunchTimeUtc > validWindowStartUtc
                    && item.PunchTimeUtc <= capturedAtUtc
                )
                .ToListAsync();
            if (candidatePunches.Count == 0)
            {
                return false;
            }
            candidatePunches = candidatePunches
                .OrderByDescending(item => item.PunchTimeUtc)
                .ThenByDescending(item => item.Id)
                .ToList();

            var candidatePunchGuids = candidatePunches
                .Select(item => item.PunchGuid)
                .ToList();
            var supersededPunchGuids = await _db.Queryable<AttendancePunch>()
                .Where(item =>
                    !item.IsDeleted
                    && item.SupersedesPunchGuid != null
                    && candidatePunchGuids.Contains(item.SupersedesPunchGuid)
                )
                .Select(item => item.SupersedesPunchGuid!)
                .ToListAsync();
            var supersededPunchGuidSet = supersededPunchGuids.ToHashSet(
                StringComparer.Ordinal
            );
            var latestEffectivePunch = candidatePunches.FirstOrDefault(item =>
                !supersededPunchGuidSet.Contains(item.PunchGuid)
            );

            // 只允许门店本地当日、10 小时以内的最后一条有效上班卡，且样本必须在该卡之后采集。
            return string.Equals(
                latestEffectivePunch?.PunchType,
                "ClockIn",
                StringComparison.OrdinalIgnoreCase
            )
                && latestEffectivePunch!.PunchTimeUtc
                    <= capturedAtUtc;
        }

        public async Task<ApiResponse<List<AttendanceLeaveRequestDto>>> GetMyLeaveRequestsAsync()
        {
            var userGuid = ResolveCurrentUserGuid();
            var rows = await _db.Queryable<AttendanceLeaveRequest>()
                .Where(item => !item.IsDeleted && item.UserGuid == userGuid)
                .OrderByDescending(item => item.CreatedAt)
                .ToListAsync();
            var reviewerNames = await ResolveUserDisplayNamesAsync(rows.Select(item => item.ReviewedBy));
            return ApiResponse<List<AttendanceLeaveRequestDto>>.OK(rows.Select(item =>
            {
                var dto = ToDto(item);
                dto.ReviewedByName = item.ReviewedBy != null
                    ? reviewerNames.GetValueOrDefault(item.ReviewedBy)
                    : null;
                return dto;
            }).ToList());
        }

        public async Task<ApiResponse<AttendanceLeaveRequestDto>> CreateMyLeaveRequestAsync(
            CreateAttendanceLeaveRequestDto request
        )
        {
            var userGuid = ResolveCurrentUserGuid();
            var access = await ResolveRelatedStoreAccessAsync(userGuid, request.StoreCode);
            if (!access.Success)
            {
                return ApiResponse<AttendanceLeaveRequestDto>.Error(access.Message, access.ErrorCode);
            }

            var now = DateTime.UtcNow;
            var model = new AttendanceLeaveRequest
            {
                LeaveGuid = Guid.NewGuid().ToString(),
                StoreCode = request.StoreCode.Trim(),
                UserGuid = userGuid,
                LeaveType = request.LeaveType.Trim(),
                StartDate = request.StartDate.Date,
                EndDate = request.EndDate.Date,
                StartTime = request.StartTime,
                EndTime = request.EndTime,
                Reason = request.Reason,
                AttachmentUrl = request.AttachmentUrl,
                Status = "Pending",
                CreatedAt = now,
                CreatedBy = _currentUserService.GetCurrentUsername(),
            };

            await _db.Insertable(model).ExecuteCommandAsync();
            await CreatePendingApprovalAsync("Leave", model.LeaveGuid, model.StoreCode, userGuid);
            return ApiResponse<AttendanceLeaveRequestDto>.OK(ToDto(model), "请假申请已提交");
        }

        public async Task<ApiResponse<AttendanceLeaveRequestDto>> CreateManagedLeaveRequestAsync(
            CreateManagedAttendanceLeaveRequestDto request
        )
        {
            var validation = ValidateManagedLeaveRequest(request);
            if (!validation.Success)
            {
                return ApiResponse<AttendanceLeaveRequestDto>.Error(validation.Message, validation.ErrorCode);
            }

            var storeCode = request.StoreCode.Trim();
            var userGuid = request.UserGuid.Trim();
            var storeAccess = await ResolveManagedStoreAccessAsync(storeCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<AttendanceLeaveRequestDto>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            var isEmployeeInStore = await _db.Queryable<UserStore>()
                .InnerJoin<Store>((us, s) => us.StoreGUID == s.StoreGUID)
                .Where((us, s) =>
                    us.UserGUID == userGuid
                    && !us.IsDeleted
                    && !s.IsDeleted
                    && s.StoreCode == storeCode
                )
                .AnyAsync();
            if (!isEmployeeInStore)
            {
                return ApiResponse<AttendanceLeaveRequestDto>.Error("员工不属于该分店", "FORBIDDEN_STORE");
            }

            var employeeType = await _db.Queryable<EmployeeProfile>()
                .Where(item => !item.IsDeleted && item.UserGUID == userGuid)
                .Select(item => item.EmployeeType)
                .FirstAsync();
            if (employeeType is not EmployeeType.FullTime and not EmployeeType.PartTime)
            {
                return ApiResponse<AttendanceLeaveRequestDto>.Error(
                    "仅全职或兼职员工可由管理端创建请假",
                    "INVALID_EMPLOYMENT_TYPE"
                );
            }

            var now = DateTime.UtcNow;
            var model = new AttendanceLeaveRequest
            {
                LeaveGuid = Guid.NewGuid().ToString(),
                StoreCode = storeCode,
                UserGuid = userGuid,
                LeaveType = NormalizeManagedLeaveType(request.LeaveType),
                StartDate = request.StartDate.Date,
                EndDate = request.EndDate.Date,
                StartTime = request.StartTime,
                EndTime = request.EndTime,
                Reason = request.Reason,
                AttachmentUrl = request.AttachmentUrl,
                Status = "Pending",
                CreatedAt = now,
                CreatedBy = _currentUserService.GetCurrentUsername(),
            };

            await _db.Insertable(model).ExecuteCommandAsync();
            await CreatePendingApprovalAsync("Leave", model.LeaveGuid, model.StoreCode, userGuid);
            return ApiResponse<AttendanceLeaveRequestDto>.OK(ToDto(model), "请假申请已提交");
        }

        public async Task<ApiResponse<bool>> CancelMyLeaveRequestAsync(string leaveGuid)
        {
            var userGuid = ResolveCurrentUserGuid();
            var model = await _db.Queryable<AttendanceLeaveRequest>()
                .FirstAsync(item => item.LeaveGuid == leaveGuid && item.UserGuid == userGuid && !item.IsDeleted);
            if (model == null)
            {
                return ApiResponse<bool>.Error("请假申请不存在", "NOT_FOUND");
            }

            model.Status = "Cancelled";
            model.IsDeleted = true;
            model.UpdatedAt = DateTime.UtcNow;
            model.UpdatedBy = _currentUserService.GetCurrentUsername();
            await _db.Updateable(model).ExecuteCommandAsync();
            await _db.Updateable<AttendanceApproval>()
                .SetColumns(item => item.ReviewStatus == "Cancelled")
                .Where(item => item.SourceType == "Leave" && item.SourceGuid == leaveGuid && item.ReviewStatus == "Pending")
                .ExecuteCommandAsync();
            return ApiResponse<bool>.OK(true, "请假申请已取消");
        }

        public Task<ApiResponse<DirectUploadSignature>> GetLeaveAttachmentUploadSignatureAsync(
            DirectUploadRequest request
        )
        {
            if (request == null || string.IsNullOrWhiteSpace(request.FileName))
            {
                return Task.FromResult(
                    ApiResponse<DirectUploadSignature>.Error("文件名不能为空", "INVALID_REQUEST")
                );
            }

            var objectKey = request.ObjectKey;
            if (string.IsNullOrWhiteSpace(objectKey))
            {
                var safeFileName = SanitizeFileName(request.FileName);
                objectKey = $"attendance/leave-attachments/{DateTime.Now:yyyyMMddHHmmss}-{safeFileName}";
            }

            var signature = _uploadService.GetDirectUploadSignature(objectKey, request.ContentType);

            return Task.FromResult(ApiResponse<DirectUploadSignature>.OK(signature, "签名生成成功"));
        }

        public async Task<ApiResponse<List<AttendanceAvailabilityDto>>> GetAvailabilityAsync(
            AttendanceAvailabilityQueryDto query
        )
        {
            var storeAccess = await ResolveManagedStoreAccessAsync(query.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<List<AttendanceAvailabilityDto>>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            var rows = await _db.Queryable<AttendanceAvailability>()
                .Where(item => !item.IsDeleted)
                .WhereIF(!string.IsNullOrWhiteSpace(query.StoreCode), item => item.StoreCode == query.StoreCode!.Trim())
                .WhereIF(string.IsNullOrWhiteSpace(query.StoreCode) && storeAccess.StoreCodes.Count > 0, item => storeAccess.StoreCodes.Contains(item.StoreCode))
                .WhereIF(!string.IsNullOrWhiteSpace(query.UserGuid), item => item.UserGuid == query.UserGuid!.Trim())
                .WhereIF(query.WeekStartDate.HasValue, item => item.WeekStartDate >= query.WeekStartDate!.Value.Date && item.WeekStartDate < query.WeekStartDate!.Value.Date.AddDays(1))
                .OrderBy(item => item.AvailableDate)
                .OrderBy(item => item.StartTime)
                .ToListAsync();
            return ApiResponse<List<AttendanceAvailabilityDto>>.OK(rows.Select(ToDto).ToList());
        }

        public async Task<ApiResponse<List<AttendancePunchDto>>> GetPunchesAsync(
            AttendancePunchQueryDto query
        )
        {
            var storeAccess = await ResolveManagedStoreAccessAsync(query.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<List<AttendancePunchDto>>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            var rows = await _db.Queryable<AttendancePunch>()
                .Where(item => !item.IsDeleted)
                .WhereIF(!string.IsNullOrWhiteSpace(query.StoreCode), item => item.StoreCode == query.StoreCode!.Trim())
                .WhereIF(string.IsNullOrWhiteSpace(query.StoreCode) && storeAccess.StoreCodes.Count > 0, item => storeAccess.StoreCodes.Contains(item.StoreCode))
                .WhereIF(!string.IsNullOrWhiteSpace(query.UserGuid), item => item.UserGuid == query.UserGuid!.Trim())
                .WhereIF(query.FromDate.HasValue, item => item.WorkDate >= query.FromDate!.Value.Date)
                .WhereIF(query.ToDate.HasValue, item => item.WorkDate <= query.ToDate!.Value.Date)
                .WhereIF(!string.IsNullOrWhiteSpace(query.Status), item => item.Status == query.Status!.Trim())
                .OrderByDescending(item => item.PunchTimeUtc)
                .ToListAsync();
            var result = rows.Select(item => ToDto(item)).ToList();
            var scheduleGuids = rows
                .Where(item => !string.IsNullOrWhiteSpace(item.ScheduleGuid))
                .Select(item => item.ScheduleGuid!)
                .Distinct()
                .ToList();
            if (scheduleGuids.Count > 0)
            {
                var schedules = await _db.Queryable<AttendanceSchedule>()
                    .Where(item => !item.IsDeleted && scheduleGuids.Contains(item.ScheduleGuid))
                    .ToListAsync();
                await PopulateWorkSessionFieldsAsync(
                    schedules.Select(item => ToDto(item)).ToList(),
                    schedules,
                    knownPunches: null,
                    punchDtos: result);
            }
            return ApiResponse<List<AttendancePunchDto>>.OK(result);
        }

        public async Task<ApiResponse<List<AttendanceLocationSampleDto>>> GetLocationSamplesAsync(
            AttendanceLocationSampleQueryDto query
        )
        {
            var storeAccess = await ResolveManagedStoreAccessAsync(query.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<List<AttendanceLocationSampleDto>>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            // 管理端定位查询默认只看门店本地当天，传入本地日期时转成 UTC 边界，避免澳洲早班样本被漏掉。
            var storeTimeZone = await ResolveStoreTimeZoneAsync(query.StoreCode, query.StoreTimeZone);
            var fromDate = query.FromDate?.Date ?? ConvertUtcToStoreLocal(DateTime.UtcNow, storeTimeZone).Date;
            var toDate = query.ToDate?.Date ?? fromDate;
            if (toDate < fromDate)
            {
                return ApiResponse<List<AttendanceLocationSampleDto>>.Error(
                    "结束日期不能早于开始日期",
                    "INVALID_DATE_RANGE"
                );
            }
            if ((toDate - fromDate).TotalDays > 31)
            {
                return ApiResponse<List<AttendanceLocationSampleDto>>.Error(
                    "定位样本查询范围不能超过31天",
                    "DATE_RANGE_TOO_LARGE"
                );
            }
            var fromUtc = ConvertStoreLocalToUtc(fromDate, storeTimeZone);
            var toExclusiveUtc = ConvertStoreLocalToUtc(toDate.AddDays(1), storeTimeZone);

            var rows = await _db.Queryable<AttendanceLocationSample>()
                .Where(item => !item.IsDeleted)
                .WhereIF(!string.IsNullOrWhiteSpace(query.StoreCode), item => item.StoreCode == query.StoreCode!.Trim())
                .WhereIF(string.IsNullOrWhiteSpace(query.StoreCode) && storeAccess.StoreCodes.Count > 0, item => item.StoreCode != null && storeAccess.StoreCodes.Contains(item.StoreCode))
                .WhereIF(!string.IsNullOrWhiteSpace(query.UserGuid), item => item.UserGuid == query.UserGuid!.Trim())
                .Where(item => item.LocationCapturedAtUtc >= fromUtc)
                .Where(item => item.LocationCapturedAtUtc < toExclusiveUtc)
                .OrderByDescending(item => item.LocationCapturedAtUtc)
                .Take(1000)
                .ToListAsync();
            return ApiResponse<List<AttendanceLocationSampleDto>>.OK(rows.Select(ToDto).ToList());
        }

        public async Task<ApiResponse<List<AttendanceApprovalDto>>> GetApprovalsAsync(
            AttendanceApprovalQueryDto query
        )
        {
            var storeAccess = await ResolveManagedStoreAccessAsync(query.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<List<AttendanceApprovalDto>>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            var rows = await _db.Queryable<AttendanceApproval>()
                .Where(item => !item.IsDeleted)
                .WhereIF(!string.IsNullOrWhiteSpace(query.StoreCode), item => item.StoreCode == query.StoreCode!.Trim())
                .WhereIF(string.IsNullOrWhiteSpace(query.StoreCode) && storeAccess.StoreCodes.Count > 0, item => storeAccess.StoreCodes.Contains(item.StoreCode))
                .WhereIF(!string.IsNullOrWhiteSpace(query.SourceType), item => item.SourceType == query.SourceType!.Trim())
                .WhereIF(!string.IsNullOrWhiteSpace(query.ReviewStatus), item => item.ReviewStatus == query.ReviewStatus!.Trim())
                .OrderByDescending(item => item.CreatedAt)
                .ToListAsync();
            var result = rows.Select(ToDto).ToList();
            await PopulateApprovalDisplayFieldsAsync(result);
            return ApiResponse<List<AttendanceApprovalDto>>.OK(result);
        }

        public Task<ApiResponse<List<AttendanceApprovalDto>>> GetPendingApprovalsAsync(
            AttendanceApprovalQueryDto query
        )
        {
            query.ReviewStatus = "Pending";
            return GetApprovalsAsync(query);
        }

        public Task<ApiResponse<AttendanceApprovalDto>> ApproveAsync(
            string approvalGuid,
            ReviewAttendanceApprovalDto request
        ) => ReviewApprovalAsync(
            approvalGuid,
            "Approved",
            request.ReviewRemark,
            request.ApprovedOvertimeMinutes);

        public Task<ApiResponse<AttendanceApprovalDto>> RejectAsync(
            string approvalGuid,
            ReviewAttendanceApprovalDto request
        ) => ReviewApprovalAsync(approvalGuid, "Rejected", request.ReviewRemark, null);

        public async Task<ApiResponse<List<AttendanceStoreHolidayDto>>> GetHolidaysAsync(
            AttendanceStoreHolidayQueryDto query
        )
        {
            var storeAccess = await ResolveManagedStoreAccessAsync(query.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<List<AttendanceStoreHolidayDto>>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            var rows = await _db.Queryable<AttendanceStoreHoliday>()
                .Where(item => !item.IsDeleted)
                .WhereIF(!string.IsNullOrWhiteSpace(query.StoreCode), item => item.StoreCode == query.StoreCode!.Trim())
                .WhereIF(string.IsNullOrWhiteSpace(query.StoreCode) && storeAccess.StoreCodes.Count > 0, item => storeAccess.StoreCodes.Contains(item.StoreCode))
                .WhereIF(query.FromDate.HasValue, item => item.HolidayDate >= query.FromDate!.Value.Date)
                .WhereIF(query.ToDate.HasValue, item => item.HolidayDate <= query.ToDate!.Value.Date)
                .OrderBy(item => item.HolidayDate)
                .ToListAsync();
            return ApiResponse<List<AttendanceStoreHolidayDto>>.OK(rows.Select(ToDto).ToList());
        }

        public async Task<ApiResponse<AttendanceStoreHolidayDto>> CreateHolidayAsync(
            CreateAttendanceStoreHolidayDto request
        )
        {
            var result = await BatchUpsertHolidaysAsync(new BatchUpsertAttendanceStoreHolidayDto
            {
                StoreCodes = new List<string> { request.StoreCode },
                HolidayDate = request.HolidayDate,
                HolidayName = request.HolidayName,
                BusinessStatus = request.BusinessStatus,
                OpenTime = request.OpenTime,
                CloseTime = request.CloseTime,
                IsPaidHoliday = request.IsPaidHoliday,
                Remark = request.Remark,
            });

            if (!result.Success)
            {
                return ApiResponse<AttendanceStoreHolidayDto>.Error(result.Message, result.ErrorCode, result.Details);
            }

            var item = result.Data?.Items.FirstOrDefault();
            return item == null
                ? ApiResponse<AttendanceStoreHolidayDto>.Error("公共假期保存失败", "HOLIDAY_SAVE_FAILED")
                : ApiResponse<AttendanceStoreHolidayDto>.OK(item, "公共假期已创建");
        }

        public async Task<ApiResponse<BatchUpsertAttendanceStoreHolidayResultDto>> BatchUpsertHolidaysAsync(
            BatchUpsertAttendanceStoreHolidayDto request
        )
        {
            var storeCodes = NormalizeStoreCodes(request.StoreCodes);
            var validation = ValidateHolidayBatchPayload(storeCodes, request.HolidayDate, request.HolidayName);
            if (!validation.Success)
            {
                return ApiResponse<BatchUpsertAttendanceStoreHolidayResultDto>.Error(validation.Message, validation.ErrorCode);
            }

            foreach (var storeCode in storeCodes)
            {
                var storeAccess = await ResolveManagedStoreAccessAsync(storeCode);
                if (!storeAccess.Success)
                {
                    return ApiResponse<BatchUpsertAttendanceStoreHolidayResultDto>.Error(storeAccess.Message, storeAccess.ErrorCode);
                }
            }

            var result = new BatchUpsertAttendanceStoreHolidayResultDto();
            var now = DateTime.UtcNow;
            var username = _currentUserService.GetCurrentUsername();
            var holidayDate = request.HolidayDate.Date;
            var holidayName = request.HolidayName.Trim();
            var businessStatus = NormalizeBusinessStatus(request.BusinessStatus);

            await _db.Ado.BeginTranAsync();
            try
            {
                foreach (var storeCode in storeCodes)
                {
                    var existingRows = await _db.Queryable<AttendanceStoreHoliday>()
                        .Where(item => !item.IsDeleted)
                        .Where(item =>
                            item.StoreCode == storeCode
                            && item.HolidayDate >= holidayDate
                            && item.HolidayDate < holidayDate.AddDays(1)
                        )
                        .OrderBy(item => item.CreatedAt)
                        .OrderBy(item => item.Id)
                        .ToListAsync();

                    AttendanceStoreHoliday model;
                    if (existingRows.Count > 0)
                    {
                        model = existingRows[0];
                        ApplyHolidayValues(
                            model,
                            holidayName,
                            businessStatus,
                            request.OpenTime,
                            request.CloseTime,
                            request.IsPaidHoliday,
                            request.Remark,
                            now,
                            username
                        );
                        await _db.Updateable(model).ExecuteCommandAsync();
                        result.UpdatedCount++;

                        foreach (var duplicate in existingRows.Skip(1))
                        {
                            duplicate.IsDeleted = true;
                            duplicate.UpdatedAt = now;
                            duplicate.UpdatedBy = username;
                            await _db.Updateable(duplicate).ExecuteCommandAsync();
                        }
                    }
                    else
                    {
                        model = new AttendanceStoreHoliday
                        {
                            HolidayGuid = Guid.NewGuid().ToString(),
                            StoreCode = storeCode,
                            HolidayDate = holidayDate,
                            HolidayName = holidayName,
                            BusinessStatus = businessStatus,
                            OpenTime = request.OpenTime,
                            CloseTime = request.CloseTime,
                            IsPaidHoliday = request.IsPaidHoliday,
                            Remark = request.Remark,
                            CreatedAt = now,
                            CreatedBy = username,
                            UpdatedAt = now,
                            UpdatedBy = username,
                        };
                        await _db.Insertable(model).ExecuteCommandAsync();
                        result.CreatedCount++;
                    }

                    result.Items.Add(ToDto(model));
                }

                await _db.Ado.CommitTranAsync();
            }
            catch (Exception ex)
            {
                await _db.Ado.RollbackTranAsync();
                _logger.LogError(ex, "批量保存公共假期失败");
                return ApiResponse<BatchUpsertAttendanceStoreHolidayResultDto>.Error(
                    "批量保存公共假期失败",
                    "HOLIDAY_BATCH_SAVE_FAILED"
                );
            }

            return ApiResponse<BatchUpsertAttendanceStoreHolidayResultDto>.OK(result, "公共假期已批量保存");
        }

        public async Task<ApiResponse<AttendanceStoreHolidayDto>> UpdateHolidayAsync(
            string holidayGuid,
            UpdateAttendanceStoreHolidayDto request
        )
        {
            var model = await _db.Queryable<AttendanceStoreHoliday>()
                .FirstAsync(item => item.HolidayGuid == holidayGuid && !item.IsDeleted);
            if (model == null)
            {
                return ApiResponse<AttendanceStoreHolidayDto>.Error("公共假期不存在", "NOT_FOUND");
            }

            var storeAccess = await ResolveManagedStoreAccessAsync(model.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<AttendanceStoreHolidayDto>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            model.HolidayDate = request.HolidayDate.Date;
            model.HolidayName = request.HolidayName.Trim();
            model.BusinessStatus = NormalizeBusinessStatus(request.BusinessStatus);
            model.OpenTime = request.OpenTime;
            model.CloseTime = request.CloseTime;
            model.IsPaidHoliday = request.IsPaidHoliday;
            model.Remark = request.Remark;
            model.UpdatedAt = DateTime.UtcNow;
            model.UpdatedBy = _currentUserService.GetCurrentUsername();
            await _db.Updateable(model).ExecuteCommandAsync();
            return ApiResponse<AttendanceStoreHolidayDto>.OK(ToDto(model), "公共假期已更新");
        }

        public async Task<ApiResponse<bool>> DeleteHolidayAsync(string holidayGuid)
        {
            var model = await _db.Queryable<AttendanceStoreHoliday>()
                .FirstAsync(item => item.HolidayGuid == holidayGuid && !item.IsDeleted);
            if (model == null)
            {
                return ApiResponse<bool>.Error("公共假期不存在", "NOT_FOUND");
            }

            var storeAccess = await ResolveManagedStoreAccessAsync(model.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<bool>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            model.IsDeleted = true;
            model.UpdatedAt = DateTime.UtcNow;
            model.UpdatedBy = _currentUserService.GetCurrentUsername();
            await _db.Updateable(model).ExecuteCommandAsync();
            return ApiResponse<bool>.OK(true, "公共假期已删除");
        }

        public async Task<ApiResponse<AttendanceSettingsDto>> GetSettingsAsync()
        {
            var settings = await GetOrCreateSettingsModelAsync();
            return ApiResponse<AttendanceSettingsDto>.OK(ToDto(settings));
        }

        public async Task<ApiResponse<AttendanceSettingsDto>> UpdateSettingsAsync(
            UpdateAttendanceSettingsDto request
        )
        {
            if (!IsAdmin())
            {
                return ApiResponse<AttendanceSettingsDto>.Error("只有 Admin 可以修改考勤设置", "ADMIN_REQUIRED");
            }

            var settings = await GetOrCreateSettingsModelAsync();
            settings.LateGraceMinutes = Math.Max(0, request.LateGraceMinutes);
            settings.EarlyLeaveGraceMinutes = Math.Max(0, request.EarlyLeaveGraceMinutes);
            settings.AllowNoSchedulePunch = request.AllowNoSchedulePunch;
            settings.RequireApprovalForLate = request.RequireApprovalForLate;
            settings.RequireApprovalForEarlyLeave = request.RequireApprovalForEarlyLeave;
            settings.RequireApprovalForNoSchedule = request.RequireApprovalForNoSchedule;
            settings.RequireApprovalForDuplicate = request.RequireApprovalForDuplicate;
            settings.UpdatedAt = DateTime.UtcNow;
            settings.UpdatedBy = _currentUserService.GetCurrentUsername();
            await _db.Updateable(settings).ExecuteCommandAsync();
            return ApiResponse<AttendanceSettingsDto>.OK(ToDto(settings), "考勤设置已更新");
        }

        private async Task<ApiResponse<AttendanceApprovalDto>> ReviewApprovalAsync(
            string approvalGuid,
            string reviewStatus,
            string? reviewRemark,
            int? approvedOvertimeMinutes
        )
        {
            var model = await _db.Queryable<AttendanceApproval>()
                .FirstAsync(item => item.ApprovalGuid == approvalGuid && !item.IsDeleted);
            if (model == null)
            {
                return ApiResponse<AttendanceApprovalDto>.Error("审核记录不存在", "NOT_FOUND");
            }

            var storeAccess = await ResolveManagedStoreAccessAsync(model.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<AttendanceApprovalDto>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            var reviewer = ResolveCurrentUserGuid();
            if (!model.ReviewStatus.Equals("Pending", StringComparison.OrdinalIgnoreCase))
            {
                return ApiResponse<AttendanceApprovalDto>.Error("审核记录已处理", "APPROVAL_ALREADY_REVIEWED");
            }
            if (model.ApplicantUserGuid.Equals(reviewer, StringComparison.OrdinalIgnoreCase))
            {
                return ApiResponse<AttendanceApprovalDto>.Error(
                    "申请人不能审核自己的考勤申请",
                    "SELF_REVIEW_FORBIDDEN");
            }

            AttendancePunchAdjustment? adjustment = null;
            if (model.SourceType == "PunchAdjustment")
            {
                adjustment = await _db.Queryable<AttendancePunchAdjustment>()
                    .FirstAsync(item => item.AdjustmentGuid == model.SourceGuid && !item.IsDeleted);
                if (adjustment == null)
                {
                    return ApiResponse<AttendanceApprovalDto>.Error(
                        "补卡申请不存在",
                        "ADJUSTMENT_NOT_FOUND");
                }
            }
            AttendanceSchedule? overtimeSchedule = null;
            var reviewedAt = _timeProvider.GetUtcNow().UtcDateTime;
            var updatedBy = _currentUserService.GetCurrentUsername();
            var mutationResource = model.SourceType is "Punch" or "MissingClockOut" or "Overtime" or "PunchAdjustment"
                ? AttendanceDailyMutationLock.BuildResource(
                    model.ApplicantUserGuid,
                    model.StoreCode,
                    default)
                : null;
            await using IAsyncDisposable? processLock = mutationResource == null
                ? null
                : await AttendanceDailyMutationLock.AcquireProcessAsync(mutationResource);
            // 审批 claim 前会重读设置、角色关系、打卡和排班；Serializable 保证该判断快照持续到提交。
            await _db.Ado.BeginTranAsync(IsolationLevel.Serializable);
            try
            {
                if (mutationResource != null)
                {
                    await AttendanceDailyMutationLock.AcquireDatabaseAsync(_db, mutationResource);
                }
                // 进员工日锁后重读审批和候选分钟，禁止用锁外旧快照批准已变化的加班。
                model = await _db.Queryable<AttendanceApproval>()
                    .FirstAsync(item => item.ApprovalGuid == approvalGuid && !item.IsDeleted);
                if (model == null
                    || !model.ReviewStatus.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendanceApprovalDto>.Error(
                        "审核记录已处理",
                        "APPROVAL_ALREADY_REVIEWED");
                }
                if (model.SourceType == "Punch")
                {
                    var sourcePunch = await _db.Queryable<AttendancePunch>().FirstAsync(item =>
                        !item.IsDeleted && item.PunchGuid == model.SourceGuid);
                    var sourceWasSuperseded = sourcePunch != null
                        && await _db.Queryable<AttendancePunch>().AnyAsync(item =>
                            !item.IsDeleted && item.SupersedesPunchGuid == sourcePunch.PunchGuid);
                    var settings = await GetOrCreateSettingsModelAsync();
                    var stillRequiresApproval = false;
                    if (sourcePunch != null && !sourceWasSuperseded
                        && !string.IsNullOrWhiteSpace(sourcePunch.ScheduleGuid))
                    {
                        var activeSchedule = await _db.Queryable<AttendanceSchedule>().FirstAsync(item =>
                            !item.IsDeleted
                            && item.Status == "Active"
                            && item.ScheduleGuid == sourcePunch.ScheduleGuid);
                        if (activeSchedule != null)
                        {
                            var currentSession = await BuildWorkSessionAsync(activeSchedule);
                            stillRequiresApproval = GetExpectedPunchApprovalCandidates(
                                    currentSession,
                                    settings)
                                .Any(item => item.PunchGuid.Equals(
                                    sourcePunch.PunchGuid,
                                    StringComparison.OrdinalIgnoreCase));
                        }
                    }
                    else if (sourcePunch != null && !sourceWasSuperseded)
                    {
                        var hasActiveSchedule = await _db.Queryable<AttendanceSchedule>().AnyAsync(item =>
                            !item.IsDeleted
                            && item.Status == "Active"
                            && item.StoreCode == sourcePunch.StoreCode
                            && item.UserGuid == sourcePunch.UserGuid
                            && item.WorkDate >= sourcePunch.WorkDate.Date
                            && item.WorkDate < sourcePunch.WorkDate.Date.AddDays(1));
                        stillRequiresApproval = !hasActiveSchedule
                            && RequiresApproval(sourcePunch.Status, settings);
                    }
                    if (!stillRequiresApproval)
                    {
                        await CancelPendingApprovalAsync(
                            model.ApprovalGuid,
                            "原打卡已失效或异常状态已变化，待审记录已自动取消");
                        await _db.Ado.CommitTranAsync();
                        return ApiResponse<AttendanceApprovalDto>.Error(
                            "原打卡异常状态已变化，不能批准",
                            "PUNCH_APPROVAL_STALE");
                    }
                }
                if (model.SourceType == "MissingClockOut")
                {
                    var missingClockOutSchedule = await _db.Queryable<AttendanceSchedule>().FirstAsync(item =>
                        !item.IsDeleted
                        && item.Status == "Active"
                        && item.ScheduleGuid == model.SourceGuid);
                    var currentSession = missingClockOutSchedule == null
                        ? null
                        : await BuildWorkSessionAsync(missingClockOutSchedule);
                    if (currentSession?.HasMissingClockOut != true)
                    {
                        await CancelPendingApprovalAsync(
                            model.ApprovalGuid,
                            "漏下班状态已消除，待审记录已自动取消");
                        await _db.Ado.CommitTranAsync();
                        return ApiResponse<AttendanceApprovalDto>.Error(
                            "漏下班状态已消除，不能批准",
                            "MISSING_CLOCK_OUT_RESOLVED");
                    }
                }
                if (model.SourceType == "PunchAdjustment")
                {
                    adjustment = await _db.Queryable<AttendancePunchAdjustment>()
                        .FirstAsync(item => item.AdjustmentGuid == model.SourceGuid && !item.IsDeleted);
                    if (adjustment == null || adjustment.Status != "Pending")
                    {
                        await CancelPendingApprovalAsync(
                            model.ApprovalGuid,
                            "补卡申请已失效，待审记录已自动取消");
                        await _db.Ado.CommitTranAsync();
                        return ApiResponse<AttendanceApprovalDto>.Error(
                            "补卡申请已失效，不能批准",
                            "ADJUSTMENT_NOT_FOUND");
                    }
                }
                if (model.SourceType == "Overtime")
                {
                    overtimeSchedule = await _db.Queryable<AttendanceSchedule>().FirstAsync(item =>
                        !item.IsDeleted
                        && item.Status == "Active"
                        && item.ScheduleGuid == model.SourceGuid);
                    if (overtimeSchedule == null)
                    {
                        await CancelPendingApprovalAsync(
                            model.ApprovalGuid,
                            "排班已失效，待审加班已自动取消");
                        await _db.Ado.CommitTranAsync();
                        return ApiResponse<AttendanceApprovalDto>.Error(
                            "加班排班已失效，不能批准",
                            "OVERTIME_APPROVAL_STALE");
                    }
                    var currentSession = await BuildWorkSessionAsync(overtimeSchedule);
                    var terminalRows = await _db.Queryable<AttendanceApproval>()
                        .Where(item =>
                            !item.IsDeleted
                            && item.SourceType == "Overtime"
                            && item.SourceGuid == model.SourceGuid
                            && (item.ReviewStatus == "Approved" || item.ReviewStatus == "Rejected"))
                        .ToListAsync();
                    var processedMinutes = terminalRows.Sum(item =>
                        item.CandidateOvertimeMinutes ?? 0);
                    var expectedCandidate = currentSession?.ScheduleState == "Completed"
                        ? Math.Max(0, currentSession.CandidateOvertimeMinutes - processedMinutes)
                        : 0;
                    if (model.CandidateOvertimeMinutes != expectedCandidate)
                    {
                        await _db.Ado.RollbackTranAsync();
                        return ApiResponse<AttendanceApprovalDto>.Error(
                            "加班候选分钟已变化，请刷新后重新审核",
                            "OVERTIME_CANDIDATE_CHANGED");
                    }

                    if (reviewStatus == "Approved")
                    {
                        var approved = approvedOvertimeMinutes ?? expectedCandidate;
                        if (approved < 0 || approved % 15 != 0 || approved > expectedCandidate)
                        {
                            await _db.Ado.RollbackTranAsync();
                            return ApiResponse<AttendanceApprovalDto>.Error(
                                "加班审批分钟数只能按15分钟倍数向下调整",
                                "INVALID_OVERTIME_MINUTES");
                        }
                        if (approved < expectedCandidate && string.IsNullOrWhiteSpace(reviewRemark))
                        {
                            await _db.Ado.RollbackTranAsync();
                            return ApiResponse<AttendanceApprovalDto>.Error(
                                "下调加班分钟数必须填写审核备注",
                                "REVIEW_REMARK_REQUIRED");
                        }
                        model.ApprovedOvertimeMinutes = approved;
                    }
                    if (reviewStatus == "Rejected" && string.IsNullOrWhiteSpace(reviewRemark))
                    {
                        await _db.Ado.RollbackTranAsync();
                        return ApiResponse<AttendanceApprovalDto>.Error(
                            "拒绝加班必须填写审核备注",
                            "REVIEW_REMARK_REQUIRED");
                    }
                }
                // 条件更新是审批的原子领取点；并发请求只有一个能把 Pending 改为终态。
                var claimed = await _db.Updateable<AttendanceApproval>()
                    .SetColumns(item => item.ReviewStatus == reviewStatus)
                    .SetColumns(item => item.ReviewRemark == reviewRemark)
                    .SetColumns(item => item.ReviewerUserGuid == reviewer)
                    .SetColumns(item => item.ReviewedAt == reviewedAt)
                    .SetColumns(item => item.ApprovedOvertimeMinutes == model.ApprovedOvertimeMinutes)
                    .SetColumns(item => item.UpdatedAt == reviewedAt)
                    .SetColumns(item => item.UpdatedBy == updatedBy)
                    .Where(item =>
                        !item.IsDeleted
                        && item.ApprovalGuid == approvalGuid
                        && item.ReviewStatus == "Pending")
                    .ExecuteCommandAsync();
                if (claimed != 1)
                {
                    await _db.Ado.RollbackTranAsync();
                    return ApiResponse<AttendanceApprovalDto>.Error(
                        "审核记录已处理",
                        "APPROVAL_ALREADY_REVIEWED");
                }

                PunchAdjustmentApplicationContext? application = null;
                if (adjustment != null && reviewStatus == "Approved")
                {
                    application = await ValidateStoredPunchAdjustmentForApplicationAsync(adjustment);
                    if (!application.Success)
                    {
                        await _db.Ado.RollbackTranAsync();
                        return ApiResponse<AttendanceApprovalDto>.Error(
                            application.Message,
                            application.ErrorCode);
                    }
                }

                if (model.SourceType == "Punch")
                {
                    await _db.Updateable<AttendancePunch>()
                        .SetColumns(item => item.Status == reviewStatus)
                        .Where(item => item.PunchGuid == model.SourceGuid)
                        .ExecuteCommandAsync();
                }
                else if (model.SourceType == "Leave")
                {
                    await _db.Updateable<AttendanceLeaveRequest>()
                        .SetColumns(item => item.Status == reviewStatus)
                        .SetColumns(item => item.ReviewedBy == reviewer)
                        .SetColumns(item => item.ReviewedAt == reviewedAt)
                        .SetColumns(item => item.ReviewRemark == reviewRemark)
                        .Where(item => item.LeaveGuid == model.SourceGuid)
                        .ExecuteCommandAsync();
                }
                else if (adjustment != null)
                {
                    adjustment.Status = reviewStatus == "Approved" ? "Applied" : "Rejected";
                    adjustment.ReviewedByUserGuid = reviewer;
                    adjustment.ReviewedAt = reviewedAt;
                    if (reviewStatus == "Approved")
                    {
                        var punch = await ApplyPunchAdjustmentAsync(
                            adjustment,
                            application!.ProposedPunch);
                        adjustment.AppliedPunchGuid = punch.PunchGuid;
                    }
                    await _db.Updateable(adjustment).ExecuteCommandAsync();
                    if (reviewStatus == "Approved" && adjustment.ScheduleGuid != null)
                    {
                        await ReconcileOvertimeForScheduleAsync(adjustment.ScheduleGuid);
                    }
                }
                await _db.Ado.CommitTranAsync();
                model.ReviewStatus = reviewStatus;
                model.ReviewRemark = reviewRemark;
                model.ReviewerUserGuid = reviewer;
                model.ReviewedAt = reviewedAt;
                model.UpdatedAt = reviewedAt;
                model.UpdatedBy = updatedBy;
            }
            catch (Exception exception) when (
                AttendancePunchPersistenceException.IsUniqueConstraintViolation(exception))
            {
                await _db.Ado.RollbackTranAsync();
                return ApiResponse<AttendanceApprovalDto>.Error(
                    "补卡记录已被其他请求处理",
                    "ADJUSTMENT_CONFLICT");
            }
            catch
            {
                await _db.Ado.RollbackTranAsync();
                throw;
            }

            return ApiResponse<AttendanceApprovalDto>.OK(ToDto(model), "审核已处理");
        }

        private async Task CreatePendingApprovalAsync(
            string sourceType,
            string sourceGuid,
            string storeCode,
            string applicantUserGuid,
            int? candidateOvertimeMinutes = null
        )
        {
            var exists = await BuildPendingApprovalExistenceQuery(
                _db,
                sourceType,
                sourceGuid).AnyAsync();
            if (exists)
            {
                return;
            }

            await InsertPendingApprovalWithConflictGuardAsync(
                sourceType,
                sourceGuid,
                storeCode,
                applicantUserGuid,
                candidateOvertimeMinutes);
        }

        internal static ISugarQueryable<AttendanceApproval> BuildPendingApprovalExistenceQuery(
            ISqlSugarClient db,
            string sourceType,
            string sourceGuid)
        {
            var query = db.Queryable<AttendanceApproval>().Where(item =>
                !item.IsDeleted
                && item.SourceType == sourceType
                && item.SourceGuid == sourceGuid);

            // 漏下班和加班允许终态后重新生成待审记录，其他来源继续按任意历史记录去重。
            if (sourceType is "MissingClockOut" or "Overtime")
            {
                query = query.Where(item => item.ReviewStatus == "Pending");
            }

            return query;
        }

        private async Task InsertPendingApprovalWithConflictGuardAsync(
            string sourceType,
            string sourceGuid,
            string storeCode,
            string applicantUserGuid,
            int? candidateOvertimeMinutes)
        {
            try
            {
                await _db.Insertable(new AttendanceApproval
                {
                    ApprovalGuid = Guid.NewGuid().ToString(),
                    SourceType = sourceType,
                    SourceGuid = sourceGuid,
                    StoreCode = storeCode,
                    ApplicantUserGuid = applicantUserGuid,
                    ReviewStatus = "Pending",
                    CandidateOvertimeMinutes = candidateOvertimeMinutes,
                    CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    CreatedBy = _currentUserService.GetCurrentUsername(),
                }).ExecuteCommandAsync();
            }
            catch (Exception exception) when (
                AttendancePunchPersistenceException.IsUniqueConstraintViolation(exception))
            {
                var existing = await _db.Queryable<AttendanceApproval>().AnyAsync(item =>
                    !item.IsDeleted
                    && item.SourceType == sourceType
                    && item.SourceGuid == sourceGuid
                    && item.ReviewStatus == "Pending");
                if (!existing)
                {
                    throw;
                }
            }
        }

        private async Task CancelPendingApprovalsAsync(
            string sourceType,
            string sourceGuid,
            string reason)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            await _db.Updateable<AttendanceApproval>()
                .SetColumns(item => item.ReviewStatus == "Cancelled")
                .SetColumns(item => item.ReviewRemark == reason)
                .SetColumns(item => item.ReviewerUserGuid == "system")
                .SetColumns(item => item.ReviewedAt == now)
                .SetColumns(item => item.UpdatedAt == now)
                .SetColumns(item => item.UpdatedBy == "system")
                .Where(item =>
                    !item.IsDeleted
                    && item.SourceType == sourceType
                    && item.SourceGuid == sourceGuid
                    && item.ReviewStatus == "Pending")
                .ExecuteCommandAsync();
        }

        private async Task CancelPendingApprovalAsync(string approvalGuid, string reason)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            await _db.Updateable<AttendanceApproval>()
                .SetColumns(item => item.ReviewStatus == "Cancelled")
                .SetColumns(item => item.ReviewRemark == reason)
                .SetColumns(item => item.ReviewerUserGuid == "system")
                .SetColumns(item => item.ReviewedAt == now)
                .SetColumns(item => item.UpdatedAt == now)
                .SetColumns(item => item.UpdatedBy == "system")
                .Where(item =>
                    !item.IsDeleted
                    && item.ApprovalGuid == approvalGuid
                    && item.ReviewStatus == "Pending")
                .ExecuteCommandAsync();
        }

        private async Task CancelScheduleDerivedApprovalsAsync(
            string scheduleGuid,
            string reason)
        {
            var punchGuids = await _db.Queryable<AttendancePunch>()
                .Where(item => item.ScheduleGuid == scheduleGuid)
                .Select(item => item.PunchGuid)
                .ToListAsync();
            foreach (var punchGuid in punchGuids)
            {
                await CancelPendingApprovalsAsync("Punch", punchGuid, reason);
            }
            await CancelPendingApprovalsAsync("Overtime", scheduleGuid, reason);
            await CancelPendingApprovalsAsync("MissingClockOut", scheduleGuid, reason);
        }

        private async Task ReconcileOvertimeApprovalAsync(
            AttendanceSchedule schedule,
            AttendanceWorkSessionDto session,
            int autoApproveDelta = 0)
        {
            var rows = await _db.Queryable<AttendanceApproval>()
                .Where(item =>
                    !item.IsDeleted
                    && item.SourceType == "Overtime"
                    && item.SourceGuid == schedule.ScheduleGuid)
                .OrderBy(item => item.CreatedAt)
                .ToListAsync();
            // 终态候选分钟是“已处理水位”；部分批准和拒绝都不能把剩余候选重新开单。
            var processedTotal = rows
                .Where(item => item.ReviewStatus is "Approved" or "Rejected")
                .Sum(item => item.CandidateOvertimeMinutes ?? 0);
            var autoApproved = Math.Min(
                Math.Max(0, autoApproveDelta),
                Math.Max(0, session.CandidateOvertimeMinutes - processedTotal));
            if (autoApproved > 0)
            {
                await _db.Insertable(new AttendanceApproval
                {
                    ApprovalGuid = Guid.NewGuid().ToString(),
                    SourceType = "Overtime",
                    SourceGuid = schedule.ScheduleGuid,
                    StoreCode = schedule.StoreCode,
                    ApplicantUserGuid = schedule.UserGuid,
                    ReviewerUserGuid = "system",
                    ReviewStatus = "Approved",
                    ReviewRemark = "店长本人直接改时产生，系统仅批准本次加班增量",
                    ReviewedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    CandidateOvertimeMinutes = autoApproved,
                    ApprovedOvertimeMinutes = autoApproved,
                    CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    CreatedBy = _currentUserService.GetCurrentUsername(),
                }).ExecuteCommandAsync();
                processedTotal += autoApproved;
            }

            var remaining = session.ScheduleState == "Completed"
                ? Math.Max(0, session.CandidateOvertimeMinutes - processedTotal)
                : 0;
            var pending = rows.FirstOrDefault(item => item.ReviewStatus == "Pending");
            if (remaining <= 0)
            {
                await CancelPendingApprovalsAsync(
                    "Overtime",
                    schedule.ScheduleGuid,
                    "排班状态或加班候选已变化，原待审加班已自动取消");
                return;
            }

            if (pending != null)
            {
                pending.CandidateOvertimeMinutes = remaining;
                pending.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
                pending.UpdatedBy = _currentUserService.GetCurrentUsername();
                await _db.Updateable(pending).ExecuteCommandAsync();
                return;
            }

            await InsertPendingApprovalWithConflictGuardAsync(
                "Overtime",
                schedule.ScheduleGuid,
                schedule.StoreCode,
                schedule.UserGuid,
                remaining);
        }

        private async Task ReconcileDerivedApprovalsForScheduleAsync(
            string scheduleGuid,
            int autoApproveDelta = 0)
        {
            var schedule = await _db.Queryable<AttendanceSchedule>().FirstAsync(item =>
                !item.IsDeleted && item.ScheduleGuid == scheduleGuid);
            if (schedule == null)
            {
                return;
            }
            var session = await BuildWorkSessionAsync(schedule);
            await ReconcileOvertimeApprovalAsync(schedule, session, autoApproveDelta);
            var settings = await GetOrCreateSettingsModelAsync();
            await ReconcileFinalPunchApprovalsAsync(schedule, session, settings);
            await ReconcileMissingClockOutApprovalAsync(schedule, session);
        }

        private Task ReconcileOvertimeForScheduleAsync(
            string scheduleGuid,
            int autoApproveDelta = 0)
        {
            // 保留旧调用名，确保任何补卡恢复路径同步协调所有派生审批而非只处理加班。
            return ReconcileDerivedApprovalsForScheduleAsync(scheduleGuid, autoApproveDelta);
        }

        private async Task<AttendanceWorkSessionDto> BuildWorkSessionAsync(
            AttendanceSchedule schedule)
        {
            var punches = await _db.Queryable<AttendancePunch>()
                .Where(item => !item.IsDeleted && item.ScheduleGuid == schedule.ScheduleGuid)
                .ToListAsync();
            var settings = await GetOrCreateSettingsModelAsync();
            var segmentLimit = await ResolveSegmentLimitAsync(schedule.UserGuid, schedule.StoreCode);
            var timeZone = await ResolveStoreTimeZoneAsync(schedule.StoreCode, null);
            return AttendanceWorkSessionCalculator.Calculate(
                schedule,
                punches,
                segmentLimit,
                ConvertUtcToStoreLocal(_timeProvider.GetUtcNow().UtcDateTime, timeZone),
                settings.EarlyLeaveGraceMinutes,
                settings.LateGraceMinutes);
        }

        private async Task ReconcileFinalPunchApprovalsAsync(
            AttendanceSchedule schedule,
            AttendanceWorkSessionDto session,
            AttendanceSettings settings)
        {
            var expected = GetExpectedPunchApprovalCandidates(session, settings);
            var expectedGuids = expected
                .Select(item => item.PunchGuid)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var schedulePunchGuids = await _db.Queryable<AttendancePunch>()
                .Where(item => item.ScheduleGuid == schedule.ScheduleGuid)
                .Select(item => item.PunchGuid)
                .ToListAsync();
            if (schedulePunchGuids.Count > 0)
            {
                var pending = await _db.Queryable<AttendanceApproval>()
                    .Where(item =>
                        !item.IsDeleted
                        && item.SourceType == "Punch"
                        && item.ReviewStatus == "Pending"
                        && schedulePunchGuids.Contains(item.SourceGuid))
                    .ToListAsync();
                foreach (var stale in pending.Where(item => !expectedGuids.Contains(item.SourceGuid)))
                {
                    await CancelPendingApprovalAsync(
                        stale.ApprovalGuid,
                        "打卡异常状态已变化，原待审记录已自动取消");
                }
            }

            foreach (var punch in expected)
            {
                await CreatePendingApprovalAsync(
                    "Punch",
                    punch.PunchGuid,
                    punch.StoreCode,
                    punch.UserGuid);
            }
        }

        private static List<AttendancePunchDto> GetExpectedPunchApprovalCandidates(
            AttendanceWorkSessionDto session,
            AttendanceSettings settings)
        {
            var firstClockIn = session.Segments.FirstOrDefault()?.ClockIn;
            var finalClockOut = session.Segments.LastOrDefault(item => item.Status == "Final")?.ClockOut;
            return new[] { firstClockIn, finalClockOut }
                .Where(item => item != null && RequiresApproval(item.Status, settings))
                .Select(item => item!)
                .GroupBy(item => item.PunchGuid, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private async Task ReconcileMissingClockOutApprovalAsync(
            AttendanceSchedule schedule,
            AttendanceWorkSessionDto session)
        {
            if (!session.HasMissingClockOut)
            {
                await CancelPendingApprovalsAsync(
                    "MissingClockOut",
                    schedule.ScheduleGuid,
                    "漏下班状态已消除，待处理记录已自动取消");
                return;
            }
            await CreatePendingApprovalAsync(
                "MissingClockOut",
                schedule.ScheduleGuid,
                schedule.StoreCode,
                schedule.UserGuid);
        }

        private async Task<PunchAdjustmentContext> BuildPunchAdjustmentContextAsync(
            CreateAttendancePunchAdjustmentDto request,
            string? managedTargetUserGuid = null)
        {
            var isManaged = !string.IsNullOrWhiteSpace(managedTargetUserGuid);
            var userGuid = isManaged ? managedTargetUserGuid!.Trim() : ResolveCurrentUserGuid();
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return PunchAdjustmentContext.Error("无法识别当前员工", "USER_NOT_FOUND");
            }
            if (string.IsNullOrWhiteSpace(request.StoreCode))
            {
                return PunchAdjustmentContext.Error("分店代码不能为空", "STORE_REQUIRED");
            }
            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return PunchAdjustmentContext.Error("补卡原因不能为空", "REASON_REQUIRED");
            }

            var storeCode = request.StoreCode.Trim();
            // 代员工补卡校验操作者的管理分店范围；员工归属由下方「该员工在该店有排班」保证。
            var access = isManaged
                ? await ResolveManagedStoreAccessAsync(storeCode)
                : await ResolveRelatedStoreAccessAsync(userGuid, storeCode);
            if (!access.Success)
            {
                return PunchAdjustmentContext.Error(access.Message, access.ErrorCode);
            }
            if (isManaged && !await IsCurrentUserManagerForStoreAsync(storeCode))
            {
                return PunchAdjustmentContext.Error("只能修改自己管理分店的打卡", "FORBIDDEN_STORE");
            }

            var punchType = NormalizePunchType(request.PunchType);
            var storeTimeZone = await ResolveStoreTimeZoneAsync(storeCode, null);
            DateTime requestedPunchTimeLocal;
            DateTime requestedPunchTimeUtc;
            if (request.RequestedPunchTimeUtc.HasValue)
            {
                // 服务器与数据库以 UTC 为权威；客户端 local 只用于兼容未升级的旧请求。
                requestedPunchTimeUtc = request.RequestedPunchTimeUtc.Value.UtcDateTime;
                requestedPunchTimeLocal = DateTime.SpecifyKind(
                    ConvertUtcToStoreLocal(requestedPunchTimeUtc, storeTimeZone),
                    DateTimeKind.Unspecified);
            }
            else
            {
                requestedPunchTimeLocal = DateTime.SpecifyKind(
                    request.RequestedPunchTimeLocal,
                    DateTimeKind.Unspecified);
                TimeZoneInfo timezone;
                try
                {
                    timezone = TimeZoneInfo.FindSystemTimeZoneById(storeTimeZone);
                }
                catch (TimeZoneNotFoundException)
                {
                    timezone = TimeZoneInfo.FindSystemTimeZoneById(DefaultStoreTimeZone);
                }
                catch (InvalidTimeZoneException)
                {
                    timezone = TimeZoneInfo.FindSystemTimeZoneById(DefaultStoreTimeZone);
                }

                if (timezone.IsInvalidTime(requestedPunchTimeLocal)
                    || timezone.IsAmbiguousTime(requestedPunchTimeLocal))
                {
                    return PunchAdjustmentContext.Error(
                        "该门店本地时间无效或存在歧义，请升级客户端并传入 UTC 时间",
                        "PUNCH_TIME_INVALID");
                }
                requestedPunchTimeUtc = ConvertStoreLocalToUtc(
                    requestedPunchTimeLocal,
                    storeTimeZone);
            }

            var scheduleQuery = _db.Queryable<AttendanceSchedule>()
                .Where(item =>
                    !item.IsDeleted
                    && item.Status == "Active"
                    && item.StoreCode == storeCode
                    && item.UserGuid == userGuid
                    && item.WorkDate >= requestedPunchTimeLocal.Date
                    && item.WorkDate < requestedPunchTimeLocal.Date.AddDays(1));
            var schedule = string.IsNullOrWhiteSpace(request.ScheduleGuid)
                ? await scheduleQuery.OrderBy(item => item.StartTime).FirstAsync()
                : await scheduleQuery.FirstAsync(item => item.ScheduleGuid == request.ScheduleGuid.Trim());
            if (schedule == null)
            {
                return PunchAdjustmentContext.Error("补卡日期没有有效排班", "SCHEDULE_NOT_FOUND");
            }

            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var nowLocal = ConvertUtcToStoreLocal(nowUtc, storeTimeZone);
            // 直接生效只认「补卡与修改管理分店打卡」权限：代员工修改本身已由接口策略校验，
            // 店长本人补卡则需同时持有该权限并管理该分店，否则与普通员工一样进入审核。
            var wouldAutoApprove = isManaged
                || (await IsCurrentUserManagerForStoreAsync(storeCode)
                    && await CurrentUserHasPermissionAsync(Permissions.Attendance.Punch.AdjustManagedStore));
            if (!IsAdmin())
            {
                if (isManaged)
                {
                    if (!IsWithinManagedAdjustmentWindow(schedule.WorkDate, nowLocal.Date))
                    {
                        return PunchAdjustmentContext.Error(
                            "只能修改本周的打卡；周一、周二仍可修改上周",
                            "ADJUSTMENT_WINDOW_EXPIRED");
                    }
                }
                else
                {
                    var ageDays = (nowLocal.Date - requestedPunchTimeLocal.Date).Days;
                    if (ageDays < 0 || ageDays > 2)
                    {
                        return PunchAdjustmentContext.Error(
                            "非 Admin 员工只能申请两天内的补卡",
                            "ADJUSTMENT_WINDOW_EXPIRED");
                    }
                }
            }
            if (isManaged && requestedPunchTimeUtc > nowUtc)
            {
                return PunchAdjustmentContext.Error("补卡时间不能晚于当前时间", "PUNCH_TIME_IN_FUTURE");
            }

            AttendancePunch? originalPunch = null;
            if (!string.IsNullOrWhiteSpace(request.OriginalPunchGuid))
            {
                originalPunch = await _db.Queryable<AttendancePunch>().FirstAsync(item =>
                    !item.IsDeleted
                    && item.PunchGuid == request.OriginalPunchGuid.Trim()
                    && item.UserGuid == userGuid
                    && item.StoreCode == storeCode
                    && item.ScheduleGuid == schedule.ScheduleGuid);
                if (originalPunch == null)
                {
                    return PunchAdjustmentContext.Error("原打卡记录不存在", "PUNCH_NOT_FOUND");
                }

                var existingAdjustment = await _db.Queryable<AttendancePunchAdjustment>().AnyAsync(item =>
                    !item.IsDeleted
                    && item.OriginalPunchGuid == originalPunch.PunchGuid
                    && (item.Status == "Pending" || item.Status == "Applied"));
                if (existingAdjustment)
                {
                    return PunchAdjustmentContext.Error(
                        "原打卡记录已有待处理或已生效的调整",
                        "ADJUSTMENT_ALREADY_EXISTS");
                }

                var alreadySuperseded = await _db.Queryable<AttendancePunch>().AnyAsync(item =>
                    !item.IsDeleted && item.SupersedesPunchGuid == originalPunch.PunchGuid);
                if (alreadySuperseded)
                {
                    return PunchAdjustmentContext.Error("原打卡记录已被调整", "PUNCH_ALREADY_ADJUSTED");
                }
            }

            var punches = await _db.Queryable<AttendancePunch>()
                .Where(item => !item.IsDeleted && item.ScheduleGuid == schedule.ScheduleGuid)
                .ToListAsync();
            var segmentLimit = await ResolveSegmentLimitAsync(userGuid, storeCode);
            var proposedPunch = new AttendancePunch
            {
                PunchGuid = Guid.NewGuid().ToString(),
                ScheduleGuid = schedule.ScheduleGuid,
                StoreCode = storeCode,
                UserGuid = userGuid,
                WorkDate = schedule.WorkDate,
                StoreTimeZone = storeTimeZone,
                PunchType = punchType,
                PunchTimeLocal = requestedPunchTimeLocal,
                PunchTimeUtc = requestedPunchTimeUtc,
                Status = "Normal",
                Source = "ManualAdjustment",
                SupersedesPunchGuid = originalPunch?.PunchGuid,
            };
            var proposedPunches = punches.Append(proposedPunch).ToList();
            var sequenceValidation = ValidateEffectivePunchSequence(proposedPunches, segmentLimit);
            if (!sequenceValidation.Success)
            {
                return PunchAdjustmentContext.Error(sequenceValidation.Message, sequenceValidation.ErrorCode);
            }

            var adjacentBusinessDayPunches = await GetAdjacentBusinessDayPunchesAsync(
                userGuid,
                schedule.WorkDate);
            var sameStoreBusinessDayPunches = adjacentBusinessDayPunches
                .Where(item =>
                    item.WorkDate >= schedule.WorkDate.Date
                    && item.WorkDate < schedule.WorkDate.Date.AddDays(1)
                    && item.StoreCode.Equals(storeCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
            sameStoreBusinessDayPunches.Add(proposedPunch);
            var dailyLimitValidation = ValidateDailyStoreSegmentLimit(
                sameStoreBusinessDayPunches,
                segmentLimit);
            if (!dailyLimitValidation.Success)
            {
                return PunchAdjustmentContext.Error(
                    dailyLimitValidation.Message,
                    dailyLimitValidation.ErrorCode);
            }
            adjacentBusinessDayPunches.Add(proposedPunch);
            if (HasCrossScheduleOverlap(adjacentBusinessDayPunches))
            {
                return PunchAdjustmentContext.Error(
                    "补卡后与其他排班或分店的班段重叠",
                    "PUNCH_TIME_OVERLAP");
            }

            var settings = await GetOrCreateSettingsModelAsync();
            var existingSession = AttendanceWorkSessionCalculator.Calculate(
                schedule,
                punches,
                segmentLimit,
                nowLocal,
                settings.EarlyLeaveGraceMinutes,
                settings.LateGraceMinutes);
            var proposedSession = AttendanceWorkSessionCalculator.Calculate(
                schedule,
                proposedPunches,
                segmentLimit,
                nowLocal,
                settings.EarlyLeaveGraceMinutes,
                settings.LateGraceMinutes);
            return PunchAdjustmentContext.OK(
                schedule,
                proposedPunch,
                new AttendancePunchAdjustmentPreviewDto
                {
                    IsValid = true,
                    ExistingSession = existingSession,
                    ProposedSession = proposedSession,
                    WorkedMinutesDelta = proposedSession.WorkedMinutes - existingSession.WorkedMinutes,
                    CandidateOvertimeMinutesDelta = proposedSession.CandidateOvertimeMinutes
                        - existingSession.CandidateOvertimeMinutes,
                    WouldAutoApprove = wouldAutoApprove,
                    PreviewRevision = ComputePunchAdjustmentPreviewRevision(
                        schedule,
                        proposedPunch,
                        punches,
                        adjacentBusinessDayPunches,
                        segmentLimit,
                        settings,
                        wouldAutoApprove),
                });
        }

        private async Task<PunchAdjustmentApplicationContext> ValidateStoredPunchAdjustmentForApplicationAsync(
            AttendancePunchAdjustment adjustment)
        {
            var schedule = await _db.Queryable<AttendanceSchedule>().FirstAsync(item =>
                !item.IsDeleted
                && item.Status == "Active"
                && item.ScheduleGuid == adjustment.ScheduleGuid
                && item.StoreCode == adjustment.StoreCode
                && item.UserGuid == adjustment.UserGuid);
            if (schedule == null)
            {
                return PunchAdjustmentApplicationContext.Error(
                    "补卡对应的排班已失效",
                    "SCHEDULE_NOT_FOUND");
            }

            if (!string.IsNullOrWhiteSpace(adjustment.OriginalPunchGuid))
            {
                var originalExists = await _db.Queryable<AttendancePunch>().AnyAsync(item =>
                    !item.IsDeleted
                    && item.PunchGuid == adjustment.OriginalPunchGuid
                    && item.ScheduleGuid == schedule.ScheduleGuid
                    && item.UserGuid == adjustment.UserGuid
                    && item.StoreCode == adjustment.StoreCode);
                if (!originalExists)
                {
                    return PunchAdjustmentApplicationContext.Error(
                        "原打卡记录不存在",
                        "PUNCH_NOT_FOUND");
                }
                var superseded = await _db.Queryable<AttendancePunch>().AnyAsync(item =>
                    !item.IsDeleted && item.SupersedesPunchGuid == adjustment.OriginalPunchGuid);
                if (superseded)
                {
                    return PunchAdjustmentApplicationContext.Error(
                        "原打卡记录已被调整",
                        "PUNCH_ALREADY_ADJUSTED");
                }
                var competingAdjustment = await _db.Queryable<AttendancePunchAdjustment>().AnyAsync(item =>
                    !item.IsDeleted
                    && item.AdjustmentGuid != adjustment.AdjustmentGuid
                    && item.OriginalPunchGuid == adjustment.OriginalPunchGuid
                    && (item.Status == "Pending" || item.Status == "Applied"));
                if (competingAdjustment)
                {
                    return PunchAdjustmentApplicationContext.Error(
                        "原打卡记录已有其他待处理或已生效的调整",
                        "ADJUSTMENT_ALREADY_EXISTS");
                }
            }

            var storeTimeZone = await ResolveStoreTimeZoneAsync(adjustment.StoreCode, null);
            var proposedPunch = new AttendancePunch
            {
                PunchGuid = Guid.NewGuid().ToString(),
                ScheduleGuid = schedule.ScheduleGuid,
                StoreCode = adjustment.StoreCode,
                UserGuid = adjustment.UserGuid,
                WorkDate = schedule.WorkDate,
                StoreTimeZone = storeTimeZone,
                PunchType = adjustment.PunchType,
                PunchTimeLocal = adjustment.RequestedPunchTimeLocal,
                PunchTimeUtc = adjustment.RequestedPunchTimeUtc,
                Status = "Normal",
                Source = "ManualAdjustment",
                SupersedesPunchGuid = adjustment.OriginalPunchGuid,
                AdjustmentGuid = adjustment.AdjustmentGuid,
            };
            var schedulePunches = await _db.Queryable<AttendancePunch>()
                .Where(item => !item.IsDeleted && item.ScheduleGuid == schedule.ScheduleGuid)
                .ToListAsync();
            var segmentLimit = await ResolveSegmentLimitAsync(adjustment.UserGuid, adjustment.StoreCode);
            var sequenceValidation = ValidateEffectivePunchSequence(
                schedulePunches.Append(proposedPunch).ToList(),
                segmentLimit);
            if (!sequenceValidation.Success)
            {
                return PunchAdjustmentApplicationContext.Error(
                    sequenceValidation.Message,
                    sequenceValidation.ErrorCode);
            }

            var adjacentBusinessDayPunches = await GetAdjacentBusinessDayPunchesAsync(
                adjustment.UserGuid,
                schedule.WorkDate);
            var sameStoreBusinessDayPunches = adjacentBusinessDayPunches
                .Where(item =>
                    item.WorkDate >= schedule.WorkDate.Date
                    && item.WorkDate < schedule.WorkDate.Date.AddDays(1)
                    && item.StoreCode.Equals(adjustment.StoreCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
            sameStoreBusinessDayPunches.Add(proposedPunch);
            var dailyLimitValidation = ValidateDailyStoreSegmentLimit(
                sameStoreBusinessDayPunches,
                segmentLimit);
            if (!dailyLimitValidation.Success)
            {
                return PunchAdjustmentApplicationContext.Error(
                    dailyLimitValidation.Message,
                    dailyLimitValidation.ErrorCode);
            }
            adjacentBusinessDayPunches.Add(proposedPunch);
            if (HasCrossScheduleOverlap(adjacentBusinessDayPunches))
            {
                return PunchAdjustmentApplicationContext.Error(
                    "补卡后与其他排班或分店的班段重叠",
                    "PUNCH_TIME_OVERLAP");
            }

            return PunchAdjustmentApplicationContext.OK(schedule, proposedPunch);
        }

        private async Task<AttendancePunch> ApplyPunchAdjustmentAsync(
            AttendancePunchAdjustment adjustment,
            AttendancePunch? preparedPunch = null)
        {
            var punch = preparedPunch ?? new AttendancePunch
            {
                PunchGuid = Guid.NewGuid().ToString(),
                ScheduleGuid = adjustment.ScheduleGuid,
                StoreCode = adjustment.StoreCode,
                UserGuid = adjustment.UserGuid,
                WorkDate = adjustment.RequestedPunchTimeLocal.Date,
                StoreTimeZone = await ResolveStoreTimeZoneAsync(adjustment.StoreCode, null),
                PunchType = adjustment.PunchType,
                PunchTimeLocal = adjustment.RequestedPunchTimeLocal,
                PunchTimeUtc = adjustment.RequestedPunchTimeUtc,
                Status = "Normal",
                Source = "ManualAdjustment",
                SupersedesPunchGuid = adjustment.OriginalPunchGuid,
            };
            punch.AdjustmentGuid = adjustment.AdjustmentGuid;
            punch.Remark = adjustment.Reason;
            punch.CreatedAt = _timeProvider.GetUtcNow().UtcDateTime;
            punch.CreatedBy = _currentUserService.GetCurrentUsername();
            await _db.Insertable(punch).ExecuteCommandAsync();
            if (!string.IsNullOrWhiteSpace(adjustment.OriginalPunchGuid))
            {
                // 覆盖原卡后，原异常卡的待审已不再有可批准的来源，必须与新卡同事务取消。
                await CancelPendingApprovalsAsync(
                    "Punch",
                    adjustment.OriginalPunchGuid,
                    "原打卡已被补卡覆盖，待审异常已自动取消");
            }
            return punch;
        }

        private async Task<bool> IsCurrentUserManagerForStoreAsync(string storeCode)
        {
            if (IsAdmin())
            {
                return true;
            }

            var user = _httpContextAccessor.HttpContext?.User;
            if (!CurrentUserManageableStoreScopeService.HasAnyRole(
                    user,
                    CurrentUserManageableStoreScopeService.StoreManagerRoleAliases))
            {
                return false;
            }

            var scope = await _scopeService.GetScopeAsync();
            return scope.IsAllowed && scope.CanAccessStoreCode(storeCode);
        }

        private async Task<int> ResolveSegmentLimitAsync(string userGuid, string storeCode)
        {
            var roleNames = await _db.Queryable<UserRole>()
                .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                .Where((userRole, role) =>
                    !userRole.IsDeleted
                    && !role.IsDeleted
                    && role.IsActive
                    && userRole.UserGUID == userGuid)
                .Select((userRole, role) => role.RoleName)
                .ToListAsync();
            if (!roleNames.Any(roleName => Permissions.StoreManagerRoleNames.Contains(
                    roleName,
                    StringComparer.OrdinalIgnoreCase)))
            {
                return 2;
            }

            // 任一仍有效的店长管理关系即可获得三段上限，不把上限错误收窄到当前门店。
            var hasActiveManagementRelation = await _db.Queryable<UserStore>()
                .InnerJoin<Store>((userStore, store) => userStore.StoreGUID == store.StoreGUID)
                .Where((userStore, store) =>
                    !userStore.IsDeleted
                    && !store.IsDeleted
                    && userStore.IsPrimary
                    && userStore.UserGUID == userGuid)
                .AnyAsync();
            return hasActiveManagementRelation ? 3 : 2;
        }

        private static ValidationResult ValidateEffectivePunchSequence(
            List<AttendancePunch> punches,
            int segmentLimit)
        {
            var superseded = punches
                .Where(item => !string.IsNullOrWhiteSpace(item.SupersedesPunchGuid))
                .Select(item => item.SupersedesPunchGuid!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var effective = punches
                .Where(item => !superseded.Contains(item.PunchGuid))
                // 有效卡序必须按真实 UTC 先后验证，不能受 Sydney 回拨时本地时间倒退影响。
                .OrderBy(item => item.PunchTimeUtc)
                .ThenBy(item => item.Id)
                .ToList();
            if (effective.Count > segmentLimit * 2)
            {
                return ValidationResult.Error("班段数量已达到上限", "SEGMENT_LIMIT_REACHED");
            }

            for (var index = 0; index < effective.Count; index++)
            {
                var expectedType = index % 2 == 0 ? "ClockIn" : "ClockOut";
                if (!effective[index].PunchType.Equals(expectedType, StringComparison.OrdinalIgnoreCase))
                {
                    return ValidationResult.Error("打卡顺序无效或班段发生重叠", "INVALID_PUNCH_SEQUENCE");
                }
            }

            return ValidationResult.OK();
        }

        private static ValidationResult ValidateDailyStoreSegmentLimit(
            IEnumerable<AttendancePunch> punches,
            int segmentLimit)
        {
            if (CountEffectiveStoreSegments(punches) > segmentLimit)
            {
                return ValidationResult.Error(
                    "该员工在本店当日班段数量已达到上限",
                    "SEGMENT_LIMIT_REACHED");
            }
            return ValidationResult.OK();
        }

        private static int CountEffectiveStoreSegments(IEnumerable<AttendancePunch> punches)
        {
            var rows = punches.ToList();
            var superseded = rows
                .Where(item => !string.IsNullOrWhiteSpace(item.SupersedesPunchGuid))
                .Select(item => item.SupersedesPunchGuid!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return rows.Count(item =>
                !superseded.Contains(item.PunchGuid)
                && item.PunchType.Equals("ClockIn", StringComparison.OrdinalIgnoreCase));
        }

        private async Task<List<AttendancePunch>> GetAdjacentBusinessDayPunchesAsync(
            string userGuid,
            DateTime workDate)
        {
            // 门店时区不同会使同一 UTC 班段落在相邻本地业务日；只查当天会漏掉重叠。
            var from = workDate.Date.AddDays(-1);
            var until = workDate.Date.AddDays(2);
            return await _db.Queryable<AttendancePunch>()
                .Where(item =>
                    !item.IsDeleted
                    && item.UserGuid == userGuid
                    && item.WorkDate >= from
                    && item.WorkDate < until)
                .ToListAsync();
        }

        private static string ComputePunchAdjustmentPreviewRevision(
            AttendanceSchedule schedule,
            AttendancePunch proposedPunch,
            IEnumerable<AttendancePunch> schedulePunches,
            IEnumerable<AttendancePunch> adjacentBusinessDayPunches,
            int segmentLimit,
            AttendanceSettings settings,
            bool wouldAutoApprove)
        {
            var values = new List<string>
            {
                "attendance-adjustment-preview-v2",
                schedule.ScheduleGuid,
                schedule.StoreCode,
                schedule.UserGuid,
                schedule.WorkDate.Date.Ticks.ToString(),
                schedule.StartTime.Ticks.ToString(),
                schedule.EndTime.Ticks.ToString(),
                schedule.Status,
                segmentLimit.ToString(),
                settings.LateGraceMinutes.ToString(),
                settings.EarlyLeaveGraceMinutes.ToString(),
                settings.AllowNoSchedulePunch ? "1" : "0",
                settings.RequireApprovalForLate ? "1" : "0",
                settings.RequireApprovalForEarlyLeave ? "1" : "0",
                settings.RequireApprovalForNoSchedule ? "1" : "0",
                settings.RequireApprovalForDuplicate ? "1" : "0",
                wouldAutoApprove ? "1" : "0",
                proposedPunch.PunchType,
                proposedPunch.PunchTimeUtc.Ticks.ToString(),
                proposedPunch.PunchTimeLocal.Ticks.ToString(),
                proposedPunch.SupersedesPunchGuid ?? string.Empty,
            };

            // revision 只编码参与计算的有效卡链；审批状态等展示字段变化不能让预览失效。
            var revisionPunches = schedulePunches
                .Concat(adjacentBusinessDayPunches)
                .GroupBy(item => item.PunchGuid, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            var supersededPunchGuids = revisionPunches
                .Where(item => !string.IsNullOrWhiteSpace(item.SupersedesPunchGuid))
                .Select(item => item.SupersedesPunchGuid!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var punch in revisionPunches
                // proposedPunch 的 GUID 每次预览重算都会新建；候选本身已在上方固定字段中编码。
                .Where(item => !item.PunchGuid.Equals(
                    proposedPunch.PunchGuid,
                    StringComparison.OrdinalIgnoreCase))
                .Where(item => !supersededPunchGuids.Contains(item.PunchGuid))
                .OrderBy(item => item.PunchTimeUtc)
                .ThenBy(item => item.PunchGuid, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(string.Join(":",
                    punch.PunchGuid,
                    punch.ScheduleGuid ?? string.Empty,
                    punch.StoreCode,
                    punch.UserGuid,
                    punch.WorkDate.Date.Ticks,
                    punch.PunchType,
                    punch.PunchTimeUtc.Ticks,
                    punch.PunchTimeLocal.Ticks,
                    punch.SupersedesPunchGuid ?? string.Empty));
            }

            var payload = Encoding.UTF8.GetBytes(string.Join("|", values));
            try
            {
                return Convert.ToHexString(SHA256.HashData(payload));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        private static bool HasCrossScheduleOverlap(List<AttendancePunch> punches)
        {
            var superseded = punches
                .Where(item => !string.IsNullOrWhiteSpace(item.SupersedesPunchGuid))
                .Select(item => item.SupersedesPunchGuid!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var intervals = new List<(string Key, DateTime Start, DateTime End)>();
            foreach (var group in punches
                .Where(item => !superseded.Contains(item.PunchGuid))
                .GroupBy(item => item.ScheduleGuid ?? $"{item.StoreCode}:{item.WorkDate:yyyyMMdd}"))
            {
                var ordered = group.OrderBy(item => item.PunchTimeUtc).ThenBy(item => item.Id).ToList();
                for (var index = 0; index + 1 < ordered.Count; index += 2)
                {
                    if (ordered[index].PunchType != "ClockIn"
                        || ordered[index + 1].PunchType != "ClockOut")
                    {
                        continue;
                    }
                    intervals.Add((group.Key, ordered[index].PunchTimeUtc, ordered[index + 1].PunchTimeUtc));
                }
            }

            var orderedIntervals = intervals.OrderBy(item => item.Start).ToList();
            for (var index = 1; index < orderedIntervals.Count; index++)
            {
                if (orderedIntervals[index].Start < orderedIntervals[index - 1].End
                    && !orderedIntervals[index].Key.Equals(
                        orderedIntervals[index - 1].Key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 店长代员工补卡窗口：按单周（周一起）工资周期，可改本周至今天；
        /// 周一、周二是上周的结算核对期，仍可改上周。
        /// </summary>
        internal static bool IsWithinManagedAdjustmentWindow(DateTime workDate, DateTime storeToday)
        {
            var today = storeToday.Date;
            var currentWeekStart = GetWeekStart(today);
            var isSettlementGrace = today.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Tuesday;
            var earliest = isSettlementGrace ? currentWeekStart.AddDays(-7) : currentWeekStart;
            return workDate.Date >= earliest && workDate.Date <= today;
        }

        private async Task<bool> CurrentUserHasPermissionAsync(string permission)
        {
            var user = _httpContextAccessor.HttpContext?.User;
            if (user == null || _authorizationService == null)
            {
                return false;
            }

            return (await _authorizationService.AuthorizeAsync(user, permission)).Succeeded;
        }

        private async Task<bool> IsWithinAdjustmentWindowAsync(DateTime workDate, string? storeCode)
        {
            if (IsAdmin())
            {
                return true;
            }

            var timeZone = await ResolveStoreTimeZoneAsync(storeCode, null);
            var today = ConvertUtcToStoreLocal(_timeProvider.GetUtcNow().UtcDateTime, timeZone).Date;
            var ageDays = (today - workDate.Date).Days;
            return ageDays is >= 0 and <= 2;
        }

        private async Task<bool> HasOverlappingScheduleAsync(
            string? currentScheduleGuid,
            string storeCode,
            string userGuid,
            DateTime workDate,
            TimeSpan startTime,
            TimeSpan endTime
        )
        {
            var rows = await _db.Queryable<AttendanceSchedule>()
                .Where(item =>
                    !item.IsDeleted
                    && item.Status != "Cancelled"
                    && item.StoreCode == storeCode.Trim()
                    && item.UserGuid == userGuid.Trim()
                    && item.WorkDate >= workDate.Date
                    && item.WorkDate < workDate.Date.AddDays(1)
                )
                .WhereIF(!string.IsNullOrWhiteSpace(currentScheduleGuid), item => item.ScheduleGuid != currentScheduleGuid)
                .ToListAsync();

            return rows.Any(item => startTime < item.EndTime && endTime > item.StartTime);
        }

        private async Task<AttendanceSchedule?> FindScheduleForPunchAsync(
            string storeCode,
            string userGuid,
            DateTime workDate,
            TimeSpan localTime,
            int segmentLimit,
            AttendanceSettings settings
        )
        {
            var rows = await _db.Queryable<AttendanceSchedule>()
                .Where(item =>
                    !item.IsDeleted
                    && item.Status == "Active"
                    && item.StoreCode == storeCode.Trim()
                    && item.UserGuid == userGuid
                    && item.WorkDate >= workDate.Date
                    && item.WorkDate < workDate.Date.AddDays(1)
                )
                .ToListAsync();
            if (rows.Count == 0)
            {
                return null;
            }

            var scheduleGuids = rows.Select(item => item.ScheduleGuid).ToList();
            var punches = await _db.Queryable<AttendancePunch>()
                .Where(item => !item.IsDeleted
                    && item.ScheduleGuid != null
                    && scheduleGuids.Contains(item.ScheduleGuid))
                .ToListAsync();
            var nowLocal = workDate.Date.Add(localTime);
            var sessions = rows.Select(schedule => new
            {
                Schedule = schedule,
                Session = AttendanceWorkSessionCalculator.Calculate(
                    schedule,
                    punches,
                    segmentLimit,
                    nowLocal,
                    settings.EarlyLeaveGraceMinutes,
                    settings.LateGraceMinutes),
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

        private static string ResolveSegmentPunchStatus(
            AttendanceSchedule? schedule,
            string punchType,
            TimeSpan localTime,
            AttendanceSettings settings,
            bool isFirstClockIn,
            bool isFinalClockOut)
        {
            if (schedule == null)
            {
                return "NoSchedule";
            }
            if (punchType == "ClockIn")
            {
                if (!isFirstClockIn)
                {
                    return "Normal";
                }
                if (localTime > schedule.StartTime.Add(
                        TimeSpan.FromMinutes(settings.LateGraceMinutes)))
                {
                    return "Late";
                }
                return schedule.StartTime - localTime >= TimeSpan.FromMinutes(15)
                    ? "Early"
                    : "Normal";
            }
            if (!isFinalClockOut)
            {
                return "Break";
            }
            if (localTime < schedule.EndTime.Subtract(
                    TimeSpan.FromMinutes(settings.EarlyLeaveGraceMinutes)))
            {
                return "EarlyLeave";
            }
            return localTime - schedule.EndTime >= TimeSpan.FromMinutes(15)
                ? "LateLeave"
                : "Normal";
        }

        private static bool RequiresApproval(string status, AttendanceSettings settings) =>
            status switch
            {
                "Late" => settings.RequireApprovalForLate,
                "EarlyLeave" => settings.RequireApprovalForEarlyLeave,
                "NoSchedule" => settings.RequireApprovalForNoSchedule,
                "Duplicate" => settings.RequireApprovalForDuplicate,
                _ => false,
            };

        private async Task<AttendanceSettings> GetOrCreateSettingsModelAsync()
        {
            var settings = await _db.Queryable<AttendanceSettings>()
                .Where(item => !item.IsDeleted)
                .OrderBy(item => item.Id)
                .FirstAsync();
            if (settings != null)
            {
                return settings;
            }

            settings = new AttendanceSettings
            {
                LateGraceMinutes = 5,
                EarlyLeaveGraceMinutes = 5,
                AllowNoSchedulePunch = true,
                RequireApprovalForLate = true,
                RequireApprovalForEarlyLeave = true,
                RequireApprovalForNoSchedule = true,
                RequireApprovalForDuplicate = true,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "system",
            };
            await _db.Insertable(settings).ExecuteCommandAsync();
            return settings;
        }

        private async Task<StoreAccessResult> ResolveManagedStoreAccessAsync(string? requestedStoreCode)
        {
            var scope = await _scopeService.GetScopeAsync();
            if (!scope.IsAllowed)
            {
                return StoreAccessResult.Forbidden(scope.Message);
            }

            var requested = requestedStoreCode?.Trim();
            if (!string.IsNullOrWhiteSpace(requested) && !scope.CanAccessStoreCode(requested))
            {
                return StoreAccessResult.Forbidden("没有权限访问该分店", "FORBIDDEN_STORE");
            }

            return StoreAccessResult.Allowed(
                string.IsNullOrWhiteSpace(requested)
                    ? scope.StoreCodes.ToList()
                    : new List<string> { requested }
            );
        }

        private async Task<StoreAccessResult> ResolveRelatedStoreAccessAsync(
            string userGuid,
            string? requestedStoreCode,
            bool includeRelatedStoreCodes = false
        )
        {
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return StoreAccessResult.Forbidden("无法识别当前员工", "USER_NOT_FOUND");
            }

            var relatedStoreCodes = !IsAdmin() || includeRelatedStoreCodes
                ? await GetRelatedStoreCodesAsync(userGuid)
                : new List<string>();
            if (IsAdmin())
            {
                return StoreAccessResult.Allowed(
                    string.IsNullOrWhiteSpace(requestedStoreCode)
                        ? new List<string>()
                        : new List<string> { requestedStoreCode.Trim() },
                    relatedStoreCodes
                );
            }

            var requested = requestedStoreCode?.Trim();
            if (!string.IsNullOrWhiteSpace(requested) && !relatedStoreCodes.Contains(requested, StringComparer.OrdinalIgnoreCase))
            {
                return StoreAccessResult.Forbidden("没有权限访问该分店", "FORBIDDEN_STORE");
            }

            return StoreAccessResult.Allowed(
                string.IsNullOrWhiteSpace(requested) ? relatedStoreCodes : new List<string> { requested },
                relatedStoreCodes
            );
        }

        private async Task<List<string>> GetRelatedStoreCodesAsync(string userGuid)
        {
            return await _db.Queryable<UserStore>()
                .InnerJoin<Store>((us, s) => us.StoreGUID == s.StoreGUID)
                .Where((us, s) => us.UserGUID == userGuid && !us.IsDeleted && !s.IsDeleted)
                .Select((us, s) => s.StoreCode)
                .ToListAsync();
        }

        private async Task PopulateScheduleDisplayFieldsAsync(List<AttendanceScheduleDto> schedules)
        {
            if (schedules.Count == 0)
            {
                return;
            }

            var userGuids = schedules.Select(item => item.UserGuid).Distinct().ToList();
            var storeCodes = schedules.Select(item => item.StoreCode).Distinct().ToList();
            var users = await _db.Queryable<User>()
                .Where(item => userGuids.Contains(item.UserGUID))
                .ToListAsync();
            var stores = await _db.Queryable<Store>()
                .Where(item => storeCodes.Contains(item.StoreCode))
                .ToListAsync();
            var userMap = users.ToDictionary(item => item.UserGUID, StringComparer.OrdinalIgnoreCase);
            var storeMap = stores.ToDictionary(item => item.StoreCode, StringComparer.OrdinalIgnoreCase);
            // 已批准请假覆盖到的班次标注为请假：不改排班状态，只在返回时附加，界面据此不计工时、不算缺卡。
            var minDate = schedules.Min(item => item.WorkDate.Date);
            var maxDate = schedules.Max(item => item.WorkDate.Date);
            var approvedLeaves = await _db.Queryable<AttendanceLeaveRequest>()
                .Where(item =>
                    !item.IsDeleted
                    && item.Status == "Approved"
                    && userGuids.Contains(item.UserGuid)
                    && item.StartDate <= maxDate
                    && item.EndDate >= minDate)
                .ToListAsync();

            foreach (var schedule in schedules)
            {
                var leave = approvedLeaves.FirstOrDefault(item =>
                    item.UserGuid.Equals(schedule.UserGuid, StringComparison.OrdinalIgnoreCase)
                    && item.StartDate.Date <= schedule.WorkDate.Date
                    && item.EndDate.Date >= schedule.WorkDate.Date);
                if (leave != null)
                {
                    schedule.LeaveType = leave.LeaveType;
                    schedule.LeaveGuid = leave.LeaveGuid;
                }

                if (userMap.TryGetValue(schedule.UserGuid, out var user))
                {
                    schedule.EmployeeName = string.IsNullOrWhiteSpace(user.FullName)
                        ? user.Username
                        : user.FullName;
                }

                if (storeMap.TryGetValue(schedule.StoreCode, out var store))
                {
                    schedule.StoreName = store.StoreName;
                }
            }
        }

        private async Task PopulateMinorEmploymentComplianceAsync(List<AttendanceScheduleDto> schedules)
        {
            foreach (var schedule in schedules)
                schedule.MinorCompliance = await EvaluateMinorEmploymentAsync(schedule.UserGuid, new AttendanceSchedule
                {
                    ScheduleGuid = schedule.ScheduleGuid, StoreCode = schedule.StoreCode, UserGuid = schedule.UserGuid,
                    WorkDate = schedule.WorkDate, StartTime = schedule.StartTime, EndTime = schedule.EndTime, Status = schedule.Status,
                });
        }

        private async Task<List<AttendanceStorePunchStateDto>> BuildRelatedStorePunchStatesAsync(
            List<AttendanceScheduleDto> scheduleDtos,
            List<AttendancePunch> punches,
            IReadOnlyCollection<string> relatedStoreCodes)
        {
            var stateScheduleDtos = relatedStoreCodes.Count == 0
                ? scheduleDtos
                : scheduleDtos.Where(item => relatedStoreCodes.Contains(
                    item.StoreCode,
                    StringComparer.OrdinalIgnoreCase)).ToList();
            var statePunches = relatedStoreCodes.Count == 0
                ? punches
                : punches.Where(item => relatedStoreCodes.Contains(
                    item.StoreCode,
                    StringComparer.OrdinalIgnoreCase)).ToList();
            // 同店可能有多个排班；异常必须按门店 any 聚合，不能被后一个已完成排班覆盖。
            var states = stateScheduleDtos
                .GroupBy(item => item.StoreCode, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var primary = group
                        .OrderByDescending(item => StorePunchStatePriority(item.ScheduleState))
                        .ThenBy(item => item.StartTime)
                        .First();
                    var hasMissingClockOut = group.Any(item => item.HasMissingClockOut);
                    var hasOpenSegment = group.Any(item => item.HasOpenSegment);
                    return new AttendanceStorePunchStateDto
                    {
                        StoreCode = group.Key,
                        StoreName = primary.StoreName,
                        ScheduleGuid = primary.ScheduleGuid,
                        State = hasMissingClockOut
                            ? "MissingClockOut"
                            : hasOpenSegment
                                ? "Working"
                                : primary.ScheduleState,
                        HasOpenSegment = hasOpenSegment,
                        HasMissingClockOut = hasMissingClockOut,
                    };
                })
                .ToList();
            var scheduledStores = states.Select(item => item.StoreCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var storeNames = relatedStoreCodes.Count == 0
                ? new List<Store>()
                : await _db.Queryable<Store>()
                    .Where(item => relatedStoreCodes.Contains(item.StoreCode))
                    .ToListAsync();
            var storeNameMap = storeNames.ToDictionary(
                item => item.StoreCode,
                item => item.StoreName,
                StringComparer.OrdinalIgnoreCase);
            foreach (var group in statePunches
                .Where(item => !scheduledStores.Contains(item.StoreCode))
                .GroupBy(item => item.StoreCode, StringComparer.OrdinalIgnoreCase))
            {
                var ordered = group.OrderBy(item => item.PunchTimeLocal).ToList();
                var hasOpenSegment = ordered.Count(item => item.PunchType == "ClockIn")
                    > ordered.Count(item => item.PunchType == "ClockOut");
                states.Add(new AttendanceStorePunchStateDto
                {
                    StoreCode = group.Key,
                    StoreName = storeNameMap.GetValueOrDefault(group.Key),
                    State = hasOpenSegment ? "Working" : "Completed",
                    HasOpenSegment = hasOpenSegment,
                });
            }
            return states;
        }

        private static int StorePunchStatePriority(string? state) => state switch
        {
            "MissingClockOut" => 5,
            "Working" => 4,
            "OnBreak" => 3,
            "Completed" => 2,
            _ => 1,
        };

        private async Task ReconcileDerivedApprovalsUnderEmployeeLocksAsync(
            IEnumerable<AttendanceSchedule> scheduleSnapshots)
        {
            foreach (var userSchedules in scheduleSnapshots
                .GroupBy(item => item.UserGuid, StringComparer.OrdinalIgnoreCase))
            {
                var scheduleGuids = userSchedules
                    .Select(item => item.ScheduleGuid)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (scheduleGuids.Count == 0)
                {
                    continue;
                }

                var mutationResource = AttendanceDailyMutationLock.BuildResource(
                    userSchedules.Key,
                    string.Empty,
                    default);
                await using var processLock = await AttendanceDailyMutationLock.AcquireProcessAsync(
                    mutationResource);
                await _db.Ado.BeginTranAsync();
                try
                {
                    await AttendanceDailyMutationLock.AcquireDatabaseAsync(_db, mutationResource);
                    // GET 也可能创建/取消审批，因此必须锁内重读排班、打卡和规则，不能复用入口快照。
                    var lockedSchedules = await _db.Queryable<AttendanceSchedule>()
                        .Where(item => !item.IsDeleted && scheduleGuids.Contains(item.ScheduleGuid))
                        .ToListAsync();
                    var activeSchedules = lockedSchedules
                        .Where(item => item.Status == "Active")
                        .ToList();
                    var activeScheduleGuids = activeSchedules
                        .Select(item => item.ScheduleGuid)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var staleScheduleGuid in scheduleGuids
                        .Where(scheduleGuid => !activeScheduleGuids.Contains(scheduleGuid)))
                    {
                        await CancelScheduleDerivedApprovalsAsync(
                            staleScheduleGuid,
                            "排班已失效，相关待审考勤已自动取消");
                    }

                    if (activeSchedules.Count > 0)
                    {
                        var activeGuids = activeSchedules.Select(item => item.ScheduleGuid).ToList();
                        var lockedPunches = await _db.Queryable<AttendancePunch>()
                            .Where(item => !item.IsDeleted
                                && item.ScheduleGuid != null
                                && activeGuids.Contains(item.ScheduleGuid))
                            .ToListAsync();
                        var settings = await GetOrCreateSettingsModelAsync();
                        var segmentLimitMap = await ResolveSegmentLimitMapAsync(activeSchedules);
                        foreach (var schedule in activeSchedules)
                        {
                            var timeZone = await ResolveStoreTimeZoneAsync(schedule.StoreCode, null);
                            var session = AttendanceWorkSessionCalculator.Calculate(
                                schedule,
                                lockedPunches,
                                segmentLimitMap.GetValueOrDefault(schedule.UserGuid, 2),
                                ConvertUtcToStoreLocal(_timeProvider.GetUtcNow().UtcDateTime, timeZone),
                                settings.EarlyLeaveGraceMinutes,
                                settings.LateGraceMinutes);
                            await ReconcileOvertimeApprovalAsync(schedule, session);
                            await ReconcileFinalPunchApprovalsAsync(schedule, session, settings);
                            await ReconcileMissingClockOutApprovalAsync(schedule, session);
                        }
                    }

                    await _db.Ado.CommitTranAsync();
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }
            }
        }

        private async Task PopulateWorkSessionFieldsAsync(
            List<AttendanceScheduleDto> scheduleDtos,
            List<AttendanceSchedule> schedules,
            List<AttendancePunch>? knownPunches = null,
            List<AttendancePunchDto>? punchDtos = null,
            bool reconcileDerivedApprovals = true)
        {
            if (schedules.Count == 0)
            {
                return;
            }

            if (reconcileDerivedApprovals)
            {
                await ReconcileDerivedApprovalsUnderEmployeeLocksAsync(schedules);
                // 协调已用锁内重读后的状态完成；下方只投影 DTO，不能再用入口快照写审批。
                reconcileDerivedApprovals = false;
            }

            var scheduleGuids = schedules.Select(item => item.ScheduleGuid).Distinct().ToList();
            var punches = knownPunches ?? await _db.Queryable<AttendancePunch>()
                .Where(item => !item.IsDeleted && item.ScheduleGuid != null && scheduleGuids.Contains(item.ScheduleGuid))
                .ToListAsync();
            var settings = await GetOrCreateSettingsModelAsync();
            var dtoMap = scheduleDtos.ToDictionary(item => item.ScheduleGuid, StringComparer.OrdinalIgnoreCase);
            var segmentLimitMap = await ResolveSegmentLimitMapAsync(schedules);
            var storeCodes = schedules.Select(item => item.StoreCode).Distinct().ToList();
            var stores = await _db.Queryable<Store>()
                .Where(item => !item.IsDeleted && storeCodes.Contains(item.StoreCode))
                .ToListAsync();
            var timeZoneMap = stores.ToDictionary(
                item => item.StoreCode,
                item => ResolveStoreTimeZoneFromStore(item) ?? DefaultStoreTimeZone,
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<AttendanceApproval>>? overtimeApprovalMap = null;
            if (!reconcileDerivedApprovals)
            {
                var overtimeApprovals = await _db.Queryable<AttendanceApproval>()
                    .Where(item =>
                        !item.IsDeleted
                        && item.SourceType == "Overtime"
                        && scheduleGuids.Contains(item.SourceGuid))
                    .OrderByDescending(item => item.CreatedAt)
                    .ToListAsync();
                overtimeApprovalMap = overtimeApprovals
                    .GroupBy(item => item.SourceGuid, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.ToList(),
                        StringComparer.OrdinalIgnoreCase);
            }
            var punchDtoMap = punchDtos?.ToDictionary(
                item => item.PunchGuid,
                StringComparer.OrdinalIgnoreCase);

            foreach (var schedule in schedules)
            {
                if (!dtoMap.TryGetValue(schedule.ScheduleGuid, out var dto))
                {
                    continue;
                }

                var segmentLimit = segmentLimitMap.GetValueOrDefault(schedule.UserGuid, 2);
                var timeZone = timeZoneMap.GetValueOrDefault(schedule.StoreCode, DefaultStoreTimeZone);
                var nowLocal = ConvertUtcToStoreLocal(_timeProvider.GetUtcNow().UtcDateTime, timeZone);
                var session = AttendanceWorkSessionCalculator.Calculate(
                    schedule,
                    punches,
                    segmentLimit,
                    nowLocal,
                    settings.EarlyLeaveGraceMinutes,
                    settings.LateGraceMinutes);
                dto.ScheduleState = session.ScheduleState;
                dto.SegmentLimit = session.SegmentLimit;
                dto.CompletedSegmentCount = session.CompletedSegmentCount;
                dto.WorkedMinutes = session.WorkedMinutes;
                dto.BreakMinutes = session.BreakMinutes;
                dto.HasOpenSegment = session.HasOpenSegment;
                dto.HasMissingClockOut = session.HasMissingClockOut;
                dto.EarlyOvertimeMinutes = session.EarlyOvertimeMinutes;
                dto.LateOvertimeMinutes = session.LateOvertimeMinutes;
                dto.CandidateOvertimeMinutes = session.CandidateOvertimeMinutes;
                dto.Segments = session.Segments;
                var overtimeApprovals = overtimeApprovalMap != null
                    ? overtimeApprovalMap.GetValueOrDefault(schedule.ScheduleGuid) ?? new List<AttendanceApproval>()
                    : await _db.Queryable<AttendanceApproval>()
                        .Where(item =>
                            !item.IsDeleted
                            && item.SourceType == "Overtime"
                            && item.SourceGuid == schedule.ScheduleGuid)
                        .OrderByDescending(item => item.CreatedAt)
                        .ToListAsync();
                var approvedTotal = overtimeApprovals
                    .Where(item => item.ReviewStatus == "Approved")
                    .Sum(item => item.ApprovedOvertimeMinutes ?? 0);
                dto.ApprovedOvertimeMinutes = Math.Min(
                    session.CandidateOvertimeMinutes,
                    approvedTotal);
                if (overtimeApprovals.Any(item => item.ReviewStatus == "Pending"))
                {
                    dto.OvertimeApprovalStatus = "Pending";
                }
                else if (dto.ApprovedOvertimeMinutes > 0)
                {
                    dto.OvertimeApprovalStatus = "Approved";
                }
                else
                {
                    dto.OvertimeApprovalStatus = overtimeApprovals.FirstOrDefault()?.ReviewStatus;
                }

                if (punchDtoMap == null)
                {
                    continue;
                }
                foreach (var segment in session.Segments)
                {
                    ApplySegmentMetadata(segment.ClockIn, segment, punchDtoMap);
                    ApplySegmentMetadata(segment.ClockOut, segment, punchDtoMap);
                }
            }
        }

        private async Task<Dictionary<string, int>> ResolveSegmentLimitMapAsync(
            List<AttendanceSchedule> schedules)
        {
            var userGuids = schedules.Select(item => item.UserGuid).Distinct().ToList();
            var roleLinks = await _db.Queryable<UserRole>()
                .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                .Where((userRole, role) =>
                    !userRole.IsDeleted
                    && !role.IsDeleted
                    && role.IsActive
                    && userGuids.Contains(userRole.UserGUID))
                .Select((userRole, role) => new { userRole.UserGUID, role.RoleName })
                .ToListAsync();
            var managerUsers = roleLinks
                .Where(item => Permissions.StoreManagerRoleNames.Contains(
                    item.RoleName,
                    StringComparer.OrdinalIgnoreCase))
                .Select(item => item.UserGUID)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var managedUsers = await _db.Queryable<UserStore>()
                .InnerJoin<Store>((userStore, store) => userStore.StoreGUID == store.StoreGUID)
                .Where((userStore, store) =>
                    !userStore.IsDeleted
                    && !store.IsDeleted
                    && userStore.IsPrimary
                    && userGuids.Contains(userStore.UserGUID))
                .Select((userStore, store) => userStore.UserGUID)
                .ToListAsync();
            var managedUserSet = managedUsers.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return userGuids.ToDictionary(
                userGuid => userGuid,
                userGuid => managerUsers.Contains(userGuid) && managedUserSet.Contains(userGuid) ? 3 : 2,
                StringComparer.OrdinalIgnoreCase);
        }

        private static void ApplySegmentMetadata(
            AttendancePunchDto? projectedPunch,
            AttendanceShiftSegmentDto segment,
            Dictionary<string, AttendancePunchDto> punchDtoMap)
        {
            if (projectedPunch == null
                || !punchDtoMap.TryGetValue(projectedPunch.PunchGuid, out var punch))
            {
                return;
            }

            punch.SegmentIndex = segment.SegmentIndex;
            punch.SegmentStatus = segment.Status;
            punch.IsBreakBoundary = punch.PunchType == "ClockOut" && segment.Status == "Break";
            punch.Status = projectedPunch.Status;
            punch.EarlyArrivalMinutes = projectedPunch.EarlyArrivalMinutes;
            punch.LateMinutes = projectedPunch.LateMinutes;
            punch.EarlyLeaveMinutes = projectedPunch.EarlyLeaveMinutes;
            punch.LateDepartureMinutes = projectedPunch.LateDepartureMinutes;
        }

        private async Task PopulateApprovalDisplayFieldsAsync(List<AttendanceApprovalDto> approvals)
        {
            if (approvals.Count == 0)
            {
                return;
            }

            var userGuids = approvals.Select(item => item.ApplicantUserGuid).Distinct().ToList();
            var storeCodes = approvals.Select(item => item.StoreCode).Distinct().ToList();
            var punchGuids = approvals
                .Where(item => item.SourceType.Equals("Punch", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.SourceGuid)
                .Distinct()
                .ToList();
            var leaveGuids = approvals
                .Where(item => item.SourceType.Equals("Leave", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.SourceGuid)
                .Distinct()
                .ToList();
            var adjustmentGuids = approvals
                .Where(item => item.SourceType.Equals("PunchAdjustment", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.SourceGuid)
                .Distinct()
                .ToList();
            var scheduleGuids = approvals
                .Where(item => item.SourceType.Equals("Overtime", StringComparison.OrdinalIgnoreCase)
                    || item.SourceType.Equals("MissingClockOut", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.SourceGuid)
                .Distinct()
                .ToList();

            var users = await _db.Queryable<User>()
                .Where(item => userGuids.Contains(item.UserGUID))
                .ToListAsync();
            var stores = await _db.Queryable<Store>()
                .Where(item => storeCodes.Contains(item.StoreCode))
                .ToListAsync();
            var punches = punchGuids.Count == 0
                ? new List<AttendancePunch>()
                : await _db.Queryable<AttendancePunch>()
                    .Where(item => punchGuids.Contains(item.PunchGuid))
                    .ToListAsync();
            var leaves = leaveGuids.Count == 0
                ? new List<AttendanceLeaveRequest>()
                : await _db.Queryable<AttendanceLeaveRequest>()
                    .Where(item => leaveGuids.Contains(item.LeaveGuid))
                    .ToListAsync();
            var adjustments = adjustmentGuids.Count == 0
                ? new List<AttendancePunchAdjustment>()
                : await _db.Queryable<AttendancePunchAdjustment>()
                    .Where(item => adjustmentGuids.Contains(item.AdjustmentGuid))
                    .ToListAsync();
            var originalPunchGuids = adjustments
                .Where(item => !string.IsNullOrWhiteSpace(item.OriginalPunchGuid))
                .Select(item => item.OriginalPunchGuid!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // 原卡即使已被补卡覆盖或软删除，审批详情仍需展示其历史本地时间。
            var originalPunches = originalPunchGuids.Count == 0
                ? new List<AttendancePunch>()
                : await _db.Queryable<AttendancePunch>()
                    .Where(item => originalPunchGuids.Contains(item.PunchGuid))
                    .ToListAsync();
            var schedules = scheduleGuids.Count == 0
                ? new List<AttendanceSchedule>()
                : await _db.Queryable<AttendanceSchedule>()
                    .Where(item => scheduleGuids.Contains(item.ScheduleGuid))
                    .ToListAsync();

            var userMap = users.ToDictionary(item => item.UserGUID, StringComparer.OrdinalIgnoreCase);
            var storeMap = stores.ToDictionary(item => item.StoreCode, StringComparer.OrdinalIgnoreCase);
            var punchMap = punches.ToDictionary(item => item.PunchGuid, StringComparer.OrdinalIgnoreCase);
            var leaveMap = leaves.ToDictionary(item => item.LeaveGuid, StringComparer.OrdinalIgnoreCase);
            var adjustmentMap = adjustments.ToDictionary(
                item => item.AdjustmentGuid,
                StringComparer.OrdinalIgnoreCase);
            var originalPunchMap = originalPunches.ToDictionary(
                item => item.PunchGuid,
                StringComparer.OrdinalIgnoreCase);
            var scheduleMap = schedules.ToDictionary(
                item => item.ScheduleGuid,
                StringComparer.OrdinalIgnoreCase);

            foreach (var approval in approvals)
            {
                if (userMap.TryGetValue(approval.ApplicantUserGuid, out var user))
                {
                    approval.EmployeeName = string.IsNullOrWhiteSpace(user.FullName)
                        ? user.Username
                        : user.FullName;
                }

                if (storeMap.TryGetValue(approval.StoreCode, out var store))
                {
                    approval.StoreName = store.StoreName;
                }

                if (approval.SourceType.Equals("Punch", StringComparison.OrdinalIgnoreCase)
                    && punchMap.TryGetValue(approval.SourceGuid, out var punch))
                {
                    approval.WorkDate = punch.WorkDate;
                    approval.Title = punch.PunchType;
                    approval.Detail = $"{punch.PunchType} · {punch.Status}";
                }
                else if (approval.SourceType.Equals("Leave", StringComparison.OrdinalIgnoreCase)
                    && leaveMap.TryGetValue(approval.SourceGuid, out var leave))
                {
                    approval.WorkDate = leave.StartDate;
                    approval.Title = leave.LeaveType;
                    approval.Detail = $"{leave.StartDate:yyyy-MM-dd} - {leave.EndDate:yyyy-MM-dd}"
                        + (string.IsNullOrWhiteSpace(leave.Reason) ? string.Empty : $" · {leave.Reason}");
                }
                else if (approval.SourceType.Equals("PunchAdjustment", StringComparison.OrdinalIgnoreCase)
                    && adjustmentMap.TryGetValue(approval.SourceGuid, out var adjustment))
                {
                    approval.WorkDate = adjustment.RequestedPunchTimeLocal.Date;
                    approval.Title = "补卡申请";
                    approval.Detail = $"{adjustment.PunchType} · {adjustment.RequestedPunchTimeLocal:yyyy-MM-dd HH:mm} · {adjustment.Reason}";
                    approval.Adjustment = ToDto(
                        adjustment,
                        originalPunchMap.GetValueOrDefault(adjustment.OriginalPunchGuid ?? string.Empty));
                }
                else if (approval.SourceType.Equals("Overtime", StringComparison.OrdinalIgnoreCase))
                {
                    if (scheduleMap.TryGetValue(approval.SourceGuid, out var schedule))
                    {
                        approval.WorkDate = schedule.WorkDate;
                    }
                    approval.Title = "加班审批";
                    approval.Detail = $"候选 {approval.CandidateOvertimeMinutes ?? 0} 分钟";
                }
                else if (approval.SourceType.Equals("MissingClockOut", StringComparison.OrdinalIgnoreCase))
                {
                    if (scheduleMap.TryGetValue(approval.SourceGuid, out var schedule))
                    {
                        approval.WorkDate = schedule.WorkDate;
                    }
                    approval.Title = "漏下班待处理";
                    approval.Detail = "排班结束后仍存在未闭合班段";
                }
                else
                {
                    approval.Title = approval.SourceType;
                }
            }
        }

        private string ResolveCurrentUserGuid()
        {
            var user = _httpContextAccessor.HttpContext?.User;
            return user?.FindFirst("userGuid")?.Value
                ?? user?.FindFirst("userId")?.Value
                ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? _currentUserService.GetCurrentUserGuid();
        }

        private bool IsAdmin()
        {
            var user = _httpContextAccessor.HttpContext?.User;
            return user?.IsInRole("Admin") == true || user?.IsInRole("管理员") == true;
        }

        private static ValidationResult ValidateSchedulePayload(
            string storeCode,
            string userGuid,
            TimeSpan startTime,
            TimeSpan endTime
        )
        {
            if (string.IsNullOrWhiteSpace(storeCode))
            {
                return ValidationResult.Error("分店代码不能为空", "STORE_REQUIRED");
            }

            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return ValidationResult.Error("员工不能为空", "USER_REQUIRED");
            }

            if (startTime >= endTime)
            {
                return ValidationResult.Error("排班结束时间必须晚于开始时间", "INVALID_TIME_RANGE");
            }

            return ValidationResult.OK();
        }

        // 用餐次数覆盖值只允许 0–3 次；null 表示按时长自动，不受限制。
        private const int MaxMealBreakCount = 3;

        private static bool IsValidMealBreakCount(int? mealBreakCount) =>
            mealBreakCount is null or (>= 0 and <= MaxMealBreakCount);

        private static ApiResponse<AttendanceScheduleDto> InvalidMealBreakCountError() =>
            ApiResponse<AttendanceScheduleDto>.Error("用餐次数必须在 0 到 3 之间", "INVALID_MEAL_BREAK_COUNT");

        private static ValidationResult ValidateHolidayBatchPayload(
            List<string> storeCodes,
            DateTime holidayDate,
            string holidayName
        )
        {
            if (storeCodes.Count == 0)
            {
                return ValidationResult.Error("请选择至少一个分店", "STORE_REQUIRED");
            }

            if (holidayDate == default)
            {
                return ValidationResult.Error("假期日期不能为空", "HOLIDAY_DATE_REQUIRED");
            }

            if (string.IsNullOrWhiteSpace(holidayName))
            {
                return ValidationResult.Error("假期名称不能为空", "HOLIDAY_NAME_REQUIRED");
            }

            return ValidationResult.OK();
        }

        private static ValidationResult ValidateManagedLeaveRequest(
            CreateManagedAttendanceLeaveRequestDto request
        )
        {
            if (string.IsNullOrWhiteSpace(request.StoreCode))
            {
                return ValidationResult.Error("分店代码不能为空", "STORE_REQUIRED");
            }

            if (string.IsNullOrWhiteSpace(request.UserGuid))
            {
                return ValidationResult.Error("员工不能为空", "USER_REQUIRED");
            }

            if (request.StartDate == default || request.EndDate == default)
            {
                return ValidationResult.Error("请假日期不能为空", "DATE_REQUIRED");
            }

            if (request.StartDate.Date > request.EndDate.Date)
            {
                return ValidationResult.Error("结束日期不能早于开始日期", "INVALID_DATE_RANGE");
            }

            if (request.StartTime.HasValue && request.EndTime.HasValue && request.StartTime >= request.EndTime)
            {
                return ValidationResult.Error("请假结束时间必须晚于开始时间", "INVALID_TIME_RANGE");
            }

            var leaveType = NormalizeManagedLeaveType(request.LeaveType);
            if (leaveType is not "AnnualLeave" and not "SickLeave")
            {
                return ValidationResult.Error("管理端仅支持创建年假或病假", "INVALID_LEAVE_TYPE");
            }

            if (leaveType == "SickLeave" && string.IsNullOrWhiteSpace(request.AttachmentUrl))
            {
                return ValidationResult.Error("病假必须上传附件", "ATTACHMENT_REQUIRED");
            }

            if (leaveType == "SickLeave" && !IsManagedLeaveAttachmentUrl(request.AttachmentUrl))
            {
                return ValidationResult.Error("病假附件地址无效", "INVALID_ATTACHMENT_URL");
            }

            return ValidationResult.OK();
        }

        private static List<string> NormalizeStoreCodes(IEnumerable<string>? storeCodes)
        {
            return (storeCodes ?? Enumerable.Empty<string>())
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void ApplyHolidayValues(
            AttendanceStoreHoliday model,
            string holidayName,
            string businessStatus,
            TimeSpan? openTime,
            TimeSpan? closeTime,
            bool isPaidHoliday,
            string? remark,
            DateTime updatedAt,
            string? updatedBy
        )
        {
            model.HolidayName = holidayName;
            model.BusinessStatus = businessStatus;
            model.OpenTime = openTime;
            model.CloseTime = closeTime;
            model.IsPaidHoliday = isPaidHoliday;
            model.Remark = remark;
            model.UpdatedAt = updatedAt;
            model.UpdatedBy = updatedBy;
        }

        private async Task<string> ResolveStoreTimeZoneAsync(string? storeCode, string? fallbackTimeZone)
        {
            // 客户端请求字段只为兼容旧 API 保留，考勤日界必须以门店配置或门店资料为准。
            var resolved = await ResolveStoreTimeZoneFromStoreAsync(storeCode);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            return DefaultStoreTimeZone;
        }

        private async Task<string?> ResolveStoreTimeZoneFromStoreAsync(string? storeCode)
        {
            var normalizedStoreCode = storeCode?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedStoreCode))
            {
                return null;
            }

            var store = await _db.Queryable<Store>()
                .FirstAsync(item => !item.IsDeleted && item.StoreCode == normalizedStoreCode);
            return store == null ? null : ResolveStoreTimeZoneFromStore(store);
        }

        private string? ResolveStoreTimeZoneFromStore(Store store)
        {
            if (!string.IsNullOrWhiteSpace(store.TimeZoneId))
            {
                if (StoreTimeZonePolicy.TryNormalize(store.TimeZoneId, out var configuredTimeZone))
                {
                    return configuredTimeZone;
                }

                // 历史数据可能绕过 CRUD 写入；不能让无效配置覆盖既有的地址推导和 Sydney 默认值。
                _logger.LogWarning(
                    "门店 {StoreCode} 配置了不支持的考勤时区 {TimeZoneId}，将按门店资料回退推导",
                    store.StoreCode,
                    store.TimeZoneId);
            }

            var postcode = PublicHolidaySyncHelper.ExtractPostcodeFromAddress(store.Address);
            var jurisdiction = PublicHolidaySyncHelper.ResolveJurisdictionFromPostcode(postcode);
            if (jurisdiction == "QLD")
            {
                return "Australia/Brisbane";
            }

            if (jurisdiction == "NSW")
            {
                return DefaultStoreTimeZone;
            }

            var storeText = $"{store.StoreCode} {store.StoreName} {store.Address}".ToUpperInvariant();
            if (storeText.Contains("BRI") || storeText.Contains("BRISBANE") || storeText.Contains("QLD") || storeText.Contains("QUEENSLAND"))
            {
                return "Australia/Brisbane";
            }

            if (storeText.Contains("MEL") || storeText.Contains("MELBOURNE") || storeText.Contains("VIC") || storeText.Contains("VICTORIA"))
            {
                return "Australia/Melbourne";
            }

            if (storeText.Contains("SYD") || storeText.Contains("SYDNEY") || storeText.Contains("NSW") || storeText.Contains("NEW SOUTH WALES"))
            {
                return DefaultStoreTimeZone;
            }

            return null;
        }

        private static string? NormalizeOptional(string? value, int maxLength)
        {
            var normalized = value?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return normalized.Length > maxLength ? normalized[..maxLength] : normalized;
        }

        private static DateTime? NormalizeUtc(DateTime? value)
        {
            return value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
        }

        private static DateTime ConvertUtcToStoreLocal(DateTime utc, string storeTimeZone)
        {
            try
            {
                var timezone = TimeZoneInfo.FindSystemTimeZoneById(storeTimeZone);
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timezone);
            }
            catch (TimeZoneNotFoundException)
            {
                var timezone = TimeZoneInfo.FindSystemTimeZoneById(DefaultStoreTimeZone);
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timezone);
            }
            catch (InvalidTimeZoneException)
            {
                var timezone = TimeZoneInfo.FindSystemTimeZoneById(DefaultStoreTimeZone);
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timezone);
            }
        }

        private static DateTime ConvertStoreLocalToUtc(DateTime local, string storeTimeZone)
        {
            var localUnspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            try
            {
                var timezone = TimeZoneInfo.FindSystemTimeZoneById(storeTimeZone);
                return TimeZoneInfo.ConvertTimeToUtc(localUnspecified, timezone);
            }
            catch (TimeZoneNotFoundException)
            {
                var timezone = TimeZoneInfo.FindSystemTimeZoneById(DefaultStoreTimeZone);
                return TimeZoneInfo.ConvertTimeToUtc(localUnspecified, timezone);
            }
            catch (InvalidTimeZoneException)
            {
                var timezone = TimeZoneInfo.FindSystemTimeZoneById(DefaultStoreTimeZone);
                return TimeZoneInfo.ConvertTimeToUtc(localUnspecified, timezone);
            }
        }

        private static string NormalizePunchType(string? punchType) =>
            punchType?.Trim().Equals("ClockOut", StringComparison.OrdinalIgnoreCase) == true
                ? "ClockOut"
                : "ClockIn";

        private static string NormalizeScheduleStatus(string? status, string defaultStatus)
        {
            var normalized = status?.Trim();
            return normalized switch
            {
                "Draft" or "Active" or "Cancelled" => normalized,
                _ => defaultStatus,
            };
        }

        private static string NormalizeBusinessStatus(string? status)
        {
            var normalized = status?.Trim();
            return normalized is "Closed" or "Partial" ? normalized : "Open";
        }

        private static string NormalizeManagedLeaveType(string? leaveType)
        {
            var normalized = leaveType?.Trim();
            return normalized switch
            {
                "AnnualLeave" => "AnnualLeave",
                "SickLeave" => "SickLeave",
                _ => normalized ?? string.Empty,
            };
        }

        private static bool IsManagedLeaveAttachmentUrl(string? attachmentUrl)
        {
            if (!Uri.TryCreate(attachmentUrl?.Trim(), UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.AbsolutePath.Contains(
                "/attendance/leave-attachments/",
                StringComparison.OrdinalIgnoreCase
            );
        }

        private static string SanitizeFileName(string fileName)
        {
            var extension = Path.GetExtension(fileName);
            var extensionChars = extension
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '.')
                .ToArray();
            var safeExtension = new string(extensionChars);
            var safeBaseName = new string(
                Path.GetFileNameWithoutExtension(fileName)
                    .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
                    .ToArray()
            );

            while (safeBaseName.Contains("--", StringComparison.Ordinal))
            {
                safeBaseName = safeBaseName.Replace("--", "-", StringComparison.Ordinal);
            }

            safeBaseName = safeBaseName.Trim('-');
            if (string.IsNullOrWhiteSpace(safeBaseName))
            {
                safeBaseName = "attachment";
            }

            return $"{safeBaseName}{safeExtension}";
        }

        private static DateTime GetWeekStart(DateTime date)
        {
            var diff = ((int)date.DayOfWeek + 6) % 7;
            return date.Date.AddDays(-diff);
        }

        /// <summary>
        /// 读取当前不可变档案版本与关联门店班次。任何资料缺失都会成为提醒，绝不回写或阻断排班/打卡。
        /// </summary>
        private async Task<MinorEmploymentComplianceEvaluationDto?> EvaluateMinorEmploymentAsync(
            string userGuid,
            AttendanceSchedule? changedSchedule = null,
            AttendancePunch? newPunch = null,
            DateTime? referenceDate = null)
        {
            try
            {
                return await EvaluateMinorEmploymentCoreAsync(userGuid, changedSchedule, newPunch, referenceDate);
            }
            catch (Exception exception)
            {
                // 合规资料反序列化、迁移或规则计算故障只能降级为无结果，绝不能把已保存的排班/打卡变成失败响应。
                _logger.LogWarning(exception, "未成年用工提醒计算失败；核心考勤动作已保留。用户 {UserGuid}", userGuid);
                return null;
            }
        }

        private async Task<MinorEmploymentComplianceEvaluationDto?> EvaluateMinorEmploymentCoreAsync(
            string userGuid,
            AttendanceSchedule? changedSchedule,
            AttendancePunch? newPunch,
            DateTime? referenceDate)
        {
            var profileRow = await _db.Queryable<EmployeeMinorCompliance>()
                .Where(item => !item.IsDeleted && item.UserGUID == userGuid)
                .OrderBy(item => item.Version, OrderByType.Desc)
                .FirstAsync();
            if (profileRow == null)
            {
                var birthday = await _db.Queryable<EmployeeProfile>().Where(item => !item.IsDeleted && item.UserGUID == userGuid).Select(item => item.Birthday).FirstAsync();
                var reference = referenceDate ?? changedSchedule?.WorkDate ?? newPunch?.WorkDate ?? DateTime.Today;
                if (birthday.HasValue && birthday.Value.AddYears(18) <= reference) return null;
                return MapMinorEvaluation(MinorEmploymentComplianceCalculator.Evaluate(new()
                {
                    WorkItems = changedSchedule == null ? [] : [ToMinorWorkItem(changedSchedule)],
                }));
            }

            var school = DeserializeMinor<EmployeeMinorSchoolCalendarDto>(profileRow.SchoolCalendarJson);
            var otherWork = DeserializeMinor<EmployeeMinorOtherWorkDto>(profileRow.OtherWorkJson);
            var commute = DeserializeMinor<EmployeeMinorCommuteDto>(profileRow.CommuteJson);
            var relatedStores = await GetRelatedStoreCodesAsync(userGuid);
            var anchorDate = referenceDate ?? changedSchedule?.WorkDate ?? newPunch?.WorkDate ?? DateTime.Today;
            var sunday = anchorDate.Date.AddDays(-(int)anchorDate.DayOfWeek);
            var weekEnd = sunday.AddDays(7);
            // 多取目标周前后各一天，仅供跨周的同雇主 12 小时间隔判断；周累计仍严格以 sunday..weekEnd 计算。
            var boundaryStart = sunday.AddDays(-1);
            var boundaryEnd = weekEnd.AddDays(1);
            var schedules = await _db.Queryable<AttendanceSchedule>()
                .Where(item => !item.IsDeleted && item.UserGuid == userGuid
                    && relatedStores.Contains(item.StoreCode)
                    && item.Status != "Cancelled"
                    && item.WorkDate >= boundaryStart && item.WorkDate < boundaryEnd)
                .ToListAsync();
            if (changedSchedule != null)
            {
                schedules.RemoveAll(item => !string.IsNullOrWhiteSpace(changedSchedule.ScheduleGuid)
                    && item.ScheduleGuid == changedSchedule.ScheduleGuid);
                schedules.Add(changedSchedule);
            }

            var items = schedules.Select(ToMinorWorkItem).ToList();
            if (otherWork?.PlannedIntervals != null)
            {
                foreach (var interval in otherWork.PlannedIntervals.Where(interval => IntervalTouchesWindow(interval, boundaryStart, boundaryEnd, school?.TimeZoneId)))
                    AddExternalWorkItem(items, interval, school?.TimeZoneId);
            }
            var actualWork = await BuildActualWorkItemsAsync(userGuid, relatedStores, boundaryStart, boundaryEnd, newPunch);
            if (actualWork.Count > 0)
            {
                // 已完成的实际段替换对应计划；进行中段拆成“已工作 + 同一排班剩余预测”，避免漏掉尚未发生的尾段。
                var ongoingTailPlans = new List<MinorEmploymentWorkItem>();
                foreach (var actual in actualWork.Where(item => item.IsOngoing && !string.IsNullOrWhiteSpace(item.ScheduleGuid)))
                {
                    var scheduled = items.FirstOrDefault(item => string.Equals(item.EmployerId, "HB", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(item.ScheduleGuid, actual.ScheduleGuid, StringComparison.OrdinalIgnoreCase));
                    if (scheduled != null && actual.EndLocal < scheduled.EndLocal)
                        ongoingTailPlans.Add(new MinorEmploymentWorkItem
                        {
                            EmployerId = "HB", ScheduleGuid = scheduled.ScheduleGuid, WorkDate = scheduled.WorkDate,
                            StartLocal = actual.EndLocal, EndLocal = scheduled.EndLocal, IsTimeKnown = true,
                        });
                }
                items.RemoveAll(item => string.Equals(item.EmployerId, "HB", StringComparison.OrdinalIgnoreCase)
                    && actualWork.Any(actual => !string.IsNullOrWhiteSpace(actual.ScheduleGuid)
                        ? string.Equals(item.ScheduleGuid, actual.ScheduleGuid, StringComparison.OrdinalIgnoreCase)
                        : item.StartLocal < actual.EndLocal && item.EndLocal > actual.StartLocal));
                items.AddRange(actualWork);
                items.AddRange(ongoingTailPlans);
            }
            if (otherWork?.ActualIntervals?.Count > 0)
            {
                // 外部实际申报只取目标窗口并替换时间重叠的计划，避免与未来或下月计划重复累计。
                foreach (var interval in otherWork.ActualIntervals.Where(interval => IntervalTouchesWindow(interval, boundaryStart, boundaryEnd, school?.TimeZoneId)))
                {
                    var actual = ToExternalWorkItem(interval, school?.TimeZoneId, "external:actual");
                    if (actual == null) continue;
                    items.RemoveAll(item => item.EmployerId?.StartsWith("external", StringComparison.OrdinalIgnoreCase) == true
                        && item.StartLocal < actual.EndLocal && item.EndLocal > actual.StartLocal);
                    items.Add(actual);
                }
            }
            else if (otherWork?.HasOtherWork == true && otherWork.Employers.Count > 0 && otherWork.PlannedIntervals.Count == 0)
            {
                foreach (var employer in otherWork.Employers)
                    foreach (var day in Enumerable.Range(0, 7).Select(offset => sunday.AddDays(offset)))
                        if (employer.WeeklyHours.TryGetValue(day.DayOfWeek, out var hours) && hours > 0)
                            items.Add(new MinorEmploymentWorkItem { EmployerId = $"external:weekly:{employer.CompanyName ?? employer.TradingName ?? "unknown"}", WorkDate = day, StartLocal = day, EndLocal = day.AddMinutes((double)hours * 60), ActualWorkedMinutes = (int)Math.Round(hours * 60m), IsTimeKnown = false });
            }

            var input = new MinorEmploymentComplianceInput
            {
                Profile = new MinorEmploymentProfile
                {
                    DateOfBirth = profileRow.DateOfBirth,
                    WorkState = profileRow.StateCode,
                    IsQldSchoolAgedChild = ResolveQldSchoolAgedChild(profileRow, anchorDate),
                    ConsentStatus = profileRow.GuardianSignedAtUtc.HasValue ? "Signed" : profileRow.Status,
                    ReviewStatus = profileRow.Status,
                    AwardCode = null,
                    ExternalWorkKnown = otherWork is not { HoursUnknown: true }
                        && (otherWork is not { HasOtherWork: true }
                            || otherWork.PlannedIntervals.Count > 0 || otherWork.ActualIntervals.Count > 0 || otherWork.Employers.Any(item => item.WeeklyHours.Count > 0)),
                    CommuteFromSchoolMinutes = commute?.AfterSchoolToStoreMinutes,
                    HomewardMinutes = commute?.HomewardMinutes,
                    LatestTransportLocalTime = ParseMinorTime(commute?.LatestTransportLocalTime),
                    LatestWorkEndLocalTime = ParseMinorTime(commute?.LatestWorkEndLocalTime),
                    RequiredSchoolDays = BuildSchoolDays(school, boundaryStart, boundaryEnd),
                },
                AssessmentDate = anchorDate,
                TargetWeekStart = sunday,
                WorkItems = items,
            };
            return MapMinorEvaluation(MinorEmploymentComplianceCalculator.Evaluate(input));
        }

        private async Task<List<MinorEmploymentWorkItem>> BuildActualWorkItemsAsync(
            string userGuid, IReadOnlyCollection<string> stores, DateTime from, DateTime to, AttendancePunch? newPunch)
        {
            var punches = await _db.Queryable<AttendancePunch>()
                .Where(item => !item.IsDeleted && item.UserGuid == userGuid && stores.Contains(item.StoreCode)
                    && item.WorkDate >= from && item.WorkDate < to)
                .OrderBy(item => item.PunchTimeUtc).ToListAsync();
            if (newPunch != null && !punches.Any(item => item.PunchGuid == newPunch.PunchGuid)) punches.Add(newPunch);
            var results = new List<MinorEmploymentWorkItem>();
            foreach (var group in punches.GroupBy(item => item.StoreCode, StringComparer.OrdinalIgnoreCase))
            {
                AttendancePunch? clockIn = null;
                foreach (var punch in group.OrderBy(item => item.PunchTimeUtc))
                {
                    if (punch.PunchType.Equals("ClockIn", StringComparison.OrdinalIgnoreCase)) { clockIn = punch; continue; }
                    if (!punch.PunchType.Equals("ClockOut", StringComparison.OrdinalIgnoreCase) || clockIn == null || punch.PunchTimeUtc <= clockIn.PunchTimeUtc) continue;
                    results.Add(new MinorEmploymentWorkItem { EmployerId = "HB", ScheduleGuid = clockIn.ScheduleGuid, WorkDate = clockIn.WorkDate, StartLocal = clockIn.PunchTimeLocal, EndLocal = punch.PunchTimeLocal, ActualWorkedMinutes = (int)Math.Round((punch.PunchTimeUtc - clockIn.PunchTimeUtc).TotalMinutes), IsActual = true });
                    clockIn = null;
                }
                // 未下班班段要按门店时区计到现在；ClockIn/ClockOut 之间的间隔天然不计为工作时间。
                if (clockIn != null)
                {
                    var nowLocal = ConvertUtcToStoreLocal(_timeProvider.GetUtcNow().UtcDateTime, string.IsNullOrWhiteSpace(clockIn.StoreTimeZone) ? DefaultStoreTimeZone : clockIn.StoreTimeZone);
                    if (nowLocal > clockIn.PunchTimeLocal)
                        results.Add(new MinorEmploymentWorkItem { EmployerId = "HB", ScheduleGuid = clockIn.ScheduleGuid, WorkDate = clockIn.WorkDate, StartLocal = clockIn.PunchTimeLocal, EndLocal = nowLocal, ActualWorkedMinutes = (int)Math.Round((nowLocal - clockIn.PunchTimeLocal).TotalMinutes), IsActual = true, IsOngoing = true });
                }
            }
            return results;
        }

        private static MinorEmploymentWorkItem ToMinorWorkItem(AttendanceSchedule item) => new()
        { EmployerId = "HB", ScheduleGuid = item.ScheduleGuid, WorkDate = item.WorkDate, StartLocal = item.WorkDate.Date.Add(item.StartTime), EndLocal = item.WorkDate.Date.Add(item.EndTime) };
        private static void AddExternalWorkItem(List<MinorEmploymentWorkItem> items, EmployeeMinorWorkIntervalDto interval, string? timezone)
        {
            var item = ToExternalWorkItem(interval, timezone, "external:planned");
            if (item != null) items.Add(item);
        }
        private static MinorEmploymentWorkItem? ToExternalWorkItem(EmployeeMinorWorkIntervalDto interval, string? timezone, string employerId)
        {
            if (interval.EndUtc <= interval.StartUtc) return null;
            var zone = string.IsNullOrWhiteSpace(timezone) ? "Australia/Brisbane" : timezone;
            var local = ConvertUtcToStoreLocal(interval.StartUtc, zone);
            var end = ConvertUtcToStoreLocal(interval.EndUtc, zone);
            return new MinorEmploymentWorkItem { EmployerId = employerId, WorkDate = local.Date, StartLocal = local, EndLocal = end, ActualWorkedMinutes = interval.Hours > 0 ? (int)Math.Round(interval.Hours * 60m) : null, IsActual = employerId.EndsWith("actual", StringComparison.Ordinal) };
        }
        private static bool IntervalTouchesWindow(EmployeeMinorWorkIntervalDto interval, DateTime from, DateTime to, string? timezone)
        {
            var zone = string.IsNullOrWhiteSpace(timezone) ? "Australia/Brisbane" : timezone;
            var localStart = ConvertUtcToStoreLocal(interval.StartUtc, zone);
            var localEnd = ConvertUtcToStoreLocal(interval.EndUtc, zone);
            return localEnd > from && localStart < to;
        }
        private static bool? ResolveQldSchoolAgedChild(EmployeeMinorCompliance profile, DateTime anchorDate)
        {
            if (!string.Equals(profile.StateCode, "QLD", StringComparison.OrdinalIgnoreCase)) return null;
            // 评估日已满 16 岁、已完成 Year 10 或有已核验豁免时，不能把 QLD 学龄儿童数值限额套用到该员工。
            // 其余未满 16 岁且档案明确要求入学时才启用 4/8/12/38 小时提醒；资料不足则返回未知。
            if (!profile.DateOfBirth.HasValue) return null;
            if (profile.DateOfBirth.Value.Date.AddYears(16) <= anchorDate.Date) return false;
            if (profile.CompletedYear10 == true || profile.EducationExemptionVerified == true) return false;
            return profile.RequiredToBeEnrolled == true ? true : null;
        }
        private static Dictionary<DateTime, MinorEmploymentSchoolDay> BuildSchoolDays(EmployeeMinorSchoolCalendarDto? school, DateTime from, DateTime to)
        {
            var days = new Dictionary<DateTime, MinorEmploymentSchoolDay>();
            if (school == null) return days;
            for (var day = from.Date; day < to.Date; day = day.AddDays(1))
            {
                var excluded = school.Holidays.Concat(school.PupilFreeDays).Any(range => range.StartDate.Date <= day && range.EndDate.Date >= day);
                var attendance = school.WeeklySchedule.FirstOrDefault(item => item.DayOfWeek == day.DayOfWeek && item.MustAttend);
                if (excluded) { days[day] = new() { IsRequired = false }; continue; }
                if (attendance != null) { days[day] = new() { IsRequired = true, Start = ParseMinorTime(attendance.StartLocalTime), End = ParseMinorTime(attendance.EndLocalTime) }; continue; }
                if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { days[day] = new() { IsRequired = false }; continue; }
                var inTerm = school.TermRanges.Any(range => range.StartDate.Date <= day && range.EndDate.Date >= day);
                if (!inTerm) continue;
                // 学期内工作日没有具体周课表时仍保留“学校日”状态，但不虚构上下课时间。
                days[day] = new() { IsRequired = true };
            }
            return days;
        }
        private static TimeSpan? ParseMinorTime(string? value) => TimeSpan.TryParse(value, out var result) ? result : null;
        private static T? DeserializeMinor<T>(string? json) where T : class => string.IsNullOrWhiteSpace(json) || json == "null" ? null : JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        private static MinorEmploymentComplianceEvaluationDto MapMinorEvaluation(MinorEmploymentComplianceEvaluation evaluation) => new()
        {
            Findings = evaluation.Findings.Select(item => new MinorEmploymentComplianceFindingDto { RuleId = item.RuleId, Severity = item.Severity, Message = item.Message, WorkDate = item.WorkDate, ActualMinutes = item.ActualMinutes, LimitMinutes = item.LimitMinutes, RuleCategory = item.RuleCategory, SourceUrl = item.SourceUrl }).ToList(),
        };
        private async Task RecordMinorEmploymentEvaluationAsync(string userGuid, string storeCode, string? scheduleGuid, string trigger, MinorEmploymentComplianceEvaluationDto evaluation)
        {
            // 提醒审计或待办记录失败不能撤销已成功的排班、发布和真实打卡。
            await RecordMinorEmploymentReminderAsync(userGuid, storeCode, scheduleGuid, trigger, evaluation);
            try
            {
                var profile = await _db.Queryable<EmployeeMinorCompliance>().Where(item => !item.IsDeleted && item.UserGUID == userGuid).OrderBy(item => item.Version, OrderByType.Desc).FirstAsync();
                if (profile == null) return;
                await _db.Insertable(new EmployeeMinorComplianceAudit { ComplianceId = profile.Id, Action = trigger, ActorUserGuid = _currentUserService.GetCurrentUserGuid(), ActorLabel = _currentUserService.GetCurrentUsername(), MetadataJson = JsonSerializer.Serialize(new { count = evaluation.Findings.Count, ruleIds = evaluation.Findings.Select(item => item.RuleId).Distinct().Take(20).ToArray() }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }).ExecuteCommandAsync();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "未成年用工评估审计失败；核心考勤动作已保留。用户 {UserGuid}，触发 {Trigger}", userGuid, trigger);
            }
        }

        private async Task RecordMinorEmploymentReminderAsync(string userGuid, string storeCode, string? scheduleGuid, string trigger, MinorEmploymentComplianceEvaluationDto evaluation)
        {
            try
            {
                await MinorEmploymentReminderService.RecordAsync(_db, userGuid, storeCode, scheduleGuid, trigger, evaluation, _currentUserService.GetCurrentUsername());
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "未成年用工提醒待办记录失败；核心考勤动作已保留。用户 {UserGuid}，触发 {Trigger}", userGuid, trigger);
            }
        }

        private static AttendanceScheduleDto ToDto(AttendanceSchedule item, string? currentUserGuid = null) => new()
        {
            ScheduleGuid = item.ScheduleGuid,
            StoreCode = item.StoreCode,
            UserGuid = item.UserGuid,
            IsMine = !string.IsNullOrWhiteSpace(currentUserGuid)
                && item.UserGuid.Equals(currentUserGuid, StringComparison.OrdinalIgnoreCase),
            WorkDate = item.WorkDate,
            StartTime = item.StartTime,
            EndTime = item.EndTime,
            Status = item.Status,
            Remark = item.Remark,
            MealBreakCount = item.MealBreakCount,
        };

        private static AttendanceAvailabilityDto ToDto(AttendanceAvailability item) => new()
        {
            AvailabilityGuid = item.AvailabilityGuid,
            StoreCode = item.StoreCode,
            UserGuid = item.UserGuid,
            WeekStartDate = item.WeekStartDate,
            AvailableDate = item.AvailableDate,
            StartTime = item.StartTime,
            EndTime = item.EndTime,
            IsUnavailable = item.IsUnavailable,
            Status = item.Status,
            Remark = item.Remark,
        };

        private static AttendancePunchDto ToDto(
            AttendancePunch item,
            string? employeeName = null,
            string? storeName = null,
            DateTime? serverTimeUtc = null) => new()
            {
                PunchGuid = item.PunchGuid,
                ScheduleGuid = item.ScheduleGuid,
                StoreCode = item.StoreCode,
                UserGuid = item.UserGuid,
                WorkDate = item.WorkDate,
                StoreTimeZone = item.StoreTimeZone,
                PunchType = item.PunchType,
                PunchTimeUtc = item.PunchTimeUtc,
                PunchTimeLocal = item.PunchTimeLocal,
                Status = item.Status,
                DeviceId = item.DeviceId,
                LocationLatitude = item.LocationLatitude,
                LocationLongitude = item.LocationLongitude,
                LocationAccuracy = item.LocationAccuracyMeters,
                LocationPermissionStatus = item.LocationPermissionStatus,
                LocationCapturedAtUtc = item.LocationCapturedAtUtc,
                Source = item.Source,
                QrTokenId = item.QrTokenId,
                PosDeviceCode = item.PosDeviceCode,
                SigningKeyId = item.SigningKeyId,
                SupersedesPunchGuid = item.SupersedesPunchGuid,
                AdjustmentGuid = item.AdjustmentGuid,
                EmployeeName = employeeName,
                StoreName = storeName,
                ServerTimeUtc = serverTimeUtc,
                Remark = item.Remark,
            };

        private static AttendanceLocationSampleDto ToDto(AttendanceLocationSample item) => new()
        {
            SampleGuid = item.SampleGuid,
            UserGuid = item.UserGuid,
            StoreCode = item.StoreCode,
            HardwareId = item.HardwareId,
            SystemDeviceNumber = item.SystemDeviceNumber,
            DeviceSystem = item.DeviceSystem,
            EventType = item.EventType,
            LocationLatitude = item.LocationLatitude,
            LocationLongitude = item.LocationLongitude,
            LocationAccuracy = item.LocationAccuracyMeters,
            LocationPermissionStatus = item.LocationPermissionStatus,
            LocationCapturedAtUtc = item.LocationCapturedAtUtc,
            CreatedAt = item.CreatedAt,
        };

        private static AttendanceApprovalDto ToDto(AttendanceApproval item) => new()
        {
            ApprovalGuid = item.ApprovalGuid,
            SourceType = item.SourceType,
            SourceGuid = item.SourceGuid,
            StoreCode = item.StoreCode,
            ApplicantUserGuid = item.ApplicantUserGuid,
            ReviewerUserGuid = item.ReviewerUserGuid,
            Title = item.SourceType,
            ReviewStatus = item.ReviewStatus,
            ReviewRemark = item.ReviewRemark,
            ReviewedAt = item.ReviewedAt,
            CreatedAt = item.CreatedAt,
            CandidateOvertimeMinutes = item.CandidateOvertimeMinutes,
            ApprovedOvertimeMinutes = item.ApprovedOvertimeMinutes,
        };

        private static AttendancePunchAdjustmentDto ToDto(
            AttendancePunchAdjustment item,
            AttendancePunch? originalPunch = null) => new()
        {
            AdjustmentGuid = item.AdjustmentGuid,
            StoreCode = item.StoreCode,
            UserGuid = item.UserGuid,
            ScheduleGuid = item.ScheduleGuid,
            OriginalPunchGuid = item.OriginalPunchGuid,
            OriginalPunchTimeLocal = originalPunch?.PunchTimeLocal,
            PunchType = item.PunchType,
            RequestedPunchTimeLocal = item.RequestedPunchTimeLocal,
            RequestedPunchTimeUtc = item.RequestedPunchTimeUtc,
            Reason = item.Reason,
            Status = item.Status,
            AppliedPunchGuid = item.AppliedPunchGuid,
            IsManagerSelfDirect = item.IsManagerSelfDirect,
            RequestedByUserGuid = item.RequestedByUserGuid,
            ReviewedByUserGuid = item.ReviewedByUserGuid,
            ReviewedAt = item.ReviewedAt,
            CreatedAt = item.CreatedAt,
        };

        private static AttendanceStoreHolidayDto ToDto(AttendanceStoreHoliday item) => new()
        {
            HolidayGuid = item.HolidayGuid,
            StoreCode = item.StoreCode,
            HolidayDate = item.HolidayDate,
            HolidayName = item.HolidayName,
            BusinessStatus = item.BusinessStatus,
            OpenTime = item.OpenTime,
            CloseTime = item.CloseTime,
            IsPaidHoliday = item.IsPaidHoliday,
            Remark = item.Remark,
        };

        private static AttendanceLeaveRequestDto ToDto(AttendanceLeaveRequest item) => new()
        {
            LeaveGuid = item.LeaveGuid,
            StoreCode = item.StoreCode,
            UserGuid = item.UserGuid,
            LeaveType = item.LeaveType,
            StartDate = item.StartDate,
            EndDate = item.EndDate,
            StartTime = item.StartTime,
            EndTime = item.EndTime,
            Reason = item.Reason,
            AttachmentUrl = item.AttachmentUrl,
            Status = item.Status,
            ReviewedBy = item.ReviewedBy,
            ReviewedAt = item.ReviewedAt,
            ReviewRemark = item.ReviewRemark,
        };

        private static AttendanceSettingsDto ToDto(AttendanceSettings item) => new()
        {
            LateGraceMinutes = item.LateGraceMinutes,
            EarlyLeaveGraceMinutes = item.EarlyLeaveGraceMinutes,
            AllowNoSchedulePunch = item.AllowNoSchedulePunch,
            RequireApprovalForLate = item.RequireApprovalForLate,
            RequireApprovalForEarlyLeave = item.RequireApprovalForEarlyLeave,
            RequireApprovalForNoSchedule = item.RequireApprovalForNoSchedule,
            RequireApprovalForDuplicate = item.RequireApprovalForDuplicate,
        };

        private sealed class StoreAccessResult
        {
            public bool Success { get; private init; }
            public string Message { get; private init; } = string.Empty;
            public string? ErrorCode { get; private init; }
            public List<string> StoreCodes { get; private init; } = new();
            public List<string> RelatedStoreCodes { get; private init; } = new();

            public static StoreAccessResult Allowed(
                List<string> storeCodes,
                List<string>? relatedStoreCodes = null) => new()
            {
                Success = true,
                StoreCodes = storeCodes,
                RelatedStoreCodes = relatedStoreCodes ?? storeCodes,
            };

            public static StoreAccessResult Forbidden(string message, string errorCode = "FORBIDDEN") => new()
            {
                Success = false,
                Message = message,
                ErrorCode = errorCode,
            };
        }

        private sealed record PunchAdjustmentContext(
            bool Success,
            string Message,
            string? ErrorCode,
            AttendanceSchedule? Schedule,
            AttendancePunch? ProposedPunch,
            AttendancePunchAdjustmentPreviewDto? Preview)
        {
            public static PunchAdjustmentContext Error(string message, string? errorCode) =>
                new(false, message, errorCode, null, null, null);

            public static PunchAdjustmentContext OK(
                AttendanceSchedule schedule,
                AttendancePunch proposedPunch,
                AttendancePunchAdjustmentPreviewDto preview) =>
                new(true, string.Empty, null, schedule, proposedPunch, preview);
        }

        private sealed record PunchAdjustmentApplicationContext(
            bool Success,
            string Message,
            string? ErrorCode,
            AttendanceSchedule? Schedule,
            AttendancePunch? ProposedPunch)
        {
            public static PunchAdjustmentApplicationContext Error(
                string message,
                string? errorCode) =>
                new(false, message, errorCode, null, null);

            public static PunchAdjustmentApplicationContext OK(
                AttendanceSchedule schedule,
                AttendancePunch proposedPunch) =>
                new(true, string.Empty, null, schedule, proposedPunch);
        }

        private sealed class ValidationResult
        {
            public bool Success { get; private init; }
            public string Message { get; private init; } = string.Empty;
            public string? ErrorCode { get; private init; }

            public static ValidationResult OK() => new() { Success = true };
            public static ValidationResult Error(string message, string errorCode) => new()
            {
                Success = false,
                Message = message,
                ErrorCode = errorCode,
            };
        }
    }
}
