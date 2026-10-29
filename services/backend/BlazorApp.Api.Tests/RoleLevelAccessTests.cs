using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 角色等级：等级低的账号不能修改等级不低于自己的账号，也不能授予不低于自己等级的角色。
    /// 角色名基准等级：Admin 100 / WarehouseManager 80 / StoreManager 70 / Manager 60 / 员工 30 / User 10；
    /// Level 为 0 视为未设置并回退到按角色名的基准，自定义角色在库里写明 Level。
    /// </summary>
    public sealed class RoleLevelAccessTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;

        public RoleLevelAccessTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
            _sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
            _sqliteConnection.Open();
            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _sqliteConnection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            });
            _db.CodeFirst.InitTables<User, Store, UserStore, Role, UserRole>();
            _db.CodeFirst.InitTables<UserLoginRestriction>();
            _db.CodeFirst.InitTables<SysPermission, SysRolePermission, SysUserPermission>();
            _db.CodeFirst.InitTables<SysUserStorePosPermission>();
            _db.CodeFirst.InitTables<RefreshToken>();
        }

        public void Dispose()
        {
            _db.Dispose();
            _sqliteConnection.Dispose();
            SqliteConnection.ClearAllPools();
            File.Delete(_dbPath);
        }

        [Theory]
        [InlineData("Admin", 0, 100)] // 超级管理员角色等级至少按 100 算，即便被误调低
        [InlineData("Admin", 120, 120)]
        [InlineData("WarehouseManager", 0, 80)] // Level 为 0 回退到按角色名的基准
        [InlineData("StoreManager", 0, 70)]
        [InlineData("Manager", 0, 60)]
        [InlineData("StoreStaff", 0, 30)]
        [InlineData("User", 0, 10)]
        [InlineData("Auditor", 0, 0)] // 未登记的自定义角色未设置等级时为 0
        [InlineData("Auditor", 90, 90)]
        [InlineData("StoreStaff", 45, 45)] // 管理员显式设置的等级优先于基准
        public void EffectiveRoleLevel_FallsBackToNameBaselineAndPinsSuperAdmin(
            string roleName,
            int level,
            int expected
        )
        {
            Assert.Equal(expected, UserAccessMutationSecurity.EffectiveRoleLevel(roleName, level));
        }

        [Fact]
        public async Task GetUserLevelsAsync_TakesHighestEffectiveLevelAndIgnoresInactiveOrDeletedRoles()
        {
            await SeedAsync();

            var levels = await UserAccessMutationSecurity.GetUserLevelsAsync(
                _db,
                new[] { "whm-1", "multi-1", "ghost-1", "norole-1", "deleted-link-1", "auditor-1" }
            );

            Assert.Equal(80, levels["whm-1"]);
            Assert.Equal(40, levels["multi-1"]); // StoreStaff(30) 与 Clerk(40) 取最高
            Assert.Equal(90, levels["auditor-1"]);
            Assert.False(levels.ContainsKey("ghost-1")); // 只有停用角色 → 等级 0
            Assert.False(levels.ContainsKey("norole-1"));
            Assert.False(levels.ContainsKey("deleted-link-1")); // 角色关联已软删除
        }

        [Fact]
        public async Task ValidateTargetLevelAsync_RequiresStrictlyHigherLevelAndAllowsSelf()
        {
            await SeedAsync();

            Assert.True((await Validate("sm-1", "staff-1")).IsAllowed);
            Assert.True((await Validate("hr-1", "clerk-1")).IsAllowed);
            Assert.True((await Validate("hr-1", "norole-1")).IsAllowed);
            Assert.True((await Validate("sm-1", "sm-1")).IsAllowed); // 本人不在此判断

            var lower = await Validate("staff-1", "sm-1");
            Assert.False(lower.IsAllowed);
            Assert.Equal("TARGET_LEVEL_DENIED", lower.ErrorCode);

            var equal = await Validate("hr-1", "hr-2"); // 同级不可互改
            Assert.False(equal.IsAllowed);
            Assert.Equal("TARGET_LEVEL_DENIED", equal.ErrorCode);

            Assert.False((await Validate("norole-1", "staff-1")).IsAllowed); // 无角色账号等级 0
            Assert.False((await Validate("", "staff-1")).IsAllowed);
        }

        [Fact]
        public async Task UpdateUserByGuidAsync_NonAdminGlobalEditor_OnlyEditsStrictlyLowerLevelTargets()
        {
            await SeedAsync();
            var service = CreateUserService(Scope("hr-1")); // 持有全局 Users.Edit 的自定义角色，等级 50

            foreach (var higherOrEqual in new[] { "sm-1", "hr-2", "whm-1", "auditor-1" })
            {
                var denied = await service.UpdateUserByGuidAsync(higherOrEqual, EditDto(higherOrEqual));
                Assert.False(denied.Success);
                Assert.Equal("TARGET_LEVEL_DENIED", denied.ErrorCode);
                Assert.Equal(
                    $"{higherOrEqual}@example.com",
                    (await _db.Queryable<User>().InSingleAsync(higherOrEqual))!.Email
                );
            }

            var allowed = await service.UpdateUserByGuidAsync("clerk-1", EditDto("clerk-1"));
            Assert.True(allowed.Success);
            Assert.Equal(
                "clerk-1-edited@example.com",
                (await _db.Queryable<User>().InSingleAsync("clerk-1"))!.Email
            );
        }

        [Fact]
        public async Task UpdateUserByGuidAsync_WarehouseManager_CanEditStoreManagerButNotAdminOrPeer()
        {
            await SeedAsync();
            var service = CreateUserService(Scope("whm-1")); // 仓库经理 80，高于店长 70

            var storeManager = await service.UpdateUserByGuidAsync("sm-1", EditDto("sm-1"));
            Assert.True(storeManager.Success);

            foreach (var blocked in new[] { "admin-1", "whm-2" }) // 管理员 100、同级仓库经理 80
            {
                var denied = await service.UpdateUserByGuidAsync(blocked, EditDto(blocked));
                Assert.False(denied.Success);
                Assert.Equal("TARGET_LEVEL_DENIED", denied.ErrorCode);
            }
        }

        [Fact]
        public async Task UpdateUserPasswordByGuidAsync_NonAdminGlobalEditor_CannotResetHigherLevelPassword()
        {
            await SeedAsync();
            var service = CreateUserService(Scope("hr-1"));
            var before = (await _db.Queryable<User>().InSingleAsync("sm-1"))!.PasswordHash;

            var result = await service.UpdateUserPasswordByGuidAsync(
                "sm-1",
                new UpdateUserPasswordDto { NewPassword = "new-password-123", PasswordFormat = "raw" }
            );

            Assert.False(result.Success);
            Assert.Equal("TARGET_LEVEL_DENIED", result.ErrorCode);
            Assert.Equal(before, (await _db.Queryable<User>().InSingleAsync("sm-1"))!.PasswordHash);
        }

        [Fact]
        public async Task UpdateUserByGuidAsync_StoreManager_CannotEditHigherLevelCustomRoleTarget()
        {
            await SeedAsync();
            var service = CreateUserService(Scope("sm-1", storeGuids: new[] { "store-1" }));

            // auditor-1 在同一分店、不属于高权限角色，但自定义角色等级 90 高于店长 70。
            var denied = await service.UpdateUserByGuidAsync("auditor-1", EditDto("auditor-1"));
            Assert.False(denied.Success);
            Assert.Equal("TARGET_LEVEL_DENIED", denied.ErrorCode);

            var allowed = await service.UpdateUserByGuidAsync("staff-1", EditDto("staff-1"));
            Assert.True(allowed.Success);
        }

        [Fact]
        public async Task UpdateUserByGuidAsync_Admin_IsNotRestrictedByLevel()
        {
            await SeedAsync();
            var service = CreateUserService(Scope("admin-1", isAdmin: true));

            var result = await service.UpdateUserByGuidAsync("whm-1", EditDto("whm-1"));

            Assert.True(result.Success);
        }

        [Fact]
        public async Task AssignRolesToUserAsync_StoreManager_CannotGrantRoleAtOrAboveOwnLevel()
        {
            await SeedAsync();
            // 员工角色别名「Employee」被管理员调到 90，高于店长 70：即使是员工角色也不能由店长授予。
            await _db.Insertable(new Role
            {
                RoleGUID = "role-employee-high",
                RoleName = "Employee",
                IsActive = true,
                Level = 90,
            }).ExecuteCommandAsync();
            var service = CreateUserService(Scope("sm-1", storeGuids: new[] { "store-1" }));

            var result = await service.AssignRolesToUserAsync(
                "staff-1",
                new UserRoleAssignmentDto
                {
                    RoleGuids = new List<string> { "role-staff", "role-employee-high" },
                }
            );

            Assert.False(result.Success);
            Assert.Equal("ROLE_LEVEL_DENIED", result.ErrorCode);
            Assert.False(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "staff-1" && item.RoleGUID == "role-employee-high"
            ));
        }

        [Theory]
        // 店长(70)：只见更低的 HrOps(50) / Clerk(40) / StoreStaff(30)；同级 StoreManager、更高的 Auditor(90) / 仓库经理 / 管理员、停用的 OldBoss 都不可见
        [InlineData("sm-1", new[] { "Clerk", "HrOps", "StoreStaff" })]
        // 仓库经理(80)：多看到店长(70)；同级仓库经理与 Auditor(90) 不可见
        [InlineData("whm-1", new[] { "Clerk", "HrOps", "StoreManager", "StoreStaff" })]
        // 员工(30)、无任何有效角色（等级 0）、只有停用角色的账号：没有更低的角色可选
        [InlineData("staff-1", new string[0])]
        [InlineData("norole-1", new string[0])]
        [InlineData("ghost-1", new string[0])]
        public async Task GetActiveRolesBelowUserLevelAsync_ListsOnlyActiveRolesStrictlyBelowActor(
            string actorGuid,
            string[] expectedRoleNames
        )
        {
            await SeedAsync();
            var service = new RoleService(
                CreateSqlSugarContext(_db),
                NullLogger<RoleService>.Instance,
                new Microsoft.AspNetCore.Http.HttpContextAccessor()
            );

            var result = await service.GetActiveRolesBelowUserLevelAsync(actorGuid);

            Assert.True(result.Success);
            Assert.Equal(
                expectedRoleNames,
                result.Data!.Select(role => role.RoleName).OrderBy(name => name, StringComparer.Ordinal).ToArray()
            );
        }

        private Task<UserAccessMutationDecision> Validate(string actor, string target) =>
            UserAccessMutationSecurity.ValidateTargetLevelAsync(_db, actor, target);

        private static UpdateUserDto EditDto(string userGuid) => new()
        {
            Username = userGuid,
            Email = $"{userGuid}-edited@example.com",
            IsActive = true,
        };

        private static FakeScope Scope(
            string userGuid,
            bool isAdmin = false,
            string[]? storeGuids = null
        ) => new(new CurrentUserManageableStoreScope
        {
            IsAllowed = true,
            IsAuthenticated = true,
            IsAdmin = isAdmin,
            UserGuid = userGuid,
            StoreGuids = storeGuids ?? Array.Empty<string>(),
            IsStoreManager = storeGuids is { Length: > 0 },
        });

        private UserService CreateUserService(ICurrentUserManageableStoreScopeService scope) =>
            new(CreateSqlSugarContext(_db), NullLogger<UserService>.Instance, scope);

        private async Task SeedAsync()
        {
            var userGuids = new[]
            {
                "admin-1", "whm-1", "whm-2", "sm-1", "hr-1", "hr-2", "staff-1", "auditor-1", "clerk-1",
                "norole-1", "ghost-1", "multi-1", "deleted-link-1",
            };
            await _db.Insertable(userGuids.Select(guid => new User
            {
                UserGUID = guid,
                Username = guid,
                Email = $"{guid}@example.com",
                PasswordHash = "hashed",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            }).ToList()).ExecuteCommandAsync();

            await _db.Insertable(new[]
            {
                Role("role-admin", "Admin", 0),
                Role("role-whm", "WarehouseManager", 0),
                Role("role-sm", "StoreManager", 0),
                Role("role-hr", "HrOps", 50),
                Role("role-staff", "StoreStaff", 0),
                Role("role-auditor", "Auditor", 90),
                Role("role-clerk", "Clerk", 40),
                Role("role-old-boss", "OldBoss", 99, isActive: false),
            }).ExecuteCommandAsync();

            await _db.Insertable(new[]
            {
                Link("admin-1", "role-admin"),
                Link("whm-1", "role-whm"),
                Link("whm-2", "role-whm"),
                Link("sm-1", "role-sm"),
                Link("hr-1", "role-hr"),
                Link("hr-2", "role-hr"),
                Link("staff-1", "role-staff"),
                Link("auditor-1", "role-auditor"),
                Link("clerk-1", "role-clerk"),
                Link("ghost-1", "role-old-boss"),
                Link("multi-1", "role-staff"),
                Link("multi-1", "role-clerk"),
                Link("deleted-link-1", "role-auditor", isDeleted: true),
            }).ExecuteCommandAsync();

            await _db.Insertable(new Store
            {
                StoreGUID = "store-1",
                StoreCode = "S001",
                StoreName = "Store 1",
                IsActive = true,
            }).ExecuteCommandAsync();
            await _db.Insertable(
                new[] { "sm-1", "staff-1", "auditor-1", "clerk-1" }.Select(guid => new UserStore
                {
                    UserStoreGUID = Guid.NewGuid().ToString(),
                    UserGUID = guid,
                    StoreGUID = "store-1",
                    IsPrimary = guid == "sm-1",
                    AssignedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }).ToList()
            ).ExecuteCommandAsync();
        }

        private static Role Role(string guid, string name, int level, bool isActive = true) => new()
        {
            RoleGUID = guid,
            RoleName = name,
            Level = level,
            IsActive = isActive,
        };

        private static UserRole Link(string userGuid, string roleGuid, bool isDeleted = false) => new()
        {
            UserRoleGUID = Guid.NewGuid().ToString(),
            UserGUID = userGuid,
            RoleGUID = roleGuid,
            IsDeleted = isDeleted,
            AssignedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };

        private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
        {
            var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
                typeof(SqlSugarContext)
            );
            typeof(SqlSugarContext)
                .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(context, db);
            return context;
        }

        private sealed class FakeScope(CurrentUserManageableStoreScope scope)
            : ICurrentUserManageableStoreScopeService
        {
            public Task<CurrentUserManageableStoreScope> GetScopeAsync() => Task.FromResult(scope);

            public Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync() =>
                Task.FromResult(scope.StoreCodes);

            public Task<bool> CanAccessStoreCodeAsync(string storeCode) =>
                Task.FromResult(scope.CanAccessStoreCode(storeCode));

            public Task<bool> CanAccessOrderAsync(string orderGuid) => Task.FromResult(false);

            public Task<bool> CanManageStoreAsync(string storeGuid) =>
                Task.FromResult(scope.CanAccessStoreGuid(storeGuid));

            public Task<bool> CanManageUserAsync(string userGuid) => Task.FromResult(false);
        }
    }
}
