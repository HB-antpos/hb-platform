using System.Data;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services;

public sealed class UserStorePosTerminalPermissionService(
    SqlSugarContext context,
    ICurrentUserManageableStoreScopeService storeScopeService,
    ICurrentUserService currentUserService
) : IUserStorePosTerminalPermissionService
{
    private static readonly IReadOnlyList<string> HighPrivilegeRoleNames =
    [
        .. Permissions.SuperAdminRoleNames,
        .. CurrentUserManageableStoreScopeService.StoreManagerRoleAliases,
        .. CurrentUserManageableStoreScopeService.WarehouseManagerRoleAliases,
        "仓库管理员",
        "WarehouseAdmin",
    ];

    public async Task<ApiResponse<UserStorePosTerminalPermissionsResponse>> GetAsync(
        string userGuid,
        string storeGuid
    )
    {
        var access = await ResolveAccessAsync(userGuid, storeGuid);
        if (!access.Success)
        {
            return ApiResponse<UserStorePosTerminalPermissionsResponse>.Error(
                access.Message,
                access.ErrorCode
            );
        }

        return ApiResponse<UserStorePosTerminalPermissionsResponse>.OK(
            await BuildResponseAsync(userGuid, storeGuid, access),
            "获取分店 POS 权限成功"
        );
    }

    public async Task<ApiResponse<UserStorePosTerminalPermissionsResponse>> UpdateAsync(
        string userGuid,
        string storeGuid,
        UpdateUserStorePosTerminalPermissionsRequest request
    )
    {
        var access = await ResolveAccessAsync(userGuid, storeGuid);
        if (!access.Success)
        {
            return ApiResponse<UserStorePosTerminalPermissionsResponse>.Error(
                access.Message,
                access.ErrorCode
            );
        }

        var assignableCodes = GetAssignablePermissionSeeds(access)
            .Select(item => item.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var grantedCodes = (request.GrantedPermissionCodes ?? new List<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var invalidCodes = grantedCodes.Where(code => !assignableCodes.Contains(code)).ToList();
        if (invalidCodes.Count > 0)
        {
            return ApiResponse<UserStorePosTerminalPermissionsResponse>.Error(
                "请求包含不可分配的 POS 权限",
                "POS_PERMISSION_INVALID_CODES",
                invalidCodes
            );
        }

        var db = context.Db;
        var committedAccess = access;
        // 按店覆盖同样在事务内复验员工身份、分店范围和操作者当前有效权限。
        await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
        try
        {
            var transactionAccess = await ResolveAccessAsync(userGuid, storeGuid);
            if (!transactionAccess.Success)
            {
                await db.Ado.RollbackTranAsync();
                return ApiResponse<UserStorePosTerminalPermissionsResponse>.Error(
                    transactionAccess.Message,
                    transactionAccess.ErrorCode
                );
            }

            var transactionAssignableCodes = GetAssignablePermissionSeeds(transactionAccess)
                .Select(item => item.Code)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var transactionInvalidCodes = grantedCodes
                .Where(code => !transactionAssignableCodes.Contains(code))
                .ToList();
            if (transactionInvalidCodes.Count > 0)
            {
                await db.Ado.RollbackTranAsync();
                return ApiResponse<UserStorePosTerminalPermissionsResponse>.Error(
                    "请求包含不可分配的 POS 权限",
                    "POS_PERMISSION_INVALID_CODES",
                    transactionInvalidCodes
                );
            }

            var assignableCodeList = transactionAssignableCodes.ToList();
            var existingRows = await db.Queryable<SysUserStorePosPermission>()
                .Where(item =>
                    item.UserGuid == userGuid
                    && item.StoreGuid == storeGuid
                    && assignableCodeList.Contains(item.PermissionCode)
                )
                .ToListAsync();

            var now = DateTime.UtcNow;
            var actor = currentUserService.GetCurrentUsername();
            var existingByCode = existingRows
                .GroupBy(item => item.PermissionCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(item => item.IsDeleted).ThenBy(item => item.CreatedAt).First(),
                    StringComparer.OrdinalIgnoreCase
                );
            var rowsToUpdate = new List<SysUserStorePosPermission>();
            var rowsToInsert = new List<SysUserStorePosPermission>();

            foreach (var code in assignableCodeList)
            {
                if (existingByCode.TryGetValue(code, out var existing))
                {
                    // 复用原始审计行和唯一键墓碑，重复保存不会换主键或撞唯一索引。
                    existing.IsGranted = grantedCodes.Contains(
                        code,
                        StringComparer.OrdinalIgnoreCase
                    );
                    existing.IsDeleted = false;
                    existing.UpdatedAt = now;
                    existing.UpdatedBy = actor;
                    rowsToUpdate.Add(existing);
                    continue;
                }

                rowsToInsert.Add(new SysUserStorePosPermission
                {
                    Id = UuidHelper.GenerateUuid7(),
                    UserGuid = userGuid,
                    StoreGuid = storeGuid,
                    PermissionCode = code,
                    IsGranted = grantedCodes.Contains(code, StringComparer.OrdinalIgnoreCase),
                    CreatedAt = now,
                    CreatedBy = actor,
                    UpdatedAt = now,
                    UpdatedBy = actor,
                });
            }

            if (rowsToUpdate.Count > 0)
            {
                await db.Updateable(rowsToUpdate)
                    .UpdateColumns(item => new
                    {
                        item.IsGranted,
                        item.IsDeleted,
                        item.UpdatedAt,
                        item.UpdatedBy,
                    })
                    .ExecuteCommandAsync();
            }

            if (rowsToInsert.Count > 0)
            {
                await db.Insertable(rowsToInsert).ExecuteCommandAsync();
            }

            await db.Ado.CommitTranAsync();
            committedAccess = transactionAccess;
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        return ApiResponse<UserStorePosTerminalPermissionsResponse>.OK(
            await BuildResponseAsync(userGuid, storeGuid, committedAccess),
            "更新分店 POS 权限成功"
        );
    }

    public async Task<ApiResponse<UserStorePosTerminalPermissionsResponse>> DeleteAsync(
        string userGuid,
        string storeGuid
    )
    {
        var access = await ResolveAccessAsync(userGuid, storeGuid);
        if (!access.Success)
        {
            return ApiResponse<UserStorePosTerminalPermissionsResponse>.Error(
                access.Message,
                access.ErrorCode
            );
        }

        var db = context.Db;
        var committedAccess = access;
        await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
        try
        {
            var transactionAccess = await ResolveAccessAsync(userGuid, storeGuid);
            if (!transactionAccess.Success)
            {
                await db.Ado.RollbackTranAsync();
                return ApiResponse<UserStorePosTerminalPermissionsResponse>.Error(
                    transactionAccess.Message,
                    transactionAccess.ErrorCode
                );
            }

            var assignableCodes = GetAssignablePermissionSeeds(transactionAccess)
                .Select(item => item.Code)
                .ToList();
            var now = DateTime.UtcNow;
            var actor = currentUserService.GetCurrentUsername();
            if (assignableCodes.Count > 0)
            {
                await db.Updateable<SysUserStorePosPermission>()
                    .SetColumns(item => item.IsDeleted == true)
                    .SetColumns(item => item.UpdatedAt == now)
                    .SetColumns(item => item.UpdatedBy == actor)
                    .Where(item =>
                        item.UserGuid == userGuid
                        && item.StoreGuid == storeGuid
                        && assignableCodes.Contains(item.PermissionCode)
                        && !item.IsDeleted
                    )
                    .ExecuteCommandAsync();
            }

            await db.Ado.CommitTranAsync();
            committedAccess = transactionAccess;
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        return ApiResponse<UserStorePosTerminalPermissionsResponse>.OK(
            await BuildResponseAsync(userGuid, storeGuid, committedAccess),
            "已恢复继承分店 POS 权限"
        );
    }

    private async Task<AccessResult> ResolveAccessAsync(string userGuid, string storeGuid)
    {
        if (string.IsNullOrWhiteSpace(userGuid) || string.IsNullOrWhiteSpace(storeGuid))
        {
            return AccessResult.Denied("用户或分店标识不能为空", "POS_PERMISSION_INVALID_SCOPE");
        }

        var db = context.Db;
        var targetExists = await db.Queryable<User>()
            .AnyAsync(item => item.UserGUID == userGuid && !item.IsDeleted && item.IsActive);
        var storeExists = await db.Queryable<Store>()
            .AnyAsync(item => item.StoreGUID == storeGuid && !item.IsDeleted && item.IsActive);
        if (!targetExists || !storeExists)
        {
            return AccessResult.Denied("用户或分店不存在", "POS_PERMISSION_NOT_FOUND");
        }

        var targetBelongsToStore = await db.Queryable<UserStore>()
            .AnyAsync(item =>
                item.UserGUID == userGuid
                && item.StoreGUID == storeGuid
                && !item.IsDeleted
            );
        if (!targetBelongsToStore)
        {
            return AccessResult.Denied("目标用户未关联该分店", "POS_PERMISSION_FORBIDDEN");
        }

        var scope = await storeScopeService.GetScopeAsync();
        var actorRoles = await GetActiveRoleNamesAsync(scope.UserGuid);
        var isAdminActor = actorRoles.Any(Permissions.IsSuperAdminRole);
        if (isAdminActor)
        {
            return AccessResult.Allowed(true, null);
        }

        var isStoreManager = actorRoles.Any(role =>
            CurrentUserManageableStoreScopeService.StoreManagerRoleAliases.Contains(
                role,
                StringComparer.OrdinalIgnoreCase
            )
        );
        // 仓库管理与店长同属「受限委派」：只能维护员工目标的 POS 业务权限，不能碰高权限账号和系统级覆盖。
        // 区别在分店范围：仓库管理在范围服务里是不限分店（IsAdmin），店长只限主分店，
        // 所以两者统一用 CanAccessStoreGuid 判断，不再单独放行。
        var isWarehouseManager = actorRoles.Any(role =>
            CurrentUserManageableStoreScopeService.WarehouseManagerRoleAliases.Contains(
                role,
                StringComparer.OrdinalIgnoreCase
            )
        );
        if (
            !(isStoreManager || isWarehouseManager)
            || !scope.IsAllowed
            || !scope.CanAccessStoreGuid(storeGuid)
        )
        {
            return AccessResult.Denied("当前账号不能管理该分店", "POS_PERMISSION_FORBIDDEN");
        }

        var targetRoles = await GetActiveRoleNamesAsync(userGuid);
        // 仓库管理优先于店长身份（同时有两个角色时以仓库管理为准，与 UserAccessMutationSecurity 一致）。
        // 它能维护店长账号的 POS 权限，所以受保护目标只算管理员和仓库管理账号；店长操作者仍按完整高权限口径保护。
        var protectedTarget = isWarehouseManager
            ? targetRoles.Any(role =>
                Permissions.IsSuperAdminRole(role)
                || CurrentUserManageableStoreScopeService.WarehouseManagerRoleAliases.Contains(
                    role,
                    StringComparer.OrdinalIgnoreCase
                ))
            : targetRoles.Any(role =>
                HighPrivilegeRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase));
        if (protectedTarget)
        {
            return AccessResult.Denied(
                isWarehouseManager ? "不能修改管理员和仓库管理账号" : "店长不能修改高权限账号",
                "POS_PERMISSION_FORBIDDEN"
            );
        }

        var targetIsStoreManager = targetRoles.Any(role =>
            CurrentUserManageableStoreScopeService.StoreManagerRoleAliases.Contains(
                role,
                StringComparer.OrdinalIgnoreCase
            ));
        if (!targetRoles.Any(Permissions.IsEmployeeRole) && !(isWarehouseManager && targetIsStoreManager))
        {
            return AccessResult.Denied(
                isWarehouseManager
                    ? "仓库管理只能维护员工和店长的 POS 收银权限"
                    : "店长只能维护员工的 POS 收银权限",
                "EMPLOYEE_TARGET_REQUIRED"
            );
        }

        // 仓库管理自己不使用收银端，角色模板里没有任何 POS 业务码；若仍按「本人持有」收口，
        // 可分配范围永远为空，入口形同虚设。因此仓库管理可分配完整的 POS 业务白名单（仍不含系统级权限）。
        // 店长维持原规则：POS 业务白名单 ∩ 本人有效权限，防止越权授予自己没有的能力。
        if (isWarehouseManager)
        {
            return AccessResult.Allowed(
                false,
                PermissionSeedData.PosTerminalBusinessPermissionCodes.ToArray()
            );
        }

        var actorEffectivePermissionCodes = await UserAccessMutationSecurity
            .GetEffectivePermissionCodesAsync(db, scope.UserGuid);
        var assignablePermissionCodes = PermissionSeedData.PosTerminalBusinessPermissionCodes
            .Where(actorEffectivePermissionCodes.Contains)
            .ToArray();

        return AccessResult.Allowed(false, assignablePermissionCodes);
    }

    private async Task<UserStorePosTerminalPermissionsResponse> BuildResponseAsync(
        string userGuid,
        string storeGuid,
        AccessResult access
    )
    {
        var db = context.Db;
        var targetRoleNames = await GetActiveRoleNamesAsync(userGuid);
        var isAdminTarget = targetRoleNames.Any(Permissions.IsSuperAdminRole);
        var allPosCodes = PermissionSeedData.AllPermissions
            .Where(item => item.Code.StartsWith("Permissions.PosTerminal.", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> inheritedCodes;
        if (isAdminTarget)
        {
            inheritedCodes = allPosCodes;
        }
        else
        {
            var roleGuids = await db.Queryable<UserRole>()
                .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                .Where((userRole, role) =>
                    userRole.UserGUID == userGuid
                    && !userRole.IsDeleted
                    && role.IsActive
                    && !role.IsDeleted
                )
                .Select((userRole, role) => role.RoleGUID)
                .ToListAsync();
            var roleCodes = roleGuids.Count == 0
                ? new List<string>()
                : await db.Queryable<SysRolePermission>()
                    .Where(item => roleGuids.Contains(item.RoleGuid) && !item.IsDeleted)
                    .Select(item => item.PermissionCode)
                    .ToListAsync();
            var directCodes = await db.Queryable<SysUserPermission>()
                .Where(item => item.UserGuid == userGuid && !item.IsDeleted)
                .Select(item => item.PermissionCode)
                .ToListAsync();
            inheritedCodes = Permissions.ExpandPermissionCodes(roleCodes.Concat(directCodes))
                .Where(code => allPosCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var allOverrideRows = await db.Queryable<SysUserStorePosPermission>()
            .Where(item =>
                item.UserGuid == userGuid
                && item.StoreGuid == storeGuid
                && !item.IsDeleted
            )
            .ToListAsync();
        var overrideMap = allOverrideRows
            .GroupBy(item => item.PermissionCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.UpdatedAt).First().IsGranted,
                StringComparer.OrdinalIgnoreCase
            );
        var assignablePermissions = GetAssignablePermissionSeeds(access);
        var assignableCodes = assignablePermissions
            .Select(item => item.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visibleOverrides = overrideMap
            .Where(item => assignableCodes.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        if (!access.IsAdminActor)
        {
            inheritedCodes = inheritedCodes
                .Where(assignableCodes.Contains)
                .ToList();
        }
        var effectiveOverrides = access.IsAdminActor ? overrideMap : visibleOverrides;

        return new UserStorePosTerminalPermissionsResponse
        {
            Mode = visibleOverrides.Count == 0 ? "Inherited" : "Override",
            AssignablePermissions = assignablePermissions,
            InheritedPermissionCodes = inheritedCodes.OrderBy(code => code).ToList(),
            OverriddenPermissionCodes = visibleOverrides.Keys.OrderBy(code => code).ToList(),
            GrantedPermissionCodes = visibleOverrides
                .Where(item => item.Value)
                .Select(item => item.Key)
                .OrderBy(code => code)
                .ToList(),
            EffectivePermissionCodes = UserStorePosTerminalPermissionResolver
                .ResolveEffectivePermissionCodes(inheritedCodes, effectiveOverrides, isAdminTarget)
                .OrderBy(code => code)
                .ToList(),
        };
    }

    private async Task<List<string>> GetActiveRoleNamesAsync(string userGuid)
    {
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            return new List<string>();
        }

        return await context.Db.Queryable<UserRole>()
            .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
            .Where((userRole, role) =>
                userRole.UserGUID == userGuid
                && !userRole.IsDeleted
                && role.IsActive
                && !role.IsDeleted
            )
            .Select((userRole, role) => role.RoleName)
            .ToListAsync();
    }

    private static List<PosTerminalAssignablePermissionDto> GetAssignablePermissionSeeds(
        AccessResult access
    )
    {
        var allowedCodes = access.IsAdminActor
            ? null
            : (access.AssignablePermissionCodes ?? Array.Empty<string>()).ToHashSet(
                StringComparer.OrdinalIgnoreCase
            );

        return PermissionSeedData.AllPermissions
            .Where(item => item.Code.StartsWith("Permissions.PosTerminal.", StringComparison.OrdinalIgnoreCase))
            .Where(item => allowedCodes == null || allowedCodes.Contains(item.Code))
            .Select(item => new PosTerminalAssignablePermissionDto
            {
                Code = item.Code,
                Name = item.Name,
                Group = item.Category,
                Description = item.Description,
            })
            .OrderBy(item => item.Code)
            .ToList();
    }

    private sealed record AccessResult(
        bool Success,
        bool IsAdminActor,
        IReadOnlyCollection<string>? AssignablePermissionCodes,
        string Message,
        string? ErrorCode
    )
    {
        public static AccessResult Allowed(
            bool isAdminActor,
            IReadOnlyCollection<string>? assignablePermissionCodes
        ) => new(true, isAdminActor, assignablePermissionCodes, string.Empty, null);

        public static AccessResult Denied(string message, string errorCode) =>
            new(false, false, Array.Empty<string>(), message, errorCode);
    }
}
