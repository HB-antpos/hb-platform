using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 已删除排班的列表与找回。删除只做软删除（IsDeleted + DeletedAt / DeletedBy），有打卡的排班也能删，
    /// 删错了在这里恢复；恢复后重新对账派生审批（漏下班、加班、打卡异常）。
    /// </summary>
    public partial class AttendanceReactService
    {
        /// <summary>不带日期条件时「已删除排班」默认回看的天数（按排班日期）。</summary>
        internal const int DeletedScheduleDefaultLookbackDays = 60;

        /// <summary>单次最多返回的已删除排班条数，防止一次拉全量历史。</summary>
        internal const int DeletedScheduleMaxRows = 200;

        public async Task<ApiResponse<List<AttendanceScheduleDto>>> GetDeletedSchedulesAsync(
            AttendanceScheduleQueryDto query)
        {
            var storeAccess = await ResolveManagedStoreAccessAsync(query.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<List<AttendanceScheduleDto>>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            var hasDateFilter = query.WeekStartDate.HasValue || query.FromDate.HasValue || query.ToDate.HasValue;
            var defaultFrom = ConvertUtcToStoreLocal(
                    _timeProvider.GetUtcNow().UtcDateTime,
                    await ResolveStoreTimeZoneAsync(query.StoreCode, null))
                .Date
                .AddDays(-DeletedScheduleDefaultLookbackDays);
            var rows = await _db.Queryable<AttendanceSchedule>()
                .Where(item => item.IsDeleted)
                .WhereIF(!string.IsNullOrWhiteSpace(query.StoreCode), item => item.StoreCode == query.StoreCode!.Trim())
                .WhereIF(string.IsNullOrWhiteSpace(query.StoreCode) && storeAccess.StoreCodes.Count > 0, item => storeAccess.StoreCodes.Contains(item.StoreCode))
                .WhereIF(!string.IsNullOrWhiteSpace(query.UserGuid), item => item.UserGuid == query.UserGuid!.Trim())
                .WhereIF(query.WeekStartDate.HasValue, item => item.WorkDate >= query.WeekStartDate!.Value.Date && item.WorkDate <= query.WeekStartDate!.Value.Date.AddDays(6))
                .WhereIF(query.FromDate.HasValue, item => item.WorkDate >= query.FromDate!.Value.Date)
                .WhereIF(query.ToDate.HasValue, item => item.WorkDate <= query.ToDate!.Value.Date)
                .WhereIF(!hasDateFilter, item => item.WorkDate >= defaultFrom)
                // 删除时 UpdatedAt 与 DeletedAt 同时写入；迁移前删除的历史行只有 UpdatedAt，统一按它排序。
                .OrderByDescending(item => item.UpdatedAt)
                .OrderByDescending(item => item.WorkDate)
                .Take(DeletedScheduleMaxRows)
                .ToListAsync();

            var result = rows.Select(item =>
            {
                var dto = ToDto(item);
                dto.DeletedAt = item.DeletedAt ?? item.UpdatedAt;
                dto.DeletedBy = item.DeletedBy ?? item.UpdatedBy;
                return dto;
            }).ToList();
            await PopulateScheduleDisplayFieldsAsync(result);
            return ApiResponse<List<AttendanceScheduleDto>>.OK(result);
        }

        public async Task<ApiResponse<AttendanceScheduleDto>> RestoreScheduleAsync(string scheduleGuid)
        {
            var model = await _db.Queryable<AttendanceSchedule>()
                .FirstAsync(item => item.ScheduleGuid == scheduleGuid && item.IsDeleted);
            if (model == null)
            {
                return ApiResponse<AttendanceScheduleDto>.Error("已删除的排班不存在", "NOT_FOUND");
            }

            var storeAccess = await ResolveManagedStoreAccessAsync(model.StoreCode);
            if (!storeAccess.Success)
            {
                return ApiResponse<AttendanceScheduleDto>.Error(storeAccess.Message, storeAccess.ErrorCode);
            }

            // 员工已离职或解绑本店时不能把班找回来，否则会出现无主班次。
            var boundUsers = await LoadStoreBoundUserGuidsAsync(model.StoreCode, new[] { model.UserGuid });
            if (!boundUsers.Contains(model.UserGuid))
            {
                return ApiResponse<AttendanceScheduleDto>.Error("员工不存在或不属于该分店", "EMPLOYEE_NOT_IN_STORE");
            }

            var mutationResource = AttendanceDailyMutationLock.BuildResource(
                model.UserGuid,
                model.StoreCode,
                model.WorkDate);
            await using (await AttendanceDailyMutationLock.AcquireProcessAsync(mutationResource))
            {
                await _db.Ado.BeginTranAsync();
                try
                {
                    await AttendanceDailyMutationLock.AcquireDatabaseAsync(_db, mutationResource);
                    model = await _db.Queryable<AttendanceSchedule>()
                        .FirstAsync(item => item.ScheduleGuid == scheduleGuid && item.IsDeleted);
                    if (model == null)
                    {
                        await _db.Ado.RollbackTranAsync();
                        return ApiResponse<AttendanceScheduleDto>.Error("已删除的排班不存在", "NOT_FOUND");
                    }
                    // 删除后同时段可能已经重新排过班；恢复不能制造重叠。
                    if (await HasOverlappingScheduleAsync(
                            model.ScheduleGuid,
                            model.StoreCode,
                            model.UserGuid,
                            model.WorkDate,
                            model.StartTime,
                            model.EndTime))
                    {
                        await _db.Ado.RollbackTranAsync();
                        return ApiResponse<AttendanceScheduleDto>.Error(
                            "该时段已有其他排班，不能恢复",
                            "SCHEDULE_OVERLAP");
                    }

                    // 删除时状态被置为 Cancelled，原状态没有留存：有打卡的班必然是发布过的，恢复为 Active 才能继续对账计薪；
                    // 没有打卡的恢复为草稿，由店长确认后再发布，避免误恢复直接出现在员工的排班里。
                    var hasPunches = await _db.Queryable<AttendancePunch>().AnyAsync(item =>
                        !item.IsDeleted && item.ScheduleGuid == model.ScheduleGuid);
                    var now = _timeProvider.GetUtcNow().UtcDateTime;
                    model.IsDeleted = false;
                    model.Status = hasPunches ? "Active" : "Draft";
                    model.DeletedAt = null;
                    model.DeletedBy = null;
                    model.UpdatedAt = now;
                    model.UpdatedBy = _currentUserService.GetCurrentUsername();
                    await _db.Updateable(model).ExecuteCommandAsync();
                    if (model.Status == "Active")
                    {
                        // 删除时取消掉的漏下班 / 打卡异常 / 加班待审，按当前打卡重新生成。
                        await ReconcileDerivedApprovalsForScheduleAsync(model.ScheduleGuid);
                    }
                    await _db.Ado.CommitTranAsync();
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }
            }

            var result = ToDto(model);
            await PopulateScheduleDisplayFieldsAsync(new List<AttendanceScheduleDto> { result });
            return ApiResponse<AttendanceScheduleDto>.OK(
                result,
                model.Status == "Active" ? "排班已恢复" : "排班已恢复为草稿，发布后员工可见");
        }
    }
}
