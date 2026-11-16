using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 已发布排班的周锁定：排班发布（Active）后，到了所在周周一 00:00（分店当地时间）就锁定，
    /// 之后只有管理员能修改、删除、撤回或找回。周一之前店长可以随意调整。
    /// 草稿不受影响（员工看不到）；新建和复制周只产生草稿，发布草稿也不受此规则限制。
    /// </summary>
    public partial class AttendanceReactService
    {
        internal const string PublishedWeekLockedErrorCode = "SCHEDULE_WEEK_LOCKED";
        private const string PublishedWeekLockedMessage = "本周排班已发布且已过周一，只有管理员可以修改";

        private static ApiResponse<T> PublishedWeekLocked<T>() =>
            ApiResponse<T>.Error(PublishedWeekLockedMessage, PublishedWeekLockedErrorCode);

        /// <summary>
        /// 当前用户是否被已发布周锁定拦住。管理员永远放行；只有 Active 状态的排班才会锁定。
        /// </summary>
        private async Task<bool> IsBlockedByPublishedWeekLockAsync(string storeCode, DateTime workDate, string? status)
        {
            if (IsAdmin() || !IsActiveScheduleStatus(status))
            {
                return false;
            }
            var lockAtUtc = await GetPublishedWeekLockAtUtcAsync(storeCode, workDate);
            return _timeProvider.GetUtcNow().UtcDateTime >= lockAtUtc;
        }

        /// <summary>排班所在周的周一 00:00（分店当地时间）换算成 UTC，即锁定生效时刻。</summary>
        private async Task<DateTime> GetPublishedWeekLockAtUtcAsync(string storeCode, DateTime workDate)
        {
            var storeTimeZone = await ResolveStoreTimeZoneAsync(storeCode, null);
            return ConvertStoreLocalToUtc(GetWeekStart(workDate.Date), storeTimeZone);
        }

        /// <summary>
        /// 给列表里的排班标出「已锁定」，前端据此禁用编辑并提示，不必自己算时区。
        /// 对管理员不标锁定（管理员仍可修改）。同一分店只解析一次时区。
        /// </summary>
        private async Task PopulatePublishedWeekLockAsync(List<AttendanceScheduleDto> items)
        {
            if (items.Count == 0 || IsAdmin())
            {
                return;
            }
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var timeZoneByStore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (!IsActiveScheduleStatus(item.Status) || string.IsNullOrWhiteSpace(item.StoreCode))
                {
                    continue;
                }
                if (!timeZoneByStore.TryGetValue(item.StoreCode, out var storeTimeZone))
                {
                    storeTimeZone = await ResolveStoreTimeZoneAsync(item.StoreCode, null);
                    timeZoneByStore[item.StoreCode] = storeTimeZone;
                }
                item.IsPublishedWeekLocked =
                    nowUtc >= ConvertStoreLocalToUtc(GetWeekStart(item.WorkDate.Date), storeTimeZone);
            }
        }

        private static bool IsActiveScheduleStatus(string? status) =>
            string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase);
    }
}
