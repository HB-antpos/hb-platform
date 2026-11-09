using BlazorApp.Api.Filters;
using System.Security.Claims;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;

namespace BlazorApp.Api.Controllers.React;

[ApiController]
[Route("api/react/v1/store-product-maintenance/hq-sync")]
[AllowAnonymous]
public sealed class ProductHqSyncOperationsController : ControllerBase
{
    private readonly IProductHqSyncOutboxQueue _queue;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAuthorizationService _authorizationService;
    private readonly IDeviceRegistrationService _deviceRegistrationService;
    private readonly IMapper _mapper;
    private readonly ISqlSugarClient _db;
    private readonly ILogger<ProductHqSyncOperationsController> _logger;

    public ProductHqSyncOperationsController(
        IProductHqSyncOutboxQueue queue,
        ICurrentUserService currentUserService,
        IAuthorizationService authorizationService,
        IDeviceRegistrationService deviceRegistrationService,
        IMapper mapper,
        SqlSugarContext context,
        ILogger<ProductHqSyncOperationsController> logger
    )
    {
        _queue = queue;
        _currentUserService = currentUserService;
        _authorizationService = authorizationService;
        _deviceRegistrationService = deviceRegistrationService;
        _mapper = mapper;
        _db = context.Db;
        _logger = logger;
    }

    [HttpGet("{operationId}")]
    public async Task<ActionResult<ApiResponse<ProductHqSyncOperationStatusDto>>> GetStatus(
        string operationId,
        CancellationToken cancellationToken
    )
    {
        var descriptor = await _queue.GetAccessDescriptorAsync(operationId, cancellationToken);
        if (descriptor == null)
        {
            return NotFound(
                ApiResponse<ProductHqSyncOperationStatusDto>.Error(
                    "HQ 同步操作不存在",
                    "PRODUCT_HQ_SYNC_OPERATION_NOT_FOUND"
                )
            );
        }

        var access = await AuthorizeOperationAsync(descriptor, cancellationToken);
        if (!access.Allowed)
        {
            return AccessDenied(access.IsAuthenticated);
        }

        return Ok(ApiResponse<ProductHqSyncOperationStatusDto>.OK(descriptor.Operation));
    }

    [HttpPost("{operationId}/retry")]
    [LegacyDatabaseDisabled(LegacyDatabaseKind.Hq)]
    public async Task<ActionResult<ApiResponse<ProductHqSyncOperationStatusDto>>> Retry(
        string operationId,
        CancellationToken cancellationToken
    )
    {
        var descriptor = await _queue.GetAccessDescriptorAsync(operationId, cancellationToken);
        if (descriptor == null)
        {
            return NotFound(
                ApiResponse<ProductHqSyncOperationStatusDto>.Error(
                    "HQ 同步操作不存在",
                    "PRODUCT_HQ_SYNC_OPERATION_NOT_FOUND"
                )
            );
        }

        var access = await AuthorizeOperationAsync(descriptor, cancellationToken);
        if (!access.Allowed)
        {
            return AccessDenied(access.IsAuthenticated);
        }
        if (descriptor.Operation.Status != ProductHqSyncOutboxStatuses.Blocked)
        {
            return Conflict(
                ApiResponse<ProductHqSyncOperationStatusDto>.Error(
                    "仅 blocked 状态可人工重试",
                    "PRODUCT_HQ_SYNC_RETRY_NOT_ALLOWED"
                )
            );
        }

        var retried = await _queue.RetryAsync(
            operationId,
            access.ActorLabel,
            cancellationToken
        );
        if (retried == null)
        {
            return NotFound(
                ApiResponse<ProductHqSyncOperationStatusDto>.Error(
                    "HQ 同步操作不存在",
                    "PRODUCT_HQ_SYNC_OPERATION_NOT_FOUND"
                )
            );
        }
        return Ok(ApiResponse<ProductHqSyncOperationStatusDto>.OK(retried));
    }

    private async Task<OperationAccessDecision> AuthorizeOperationAsync(
        ProductHqSyncOutboxAccessDescriptor descriptor,
        CancellationToken cancellationToken
    )
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            var userGuid = Normalize(_currentUserService.GetCurrentUserGuid());
            var permission = RequiredPermission(descriptor.OperationKind);
            var permissionResult = await _authorizationService.AuthorizeAsync(
                User,
                null,
                permission
            );
            if (userGuid == null || !permissionResult.Succeeded)
            {
                return OperationAccessDecision.Denied(isAuthenticated: true);
            }

            var hasGlobalScope = HasElevatedStoreAccess(User);
            IReadOnlyList<string>? storeCodes = null;
            if (!hasGlobalScope)
            {
                storeCodes = await _db.Queryable<UserStore>()
                    .InnerJoin<Store>((userStore, store) => userStore.StoreGUID == store.StoreGUID)
                    .Where((userStore, store) =>
                        userStore.UserGUID == userGuid
                        && !userStore.IsDeleted
                        && !store.IsDeleted
                    )
                    .Select((userStore, store) => store.StoreCode)
                    .ToListAsync(cancellationToken);
            }

            return HasActorAndStoreScope(
                descriptor,
                userGuid,
                null,
                storeCodes,
                hasGlobalScope
            )
                ? OperationAccessDecision.Granted(
                    isAuthenticated: true,
                    User.Identity?.Name ?? $"user:{userGuid}"
                )
                : OperationAccessDecision.Denied(isAuthenticated: true);
        }

        var deviceId = Normalize(Request.Headers["X-Device-Id"].FirstOrDefault());
        var authCode = Normalize(Request.Headers["X-Auth-Code"].FirstOrDefault());
        if (deviceId == null || authCode == null)
        {
            return OperationAccessDecision.Denied(isAuthenticated: false);
        }

        try
        {
            if (!await _deviceRegistrationService.ValidateDeviceAuthCodeAsync(deviceId, authCode))
            {
                return OperationAccessDecision.Denied(isAuthenticated: false);
            }

            var entity = await _deviceRegistrationService.GetDeviceByHardwareIdAsync(deviceId);
            if (entity == null)
            {
                return OperationAccessDecision.Denied(isAuthenticated: false);
            }
            var device = _mapper.Map<DeviceDataDto>(entity);
            var deviceStores = device.Status == 1 && !string.IsNullOrWhiteSpace(device.StoreCode)
                ? new[] { device.StoreCode! }
                : Array.Empty<string>();
            return HasActorAndStoreScope(
                descriptor,
                null,
                deviceId,
                deviceStores,
                hasGlobalStoreScope: false
            )
                ? OperationAccessDecision.Granted(
                    isAuthenticated: false,
                    $"device:{deviceId}"
                )
                : OperationAccessDecision.Denied(isAuthenticated: false);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "校验商品 HQ 同步操作的设备范围失败: OperationId={OperationId}",
                descriptor.Operation.OperationId
            );
            return OperationAccessDecision.Denied(isAuthenticated: false);
        }
    }

    internal static string RequiredPermission(string? operationKind) =>
        string.Equals(
            operationKind?.Trim(),
            ProductMaintenanceHqOperationKinds.ProductCreated,
            StringComparison.OrdinalIgnoreCase
        )
            ? Permissions.StoreProducts.Create
            : Permissions.StoreProducts.Edit;

    internal static bool HasActorAndStoreScope(
        ProductHqSyncOutboxAccessDescriptor descriptor,
        string? currentUserGuid,
        string? currentDeviceId,
        IReadOnlyCollection<string>? allowedStoreCodes,
        bool hasGlobalStoreScope
    )
    {
        var expectedUser = Normalize(descriptor.RequestedByUserGuid);
        var expectedDevice = Normalize(descriptor.RequestedByDeviceId);
        var actorMatches = expectedUser != null
            ? string.Equals(expectedUser, Normalize(currentUserGuid), StringComparison.OrdinalIgnoreCase)
            : expectedDevice != null
                && string.Equals(
                    expectedDevice,
                    Normalize(currentDeviceId),
                    StringComparison.OrdinalIgnoreCase
                );
        if (!actorMatches)
        {
            return false;
        }

        if (hasGlobalStoreScope)
        {
            return true;
        }

        var requiredStores = NormalizeStores(descriptor.AuthorizedStoreCodes);
        if (descriptor.AuthorizedStoreCodes == null)
        {
            // 创建商品本身是全局投影；查询仍须原 actor 且通过 Create 权限。
            return string.Equals(
                descriptor.OperationKind,
                ProductMaintenanceHqOperationKinds.ProductCreated,
                StringComparison.OrdinalIgnoreCase
            );
        }
        if (requiredStores.Count == 0)
        {
            return false;
        }

        var allowedStores = NormalizeStores(allowedStoreCodes).ToHashSet(
            StringComparer.OrdinalIgnoreCase
        );
        if (!requiredStores.All(allowedStores.Contains))
        {
            return false;
        }

        // TargetStoreCodes 是系统为了维护跨店派生投影而计算的执行范围，不是用户授权范围。
        // 状态读取与重试只依赖入队时冻结的 AuthorizedStoreCodes，避免套装投影扩店后误拒绝原 actor。
        return true;
    }

    private ActionResult<ApiResponse<ProductHqSyncOperationStatusDto>> AccessDenied(
        bool isAuthenticated
    ) =>
        isAuthenticated
            ? Forbid()
            : Unauthorized(
                ApiResponse<ProductHqSyncOperationStatusDto>.Error(
                    "无权访问该 HQ 同步操作",
                    "PRODUCT_HQ_SYNC_OPERATION_FORBIDDEN"
                )
            );

    private static bool HasElevatedStoreAccess(ClaimsPrincipal principal) =>
        principal.Claims.Any(claim =>
            claim.Type == ClaimTypes.Role
            && (
                Permissions.IsSuperAdminRole(claim.Value)
                || claim.Value.Equals("Manager", StringComparison.OrdinalIgnoreCase)
                || claim.Value.Equals("WarehouseManager", StringComparison.OrdinalIgnoreCase)
                || claim.Value.Equals("WarehouseStaff", StringComparison.OrdinalIgnoreCase)
            )
        );

    private static IReadOnlyList<string> NormalizeStores(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
            .Select(Normalize)
            .Where(value => value != null)
            .Select(value => value!.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private sealed record OperationAccessDecision(
        bool Allowed,
        bool IsAuthenticated,
        string ActorLabel
    )
    {
        public static OperationAccessDecision Granted(bool isAuthenticated, string actorLabel) =>
            new(true, isAuthenticated, actorLabel);

        public static OperationAccessDecision Denied(bool isAuthenticated) =>
            new(false, isAuthenticated, "system");
    }
}
