using System.Diagnostics;
using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BlazorApp.Api.Features.StoreOrders.Common;

internal sealed class StoreOrderAccessPolicy(
    IStoreOrderActorContext actorContext,
    IAuthorizationService authorizationService,
    ICurrentUserManageableStoreScopeService storeScopeService,
    IUserService userService,
    IStoreOrderAccessOrderReader orderReader,
    IHttpContextAccessor httpContextAccessor,
    IMemoryCache cache,
    ILogger<StoreOrderAccessPolicy> logger
) : IStoreOrderAccessPolicy
{
    private const string ScanTraceHeaderName = "X-Scan-Trace-Id";
    private const string AuthorizationCacheOwner = "ReactStoreOrderController";
    private static readonly TimeSpan AuthorizationSuccessCacheDuration =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AuthorizationFailureCacheDuration =
        TimeSpan.FromSeconds(5);

    private static readonly string[] OrderReadPermissions =
    {
        Permissions.OrderFront.View,
        Permissions.Orders.View,
        Permissions.Warehouse.ManageOrders,
        Permissions.Warehouse.Manage,
    };

    private static readonly string[] OrderCreatePermissions =
    {
        Permissions.Orders.Create,
        Permissions.Warehouse.ManageOrders,
        Permissions.Warehouse.Manage,
    };

    private static readonly string[] CartWritePermissions =
    {
        Permissions.OrderFront.View,
        Permissions.Orders.Create,
        Permissions.Warehouse.ManageOrders,
        Permissions.Warehouse.Manage,
    };

    private static readonly string[] OrderEditPermissions =
    {
        Permissions.Orders.Edit,
        Permissions.Warehouse.ManageOrders,
        Permissions.Warehouse.Manage,
    };

    private static readonly string[] OrderDeletePermissions =
    {
        Permissions.Orders.Delete,
        Permissions.Warehouse.ManageOrders,
        Permissions.Warehouse.Manage,
    };

    private static readonly string[] WarehouseOrderSyncPermissions =
    {
        Permissions.Warehouse.ManageOrders,
        Permissions.Warehouse.Manage,
    };

    private static readonly string[] ImportPriceRefreshRoles =
    {
        "Admin",
        "管理员",
        "WarehouseManager",
        "仓库经理",
    };

    private static readonly string[] GlobalStoreScopeRoles =
    {
        "Admin",
        "管理员",
        "WarehouseManager",
        "仓库经理",
    };

    private static readonly string[] ScopedStoreRoles =
    {
        "StoreManager",
        "店长",
        "经理",
    };

    private static readonly string[] LocationProductLookupRoleNames =
        Permissions.SuperAdminRoleNames
            .Concat(Permissions.WarehouseManagerRoleNames)
            .Concat(new[] { "WarehouseStaff", "仓库员工" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public bool IsWarehouseStaffOnly()
    {
        return HasAnyRole("WarehouseStaff", "仓库员工")
            && !IsRealAdmin()
            && !HasAnyRole("WarehouseManager", "仓库经理");
    }

    public bool IsStoreScopedUser()
    {
        return HasAnyRole(ScopedStoreRoles) && !HasAnyRole(GlobalStoreScopeRoles);
    }

    public bool IsRealAdmin()
    {
        return HasAnyRole("Admin", "管理员");
    }

    public bool IsLocationProductLookupEnabled()
    {
        return HasAnyRole(LocationProductLookupRoleNames);
    }

    public string? GetCurrentUserId()
    {
        return actorContext.User?.FindFirst("userId")?.Value
            ?? actorContext.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }

    public StoreOrderAccessDecision RequireRealAdmin()
    {
        return IsRealAdmin()
            ? StoreOrderAccessDecision.Allowed
            : StoreOrderAccessDecision.Forbidden;
    }

    public async Task<bool> HasGlobalWarehouseOrderScopeAsync()
    {
        var traceId = GetExplicitScanTraceId();
        var stopwatch = Stopwatch.StartNew();
        var hasScope = IsRealAdmin()
            || await HasAnyPermissionAsync(new[]
            {
                Permissions.Warehouse.ManageOrders,
                Permissions.Warehouse.Manage
            });
        stopwatch.Stop();

        if (!string.IsNullOrWhiteSpace(traceId))
        {
            logger.LogInformation(
                "[shop-scan-perf] traceId={TraceId} stage=authorization.global-scope allowed={Allowed} elapsedMs={ElapsedMs}",
                traceId,
                hasScope,
                stopwatch.ElapsedMilliseconds
            );
        }

        return hasScope;
    }

    public async Task<bool> CanBypassPreorderCompletionAsync()
    {
        // 这里只返回已授权角色/范围的 bypass 能力；Preorder 检查本身仍由业务写入口负责。
        return IsWarehouseStaffOnly() || await HasGlobalWarehouseOrderScopeAsync();
    }

    public async Task<StoreOrderAccessDecision> RequireOrderReadAsync()
    {
        return await RequireAnyPermissionAsync(OrderReadPermissions);
    }

    public async Task<StoreOrderAccessDecision> RequireProductPickerReadAsync(
        string? storeCode,
        string? excludedOrderGuid,
        string checkType
    )
    {
        var decision = await RequireCartReadAsync(storeCode, checkType);
        if (decision.IsForbidden || string.IsNullOrWhiteSpace(excludedOrderGuid))
        {
            return decision;
        }

        return await RequireOrderScopeAsync(excludedOrderGuid.Trim());
    }

    public async Task<StoreOrderAccessDecision> RequireOrderListReadAsync(
        string? storeCode,
        IEnumerable<string?>? storeCodes
    )
    {
        var decision = await RequireOrderReadAsync();
        if (decision.IsForbidden)
        {
            return decision;
        }

        var requestedStoreCodes = (storeCodes ?? Array.Empty<string?>())
            .Append(storeCode)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requestedStoreCodes.Count == 0)
        {
            // 没有任何有效分店筛选时只允许全局范围，避免普通用户读取全部订单。
            return await RequireAssignedStoreScopeAsync(null);
        }

        foreach (var requestedStoreCode in requestedStoreCodes)
        {
            // 单店与多店参数取并集逐一校验，每个分店都可由管理范围或用户分配关系授权。
            decision = await RequireAssignedStoreScopeAsync(requestedStoreCode);
            if (decision.IsForbidden)
            {
                return decision;
            }
        }

        return StoreOrderAccessDecision.Allowed;
    }

    public async Task<StoreOrderAccessDecision> RequireOrderDetailReadAsync(string orderGuid)
    {
        var decision = await RequireOrderReadAsync();
        return decision.IsForbidden
            ? decision
            : await RequireOrderReadScopeAsync(orderGuid);
    }

    public async Task<StoreOrderAccessDecision> RequireOrderDetailProductCodesReadAsync(
        string orderGuid
    )
    {
        var decision = await RequireOrderReadAsync();
        return decision.IsForbidden ? decision : await RequireOrderScopeAsync(orderGuid);
    }

    public async Task<StoreOrderAccessDecision> RequireCartReadAsync(
        string? storeCode,
        string checkType
    )
    {
        if (IsWarehouseStaffOnly())
        {
            // 纯 WarehouseStaff 读取商品/购物车只认代建订单所需的显式 Orders.Create。
            return await RequireAnyPermissionAsync(Permissions.Orders.Create);
        }

        var decision = await RequireAnyPermissionAsync(
            storeCode,
            checkType,
            OrderReadPermissions
        );
        return decision.IsForbidden
            ? decision
            : await RequireAssignedStoreScopeAsync(storeCode);
    }

    public async Task<StoreOrderAccessDecision> RequireCartWriteAsync(
        string? storeCode,
        string checkType
    )
    {
        if (IsWarehouseStaffOnly())
        {
            // 纯 WarehouseStaff 使用按用户隔离的代建购物车，不落到单店 scope。
            return await RequireAnyPermissionAsync(Permissions.Orders.Create);
        }

        var decision = await RequireAnyPermissionAsync(
            storeCode,
            checkType,
            CartWritePermissions
        );
        return decision.IsForbidden
            ? decision
            : await RequireAssignedStoreScopeAsync(storeCode);
    }

    public async Task<StoreOrderAccessDecision> RequireCreateOrderAsync(string? storeCode)
    {
        if (IsWarehouseStaffOnly())
        {
            return await RequireAnyPermissionAsync(Permissions.Orders.Create);
        }

        var decision = await RequireOrderManagementActionAsync(OrderCreatePermissions);
        return decision.IsForbidden ? decision : await RequireStoreScopeAsync(storeCode);
    }

    public async Task<StoreOrderAccessDecision> RequireOrderLineMutationAsync(string orderGuid)
    {
        if (IsWarehouseStaffOnly())
        {
            // 仓库员工维护代建正式订单明细时只认显式 Orders.Edit，不要求订单 scope。
            return await RequireAnyPermissionAsync(Permissions.Orders.Edit);
        }

        var decision = await RequireOrderManagementActionAsync(OrderEditPermissions);
        return decision.IsForbidden ? decision : await RequireOrderScopeAsync(orderGuid);
    }

    public async Task<StoreOrderAccessDecision> RequireOrderManagementEditAsync()
    {
        return await RequireOrderManagementActionAsync(OrderEditPermissions);
    }

    public async Task<StoreOrderAccessDecision> RequireStartPickingAsync(string orderGuid)
    {
        if (IsWarehouseStaffOnly())
        {
            // 纯仓库员工只认显式授予的 Warehouse.StartPicking，不会因 Warehouse.Manage / Orders.Edit
            // 等宽泛权限获得；与 RequireCreateOrderAsync、RequireOrderLineMutationAsync 的员工分支同一口径。
            // 只放行「开始配货」这一个状态流转，不放开其它订单管理动作。
            return await RequireAnyPermissionAsync(Permissions.Warehouse.StartPicking);
        }

        return await RequireOrderEditAsync(orderGuid);
    }

    public async Task<StoreOrderAccessDecision> RequireOrderEditAsync(
        string orderGuid,
        string? storeCode = null
    )
    {
        var decision = await RequireOrderManagementEditAsync();
        if (decision.IsForbidden)
        {
            return decision;
        }

        decision = await RequireOrderScopeAsync(orderGuid);
        if (decision.IsForbidden)
        {
            return decision;
        }

        return storeCode == null
            ? StoreOrderAccessDecision.Allowed
            : await RequireStoreScopeAsync(storeCode);
    }

    public async Task<StoreOrderAccessDecision> RequireOrderEditForStoresAsync(
        IEnumerable<string?> storeCodes
    )
    {
        var decision = await RequireOrderManagementActionAsync(OrderEditPermissions);
        if (decision.IsForbidden)
        {
            return decision;
        }

        foreach (var storeCode in storeCodes)
        {
            decision = await RequireStoreScopeAsync(storeCode);
            if (decision.IsForbidden)
            {
                return decision;
            }
        }

        return StoreOrderAccessDecision.Allowed;
    }

    public async Task<StoreOrderAccessDecision> RequireOrderEditsAsync(
        IEnumerable<string?> orderGuids
    )
    {
        var decision = await RequireOrderManagementActionAsync(OrderEditPermissions);
        return decision.IsForbidden
            ? decision
            : await RequireOrderScopesAsync(orderGuids);
    }

    public async Task<StoreOrderAccessDecision> RequireOrderDeleteAsync(string orderGuid)
    {
        var decision = await RequireOrderManagementActionAsync(OrderDeletePermissions);
        return decision.IsForbidden ? decision : await RequireOrderScopeAsync(orderGuid);
    }

    public async Task<StoreOrderAccessDecision> RequireCopyOrderAsync(
        string sourceOrderGuid,
        string? targetStoreCode
    )
    {
        var decision = await RequireOrderManagementActionAsync(OrderCreatePermissions);
        if (decision.IsForbidden)
        {
            return decision;
        }

        decision = await RequireOrderScopeAsync(sourceOrderGuid);
        return decision.IsForbidden
            ? decision
            : await RequireStoreScopeAsync(targetStoreCode);
    }

    public async Task<StoreOrderAccessDecision> RequireWarehouseSyncAsync()
    {
        return await RequireOrderManagementActionAsync(WarehouseOrderSyncPermissions);
    }

    public async Task<StoreOrderAccessDecision> RequireImportPriceRefreshAsync(
        string orderGuid
    )
    {
        if (!HasAnyRole(ImportPriceRefreshRoles))
        {
            return StoreOrderAccessDecision.Forbidden;
        }

        return await RequireOrderScopeAsync(orderGuid);
    }

    public async Task<StoreOrderAccessDecision> RequireStoreScopeAsync(string? storeCode)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return StoreOrderAccessDecision.Allowed;
        }

        if (await HasGlobalWarehouseOrderScopeAsync())
        {
            return StoreOrderAccessDecision.Allowed;
        }

        return await storeScopeService.CanAccessStoreCodeAsync(storeCode)
            ? StoreOrderAccessDecision.Allowed
            : StoreOrderAccessDecision.Forbidden;
    }

    public async Task<StoreOrderAccessDecision> RequireAssignedStoreScopeAsync(
        string? storeCode
    )
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return await HasGlobalWarehouseOrderScopeAsync()
                ? StoreOrderAccessDecision.Allowed
                : StoreOrderAccessDecision.Forbidden;
        }

        if (await HasGlobalWarehouseOrderScopeAsync())
        {
            return StoreOrderAccessDecision.Allowed;
        }

        var userGuid = GetCurrentUserId();
        var normalizedStoreCode = NormalizeAuthorizationStoreCode(storeCode);
        var cacheKey = BuildAuthorizationCacheKey(
            "assigned-store-scope",
            userGuid,
            normalizedStoreCode,
            "RequireAssignedStoreScopeAsync",
            "manage-or-assigned"
        );

        if (
            !string.IsNullOrWhiteSpace(userGuid)
            && cache.TryGetValue<bool>(cacheKey, out var cachedScopeAllowed)
        )
        {
            LogAssignedStoreScopeMetric(normalizedStoreCode, true, 0, null);
            return cachedScopeAllowed
                ? StoreOrderAccessDecision.Allowed
                : StoreOrderAccessDecision.Forbidden;
        }

        var stopwatch = Stopwatch.StartNew();
        var isAllowed =
            await storeScopeService.CanAccessStoreCodeAsync(storeCode)
            || await CanAccessAssignedStoreCodeAsync(storeCode);
        stopwatch.Stop();

        if (!string.IsNullOrWhiteSpace(userGuid))
        {
            SetAuthorizationCache(cacheKey, isAllowed);
        }

        LogAssignedStoreScopeMetric(
            normalizedStoreCode,
            false,
            stopwatch.ElapsedMilliseconds,
            isAllowed
        );
        return isAllowed
            ? StoreOrderAccessDecision.Allowed
            : StoreOrderAccessDecision.Forbidden;
    }

    public async Task<StoreOrderAccessDecision> RequireOrderScopeAsync(string orderGuid)
    {
        if (await HasGlobalWarehouseOrderScopeAsync())
        {
            return StoreOrderAccessDecision.Allowed;
        }

        return await storeScopeService.CanAccessOrderAsync(orderGuid)
            ? StoreOrderAccessDecision.Allowed
            : StoreOrderAccessDecision.Forbidden;
    }

    public async Task<StoreOrderAccessDecision> RequireOrderReadScopeAsync(string orderGuid)
    {
        var decision = await RequireOrderScopeAsync(orderGuid);
        if (decision.IsAllowed)
        {
            return decision;
        }

        var storeCode = await orderReader.GetActiveOrderStoreCodeAsync(orderGuid);
        return await RequireAssignedStoreScopeAsync(storeCode);
    }

    public async Task<StoreOrderAccessDecision> RequireOrderScopesAsync(
        IEnumerable<string?> orderGuids
    )
    {
        foreach (var orderGuid in orderGuids.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            var decision = await RequireOrderScopeAsync(orderGuid!);
            if (decision.IsForbidden)
            {
                return decision;
            }
        }

        return StoreOrderAccessDecision.Allowed;
    }

    public async Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync()
    {
        return await storeScopeService.GetAccessibleStoreCodesAsync();
    }

    public Task<StoreOrderStoreSelectionDecision> ResolveMissingOrdersSyncScopeAsync(
        IEnumerable<string?>? storeCodes,
        string? legacyStoreCode
    )
    {
        return ResolveSyncScopeAsync(
            storeCodes,
            legacyStoreCode,
            allowAdminScopeWithoutSelection: true
        );
    }

    public Task<StoreOrderStoreSelectionDecision> ResolveHqIncrementalSyncScopeAsync(
        IEnumerable<string?>? storeCodes,
        string? legacyStoreCode
    )
    {
        return ResolveSyncScopeAsync(
            storeCodes,
            legacyStoreCode,
            allowAdminScopeWithoutSelection: false
        );
    }

    public async Task<StoreOrderAccessDecision> RequireScopedJobStoresAsync(
        IEnumerable<string?>? storeCodes
    )
    {
        if (!IsStoreScopedUser() || await HasGlobalWarehouseOrderScopeAsync())
        {
            return StoreOrderAccessDecision.Allowed;
        }

        var requestedStoreCodes = (storeCodes ?? Array.Empty<string?>()).ToList();
        if (requestedStoreCodes.Count == 0)
        {
            return StoreOrderAccessDecision.Forbidden;
        }

        foreach (var storeCode in requestedStoreCodes)
        {
            var decision = await RequireStoreScopeAsync(storeCode);
            if (decision.IsForbidden)
            {
                return decision;
            }
        }

        return StoreOrderAccessDecision.Allowed;
    }

    private async Task<StoreOrderStoreSelectionDecision> ResolveSyncScopeAsync(
        IEnumerable<string?>? storeCodes,
        string? legacyStoreCode,
        bool allowAdminScopeWithoutSelection
    )
    {
        var normalizedStoreCodes = NormalizeStoreCodes(storeCodes, legacyStoreCode);
        if (!IsStoreScopedUser() || await HasGlobalWarehouseOrderScopeAsync())
        {
            return StoreOrderStoreSelectionDecision.Preserve;
        }

        if (normalizedStoreCodes.Count > 0)
        {
            foreach (var storeCode in normalizedStoreCodes)
            {
                var decision = await RequireStoreScopeAsync(storeCode);
                if (decision.IsForbidden)
                {
                    return StoreOrderStoreSelectionDecision.Forbidden;
                }
            }

            return StoreOrderStoreSelectionDecision.Preserve;
        }

        var scope = await storeScopeService.GetScopeAsync();
        if (!scope.IsAllowed || (scope.IsAdmin && !allowAdminScopeWithoutSelection))
        {
            return StoreOrderStoreSelectionDecision.Forbidden;
        }

        if (scope.IsAdmin)
        {
            return StoreOrderStoreSelectionDecision.Preserve;
        }

        var scopedStoreCodes = NormalizeStoreCodes(scope.StoreCodes, null);
        return scopedStoreCodes.Count == 0
            ? StoreOrderStoreSelectionDecision.Forbidden
            : StoreOrderStoreSelectionDecision.RestrictTo(scopedStoreCodes);
    }

    private async Task<StoreOrderAccessDecision> RequireOrderManagementActionAsync(
        params string[] permissions
    )
    {
        if (IsWarehouseStaffOnly())
        {
            return StoreOrderAccessDecision.Forbidden;
        }

        return await RequireAnyPermissionAsync(permissions);
    }

    private async Task<StoreOrderAccessDecision> RequireAnyPermissionAsync(
        params string[] permissions
    )
    {
        return await RequireAnyPermissionAsync(
            null,
            StoreOrderAccessCheckTypes.Global,
            permissions
        );
    }

    private async Task<StoreOrderAccessDecision> RequireAnyPermissionAsync(
        string? storeCode,
        string checkType,
        params string[] permissions
    )
    {
        return await HasAnyPermissionAsync(storeCode, checkType, permissions)
            ? StoreOrderAccessDecision.Allowed
            : StoreOrderAccessDecision.Forbidden;
    }

    private async Task<bool> HasAnyPermissionAsync(params string[] permissions)
    {
        return await HasAnyPermissionAsync(
            null,
            StoreOrderAccessCheckTypes.Global,
            permissions
        );
    }

    private async Task<bool> HasAnyPermissionAsync(
        string? storeCode,
        string checkType,
        params string[] permissions
    )
    {
        var userId = GetCurrentUserId();
        var normalizedStoreCode = NormalizeAuthorizationStoreCode(storeCode);
        var permissionsCacheKey = BuildAuthorizationCacheKey(
            "permissions",
            userId,
            "any",
            "any",
            string.Join("|", permissions)
        );

        if (
            !string.IsNullOrWhiteSpace(userId)
            && cache.TryGetValue<bool>(permissionsCacheKey, out var cachedPermissionsResult)
        )
        {
            LogScanAuthorizationMetric(
                "authorization.permissions",
                normalizedStoreCode,
                checkType,
                true,
                0
            );
            return cachedPermissionsResult;
        }

        var stopwatch = Stopwatch.StartNew();
        var isAllowed = false;
        foreach (var permission in permissions)
        {
            if (
                await AuthorizePolicyWithCacheAsync(
                    userId,
                    normalizedStoreCode,
                    checkType,
                    permission
                )
            )
            {
                isAllowed = true;
                break;
            }
        }

        stopwatch.Stop();
        if (!string.IsNullOrWhiteSpace(userId))
        {
            SetAuthorizationCache(permissionsCacheKey, isAllowed);
        }

        LogScanAuthorizationMetric(
            "authorization.permissions",
            normalizedStoreCode,
            checkType,
            false,
            stopwatch.ElapsedMilliseconds
        );
        return isAllowed;
    }

    private async Task<bool> AuthorizePolicyWithCacheAsync(
        string? userId,
        string normalizedStoreCode,
        string checkType,
        string permission
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var user = actorContext.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        if (string.IsNullOrWhiteSpace(userId))
        {
            var uncached = await authorizationService.AuthorizeAsync(user, null, permission);
            stopwatch.Stop();
            LogScanAuthorizationMetric(
                "authorization.policy",
                normalizedStoreCode,
                checkType,
                false,
                stopwatch.ElapsedMilliseconds,
                permission
            );
            return uncached.Succeeded;
        }

        var cacheKey = BuildAuthorizationCacheKey(
            "policy",
            userId,
            "any",
            "any",
            permission
        );
        if (cache.TryGetValue<bool>(cacheKey, out var cachedResult))
        {
            stopwatch.Stop();
            LogScanAuthorizationMetric(
                "authorization.policy",
                normalizedStoreCode,
                checkType,
                true,
                stopwatch.ElapsedMilliseconds,
                permission
            );
            return cachedResult;
        }

        var result = await authorizationService.AuthorizeAsync(user, null, permission);
        stopwatch.Stop();
        SetAuthorizationCache(cacheKey, result.Succeeded);
        LogScanAuthorizationMetric(
            "authorization.policy",
            normalizedStoreCode,
            checkType,
            false,
            stopwatch.ElapsedMilliseconds,
            permission
        );
        return result.Succeeded;
    }

    private async Task<bool> CanAccessAssignedStoreCodeAsync(string storeCode)
    {
        var userGuid = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        var storesResult = await userService.GetUserStoresAsync(userGuid);
        stopwatch.Stop();
        var traceId = GetExplicitScanTraceId();
        if (!string.IsNullOrWhiteSpace(traceId))
        {
            logger.LogInformation(
                "[shop-scan-perf] traceId={TraceId} stage=authorization.user-stores-query elapsedMs={ElapsedMs} success={Success} storeCount={StoreCount}",
                traceId,
                stopwatch.ElapsedMilliseconds,
                storesResult.Success,
                storesResult.Data?.Count ?? 0
            );
        }

        if (!storesResult.Success || storesResult.Data == null)
        {
            return false;
        }

        return storesResult.Data.Any(store =>
            !string.IsNullOrWhiteSpace(store.StoreCode)
            && store.StoreCode.Equals(storeCode.Trim(), StringComparison.OrdinalIgnoreCase)
        );
    }

    private bool HasAnyRole(params string[] roleNames)
    {
        return roleNames.Any(actorContext.HasRole);
    }

    private string? GetExplicitScanTraceId()
    {
        var traceId = httpContextAccessor
            .HttpContext?.Request.Headers[ScanTraceHeaderName]
            .FirstOrDefault()
            ?.Trim();
        return string.IsNullOrWhiteSpace(traceId) ? null : traceId;
    }

    private static string NormalizeAuthorizationStoreCode(string? storeCode)
    {
        return string.IsNullOrWhiteSpace(storeCode)
            ? "none"
            : storeCode.Trim().ToUpperInvariant();
    }

    private static List<string> NormalizeStoreCodes(
        IEnumerable<string?>? storeCodes,
        string? legacyStoreCode
    )
    {
        var normalizedStoreCodes = (storeCodes ?? Array.Empty<string?>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (
            normalizedStoreCodes.Count == 0
            && !string.IsNullOrWhiteSpace(legacyStoreCode)
        )
        {
            normalizedStoreCodes.Add(legacyStoreCode.Trim());
        }

        return normalizedStoreCodes;
    }

    private static string BuildAuthorizationCacheKey(
        string cacheType,
        string? userId,
        string normalizedStoreCode,
        string checkType,
        string permissionOrScope
    )
    {
        return string.Join(
            ':',
            AuthorizationCacheOwner,
            "authorization",
            cacheType,
            userId?.Trim() ?? "anonymous",
            normalizedStoreCode,
            checkType,
            permissionOrScope
        );
    }

    private void LogScanAuthorizationMetric(
        string stage,
        string normalizedStoreCode,
        string checkType,
        bool cacheHit,
        long elapsedMs,
        string? permission = null
    )
    {
        var traceId = GetExplicitScanTraceId();
        if (string.IsNullOrWhiteSpace(traceId) || !IsScanAuthorizationCheckType(checkType))
        {
            return;
        }

        logger.LogInformation(
            "[shop-scan-perf] traceId={TraceId} stage={Stage} storeCode={StoreCode} checkType={CheckType} permission={Permission} cacheHit={CacheHit} elapsedMs={ElapsedMs}",
            traceId,
            stage,
            normalizedStoreCode,
            checkType,
            permission ?? "all",
            cacheHit,
            elapsedMs
        );
    }

    private void LogAssignedStoreScopeMetric(
        string normalizedStoreCode,
        bool cacheHit,
        long elapsedMs,
        bool? isAllowed
    )
    {
        var traceId = GetExplicitScanTraceId();
        if (string.IsNullOrWhiteSpace(traceId))
        {
            return;
        }

        logger.LogInformation(
            "[shop-scan-perf] traceId={TraceId} stage=authorization.assigned-store-scope storeCode={StoreCode} cacheHit={CacheHit} allowed={Allowed} elapsedMs={ElapsedMs}",
            traceId,
            normalizedStoreCode,
            cacheHit,
            isAllowed,
            elapsedMs
        );
    }

    private static bool IsScanAuthorizationCheckType(string checkType)
    {
        return string.Equals(
                checkType,
                StoreOrderAccessCheckTypes.ScanOrderFlow,
                StringComparison.Ordinal
            )
            || string.Equals(
                checkType,
                StoreOrderAccessCheckTypes.CartFlow,
                StringComparison.Ordinal
            );
    }

    private void SetAuthorizationCache(string cacheKey, bool isAllowed)
    {
        var duration = isAllowed
            ? AuthorizationSuccessCacheDuration
            : AuthorizationFailureCacheDuration;

        cache.Set(
            cacheKey,
            isAllowed,
            new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(duration)
                .SetPriority(CacheItemPriority.Low)
        );
    }
}
