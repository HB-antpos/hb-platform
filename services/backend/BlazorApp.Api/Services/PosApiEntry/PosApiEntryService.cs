using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using SqlSugar;

namespace BlazorApp.Api.Services.PosApiEntry;

/// <summary>
/// 后台按「分店」设置收银端访问 POS API 的首选入口（edge / aws / direct），并展示设备实际走的入口。
/// 值存 dbo.Store.PosApiEntryMode（迁移 20261024.003），由 POS API 通过 receipt-profile/sync 下发；
/// 设备实际入口来自设备表 LastApiEntry / LastApiEntryAt（POS API 读 X-HB-Entry 回写，迁移 20261024.004）。
/// </summary>
public interface IPosApiEntryService
{
    Task<ApiResponse<List<PosApiEntryStoreDto>>> GetStoresAsync(CancellationToken cancellationToken = default);

    Task<ApiResponse<PosApiEntryUpdateResultDto>> UpdateAsync(
        PosApiEntryUpdateRequestDto? request,
        string? updatedBy,
        CancellationToken cancellationToken = default);
}

public sealed class PosApiEntryService : IPosApiEntryService
{
    public const int MaxStoresPerRequest = 200;
    private const int MaxUpdatedByLength = 64;
    private const string PosDeviceType = "POS";
    private const int EnabledDeviceStatus = 1;

    private readonly ISqlSugarClient _mainDb;
    private readonly ISqlSugarClient _posmDb;
    private readonly ICurrentUserManageableStoreScopeService _storeScopeService;
    private readonly ILogger<PosApiEntryService> _logger;
    private readonly TimeProvider _timeProvider;

    public PosApiEntryService(
        SqlSugarContext mainContext,
        POSMSqlSugarContext posmContext,
        ICurrentUserManageableStoreScopeService storeScopeService,
        ILogger<PosApiEntryService> logger,
        TimeProvider? timeProvider = null)
        : this(mainContext.Db, posmContext.Db, storeScopeService, logger, timeProvider)
    {
    }

    internal PosApiEntryService(
        ISqlSugarClient mainDb,
        ISqlSugarClient posmDb,
        ICurrentUserManageableStoreScopeService storeScopeService,
        ILogger<PosApiEntryService> logger,
        TimeProvider? timeProvider = null)
    {
        _mainDb = mainDb;
        _posmDb = posmDb;
        _storeScopeService = storeScopeService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ───────────────────────── GET ─────────────────────────

    public async Task<ApiResponse<List<PosApiEntryStoreDto>>> GetStoresAsync(
        CancellationToken cancellationToken = default)
    {
        var scope = await _storeScopeService.GetScopeAsync();
        if (!scope.IsAllowed)
        {
            return ApiResponse<List<PosApiEntryStoreDto>>.Error(
                scope.Message.Length > 0 ? scope.Message : "当前账号没有可管理分店",
                PosApiEntryErrorCodes.ScopeForbidden);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var scopedCodes = scope.IsAdmin ? null : scope.StoreCodes.ToList();
        if (scopedCodes is { Count: 0 })
        {
            return ApiResponse<List<PosApiEntryStoreDto>>.OK(new());
        }

        // 启用的收银设备（设备类型 POS：WPF / 手持 / iPad）。设备类型在内存里按大小写不敏感比较
        // （SQL Server 列排序规则本就不区分，SQLite 测试库区分）。
        var deviceQuery = _posmDb
            .Queryable<POSM_设备注册信息表>()
            .Where(device => device.设备状态 == EnabledDeviceStatus && device.分店代码 != null);
        if (scopedCodes is not null)
        {
            deviceQuery = deviceQuery.Where(device => scopedCodes.Contains(device.分店代码!));
        }

        var devices = (await deviceQuery
                .Select(device => new DeviceRow
                {
                    StoreCode = device.分店代码!,
                    DeviceType = device.设备类型,
                    DeviceSystem = device.设备系统,
                    IsOnline = device.是否在线,
                    LastHeartbeatAt = device.最后心跳时间,
                    AppVersion = device.应用版本,
                    LastApiEntry = device.LastApiEntry,
                    LastApiEntryAt = device.LastApiEntryAt,
                })
                .ToListAsync())
            .Where(device => string.Equals(device.DeviceType?.Trim(), PosDeviceType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var deviceStoreCodes = devices
            .Select(device => device.StoreCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (deviceStoreCodes.Count == 0)
        {
            return ApiResponse<List<PosApiEntryStoreDto>>.OK(new());
        }

        var stores = await _mainDb
            .Queryable<Store>()
            .Where(store => deviceStoreCodes.Contains(store.StoreCode) && store.IsActive && !store.IsDeleted)
            .OrderBy(store => store.StoreCode)
            .Select(store => new
            {
                store.StoreGUID,
                store.StoreCode,
                store.StoreName,
                store.PosApiEntryMode,
                store.PosApiEntryUpdatedAt,
                store.PosApiEntryUpdatedBy,
            })
            .ToListAsync();

        var devicesByStore = devices
            .GroupBy(device => device.StoreCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var items = stores
            .Select(store =>
            {
                // 库里的脏值（绕过 CHECK 约束的历史数据）按未设置展示，与 POS API 下发口径一致。
                var mode = PosApiEntryModes.Normalize(store.PosApiEntryMode);
                var storeDevices = devicesByStore.GetValueOrDefault(store.StoreCode) ?? [];
                return new PosApiEntryStoreDto
                {
                    StoreGuid = store.StoreGUID,
                    StoreCode = store.StoreCode,
                    StoreName = store.StoreName,
                    Mode = mode,
                    EffectiveMode = mode ?? PosApiEntryModes.Default,
                    UpdatedAt = DeviceRuntimeTime.AsUtc(store.PosApiEntryUpdatedAt),
                    UpdatedBy = store.PosApiEntryUpdatedBy,
                    Devices = BuildStats(storeDevices, mode ?? PosApiEntryModes.Default, nowUtc),
                };
            })
            .ToList();

        return ApiResponse<List<PosApiEntryStoreDto>>.OK(items);
    }

    internal static PosApiEntryDeviceStatsDto BuildStats(
        IReadOnlyCollection<DeviceRow> devices,
        string preferredMode,
        DateTime nowUtc)
    {
        var stats = new PosApiEntryDeviceStatsDto { Total = devices.Count };
        foreach (var device in devices)
        {
            var unsupported = PosApiEntryRules.IsUnsupportedWpf(device.DeviceSystem, device.AppVersion);
            if (unsupported)
            {
                stats.UnsupportedCount++;
            }

            var heartbeatUtc = DeviceRuntimeTime.AsUtc(device.LastHeartbeatAt);
            if (!PosApiEntryRules.IsOnline(device.IsOnline, heartbeatUtc, nowUtc))
            {
                continue;
            }

            stats.Online++;
            // 版本过低的 WPF 不认后台设置，实际按旧默认走 EdgeOne：它们只计入 unsupportedCount，
            // 不计入 byEntry 的 edge / aws / direct，也不算回退（否则同一台设备会在面板上被重复统计）。
            // 为保持「byEntry 四项之和 = 在线设备数」，在线的这类设备并入 unknown。
            var entry = unsupported
                ? null
                : PosApiEntryRules.FreshEntry(
                    device.LastApiEntry,
                    DeviceRuntimeTime.AsUtc(device.LastApiEntryAt),
                    nowUtc);
            switch (entry)
            {
                case PosApiEntryModes.Edge: stats.ByEntry.Edge++; break;
                case PosApiEntryModes.Aws: stats.ByEntry.Aws++; break;
                case PosApiEntryModes.Direct: stats.ByEntry.Direct++; break;
                default: stats.ByEntry.Unknown++; break;
            }

            // 回退中：入口已知且新鲜、却不是首选入口。未知入口不算回退。
            if (entry is not null && !string.Equals(entry, preferredMode, StringComparison.Ordinal))
            {
                stats.FallbackCount++;
            }
        }

        return stats;
    }

    // ───────────────────────── PUT ─────────────────────────

    public async Task<ApiResponse<PosApiEntryUpdateResultDto>> UpdateAsync(
        PosApiEntryUpdateRequestDto? request,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        // mode 只能是白名单三值，不提供清空 / NULL：大小写不同的合法值归一化后接受，其余一律 400。
        var mode = PosApiEntryModes.Normalize(request?.Mode);
        if (mode is null)
        {
            return ApiResponse<PosApiEntryUpdateResultDto>.Error(
                "mode 只能是 edge、aws 或 direct",
                PosApiEntryErrorCodes.InvalidMode);
        }

        var guids = ValidateStoreGuids(request!.StoreGuids, out var requestError);
        if (guids is null)
        {
            return ApiResponse<PosApiEntryUpdateResultDto>.Error(requestError!, PosApiEntryErrorCodes.InvalidRequest);
        }

        var scope = await _storeScopeService.GetScopeAsync();
        if (!scope.IsAllowed)
        {
            return ApiResponse<PosApiEntryUpdateResultDto>.Error(
                scope.Message.Length > 0 ? scope.Message : "当前账号没有可管理分店",
                PosApiEntryErrorCodes.ScopeForbidden);
        }

        // 每家分店都必须在用户可管理范围内；有一家越权就整批拒绝，不做部分写入。
        var forbidden = guids.Where(guid => !scope.CanAccessStoreGuid(guid)).ToList();
        if (forbidden.Count > 0)
        {
            return ApiResponse<PosApiEntryUpdateResultDto>.Error(
                "无权修改部分分店的收银入口",
                PosApiEntryErrorCodes.StoreForbidden);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stores = await _mainDb
            .Queryable<Store>()
            .Where(store => guids.Contains(store.StoreGUID) && !store.IsDeleted)
            .Select(store => new { store.StoreGUID, store.StoreCode, store.PosApiEntryMode })
            .ToListAsync();
        var foundGuids = stores.Select(store => store.StoreGUID).ToHashSet(StringComparer.Ordinal);
        var missing = guids.Where(guid => !foundGuids.Contains(guid)).ToList();
        if (missing.Count > 0)
        {
            return ApiResponse<PosApiEntryUpdateResultDto>.Error(
                "部分分店不存在或已删除",
                PosApiEntryErrorCodes.StoreNotFound);
        }

        // 只改 mode 实际变化的行：NULL（未设置）→ 任一白名单值都算变化；脏值也会被规整为目标值。
        var changed = stores
            .Where(store => !string.Equals(store.PosApiEntryMode, mode, StringComparison.Ordinal))
            .ToList();
        var skipped = stores.Count - changed.Count;
        if (changed.Count == 0)
        {
            return ApiResponse<PosApiEntryUpdateResultDto>.OK(
                new PosApiEntryUpdateResultDto { Updated = 0, Skipped = skipped });
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var actor = NormalizeActor(updatedBy);
        var changedGuids = changed.Select(store => store.StoreGUID).ToList();
        // 批量 UPDATE 只写这 3 列，不碰 BaseEntity.UpdatedAt 等其它分店资料字段。
        var affected = await _mainDb
            .Updateable<Store>()
            .SetColumns(store => store.PosApiEntryMode == mode)
            .SetColumns(store => store.PosApiEntryUpdatedAt == now)
            .SetColumns(store => store.PosApiEntryUpdatedBy == actor)
            .Where(store => changedGuids.Contains(store.StoreGUID))
            .ExecuteCommandAsync();

        // 操作日志：沿用主 API 现有做法（结构化日志，无独立的操作日志表），记录操作人、目标入口与每家分店的前后值。
        _logger.LogInformation(
            "收银入口首选已修改：操作人 {Actor}，目标 {Mode}，实际更新 {Updated} 家、跳过 {Skipped} 家，明细 {Changes}",
            actor,
            mode,
            affected,
            skipped,
            string.Join(
                "; ",
                changed.Select(store =>
                    $"{store.StoreCode}:{(string.IsNullOrEmpty(store.PosApiEntryMode) ? "(未设置)" : store.PosApiEntryMode)}->{mode}")));

        return ApiResponse<PosApiEntryUpdateResultDto>.OK(
            new PosApiEntryUpdateResultDto { Updated = affected, Skipped = skipped + (changed.Count - affected) });
    }

    private static List<string>? ValidateStoreGuids(IReadOnlyList<string>? storeGuids, out string? error)
    {
        error = null;
        if (storeGuids is null || storeGuids.Count == 0)
        {
            error = "请选择至少一家分店";
            return null;
        }

        if (storeGuids.Count > MaxStoresPerRequest)
        {
            error = $"一次最多修改 {MaxStoresPerRequest} 家分店";
            return null;
        }

        var result = new List<string>(storeGuids.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in storeGuids)
        {
            var guid = raw?.Trim();
            if (string.IsNullOrEmpty(guid))
            {
                error = "分店 GUID 不能为空";
                return null;
            }

            // 重复的 GUID 静默去重，不当作错误。
            if (seen.Add(guid))
            {
                result.Add(guid);
            }
        }

        return result;
    }

    private static string NormalizeActor(string? updatedBy)
    {
        var actor = string.IsNullOrWhiteSpace(updatedBy) ? "system" : updatedBy.Trim();
        return actor.Length > MaxUpdatedByLength ? actor[..MaxUpdatedByLength] : actor;
    }

    internal sealed class DeviceRow
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? DeviceType { get; set; }
        public string? DeviceSystem { get; set; }
        public bool IsOnline { get; set; }
        public DateTime? LastHeartbeatAt { get; set; }
        public string? AppVersion { get; set; }
        public string? LastApiEntry { get; set; }
        public DateTime? LastApiEntryAt { get; set; }
    }
}
