using BlazorApp.Api.Data;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 种子数据服务
    /// </summary>
    public class SeedDataService
    {
        private readonly SqlSugarContext _dbContext;
        private readonly ILogger<SeedDataService> _logger;
        private static readonly string[] AdminRoleNames = Permissions.SuperAdminRoleNames;
        private static readonly string[] OrderRoleNames = { "Order", "订货员" };
        private static readonly string[] OrderRoleTemplateRevokedPermissionCodes =
        {
            Permissions.Orders.View,
            Permissions.Orders.Edit,
            Permissions.LocalPurchase.View,
            "LocalInvocie.View",
        };

        public SeedDataService(SqlSugarContext dbContext, ILogger<SeedDataService> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        /// <summary>
        /// 初始化所有种子数据
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                // 1. 首先创建角色（必须在用户之前）
                await CreateRoleSeedDataAsync();

                // 2. 创建管理员用户
                await CreateAdminUserAsync();

                // 3. 立即为管理员分配Admin角色（确保默认角色）
                await AssignAdminRoleAsync();

                // 4. 创建默认店铺
                // await CreateDefaultStoreAsync();

                // 5. 为admin用户分配默认店铺
                // await AssignDefaultStoreToAdminAsync();

                // 6. 初始化权限数据并分配给Admin
                await InitializePermissionSeedsAsync();

                // 7. 验证管理员角色分配
                await VerifyAdminRoleAssignmentAsync();

                _logger.LogInformation("种子数据初始化完成 - admin用户已分配Admin角色和默认店铺");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "种子数据初始化失败");
                throw;
            }
        }

        /// <summary>
        /// 创建角色种子数据
        /// </summary>
        private async Task CreateRoleSeedDataAsync()
        {
            var db = _dbContext.Db;

            // 检查是否已存在角色
            var existingRoles = await db.Queryable<Role>().ToListAsync();

            var rolesToCreate = new List<Role>();

            AddRoleIfMissing(
                existingRoles,
                rolesToCreate,
                "Admin",
                "System Administrator with full permissions"
            );
            AddRoleIfMissing(
                existingRoles,
                rolesToCreate,
                "Manager",
                "Manager role with business management permissions"
            );
            AddRoleIfMissing(
                existingRoles,
                rolesToCreate,
                "User",
                "Regular user with basic viewing permissions"
            );
            AddRoleIfMissing(existingRoles, rolesToCreate, "Order", "订货员");
            AddRoleIfMissing(
                existingRoles,
                rolesToCreate,
                "WarehouseManager",
                "Warehouse manager role for inventory and replenishment operations"
            );
            AddRoleIfMissing(
                existingRoles,
                rolesToCreate,
                "WarehouseStaff",
                "Warehouse staff role for product and location operations"
            );
            AddRoleIfMissing(
                existingRoles,
                rolesToCreate,
                "StoreManager",
                "Store manager role for branch staff maintenance"
            );
            AddRoleIfMissing(
                existingRoles,
                rolesToCreate,
                "StoreStaff",
                "Store staff role for branch employees"
            );

            if (rolesToCreate.Any())
            {
                await db.Insertable(rolesToCreate).ExecuteCommandAsync();
                _logger.LogInformation(
                    $"已创建 {rolesToCreate.Count} 个角色: {string.Join(", ", rolesToCreate.Select(r => r.RoleName))}"
                );
            }
            else
            {
                _logger.LogInformation("角色数据已存在，跳过创建");
            }
        }

        /// <summary>
        /// 创建管理员用户
        /// </summary>
        private async Task CreateAdminUserAsync()
        {
            var db = _dbContext.Db;

            var adminUser = await db.Queryable<User>().FirstAsync(u => u.Username == "admin");

            if (adminUser == null)
            {
                var seedUser = new User
                {
                    UserGUID = Guid.NewGuid().ToString(),
                    Username = "admin",
                    Email = "admin@example.com",
                    FullName = "Administrator",
                    // 默认密码直接交给后端慢哈希，避免继续生成可重放的旧客户端哈希。
                    PasswordHash = PasswordHasher.HashPassword("admin"),
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    LastLoginAt = null,
                };

                await db.Insertable(seedUser).ExecuteCommandAsync();
                _logger.LogInformation("管理员用户已创建，用户名: admin, 密码: admin");
            }
            else
            {
                // 确保管理员用户是激活状态
                if (!adminUser.IsActive)
                {
                    adminUser.IsActive = true;
                    adminUser.UpdatedAt = DateTime.UtcNow;
                    await db.Updateable(adminUser)
                        .UpdateColumns(u => new { u.IsActive, u.UpdatedAt })
                        .ExecuteCommandAsync();
                }
                _logger.LogInformation("管理员用户已存在");
            }
        }

        /// <summary>
        /// 为管理员分配Admin角色
        /// </summary>
        private async Task AssignAdminRoleAsync()
        {
            var db = _dbContext.Db;

            // 获取Admin角色和admin用户
            var adminRole = await db.Queryable<Role>().FirstAsync(r => r.RoleName == "Admin");

            var adminUser = await db.Queryable<User>().FirstAsync(u => u.Username == "admin");

            if (adminRole == null)
            {
                _logger.LogError("Admin角色不存在，无法分配给管理员用户");
                return;
            }

            if (adminUser == null)
            {
                _logger.LogError("管理员用户不存在，无法分配角色");
                return;
            }

            // 检查是否已存在角色关联
            var existingUserRole = await db.Queryable<UserRole>()
                .FirstAsync(ur =>
                    ur.UserGUID == adminUser.UserGUID && ur.RoleGUID == adminRole.RoleGUID
                );

            if (existingUserRole == null)
            {
                var userRole = new UserRole
                {
                    UserRoleGUID = Guid.NewGuid().ToString(),
                    UserGUID = adminUser.UserGUID,
                    RoleGUID = adminRole.RoleGUID,
                    AssignedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                };

                await db.Insertable(userRole).ExecuteCommandAsync();
                _logger.LogInformation("管理员用户已成功关联Admin角色");
            }
            else
            {
                _logger.LogInformation("管理员用户角色关联已存在");
            }
        }

        /// <summary>
        /// 创建默认店铺
        /// </summary>
        private async Task CreateDefaultStoreAsync()
        {
            var db = _dbContext.Db;

            // 检查是否已存在默认店铺
            var existingStore = await db.Queryable<Store>()
                .FirstAsync(s => s.StoreCode == "DEFAULT" || s.StoreName == "Default Store");

            if (existingStore == null)
            {
                var defaultStore = new Store
                {
                    StoreGUID = "default", // 使用固定的GUID，方便购物车API fallback使用
                    StoreCode = "DEFAULT",
                    StoreName = "Default Store",
                    Address = "Default Store Address",
                    Phone = "000-0000-0000",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                };

                await db.Insertable(defaultStore).ExecuteCommandAsync();
                _logger.LogInformation(
                    "已创建默认店铺: {StoreName} (GUID: {StoreGUID})",
                    defaultStore.StoreName,
                    defaultStore.StoreGUID
                );
            }
            else
            {
                _logger.LogInformation("默认店铺已存在，跳过创建");
            }
        }

        /// <summary>
        /// 为admin用户分配默认店铺
        /// </summary>
        private async Task AssignDefaultStoreToAdminAsync()
        {
            var db = _dbContext.Db;

            // 获取admin用户
            var adminUser = await db.Queryable<User>().FirstAsync(u => u.Username == "admin");

            if (adminUser == null)
            {
                _logger.LogError("admin用户不存在，无法分配店铺");
                return;
            }

            // 获取默认店铺
            var defaultStore = await db.Queryable<Store>()
                .FirstAsync(s => s.StoreGUID == "default" || s.StoreCode == "DEFAULT");

            if (defaultStore == null)
            {
                _logger.LogError("默认店铺不存在，无法分配给admin用户");
                return;
            }

            // 检查是否已存在用户店铺关联
            var existingUserStore = await db.Queryable<UserStore>()
                .FirstAsync(us =>
                    us.UserGUID == adminUser.UserGUID && us.StoreGUID == defaultStore.StoreGUID
                );

            if (existingUserStore == null)
            {
                var userStore = new UserStore
                {
                    UserStoreGUID = Guid.NewGuid().ToString(),
                    UserGUID = adminUser.UserGUID,
                    StoreGUID = defaultStore.StoreGUID,
                    CreatedAt = DateTime.UtcNow,
                };

                await db.Insertable(userStore).ExecuteCommandAsync();
                _logger.LogInformation(
                    "✅ 已为admin用户分配默认店铺: {StoreName}",
                    defaultStore.StoreName
                );
            }
            else
            {
                _logger.LogInformation("admin用户已关联默认店铺，跳过分配");
            }
        }

        /// <summary>
        /// 验证管理员角色分配是否正确
        /// </summary>
        private async Task VerifyAdminRoleAssignmentAsync()
        {
            var db = _dbContext.Db;

            try
            {
                // 获取admin用户及其角色
                var adminUser = await db.Queryable<User>()
                    .Includes(u => u.Roles)
                    .FirstAsync(u => u.Username == "admin");

                if (adminUser == null)
                {
                    _logger.LogError("验证失败：admin用户不存在");
                    return;
                }

                // 检查是否拥有Admin角色
                var hasAdminRole = adminUser.Roles?.Any(r => r.RoleName == "Admin") ?? false;

                if (hasAdminRole)
                {
                    _logger.LogInformation("✅ 验证成功：admin用户拥有Admin角色");
                }
                else
                {
                    _logger.LogWarning("⚠️ 验证警告：admin用户未拥有Admin角色，正在重新分配...");

                    // 重新分配Admin角色
                    var adminRole = await db.Queryable<Role>()
                        .FirstAsync(r => r.RoleName == "Admin");

                    if (adminRole != null)
                    {
                        var userRole = new UserRole
                        {
                            UserRoleGUID = Guid.NewGuid().ToString(),
                            UserGUID = adminUser.UserGUID,
                            RoleGUID = adminRole.RoleGUID,
                            AssignedAt = DateTime.UtcNow,
                            CreatedAt = DateTime.UtcNow,
                        };

                        await db.Insertable(userRole).ExecuteCommandAsync();
                        _logger.LogInformation("✅ Admin角色重新分配成功");
                    }
                }

                // 记录用户的所有角色
                var roleNames =
                    adminUser.Roles?.Select(r => r.RoleName).ToList() ?? new List<string>();
                _logger.LogInformation($"admin用户当前角色：{string.Join(", ", roleNames)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证管理员角色分配时发生错误");
            }
        }

        /// <summary>
        /// 初始化权限种子数据
        /// </summary>
        public async Task InitializePermissionSeedsAsync()
        {
            var db = _dbContext.Db;

            // 1. 同步规范权限定义，并清理已知废弃/重复权限码
            var allPermissions = PermissionSeedData.AllPermissions.ToList();
            var deprecatedPermissionCodes = PermissionSeedData.DeprecatedPermissionCodes;
            var existingPermissions = await db.Queryable<SysPermission>().ToListAsync();
            var existingPermissionsByCode = existingPermissions
                .GroupBy(permission => permission.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderBy(permission => permission.IsDeleted)
                        .ThenBy(permission => permission.CreatedAt)
                        .ThenBy(permission => permission.Id, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase
                );

            var newPermissions = new List<SysPermission>();
            var updatedPermissionsById = new Dictionary<string, SysPermission>(StringComparer.OrdinalIgnoreCase);
            var duplicatePermissionIdsToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;
            foreach (var seed in allPermissions)
            {
                if (
                    !existingPermissionsByCode.TryGetValue(seed.Code, out var matchingPermissions)
                    || matchingPermissions.Count == 0
                )
                {
                    newPermissions.Add(
                        new SysPermission
                        {
                            Id = UuidHelper.GenerateUuid7(),
                            Code = seed.Code,
                            Name = seed.Name,
                            Category = seed.Category,
                            Description = seed.Description,
                            IsDeleted = false,
                            CreatedAt = now,
                            CreatedBy = "System",
                            UpdatedAt = now,
                            UpdatedBy = "System",
                        }
                    );
                    continue;
                }

                var existingPermission = matchingPermissions[0];
                var shouldUpdate =
                    existingPermission.Name != seed.Name
                    || existingPermission.Category != seed.Category
                    || existingPermission.Description != seed.Description
                    || existingPermission.IsDeleted;

                existingPermission.Name = seed.Name;
                existingPermission.Category = seed.Category;
                existingPermission.Description = seed.Description;
                existingPermission.IsDeleted = false;

                if (shouldUpdate)
                {
                    MarkPermissionUpdated(existingPermission, now, updatedPermissionsById);
                }

                foreach (var duplicatePermission in matchingPermissions.Skip(1))
                {
                    if (duplicatePermission.IsDeleted)
                    {
                        duplicatePermissionIdsToDelete.Add(duplicatePermission.Id);
                        continue;
                    }

                    duplicatePermission.IsDeleted = true;
                    MarkPermissionUpdated(duplicatePermission, now, updatedPermissionsById);
                }
            }

            foreach (var deprecatedCode in deprecatedPermissionCodes)
            {
                if (
                    !existingPermissionsByCode.TryGetValue(deprecatedCode, out var matchingPermissions)
                    || matchingPermissions.Count == 0
                )
                {
                    continue;
                }

                foreach (var permission in matchingPermissions.Where(permission => !permission.IsDeleted))
                {
                    permission.IsDeleted = true;
                    MarkPermissionUpdated(permission, now, updatedPermissionsById);
                }
            }

            if (duplicatePermissionIdsToDelete.Any())
            {
                var duplicateIds = duplicatePermissionIdsToDelete.ToList();
                var removedDuplicates = await db.Deleteable<SysPermission>()
                    .Where(permission => duplicateIds.Contains(permission.Id))
                    .ExecuteCommandAsync();
                _logger.LogInformation("已物理删除 {Count} 条软删除的重复权限定义", removedDuplicates);
            }

            if (newPermissions.Any())
            {
                await db.Insertable(newPermissions).ExecuteCommandAsync();
                _logger.LogInformation($"已新增 {newPermissions.Count} 个权限定义");
            }

            var updatedPermissions = updatedPermissionsById.Values.ToList();
            if (updatedPermissions.Any())
            {
                await db.Updateable(updatedPermissions)
                    .UpdateColumns(permission => new
                    {
                        permission.Name,
                        permission.Category,
                        permission.Description,
                        permission.IsDeleted,
                        permission.UpdatedAt,
                        permission.UpdatedBy,
                    })
                    .ExecuteCommandAsync();
                _logger.LogInformation($"已更新 {updatedPermissions.Count} 个权限定义");
            }

            // 旧折扣权限先展开到同范围六项，再清理旧关联；重复启动不会产生重复行。
            await MigrateDeprecatedPosDiscountPermissionsAsync(db, now);

            // 2. 清理 Admin / 管理员 的显式权限关联
            var adminRoleNames = AdminRoleNames.ToArray();
            var adminRoles = await db.Queryable<Role>()
                .Where(role => adminRoleNames.Contains(role.RoleName))
                .ToListAsync();
            var adminRoleGuids = adminRoles
                .Select(role => role.RoleGUID)
                .Where(roleGuid => !string.IsNullOrWhiteSpace(roleGuid))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (adminRoleGuids.Any())
            {
                var adminRoleGuidList = adminRoleGuids.ToList();
                var removedAdminLinks = await db.Deleteable<SysRolePermission>()
                    .Where(permission => adminRoleGuidList.Contains(permission.RoleGuid))
                    .ExecuteCommandAsync();

                if (removedAdminLinks > 0)
                {
                    _logger.LogInformation(
                        "已清理 Admin/管理员 角色的 {Count} 条显式权限关联",
                        removedAdminLinks
                    );
                }
            }

            // 3. 为普通角色按模板补齐缺失权限，不覆盖额外权限
            await SeedRolePermissionTemplatesAsync();

            // 4. 初始化季节卡目录种子
            await SeedSeasonalCardCatalogAsync();
        }

        private static void AddRoleIfMissing(
            IEnumerable<Role> existingRoles,
            ICollection<Role> rolesToCreate,
            string roleName,
            string description
        )
        {
            if (existingRoles.Any(role => role.RoleName == roleName))
            {
                return;
            }

            rolesToCreate.Add(
                new Role
                {
                    RoleGUID = Guid.NewGuid().ToString(),
                    RoleName = roleName,
                    Description = description,
                    IsActive = true,
                    Level = Permissions.RoleLevels.ForRoleName(roleName),
                    CreatedAt = DateTime.UtcNow,
                }
            );
        }

        private static void MarkPermissionUpdated(
            SysPermission permission,
            DateTime updatedAt,
            IDictionary<string, SysPermission> updatedPermissionsById
        )
        {
            permission.UpdatedAt = updatedAt;
            permission.UpdatedBy = "System";
            updatedPermissionsById[permission.Id] = permission;
        }

        private async Task MigrateDeprecatedPosDiscountPermissionsAsync(
            ISqlSugarClient db,
            DateTime now
        )
        {
            var migrations = new[]
            {
                new
                {
                    OldCode = Permissions.PosTerminal.Sales.LineDiscount,
                    NewCodes = PermissionSeedData.PosTerminalLineDiscountPermissionCodes,
                },
                new
                {
                    OldCode = Permissions.PosTerminal.Sales.OrderDiscount,
                    NewCodes = PermissionSeedData.PosTerminalOrderDiscountPermissionCodes,
                },
            };

            foreach (var migration in migrations)
            {
                var oldRoleLinks = await db.Queryable<SysRolePermission>()
                    .Where(item =>
                        item.PermissionCode == migration.OldCode && !item.IsDeleted
                    )
                    .ToListAsync();
                var oldUserLinks = await db.Queryable<SysUserPermission>()
                    .Where(item =>
                        item.PermissionCode == migration.OldCode && !item.IsDeleted
                    )
                    .ToListAsync();

                var roleGuids = oldRoleLinks
                    .Select(item => item.RoleGuid)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var userGuids = oldUserLinks
                    .Select(item => item.UserGuid)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var newCodeList = migration.NewCodes.ToList();
                if (roleGuids.Count > 0)
                {
                    // 唯一索引包含软删除行，迁移前先清掉同键墓碑，避免恢复授权时撞唯一键。
                    await db.Deleteable<SysRolePermission>()
                        .Where(item =>
                            roleGuids.Contains(item.RoleGuid)
                            && newCodeList.Contains(item.PermissionCode)
                            && item.IsDeleted
                        )
                        .ExecuteCommandAsync();
                }

                if (userGuids.Count > 0)
                {
                    await db.Deleteable<SysUserPermission>()
                        .Where(item =>
                            userGuids.Contains(item.UserGuid)
                            && newCodeList.Contains(item.PermissionCode)
                            && item.IsDeleted
                        )
                        .ExecuteCommandAsync();
                }

                var existingRoleLinks = roleGuids.Count == 0
                    ? new List<SysRolePermission>()
                    : await db.Queryable<SysRolePermission>()
                        .Where(item => roleGuids.Contains(item.RoleGuid) && !item.IsDeleted)
                        .ToListAsync();
                var existingUserLinks = userGuids.Count == 0
                    ? new List<SysUserPermission>()
                    : await db.Queryable<SysUserPermission>()
                        .Where(item => userGuids.Contains(item.UserGuid) && !item.IsDeleted)
                        .ToListAsync();

                var newRoleLinks = roleGuids
                    .SelectMany(roleGuid => migration.NewCodes.Select(code => (roleGuid, code)))
                    .Where(entry => !existingRoleLinks.Any(item =>
                        item.RoleGuid.Equals(entry.roleGuid, StringComparison.OrdinalIgnoreCase)
                        && item.PermissionCode.Equals(entry.code, StringComparison.OrdinalIgnoreCase)))
                    .Select(entry => new SysRolePermission
                    {
                        Id = UuidHelper.GenerateUuid7(),
                        RoleGuid = entry.roleGuid,
                        PermissionCode = entry.code,
                        CreatedAt = now,
                        CreatedBy = "System",
                        UpdatedAt = now,
                        UpdatedBy = "System",
                    })
                    .ToList();
                var newUserLinks = userGuids
                    .SelectMany(userGuid => migration.NewCodes.Select(code => (userGuid, code)))
                    .Where(entry => !existingUserLinks.Any(item =>
                        item.UserGuid.Equals(entry.userGuid, StringComparison.OrdinalIgnoreCase)
                        && item.PermissionCode.Equals(entry.code, StringComparison.OrdinalIgnoreCase)))
                    .Select(entry => new SysUserPermission
                    {
                        Id = UuidHelper.GenerateUuid7(),
                        UserGuid = entry.userGuid,
                        PermissionCode = entry.code,
                        CreatedAt = now,
                        CreatedBy = "System",
                        UpdatedAt = now,
                        UpdatedBy = "System",
                    })
                    .ToList();

                if (newRoleLinks.Count > 0)
                {
                    await db.Insertable(newRoleLinks).ExecuteCommandAsync();
                }

                if (newUserLinks.Count > 0)
                {
                    await db.Insertable(newUserLinks).ExecuteCommandAsync();
                }

                await db.Deleteable<SysRolePermission>()
                    .Where(item => item.PermissionCode == migration.OldCode)
                    .ExecuteCommandAsync();
                await db.Deleteable<SysUserPermission>()
                    .Where(item => item.PermissionCode == migration.OldCode)
                    .ExecuteCommandAsync();
            }
        }

        private async Task SeedRolePermissionTemplatesAsync()
        {
            var db = _dbContext.Db;
            var adminRoleNames = AdminRoleNames.ToArray();
            var roleTemplates = PermissionSeedData.RolePermissionTemplates
                .Where(template => !adminRoleNames.Contains(template.RoleName))
                .ToList();

            if (!roleTemplates.Any())
            {
                return;
            }

            var roleNames = roleTemplates.Select(template => template.RoleName).ToList();
            var roles = await db.Queryable<Role>().Where(role => roleNames.Contains(role.RoleName)).ToListAsync();
            if (!roles.Any())
            {
                return;
            }

            var roleByName = roles.ToDictionary(role => role.RoleName, StringComparer.OrdinalIgnoreCase);
            var roleGuids = roles.Select(role => role.RoleGUID).ToList();
            var existingRolePermissions = await db.Queryable<SysRolePermission>()
                .Where(permission => roleGuids.Contains(permission.RoleGuid) && !permission.IsDeleted)
                .ToListAsync();
            var orderRoleGuids = roles
                .Where(role => OrderRoleNames.Contains(role.RoleName, StringComparer.OrdinalIgnoreCase))
                .Select(role => role.RoleGUID)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var normalizedOrderRoleRevokedPermissionCodes = OrderRoleTemplateRevokedPermissionCodes
                .Select(code => code.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 订货员入口模板改版后的历史权限必须按角色和权限码直接删除，不能依赖历史关联主键完整性。
            var removedOrderRoleTemplateLinks = await db.Deleteable<SysRolePermission>()
                .Where(permission =>
                    orderRoleGuids.Contains(permission.RoleGuid)
                    && !permission.IsDeleted
                    && normalizedOrderRoleRevokedPermissionCodes.Contains(
                        permission.PermissionCode.ToLower()
                    )
                )
                .ExecuteCommandAsync();
            if (removedOrderRoleTemplateLinks > 0)
            {
                existingRolePermissions = existingRolePermissions
                    .Where(permission =>
                        !orderRoleGuids.Contains(permission.RoleGuid)
                        || !normalizedOrderRoleRevokedPermissionCodes.Contains(
                            permission.PermissionCode.ToLower()
                        )
                    )
                    .ToList();
                _logger.LogInformation(
                    "已清理 Order/订货员 角色的 {Count} 条历史入口模板权限关联",
                    removedOrderRoleTemplateLinks
                );
            }

            var staleOrderAttendancePermissionIds = existingRolePermissions
                .Where(permission =>
                    orderRoleGuids.Contains(permission.RoleGuid)
                    && IsAttendancePermissionCode(permission.PermissionCode)
                )
                .Select(permission => permission.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (staleOrderAttendancePermissionIds.Any())
            {
                // Order/订货员只用于前台订货，历史种子带入的考勤权限继续按关联主键清理。
                var removedOrderAttendanceLinks = await db.Deleteable<SysRolePermission>()
                    .Where(permission => staleOrderAttendancePermissionIds.Contains(permission.Id))
                    .ExecuteCommandAsync();
                existingRolePermissions = existingRolePermissions
                    .Where(permission => !staleOrderAttendancePermissionIds.Contains(permission.Id))
                    .ToList();
                _logger.LogInformation(
                    "已清理 Order/订货员 角色的 {Count} 条历史考勤权限关联",
                    removedOrderAttendanceLinks
                );
            }

            var strictRoleTemplatePermissionsByRoleGuid = roleTemplates
                .Where(template => template.RoleName.Equals("WarehouseStaff", StringComparison.OrdinalIgnoreCase))
                .Where(template => roleByName.ContainsKey(template.RoleName))
                .ToDictionary(
                    template => roleByName[template.RoleName].RoleGUID,
                    template => template.PermissionCodes.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase
                );
            var staleStrictRolePermissionIds = existingRolePermissions
                .Where(permission =>
                    strictRoleTemplatePermissionsByRoleGuid.TryGetValue(permission.RoleGuid, out var allowedCodes)
                    && !allowedCodes.Contains(permission.PermissionCode)
                )
                .Select(permission => permission.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (staleStrictRolePermissionIds.Any())
            {
                var removedStrictRoleLinks = await db.Deleteable<SysRolePermission>()
                    .Where(permission => staleStrictRolePermissionIds.Contains(permission.Id))
                    .ExecuteCommandAsync();
                existingRolePermissions = existingRolePermissions
                    .Where(permission => !staleStrictRolePermissionIds.Contains(permission.Id))
                    .ToList();
                _logger.LogInformation(
                    "已收敛 WarehouseStaff 角色的 {Count} 条非模板权限关联",
                    removedStrictRoleLinks
                );
            }

            var currentPermissionCodesByRole = existingRolePermissions
                .GroupBy(permission => permission.RoleGuid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(permission => permission.PermissionCode)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase
                );

            var newRolePermissions = new List<SysRolePermission>();
            foreach (var template in roleTemplates)
            {
                if (!roleByName.TryGetValue(template.RoleName, out var role))
                {
                    continue;
                }

                if (!currentPermissionCodesByRole.TryGetValue(role.RoleGUID, out var currentPermissionCodes))
                {
                    currentPermissionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    currentPermissionCodesByRole[role.RoleGUID] = currentPermissionCodes;
                }

                foreach (var permissionCode in template.PermissionCodes.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!currentPermissionCodes.Add(permissionCode))
                    {
                        continue;
                    }

                    newRolePermissions.Add(
                        new SysRolePermission
                        {
                            Id = UuidHelper.GenerateUuid7(),
                            RoleGuid = role.RoleGUID,
                            PermissionCode = permissionCode,
                        }
                    );
                }
            }

            if (newRolePermissions.Any())
            {
                await db.Insertable(newRolePermissions).ExecuteCommandAsync();
                _logger.LogInformation("已补齐 {Count} 条普通角色模板权限", newRolePermissions.Count);
            }
        }

        private static bool IsAttendancePermissionCode(string? permissionCode)
        {
            return !string.IsNullOrWhiteSpace(permissionCode)
                && permissionCode.StartsWith("Attendance.", StringComparison.OrdinalIgnoreCase);
        }

        private async Task SeedSeasonalCardCatalogAsync()
        {
            var db = _dbContext.Db;
            var now = DateTime.UtcNow;
            var definitions = SeasonalCardCatalogSeedData.Catalogs.ToList();
            var existing = await db.Queryable<SeasonalCardCatalog>().ToListAsync();
            var existingByCode = existing.ToDictionary(
                item => item.CatalogCode,
                item => item,
                StringComparer.OrdinalIgnoreCase
            );

            var inserts = new List<SeasonalCardCatalog>();
            var updates = new List<SeasonalCardCatalog>();

            foreach (var definition in definitions)
            {
                if (!existingByCode.TryGetValue(definition.CatalogCode, out var model))
                {
                    inserts.Add(
                        new SeasonalCardCatalog
                        {
                            CatalogGuid = Guid.NewGuid().ToString(),
                            CatalogCode = definition.CatalogCode,
                            CardType = definition.CardType,
                            PriceOption = definition.PriceOption,
                            PriceLabel = definition.PriceLabel,
                            FixedUnitPrice = definition.FixedUnitPrice,
                            AllowsCustomUnitPrice = definition.AllowsCustomUnitPrice,
                            IsEnabled = true,
                            SortOrder = definition.SortOrder,
                            CreatedAt = now,
                            CreatedBy = "System",
                            UpdatedAt = now,
                            UpdatedBy = "System",
                        }
                    );
                    continue;
                }

                var shouldUpdate =
                    model.CardType != definition.CardType
                    || model.PriceOption != definition.PriceOption
                    || model.PriceLabel != definition.PriceLabel
                    || model.FixedUnitPrice != definition.FixedUnitPrice
                    || model.AllowsCustomUnitPrice != definition.AllowsCustomUnitPrice
                    || !model.IsEnabled
                    || model.SortOrder != definition.SortOrder
                    || model.IsDeleted;

                model.CardType = definition.CardType;
                model.PriceOption = definition.PriceOption;
                model.PriceLabel = definition.PriceLabel;
                model.FixedUnitPrice = definition.FixedUnitPrice;
                model.AllowsCustomUnitPrice = definition.AllowsCustomUnitPrice;
                model.IsEnabled = true;
                model.SortOrder = definition.SortOrder;
                model.IsDeleted = false;

                if (!shouldUpdate)
                {
                    continue;
                }

                model.UpdatedAt = now;
                model.UpdatedBy = "System";
                updates.Add(model);
            }

            if (inserts.Any())
            {
                await db.Insertable(inserts).ExecuteCommandAsync();
                _logger.LogInformation("已新增 {Count} 条季节卡目录种子", inserts.Count);
            }

            if (updates.Any())
            {
                await db.Updateable(updates)
                    .UpdateColumns(item => new
                    {
                        item.CardType,
                        item.PriceOption,
                        item.PriceLabel,
                        item.FixedUnitPrice,
                        item.AllowsCustomUnitPrice,
                        item.IsEnabled,
                        item.SortOrder,
                        item.IsDeleted,
                        item.UpdatedAt,
                        item.UpdatedBy,
                    })
                    .ExecuteCommandAsync();
                _logger.LogInformation("已更新 {Count} 条季节卡目录种子", updates.Count);
            }
        }
    }
}
