using BlazorApp.Api.Data;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using SqlSugar;

namespace BlazorApp.Api.Services.PosSettings;

/// <summary>调用者：用户名（记修改记录）、是否管理员、可访问的门店代码。</summary>
public sealed record PosSettingsActor(string UserName, bool IsAdmin, IReadOnlyCollection<string> StoreCodes)
{
    public bool CanAccessStore(string storeCode) =>
        IsAdmin || StoreCodes.Contains(storeCode, StringComparer.OrdinalIgnoreCase);
}

public static class PosSettingsErrorCodes
{
    public const string InvalidRequest = "POS_SETTINGS_INVALID_REQUEST";
    public const string Forbidden = "POS_SETTINGS_FORBIDDEN";
    public const string NotFound = "POS_SETTINGS_NOT_FOUND";
}

public interface IPosSettingsService
{
    PosSettingsCatalogDto GetCatalog();

    Task<ApiResponse<PosSettingLayerDto>> GetGlobalAsync(CancellationToken cancellationToken);

    Task<ApiResponse<PosSettingLayerDto>> SaveGlobalAsync(
        PosSettingsActor actor,
        SavePosSettingsRequest request,
        CancellationToken cancellationToken);

    Task<ApiResponse<List<PosSettingStoreSummaryDto>>> ListStoresAsync(
        PosSettingsActor actor,
        CancellationToken cancellationToken);

    Task<ApiResponse<PosSettingStoreDetailDto>> GetStoreAsync(
        PosSettingsActor actor,
        string storeCode,
        CancellationToken cancellationToken);

    Task<ApiResponse<PosSettingStoreDetailDto>> SaveStoreAsync(
        PosSettingsActor actor,
        string storeCode,
        SavePosSettingsRequest request,
        CancellationToken cancellationToken);

    Task<ApiResponse<List<PosSettingDeviceStatusDto>>> ListDevicesAsync(
        PosSettingsActor actor,
        string storeCode,
        CancellationToken cancellationToken);

    Task<ApiResponse<PosSettingDeviceDetailDto>> GetDeviceAsync(
        PosSettingsActor actor,
        string deviceCode,
        CancellationToken cancellationToken);

    Task<ApiResponse<PosSettingDeviceDetailDto>> SaveDeviceAsync(
        PosSettingsActor actor,
        string deviceCode,
        SavePosSettingsRequest request,
        CancellationToken cancellationToken);

    Task<ApiResponse<List<PosSettingChangeDto>>> ListChangesAsync(
        PosSettingsActor actor,
        string? scope,
        string? scopeKey,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>
/// 收银端设置下发（后台 / 员工 App 管理侧）：三层已设置值的读写、修改记录、门店与设备的应用情况。
/// 合并与指纹用 <see cref="PosSettingMerger"/>（与 POS API 同一份代码）；设备清单在 POSM 库，回执在主库，按设备编号内存关联。
/// 门店范围：管理员不限；其余账号只能看 / 改自己关联分店及其设备。全局层只有管理员能改。
/// </summary>
public sealed class PosSettingsService : IPosSettingsService
{
    private const string PosDeviceType = "POS";
    private const int EnabledDeviceStatus = 1;
    private const int MaxChangesLimit = 200;

    private readonly ISqlSugarClient _mainDb;
    private readonly ISqlSugarClient _posmDb;
    private readonly TimeProvider _timeProvider;

    public PosSettingsService(SqlSugarContext mainContext, POSMSqlSugarContext posmContext, TimeProvider timeProvider)
        : this(mainContext.Db, posmContext.Db, timeProvider) { }

    internal PosSettingsService(ISqlSugarClient mainDb, ISqlSugarClient posmDb, TimeProvider timeProvider)
    {
        _mainDb = mainDb;
        _posmDb = posmDb;
        _timeProvider = timeProvider;
    }

    public PosSettingsCatalogDto GetCatalog() =>
        new()
        {
            Definitions = PosSettingCatalog.All
                .Select(item => new PosSettingDefinitionDto
                {
                    Key = item.Key,
                    ClientKinds = item.ClientKinds.ToList(),
                    DefaultWhenUnset = item.DefaultWhenUnset,
                    IsNew = item.IsNew,
                })
                .ToList(),
        };

    // ───────────────────────── 全局 ─────────────────────────

    public async Task<ApiResponse<PosSettingLayerDto>> GetGlobalAsync(CancellationToken cancellationToken) =>
        ApiResponse<PosSettingLayerDto>.OK(
            await LoadLayerDtoAsync(PosSettingScopes.Global, PosSettingScopes.GlobalScopeKey, cancellationToken));

    public async Task<ApiResponse<PosSettingLayerDto>> SaveGlobalAsync(
        PosSettingsActor actor,
        SavePosSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (!actor.IsAdmin)
        {
            return Fail<PosSettingLayerDto>("全局设置会影响所有门店，只有管理员可以修改", PosSettingsErrorCodes.Forbidden);
        }

        var error = await ApplyChangesAsync(
            PosSettingScopes.Global,
            PosSettingScopes.GlobalScopeKey,
            request,
            actor,
            cancellationToken);
        return error is not null
            ? Fail<PosSettingLayerDto>(error, PosSettingsErrorCodes.InvalidRequest)
            : await GetGlobalAsync(cancellationToken);
    }

    // ───────────────────────── 门店 ─────────────────────────

    public async Task<ApiResponse<List<PosSettingStoreSummaryDto>>> ListStoresAsync(
        PosSettingsActor actor,
        CancellationToken cancellationToken)
    {
        var stores = await _mainDb.Queryable<Store>().Where(item => !item.IsDeleted).ToListAsync(cancellationToken);
        var visible = stores
            .Where(item => actor.CanAccessStore(item.StoreCode))
            .OrderBy(item => item.StoreCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var codes = visible.Select(item => item.StoreCode).ToList();

        var global = await LoadLayerAsync(PosSettingScopes.Global, PosSettingScopes.GlobalScopeKey, cancellationToken);
        var storeRows = codes.Count == 0
            ? []
            : await _mainDb.Queryable<PosSettingValue>()
                .Where(item => item.Scope == PosSettingScopes.Store && codes.Contains(item.ScopeKey))
                .ToListAsync(cancellationToken);
        var storeLayers = storeRows
            .Where(item => PosSettingCatalog.IsKnownKey(item.SettingKey))
            .GroupBy(item => item.ScopeKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(item => item.SettingKey, item => item.Value, StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase);

        var devices = await LoadDeviceViewAsync(codes, cancellationToken);
        var result = new List<PosSettingStoreSummaryDto>(visible.Count);
        foreach (var store in visible)
        {
            storeLayers.TryGetValue(store.StoreCode, out var overrides);
            var statuses = BuildDeviceStatuses(store.StoreCode, global, overrides, devices);
            result.Add(new PosSettingStoreSummaryDto
            {
                StoreCode = store.StoreCode,
                StoreName = store.StoreName,
                Overrides = overrides ?? [],
                DeviceOverrideCount = statuses.Count(item => item.HasDeviceOverride),
                DeviceTotal = statuses.Count,
                DeviceApplied = statuses.Count(item => item.Status == PosSettingDeviceStatuses.Applied),
            });
        }

        return ApiResponse<List<PosSettingStoreSummaryDto>>.OK(result);
    }

    public async Task<ApiResponse<PosSettingStoreDetailDto>> GetStoreAsync(
        PosSettingsActor actor,
        string storeCode,
        CancellationToken cancellationToken)
    {
        var store = await FindAccessibleStoreAsync(actor, storeCode, cancellationToken);
        return store.Error is not null
            ? Fail<PosSettingStoreDetailDto>(store.Error.Value.Message, store.Error.Value.Code)
            : ApiResponse<PosSettingStoreDetailDto>.OK(await BuildStoreDetailAsync(store.Store!, cancellationToken));
    }

    public async Task<ApiResponse<PosSettingStoreDetailDto>> SaveStoreAsync(
        PosSettingsActor actor,
        string storeCode,
        SavePosSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var store = await FindAccessibleStoreAsync(actor, storeCode, cancellationToken);
        if (store.Error is not null)
        {
            return Fail<PosSettingStoreDetailDto>(store.Error.Value.Message, store.Error.Value.Code);
        }

        var error = await ApplyChangesAsync(PosSettingScopes.Store, store.Store!.StoreCode, request, actor, cancellationToken);
        return error is not null
            ? Fail<PosSettingStoreDetailDto>(error, PosSettingsErrorCodes.InvalidRequest)
            : ApiResponse<PosSettingStoreDetailDto>.OK(await BuildStoreDetailAsync(store.Store!, cancellationToken));
    }

    private async Task<PosSettingStoreDetailDto> BuildStoreDetailAsync(Store store, CancellationToken cancellationToken)
    {
        var global = await LoadLayerAsync(PosSettingScopes.Global, PosSettingScopes.GlobalScopeKey, cancellationToken);
        var layer = await LoadLayerDtoAsync(PosSettingScopes.Store, store.StoreCode, cancellationToken);
        var devices = await LoadDeviceViewAsync([store.StoreCode], cancellationToken);
        return new PosSettingStoreDetailDto
        {
            StoreCode = store.StoreCode,
            StoreName = store.StoreName,
            Overrides = layer.Values,
            Effective = ToEffectiveDto(PosSettingMerger.Merge(global, layer.Values, null)),
            Devices = BuildDeviceStatuses(store.StoreCode, global, layer.Values, devices),
            UpdatedAtUtc = layer.UpdatedAtUtc,
            UpdatedByName = layer.UpdatedByName,
        };
    }

    // ───────────────────────── 设备 ─────────────────────────

    public async Task<ApiResponse<List<PosSettingDeviceStatusDto>>> ListDevicesAsync(
        PosSettingsActor actor,
        string storeCode,
        CancellationToken cancellationToken)
    {
        var store = await FindAccessibleStoreAsync(actor, storeCode, cancellationToken);
        if (store.Error is not null)
        {
            return Fail<List<PosSettingDeviceStatusDto>>(store.Error.Value.Message, store.Error.Value.Code);
        }

        var global = await LoadLayerAsync(PosSettingScopes.Global, PosSettingScopes.GlobalScopeKey, cancellationToken);
        var storeLayer = await LoadLayerAsync(PosSettingScopes.Store, store.Store!.StoreCode, cancellationToken);
        var devices = await LoadDeviceViewAsync([store.Store.StoreCode], cancellationToken);
        return ApiResponse<List<PosSettingDeviceStatusDto>>.OK(
            BuildDeviceStatuses(store.Store.StoreCode, global, storeLayer, devices));
    }

    public async Task<ApiResponse<PosSettingDeviceDetailDto>> GetDeviceAsync(
        PosSettingsActor actor,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var located = await FindAccessibleDeviceAsync(actor, deviceCode, cancellationToken);
        return located.Error is not null
            ? Fail<PosSettingDeviceDetailDto>(located.Error.Value.Message, located.Error.Value.Code)
            : ApiResponse<PosSettingDeviceDetailDto>.OK(
                await BuildDeviceDetailAsync(located.Device!, located.Store!, cancellationToken));
    }

    public async Task<ApiResponse<PosSettingDeviceDetailDto>> SaveDeviceAsync(
        PosSettingsActor actor,
        string deviceCode,
        SavePosSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var located = await FindAccessibleDeviceAsync(actor, deviceCode, cancellationToken);
        if (located.Error is not null)
        {
            return Fail<PosSettingDeviceDetailDto>(located.Error.Value.Message, located.Error.Value.Code);
        }

        var error = await ApplyChangesAsync(
            PosSettingScopes.Device,
            located.Device!.系统设备编号.Trim(),
            request,
            actor,
            cancellationToken);
        return error is not null
            ? Fail<PosSettingDeviceDetailDto>(error, PosSettingsErrorCodes.InvalidRequest)
            : ApiResponse<PosSettingDeviceDetailDto>.OK(
                await BuildDeviceDetailAsync(located.Device!, located.Store!, cancellationToken));
    }

    private async Task<PosSettingDeviceDetailDto> BuildDeviceDetailAsync(
        POSM_设备注册信息表 device,
        Store store,
        CancellationToken cancellationToken)
    {
        var code = device.系统设备编号.Trim();
        var global = await LoadLayerAsync(PosSettingScopes.Global, PosSettingScopes.GlobalScopeKey, cancellationToken);
        var storeLayer = await LoadLayerAsync(PosSettingScopes.Store, store.StoreCode, cancellationToken);
        var deviceLayer = await LoadLayerAsync(PosSettingScopes.Device, code, cancellationToken);
        var view = await LoadDeviceViewAsync([store.StoreCode], cancellationToken);
        var status = BuildDeviceStatuses(store.StoreCode, global, storeLayer, view)
            .First(item => string.Equals(item.DeviceCode, code, StringComparison.OrdinalIgnoreCase));
        var clientKind = PosSettingMerger.ClientKindFromDeviceSystem(device.设备系统);
        view.Acks.TryGetValue(code, out var ack);
        return new PosSettingDeviceDetailDto
        {
            Status = status,
            StoreName = store.StoreName,
            Overrides = deviceLayer,
            Effective = ToEffectiveDto(PosSettingMerger.Merge(global, storeLayer, deviceLayer, clientKind)),
            Reported = ParseReported(ack?.EffectiveJson),
        };
    }

    // ───────────────────────── 修改记录 ─────────────────────────

    public async Task<ApiResponse<List<PosSettingChangeDto>>> ListChangesAsync(
        PosSettingsActor actor,
        string? scope,
        string? scopeKey,
        int limit,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit <= 0 ? 50 : limit, 1, MaxChangesLimit);
        var scopeFilter = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim().ToLowerInvariant();
        if (scopeFilter is not null && !PosSettingScopes.IsValid(scopeFilter))
        {
            return Fail<List<PosSettingChangeDto>>("scope 无效", PosSettingsErrorCodes.InvalidRequest);
        }

        var keyFilter = string.IsNullOrWhiteSpace(scopeKey) ? null : scopeKey.Trim();
        var query = _mainDb.Queryable<PosSettingChange>();
        if (scopeFilter is not null)
        {
            query = query.Where(item => item.Scope == scopeFilter);
        }

        if (keyFilter is not null)
        {
            query = query.Where(item => item.ScopeKey == keyFilter);
        }

        if (!actor.IsAdmin)
        {
            // 非管理员只看自己门店层的记录；全局与设备层的记录不外露。
            var codes = actor.StoreCodes.ToList();
            query = query.Where(item => item.Scope == PosSettingScopes.Store && codes.Contains(item.ScopeKey));
        }

        var rows = await query.OrderByDescending(item => item.Id).Take(take).ToListAsync(cancellationToken);
        return ApiResponse<List<PosSettingChangeDto>>.OK(
            rows.Select(row => new PosSettingChangeDto
                {
                    Id = row.Id,
                    Scope = row.Scope,
                    ScopeKey = row.ScopeKey,
                    SettingKey = row.SettingKey,
                    OldValue = row.OldValue,
                    NewValue = row.NewValue,
                    ChangedAtUtc = DateTime.SpecifyKind(row.ChangedAtUtc, DateTimeKind.Utc),
                    ChangedByName = row.ChangedBy,
                })
                .ToList());
    }

    // ───────────────────────── 内部：层读写 ─────────────────────────

    private async Task<Dictionary<string, bool>> LoadLayerAsync(
        string scope,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        var rows = await _mainDb.Queryable<PosSettingValue>()
            .Where(item => item.Scope == scope && item.ScopeKey == scopeKey)
            .ToListAsync(cancellationToken);
        // 清单以后删项时，库里的老数据直接忽略，不让它进入合并与指纹。
        return rows
            .Where(item => PosSettingCatalog.IsKnownKey(item.SettingKey))
            .ToDictionary(item => item.SettingKey, item => item.Value, StringComparer.Ordinal);
    }

    private async Task<PosSettingLayerDto> LoadLayerDtoAsync(
        string scope,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        var rows = await _mainDb.Queryable<PosSettingValue>()
            .Where(item => item.Scope == scope && item.ScopeKey == scopeKey)
            .ToListAsync(cancellationToken);
        var known = rows.Where(item => PosSettingCatalog.IsKnownKey(item.SettingKey)).ToList();
        var latest = known.OrderByDescending(item => item.UpdatedAtUtc).FirstOrDefault();
        return new PosSettingLayerDto
        {
            Scope = scope,
            ScopeKey = scopeKey,
            Values = known.ToDictionary(item => item.SettingKey, item => item.Value, StringComparer.Ordinal),
            UpdatedAtUtc = latest is null ? null : DateTime.SpecifyKind(latest.UpdatedAtUtc, DateTimeKind.Utc),
            UpdatedByName = latest?.UpdatedBy,
        };
    }

    /// <summary>
    /// 应用一次保存：Values 里 true / false = 设置，null = 清除；没出现的项不变。值没变化的项不写库、不记修改记录。
    /// 返回错误文案（参数问题）；成功返回 null。整批在一个事务里。
    /// </summary>
    private async Task<string?> ApplyChangesAsync(
        string scope,
        string scopeKey,
        SavePosSettingsRequest? request,
        PosSettingsActor actor,
        CancellationToken cancellationToken)
    {
        if (request?.Values is null || request.Values.Count == 0)
        {
            return "没有要保存的设置";
        }

        foreach (var key in request.Values.Keys)
        {
            if (!PosSettingCatalog.IsKnownKey(key))
            {
                return $"未知的设置项：{key}";
            }
        }

        var existing = (await _mainDb.Queryable<PosSettingValue>()
                .Where(item => item.Scope == scope && item.ScopeKey == scopeKey)
                .ToListAsync(cancellationToken))
            .ToDictionary(item => item.SettingKey, StringComparer.Ordinal);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var userName = Truncate(actor.UserName, 100);

        var inserts = new List<PosSettingValue>();
        var updates = new List<PosSettingValue>();
        var deletes = new List<PosSettingValue>();
        var changes = new List<PosSettingChange>();
        foreach (var (key, requested) in request.Values)
        {
            existing.TryGetValue(key, out var current);
            bool? oldValue = current?.Value;
            if (oldValue == requested)
            {
                continue;
            }

            changes.Add(new PosSettingChange
            {
                Scope = scope,
                ScopeKey = scopeKey,
                SettingKey = key,
                OldValue = oldValue,
                NewValue = requested,
                ChangedAtUtc = now,
                ChangedBy = userName,
            });
            if (requested is null)
            {
                deletes.Add(current!);
            }
            else if (current is null)
            {
                inserts.Add(new PosSettingValue
                {
                    Scope = scope,
                    ScopeKey = scopeKey,
                    SettingKey = key,
                    Value = requested.Value,
                    UpdatedAtUtc = now,
                    UpdatedBy = userName,
                });
            }
            else
            {
                current.Value = requested.Value;
                current.UpdatedAtUtc = now;
                current.UpdatedBy = userName;
                updates.Add(current);
            }
        }

        if (changes.Count == 0)
        {
            return null;
        }

        await _mainDb.Ado.BeginTranAsync();
        try
        {
            if (inserts.Count > 0)
            {
                await _mainDb.Insertable(inserts).ExecuteCommandAsync(cancellationToken);
            }

            if (updates.Count > 0)
            {
                await _mainDb.Updateable(updates).ExecuteCommandAsync(cancellationToken);
            }

            if (deletes.Count > 0)
            {
                await _mainDb.Deleteable(deletes).ExecuteCommandAsync(cancellationToken);
            }

            await _mainDb.Insertable(changes).ExecuteCommandAsync(cancellationToken);
            await _mainDb.Ado.CommitTranAsync();
        }
        catch
        {
            await _mainDb.Ado.RollbackTranAsync();
            throw;
        }

        return null;
    }

    // ───────────────────────── 内部：门店 / 设备 ─────────────────────────

    private async Task<(Store? Store, (string Message, string Code)? Error)> FindAccessibleStoreAsync(
        PosSettingsActor actor,
        string storeCode,
        CancellationToken cancellationToken)
    {
        var code = storeCode?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            return (null, ("请选择门店", PosSettingsErrorCodes.InvalidRequest));
        }

        // 先判范围再查库：无权门店与不存在的门店对调用方表现一致，避免探测门店代码。
        if (!actor.CanAccessStore(code))
        {
            return (null, ("无权操作该门店的收银端设置", PosSettingsErrorCodes.Forbidden));
        }

        var store = await _mainDb.Queryable<Store>()
            .Where(item => item.StoreCode == code && !item.IsDeleted)
            .FirstAsync(cancellationToken);
        return store is null
            ? (null, ("门店不存在", PosSettingsErrorCodes.NotFound))
            : (store, null);
    }

    private async Task<(POSM_设备注册信息表? Device, Store? Store, (string Message, string Code)? Error)>
        FindAccessibleDeviceAsync(PosSettingsActor actor, string deviceCode, CancellationToken cancellationToken)
    {
        var code = deviceCode?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            return (null, null, ("请选择设备", PosSettingsErrorCodes.InvalidRequest));
        }

        var device = await _posmDb.Queryable<POSM_设备注册信息表>()
            .Where(item => item.系统设备编号 == code)
            .FirstAsync(cancellationToken);
        if (device is null || string.IsNullOrWhiteSpace(device.分店代码))
        {
            return (null, null, ("设备不存在", PosSettingsErrorCodes.NotFound));
        }

        var store = await FindAccessibleStoreAsync(actor, device.分店代码, cancellationToken);
        return store.Error is not null
            ? (null, null, store.Error)
            : (device, store.Store, null);
    }

    private sealed class DeviceView
    {
        public List<POSM_设备注册信息表> Devices { get; init; } = [];
        public Dictionary<string, PosSettingAck> Acks { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, bool>> DeviceLayers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<DeviceView> LoadDeviceViewAsync(
        IReadOnlyCollection<string> storeCodes,
        CancellationToken cancellationToken)
    {
        var codes = storeCodes.ToList();
        if (codes.Count == 0)
        {
            return new DeviceView();
        }

        var candidates = await _posmDb.Queryable<POSM_设备注册信息表>()
            .Where(device => device.设备状态 == EnabledDeviceStatus && codes.Contains(device.分店代码!))
            .ToListAsync(cancellationToken);
        var devices = candidates
            .Where(device => string.Equals(device.设备类型?.Trim(), PosDeviceType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var deviceCodes = devices
            .Select(device => device.系统设备编号.Trim())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var view = new DeviceView { Devices = devices };
        foreach (var chunk in deviceCodes.Chunk(500))
        {
            var batch = chunk.ToList();
            foreach (var ack in await _mainDb.Queryable<PosSettingAck>()
                         .Where(item => batch.Contains(item.DeviceCode))
                         .ToListAsync(cancellationToken))
            {
                view.Acks[ack.DeviceCode.Trim()] = ack;
            }

            var rows = await _mainDb.Queryable<PosSettingValue>()
                .Where(item => item.Scope == PosSettingScopes.Device && batch.Contains(item.ScopeKey))
                .ToListAsync(cancellationToken);
            foreach (var group in rows.Where(item => PosSettingCatalog.IsKnownKey(item.SettingKey))
                         .GroupBy(item => item.ScopeKey.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                view.DeviceLayers[group.Key] = group.ToDictionary(item => item.SettingKey, item => item.Value, StringComparer.Ordinal);
            }
        }

        return view;
    }

    /// <summary>
    /// 一个门店下每台设备的应用状态：用同一份合并 + 指纹算出设备「应该」拿到的指纹，与回执里设备应用的指纹比较。
    /// 设备换店后旧店的回执不计入新店（回执里的门店与当前门店不一致按「从未回执」）。
    /// </summary>
    private List<PosSettingDeviceStatusDto> BuildDeviceStatuses(
        string storeCode,
        IReadOnlyDictionary<string, bool>? global,
        IReadOnlyDictionary<string, bool>? store,
        DeviceView view)
    {
        var result = new List<PosSettingDeviceStatusDto>();
        foreach (var device in view.Devices
                     .Where(item => string.Equals(item.分店代码?.Trim(), storeCode, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => item.系统设备编号, StringComparer.OrdinalIgnoreCase))
        {
            var code = device.系统设备编号.Trim();
            view.DeviceLayers.TryGetValue(code, out var deviceLayer);
            var kind = PosSettingMerger.ClientKindFromDeviceSystem(device.设备系统);
            var expected = PosSettingMerger.ComputeHash(
                PosSettingMerger.Managed(PosSettingMerger.Merge(global, store, deviceLayer, kind)));

            view.Acks.TryGetValue(code, out var ack);
            var sameStore = ack is not null && string.Equals(ack.StoreCode?.Trim(), storeCode, StringComparison.OrdinalIgnoreCase);
            result.Add(new PosSettingDeviceStatusDto
            {
                DeviceCode = code,
                StoreCode = storeCode,
                ClientKind = kind,
                AppVersion = sameStore ? ack!.AppVersion : null,
                Status = !sameStore
                    ? PosSettingDeviceStatuses.Never
                    : string.Equals(ack!.AppliedHash, expected, StringComparison.Ordinal)
                        ? PosSettingDeviceStatuses.Applied
                        : PosSettingDeviceStatuses.Pending,
                AppliedAtUtc = sameStore ? DateTime.SpecifyKind(ack!.AppliedAtUtc, DateTimeKind.Utc) : null,
                HasDeviceOverride = deviceLayer is { Count: > 0 },
                OfflineOverrideCount = sameStore ? ack!.OfflineOverrideCount : 0,
                LastOfflineOverrideAtUtc = sameStore && ack!.LastOfflineOverrideAtUtc is { } last
                    ? DateTime.SpecifyKind(last, DateTimeKind.Utc)
                    : null,
            });
        }

        return result;
    }

    private static Dictionary<string, PosSettingEffectiveValueDto> ToEffectiveDto(
        IReadOnlyDictionary<string, PosSettingEffective> effective) =>
        effective.ToDictionary(
            item => item.Key,
            item => new PosSettingEffectiveValueDto { Value = item.Value.Value, Source = item.Value.Source },
            StringComparer.Ordinal);

    private static Dictionary<string, bool> ParseReported(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
            return parsed?
                       .Where(item => PosSettingCatalog.IsKnownKey(item.Key))
                       .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                   ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            // 回执里的上报值损坏时不让整个详情打不开：当作没有上报。
            return [];
        }
    }

    private static ApiResponse<T> Fail<T>(string message, string code) => ApiResponse<T>.Error(message, code);

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>设备应用状态取值。</summary>
public static class PosSettingDeviceStatuses
{
    public const string Applied = "applied";
    public const string Pending = "pending";
    public const string Never = "never";
}
