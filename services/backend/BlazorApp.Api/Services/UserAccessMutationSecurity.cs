using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 用户访问管理写入的统一安全边界，供角色、权限和分店关系服务共同使用。
    /// </summary>
    internal static class UserAccessMutationSecurity
    {
        internal static async Task<UserAccessMutationActor> ResolveActorAsync(
            ISqlSugarClient db,
            ICurrentUserManageableStoreScopeService? manageableStoreScopeService
        )
        {
            if (manageableStoreScopeService == null)
            {
                return UserAccessMutationActor.Denied;
            }

            var scope = await manageableStoreScopeService.GetScopeAsync();
            if (!scope.IsAuthenticated || string.IsNullOrWhiteSpace(scope.UserGuid))
            {
                return UserAccessMutationActor.Denied;
            }

            if (!scope.IsAllowed)
            {
                return UserAccessMutationActor.DeniedFor(scope.UserGuid);
            }

            return await ResolveActorByUserGuidAsync(db, scope.UserGuid);
        }

        internal static async Task<UserAccessMutationActor> ResolveActorByUserGuidAsync(
            ISqlSugarClient db,
            string userGuid
        )
        {
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return UserAccessMutationActor.Denied;
            }

            var accessRoleNames = Permissions.SuperAdminRoleNames
                .Concat(Permissions.StoreManagerRoleNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var actorRoleNames = await db.Queryable<UserRole>()
                .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                .Where(
                    (userRole, role) =>
                        userRole.UserGUID == userGuid
                        && !userRole.IsDeleted
                        && !role.IsDeleted
                        && role.IsActive
                        && accessRoleNames.Contains(role.RoleName)
                )
                .Select((userRole, role) => role.RoleName)
                .ToListAsync();

            if (
                actorRoleNames.Any(roleName =>
                    Permissions.IsSuperAdminRole(roleName)
                )
            )
            {
                return UserAccessMutationActor.Admin(userGuid);
            }

            if (!actorRoleNames.Any(Permissions.IsStoreManagerRole))
            {
                return UserAccessMutationActor.DeniedFor(userGuid);
            }

            var manageableStoreGuids = (
                await db.Queryable<UserStore>()
                    .InnerJoin<Store>((userStore, store) =>
                        userStore.StoreGUID == store.StoreGUID
                    )
                    .Where((userStore, store) =>
                        userStore.UserGUID == userGuid
                        && !userStore.IsDeleted
                        && userStore.IsPrimary
                        && !store.IsDeleted
                        && store.IsActive
                    )
                    .Select((userStore, store) => userStore.StoreGUID)
                    .ToListAsync()
            )
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return manageableStoreGuids.Length == 0
                ? UserAccessMutationActor.DeniedFor(userGuid)
                : UserAccessMutationActor.StoreManager(userGuid, manageableStoreGuids);
        }

        internal static async Task<UserAccessMutationDecision> ValidateTargetAsync(
            ISqlSugarClient db,
            UserAccessMutationActor actor,
            string targetUserGuid
        )
        {
            if (!actor.IsEnforced)
            {
                return UserAccessMutationDecision.Allow;
            }

            if (actor.Kind == UserAccessMutationActorKind.Denied)
            {
                return new(false, "当前账号不能分配员工访问权限", "ACCESS_DELEGATOR_DENIED");
            }

            if (actor.IsSuperAdmin)
            {
                return UserAccessMutationDecision.Allow;
            }

            // 店长禁止通过员工管理入口修改本人；Admin 保留完整管理能力。
            if (string.Equals(actor.UserGuid, targetUserGuid, StringComparison.OrdinalIgnoreCase))
            {
                return new(false, "不能修改本人的访问权限", "SELF_ACCESS_MANAGEMENT_DENIED");
            }

            var targetIsHighPrivilege = await IsHighPrivilegeTargetAsync(db, targetUserGuid);
            if (targetIsHighPrivilege)
            {
                return new(false, "不能修改高权限账号", "HIGH_PRIVILEGE_TARGET_DENIED");
            }

            if (actor.HasUnrestrictedStoreScope)
            {
                return UserAccessMutationDecision.Allow;
            }

            var scopedStoreGuids = actor.StoreGuids
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (
                scopedStoreGuids.Length == 0
                || !await db.Queryable<UserStore>()
                    .AnyAsync(item =>
                        item.UserGUID == targetUserGuid
                        && !item.IsDeleted
                        && scopedStoreGuids.Contains(item.StoreGUID)
                    )
            )
            {
                return new(false, "无权修改该用户", "USER_SCOPE_DENIED");
            }

            return UserAccessMutationDecision.Allow;
        }

        internal static async Task<UserAccessMutationDecision> ValidateGlobalTargetAsync(
            ISqlSugarClient db,
            UserAccessMutationActor actor,
            string targetUserGuid
        )
        {
            var targetDecision = await ValidateTargetAsync(db, actor, targetUserGuid);
            if (!targetDecision.IsAllowed || !actor.IsEnforced || actor.IsSuperAdmin)
            {
                return targetDecision;
            }

            var actorStoreGuids = actor.StoreGuids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targetStoreGuids = await db.Queryable<UserStore>()
                .InnerJoin<Store>((userStore, store) => userStore.StoreGUID == store.StoreGUID)
                .Where((userStore, store) =>
                    userStore.UserGUID == targetUserGuid
                    && !userStore.IsDeleted
                    && !store.IsDeleted
                )
                .Select((userStore, store) => userStore.StoreGUID)
                .ToListAsync();

            // 角色与直接权限是全局能力，目标任一有效分店越界都必须拒绝。
            return targetStoreGuids.Any(storeGuid => !actorStoreGuids.Contains(storeGuid))
                ? new(false, "无权修改跨分店用户的全局权限", "USER_SCOPE_DENIED")
                : UserAccessMutationDecision.Allow;
        }

        internal static async Task<UserAccessMutationDecision> ValidateAdminTargetAsync(
            ISqlSugarClient db,
            UserAccessMutationActor actor,
            string targetUserGuid
        )
        {
            if (!actor.IsSuperAdmin)
            {
                return new(false, "只有管理员可以修改用户角色或分店", "ADMIN_REQUIRED");
            }

            return await ValidateTargetAsync(db, actor, targetUserGuid);
        }

        internal static async Task<UserAccessMutationDecision> ValidateAdminOperationAsync(
            ISqlSugarClient db,
            ICurrentUserManageableStoreScopeService? manageableStoreScopeService
        )
        {
            var actor = await ResolveActorAsync(db, manageableStoreScopeService);
            return actor.IsSuperAdmin
                ? UserAccessMutationDecision.Allow
                : new(false, "只有管理员可以维护全局角色与权限定义", "ADMIN_REQUIRED");
        }

        internal static async Task<UserAccessReadDecision> ValidateReadTargetAsync(
            ISqlSugarClient db,
            string actorUserGuid,
            string targetUserGuid
        )
        {
            var targetExists = await db.Queryable<User>()
                .AnyAsync(item => item.UserGUID == targetUserGuid && !item.IsDeleted);
            if (!targetExists)
            {
                return UserAccessReadDecision.NotFound;
            }

            if (string.IsNullOrWhiteSpace(actorUserGuid))
            {
                return UserAccessReadDecision.Forbidden;
            }

            if (string.Equals(actorUserGuid, targetUserGuid, StringComparison.OrdinalIgnoreCase))
            {
                var selfStoreGuids = await db.Queryable<UserStore>()
                    .Where(item => item.UserGUID == targetUserGuid && !item.IsDeleted)
                    .Select(item => item.StoreGUID)
                    .ToListAsync();
                return UserAccessReadDecision.Self(
                    selfStoreGuids
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                );
            }

            var actor = await ResolveActorByUserGuidAsync(db, actorUserGuid);
            if (actor.Kind == UserAccessMutationActorKind.Denied)
            {
                return UserAccessReadDecision.Forbidden;
            }

            if (actor.IsSuperAdmin)
            {
                return UserAccessReadDecision.Unrestricted;
            }

            var targetStoreGuids = await db.Queryable<UserStore>()
                .Where(item => item.UserGUID == targetUserGuid && !item.IsDeleted)
                .Select(item => item.StoreGUID)
                .ToListAsync();
            var normalizedTargetStoreGuids = targetStoreGuids
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (await IsHighPrivilegeTargetAsync(db, targetUserGuid))
            {
                return UserAccessReadDecision.Forbidden;
            }

            var visibleStoreGuids = actor.StoreGuids
                .Intersect(normalizedTargetStoreGuids, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return visibleStoreGuids.Length == 0
                ? UserAccessReadDecision.Forbidden
                : UserAccessReadDecision.Delegated(visibleStoreGuids);
        }

        internal static async Task<HashSet<string>> GetEffectivePermissionCodesAsync(
            ISqlSugarClient db,
            string userGuid
        )
        {
            var roleGuids = await db.Queryable<UserRole>()
                .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                .Where(
                    (userRole, role) =>
                        userRole.UserGUID == userGuid
                        && !userRole.IsDeleted
                        && !role.IsDeleted
                        && role.IsActive
                )
                .Select((userRole, role) => userRole.RoleGUID)
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

            return Permissions.ExpandPermissionCodes(roleCodes.Concat(directCodes))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        internal static async Task<bool> IsEmployeeTargetAsync(
            ISqlSugarClient db,
            string targetUserGuid
        )
        {
            var isActiveUser = await db.Queryable<User>()
                .AnyAsync(user =>
                    user.UserGUID == targetUserGuid
                    && user.IsActive
                    && !user.IsDeleted
                );
            if (!isActiveUser)
            {
                return false;
            }

            var employeeRoleNames = Permissions.EmployeeRoleNames.ToArray();
            return await db.Queryable<UserRole>()
                .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                .Where((userRole, role) =>
                    userRole.UserGUID == targetUserGuid
                    && !userRole.IsDeleted
                    && role.IsActive
                    && !role.IsDeleted
                    && employeeRoleNames.Contains(role.RoleName)
                )
                .AnyAsync();
        }

        internal static async Task<UserAccessMutationDecision> ValidateStoreTargetMutationAsync(
            ISqlSugarClient db,
            ICurrentUserManageableStoreScopeService? manageableStoreScopeService,
            string targetUserGuid,
            string storeGuid,
            bool grantsManagement
        )
        {
            var actor = await ResolveActorAsync(db, manageableStoreScopeService);
            var targetDecision = await ValidateAdminTargetAsync(db, actor, targetUserGuid);
            if (!targetDecision.IsAllowed)
            {
                return targetDecision;
            }

            return UserAccessMutationDecision.Allow;
        }

        internal static async Task<HashSet<string>> GetHighPrivilegeUserGuidsAsync(
            ISqlSugarClient db,
            IReadOnlyCollection<string>? candidateUserGuids = null
        )
        {
            var candidates = candidateUserGuids?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (candidates is { Length: 0 })
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var highPrivilegeRoleNames = Permissions.HighPrivilegeRoleNames.ToArray();
            var highRoleQuery = db.Queryable<UserRole>()
                .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                .Where((userRole, role) =>
                    !userRole.IsDeleted
                    && !role.IsDeleted
                    && role.IsActive
                    && highPrivilegeRoleNames.Contains(role.RoleName)
                );
            if (candidates != null)
            {
                highRoleQuery = highRoleQuery.Where((userRole, role) =>
                    candidates.Contains(userRole.UserGUID)
                );
            }
            var result = (await highRoleQuery
                    .Select((userRole, role) => userRole.UserGUID)
                    .Distinct()
                    .ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var highPrivilegePermissionCodes = new HashSet<string>(
                new[]
                {
                    Permissions.Users.Create,
                    Permissions.Users.View,
                    Permissions.Users.Edit,
                    Permissions.Users.Delete,
                    Permissions.Users.ManageRoles,
                    Permissions.Users.ManageStores,
                    Permissions.Users.ManagePosTerminalPermissions,
                    Permissions.Users.ResetPassword,
                    Permissions.Users.CreateStoreStaff,
                    Permissions.Users.EditStoreStaff,
                    Permissions.Users.ResetStoreStaffPassword,
                    Permissions.Roles.Create,
                    Permissions.Roles.View,
                    Permissions.Roles.Edit,
                    Permissions.Roles.Delete,
                    Permissions.Roles.ManagePermissions,
                    Permissions.Roles.ManageUsers,
                    Permissions.System.ManageSettings,
                },
                StringComparer.OrdinalIgnoreCase
            );
            var rolePermissionQuery = db.Queryable<UserRole>()
                .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                .InnerJoin<SysRolePermission>((userRole, role, permission) =>
                    userRole.RoleGUID == permission.RoleGuid
                )
                .Where((userRole, role, permission) =>
                    !userRole.IsDeleted
                    && !role.IsDeleted
                    && role.IsActive
                    && !permission.IsDeleted
                );
            if (candidates != null)
            {
                rolePermissionQuery = rolePermissionQuery.Where((userRole, role, permission) =>
                    candidates.Contains(userRole.UserGUID)
                );
            }
            var rolePermissions = await rolePermissionQuery
                .Select((userRole, role, permission) => new
                {
                    UserGuid = userRole.UserGUID,
                    permission.PermissionCode,
                })
                .ToListAsync();

            var directPermissionQuery = db.Queryable<SysUserPermission>()
                .Where(permission => !permission.IsDeleted);
            if (candidates != null)
            {
                directPermissionQuery = directPermissionQuery.Where(permission =>
                    candidates.Contains(permission.UserGuid)
                );
            }
            var directPermissions = await directPermissionQuery
                .Select(permission => new
                {
                    permission.UserGuid,
                    permission.PermissionCode,
                })
                .ToListAsync();

            foreach (var permissionGroup in rolePermissions
                .Concat(directPermissions)
                .GroupBy(item => item.UserGuid, StringComparer.OrdinalIgnoreCase))
            {
                var effective = Permissions.ExpandPermissionCodes(
                    permissionGroup.Select(item => item.PermissionCode)
                );
                if (effective.Any(highPrivilegePermissionCodes.Contains))
                {
                    result.Add(permissionGroup.Key);
                }
            }
            return result;
        }

        internal static async Task<bool> IsHighPrivilegeTargetAsync(
            ISqlSugarClient db,
            string targetUserGuid
        )
        {
            var targets = await GetHighPrivilegeUserGuidsAsync(
                db,
                new[] { targetUserGuid }
            );
            return targets.Contains(targetUserGuid);
        }
    }

    /// <summary>
    /// 保持可管理分店关系与派生店长角色一致；调用方必须将其放在 UserStore 同一事务中。
    /// </summary>
    internal static class UserStoreManagerRoleSynchronizer
    {
        internal static async Task SynchronizeAsync(ISqlSugarClient db, string userGuid)
        {
            var managerRoleNames = Permissions.StoreManagerRoleNames.ToArray();
            var managerRoles = await db.Queryable<Role>()
                .Where(role => !role.IsDeleted && managerRoleNames.Contains(role.RoleName))
                .ToListAsync();
            var managerRoleGuids = managerRoles.Select(role => role.RoleGUID).ToArray();
            var hasManageableStore = await db.Queryable<UserStore>()
                .AnyAsync(item =>
                    item.UserGUID == userGuid && !item.IsDeleted && item.IsPrimary
                );

            if (!hasManageableStore)
            {
                if (managerRoleGuids.Length > 0)
                {
                    await db.Deleteable<UserRole>()
                        .Where(item =>
                            item.UserGUID == userGuid && managerRoleGuids.Contains(item.RoleGUID)
                        )
                        .ExecuteCommandAsync();
                }
                return;
            }

            var alreadyHasManagerRole = managerRoleGuids.Length > 0
                && await db.Queryable<UserRole>()
                    .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                    .Where((userRole, role) =>
                        userRole.UserGUID == userGuid
                        && !userRole.IsDeleted
                        && managerRoleGuids.Contains(userRole.RoleGUID)
                        && !role.IsDeleted
                        && role.IsActive
                    )
                    .AnyAsync();
            if (alreadyHasManagerRole)
            {
                return;
            }

            var canonicalRole = managerRoles
                .Where(role => role.IsActive)
                .OrderBy(role =>
                    Array.FindIndex(
                        Permissions.StoreManagerRoleNames,
                        name => string.Equals(name, role.RoleName, StringComparison.OrdinalIgnoreCase)
                    )
                )
                .FirstOrDefault();
            if (canonicalRole == null)
            {
                throw new InvalidOperationException("缺少可用的店长角色，无法同步分店管理关系");
            }

            // 店长角色是分店管理关系的派生数据，禁止由独立角色接口直接维护。
            await db.Insertable(new UserRole
            {
                UserRoleGUID = Guid.NewGuid().ToString(),
                UserGUID = userGuid,
                RoleGUID = canonicalRole.RoleGUID,
                AssignedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
        }
    }

    internal enum UserAccessMutationActorKind
    {
        Denied,
        Admin,
        StoreManager,
    }

    internal sealed record UserAccessMutationActor(
        UserAccessMutationActorKind Kind,
        string UserGuid,
        IReadOnlyCollection<string> StoreGuids
    )
    {
        internal bool IsEnforced => true;
        internal bool IsSuperAdmin => Kind == UserAccessMutationActorKind.Admin;
        internal bool IsStoreManager => Kind == UserAccessMutationActorKind.StoreManager;
        internal bool HasUnrestrictedStoreScope => IsSuperAdmin;

        internal static UserAccessMutationActor Denied { get; } =
            new(UserAccessMutationActorKind.Denied, string.Empty, Array.Empty<string>());

        internal static UserAccessMutationActor DeniedFor(string userGuid) =>
            new(UserAccessMutationActorKind.Denied, userGuid, Array.Empty<string>());

        internal static UserAccessMutationActor Admin(string userGuid) =>
            new(UserAccessMutationActorKind.Admin, userGuid, Array.Empty<string>());

        internal static UserAccessMutationActor StoreManager(
            string userGuid,
            IReadOnlyCollection<string> storeGuids
        ) => new(UserAccessMutationActorKind.StoreManager, userGuid, storeGuids);
    }

    internal sealed record UserAccessMutationDecision(
        bool IsAllowed,
        string Message,
        string ErrorCode
    )
    {
        internal static UserAccessMutationDecision Allow { get; } =
            new(true, string.Empty, string.Empty);
    }

    internal sealed record UserAccessReadDecision(
        bool IsAllowed,
        bool IsNotFound,
        bool RequiresDelegatedPermission,
        IReadOnlyCollection<string>? VisibleStoreGuids
    )
    {
        internal static UserAccessReadDecision Unrestricted { get; } =
            new(true, false, false, null);
        internal static UserAccessReadDecision Forbidden { get; } =
            new(false, false, false, Array.Empty<string>());
        internal static UserAccessReadDecision NotFound { get; } =
            new(false, true, false, Array.Empty<string>());

        internal static UserAccessReadDecision Self(IReadOnlyCollection<string> storeGuids) =>
            new(true, false, false, storeGuids);

        internal static UserAccessReadDecision Delegated(
            IReadOnlyCollection<string> storeGuids
        ) => new(true, false, true, storeGuids);
    }
}
