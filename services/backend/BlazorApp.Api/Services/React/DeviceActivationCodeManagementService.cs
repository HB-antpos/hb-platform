using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using BlazorApp.Shared.Security;
using SqlSugar;
using System.Linq.Expressions;

namespace BlazorApp.Api.Services.React;

public sealed class DeviceActivationCodeManagementService
{
    private static readonly HashSet<int> AllowedValidForMinutes = [30, 120, 1440];
    private static readonly HashSet<string> AllowedDeviceSystems =
        // AndroidTablet：安卓平板大屏收银，开通码与 iPadOS 分开签发，消费时由 POS API 精确匹配平台。
        new(StringComparer.Ordinal) { "Windows", "iPadOS", "Android", "iOS", "AndroidTablet" };
    private static readonly Expression<Func<DeviceActivationCodeGrant, DeviceActivationCodeGrant>>
        ManagementProjection = grant => new DeviceActivationCodeGrant
        {
            GrantId = grant.GrantId,
            StoreCode = grant.StoreCode,
            DeviceSystem = grant.DeviceSystem,
            CreatedAtUtc = grant.CreatedAtUtc,
            CreatedBy = grant.CreatedBy,
            Reason = grant.Reason,
            ExpiresAtUtc = grant.ExpiresAtUtc,
            RevokedAtUtc = grant.RevokedAtUtc,
            RevokedBy = grant.RevokedBy,
            RevokeReason = grant.RevokeReason,
            ConsumedAtUtc = grant.ConsumedAtUtc,
            ConsumedHardwareId = grant.ConsumedHardwareId,
            ConsumedDeviceCode = grant.ConsumedDeviceCode,
            ConsumptionKind = grant.ConsumptionKind,
            PreviousStoreCode = grant.PreviousStoreCode,
            PreviousDeviceCode = grant.PreviousDeviceCode,
        };

    private readonly ISqlSugarClient _posmDb;
    private readonly ISqlSugarClient _mainDb;
    private readonly ICurrentUserManageableStoreScopeService _storeScopeService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeviceActivationCodeManagementService> _logger;

    public DeviceActivationCodeManagementService(
        POSMSqlSugarContext posmContext,
        SqlSugarContext mainContext,
        ICurrentUserManageableStoreScopeService storeScopeService,
        ILogger<DeviceActivationCodeManagementService> logger,
        TimeProvider? timeProvider = null)
        : this(
            posmContext.Db,
            mainContext.Db,
            storeScopeService,
            logger,
            timeProvider)
    {
    }

    internal DeviceActivationCodeManagementService(
        ISqlSugarClient posmDb,
        ISqlSugarClient mainDb,
        ICurrentUserManageableStoreScopeService storeScopeService,
        ILogger<DeviceActivationCodeManagementService> logger,
        TimeProvider? timeProvider = null)
    {
        _posmDb = posmDb;
        _mainDb = mainDb;
        _storeScopeService = storeScopeService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ApiResponse<List<DeviceActivationCodeManageableStoreDto>>> GetManageableStoresAsync()
    {
        var scope = await _storeScopeService.GetScopeAsync();
        if (!scope.IsAllowed)
        {
            return ApiResponse<List<DeviceActivationCodeManageableStoreDto>>.Error(
                scope.Message.Length > 0 ? scope.Message : "当前账号没有可管理分店",
                "DEVICE_ACTIVATION_STORE_SCOPE_FORBIDDEN");
        }

        var storeCodes = scope.StoreCodes.ToList();
        var query = _mainDb.Queryable<Store>().Where(store => store.IsActive && !store.IsDeleted);
        if (!scope.IsAdmin)
        {
            query = query.Where(store => storeCodes.Contains(store.StoreCode));
        }

        var stores = await query
            .OrderBy(store => store.StoreCode)
            .Select(store => new { store.StoreCode, store.StoreName })
            .ToListAsync();
        return ApiResponse<List<DeviceActivationCodeManageableStoreDto>>.OK(
            stores.Select(store => new DeviceActivationCodeManageableStoreDto(
                store.StoreCode,
                DeviceActivationCodeCodec.RedactReservedActivationMetadata(store.StoreName)
                    ?? string.Empty)).ToList());
    }

    public async Task<ApiResponse<PagedResult<DeviceActivationCodeGrantDto>>> ListAsync(
        int page,
        int pageSize,
        string? storeCode,
        string? deviceSystem,
        string? status)
    {
        var scope = await _storeScopeService.GetScopeAsync();
        if (!scope.IsAllowed)
        {
            return ApiResponse<PagedResult<DeviceActivationCodeGrantDto>>.Error(
                scope.Message.Length > 0 ? scope.Message : "当前账号没有可管理分店",
                "DEVICE_ACTIVATION_STORE_SCOPE_FORBIDDEN");
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var normalizedStore = Normalize(storeCode);
        var normalizedSystem = Normalize(deviceSystem);
        var normalizedStatus = Normalize(status);
        if (normalizedStore != null && !scope.CanAccessStoreCode(normalizedStore))
        {
            return ApiResponse<PagedResult<DeviceActivationCodeGrantDto>>.Error(
                "无权查看该分店的设备开通码",
                "DEVICE_ACTIVATION_STORE_FORBIDDEN");
        }

        if (normalizedSystem != null && !AllowedDeviceSystems.Contains(normalizedSystem))
        {
            return ApiResponse<PagedResult<DeviceActivationCodeGrantDto>>.Error(
                "deviceSystem 无效",
                "DEVICE_ACTIVATION_SYSTEM_INVALID");
        }

        var now = UtcNow();
        var accessibleStores = scope.StoreCodes.ToList();
        var query = _posmDb.Queryable<DeviceActivationCodeGrant>();
        if (!scope.IsAdmin)
        {
            query = query.Where(grant => accessibleStores.Contains(grant.StoreCode));
        }
        if (normalizedStore != null)
        {
            query = query.Where(grant => grant.StoreCode == normalizedStore);
        }
        if (normalizedSystem != null)
        {
            query = query.Where(grant => grant.DeviceSystem == normalizedSystem);
        }

        query = normalizedStatus switch
        {
            "Available" => query.Where(grant =>
                grant.ConsumedAtUtc == null
                && grant.RevokedAtUtc == null
                && grant.ExpiresAtUtc > now),
            "Consumed" => query.Where(grant => grant.ConsumedAtUtc != null),
            "Revoked" => query.Where(grant =>
                grant.ConsumedAtUtc == null && grant.RevokedAtUtc != null),
            "Expired" => query.Where(grant =>
                grant.ConsumedAtUtc == null
                && grant.RevokedAtUtc == null
                && grant.ExpiresAtUtc <= now),
            null => query,
            _ => null!,
        };
        if (query == null)
        {
            return ApiResponse<PagedResult<DeviceActivationCodeGrantDto>>.Error(
                "status 无效",
                "DEVICE_ACTIVATION_STATUS_INVALID");
        }

        var total = await query.CountAsync();
        var grants = await query
            .OrderByDescending(grant => grant.CreatedAtUtc)
            .Select(ManagementProjection)
            .ToPageListAsync(page, pageSize);
        var storeNames = await LoadStoreNamesAsync(grants.Select(grant => grant.StoreCode));
        var result = new PagedResult<DeviceActivationCodeGrantDto>
        {
            Items = grants.Select(grant => Map(grant, storeNames, now)).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
        return ApiResponse<PagedResult<DeviceActivationCodeGrantDto>>.OK(result);
    }

    public async Task<ApiResponse<DeviceActivationCodeCreateResponseDto>> CreateAsync(
        DeviceActivationCodeCreateRequestDto request,
        string actor)
    {
        var storeCode = Normalize(request.StoreCode);
        var deviceSystem = Normalize(request.DeviceSystem);
        var reason = NormalizeReason(request.Reason);
        if (storeCode == null)
        {
            return ApiResponse<DeviceActivationCodeCreateResponseDto>.Error(
                "storeCode 不能为空",
                "DEVICE_ACTIVATION_STORE_REQUIRED");
        }
        if (deviceSystem == null || !AllowedDeviceSystems.Contains(deviceSystem))
        {
            return ApiResponse<DeviceActivationCodeCreateResponseDto>.Error(
                "deviceSystem 无效",
                "DEVICE_ACTIVATION_SYSTEM_INVALID");
        }
        if (!AllowedValidForMinutes.Contains(request.ValidForMinutes))
        {
            return ApiResponse<DeviceActivationCodeCreateResponseDto>.Error(
                "validForMinutes 仅允许 30、120 或 1440",
                "DEVICE_ACTIVATION_VALIDITY_INVALID");
        }
        if (reason == null)
        {
            return ApiResponse<DeviceActivationCodeCreateResponseDto>.Error(
                "reason 不能为空且不能超过 200 个字符",
                "DEVICE_ACTIVATION_REASON_INVALID");
        }
        if (!await _storeScopeService.CanAccessStoreCodeAsync(storeCode))
        {
            return ApiResponse<DeviceActivationCodeCreateResponseDto>.Error(
                "无权管理该分店的设备开通码",
                "DEVICE_ACTIVATION_STORE_FORBIDDEN");
        }

        var store = await _mainDb.Queryable<Store>()
            .Where(item => item.StoreCode == storeCode && item.IsActive && !item.IsDeleted)
            .Select(item => new { item.StoreCode, item.StoreName })
            .FirstAsync();
        if (store == null)
        {
            return ApiResponse<DeviceActivationCodeCreateResponseDto>.Error(
                "分店不存在或已停用",
                "DEVICE_ACTIVATION_STORE_UNAVAILABLE");
        }

        var material = DeviceActivationCodeCodec.Create();
        var now = UtcNow();
        var grant = new DeviceActivationCodeGrant
        {
            GrantId = material.GrantId,
            SecretHash = material.SecretHash,
            StoreCode = storeCode,
            DeviceSystem = deviceSystem,
            CreatedAtUtc = now,
            CreatedBy = NormalizeActor(actor),
            Reason = reason,
            ExpiresAtUtc = now.AddMinutes(request.ValidForMinutes),
        };
        await _posmDb.Insertable(grant).ExecuteCommandAsync();

        _logger.LogInformation(
            "已创建一次性设备开通码摘要，GrantId={GrantId}, StoreCode={StoreCode}, DeviceSystem={DeviceSystem}, CreatedBy={CreatedBy}, ExpiresAtUtc={ExpiresAtUtc}",
            grant.GrantId,
            grant.StoreCode,
            grant.DeviceSystem,
            grant.CreatedBy,
            grant.ExpiresAtUtc);
        var dto = Map(
            grant,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [store.StoreCode] = store.StoreName,
            },
            now);
        return ApiResponse<DeviceActivationCodeCreateResponseDto>.OK(
            new DeviceActivationCodeCreateResponseDto(dto, material.ActivationCode),
            "设备开通码已创建；明文仅显示本次");
    }

    public async Task<ApiResponse<DeviceActivationCodeGrantDto>> RevokeAsync(
        Guid grantId,
        DeviceActivationCodeRevokeRequestDto request,
        string actor)
    {
        if (grantId == Guid.Empty)
        {
            return ApiResponse<DeviceActivationCodeGrantDto>.Error(
                "grantId 无效",
                "DEVICE_ACTIVATION_GRANT_INVALID");
        }

        var reason = NormalizeReason(request.Reason);
        if (reason == null)
        {
            return ApiResponse<DeviceActivationCodeGrantDto>.Error(
                "reason 不能为空且不能超过 200 个字符",
                "DEVICE_ACTIVATION_REASON_INVALID");
        }

        var grant = await _posmDb.Queryable<DeviceActivationCodeGrant>()
            .Where(item => item.GrantId == grantId)
            .Select(item => new DeviceActivationCodeGrant
            {
                GrantId = item.GrantId,
                StoreCode = item.StoreCode,
            })
            .FirstAsync();
        if (grant == null)
        {
            return ApiResponse<DeviceActivationCodeGrantDto>.Error(
                "设备开通码不存在",
                "DEVICE_ACTIVATION_GRANT_NOT_FOUND");
        }
        if (!await _storeScopeService.CanAccessStoreCodeAsync(grant.StoreCode))
        {
            return ApiResponse<DeviceActivationCodeGrantDto>.Error(
                "无权管理该分店的设备开通码",
                "DEVICE_ACTIVATION_STORE_FORBIDDEN");
        }

        var revokedAtUtc = UtcNow();
        var revokedBy = NormalizeActor(actor);
        var affected = await _posmDb.Updateable<DeviceActivationCodeGrant>()
            .SetColumns(item => new DeviceActivationCodeGrant
            {
                RevokedAtUtc = revokedAtUtc,
                RevokedBy = revokedBy,
                RevokeReason = reason,
            })
            .Where(item => item.GrantId == grantId
                && item.RevokedAtUtc == null
                && item.ConsumedAtUtc == null)
            .ExecuteCommandAsync();
        if (affected != 1)
        {
            return ApiResponse<DeviceActivationCodeGrantDto>.Error(
                "设备开通码已使用或已撤销",
                "DEVICE_ACTIVATION_NOT_REVOCABLE");
        }

        var updated = await _posmDb.Queryable<DeviceActivationCodeGrant>()
            .Where(item => item.GrantId == grantId)
            .Select(ManagementProjection)
            .FirstAsync()
            ?? throw new InvalidOperationException("Revoked device activation grant disappeared.");
        var storeNames = await LoadStoreNamesAsync([updated.StoreCode]);
        _logger.LogInformation(
            "已撤销一次性设备开通码，GrantId={GrantId}, StoreCode={StoreCode}, RevokedBy={RevokedBy}",
            updated.GrantId,
            updated.StoreCode,
            updated.RevokedBy);
        return ApiResponse<DeviceActivationCodeGrantDto>.OK(
            Map(updated, storeNames, UtcNow()),
            "设备开通码已撤销");
    }

    private async Task<Dictionary<string, string>> LoadStoreNamesAsync(IEnumerable<string> storeCodes)
    {
        var codes = storeCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var stores = await _mainDb.Queryable<Store>()
            .Where(store => codes.Contains(store.StoreCode))
            .Select(store => new { store.StoreCode, store.StoreName })
            .ToListAsync();
        return stores.ToDictionary(
            store => store.StoreCode,
            store => store.StoreName,
            StringComparer.OrdinalIgnoreCase);
    }

    internal static DeviceActivationCodeGrantDto Map(
        DeviceActivationCodeGrant grant,
        IReadOnlyDictionary<string, string> storeNames,
        DateTime now)
    {
        var status = grant.ConsumedAtUtc != null
            ? "Consumed"
            : grant.RevokedAtUtc != null
                ? "Revoked"
                : grant.ExpiresAtUtc <= now
                    ? "Expired"
                    : "Available";
        storeNames.TryGetValue(grant.StoreCode, out var storeName);
        return new DeviceActivationCodeGrantDto(
            grant.GrantId,
            RedactRequired(grant.StoreCode),
            DeviceActivationCodeCodec.RedactReservedActivationMetadata(storeName),
            RedactRequired(grant.DeviceSystem),
            status,
            DeviceActivationCodeCodec.NormalizeUtcForWire(grant.CreatedAtUtc),
            RedactRequired(grant.CreatedBy),
            RedactRequired(grant.Reason),
            DeviceActivationCodeCodec.NormalizeUtcForWire(grant.ExpiresAtUtc),
            DeviceActivationCodeCodec.NormalizeUtcForWire(grant.RevokedAtUtc),
            DeviceActivationCodeCodec.RedactReservedActivationMetadata(grant.RevokedBy),
            DeviceActivationCodeCodec.RedactReservedActivationMetadata(grant.RevokeReason),
            DeviceActivationCodeCodec.NormalizeUtcForWire(grant.ConsumedAtUtc),
            DeviceActivationCodeCodec.RedactReservedActivationMetadata(grant.ConsumedHardwareId),
            DeviceActivationCodeCodec.RedactReservedActivationMetadata(grant.ConsumedDeviceCode),
            DeviceActivationCodeCodec.RedactReservedActivationMetadata(grant.ConsumptionKind),
            DeviceActivationCodeCodec.RedactReservedActivationMetadata(grant.PreviousStoreCode),
            DeviceActivationCodeCodec.RedactReservedActivationMetadata(grant.PreviousDeviceCode));
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static string? NormalizeReason(string? value)
    {
        var normalized = Normalize(value);
        return normalized is { Length: <= 200 }
            && !DeviceActivationCodeCodec.ContainsReservedActivationCode(normalized)
            ? normalized
            : null;
    }

    private static string NormalizeActor(string? actor)
    {
        var normalized = Normalize(actor) ?? "System";
        if (DeviceActivationCodeCodec.ContainsReservedActivationCode(normalized))
        {
            return "[REDACTED]";
        }
        return normalized[..Math.Min(128, normalized.Length)];
    }

    private static string RedactRequired(string value) =>
        DeviceActivationCodeCodec.RedactReservedActivationMetadata(value) ?? string.Empty;

}
