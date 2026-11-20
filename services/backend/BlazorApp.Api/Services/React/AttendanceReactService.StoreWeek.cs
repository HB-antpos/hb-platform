using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React
{
    // partial：员工端「整店整周排班」只读接口。
    // 员工只有「查看自己的排班」权限，现有 schedules/week 需要「查看相关分店排班」（店长才有），所以单独提供这个接口：
    // 权限仍是 ViewSelf，范围校验沿用 my/week（只能看自己关联的分店），并且只返回 AttendanceStoreWeekShiftDto 里的字段。
    public partial class AttendanceReactService
    {
        public async Task<ApiResponse<AttendanceStoreWeekDto>> GetMyStoreWeekAsync(
            DateTime? weekStartDate,
            string? storeCode = null
        )
        {
            var userGuid = ResolveCurrentUserGuid();
            // includeRelatedStoreCodes：管理员不传 storeCode 时 StoreCodes 为空，退回到他自己关联的分店。
            var access = await ResolveRelatedStoreAccessAsync(userGuid, storeCode, includeRelatedStoreCodes: true);
            if (!access.Success)
            {
                return ApiResponse<AttendanceStoreWeekDto>.Error(access.Message, access.ErrorCode);
            }

            var start = GetWeekStart(weekStartDate ?? DateTime.Today).Date;
            var end = start.AddDays(6);
            var result = new AttendanceStoreWeekDto { WeekStartDate = start };
            var storeCodes = (access.StoreCodes.Count > 0 ? access.StoreCodes : access.RelatedStoreCodes)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (storeCodes.Count == 0)
            {
                return ApiResponse<AttendanceStoreWeekDto>.OK(result);
            }

            var stores = await _db.Queryable<Store>()
                .Where(item => !item.IsDeleted && storeCodes.Contains(item.StoreCode))
                .ToListAsync();
            // 只取已发布（Active）的班：草稿、已取消的班员工看不到，与「我的一周」口径一致。
            var rows = await _db.Queryable<AttendanceSchedule>()
                .Where(item =>
                    !item.IsDeleted
                    && item.Status == "Active"
                    && storeCodes.Contains(item.StoreCode)
                    && item.WorkDate >= start
                    && item.WorkDate <= end)
                .ToListAsync();

            var userGuids = rows.Select(item => item.UserGuid).Distinct().ToList();
            var users = userGuids.Count == 0
                ? new List<User>()
                : await _db.Queryable<User>().Where(item => userGuids.Contains(item.UserGUID)).ToListAsync();
            var userMap = users.ToDictionary(item => item.UserGUID, StringComparer.OrdinalIgnoreCase);
            // 已批准请假覆盖的同事班次不下发（请假属个人隐私，对同事就是「那天没班」）；自己的班照常返回，我的一周本来就显示请假。
            var approvedLeaves = userGuids.Count == 0
                ? new List<AttendanceLeaveRequest>()
                : await _db.Queryable<AttendanceLeaveRequest>()
                    .Where(item =>
                        !item.IsDeleted
                        && item.Status == "Approved"
                        && userGuids.Contains(item.UserGuid)
                        && item.StartDate <= end
                        && item.EndDate >= start)
                    .ToListAsync();

            foreach (var store in stores.OrderBy(item => item.StoreName ?? item.StoreCode, StringComparer.OrdinalIgnoreCase))
            {
                var storeRows = rows
                    .Where(item => item.StoreCode.Equals(store.StoreCode, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var shifts = new List<AttendanceStoreWeekShiftDto>();
                foreach (var row in storeRows)
                {
                    var isMine = row.UserGuid.Equals(userGuid, StringComparison.OrdinalIgnoreCase);
                    if (!isMine && approvedLeaves.Any(leave =>
                            leave.UserGuid.Equals(row.UserGuid, StringComparison.OrdinalIgnoreCase)
                            && leave.StartDate.Date <= row.WorkDate.Date
                            && leave.EndDate.Date >= row.WorkDate.Date))
                    {
                        continue;
                    }

                    userMap.TryGetValue(row.UserGuid, out var user);
                    var fullName = PersonNameFormatter.Normalize(user?.FullName);
                    shifts.Add(new AttendanceStoreWeekShiftDto
                    {
                        UserGuid = row.UserGuid,
                        DisplayName = PersonNameFormatter.ShortName(user?.Nickname, user?.GivenName, user?.FullName, user?.Username)
                            ?? string.Empty,
                        FullName = fullName,
                        WorkDate = row.WorkDate.Date,
                        StartTime = row.StartTime,
                        EndTime = row.EndTime,
                        IsMine = isMine,
                    });
                }

                result.Stores.Add(new AttendanceStoreWeekStoreDto
                {
                    StoreCode = store.StoreCode,
                    StoreName = store.StoreName,
                    // 「已发布」看请假过滤之前：整周同事都请假也不能说成「店长没发布」。
                    Published = storeRows.Count > 0,
                    Shifts = shifts
                        .OrderBy(item => item.WorkDate)
                        .ThenBy(item => item.StartTime)
                        .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                });
            }

            return ApiResponse<AttendanceStoreWeekDto>.OK(result);
        }
    }
}
