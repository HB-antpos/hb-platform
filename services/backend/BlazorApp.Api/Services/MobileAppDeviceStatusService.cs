using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    public sealed class MobileAppDeviceStatusService
    {
        public const int OnlineWindowMinutes = 15;

        private readonly ISqlSugarClient _db;
        private readonly IDeviceRegistrationService _deviceRegistrationService;
        private readonly ILogger<MobileAppDeviceStatusService> _logger;
        private readonly Func<DateTime> _utcNow;

        public MobileAppDeviceStatusService(
            ISqlSugarClient db,
            IDeviceRegistrationService deviceRegistrationService,
            ILogger<MobileAppDeviceStatusService> logger,
            Func<DateTime>? utcNowProvider = null
        )
        {
            _db = db;
            _deviceRegistrationService = deviceRegistrationService;
            _logger = logger;
            _utcNow = utcNowProvider ?? (() => DateTime.UtcNow);
        }

        public async Task<ApiResponse<MobileAppDeviceStatusDto>> UpsertHeartbeatAsync(
            MobileAppDeviceHeartbeatDto dto,
            MobileAppDeviceHeartbeatAuthContext authContext
        )
        {
            var hardwareId = NormalizeText(dto.HardwareId, 120);
            if (string.IsNullOrWhiteSpace(hardwareId))
            {
                return ApiResponse<MobileAppDeviceStatusDto>.Error(
                    "缺少设备硬件识别码",
                    "INVALID_HARDWARE_ID"
                );
            }

            var now = EnsureUtc(_utcNow());
            var registeredDeviceResult = await ResolveRegisteredDeviceAsync(hardwareId);
            if (!registeredDeviceResult.Success)
            {
                return ApiResponse<MobileAppDeviceStatusDto>.Error(
                    "读取设备注册信息失败，心跳未写入",
                    "REGISTERED_DEVICE_LOOKUP_FAILED"
                );
            }

            var registeredDevice = registeredDeviceResult.Device;
            if (registeredDevice != null && !authContext.IsDeviceSessionVerified)
            {
                return ApiResponse<MobileAppDeviceStatusDto>.Error(
                    "已注册设备必须通过设备会话校验后才能更新 App 状态",
                    "REGISTERED_DEVICE_AUTH_REQUIRED"
                );
            }

            var existing = await _db
                .Queryable<MobileAppDeviceStatus>()
                .FirstAsync(item => item.HardwareId == hardwareId);
            var entity = existing ?? new MobileAppDeviceStatus { Id = Guid.NewGuid() };

            // 关键逻辑：只更新当前快照，不记录版本历史；已注册设备的分店和系统信息必须以服务端注册表为准。
            ApplyHeartbeat(entity, dto, hardwareId, registeredDevice, authContext, now);

            if (existing == null)
            {
                try
                {
                    await _db.Insertable(entity).ExecuteCommandAsync();
                }
                catch (Exception ex) when (IsUniqueHardwareIdConflict(ex))
                {
                    var concurrentExisting = await _db
                        .Queryable<MobileAppDeviceStatus>()
                        .FirstAsync(item => item.HardwareId == hardwareId);
                    if (concurrentExisting == null)
                    {
                        throw;
                    }

                    ApplyHeartbeat(concurrentExisting, dto, hardwareId, registeredDevice, authContext, now);
                    await _db.Updateable(concurrentExisting).ExecuteCommandAsync();
                    entity = concurrentExisting;
                }
            }
            else
            {
                await _db.Updateable(entity).ExecuteCommandAsync();
            }

            return ApiResponse<MobileAppDeviceStatusDto>.OK(
                MapToDto(entity, GetOnlineThreshold(now)),
                "心跳已记录"
            );
        }

        public async Task<ApiResponse<PagedResult<MobileAppDeviceStatusDto>>> GetPagedAsync(
            MobileAppDeviceStatusQueryDto query
        )
        {
            var page = Math.Max(query.Page, 1);
            var pageSize = Math.Clamp(query.PageSize, 1, 100);
            var threshold = GetOnlineThreshold(EnsureUtc(_utcNow()));
            var queryable = BuildQuery(query, threshold);

            var total = await queryable.CountAsync();
            var items = await queryable
                .OrderByDescending(item => item.LastSeenAtUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return ApiResponse<PagedResult<MobileAppDeviceStatusDto>>.OK(
                new PagedResult<MobileAppDeviceStatusDto>
                {
                    Items = items.Select(item => MapToDto(item, threshold)).ToList(),
                    Total = total,
                    Page = page,
                    PageSize = pageSize,
                }
            );
        }

        public async Task<ApiResponse<MobileAppDeviceStatusSummaryDto>> GetSummaryAsync(
            MobileAppDeviceStatusQueryDto query
        )
        {
            var threshold = GetOnlineThreshold(EnsureUtc(_utcNow()));
            var allItems = await BuildQuery(
                    new MobileAppDeviceStatusQueryDto
                    {
                        StoreCode = query.StoreCode,
                        DeviceSystem = query.DeviceSystem,
                        Keyword = query.Keyword,
                        // 点选某个版本后，汇总条要和下方列表保持同一口径。
                        AppVersion = query.AppVersion,
                        AppBuildVersion = query.AppBuildVersion,
                    },
                    threshold
                )
                .ToListAsync();

            var online = allItems.Count(item => item.LastSeenAtUtc >= threshold);
            var android = allItems.Count(item => ResolveSystemBucket(item) == "android");
            var ios = allItems.Count(item => ResolveSystemBucket(item) == "ios");

            return ApiResponse<MobileAppDeviceStatusSummaryDto>.OK(
                new MobileAppDeviceStatusSummaryDto
                {
                    Total = allItems.Count,
                    Online = online,
                    Offline = allItems.Count - online,
                    Android = android,
                    Ios = ios,
                    UnknownSystem = allItems.Count - android - ios,
                }
            );
        }

        /// <summary>
        /// 按系统 + 原生版本号 + 构建号统计设备数，同时给出在线数与 OTA / 内置 bundle 的分布。
        /// </summary>
        public async Task<ApiResponse<MobileAppVersionDistributionDto>> GetVersionDistributionAsync(
            MobileAppDeviceStatusQueryDto query
        )
        {
            var threshold = GetOnlineThreshold(EnsureUtc(_utcNow()));
            // 关键逻辑：分布只受分店、系统、关键词约束。在线状态与版本精确筛选属于对明细的下钻，
            // 若也作用在分布上，点选一个版本后其余版本会消失，无法切换。
            var allItems = await BuildQuery(
                    new MobileAppDeviceStatusQueryDto
                    {
                        StoreCode = query.StoreCode,
                        DeviceSystem = query.DeviceSystem,
                        Keyword = query.Keyword,
                    },
                    threshold
                )
                .ToListAsync();

            var items = allItems
                .GroupBy(item => (
                    System: ResolveSystemLabel(item),
                    Version: NormalizeText(item.AppVersion, 80),
                    Build: NormalizeText(item.AppBuildVersion, 80)
                ))
                .Select(group => new MobileAppVersionDistributionItemDto
                {
                    DeviceSystem = group.Key.System,
                    AppVersion = group.Key.Version,
                    AppBuildVersion = group.Key.Build,
                    Total = group.Count(),
                    Online = group.Count(item => item.LastSeenAtUtc >= threshold),
                    Ota = group.Count(item => IsUpdateSource(item, "ota")),
                    Embedded = group.Count(item => IsUpdateSource(item, "embedded")),
                })
                .ToList();
            items.Sort(CompareDistributionItems);

            return ApiResponse<MobileAppVersionDistributionDto>.OK(
                new MobileAppVersionDistributionDto { Total = allItems.Count, Items = items }
            );
        }

        private ISugarQueryable<MobileAppDeviceStatus> BuildQuery(
            MobileAppDeviceStatusQueryDto query,
            DateTime onlineThreshold
        )
        {
            var queryable = _db
                .Queryable<MobileAppDeviceStatus>()
                .Where(item => !item.IsDeleted);

            var storeCode = NormalizeText(query.StoreCode, 50);
            if (!string.IsNullOrWhiteSpace(storeCode))
            {
                queryable = queryable.Where(item => item.StoreCode == storeCode);
            }

            var deviceSystem = NormalizeDeviceSystem(query.DeviceSystem);
            if (!string.IsNullOrWhiteSpace(deviceSystem))
            {
                queryable = queryable.Where(item => item.DeviceSystem == deviceSystem);
            }

            var appVersion = NormalizeText(query.AppVersion, 80);
            if (!string.IsNullOrWhiteSpace(appVersion))
            {
                queryable = queryable.Where(item => item.AppVersion == appVersion);
            }

            var appBuildVersion = NormalizeText(query.AppBuildVersion, 80);
            if (!string.IsNullOrWhiteSpace(appBuildVersion))
            {
                queryable = queryable.Where(item => item.AppBuildVersion == appBuildVersion);
            }

            var keyword = NormalizeText(query.Keyword, 120);
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                queryable = queryable.Where(item =>
                    item.HardwareId.Contains(keyword)
                    || (item.SystemDeviceNumber != null && item.SystemDeviceNumber.Contains(keyword))
                    || (item.StoreCode != null && item.StoreCode.Contains(keyword))
                    || (item.AppVersion != null && item.AppVersion.Contains(keyword))
                    || (item.RuntimeVersion != null && item.RuntimeVersion.Contains(keyword))
                    || (item.Channel != null && item.Channel.Contains(keyword))
                    || (item.UpdateId != null && item.UpdateId.Contains(keyword))
                    || (item.LastSeenUsername != null && item.LastSeenUsername.Contains(keyword))
                    || (item.LastSeenUserFullName != null && item.LastSeenUserFullName.Contains(keyword))
                );
            }

            var onlineState = NormalizeText(query.OnlineState, 30)?.ToLowerInvariant();
            queryable = onlineState switch
            {
                "online" => queryable.Where(item => item.LastSeenAtUtc >= onlineThreshold),
                "offline" => queryable.Where(item => item.LastSeenAtUtc < onlineThreshold),
                _ => queryable,
            };

            return queryable;
        }

        private async Task<(bool Success, POSM_设备注册信息表? Device)> ResolveRegisteredDeviceAsync(
            string hardwareId
        )
        {
            try
            {
                return (
                    true,
                    await _deviceRegistrationService.GetDeviceByHardwareIdAsync(hardwareId)
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取 POSM 注册设备信息失败，HardwareId: {HardwareId}", hardwareId);
                return (false, null);
            }
        }

        private static void ApplyHeartbeat(
            MobileAppDeviceStatus entity,
            MobileAppDeviceHeartbeatDto dto,
            string hardwareId,
            POSM_设备注册信息表? registeredDevice,
            MobileAppDeviceHeartbeatAuthContext authContext,
            DateTime now
        )
        {
            entity.HardwareId = hardwareId;
            entity.SystemDeviceNumber = FirstNonEmpty(
                NormalizeText(registeredDevice?.系统设备编号, 120),
                NormalizeText(dto.SystemDeviceNumber, 120)
            );
            entity.DeviceSystem = FirstNonEmpty(
                NormalizeDeviceSystem(registeredDevice?.设备系统),
                NormalizeDeviceSystem(dto.DeviceSystem),
                NormalizeDeviceSystem(dto.Platform)
            );
            entity.Platform = NormalizePlatform(dto.Platform);
            entity.StoreCode = FirstNonEmpty(
                NormalizeText(registeredDevice?.分店代码, 50),
                NormalizeText(dto.StoreCode, 50)
            );
            entity.AppVersion = NormalizeText(dto.AppVersion, 80);
            entity.AppBuildVersion = NormalizeText(dto.AppBuildVersion, 80);
            entity.RuntimeVersion = NormalizeText(dto.RuntimeVersion, 120);
            entity.Channel = NormalizeText(dto.Channel, 120);
            entity.UpdateId = NormalizeText(dto.UpdateId, 120);
            entity.UpdateSource = NormalizeText(dto.UpdateSource, 40);
            entity.LastSeenAtUtc = now;
            entity.LastAuthMode = NormalizeText(authContext.AuthMode, 30) ?? "unknown";
            entity.LastSeenUserGuid = NormalizeText(authContext.UserGuid, 50);
            entity.LastSeenUsername = NormalizeText(authContext.Username, 100);
            entity.LastSeenUserFullName = NormalizeText(authContext.UserFullName, 160);
            entity.RegisteredDeviceId = registeredDevice?.ID;
            entity.IsDeleted = false;
        }

        private static MobileAppDeviceStatusDto MapToDto(
            MobileAppDeviceStatus entity,
            DateTime onlineThreshold
        )
        {
            return new MobileAppDeviceStatusDto
            {
                Id = entity.Id,
                HardwareId = entity.HardwareId,
                SystemDeviceNumber = entity.SystemDeviceNumber,
                DeviceSystem = entity.DeviceSystem,
                Platform = entity.Platform,
                StoreCode = entity.StoreCode,
                AppVersion = entity.AppVersion,
                AppBuildVersion = entity.AppBuildVersion,
                RuntimeVersion = entity.RuntimeVersion,
                Channel = entity.Channel,
                UpdateId = entity.UpdateId,
                UpdateSource = entity.UpdateSource,
                LastSeenAtUtc = entity.LastSeenAtUtc,
                IsOnline = entity.LastSeenAtUtc >= onlineThreshold,
                LastAuthMode = entity.LastAuthMode,
                LastSeenUserGuid = entity.LastSeenUserGuid,
                LastSeenUsername = entity.LastSeenUsername,
                LastSeenUserFullName = entity.LastSeenUserFullName,
                RegisteredDeviceId = entity.RegisteredDeviceId,
            };
        }

        private static DateTime GetOnlineThreshold(DateTime now)
        {
            return now.AddMinutes(-OnlineWindowMinutes);
        }

        private static DateTime EnsureUtc(DateTime value)
        {
            return value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static bool IsUniqueHardwareIdConflict(Exception ex)
        {
            var message = ex.ToString();
            return message.Contains("MobileAppDeviceStatus", StringComparison.OrdinalIgnoreCase)
                && (
                    message.Contains("HardwareId", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("IX_MobileAppDeviceStatus_HardwareId", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                );
        }

        private static string ResolveSystemBucket(MobileAppDeviceStatus entity)
        {
            var normalized = NormalizeDeviceSystem(entity.DeviceSystem)
                ?? NormalizeDeviceSystem(entity.Platform);
            return normalized switch
            {
                "Android" => "android",
                "iOS" => "ios",
                _ => "unknown",
            };
        }

        /// <summary>系统展示名：与列表筛选同源，优先 DeviceSystem，缺失时回退 Platform。</summary>
        private static string? ResolveSystemLabel(MobileAppDeviceStatus entity)
        {
            return NormalizeDeviceSystem(entity.DeviceSystem) ?? NormalizeDeviceSystem(entity.Platform);
        }

        private static bool IsUpdateSource(MobileAppDeviceStatus entity, string source)
        {
            return string.Equals(entity.UpdateSource?.Trim(), source, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 排序：iOS、Android 在前，其他系统按名称，未知系统最后；同系统内版本号从新到旧，未上报版本垫底。
        /// </summary>
        private static int CompareDistributionItems(
            MobileAppVersionDistributionItemDto left,
            MobileAppVersionDistributionItemDto right
        )
        {
            var result = GetSystemRank(left.DeviceSystem).CompareTo(GetSystemRank(right.DeviceSystem));
            if (result != 0)
            {
                return result;
            }

            result = string.Compare(left.DeviceSystem, right.DeviceSystem, StringComparison.OrdinalIgnoreCase);
            if (result != 0)
            {
                return result;
            }

            result = CompareVersionTextDescending(left.AppVersion, right.AppVersion);
            return result != 0
                ? result
                : CompareVersionTextDescending(left.AppBuildVersion, right.AppBuildVersion);
        }

        private static int GetSystemRank(string? system)
        {
            return system?.ToLowerInvariant() switch
            {
                "ios" => 0,
                "android" => 1,
                null => 3,
                _ => 2,
            };
        }

        /// <summary>
        /// 版本文本从新到旧比较：按「.」或「-」分段，两边都是数字时按数值比（1.0.10 大于 1.0.9），
        /// 否则按字符；空值视为最旧，排在最后。
        /// </summary>
        private static int CompareVersionTextDescending(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return string.IsNullOrWhiteSpace(left) == string.IsNullOrWhiteSpace(right)
                    ? 0
                    : string.IsNullOrWhiteSpace(left) ? 1 : -1;
            }

            var leftParts = left.Split('.', '-');
            var rightParts = right.Split('.', '-');
            for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
            {
                // 较短的一方缺段，视为更旧（1.0 比 1.0.1 旧）。
                if (index >= leftParts.Length)
                {
                    return 1;
                }

                if (index >= rightParts.Length)
                {
                    return -1;
                }

                var result =
                    long.TryParse(leftParts[index], out var leftNumber)
                    && long.TryParse(rightParts[index], out var rightNumber)
                        ? leftNumber.CompareTo(rightNumber)
                        : string.Compare(leftParts[index], rightParts[index], StringComparison.OrdinalIgnoreCase);
                if (result != 0)
                {
                    return -result;
                }
            }

            return 0;
        }

        private static string? NormalizeDeviceSystem(string? value)
        {
            var normalized = NormalizeText(value, 30);
            if (normalized == null)
            {
                return null;
            }

            return normalized.ToLowerInvariant() switch
            {
                "android" => "Android",
                "ios" => "iOS",
                "iphone" => "iOS",
                "ipad" => "iOS",
                _ => normalized,
            };
        }

        private static string? NormalizePlatform(string? value)
        {
            var normalized = NormalizeText(value, 30);
            return normalized?.ToLowerInvariant() switch
            {
                "android" => "android",
                "ios" => "ios",
                _ => normalized,
            };
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private static string? NormalizeText(string? value, int maxLength)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }
    }

    public sealed record MobileAppDeviceHeartbeatAuthContext(
        string AuthMode,
        string? UserGuid,
        string? Username,
        string? UserFullName,
        bool IsDeviceSessionVerified = false
    );
}
