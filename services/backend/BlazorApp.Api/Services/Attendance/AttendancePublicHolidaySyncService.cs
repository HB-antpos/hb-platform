using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Attendance
{
    public class AttendancePublicHolidaySyncService : IAttendancePublicHolidaySyncService
    {
        private const string AutoSyncRemarkPrefix = "Auto public holiday sync";
        private readonly ISqlSugarClient _db;
        private readonly IAustralianPublicHolidayProvider _provider;
        private readonly ICurrentUserManageableStoreScopeService _scopeService;
        private readonly ILogger<AttendancePublicHolidaySyncService> _logger;
        private readonly TimeZoneInfo _businessTimeZone;

        public AttendancePublicHolidaySyncService(
            SqlSugarContext context,
            IAustralianPublicHolidayProvider provider,
            ICurrentUserManageableStoreScopeService scopeService,
            ILogger<AttendancePublicHolidaySyncService> logger
        )
        {
            _db = context.Db;
            _provider = provider;
            _scopeService = scopeService;
            _logger = logger;
            _businessTimeZone = ResolveTimeZone();
        }

        public async Task<ApiResponse<SyncAttendanceStoreHolidayResultDto>> SyncStoreAsync(
            SyncAttendanceStoreHolidayDto request,
            CancellationToken cancellationToken = default
        )
        {
            if (string.IsNullOrWhiteSpace(request.StoreCode))
            {
                return ApiResponse<SyncAttendanceStoreHolidayResultDto>.Error(
                    "分店代码不能为空",
                    "STORE_REQUIRED"
                );
            }

            var storeCode = request.StoreCode.Trim();
            var store = await _db.Queryable<Store>()
                .FirstAsync(item => item.StoreCode == storeCode && !item.IsDeleted);
            if (store == null)
            {
                return ApiResponse<SyncAttendanceStoreHolidayResultDto>.Error(
                    "分店不存在",
                    "STORE_NOT_FOUND"
                );
            }

            var scope = await _scopeService.GetScopeAsync();
            if (!scope.IsAllowed || !scope.CanAccessStoreCode(storeCode))
            {
                return ApiResponse<SyncAttendanceStoreHolidayResultDto>.Error(
                    "没有权限访问该分店",
                    "FORBIDDEN_STORE"
                );
            }

            var today = GetBusinessToday();
            var window = PublicHolidaySyncHelper.BuildSyncWindow(
                today,
                request.FromDate,
                request.ToDate,
                request.DaysAhead
            );

            var jurisdiction =
                PublicHolidaySyncHelper.NormalizeJurisdiction(request.StateCode)
                ?? PublicHolidaySyncHelper.NormalizeJurisdiction(request.Jurisdiction)
                ?? PublicHolidaySyncHelper.ResolveJurisdictionFromPostcode(request.Postcode)
                ?? PublicHolidaySyncHelper.ResolveStoreJurisdiction(store.TimeZoneId, store.Address);
            if (jurisdiction == null)
            {
                return ApiResponse<SyncAttendanceStoreHolidayResultDto>.Error(
                    "无法从门店时区或地址解析 NSW/QLD 公共假期州别",
                    "JURISDICTION_REQUIRED"
                );
            }

            var result = await SyncResolvedStoresAsync(
                new[] { (storeCode, jurisdiction) },
                window,
                "system-public-holiday-sync",
                cancellationToken
            );
            return ApiResponse<SyncAttendanceStoreHolidayResultDto>.OK(result, "公共假期已同步");
        }

        public async Task<SyncAttendanceStoreHolidayResultDto> SyncAllActiveStoresAsync(
            int daysAhead = 30,
            CancellationToken cancellationToken = default
        )
        {
            var today = GetBusinessToday();
            var window = PublicHolidaySyncHelper.BuildSyncWindow(today, null, null, daysAhead);
            var stores = await _db.Queryable<Store>()
                .Where(item => item.IsActive && !item.IsDeleted)
                .Select(item => new { item.StoreCode, item.TimeZoneId, item.Address })
                .ToListAsync();

            var targets = new List<(string StoreCode, string Jurisdiction)>();
            var skipped = new List<string>();
            foreach (var store in stores)
            {
                // 先认配置时区再按地址邮编：地址只写区名的门店（如 Bankstown）也要同步假期
                var jurisdiction = PublicHolidaySyncHelper.ResolveStoreJurisdiction(
                    store.TimeZoneId,
                    store.Address
                );
                if (jurisdiction == null)
                {
                    skipped.Add(store.StoreCode);
                    _logger.LogInformation(
                        "跳过公共假期同步：分店 {StoreCode} 时区与地址都无法解析 NSW/QLD",
                        store.StoreCode
                    );
                    continue;
                }

                targets.Add((store.StoreCode, jurisdiction));
            }

            var result = await SyncResolvedStoresAsync(
                targets,
                window,
                "scheduled-public-holiday-sync",
                cancellationToken
            );
            result.SkippedStores.AddRange(skipped);
            result.SkippedCount += skipped.Count;
            return result;
        }

        private async Task<SyncAttendanceStoreHolidayResultDto> SyncResolvedStoresAsync(
            IReadOnlyList<(string StoreCode, string Jurisdiction)> targets,
            PublicHolidaySyncWindow window,
            string username,
            CancellationToken cancellationToken
        )
        {
            var result = new SyncAttendanceStoreHolidayResultDto
            {
                StoreCode = targets.Count == 1 ? targets[0].StoreCode : string.Empty,
                Jurisdiction = targets.Count == 1 ? targets[0].Jurisdiction : null,
                FromDate = window.FromDate,
                ToDate = window.ToDate,
                SyncedAt = DateTime.UtcNow,
            };

            if (targets.Count == 0)
            {
                return result;
            }

            var holidaysByJurisdiction = new Dictionary<string, IReadOnlyList<PublicHolidaySourceItem>>();
            foreach (var jurisdiction in targets.Select(item => item.Jurisdiction).Distinct())
            {
                holidaysByJurisdiction[jurisdiction] = await _provider.GetHolidaysAsync(
                    jurisdiction,
                    window.FromDate,
                    window.ToDate,
                    cancellationToken
                );
            }

            var now = DateTime.UtcNow;
            await _db.Ado.BeginTranAsync();
            try
            {
                foreach (var target in targets)
                {
                    if (!holidaysByJurisdiction.TryGetValue(target.Jurisdiction, out var holidays))
                    {
                        continue;
                    }

                    foreach (var holiday in holidays)
                    {
                        var saved = await UpsertHolidayAsync(target.StoreCode, target.Jurisdiction, holiday, now, username);
                        if (saved.Skipped)
                        {
                            result.SkippedCount++;
                            continue;
                        }

                        if (saved.Created)
                        {
                            result.CreatedCount++;
                        }
                        else
                        {
                            result.UpdatedCount++;
                        }

                        result.Holidays.Add(ToDto(saved.Model!));
                    }
                }

                await _db.Ado.CommitTranAsync();
            }
            catch (Exception ex)
            {
                await _db.Ado.RollbackTranAsync();
                _logger.LogError(ex, "同步公共假期失败");
                throw;
            }

            result.SyncedCount = result.CreatedCount + result.UpdatedCount;
            return result;
        }

        private async Task<(AttendanceStoreHoliday? Model, bool Created, bool Skipped)> UpsertHolidayAsync(
            string storeCode,
            string jurisdiction,
            PublicHolidaySourceItem holiday,
            DateTime now,
            string username
        )
        {
            var holidayDate = holiday.HolidayDate.Date;
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

            var remark = $"{AutoSyncRemarkPrefix}: {jurisdiction}";
            if (existingRows.Count > 0)
            {
                var model = existingRows[0];
                if (!IsAutoSyncedHoliday(model))
                {
                    return (model, false, true);
                }

                model.HolidayName = holiday.HolidayName;
                model.BusinessStatus = "Open";
                model.OpenTime = null;
                model.CloseTime = null;
                model.IsPaidHoliday = true;
                model.Remark = remark;
                model.UpdatedAt = now;
                model.UpdatedBy = username;
                await _db.Updateable(model).ExecuteCommandAsync();

                foreach (var duplicate in existingRows.Skip(1))
                {
                    duplicate.IsDeleted = true;
                    duplicate.UpdatedAt = now;
                    duplicate.UpdatedBy = username;
                    await _db.Updateable(duplicate).ExecuteCommandAsync();
                }

                return (model, false, false);
            }

            var created = new AttendanceStoreHoliday
            {
                HolidayGuid = Guid.NewGuid().ToString(),
                StoreCode = storeCode,
                HolidayDate = holidayDate,
                HolidayName = holiday.HolidayName,
                BusinessStatus = "Open",
                IsPaidHoliday = true,
                Remark = remark,
                CreatedAt = now,
                CreatedBy = username,
                UpdatedAt = now,
                UpdatedBy = username,
            };
            await _db.Insertable(created).ExecuteCommandAsync();
            return (created, true, false);
        }

        private static bool IsAutoSyncedHoliday(AttendanceStoreHoliday item) =>
            item.Remark?.StartsWith(AutoSyncRemarkPrefix, StringComparison.OrdinalIgnoreCase) == true;

        private DateTime GetBusinessToday() =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _businessTimeZone).Date;

        private static TimeZoneInfo ResolveTimeZone()
        {
            foreach (var id in new[] { "Australia/Brisbane", "E. Australia Standard Time" })
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }

            return TimeZoneInfo.Local;
        }

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
    }
}
