using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;

namespace BlazorApp.Api.Features.Packing;

public sealed record PackingAccessResult(PackingOperator? Operator, int StatusCode, string? ErrorCode, string? Message)
{
    public static PackingAccessResult Allowed(PackingOperator op) => new(op, 200, null, null);

    public static PackingAccessResult Denied(int statusCode, string errorCode, string message) =>
        new(null, statusCode, errorCode, message);
}

public interface IPackingAccessService
{
    /// <summary>
    /// 把登录账号换成「当前操作人」：能操作哪些所属地点、是不是经理 / 管理员。
    /// 没有任何打包权限、或没有可操作的地点时返回 403。
    /// </summary>
    Task<PackingAccessResult> ResolveAsync(ClaimsPrincipal user);
}

/// <summary>
/// 打包的数据范围：
/// - 分店：持有 StorePacking.Use 的账号，操作自己在 UserStore 里关联的全部分店；
/// - 总仓：持有 Warehouse.Packing（管理仓库 / 管理仓库订货按别名自动具备）或仓库经理角色；
/// - 管理员：全部分店加总仓。
/// 范围只在这里算一次，服务层只认 PackingOperator.Owners，不再各处重复判断。
/// </summary>
internal sealed class PackingAccessService(
    SqlSugarContext context,
    IAuthorizationService authorizationService,
    IConfiguration configuration
) : IPackingAccessService
{
    public async Task<PackingAccessResult> ResolveAsync(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true || user.HasClaim("token_use", "browser_extension"))
        {
            return PackingAccessResult.Denied(403, PackingErrorCodes.NotAllowed, "当前账号不能使用扫码打包");
        }

        var userGuid = ResolveUserGuid(user);
        if (string.IsNullOrEmpty(userGuid))
        {
            return PackingAccessResult.Denied(401, PackingErrorCodes.NotAllowed, "登录信息无效");
        }

        var isAdmin = HasAnyRole(user, Permissions.SuperAdminRoleNames);
        var isWarehouseManagerRole = HasAnyRole(user, Permissions.WarehouseManagerRoleNames);
        var isStoreManagerRole = HasAnyRole(user, Permissions.StoreManagerRoleNames);
        var hasStore = isAdmin || await HasPermissionAsync(user, Permissions.StorePacking.Use);
        var hasWarehouse = isAdmin
            || isWarehouseManagerRole
            || await HasPermissionAsync(user, Permissions.Warehouse.Packing);

        var owners = new List<PackingOwner>();
        if (hasStore)
        {
            owners.AddRange(await LoadStoreOwnersAsync(userGuid, allStores: isAdmin));
        }

        if (hasWarehouse)
        {
            owners.Add(new PackingOwner(PackOwners.Warehouse, PackingConfig.WarehouseName(configuration), true));
        }

        if (owners.Count == 0)
        {
            return PackingAccessResult.Denied(403, PackingErrorCodes.NotAllowed, "当前账号没有可以打包的分店或仓库");
        }

        var isManager = isAdmin
            || isStoreManagerRole
            || isWarehouseManagerRole
            || (hasWarehouse
                && (await HasPermissionAsync(user, Permissions.Warehouse.Manage)
                    || await HasPermissionAsync(user, Permissions.Warehouse.ManageOrders)));

        var name = user.FindFirst("fullName")?.Value;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = user.Identity?.Name ?? userGuid;
        }

        return PackingAccessResult.Allowed(new PackingOperator(userGuid, name.Trim(), isManager, isAdmin, owners));
    }

    private async Task<List<PackingOwner>> LoadStoreOwnersAsync(string userGuid, bool allStores)
    {
        var db = context.Db;
        var stores = allStores
            ? await db.Queryable<Store>()
                .Where(store => !store.IsDeleted && store.IsActive)
                .Select(store => new { store.StoreCode, store.StoreName })
                .ToListAsync()
            : await db.Queryable<UserStore>()
                .InnerJoin<Store>((userStore, store) => userStore.StoreGUID == store.StoreGUID)
                .Where((userStore, store) =>
                    userStore.UserGUID == userGuid
                    && !userStore.IsDeleted
                    && !store.IsDeleted
                    && store.IsActive
                )
                .Select((userStore, store) => new { store.StoreCode, store.StoreName })
                .ToListAsync();

        return stores
            .Where(store => !string.IsNullOrWhiteSpace(store.StoreCode))
            .GroupBy(store => store.StoreCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PackingOwner(group.Key, group.First().StoreName, false))
            .OrderBy(owner => owner.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<bool> HasPermissionAsync(ClaimsPrincipal user, string permission) =>
        (await authorizationService.AuthorizeAsync(user, null, permission)).Succeeded;

    private static bool HasAnyRole(ClaimsPrincipal user, IEnumerable<string> roles) =>
        roles.Any(role =>
            user.Claims.Any(claim =>
                claim.Type == ClaimTypes.Role
                && string.Equals(claim.Value, role, StringComparison.OrdinalIgnoreCase)
            )
        );

    private static string? ResolveUserGuid(ClaimsPrincipal user)
    {
        var value = user.FindFirst("userId")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("userGuid")?.Value
            ?? user.FindFirst("sub")?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

}
