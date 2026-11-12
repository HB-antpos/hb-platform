using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;

namespace BlazorApp.Api.Services.React;

public sealed class BrowserExtensionAccessService : IBrowserExtensionAccessService
{
    private static readonly string[] AdminRoles =
    {
        "Admin",
        "管理员",
        "SuperAdmin",
        "超级管理员",
    };

    private static readonly string[] WarehouseManagerRoles = { "WarehouseManager", "仓库经理" };
    private static readonly string[] WarehouseStaffRoles = { "WarehouseStaff", "仓库员工" };

    private readonly IAuthorizationService _authorizationService;
    private readonly ICurrentUserManageableStoreScopeService _storeScopeService;
    private readonly IUserService _userService;

    public BrowserExtensionAccessService(
        IAuthorizationService authorizationService,
        ICurrentUserManageableStoreScopeService storeScopeService,
        IUserService userService
    )
    {
        _authorizationService = authorizationService;
        _storeScopeService = storeScopeService;
        _userService = userService;
    }

    public async Task<bool> CanAccessAsync(ClaimsPrincipal user, string? storeCode = null)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (IsWarehouseStaffOnly(user))
        {
            // 与现有订货前台一致：纯仓库员工只能凭显式 Orders.Create 代建任意分店订单。
            return await HasPermissionAsync(user, Permissions.Orders.Create);
        }

        var isAdmin = HasAnyRole(user, AdminRoles);
        if (!isAdmin && !await HasPermissionAsync(user, Permissions.OrderFront.View))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(storeCode) || isAdmin)
        {
            return true;
        }

        var normalizedStoreCode = storeCode.Trim();
        if (await _storeScopeService.CanAccessStoreCodeForWarehouseAsync(normalizedStoreCode))
        {
            return true;
        }

        var userGuid = user.FindFirst("userId")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            return false;
        }

        var stores = await _userService.GetUserStoresAsync(userGuid);
        return stores.Success
            && stores.Data?.Any(item =>
                item.StoreCode.Equals(normalizedStoreCode, StringComparison.OrdinalIgnoreCase)
            ) == true;
    }

    public async Task<IReadOnlyList<string>> GetRelatedStoreCodesAsync(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return Array.Empty<string>();
        }

        var userGuid = user.FindFirst("userId")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            return Array.Empty<string>();
        }

        // 订货插件属仓库本职：开关打开后仓库经理的门店口径会收口，这里改取仓库口径（全部分店）。
        var useWarehouseScope =
            StoreScopePolicy.HasWarehouseWideScope(user) && !StoreScopePolicy.HasAllStoreScope(user);
        var stores = useWarehouseScope
            ? await _userService.GetWarehouseUserStoresAsync(userGuid)
            : await _userService.GetUserStoresAsync(userGuid);
        return stores.Success
            ? BrowserExtensionStoreSelection.NormalizeRelatedStoreCodes(stores.Data)
            : Array.Empty<string>();
    }

    private async Task<bool> HasPermissionAsync(ClaimsPrincipal user, string permission)
    {
        var result = await _authorizationService.AuthorizeAsync(user, null, permission);
        return result.Succeeded;
    }

    private static bool IsWarehouseStaffOnly(ClaimsPrincipal user) =>
        HasAnyRole(user, WarehouseStaffRoles)
        && !HasAnyRole(user, AdminRoles)
        && !HasAnyRole(user, WarehouseManagerRoles);

    private static bool HasAnyRole(ClaimsPrincipal user, IReadOnlyCollection<string> roles) =>
        user.Claims.Any(claim =>
            claim.Type == ClaimTypes.Role
            && roles.Any(role => role.Equals(claim.Value, StringComparison.OrdinalIgnoreCase))
        );
}
