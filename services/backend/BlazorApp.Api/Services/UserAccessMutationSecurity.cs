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
                .Concat(Permissions.WarehouseManagerRoleNames)
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

            // 仓库管理是「不限分店、但不是超管」的受限委派者：和范围服务里的口径一致（看全部分店），
            // 但提权相关的动作（授 Admin / 仓库管理角色、改管理员账号）都由下面各校验单独拦住。
            // 同时有店长角色时以仓库管理为准（范围更大），店长身份仍由主分店关系派生保留。
            if (actorRoleNames.Any(IsWarehouseManagerRoleName))
            {
                return UserAccessMutationActor.WarehouseManager(userGuid);
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

            // 店长按完整的高权限口径保护（含店长账号自己）；仓库管理要能把店长改成店员，
            // 所以只保护管理员、仓库管理账号和带提升类权限码的账号，店长账号可以改。
            var targetIsHighPrivilege = actor.IsWarehouseManager
                ? await IsWarehouseProtectedTargetAsync(db, targetUserGuid)
                : await IsHighPrivilegeTargetAsync(db, targetUserGuid);
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
            // 不限分店的操作者（管理员、仓库管理）不需要再做跨店越界检查；目标保护已在上面完成。
            if (!targetDecision.IsAllowed || !actor.IsEnforced || actor.HasUnrestrictedStoreScope)
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

        /// <summary>
        /// 分店关系（只动 UserStore，不碰角色定义）的目标校验：管理员不受限；仓库管理可以操作任意分店，
        /// 但目标保护、本人保护照常生效；店长及其他账号一律要求管理员。
        /// </summary>
        internal static async Task<UserAccessMutationDecision> ValidateStoreRelationTargetAsync(
            ISqlSugarClient db,
            UserAccessMutationActor actor,
            string targetUserGuid
        )
        {
            if (!actor.IsSuperAdmin && !actor.IsWarehouseManager)
            {
                return new(false, "只有管理员或仓库管理可以修改用户分店", "ADMIN_REQUIRED");
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

            // 仓库管理能读除管理员 / 仓库管理账号外的任意账号（含店长，要把店长改成店员就得先读到），
            // 不限分店，但仍要求持有控制器指定的委派权限（Users.ManageRoles / ManageStores / ManagePos 之一）。
            if (actor.IsWarehouseManager)
            {
                return await IsWarehouseProtectedTargetAsync(db, targetUserGuid)
                    ? UserAccessReadDecision.Forbidden
                    : UserAccessReadDecision.DelegatedAllStores;
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

            // 店长只能读「店员」角色的账号；订货员、仓库员工等其他角色即使在自己管理的分店里也不可见。
            var nonEmployee = await GetNonEmployeeUserGuidsAsync(db, new[] { targetUserGuid });
            if (nonEmployee.Count > 0)
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

        internal static async Task<bool> IsStoreManagerTargetAsync(
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

            var managerRoleNames = Permissions.StoreManagerRoleNames.ToArray();
            return await db.Queryable<UserRole>()
                .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                .Where((userRole, role) =>
                    userRole.UserGUID == targetUserGuid
                    && !userRole.IsDeleted
                    && role.IsActive
                    && !role.IsDeleted
                    && managerRoleNames.Contains(role.RoleName)
                )
                .AnyAsync();
        }

        /// <summary>
        /// 受限操作者（店长、仓库管理）维护目标「直接权限」时的委派范围，三处共用：读可分配权限、保存、校验。
        /// - 店长：目标必须是员工；可授 = POS 业务权限 ∩ 本人持有。
        /// - 仓库管理：目标可以是员工或店长。员工同店长口径；店长目标可授 = 本人持有的权限，
        ///   去掉提升类权限码（<see cref="WarehouseProtectedPermissionCodes"/>）和非业务的 POS 系统级权限，
        ///   所以任何情况下都不会授出超过仓库管理自己的权限，也不会授出能改角色 / 系统设置的权限。
        /// </summary>
        internal static async Task<PermissionDelegation> ResolvePermissionDelegationAsync(
            ISqlSugarClient db,
            UserAccessMutationActor actor,
            string targetUserGuid
        )
        {
            var posBusinessCodes = PermissionSeedData.PosTerminalBusinessPermissionCodes
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targetIsEmployee = await IsEmployeeTargetAsync(db, targetUserGuid);
            var targetIsStoreManager =
                actor.IsWarehouseManager && await IsStoreManagerTargetAsync(db, targetUserGuid);
            if (!targetIsEmployee && !targetIsStoreManager)
            {
                return PermissionDelegation.TargetDenied;
            }

            var actorCodes = await GetEffectivePermissionCodesAsync(db, actor.UserGuid);
            if (!targetIsStoreManager)
            {
                return new PermissionDelegation(
                    true,
                    false,
                    code => posBusinessCodes.Contains(code),
                    posBusinessCodes.Where(actorCodes.Contains)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                );
            }

            var elevated = WarehouseProtectedPermissionCodes
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool IsDelegableCatalogCode(string code) =>
                !elevated.Contains(code)
                && (
                    !code.StartsWith("Permissions.PosTerminal.", StringComparison.OrdinalIgnoreCase)
                    || posBusinessCodes.Contains(code)
                );
            return new PermissionDelegation(
                true,
                true,
                IsDelegableCatalogCode,
                actorCodes.Where(IsDelegableCatalogCode)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
            );
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

        // 仓库管理委派场景下的「提升类」权限码：持有任一项的账号能改角色定义 / 系统设置 / 别人的角色，
        // 不能被仓库管理修改。店长角色自带的 Users.Create / ManageStores / ResetPassword 等不在其中，
        // 所以店长账号不受此规则保护。
        private static readonly string[] WarehouseProtectedPermissionCodes =
        {
            Permissions.Roles.Create,
            Permissions.Roles.Edit,
            Permissions.Roles.Delete,
            Permissions.Roles.ManagePermissions,
            Permissions.Roles.ManageUsers,
            Permissions.System.ManageSettings,
            Permissions.Users.Delete,
            Permissions.Users.ManageRoles,
        };

        internal static bool IsWarehouseManagerRoleName(string? roleName) =>
            !string.IsNullOrWhiteSpace(roleName)
            && Permissions.WarehouseManagerRoleNames.Contains(
                roleName,
                StringComparer.OrdinalIgnoreCase
            );

        /// <summary>
        /// 仓库管理不能操作的目标：管理员、仓库管理账号（按角色名），以及直接 / 经角色持有提升类权限码的账号。
        /// 比 <see cref="IsHighPrivilegeTargetAsync"/> 窄：店长账号不算受保护，仓库管理可以调整店长。
        /// </summary>
        internal static async Task<bool> IsWarehouseProtectedTargetAsync(
            ISqlSugarClient db,
            string targetUserGuid
        )
        {
            var targets = await GetHighPrivilegeUserGuidsAsync(
                db,
                new[] { targetUserGuid },
                warehouseDelegation: true
            );
            return targets.Contains(targetUserGuid);
        }

        internal static async Task<HashSet<string>> GetHighPrivilegeUserGuidsAsync(
            ISqlSugarClient db,
            IReadOnlyCollection<string>? candidateUserGuids = null,
            bool warehouseDelegation = false
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

            // 仓库管理委派场景只按「管理员 + 仓库管理」角色名保护；其余沿用完整高权限口径。
            var highPrivilegeRoleNames = warehouseDelegation
                ? Permissions.SuperAdminRoleNames
                    .Concat(Permissions.WarehouseManagerRoleNames)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Permissions.HighPrivilegeRoleNames.ToArray();
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
                    Permissions.Users.ViewStoreStaff,
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
            if (warehouseDelegation)
            {
                // 仓库管理委派场景：只认提升类权限码，店长角色的 Users.* 店员管理码不再触发保护。
                highPrivilegePermissionCodes = new HashSet<string>(
                    WarehouseProtectedPermissionCodes,
                    StringComparer.OrdinalIgnoreCase
                );
            }
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

        /// <summary>
        /// 候选账号里「没有任何员工（店员）角色」的那部分。
        /// 产品规则：店长只能看到自己管理分店里角色是店员的账号（外加本人），订货员、仓库员工、普通 User 等
        /// 其他角色对店长不可见——与 store-users 店员列表同一口径（有员工角色且不是高权限账号）。
        /// 员工角色认 <see cref="Permissions.EmployeeRoleNames"/>（含中英文历史别名）；与店员列表一致，不要求角色处于启用状态。
        /// </summary>
        internal static async Task<HashSet<string>> GetNonEmployeeUserGuidsAsync(
            ISqlSugarClient db,
            IReadOnlyCollection<string> candidateUserGuids
        )
        {
            var candidates = candidateUserGuids
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (candidates.Length == 0)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var employeeRoleNames = Permissions.EmployeeRoleNames.ToArray();
            var employeeUserGuids = (await db.Queryable<UserRole>()
                    .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
                    .Where((userRole, role) =>
                        !userRole.IsDeleted
                        && !role.IsDeleted
                        && employeeRoleNames.Contains(role.RoleName)
                        && candidates.Contains(userRole.UserGUID)
                    )
                    .Select((userRole, role) => userRole.UserGUID)
                    .Distinct()
                    .ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return candidates
                .Where(userGuid => !employeeUserGuids.Contains(userGuid))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        // 仓库管理：不限分店，但不是超管。凡是写 `!IsSuperAdmin` 的受限分支对它仍然生效（只能授员工角色、
        // 只能分配 POS 业务权限），只有「授分店 / 读目标」这些明确放行的地方才按不限分店处理。
        WarehouseManager,
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
        internal bool IsWarehouseManager => Kind == UserAccessMutationActorKind.WarehouseManager;
        internal bool HasUnrestrictedStoreScope => IsSuperAdmin || IsWarehouseManager;
        // 能授予「管理」分店关系（主分店，会派生店长角色）：管理员和仓库管理；店长只能授查看关系。
        internal bool CanGrantManageableStores => IsSuperAdmin || IsWarehouseManager;

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

        internal static UserAccessMutationActor WarehouseManager(string userGuid) =>
            new(UserAccessMutationActorKind.WarehouseManager, userGuid, Array.Empty<string>());
    }

    /// <summary>
    /// 受限操作者对某个目标的「直接权限」委派范围。
    /// IsDelegableCatalogCode：该权限码在类别上允许被委派（与操作者是否持有无关，用来区分「越类别」与「越权」两种错误）；
    /// DelegableCodes：真正可授 = 类别允许 ∩ 操作者本人持有。
    /// </summary>
    internal sealed record PermissionDelegation(
        bool TargetAllowed,
        bool TargetIsStoreManager,
        Func<string, bool> IsDelegableCatalogCode,
        HashSet<string> DelegableCodes
    )
    {
        internal static PermissionDelegation TargetDenied { get; } =
            new(false, false, _ => false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
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
        // 不限分店，但调用方仍须持有控制器指定的委派权限（仓库管理读取目标的角色 / 分店）。
        internal static UserAccessReadDecision DelegatedAllStores { get; } =
            new(true, false, true, null);
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
