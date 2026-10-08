using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading.Tasks;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests
{
    public sealed class UserStoreManagementRelationTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;
        private readonly string _hqDbPath;
        private readonly SqliteConnection _hqSqliteConnection;
        private readonly SqlSugarClient _hqDb;

        public UserStoreManagementRelationTests()
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
            _db.CodeFirst.InitTables<SysPermission, SysRolePermission, SysUserPermission>();
            _db.CodeFirst.InitTables<SysUserStorePosPermission>();
            _db.CodeFirst.InitTables<RefreshToken>();

            _hqDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
            _hqSqliteConnection = new SqliteConnection($"Data Source={_hqDbPath}");
            _hqSqliteConnection.Open();

            _hqDb = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _hqSqliteConnection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            });
            _hqDb.CodeFirst.InitTables<HqBranch>();
            // 测试库按HQ业务键建唯一索引，覆盖重复同步时的数据库约束行为。
            _hqDb.Ado.ExecuteCommand(
                "CREATE UNIQUE INDEX IX_HqBranch_BranchCode ON \"DIC_分店信息表\" (\"H分店代码\")"
            );
        }

        [Theory]
        [InlineData(nameof(UsersController.AssignStoresToUser))]
        [InlineData(nameof(UsersController.RemoveStoreFromUser))]
        [InlineData(nameof(UsersController.BatchManageUserStores))]
        public void UserStoreWriteRoutes_RequireManageStoresPermission(string methodName)
        {
            // 分配 / 移除用户分店都必须持有 Users.ManageStores；服务层的角色与范围校验是第二道防线。
            var method = typeof(UsersController).GetMethod(methodName);

            Assert.Equal(
                Permissions.Users.ManageStores,
                method?.GetCustomAttribute<AuthorizeAttribute>()?.Policy
            );
        }

        [Fact]
        public void UsersController_HasSinglePublicConstructor_ForDependencyInjection()
        {
            var constructors = typeof(UsersController).GetConstructors();

            Assert.Single(constructors);
        }

        [Fact]
        public void UsersController_GetUserLoginRecords_UsesUsersViewPermission()
        {
            var method = typeof(UsersController).GetMethod(nameof(UsersController.GetUserLoginRecords));

            var authorize = method?.GetCustomAttribute<AuthorizeAttribute>();

            Assert.NotNull(authorize);
            Assert.Equal(Permissions.Users.View, authorize!.Policy);
        }

        [Fact]
        public async Task UpdateUserByGuidAsync_StoreManagerCannotEditCrossStoreTarget()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true,
                IsAuthenticated = true,
                UserGuid = "manager-1",
                StoreGuids = new[] { "store-1" },
            }));

            var result = await service.UpdateUserByGuidAsync("foreign-user", new UpdateUserDto
            {
                Username = "foreign-user-updated",
                Email = "foreign-user-updated@example.com",
                IsActive = true,
            });

            Assert.False(result.Success);
            Assert.Equal("USER_SCOPE_DENIED", result.ErrorCode);
            Assert.Equal("foreign-user", (await _db.Queryable<User>().InSingleAsync("foreign-user"))!.Username);
        }

        [Fact]
        public async Task UpdateUserPasswordByGuidAsync_StoreManagerCanEditSameStoreTarget()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true,
                IsAuthenticated = true,
                UserGuid = "manager-1",
                StoreGuids = new[] { "store-1" },
            }));

            var result = await service.UpdateUserPasswordByGuidAsync("scoped-user", new UpdateUserPasswordDto
            {
                NewPassword = "new-password-123",
                PasswordFormat = "raw",
            });

            Assert.True(result.Success);
        }

        [Fact]
        public async Task GetUserLoginRecordsAsync_StoreManagerCannotReadCrossStoreTarget()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true,
                IsAuthenticated = true,
                UserGuid = "manager-1",
                StoreGuids = new[] { "store-1" },
            }));

            var result = await service.GetUserLoginRecordsAsync("foreign-user", new UserLoginRecordQueryDto());

            Assert.False(result.Success);
            Assert.Equal("FORBIDDEN", result.ErrorCode);
        }

        [Fact]
        public async Task UpdateUserByGuidAsync_StoreManagerCannotEditHighPrivilegeTarget()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new Role { RoleGUID = "role-admin", RoleName = "Admin", IsActive = true })
                .ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("foreign-user", "role-admin")).ExecuteCommandAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));

            var result = await service.UpdateUserByGuidAsync("foreign-user", new UpdateUserDto
            {
                Username = "foreign-admin-edited", Email = "foreign-admin-edited@example.com", IsActive = true,
            });

            Assert.False(result.Success);
            Assert.Equal("HIGH_PRIVILEGE_TARGET_DENIED", result.ErrorCode);
            var target = await _db.Queryable<User>().InSingleAsync("foreign-user");
            Assert.Equal("foreign-user", target!.Username);
            Assert.Equal("foreign@example.com", target.Email);
        }

        [Fact]
        public async Task UpdateUserPasswordByGuidAsync_StoreManagerCannotEditHighPrivilegeTarget()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new Role { RoleGUID = "role-admin", RoleName = "Admin", IsActive = true })
                .ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("foreign-user", "role-admin")).ExecuteCommandAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));
            var before = (await _db.Queryable<User>().InSingleAsync("foreign-user"))!.PasswordHash;

            var result = await service.UpdateUserPasswordByGuidAsync("foreign-user", new UpdateUserPasswordDto
            {
                NewPassword = "new-password-123", PasswordFormat = "raw",
            });

            Assert.False(result.Success);
            Assert.Equal("HIGH_PRIVILEGE_TARGET_DENIED", result.ErrorCode);
            Assert.Equal(before, (await _db.Queryable<User>().InSingleAsync("foreign-user"))!.PasswordHash);
        }

        [Fact]
        public async Task UpdateUserByGuidAsync_GlobalNonAdminExplicitScopeCanEditTarget()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "viewer-1",
            }));

            var result = await service.UpdateUserByGuidAsync("foreign-user", new UpdateUserDto
            {
                Username = "foreign-user-edited", Email = "foreign-edited@example.com", IsActive = true,
            });

            Assert.True(result.Success);
            Assert.Equal("foreign-user-edited", (await _db.Queryable<User>().InSingleAsync("foreign-user"))!.Username);
        }

        [Fact]
        public async Task UpdateUserByGuidAsync_SelfAllowsProfileButRejectsUsernameAndStatus()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));

            var profile = await service.UpdateUserByGuidAsync("manager-1", new UpdateUserDto
            {
                Username = "manager-1", Email = "manager-updated@example.com", FullName = "店长本人", IsActive = true,
            });
            Assert.True(profile.Success);

            var username = await service.UpdateUserByGuidAsync("manager-1", new UpdateUserDto
            {
                Username = "manager-renamed", Email = "manager-updated@example.com", FullName = "店长本人", IsActive = true,
            });
            Assert.False(username.Success);
            Assert.Equal("SELF_PROFILE_FIELDS_DENIED", username.ErrorCode);

            var status = await service.UpdateUserByGuidAsync("manager-1", new UpdateUserDto
            {
                Username = "manager-1", Email = "manager-updated@example.com", FullName = "店长本人", IsActive = false,
            });
            Assert.False(status.Success);
            Assert.Equal("SELF_PROFILE_FIELDS_DENIED", status.ErrorCode);
        }

        [Fact]
        public async Task GetUserByGuidAsync_StoreManagerSeesOnlySharedStoresAndCannotSeePrivilegedTarget()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));

            var shared = await service.GetUserByGuidAsync("dual-user");
            Assert.True(shared.Success);
            Assert.Single(shared.Data!.Stores);
            Assert.Equal("store-1", shared.Data.Stores[0].StoreGUID);

            await _db.Insertable(new Role { RoleGUID = "role-admin", RoleName = "Admin", IsActive = true })
                .ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("foreign-user", "role-admin")).ExecuteCommandAsync();
            var privileged = await service.GetUserByGuidAsync("foreign-user");
            Assert.False(privileged.Success);
            Assert.Equal("FORBIDDEN", privileged.ErrorCode);
        }

        [Fact]
        public async Task GlobalNonAdminCanReadUserDetailsAndLoginRecords()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "viewer-1",
            }));

            var detail = await service.GetUserByGuidAsync("foreign-user");
            var records = await service.GetUserLoginRecordsAsync("foreign-user", new UserLoginRecordQueryDto());

            Assert.True(detail.Success);
            Assert.True(records.Success);
        }

        [Fact]
        public async Task ResetUserPasswordAsync_StoreManagerCannotResetHighPrivilegeTarget()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new Role { RoleGUID = "role-admin", RoleName = "Admin", IsActive = true })
                .ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("foreign-user", "role-admin")).ExecuteCommandAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));
            var before = (await _db.Queryable<User>().InSingleAsync("foreign-user"))!.PasswordHash;

            var result = await service.ResetUserPasswordAsync("foreign-user");

            Assert.False(result.Success);
            Assert.Equal("HIGH_PRIVILEGE_TARGET_DENIED", result.ErrorCode);
            Assert.Equal(before, (await _db.Queryable<User>().InSingleAsync("foreign-user"))!.PasswordHash);
        }

        [Fact]
        public async Task UpdateUserStatusByGuidAsync_StoreManagerCannotDisableCrossStoreTarget()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));

            var result = await service.UpdateUserStatusByGuidAsync("foreign-user", false);

            Assert.False(result.Success);
            Assert.Equal("USER_SCOPE_DENIED", result.ErrorCode);
            Assert.True((await _db.Queryable<User>().InSingleAsync("foreign-user"))!.IsActive);
        }

        [Fact]
        public async Task UpdateUserStatusByGuidAsync_StoreManagerCannotDisableSelfOrHighPrivilegeTarget()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new Role { RoleGUID = "role-admin", RoleName = "Admin", IsActive = true })
                .ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("scoped-user", "role-admin")).ExecuteCommandAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));

            var self = await service.UpdateUserStatusByGuidAsync("manager-1", false);
            var privileged = await service.UpdateUserStatusByGuidAsync("scoped-user", false);

            Assert.False(self.Success);
            Assert.Equal("SELF_PROFILE_FIELDS_DENIED", self.ErrorCode);
            Assert.False(privileged.Success);
            Assert.Equal("HIGH_PRIVILEGE_TARGET_DENIED", privileged.ErrorCode);
            Assert.True((await _db.Queryable<User>().InSingleAsync("manager-1"))!.IsActive);
            Assert.True((await _db.Queryable<User>().InSingleAsync("scoped-user"))!.IsActive);
        }

        [Theory]
        [InlineData("manager-1", "SELF_PROFILE_FIELDS_DENIED")]
        [InlineData("foreign-user", "USER_SCOPE_DENIED")]
        public async Task DeleteUserByGuidAsync_StoreManagerCannotDeleteForbiddenTarget(
            string targetUserGuid,
            string expectedErrorCode
        )
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));

            var result = await service.DeleteUserByGuidAsync(targetUserGuid);

            Assert.False(result.Success);
            Assert.Equal(expectedErrorCode, result.ErrorCode);
            Assert.NotNull(await _db.Queryable<User>().InSingleAsync(targetUserGuid));
        }

        [Fact]
        public async Task DeleteUserByGuidAsync_StoreManagerCannotDeleteHighPrivilegeTarget()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new Role { RoleGUID = "role-admin", RoleName = "Admin", IsActive = true })
                .ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("scoped-user", "role-admin")).ExecuteCommandAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));

            var result = await service.DeleteUserByGuidAsync("scoped-user");

            Assert.False(result.Success);
            Assert.Equal("HIGH_PRIVILEGE_TARGET_DENIED", result.ErrorCode);
            Assert.NotNull(await _db.Queryable<User>().InSingleAsync("scoped-user"));
        }

        [Fact]
        public async Task BatchManageUsersAsync_RejectedTargetRollsBackWholeStatusBatch()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));

            var result = await service.BatchManageUsersAsync(new BatchUserOperationDto
            {
                Operation = "deactivate",
                UserGuids = new List<string> { "scoped-user", "foreign-user" },
            });

            Assert.False(result.Success);
            Assert.Equal("USER_SCOPE_DENIED", result.ErrorCode);
            Assert.True((await _db.Queryable<User>().InSingleAsync("scoped-user"))!.IsActive);
            Assert.True((await _db.Queryable<User>().InSingleAsync("foreign-user"))!.IsActive);
        }

        [Fact]
        public async Task GetUserStatisticsAsync_StoreManagerUsesSameVisibleUserScopeAsList()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));

            var result = await service.GetUserStatisticsAsync();

            Assert.True(result.Success);
            Assert.Equal(3, result.Data!.TotalUsers);
            Assert.Equal(3, result.Data.ActiveUsers);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task UserLists_ExcludeSoftDeletedUsersAndRoleRelations(bool optimized)
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var deleted = await _db.Queryable<User>().InSingleAsync("foreign-user");
            deleted!.IsDeleted = true;
            await _db.Updateable(deleted).ExecuteCommandAsync();
            var deletedRole = CreateUserRole("scoped-user", "role-viewer");
            deletedRole.IsDeleted = true;
            await _db.Insertable(deletedRole).ExecuteCommandAsync();
            var service = CreateUserService();

            var all = await GetUsersAsync(service, optimized, new UserQueryDto { Page = 1, PageSize = 100 });
            var filtered = await GetUsersAsync(service, optimized, new UserQueryDto
            {
                Page = 1, PageSize = 100, RoleGuid = "role-viewer",
            });

            Assert.DoesNotContain(all.Data!.Items!, user => user.UserGUID == "foreign-user");
            Assert.DoesNotContain(filtered.Data!.Items!, user => user.UserGUID == "scoped-user");
        }

        [Fact]
        public async Task UserMutation_StaleStoreManagerClaimFailsClosedAfterRoleRemoval()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Deleteable<UserRole>()
                .Where(role => role.UserGUID == "manager-1")
                .ExecuteCommandAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true,
                IsAuthenticated = true,
                IsStoreManager = true,
                UserGuid = "manager-1",
                StoreGuids = new[] { "store-1" },
            }));

            var result = await service.UpdateUserStatusByGuidAsync("scoped-user", false);

            Assert.False(result.Success);
            Assert.Equal("ACCESS_DELEGATOR_DENIED", result.ErrorCode);
            Assert.True((await _db.Queryable<User>().InSingleAsync("scoped-user"))!.IsActive);
        }

        [Fact]
        public async Task UserMutation_StoreManagerCannotEditTargetsWithDirectOrRoleAdminPermission()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new SysUserPermission
            {
                Id = Guid.NewGuid().ToString(),
                UserGuid = "scoped-user",
                PermissionCode = Permissions.Users.View,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new Role
            {
                RoleGUID = "role-custom-admin",
                RoleName = "CustomOperator",
                IsActive = true,
            }).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("dual-user", "role-custom-admin")).ExecuteCommandAsync();
            await _db.Insertable(new SysRolePermission
            {
                Id = Guid.NewGuid().ToString(),
                RoleGuid = "role-custom-admin",
                PermissionCode = Permissions.Roles.View,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));

            var direct = await service.UpdateUserStatusByGuidAsync("scoped-user", false);
            var customRole = await service.UpdateUserStatusByGuidAsync("dual-user", false);
            var reset = await service.ResetUserPasswordAsync("scoped-user");

            Assert.False(direct.Success);
            Assert.Equal("HIGH_PRIVILEGE_TARGET_DENIED", direct.ErrorCode);
            Assert.False(customRole.Success);
            Assert.Equal("HIGH_PRIVILEGE_TARGET_DENIED", customRole.ErrorCode);
            Assert.False(reset.Success);
            Assert.Equal("HIGH_PRIVILEGE_TARGET_DENIED", reset.ErrorCode);
        }

        [Fact]
        public async Task RoleMemberReads_StoreManagerOnlySeesScopedNonPrivilegedUsers()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new[]
            {
                CreateUserRole("scoped-user", "role-viewer"),
                CreateUserRole("foreign-user", "role-viewer"),
                CreateUserRole("manager-1", "role-viewer"),
            }).ExecuteCommandAsync();
            var scope = new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, IsStoreManager = true,
                UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            });
            var roleService = new RoleService(
                CreateSqlSugarContext(_db),
                NullLogger<RoleService>.Instance,
                new HttpContextAccessor(),
                scope
            );

            var detail = await roleService.GetRoleByGuidAsync("role-viewer");
            var members = await roleService.GetRoleUsersAsync("role-viewer", new RoleQueryDto
            {
                Page = 1, PageSize = 100,
            });

            Assert.Equal(new[] { "scoped-user" }, detail.Data!.Users.Select(user => user.UserGUID));
            Assert.Equal(new[] { "scoped-user" }, members.Data!.Items!.Select(user => user.UserGUID));
        }

        [Fact]
        public async Task UserLists_StoreManagerExcludesSameStoreAdminAndWarehouseManager()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new[]
            {
                CreateUser("same-store-admin", "same-admin@example.com"),
                CreateUser("same-store-warehouse", "same-warehouse@example.com"),
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new Role { RoleGUID = "role-admin", RoleName = "Admin", IsActive = true },
                new Role { RoleGUID = "role-warehouse", RoleName = "WarehouseManager", IsActive = true },
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                CreateUserRole("same-store-admin", "role-admin"),
                CreateUserRole("same-store-warehouse", "role-warehouse"),
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                CreateUserStore("same-store-admin", "store-1", false),
                CreateUserStore("same-store-warehouse", "store-1", false),
            }).ExecuteCommandAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));

            var regular = await service.GetUsersAsync(new UserQueryDto { Page = 1, PageSize = 100 });
            var optimized = await service.GetUsersOptimizedAsync(new UserQueryDto { Page = 1, PageSize = 100 });

            Assert.True(regular.Success);
            Assert.True(optimized.Success);
            Assert.DoesNotContain(regular.Data!.Items!, user =>
                user.UserGUID is "same-store-admin" or "same-store-warehouse");
            Assert.DoesNotContain(optimized.Data!.Items!, user =>
                user.UserGUID is "same-store-admin" or "same-store-warehouse");
        }

        [Fact]
        public async Task GetUserLoginRecordsAsync_ReturnsSelectedUserRecordsInCreatedAtDescendingOrder()
        {
            await SeedLoginRecordUsersAsync();
            await SeedRefreshTokenAsync(
                "session-old",
                "login-user",
                DateTime.UtcNow.AddHours(-3),
                "203.0.113.1"
            );
            await SeedRefreshTokenAsync(
                "session-new",
                "login-user",
                DateTime.UtcNow.AddHours(-1),
                "203.0.113.2"
            );
            await SeedRefreshTokenAsync(
                "session-other-user",
                "other-user",
                DateTime.UtcNow,
                "198.51.100.9"
            );
            var service = CreateUserService();

            var result = await service.GetUserLoginRecordsAsync(
                "login-user",
                new UserLoginRecordQueryDto { Page = 1, PageSize = 1 }
            );

            Assert.True(result.Success);
            Assert.Equal(2, result.Data!.Total);
            var item = Assert.Single(result.Data.Items!);
            Assert.Equal("session-new", item.SessionId);
            Assert.Equal("203.0.113.2", item.IpAddress);
        }

        [Fact]
        public async Task GetUserLoginRecordsAsync_ComputesActiveRevokedAndExpiredStatuses()
        {
            await SeedLoginRecordUsersAsync();
            await SeedRefreshTokenAsync(
                "session-active",
                "login-user",
                DateTime.UtcNow.AddMinutes(-3),
                "203.0.113.1"
            );
            await SeedRefreshTokenAsync(
                "session-revoked",
                "login-user",
                DateTime.UtcNow.AddMinutes(-2),
                "203.0.113.2",
                isRevoked: true
            );
            await SeedRefreshTokenAsync(
                "session-expired",
                "login-user",
                DateTime.UtcNow.AddMinutes(-1),
                "203.0.113.3",
                expiresAt: DateTime.UtcNow.AddMinutes(-1)
            );
            var service = CreateUserService();

            var result = await service.GetUserLoginRecordsAsync(
                "login-user",
                new UserLoginRecordQueryDto { Page = 1, PageSize = 10 }
            );

            Assert.True(result.Success);
            var items = result.Data!.Items!.ToDictionary(item => item.SessionId);
            Assert.Equal("active", items["session-active"].Status);
            Assert.Equal("revoked", items["session-revoked"].Status);
            Assert.Equal("expired", items["session-expired"].Status);
            Assert.False(items["session-active"].IsExpired);
            Assert.True(items["session-revoked"].IsRevoked);
            Assert.True(items["session-expired"].IsExpired);
        }

        [Fact]
        public async Task BatchManageUserStoresAsync_AdminAddAsManageableInsertsUpgradesAndSyncsRole()
        {
            await SeedUsersAndStoresAsync();
            await _db.Insertable(CreateUser("user-2", "user2@example.com")).ExecuteCommandAsync();
            // user-1 已有 store-1 普通关联（应升级），user-2 已是 store-1 可管理（应不变）。
            await _db.Insertable(new[]
            {
                CreateUserStore("user-1", "store-1", false),
                CreateUserStore("user-2", "store-1", true),
            }).ExecuteCommandAsync();
            var service = CreateUserService(await SeedAdminScopeAsync());

            var result = await service.BatchManageUserStoresAsync(new BatchUserStoreOperationDto
            {
                Operation = "add",
                UserGuids = new List<string> { "user-1", "user-2" },
                StoreGuids = new List<string> { "store-1", "store-2" },
                AsManageable = true,
            });

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.Data!.AddedCount);
            Assert.Equal(1, result.Data.UpgradedCount);
            Assert.Equal(1, result.Data.UnchangedCount);
            Assert.Equal(2, result.Data.AffectedUserCount);
            Assert.Equal(new[] { "user_1" }, result.Data.UsersGainingStoreManagerRole);
            Assert.True((await FindUserStoreAsync("user-1", "store-1")).IsPrimary);
            Assert.True((await FindUserStoreAsync("user-1", "store-2")).IsPrimary);
            Assert.True((await FindUserStoreAsync("user-2", "store-2")).IsPrimary);
            Assert.Equal(1, await _db.Queryable<UserStore>().CountAsync(item =>
                item.UserGUID == "user-1" && item.StoreGUID == "store-1"));
            Assert.True(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.RoleGUID == "role-store-manager"));
        }

        [Fact]
        public async Task BatchManageUserStoresAsync_DryRunReportsImpactWithoutWriting()
        {
            await SeedUsersAndStoresAsync();
            await _db.Insertable(CreateUserStore("user-1", "store-1", true)).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("user-1", "role-store-manager")).ExecuteCommandAsync();
            var service = CreateUserService(await SeedAdminScopeAsync());

            var result = await service.BatchManageUserStoresAsync(new BatchUserStoreOperationDto
            {
                Operation = "remove",
                UserGuids = new List<string> { "user-1" },
                StoreGuids = new List<string> { "store-1", "store-2" },
                DryRun = true,
            });

            Assert.True(result.Success, result.Message);
            Assert.True(result.Data!.DryRun);
            Assert.Equal(1, result.Data.RemovedCount);
            Assert.Equal(1, result.Data.RemovedManageableCount);
            Assert.Equal(1, result.Data.UnchangedCount);
            Assert.Equal(new[] { "user_1" }, result.Data.UsersLosingStoreManagerRole);
            Assert.True((await FindUserStoreAsync("user-1", "store-1")).IsPrimary);
            Assert.True(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.RoleGUID == "role-store-manager"));
        }

        [Fact]
        public async Task BatchManageUserStoresAsync_AdminRemovingLastManageableStoreDropsStoreManagerRole()
        {
            await SeedUsersAndStoresAsync();
            await _db.Insertable(new[]
            {
                CreateUserStore("user-1", "store-1", true),
                CreateUserStore("user-1", "store-2", false),
            }).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("user-1", "role-store-manager")).ExecuteCommandAsync();
            var service = CreateUserService(await SeedAdminScopeAsync());

            var result = await service.BatchManageUserStoresAsync(new BatchUserStoreOperationDto
            {
                Operation = "remove",
                UserGuids = new List<string> { "user-1" },
                StoreGuids = new List<string> { "store-1" },
            });

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.Data!.RemovedCount);
            Assert.False(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.StoreGUID == "store-1"));
            Assert.True(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.StoreGUID == "store-2"));
            Assert.False(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.RoleGUID == "role-store-manager"));
        }

        [Fact]
        public async Task BatchManageUserStoresAsync_StoreManagerChangesOnlyInScopeViewRelationships()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new Store
            {
                StoreGUID = "store-3",
                StoreCode = "S003",
                StoreName = "Store 3",
                IsActive = true,
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                CreateUserStore("manager-1", "store-3", true),
                // dual-user 在 store-3 是可管理关联：店长不能撤销，应原样保留。
                CreateUserStore("dual-user", "store-3", true),
            }).ExecuteCommandAsync();
            var scope = new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1", "store-3" },
            });
            var service = CreateUserService(scope);

            var add = await service.BatchManageUserStoresAsync(new BatchUserStoreOperationDto
            {
                Operation = "add",
                UserGuids = new List<string> { "scoped-user", "dual-user" },
                StoreGuids = new List<string> { "store-3" },
            });
            Assert.True(add.Success, add.Message);
            Assert.Equal(1, add.Data!.AddedCount);
            Assert.Equal(1, add.Data.UnchangedCount);
            Assert.False((await FindUserStoreAsync("scoped-user", "store-3")).IsPrimary);

            var remove = await service.BatchManageUserStoresAsync(new BatchUserStoreOperationDto
            {
                Operation = "remove",
                UserGuids = new List<string> { "scoped-user", "dual-user" },
                StoreGuids = new List<string> { "store-3" },
            });
            Assert.True(remove.Success, remove.Message);
            Assert.Equal(1, remove.Data!.RemovedCount);
            Assert.Equal(1, remove.Data.ProtectedManageableCount);
            Assert.False(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "scoped-user" && item.StoreGUID == "store-3"));
            Assert.True((await FindUserStoreAsync("dual-user", "store-3")).IsPrimary);
        }

        [Theory]
        [InlineData("store-2", false, "STORE_SCOPE_DENIED")]
        [InlineData("store-1", true, "MANAGEABLE_STORE_GRANT_DENIED")]
        public async Task BatchManageUserStoresAsync_StoreManagerCannotExceedScopeOrGrantManagement(
            string storeGuid,
            bool asManageable,
            string expectedErrorCode
        )
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));

            var result = await service.BatchManageUserStoresAsync(new BatchUserStoreOperationDto
            {
                Operation = "add",
                UserGuids = new List<string> { "scoped-user" },
                StoreGuids = new List<string> { storeGuid },
                AsManageable = asManageable,
            });

            Assert.False(result.Success);
            Assert.Equal(expectedErrorCode, result.ErrorCode);
            Assert.False(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "scoped-user" && item.StoreGUID == "store-2"));
            Assert.False((await FindUserStoreAsync("scoped-user", "store-1")).IsPrimary);
        }

        [Fact]
        public async Task BatchManageUserStoresAsync_RejectedTargetRollsBackWholeBatch()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true, IsAuthenticated = true, UserGuid = "manager-1", StoreGuids = new[] { "store-1" },
            }));

            // foreign-user 不在店长范围内：整批拒绝，scoped-user 的移除也不能生效。
            var result = await service.BatchManageUserStoresAsync(new BatchUserStoreOperationDto
            {
                Operation = "remove",
                UserGuids = new List<string> { "scoped-user", "foreign-user" },
                StoreGuids = new List<string> { "store-1" },
            });

            Assert.False(result.Success);
            Assert.Equal("USER_SCOPE_DENIED", result.ErrorCode);
            Assert.Contains("foreign-user", result.Message);
            Assert.True(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "scoped-user" && item.StoreGUID == "store-1"));
        }

        [Theory]
        [InlineData("move", "UNSUPPORTED_OPERATION")]
        [InlineData("add", "BATCH_LIMIT_EXCEEDED")]
        public async Task BatchManageUserStoresAsync_RejectsInvalidRequestBeforeTouchingDatabase(
            string operation,
            string expectedErrorCode
        )
        {
            var service = CreateUserService();
            var userGuids = expectedErrorCode == "BATCH_LIMIT_EXCEEDED"
                ? Enumerable.Range(0, BatchUserStoreOperationDto.MaxUserCount + 1).Select(i => $"u-{i}").ToList()
                : new List<string> { "user-1" };

            var result = await service.BatchManageUserStoresAsync(new BatchUserStoreOperationDto
            {
                Operation = operation,
                UserGuids = userGuids,
                StoreGuids = new List<string> { "store-1" },
            });

            Assert.False(result.Success);
            Assert.Equal(expectedErrorCode, result.ErrorCode);
        }

        [Fact]
        public async Task AssignStoresToUserAsync_PreservesExplicitManagementFlags()
        {
            await SeedUsersAndStoresAsync();
            var service = CreateUserService(await SeedAdminScopeAsync());

            var result = await service.AssignStoresToUserAsync(
                "user-1",
                new List<UserStoreAssignmentDto>
                {
                    new() { StoreGUID = "store-1", IsPrimary = false },
                    new() { StoreGUID = "store-2", IsPrimary = true },
                }
            );

            Assert.True(result.Success);
            var store1 = await FindUserStoreAsync("user-1", "store-1");
            var store2 = await FindUserStoreAsync("user-1", "store-2");
            Assert.False(store1.IsPrimary);
            Assert.True(store2.IsPrimary);
        }

        [Fact]
        public async Task AssignStoresToUserAsync_PreservesExistingInactiveStoreAndAllowsRemoval()
        {
            await SeedInactiveStoreUserAsync();
            var service = CreateUserService(await SeedAdminScopeAsync());

            var preserveResult = await service.AssignStoresToUserAsync(
                "inactive-store-user",
                new List<UserStoreAssignmentDto>
                {
                    new() { StoreGUID = "inactive-store", IsPrimary = false },
                }
            );

            Assert.True(preserveResult.Success);
            Assert.True(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "inactive-store-user"
                && item.StoreGUID == "inactive-store"
                && !item.IsDeleted
            ));

            var removeResult = await service.AssignStoresToUserAsync(
                "inactive-store-user",
                new List<UserStoreAssignmentDto>()
            );

            Assert.True(removeResult.Success);
            Assert.False(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "inactive-store-user" && item.StoreGUID == "inactive-store"
            ));
        }

        [Fact]
        public async Task AssignStoresToUserAsync_AllowsNewInactiveStoreRelationship()
        {
            await SeedUsersAndStoresAsync();
            await _db.Insertable(new Store
            {
                StoreGUID = "inactive-new-store",
                StoreCode = "INACTIVE-NEW",
                StoreName = "Inactive New Store",
                IsActive = false,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            var service = CreateUserService(await SeedAdminScopeAsync());

            var result = await service.AssignStoresToUserAsync(
                "user-1",
                new List<UserStoreAssignmentDto>
                {
                    new() { StoreGUID = "inactive-new-store", IsPrimary = false },
                }
            );

            Assert.True(result.Success);
            Assert.True(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "user-1"
                && item.StoreGUID == "inactive-new-store"
                && !item.IsDeleted
            ));
        }

        [Fact]
        public async Task AssignStoresToUserAsync_RejectsMissingOrDeletedStoreWithoutPartialWrite()
        {
            await SeedUsersAndStoresAsync();
            await _db.Insertable(new Store
            {
                StoreGUID = "deleted-store",
                StoreCode = "DELETED",
                StoreName = "Deleted Store",
                IsActive = false,
                IsDeleted = true,
            }).ExecuteCommandAsync();
            var service = CreateUserService(await SeedAdminScopeAsync());

            var result = await service.AssignStoresToUserAsync(
                "user-1",
                new List<UserStoreAssignmentDto>
                {
                    new() { StoreGUID = "store-2", IsPrimary = false },
                    new() { StoreGUID = "deleted-store", IsPrimary = false },
                    new() { StoreGUID = "missing-store", IsPrimary = false },
                }
            );

            Assert.False(result.Success);
            Assert.Equal("STORE_NOT_FOUND", result.ErrorCode);
            Assert.False(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.StoreGUID == "store-2"
            ));
        }

        [Fact]
        public async Task AssignStoresToUserAsync_AdminManagementFlagSynchronizesStoreManagerRole()
        {
            await SeedUsersAndStoresAsync();
            await _db.Insertable(CreateUser("admin-1", "admin@example.test")).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new Role { RoleGUID = "role-admin", RoleName = "Admin", IsActive = true },
            }).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("admin-1", "role-admin")).ExecuteCommandAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    IsAdmin = true,
                    UserGuid = "admin-1",
                })
            );

            var grantResult = await service.AssignStoresToUserAsync(
                "user-1",
                new List<UserStoreAssignmentDto>
                {
                    new() { StoreGUID = "store-1", IsPrimary = true },
                }
            );

            Assert.True(grantResult.Success);
            Assert.True(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.RoleGUID == "role-store-manager"
            ));

            var revokeResult = await service.AssignStoresToUserAsync(
                "user-1",
                new List<UserStoreAssignmentDto>
                {
                    new() { StoreGUID = "store-1", IsPrimary = false },
                }
            );

            Assert.True(revokeResult.Success);
            Assert.False(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.RoleGUID == "role-store-manager"
            ));
        }

        [Fact]
        public async Task AssignStoresToUserAsync_NonAdminCannotGrantNewManagementRelationship()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.AssignStoresToUserAsync(
                "scoped-user",
                new List<UserStoreAssignmentDto>
                {
                    new() { StoreGUID = "store-1", IsPrimary = true },
                }
            );

            Assert.False(result.Success);
            Assert.Equal("MANAGEABLE_STORE_GRANT_DENIED", result.ErrorCode);
            Assert.False((await FindUserStoreAsync("scoped-user", "store-1")).IsPrimary);
        }

        [Fact]
        public async Task AssignStoresToUserAsync_StoreManagerReplacesOnlyInScopeViewRelationships()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new Store
            {
                StoreGUID = "store-3",
                StoreCode = "S003",
                StoreName = "Store 3",
                IsActive = true,
            }).ExecuteCommandAsync();
            var protectedPrimary = CreateUserStore("scoped-user", "store-3", true);
            protectedPrimary.UserStoreGUID = "protected-primary-store";
            protectedPrimary.CreatedAt = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            await _db.Insertable(new[]
            {
                CreateUserStore("manager-1", "store-3", true),
                CreateUserStore("scoped-user", "store-2", false),
                protectedPrimary,
            }).ExecuteCommandAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1", "store-3" },
                })
            );

            var addResult = await service.AssignStoresToUserAsync(
                "scoped-user",
                new List<UserStoreAssignmentDto>
                {
                    new() { StoreGUID = "store-1", IsPrimary = false },
                    new() { StoreGUID = "store-3", IsPrimary = false },
                }
            );

            Assert.True(addResult.Success);
            var preservedPrimaryAfterAdd = await FindUserStoreAsync("scoped-user", "store-3");
            Assert.True(preservedPrimaryAfterAdd.IsPrimary);
            Assert.Equal("protected-primary-store", preservedPrimaryAfterAdd.UserStoreGUID);
            Assert.Equal(protectedPrimary.CreatedAt, preservedPrimaryAfterAdd.CreatedAt);
            Assert.False((await FindUserStoreAsync("scoped-user", "store-2")).IsPrimary);

            var removeResult = await service.AssignStoresToUserAsync(
                "scoped-user",
                new List<UserStoreAssignmentDto>
                {
                    new() { StoreGUID = "store-3", IsPrimary = false },
                }
            );

            Assert.True(removeResult.Success);
            Assert.False(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "scoped-user" && item.StoreGUID == "store-1"
            ));
            Assert.True(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "scoped-user" && item.StoreGUID == "store-2"
            ));
            var preservedPrimaryAfterRemove = await FindUserStoreAsync("scoped-user", "store-3");
            Assert.True(preservedPrimaryAfterRemove.IsPrimary);
            Assert.Equal("protected-primary-store", preservedPrimaryAfterRemove.UserStoreGUID);
            Assert.Equal(protectedPrimary.CreatedAt, preservedPrimaryAfterRemove.CreatedAt);
        }

        [Fact]
        public async Task AssignRolesToUserAsync_CannotBypassStoreManagerDerivedRelationship()
        {
            await SeedUsersAndStoresAsync();
            var service = CreateUserService(await SeedAdminScopeAsync());

            var result = await service.AssignRolesToUserAsync(
                "user-1",
                new UserRoleAssignmentDto
                {
                    RoleGuids = new List<string> { "role-store-manager" },
                }
            );

            Assert.True(result.Success);
            Assert.False(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.RoleGUID == "role-store-manager"
            ));
        }

        // 分配角色仍只靠服务层按角色与范围收口（店长不持有 Users.ManageRoles）。
        // 分配分店自 10-07 起改为要求 Users.ManageStores，见 UserStoreWriteRoutes_RequireManageStoresPermission。
        [Theory]
        [InlineData(nameof(UsersController.AssignRolesToUser))]
        public void LimitedAccessWriteEndpoints_UseAuthenticatedServiceAuthorization(string methodName)
        {
            var method = typeof(UsersController).GetMethod(methodName);

            var authorize = method?.GetCustomAttribute<AuthorizeAttribute>();

            Assert.NotNull(authorize);
            Assert.Null(authorize!.Policy);
        }

        [Fact]
        public async Task AssignRolesToUserAsync_NonAdminCannotAssignHighPrivilegeRole()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(
                new Role { RoleGUID = "role-admin", RoleName = "Admin", IsActive = true }
            ).ExecuteCommandAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.AssignRolesToUserAsync(
                "scoped-user",
                new UserRoleAssignmentDto
                {
                    RoleGuids = new List<string> { "role-admin" },
                }
            );

            Assert.False(result.Success);
            Assert.Equal("ROLE_DELEGATION_DENIED", result.ErrorCode);
            Assert.False(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "scoped-user" && item.RoleGUID == "role-admin"
            ));
        }

        [Fact]
        public async Task AssignRolesToUserAsync_NonAdminPreservesExistingRoleItCannotGrant()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new[]
            {
                new Role { RoleGUID = "role-allowed", RoleName = "StoreStaff", IsActive = true },
                new Role { RoleGUID = "role-protected", RoleName = "Protected", IsActive = true },
            }).ExecuteCommandAsync();
            await _db.Insertable(new SysRolePermission
            {
                Id = "protected-reports-view",
                RoleGuid = "role-protected",
                PermissionCode = Permissions.Reports.View,
            }).ExecuteCommandAsync();
            var protectedRole = CreateUserRole("scoped-user", "role-protected");
            protectedRole.UserRoleGUID = "protected-user-role";
            protectedRole.CreatedAt = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            await _db.Insertable(protectedRole).ExecuteCommandAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.AssignRolesToUserAsync(
                "scoped-user",
                new UserRoleAssignmentDto
                {
                    RoleGuids = new List<string> { "role-allowed" },
                }
            );

            Assert.True(result.Success);
            var roleGuids = await _db.Queryable<UserRole>()
                .Where(item => item.UserGUID == "scoped-user")
                .Select(item => item.RoleGUID)
                .ToListAsync();
            Assert.Contains("role-allowed", roleGuids);
            Assert.Contains("role-protected", roleGuids);
            var preservedRole = await _db.Queryable<UserRole>()
                .SingleAsync(item =>
                    item.UserGUID == "scoped-user" && item.RoleGUID == "role-protected"
                );
            Assert.Equal("protected-user-role", preservedRole.UserRoleGUID);
            Assert.Equal(protectedRole.CreatedAt, preservedRole.CreatedAt);
        }

        [Fact]
        public async Task AssignRolesToUserAsync_NonAdminMayEchoExistingRoleItCannotGrant()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new[]
            {
                new Role { RoleGUID = "role-allowed", RoleName = "店铺员工", IsActive = true },
                new Role { RoleGUID = "role-protected", RoleName = "Protected", IsActive = true },
            }).ExecuteCommandAsync();
            await _db.Insertable(new SysRolePermission
            {
                Id = "protected-reports-view",
                RoleGuid = "role-protected",
                PermissionCode = Permissions.Reports.View,
            }).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("scoped-user", "role-protected"))
                .ExecuteCommandAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.AssignRolesToUserAsync(
                "scoped-user",
                new UserRoleAssignmentDto
                {
                    RoleGuids = new List<string> { "role-allowed", "role-protected" },
                }
            );

            Assert.True(result.Success);
            var roleGuids = await _db.Queryable<UserRole>()
                .Where(item => item.UserGUID == "scoped-user")
                .Select(item => item.RoleGUID)
                .ToListAsync();
            Assert.Contains("role-allowed", roleGuids);
            Assert.Contains("role-protected", roleGuids);
        }

        [Fact]
        public async Task AssignRolesToUserAsync_StoreManagerRemovesOnlyEmployeeRole()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new[]
            {
                new Role { RoleGUID = "role-employee", RoleName = "员工", IsActive = true },
                new Role { RoleGUID = "role-protected", RoleName = "Protected", IsActive = true },
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                CreateUserRole("scoped-user", "role-employee"),
                CreateUserRole("scoped-user", "role-protected"),
            }).ExecuteCommandAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.AssignRolesToUserAsync(
                "scoped-user",
                new UserRoleAssignmentDto()
            );

            Assert.True(result.Success);
            var roleGuids = await _db.Queryable<UserRole>()
                .Where(item => item.UserGUID == "scoped-user")
                .Select(item => item.RoleGUID)
                .ToListAsync();
            Assert.DoesNotContain("role-employee", roleGuids);
            Assert.Contains("role-protected", roleGuids);
        }

        [Fact]
        public async Task AssignRolesToUserAsync_StoreManagerCannotRemoveLastEmployeeRoleWithDirectPosPermissions()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(
                new Role { RoleGUID = "role-employee", RoleName = "StoreStaff", IsActive = true }
            ).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("scoped-user", "role-employee"))
                .ExecuteCommandAsync();
            await _db.Insertable(new SysUserPermission
            {
                Id = "staff-pos-permission",
                UserGuid = "scoped-user",
                PermissionCode = Permissions.PosTerminal.Sales.AddItem,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.AssignRolesToUserAsync(
                "scoped-user",
                new UserRoleAssignmentDto()
            );

            Assert.False(result.Success);
            Assert.Equal("EMPLOYEE_ROLE_HAS_POS_PERMISSIONS", result.ErrorCode);
            Assert.True(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "scoped-user" && item.RoleGUID == "role-employee"
            ));
        }

        [Fact]
        public async Task AssignRolesToUserAsync_StoreManagerCannotRemoveLastEmployeeRoleWithStorePosOverrides()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(
                new Role { RoleGUID = "role-employee", RoleName = "StoreStaff", IsActive = true }
            ).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("scoped-user", "role-employee"))
                .ExecuteCommandAsync();
            await _db.Insertable(new SysUserStorePosPermission
            {
                Id = "staff-store-pos-override",
                UserGuid = "scoped-user",
                StoreGuid = "store-1",
                PermissionCode = Permissions.PosTerminal.Sales.AddItem,
                IsGranted = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.AssignRolesToUserAsync(
                "scoped-user",
                new UserRoleAssignmentDto()
            );

            Assert.False(result.Success);
            Assert.Equal("EMPLOYEE_ROLE_HAS_POS_PERMISSIONS", result.ErrorCode);
            Assert.True(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "scoped-user" && item.RoleGUID == "role-employee"
            ));
        }

        [Fact]
        public async Task AssignRolesToUserAsync_WarehouseAliasCannotBypassPrimaryStoreScope()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await SeedAliasedStoreManagerAsync(
                "warehouse-1",
                "role-warehouse-1",
                "WarehouseManager",
                true
            );
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    IsAdmin = true,
                    UserGuid = "warehouse-1",
                })
            );

            var result = await service.AssignRolesToUserAsync(
                "foreign-user",
                new UserRoleAssignmentDto()
            );

            Assert.False(result.Success);
            Assert.Equal("ACCESS_DELEGATOR_DENIED", result.ErrorCode);
        }

        [Fact]
        public async Task AssignRolesToUserAsync_NonAdminCannotModifyCrossStoreTargetGlobally()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new Role
            {
                RoleGUID = "role-allowed",
                RoleName = "StoreStaff",
                IsActive = true,
            }).ExecuteCommandAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.AssignRolesToUserAsync(
                "dual-user",
                new UserRoleAssignmentDto
                {
                    RoleGuids = new List<string> { "role-allowed" },
                }
            );

            Assert.False(result.Success);
            Assert.Equal("USER_SCOPE_DENIED", result.ErrorCode);
        }

        [Fact]
        public async Task AssignStoresToUserAsync_NonAdminCannotModifySelf()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.AssignStoresToUserAsync(
                "manager-1",
                new List<UserStoreAssignmentDto>
                {
                    new() { StoreGUID = "store-1", IsPrimary = false },
                }
            );

            Assert.False(result.Success);
            Assert.Equal("SELF_ACCESS_MANAGEMENT_DENIED", result.ErrorCode);
            Assert.True((await FindUserStoreAsync("manager-1", "store-1")).IsPrimary);
        }

        [Fact]
        public async Task RemoveRoleFromUserAsync_RejectsDerivedStoreManagerRole()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService();

            var result = await service.RemoveRoleFromUserAsync(
                "manager-1",
                "role-store-manager"
            );

            Assert.False(result.Success);
            Assert.Equal("ADMIN_REQUIRED", result.ErrorCode);
            Assert.True(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "manager-1" && item.RoleGUID == "role-store-manager"
            ));
        }

        [Fact]
        public async Task AddUserToStoreAsync_PersistsManagementFlag()
        {
            await SeedUsersAndStoresAsync();
            var service = CreateStoreService(await SeedAdminScopeAsync());

            var result = await service.AddUserToStoreAsync(
                "store-1",
                new AddUserToStoreDto { UserGUID = "user-1", IsPrimary = true }
            );

            Assert.True(result.Success);
            var userStore = await FindUserStoreAsync("user-1", "store-1");
            Assert.True(userStore.IsPrimary);
            Assert.True(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.RoleGUID == "role-store-manager"
            ));
        }

        [Fact]
        public async Task AddUserToStoreAsync_NonAdminCannotGrantManagementRelationship()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateStoreService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.AddUserToStoreAsync(
                "store-2",
                new AddUserToStoreDto { UserGUID = "scoped-user", IsPrimary = true }
            );

            Assert.False(result.Success);
            Assert.Equal("ADMIN_REQUIRED", result.ErrorCode);
            Assert.False(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "scoped-user" && item.StoreGUID == "store-2"
            ));
        }

        [Fact]
        public async Task AddUserToStoreAsync_StoreManagerCannotUseStoreSideBypass()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await InsertUserStoreAsync("manager-1", "store-2", true);
            var service = CreateStoreService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.AddUserToStoreAsync(
                "store-2",
                new AddUserToStoreDto { UserGUID = "scoped-user", IsPrimary = false }
            );

            Assert.False(result.Success);
            Assert.Equal("ADMIN_REQUIRED", result.ErrorCode);
            Assert.False(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "scoped-user" && item.StoreGUID == "store-2"
            ));
        }

        [Fact]
        public async Task AddUserToStoreAsync_NonAdminCannotAddOutsideConcreteStoreScope()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateStoreService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.AddUserToStoreAsync(
                "store-2",
                new AddUserToStoreDto { UserGUID = "scoped-user", IsPrimary = false }
            );

            Assert.False(result.Success);
            Assert.Equal("ADMIN_REQUIRED", result.ErrorCode);
        }

        [Fact]
        public async Task RemoveUserFromStoreAsync_NonAdminCannotRemoveOutsideConcreteStoreScope()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateStoreService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.RemoveUserFromStoreAsync("store-2", "foreign-user");

            Assert.False(result.Success);
            Assert.Equal("ADMIN_REQUIRED", result.ErrorCode);
            Assert.True(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "foreign-user" && item.StoreGUID == "store-2"
            ));
        }

        [Fact]
        public async Task RemoveUserFromStoreAsync_AdminRemovalSynchronizesDerivedManagerRole()
        {
            await SeedUsersAndStoresAsync();
            await _db.Insertable(CreateUser("other-user", "other@example.test"))
                .ExecuteCommandAsync();
            await InsertUserStoreAsync("user-1", "store-1", true);
            await InsertUserStoreAsync("other-user", "store-1", false);
            await _db.Insertable(CreateUserRole("user-1", "role-store-manager"))
                .ExecuteCommandAsync();
            await _db.Insertable(CreateUser("admin-1", "admin@example.test"))
                .ExecuteCommandAsync();
            await _db.Insertable(new Role
            {
                RoleGUID = "role-admin",
                RoleName = "Admin",
                IsActive = true,
            }).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("admin-1", "role-admin")).ExecuteCommandAsync();
            var service = CreateStoreService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    IsAdmin = true,
                    UserGuid = "admin-1",
                })
            );

            var result = await service.RemoveUserFromStoreAsync("store-1", "user-1");

            Assert.True(result.Success);
            Assert.False(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.RoleGUID == "role-store-manager"
            ));
        }

        [Fact]
        public async Task RemoveUserFromStoreAsync_NonAdminCannotModifyHighPrivilegeTarget()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await SeedAliasedStoreManagerAsync(
                "manager-2",
                "role-manager-2",
                "StoreManager",
                true
            );
            var service = CreateStoreService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.RemoveUserFromStoreAsync("store-1", "manager-2");

            Assert.False(result.Success);
            Assert.Equal("ADMIN_REQUIRED", result.ErrorCode);
        }

        [Fact]
        public async Task CreateStoreAsync_WhenIsActiveOmitted_DefaultsCashRegisterDisabled()
        {
            var service = CreateStoreService();

            var result = await service.CreateStoreAsync(new CreateStoreDto
            {
                StoreName = "New Store",
                StoreCode = "1999",
            });

            Assert.True(result.Success);
            Assert.False(result.Data!.IsActive);
            var createdStore = await _db.Queryable<Store>()
                .FirstAsync(store => store.StoreCode == "1999");
            Assert.NotNull(createdStore);
            Assert.False(createdStore!.IsActive);
            Assert.Null(result.Data!.TimeZoneId);
            Assert.Null(createdStore.TimeZoneId);
        }

        [Fact]
        public async Task CreateStoreAsync_WhenTimeZoneIsInvalid_ReturnsValidationErrorWithoutInsert()
        {
            var service = CreateStoreService();
            var dto = new CreateStoreDto
            {
                StoreName = "Invalid Time Zone Store",
                StoreCode = "1998",
            };
            dto.TimeZoneId = "Australia/Perth";

            var result = await service.CreateStoreAsync(dto);

            Assert.False(result.Success);
            Assert.Equal("INVALID_STORE_TIME_ZONE", result.ErrorCode);
            Assert.False(await _db.Queryable<Store>()
                .AnyAsync(store => store.StoreCode == "1998"));
        }

        [Fact]
        public async Task UpdateStoreByGuidAsync_WhenTimeZoneIsInvalid_ReturnsValidationErrorAndPreservesStore()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-time-zone-update",
                StoreName = "Existing Store",
                StoreCode = "1997",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            var service = CreateStoreService();
            var dto = new UpdateStoreDto
            {
                StoreName = "Changed Store",
                StoreCode = "1997",
                IsActive = false,
            };
            dto.TimeZoneId = "Australia/Perth";

            var result = await service.UpdateStoreByGuidAsync("store-time-zone-update", dto);

            Assert.False(result.Success);
            Assert.Equal("INVALID_STORE_TIME_ZONE", result.ErrorCode);
            var store = await _db.Queryable<Store>()
                .FirstAsync(item => item.StoreGUID == "store-time-zone-update");
            Assert.Equal("Existing Store", store!.StoreName);
            Assert.True(store.IsActive);
        }

        [Fact]
        public async Task UpdateStoreByGuidAsync_WhenTimeZoneIsBlank_PreservesExistingTimeZoneAndReturnsIt()
        {
            var store = new Store
            {
                StoreGUID = "store-time-zone-preserve",
                StoreName = "Existing Store",
                StoreCode = "1996",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            };
            store.TimeZoneId = "Australia/Melbourne";
            await _db.Insertable(store).ExecuteCommandAsync();
            var dto = new UpdateStoreDto
            {
                StoreName = "Updated Store",
                StoreCode = "1996",
                IsActive = true,
            };
            dto.TimeZoneId = "  ";

            var result = await CreateStoreService().UpdateStoreByGuidAsync(store.StoreGUID, dto);

            Assert.True(result.Success, result.Message);
            Assert.Equal("Australia/Melbourne", result.Data!.TimeZoneId);
            var updated = await _db.Queryable<Store>()
                .FirstAsync(item => item.StoreGUID == store.StoreGUID);
            Assert.Equal("Australia/Melbourne", updated!.TimeZoneId);
        }

        [Fact]
        public async Task CreateStoreAsync_PersistsReturnPolicy()
        {
            var service = CreateStoreService();

            var result = await service.CreateStoreAsync(new CreateStoreDto
            {
                StoreName = "Return Policy Store",
                StoreCode = "1887",
                IsActive = true,
                ReturnPolicy = "7 天内可退换，凭小票。",
            });

            Assert.True(result.Success, result.Message);
            Assert.Equal("7 天内可退换，凭小票。", result.Data!.ReturnPolicy);
            var createdStore = await _db.Queryable<Store>()
                .FirstAsync(store => store.StoreCode == "1887");
            Assert.NotNull(createdStore);
            Assert.Equal("7 天内可退换，凭小票。", createdStore!.ReturnPolicy);
        }

        [Fact]
        public async Task UpdateStoreByGuidAsync_PersistsReturnPolicy()
        {
            var store = new Store
            {
                StoreGUID = "store-return-policy-update",
                StoreName = "Existing Store",
                StoreCode = "1886",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            };
            await _db.Insertable(store).ExecuteCommandAsync();

            var result = await CreateStoreService().UpdateStoreByGuidAsync(
                store.StoreGUID,
                new UpdateStoreDto
                {
                    StoreName = "Updated Store",
                    StoreCode = "1886",
                    IsActive = true,
                    ReturnPolicy = "无质量问题不退不换。",
                }
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal("无质量问题不退不换。", result.Data!.ReturnPolicy);
            var updated = await _db.Queryable<Store>()
                .FirstAsync(item => item.StoreGUID == store.StoreGUID);
            Assert.Equal("无质量问题不退不换。", updated!.ReturnPolicy);
        }

        [Fact]
        public async Task GetStoreByGuidAsync_ReturnsReturnPolicy()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-return-policy-detail",
                StoreName = "Detail Store",
                StoreCode = "1885",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                ReturnPolicy = "本店支持 14 天无理由退换。",
            }).ExecuteCommandAsync();

            var result = await CreateStoreService().GetStoreByGuidAsync("store-return-policy-detail");

            Assert.True(result.Success, result.Message);
            Assert.Equal("本店支持 14 天无理由退换。", result.Data!.ReturnPolicy);
        }

        [Fact]
        public async Task CreateStoreAsync_PersistsVoucherAndInstallmentTerms()
        {
            var service = CreateStoreService();

            var result = await service.CreateStoreAsync(new CreateStoreDto
            {
                StoreName = "Terms Store",
                StoreCode = "1888",
                IsActive = true,
                VoucherTerms = "Use at the issuing store only.\nNot redeemable for cash.",
                InstallmentTerms = "Order total: $50.00 minimum.\nFirst payment: $20.00 minimum.",
            });

            Assert.True(result.Success, result.Message);
            Assert.Equal("Use at the issuing store only.\nNot redeemable for cash.", result.Data!.VoucherTerms);
            Assert.Equal("Order total: $50.00 minimum.\nFirst payment: $20.00 minimum.", result.Data.InstallmentTerms);
            var createdStore = await _db.Queryable<Store>()
                .FirstAsync(store => store.StoreCode == "1888");
            Assert.NotNull(createdStore);
            Assert.Equal("Use at the issuing store only.\nNot redeemable for cash.", createdStore!.VoucherTerms);
            Assert.Equal("Order total: $50.00 minimum.\nFirst payment: $20.00 minimum.", createdStore.InstallmentTerms);
        }

        [Fact]
        public async Task UpdateStoreByGuidAsync_PersistsVoucherAndInstallmentTerms_AndNullClearsThem()
        {
            var store = new Store
            {
                StoreGUID = "store-terms-update",
                StoreName = "Existing Store",
                StoreCode = "1889",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                VoucherTerms = "old voucher terms",
                InstallmentTerms = "old installment terms",
            };
            await _db.Insertable(store).ExecuteCommandAsync();
            var service = CreateStoreService();

            var result = await service.UpdateStoreByGuidAsync(
                store.StoreGUID,
                new UpdateStoreDto
                {
                    StoreName = "Updated Store",
                    StoreCode = "1889",
                    IsActive = true,
                    VoucherTerms = "Use at the issuing store only.",
                    InstallmentTerms = "Each later payment: $5.00 minimum.",
                }
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal("Use at the issuing store only.", result.Data!.VoucherTerms);
            Assert.Equal("Each later payment: $5.00 minimum.", result.Data.InstallmentTerms);
            var updated = await _db.Queryable<Store>().FirstAsync(item => item.StoreGUID == store.StoreGUID);
            Assert.Equal("Use at the issuing store only.", updated!.VoucherTerms);
            Assert.Equal("Each later payment: $5.00 minimum.", updated.InstallmentTerms);

            // 表单清空后传 null：落库为 NULL，下发后收银端回落到内置默认文案。
            var cleared = await service.UpdateStoreByGuidAsync(
                store.StoreGUID,
                new UpdateStoreDto { StoreName = "Updated Store", StoreCode = "1889", IsActive = true }
            );
            Assert.True(cleared.Success, cleared.Message);
            Assert.Null(cleared.Data!.VoucherTerms);
            var afterClear = await _db.Queryable<Store>().FirstAsync(item => item.StoreGUID == store.StoreGUID);
            Assert.Null(afterClear!.VoucherTerms);
            Assert.Null(afterClear.InstallmentTerms);
        }

        [Fact]
        public async Task UpdateStoreAsync_PersistsVoucherAndInstallmentTerms()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-terms-update-legacy",
                StoreName = "Legacy Update Store",
                StoreCode = "1890",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var result = await CreateStoreService().UpdateStoreAsync(
                "store-terms-update-legacy",
                new UpdateStoreDto
                {
                    StoreName = "Legacy Update Store",
                    StoreCode = "1890",
                    IsActive = true,
                    VoucherTerms = "voucher",
                    InstallmentTerms = "installment",
                }
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal("voucher", result.Data!.VoucherTerms);
            Assert.Equal("installment", result.Data.InstallmentTerms);
            var updated = await _db.Queryable<Store>().FirstAsync(item => item.StoreGUID == "store-terms-update-legacy");
            Assert.Equal("voucher", updated!.VoucherTerms);
            Assert.Equal("installment", updated.InstallmentTerms);
        }

        [Fact]
        public async Task StoreReadModels_ReturnVoucherAndInstallmentTerms()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-terms-read",
                StoreName = "Terms Read Store",
                StoreCode = "1891",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                VoucherTerms = "voucher terms",
                InstallmentTerms = "installment terms",
            }).ExecuteCommandAsync();
            var service = CreateStoreService();

            var detail = await service.GetStoreByGuidAsync("store-terms-read");
            var byCode = await service.GetStoreByCodeAsync("1891");
            var list = await service.GetStoresAsync(new StoreQueryDto { Search = "1891", Page = 1, PageSize = 20 });
            var allByName = await service.GetAllStoresByNameAsync();
            var active = await service.GetActiveStoresAsync();

            Assert.True(detail.Success, detail.Message);
            Assert.Equal(("voucher terms", "installment terms"), (detail.Data!.VoucherTerms, detail.Data.InstallmentTerms));
            Assert.Equal(("voucher terms", "installment terms"), (byCode.Data!.VoucherTerms, byCode.Data.InstallmentTerms));
            var listed = Assert.Single(list.Data!.Items!);
            Assert.Equal(("voucher terms", "installment terms"), (listed.VoucherTerms, listed.InstallmentTerms));
            var named = Assert.Single(allByName.Data!, item => item.StoreCode == "1891");
            Assert.Equal(("voucher terms", "installment terms"), (named.VoucherTerms, named.InstallmentTerms));
            var activeItem = Assert.Single(active.Data!, item => item.StoreCode == "1891");
            Assert.Equal(("voucher terms", "installment terms"), (activeItem.VoucherTerms, activeItem.InstallmentTerms));
        }

        [Fact]
        public async Task BatchUpdateStoresAsync_UpdatesVoucherAndInstallmentTermsWithTrimAndBlankToNull()
        {
            await _db.Insertable(new[]
            {
                new Store
                {
                    StoreGUID = "store-batch-terms-1",
                    StoreName = "Batch Terms Store 1",
                    StoreCode = "1892",
                    ReturnPolicy = "Keep policy",
                    VoucherTerms = "old voucher 1",
                    InstallmentTerms = "old installment 1",
                    IsActive = true,
                },
                new Store
                {
                    StoreGUID = "store-batch-terms-2",
                    StoreName = "Batch Terms Store 2",
                    StoreCode = "1893",
                    ReturnPolicy = "Keep policy",
                    IsActive = true,
                },
            }).ExecuteCommandAsync();
            var service = CreateStoreService();

            var result = await service.BatchUpdateStoresAsync(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-terms-1", "store-batch-terms-2" },
                    Fields = new List<string> { "voucherTerms", "installmentTerms" },
                    VoucherTerms = "  Use at the issuing store only.\nNot redeemable for cash.  ",
                    InstallmentTerms = "   ",
                }
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.Data!.UpdatedCount);
            var stores = await _db.Queryable<Store>()
                .Where(store => new[] { "store-batch-terms-1", "store-batch-terms-2" }.Contains(store.StoreGUID))
                .ToListAsync();
            Assert.All(stores, store =>
            {
                Assert.Equal("Use at the issuing store only.\nNot redeemable for cash.", store.VoucherTerms);
                // 空白＝未定制：落库 NULL（把原有的 old installment 1 清掉）。
                Assert.Null(store.InstallmentTerms);
                Assert.Equal("Keep policy", store.ReturnPolicy);
            });
        }

        [Fact]
        public async Task BatchUpdateStoresAsync_AcceptsAllSevenFieldsAndIgnoresUnselectedTerms()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-batch-seven",
                StoreName = "Seven Fields Store",
                StoreCode = "1894",
                TimeZoneId = StoreTimeZonePolicy.Brisbane,
                ABN = "old-abn",
                BrandName = "Old Brand",
                ReturnPolicy = "Old policy",
                VoucherTerms = "Keep voucher",
                InstallmentTerms = "Keep installment",
                IsActive = true,
            }).ExecuteCommandAsync();
            var service = CreateStoreService();

            // 只选 returnPolicy：即使请求体带了新字段（含超长值），也被忽略、不校验、不落库。
            var ignored = await service.BatchUpdateStoresAsync(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-seven" },
                    Fields = new List<string> { "returnPolicy" },
                    ReturnPolicy = "New policy",
                    VoucherTerms = new string('v', 601),
                    InstallmentTerms = new string('i', 601),
                }
            );
            Assert.True(ignored.Success, ignored.Message);
            var afterIgnored = await _db.Queryable<Store>().FirstAsync(item => item.StoreGUID == "store-batch-seven");
            Assert.Equal("New policy", afterIgnored!.ReturnPolicy);
            Assert.Equal("Keep voucher", afterIgnored.VoucherTerms);
            Assert.Equal("Keep installment", afterIgnored.InstallmentTerms);

            // 七个字段一次全选（旧上限是 5，必须放宽到 7）。
            var all = await service.BatchUpdateStoresAsync(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-seven" },
                    Fields = new List<string>
                    {
                        "timeZoneId", "abn", "brandName", "isActive", "returnPolicy", "voucherTerms", "installmentTerms",
                    },
                    TimeZoneId = StoreTimeZonePolicy.Sydney,
                    ABN = "new-abn",
                    BrandName = "New Brand",
                    IsActive = false,
                    ReturnPolicy = "Newest policy",
                    VoucherTerms = "New voucher",
                    InstallmentTerms = "New installment",
                }
            );
            Assert.True(all.Success, all.Message);
            var afterAll = await _db.Queryable<Store>().FirstAsync(item => item.StoreGUID == "store-batch-seven");
            Assert.Equal(("New voucher", "New installment"), (afterAll!.VoucherTerms, afterAll.InstallmentTerms));
            Assert.Equal("Newest policy", afterAll.ReturnPolicy);
            Assert.False(afterAll.IsActive);

            // 八个字段（含重复）仍被拒绝。
            var tooMany = await service.BatchUpdateStoresAsync(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-seven" },
                    Fields = new List<string>
                    {
                        "timeZoneId", "abn", "brandName", "isActive", "returnPolicy", "voucherTerms", "installmentTerms", "abn",
                    },
                }
            );
            Assert.False(tooMany.Success);
            Assert.Equal("INVALID_STORE_BATCH_FIELDS", tooMany.ErrorCode);
        }

        [Fact]
        public async Task BatchUpdateStoresAsync_TermsLengthLimit600_AppliesAfterTrimAndRejectsWithoutWriting()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-batch-terms-length",
                StoreName = "Terms Length Store",
                StoreCode = "1895",
                VoucherTerms = "Original voucher",
                InstallmentTerms = "Original installment",
                IsActive = true,
            }).ExecuteCommandAsync();
            var service = CreateStoreService();

            var voucherTooLong = await service.BatchUpdateStoresAsync(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-terms-length" },
                    Fields = new List<string> { "voucherTerms" },
                    VoucherTerms = new string('v', 601),
                }
            );
            var installmentTooLong = await service.BatchUpdateStoresAsync(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-terms-length" },
                    Fields = new List<string> { "installmentTerms" },
                    InstallmentTerms = new string('i', 601),
                }
            );

            Assert.False(voucherTooLong.Success);
            Assert.Equal("INVALID_STORE_BATCH_VALUE", voucherTooLong.ErrorCode);
            Assert.False(installmentTooLong.Success);
            Assert.Equal("INVALID_STORE_BATCH_VALUE", installmentTooLong.ErrorCode);
            var untouched = await _db.Queryable<Store>().FirstAsync(item => item.StoreGUID == "store-batch-terms-length");
            Assert.Equal("Original voucher", untouched!.VoucherTerms);
            Assert.Equal("Original installment", untouched.InstallmentTerms);

            // 恰好 600 个有效字符（前后带空白）：trim 后通过。
            var boundary = await service.BatchUpdateStoresAsync(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-terms-length" },
                    Fields = new List<string> { "voucherTerms", "installmentTerms" },
                    VoucherTerms = "  " + new string('v', 600) + "\n",
                    InstallmentTerms = new string('i', 600),
                }
            );
            Assert.True(boundary.Success, boundary.Message);
            var updated = await _db.Queryable<Store>().FirstAsync(item => item.StoreGUID == "store-batch-terms-length");
            Assert.Equal(600, updated!.VoucherTerms!.Length);
            Assert.Equal(600, updated.InstallmentTerms!.Length);
        }

        [Fact]
        public async Task BatchUpdateStoresAsync_UpdatesFiveSelectedFieldsAtomically()
        {
            var originalUpdatedAt = DateTime.UtcNow.AddDays(-1);
            await _db.Insertable(new[]
            {
                new Store
                {
                    StoreGUID = "store-batch-1",
                    StoreName = "Batch Store 1",
                    StoreCode = "1871",
                    Address = "Address 1",
                    TimeZoneId = StoreTimeZonePolicy.Brisbane,
                    ABN = "old-abn-1",
                    BrandName = "Old Brand 1",
                    ReturnPolicy = "Old policy 1",
                    IsActive = true,
                    CreatedAt = originalUpdatedAt,
                    UpdatedAt = originalUpdatedAt,
                },
                new Store
                {
                    StoreGUID = "store-batch-2",
                    StoreName = "Batch Store 2",
                    StoreCode = "1872",
                    Address = "Address 2",
                    TimeZoneId = StoreTimeZonePolicy.Melbourne,
                    ABN = "old-abn-2",
                    BrandName = "Old Brand 2",
                    ReturnPolicy = "Old policy 2",
                    IsActive = true,
                    CreatedAt = originalUpdatedAt,
                    UpdatedAt = originalUpdatedAt,
                },
            }).ExecuteCommandAsync();

            var result = await CreateStoreService().BatchUpdateStoresAsync(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-1", "store-batch-2" },
                    Fields = new List<string>
                    {
                        "timeZoneId",
                        "abn",
                        "brandName",
                        "isActive",
                        "returnPolicy",
                    },
                    TimeZoneId = "  australia/sydney  ",
                    ABN = "   ",
                    BrandName = "  Unified Brand  ",
                    IsActive = false,
                    ReturnPolicy = "  30 days with receipt.  ",
                }
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.Data!.RequestedCount);
            Assert.Equal(2, result.Data.UpdatedCount);
            Assert.Equal(
                new[] { "store-batch-1", "store-batch-2" },
                result.Data.UpdatedStoreGuids
            );

            var stores = await _db.Queryable<Store>()
                .Where(store => new[] { "store-batch-1", "store-batch-2" }.Contains(store.StoreGUID))
                .OrderBy(store => store.StoreGUID)
                .ToListAsync();
            Assert.Equal(2, stores.Count);
            Assert.All(stores, store =>
            {
                Assert.Equal(StoreTimeZonePolicy.Sydney, store.TimeZoneId);
                Assert.Null(store.ABN);
                Assert.Equal("Unified Brand", store.BrandName);
                Assert.False(store.IsActive);
                Assert.Equal("30 days with receipt.", store.ReturnPolicy);
                Assert.True(store.UpdatedAt > originalUpdatedAt);
            });
            Assert.Equal("Batch Store 1", stores[0].StoreName);
            Assert.Equal("Address 1", stores[0].Address);
            Assert.Equal("Batch Store 2", stores[1].StoreName);
            Assert.Equal("Address 2", stores[1].Address);
            Assert.Equal(stores[0].UpdatedAt, stores[1].UpdatedAt);
        }

        [Fact]
        public async Task BatchUpdateStoresAsync_IgnoresValuesForUnselectedFields()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-batch-selected-fields",
                StoreName = "Selected Fields Store",
                StoreCode = "1873",
                TimeZoneId = StoreTimeZonePolicy.Brisbane,
                ABN = "old-abn",
                BrandName = "Keep Brand",
                ReturnPolicy = "Keep policy",
                IsActive = true,
            }).ExecuteCommandAsync();

            var result = await CreateStoreService().BatchUpdateStoresAsync(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-selected-fields" },
                    Fields = new List<string> { "abn" },
                    ABN = "   ",
                    TimeZoneId = "Australia/Perth",
                    BrandName = new string('b', 101),
                    ReturnPolicy = new string('r', 501),
                    IsActive = false,
                }
            );

            Assert.True(result.Success, result.Message);
            var store = await _db.Queryable<Store>()
                .FirstAsync(item => item.StoreGUID == "store-batch-selected-fields");
            Assert.Null(store!.ABN);
            Assert.Equal(StoreTimeZonePolicy.Brisbane, store.TimeZoneId);
            Assert.Equal("Keep Brand", store.BrandName);
            Assert.Equal("Keep policy", store.ReturnPolicy);
            Assert.True(store.IsActive);
        }

        [Fact]
        public async Task BatchUpdateStoresAsync_RejectsInvalidRequestShapesWithoutWriting()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-batch-validation",
                StoreName = "Validation Store",
                StoreCode = "1874",
                BrandName = "Original Brand",
                IsActive = true,
            }).ExecuteCommandAsync();

            var invalidRequests = new[]
            {
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string>(),
                    Fields = new List<string> { "brandName" },
                    BrandName = "Changed",
                },
                new BatchUpdateStoresDto
                {
                    StoreGuids = Enumerable.Range(1, 101).Select(index => $"store-{index}").ToList(),
                    Fields = new List<string> { "brandName" },
                    BrandName = "Changed",
                },
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string>
                    {
                        "store-batch-validation",
                        "store-batch-validation",
                    },
                    Fields = new List<string> { "brandName" },
                    BrandName = "Changed",
                },
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-validation" },
                    Fields = new List<string>(),
                },
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-validation" },
                    Fields = new List<string> { "brandName", "brandName" },
                    BrandName = "Changed",
                },
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-validation" },
                    Fields = new List<string> { "storeName" },
                },
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-validation" },
                    Fields = new List<string> { "isActive" },
                },
            };

            foreach (var request in invalidRequests)
            {
                var result = await CreateStoreService().BatchUpdateStoresAsync(request);

                Assert.False(result.Success);
                Assert.NotNull(result.Data);
                Assert.Equal(0, result.Data!.UpdatedCount);
            }

            var store = await _db.Queryable<Store>()
                .FirstAsync(item => item.StoreGUID == "store-batch-validation");
            Assert.Equal("Original Brand", store!.BrandName);
            Assert.True(store.IsActive);
        }

        [Fact]
        public async Task BatchUpdateStoresAsync_RejectsInvalidSelectedValuesWithoutWriting()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-batch-value-validation",
                StoreName = "Value Validation Store",
                StoreCode = "1875",
                TimeZoneId = StoreTimeZonePolicy.Brisbane,
                ABN = "Original ABN",
                BrandName = "Original Brand",
                ReturnPolicy = "Original policy",
                IsActive = true,
            }).ExecuteCommandAsync();

            var invalidRequests = new[]
            {
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-value-validation" },
                    Fields = new List<string> { "timeZoneId" },
                    TimeZoneId = "   ",
                },
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-value-validation" },
                    Fields = new List<string> { "timeZoneId" },
                    TimeZoneId = "Australia/Perth",
                },
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-value-validation" },
                    Fields = new List<string> { "abn" },
                    ABN = new string('a', 21),
                },
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-value-validation" },
                    Fields = new List<string> { "brandName" },
                    BrandName = new string('b', 101),
                },
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-value-validation" },
                    Fields = new List<string> { "returnPolicy" },
                    ReturnPolicy = new string('r', 501),
                },
            };

            foreach (var request in invalidRequests)
            {
                var result = await CreateStoreService().BatchUpdateStoresAsync(request);

                Assert.False(result.Success);
                Assert.NotNull(result.Data);
                Assert.Equal(0, result.Data!.UpdatedCount);
            }

            var store = await _db.Queryable<Store>()
                .FirstAsync(item => item.StoreGUID == "store-batch-value-validation");
            Assert.Equal(StoreTimeZonePolicy.Brisbane, store!.TimeZoneId);
            Assert.Equal("Original ABN", store.ABN);
            Assert.Equal("Original Brand", store.BrandName);
            Assert.Equal("Original policy", store.ReturnPolicy);
        }

        [Fact]
        public async Task BatchUpdateStoresAsync_WhenTargetIsMissing_RollsBackWholeBatch()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-batch-existing",
                StoreName = "Existing Batch Store",
                StoreCode = "1876",
                BrandName = "Original Brand",
                IsActive = true,
            }).ExecuteCommandAsync();

            var result = await CreateStoreService().BatchUpdateStoresAsync(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-existing", "store-batch-missing" },
                    Fields = new List<string> { "brandName" },
                    BrandName = "Changed Brand",
                }
            );

            Assert.False(result.Success);
            Assert.Equal("STORE_BATCH_TARGET_INVALID", result.ErrorCode);
            Assert.NotNull(result.Data);
            Assert.Equal(0, result.Data!.UpdatedCount);
            var store = await _db.Queryable<Store>()
                .FirstAsync(item => item.StoreGUID == "store-batch-existing");
            Assert.Equal("Original Brand", store!.BrandName);
        }

        [Fact]
        public async Task BatchUpdateStoresAsync_WhenTargetIsSoftDeleted_RollsBackWholeBatch()
        {
            await _db.Insertable(new[]
            {
                new Store
                {
                    StoreGUID = "store-batch-active",
                    StoreName = "Active Batch Store",
                    StoreCode = "1877",
                    BrandName = "Active Brand",
                    IsActive = true,
                },
                new Store
                {
                    StoreGUID = "store-batch-deleted",
                    StoreName = "Deleted Batch Store",
                    StoreCode = "1878",
                    BrandName = "Deleted Brand",
                    IsActive = true,
                    IsDeleted = true,
                },
            }).ExecuteCommandAsync();

            var result = await CreateStoreService().BatchUpdateStoresAsync(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-batch-active", "store-batch-deleted" },
                    Fields = new List<string> { "brandName" },
                    BrandName = "Changed Brand",
                }
            );

            Assert.False(result.Success);
            Assert.Equal("STORE_BATCH_TARGET_INVALID", result.ErrorCode);
            Assert.NotNull(result.Data);
            Assert.Equal(0, result.Data!.UpdatedCount);
            var activeStore = await _db.Queryable<Store>()
                .FirstAsync(item => item.StoreGUID == "store-batch-active");
            Assert.Equal("Active Brand", activeStore!.BrandName);
        }

        [Fact]
        public async Task BatchUpdateStoresAsync_WhenAffectedRowCountConflicts_RollsBackWholeBatch()
        {
            await _db.Insertable(new[]
            {
                new Store
                {
                    StoreGUID = "store-batch-conflict-1",
                    StoreName = "Conflict Store 1",
                    StoreCode = "1879",
                    BrandName = "Original Brand 1",
                    IsActive = true,
                },
                new Store
                {
                    StoreGUID = "store-batch-conflict-2",
                    StoreName = "Conflict Store 2",
                    StoreCode = "1880",
                    BrandName = "Original Brand 2",
                    IsActive = true,
                },
            }).ExecuteCommandAsync();
            _db.Ado.ExecuteCommand(
                """
                CREATE TRIGGER IgnoreSecondBatchStoreUpdate
                BEFORE UPDATE ON "Store"
                WHEN OLD."StoreGUID" = 'store-batch-conflict-2'
                BEGIN
                    SELECT RAISE(IGNORE);
                END;
                """
            );

            var result = await CreateStoreService().BatchUpdateStoresAsync(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string>
                    {
                        "store-batch-conflict-1",
                        "store-batch-conflict-2",
                    },
                    Fields = new List<string> { "brandName" },
                    BrandName = "Changed Brand",
                }
            );

            Assert.False(result.Success);
            Assert.Equal("STORE_BATCH_CONFLICT", result.ErrorCode);
            Assert.NotNull(result.Data);
            Assert.Equal(0, result.Data!.UpdatedCount);
            var stores = await _db.Queryable<Store>()
                .Where(store => new[]
                {
                    "store-batch-conflict-1",
                    "store-batch-conflict-2",
                }.Contains(store.StoreGUID))
                .OrderBy(store => store.StoreGUID)
                .ToListAsync();
            Assert.Equal("Original Brand 1", stores[0].BrandName);
            Assert.Equal("Original Brand 2", stores[1].BrandName);
        }

        [Fact]
        public async Task BatchUpdateStoresAsync_TreatsCaseVariantTargetsAsDistinct()
        {
            await _db.Insertable(new[]
            {
                new Store
                {
                    StoreGUID = "store-A",
                    StoreCode = "1881",
                    StoreName = "Upper Case Store",
                    BrandName = "Original A",
                    IsActive = true,
                },
                new Store
                {
                    StoreGUID = "store-a",
                    StoreCode = "1882",
                    StoreName = "Lower Case Store",
                    BrandName = "Original a",
                    IsActive = true,
                },
            }).ExecuteCommandAsync();

            var result = await CreateStoreService().BatchUpdateStoresAsync(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-A", "store-a" },
                    Fields = new List<string> { "brandName" },
                    BrandName = "Updated Brand",
                }
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.Data!.UpdatedCount);

            var upper = await _db.Queryable<Store>()
                .FirstAsync(store => store.StoreGUID == "store-A");
            var lower = await _db.Queryable<Store>()
                .FirstAsync(store => store.StoreGUID == "store-a");
            Assert.Equal("Updated Brand", upper!.BrandName);
            Assert.Equal("Updated Brand", lower!.BrandName);
        }

        [Fact]
        public async Task BatchUpdateStoresAsync_RejectsExactDuplicateTargets()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-dup-exact",
                StoreCode = "1883",
                StoreName = "Exact Duplicate Store",
                BrandName = "Original",
                IsActive = true,
            }).ExecuteCommandAsync();

            var result = await CreateStoreService().BatchUpdateStoresAsync(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-dup-exact", "store-dup-exact" },
                    Fields = new List<string> { "brandName" },
                    BrandName = "Changed",
                }
            );

            Assert.False(result.Success);
            Assert.Equal("INVALID_STORE_BATCH_REQUEST", result.ErrorCode);
            Assert.Equal(0, result.Data!.UpdatedCount);
        }

        [Fact]
        public async Task CreateStoreAsync_WhenIsActiveTrue_PersistsCashRegisterEnabled()
        {
            var service = CreateStoreService();

            var result = await service.CreateStoreAsync(new CreateStoreDto
            {
                StoreName = "Cash Store",
                StoreCode = "1888",
                IsActive = true,
            });

            Assert.True(result.Success);
            Assert.True(result.Data!.IsActive);
            var createdStore = await _db.Queryable<Store>()
                .FirstAsync(store => store.StoreCode == "1888");
            Assert.NotNull(createdStore);
            Assert.True(createdStore!.IsActive);
        }

        [Fact]
        public async Task GetNextStoreCodeAsync_WhenNoNumericCodes_ReturnsDefaultStartCode()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-alpha",
                StoreName = "Alpha Store",
                StoreCode = "S001",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            var service = CreateStoreService();

            var result = await service.GetNextStoreCodeAsync();

            Assert.True(result.Success);
            Assert.Equal("1001", result.Data);
        }

        [Fact]
        public async Task GetNextStoreCodeAsync_UsesMaxNumericCodePlusOne()
        {
            await _db.Insertable(new[]
            {
                new Store
                {
                    StoreGUID = "store-1001",
                    StoreName = "Store 1001",
                    StoreCode = "1001",
                    IsActive = true,
                    IsDeleted = false,
                },
                new Store
                {
                    StoreGUID = "store-1042",
                    StoreName = "Store 1042",
                    StoreCode = "1042",
                    IsActive = false,
                    IsDeleted = false,
                },
                new Store
                {
                    StoreGUID = "store-s001",
                    StoreName = "Store S001",
                    StoreCode = "S001",
                    IsActive = true,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
            var service = CreateStoreService();

            var result = await service.GetNextStoreCodeAsync();

            Assert.True(result.Success);
            Assert.Equal("1043", result.Data);
        }

        [Fact]
        public async Task CreateStoreAsync_WhenStoreCodeExists_ReturnsDuplicateAndDoesNotInsert()
        {
            var service = CreateStoreService();
            var first = await service.CreateStoreAsync(new CreateStoreDto
            {
                StoreName = "First Store",
                StoreCode = "1043",
            });

            var duplicate = await service.CreateStoreAsync(new CreateStoreDto
            {
                StoreName = "Duplicate Store",
                StoreCode = "1043",
            });

            Assert.True(first.Success);
            Assert.False(duplicate.Success);
            Assert.Equal("DUPLICATE_STORE_CODE", duplicate.ErrorCode);
            var count = await _db.Queryable<Store>()
                .Where(store => store.StoreCode == "1043")
                .CountAsync();
            Assert.Equal(1, count);
        }

        [Fact]
        public async Task CreateStoreAsync_TrimsStoreCodeBeforeDuplicateCheckAndInsert()
        {
            var service = CreateStoreService();
            var first = await service.CreateStoreAsync(new CreateStoreDto
            {
                StoreName = "Trimmed Store",
                StoreCode = " 1044 ",
            });

            var duplicate = await service.CreateStoreAsync(new CreateStoreDto
            {
                StoreName = "Duplicate Trimmed Store",
                StoreCode = "1044",
            });

            Assert.True(first.Success);
            Assert.Equal("1044", first.Data!.StoreCode);
            Assert.False(duplicate.Success);
            Assert.Equal("DUPLICATE_STORE_CODE", duplicate.ErrorCode);
            var count = await _db.Queryable<Store>()
                .Where(store => store.StoreCode == "1044")
                .CountAsync();
            Assert.Equal(1, count);
        }

        [Fact]
        public async Task SyncStoreToHqAsync_WhenStoreMissing_ReturnsStoreNotFound()
        {
            var service = CreateStoreServiceWithHq();

            var result = await service.SyncStoreToHqAsync("missing-store");

            Assert.False(result.Success);
            Assert.Equal("STORE_NOT_FOUND", result.ErrorCode);
        }

        [Fact]
        public async Task SyncStoreToHqAsync_WhenBranchMissing_InsertsHqBranch()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-sync-new",
                StoreCode = "1077",
                StoreName = "Sync New Store",
                ABN = "11 222 333 444",
                Phone = "02 1111 2222",
                Address = "1 Sync Street",
                IsActive = true,
            }).ExecuteCommandAsync();
            var service = CreateStoreServiceWithHq();

            var result = await service.SyncStoreToHqAsync("store-sync-new");

            Assert.True(result.Success);
            var branch = await _hqDb.Queryable<HqBranch>()
                .SingleAsync(item => item.BranchCode == "1077");
            Assert.Equal("Sync New Store", branch.BranchName);
            Assert.Equal("11 222 333 444", branch.BusinessNumber);
            Assert.Equal("02 1111 2222", branch.Phone);
            Assert.Equal("1 Sync Street", branch.Address);
        }

        [Fact]
        public async Task SyncStoreToHqAsync_WhenBranchExists_UpdatesMappedFieldsOnly()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-sync-existing",
                StoreCode = "1088",
                StoreName = "Updated Store",
                ABN = "55 666 777 888",
                Phone = "02 3333 4444",
                Address = "8 Updated Road",
                IsActive = true,
            }).ExecuteCommandAsync();
            await _hqDb.Insertable(new HqBranch
            {
                BranchCode = "1088",
                BranchName = "Old Store",
                BusinessNumber = "old-abn",
                Phone = "old-phone",
                ManagerName = "HQ Manager",
                Address = "Old Address",
            }).ExecuteCommandAsync();
            var service = CreateStoreServiceWithHq();

            var result = await service.SyncStoreToHqAsync("store-sync-existing");

            Assert.True(result.Success);
            var branch = await _hqDb.Queryable<HqBranch>()
                .SingleAsync(item => item.BranchCode == "1088");
            Assert.Equal("Updated Store", branch.BranchName);
            Assert.Equal("55 666 777 888", branch.BusinessNumber);
            Assert.Equal("02 3333 4444", branch.Phone);
            Assert.Equal("8 Updated Road", branch.Address);
            Assert.Equal("HQ Manager", branch.ManagerName);
            Assert.Equal(1, await _hqDb.Queryable<HqBranch>().CountAsync());
        }

        [Fact]
        public async Task SyncStoreToHqAsync_WhenSameBranchSyncedConcurrently_RemainsSingleHqBranch()
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-sync-concurrent",
                StoreCode = "1099",
                StoreName = "Concurrent Store",
                ABN = "99 888 777 666",
                Phone = "02 5555 6666",
                Address = "9 Concurrent Way",
                IsActive = true,
            }).ExecuteCommandAsync();
            var clients = Enumerable.Range(0, 8)
                .Select(_ => new
                {
                    Local = CreateSqliteSqlSugarClient(_sqliteConnection.ConnectionString),
                    Hq = CreateSqliteSqlSugarClient(_hqSqliteConnection.ConnectionString),
                })
                .ToList();

            try
            {
                var tasks = clients
                    .Select(client =>
                    {
                        var service = new StoreService(
                            CreateSqlSugarContext(client.Local),
                            NullLogger<StoreService>.Instance,
                            CreateHqSqlSugarContext(client.Hq)
                        );
                        return service.SyncStoreToHqAsync("store-sync-concurrent");
                    })
                    .ToArray();

                var results = await Task.WhenAll(tasks);

                Assert.All(results, result => Assert.True(result.Success));
                var count = await _hqDb.Queryable<HqBranch>()
                    .Where(item => item.BranchCode == "1099")
                    .CountAsync();
                Assert.Equal(1, count);
                var branch = await _hqDb.Queryable<HqBranch>()
                    .SingleAsync(item => item.BranchCode == "1099");
                Assert.Equal("Concurrent Store", branch.BranchName);
                Assert.Equal("99 888 777 666", branch.BusinessNumber);
                Assert.Equal("02 5555 6666", branch.Phone);
                Assert.Equal("9 Concurrent Way", branch.Address);
            }
            finally
            {
                foreach (var client in clients)
                {
                    client.Local.Dispose();
                    client.Hq.Dispose();
                }
            }
        }

        [Fact]
        public async Task SetPrimaryUserAsync_UpdatesOnlyTargetStoreManagementFlag()
        {
            await SeedUsersAndStoresAsync();
            await InsertUserStoreAsync("user-1", "store-1", false);
            await InsertUserStoreAsync("user-1", "store-2", true);
            var service = CreateStoreService(await SeedAdminScopeAsync());

            var enableResult = await service.SetPrimaryUserAsync("store-1", "user-1", true);

            Assert.True(enableResult.Success);
            Assert.True((await FindUserStoreAsync("user-1", "store-1")).IsPrimary);
            Assert.True((await FindUserStoreAsync("user-1", "store-2")).IsPrimary);

            var disableResult = await service.SetPrimaryUserAsync("store-2", "user-1", false);

            Assert.True(disableResult.Success);
            Assert.True((await FindUserStoreAsync("user-1", "store-1")).IsPrimary);
            Assert.False((await FindUserStoreAsync("user-1", "store-2")).IsPrimary);
        }

        [Fact]
        public async Task SetPrimaryUserAsync_NonAdminCannotGrantManagementRelationship()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateStoreService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.SetPrimaryUserAsync(
                "store-1",
                "scoped-user",
                true
            );

            Assert.False(result.Success);
            Assert.Equal("ADMIN_REQUIRED", result.ErrorCode);
            Assert.False((await FindUserStoreAsync("scoped-user", "store-1")).IsPrimary);
        }

        [Theory]
        [InlineData("manager-1", "store-1", "ADMIN_REQUIRED")]
        [InlineData("privileged-target", "store-1", "ADMIN_REQUIRED")]
        [InlineData("foreign-user", "store-2", "ADMIN_REQUIRED")]
        public async Task SetPrimaryUserAsync_GrantProtectsSensitiveTargets(
            string targetUserGuid,
            string storeGuid,
            string expectedErrorCode
        )
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(CreateUser("privileged-target", "privileged@example.test"))
                .ExecuteCommandAsync();
            await _db.Insertable(new Role
            {
                RoleGUID = "role-admin",
                RoleName = "Admin",
                IsActive = true,
            }).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("privileged-target", "role-admin"))
                .ExecuteCommandAsync();
            await _db.Insertable(CreateUserStore("privileged-target", "store-1", false))
                .ExecuteCommandAsync();
            var service = CreateStoreService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.SetPrimaryUserAsync(storeGuid, targetUserGuid, true);

            Assert.False(result.Success);
            Assert.Equal(expectedErrorCode, result.ErrorCode);
        }

        [Fact]
        public async Task SetPrimaryUserAsync_AdminGrantAndRevokeSynchronizeStoreManagerRole()
        {
            await SeedUsersAndStoresAsync();
            await InsertUserStoreAsync("user-1", "store-1", false);
            await _db.Insertable(CreateUser("admin-1", "admin@example.test")).ExecuteCommandAsync();
            await _db.Insertable(new Role
            {
                RoleGUID = "role-admin",
                RoleName = "Admin",
                IsActive = true,
            }).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("admin-1", "role-admin")).ExecuteCommandAsync();
            var service = CreateStoreService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    IsAdmin = true,
                    UserGuid = "admin-1",
                })
            );

            var grantResult = await service.SetPrimaryUserAsync("store-1", "user-1", true);

            Assert.True(grantResult.Success);
            Assert.True(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.RoleGUID == "role-store-manager"
            ));

            var revokeResult = await service.SetPrimaryUserAsync("store-1", "user-1", false);

            Assert.True(revokeResult.Success);
            Assert.False(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.RoleGUID == "role-store-manager"
            ));
        }

        [Fact]
        public async Task SetPrimaryUserAsync_NonAdminCannotDowngradeOutsideConcreteStoreScope()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateStoreService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.SetPrimaryUserAsync("store-2", "foreign-user", false);

            Assert.False(result.Success);
            Assert.Equal("ADMIN_REQUIRED", result.ErrorCode);
        }

        [Fact]
        public async Task SetPrimaryUserAsync_InactiveManagerRoleDoesNotBlockActiveCanonicalRole()
        {
            await SeedUsersAndStoresAsync();
            await InsertUserStoreAsync("user-1", "store-1", false);
            await _db.Insertable(new Role
            {
                RoleGUID = "role-inactive-manager",
                RoleName = "店长",
                IsActive = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("user-1", "role-inactive-manager"))
                .ExecuteCommandAsync();
            await _db.Insertable(CreateUser("admin-1", "admin@example.test"))
                .ExecuteCommandAsync();
            await _db.Insertable(new Role
            {
                RoleGUID = "role-admin",
                RoleName = "Admin",
                IsActive = true,
            }).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("admin-1", "role-admin")).ExecuteCommandAsync();
            var service = CreateStoreService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    IsAdmin = true,
                    UserGuid = "admin-1",
                })
            );

            var result = await service.SetPrimaryUserAsync("store-1", "user-1", true);

            Assert.True(result.Success);
            Assert.True(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.RoleGUID == "role-store-manager"
            ));
        }

        [Fact]
        public async Task BatchManageUsersAsync_NonAdminCannotRemoveOutsideConcreteStoreScope()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateStoreService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.BatchManageUsersAsync(
                "store-2",
                new BatchUserOperationDto
                {
                    Action = "remove",
                    UserGUIDs = new List<string> { "foreign-user" },
                }
            );

            Assert.False(result.Success);
            Assert.Equal("ADMIN_REQUIRED", result.ErrorCode);
        }

        [Fact]
        public async Task BatchManageUsersAsync_AdminRemovalSynchronizesDerivedManagerRole()
        {
            await SeedUsersAndStoresAsync();
            await InsertUserStoreAsync("user-1", "store-1", true);
            await _db.Insertable(CreateUserRole("user-1", "role-store-manager"))
                .ExecuteCommandAsync();
            await _db.Insertable(CreateUser("admin-1", "admin@example.test"))
                .ExecuteCommandAsync();
            await _db.Insertable(new Role
            {
                RoleGUID = "role-admin",
                RoleName = "Admin",
                IsActive = true,
            }).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("admin-1", "role-admin")).ExecuteCommandAsync();
            var service = CreateStoreService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    IsAdmin = true,
                    UserGuid = "admin-1",
                })
            );

            var result = await service.BatchManageUsersAsync(
                "store-1",
                new BatchUserOperationDto
                {
                    Action = "remove",
                    UserGUIDs = new List<string> { "user-1" },
                }
            );

            Assert.True(result.Success);
            Assert.False(await _db.Queryable<UserRole>().AnyAsync(item =>
                item.UserGUID == "user-1" && item.RoleGUID == "role-store-manager"
            ));
        }

        [Fact]
        public async Task GetStoreUsersAsync_ReturnsManagementFlag()
        {
            await SeedUsersAndStoresAsync();
            await InsertUserStoreAsync("user-1", "store-1", true);
            var service = CreateStoreService();

            var result = await service.GetStoreUsersAsync(
                "store-1",
                new UserQueryDto { Page = 1, PageSize = 20 }
            );

            Assert.True(result.Success);
            var data = result.Data;
            Assert.NotNull(data);
            var items = data!.Items;
            Assert.NotNull(items);
            Assert.Single(items);
            Assert.True(items![0].IsPrimary);
        }

        [Fact]
        public async Task GetStoreByGuidAsync_ReturnsManagementFlagForLinkedUsers()
        {
            await SeedUsersAndStoresAsync();
            await InsertUserStoreAsync("user-1", "store-1", true);
            var service = CreateStoreService();

            var result = await service.GetStoreByGuidAsync("store-1");

            Assert.True(result.Success);
            Assert.Single(result.Data!.Users!);
            Assert.True(result.Data.Users[0].IsPrimary);
        }

        [Theory]
        [InlineData("store-1", true)]
        [InlineData("inactive-store", false)]
        public async Task GetStoreByGuidAsync_ReturnsPersistedIsActiveForEnabledAndInactiveStores(
            string storeGuid,
            bool expectedIsActive
        )
        {
            await SeedUsersAndStoresAsync();
            await _db.Insertable(new Store
            {
                StoreGUID = "inactive-store",
                StoreCode = "INACTIVE-DETAIL",
                StoreName = "Inactive Detail Store",
                IsActive = false,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            var service = CreateStoreService();

            var result = await service.GetStoreByGuidAsync(storeGuid);

            Assert.True(result.Success);
            Assert.Equal(expectedIsActive, result.Data!.IsActive);
        }

        [Fact]
        public async Task GetStoresAsync_FiltersByBrandNameAndSortsStoreColumns()
        {
            await SeedStoresForListQueryAsync();
            await _db.Insertable(new[]
            {
                CreateUserStore("user-a", "store-hot-a", false),
                CreateUserStore("user-b", "store-hot-b", false),
                CreateUserStore("user-c", "store-hot-b", false),
            }).ExecuteCommandAsync();
            var service = CreateStoreService();

            var filtered = await service.GetStoresAsync(
                new StoreQueryDto { BrandName = "Hot Bargain", Page = 1, PageSize = 20 }
            );

            Assert.True(filtered.Success);
            var filteredData = filtered.Data!;
            var filteredItems = filteredData.Items!;
            Assert.Equal(2, filteredData.Total);
            Assert.All(filteredItems, item => Assert.Equal("Hot Bargain", item.BrandName));

            var brandSorted = await service.GetStoresAsync(
                new StoreQueryDto
                {
                    Page = 1,
                    PageSize = 20,
                    SortField = "BrandName",
                    SortOrder = "desc",
                }
            );

            Assert.True(brandSorted.Success);
            var brandSortedItems = brandSorted.Data!.Items!;
            Assert.Equal("Other Brand", brandSortedItems[0].BrandName);

            var userCountSorted = await service.GetStoresAsync(
                new StoreQueryDto
                {
                    Page = 1,
                    PageSize = 20,
                    SortField = "totalUsers",
                    SortOrder = "desc",
                }
            );

            Assert.True(userCountSorted.Success);
            var userCountSortedItems = userCountSorted.Data!.Items!;
            Assert.Equal(
                new[] { "store-hot-b", "store-hot-a", "store-other" },
                userCountSortedItems.Select(item => item.StoreGUID).ToArray()
            );
            Assert.Equal(new[] { 2, 1, 0 }, userCountSortedItems.Select(item => item.TotalUsers).ToArray());
        }

        [Theory]
        [InlineData(StoreTimeZonePolicy.Brisbane, 2)]
        [InlineData(StoreTimeZonePolicy.Sydney, 1)]
        [InlineData(StoreTimeZonePolicy.Melbourne, 1)]
        public async Task GetStoresAsync_FiltersByEachStandardTimeZoneId(
            string timeZoneId,
            int expectedTotal
        )
        {
            await SeedStoresForTimeZoneFilterAsync();
            var service = CreateStoreService();

            var filtered = await service.GetStoresAsync(
                new StoreQueryDto
                {
                    TimeZoneId = timeZoneId,
                    Page = 1,
                    PageSize = 20,
                }
            );

            Assert.True(filtered.Success, filtered.Message);
            Assert.Equal(expectedTotal, filtered.Data!.Total);
            Assert.All(
                filtered.Data.Items!,
                item => Assert.Equal(timeZoneId, item.TimeZoneId)
            );
        }

        [Fact]
        public async Task GetStoresAsync_BlankOrMissingTimeZoneIdDoesNotFilter()
        {
            await SeedStoresForTimeZoneFilterAsync();
            var service = CreateStoreService();

            var unfiltered = await service.GetStoresAsync(
                new StoreQueryDto { Page = 1, PageSize = 50 }
            );
            Assert.True(unfiltered.Success, unfiltered.Message);
            Assert.Equal(6, unfiltered.Data!.Total);

            var whitespace = await service.GetStoresAsync(
                new StoreQueryDto { TimeZoneId = "   ", Page = 1, PageSize = 50 }
            );
            Assert.True(whitespace.Success, whitespace.Message);
            Assert.Equal(6, whitespace.Data!.Total);
        }

        [Fact]
        public async Task GetStoresAsync_FiltersByUnsetTimeZoneSentinel()
        {
            await SeedStoresForTimeZoneFilterAsync();
            var service = CreateStoreService();

            var result = await service.GetStoresAsync(
                new StoreQueryDto
                {
                    TimeZoneId = "  __unset__  ",
                    Page = 1,
                    PageSize = 20,
                }
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.Data!.Total);
            var storeGuids = result.Data.Items!
                .Select(item => item.StoreGUID)
                .OrderBy(storeGuid => storeGuid, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "store-tz-empty", "store-tz-null" }, storeGuids);
        }

        [Fact]
        public async Task GetStoresAsync_TimeZoneFilterCombinesWithBrandAndActiveAndKeepsPagedTotal()
        {
            await SeedStoresForTimeZoneFilterAsync();
            var service = CreateStoreService();

            var combined = await service.GetStoresAsync(
                new StoreQueryDto
                {
                    TimeZoneId = StoreTimeZonePolicy.Brisbane,
                    BrandName = "Hot Bargain",
                    IsActive = true,
                    Page = 1,
                    PageSize = 20,
                }
            );

            Assert.True(combined.Success, combined.Message);
            Assert.Equal(1, combined.Data!.Total);
            var combinedItem = Assert.Single(combined.Data.Items!);
            Assert.Equal("store-tz-bris-active", combinedItem.StoreGUID);

            var firstPage = await service.GetStoresAsync(
                new StoreQueryDto
                {
                    TimeZoneId = StoreTimeZonePolicy.Brisbane,
                    Page = 1,
                    PageSize = 1,
                }
            );
            Assert.True(firstPage.Success, firstPage.Message);
            Assert.Equal(2, firstPage.Data!.Total);
            Assert.Single(firstPage.Data.Items!);

            var secondPage = await service.GetStoresAsync(
                new StoreQueryDto
                {
                    TimeZoneId = StoreTimeZonePolicy.Brisbane,
                    Page = 2,
                    PageSize = 1,
                }
            );
            Assert.True(secondPage.Success, secondPage.Message);
            Assert.Equal(2, secondPage.Data!.Total);
            Assert.Single(secondPage.Data.Items!);
        }

        [Fact]
        public async Task GetUsersOptimizedAsync_WhenLinkedStoreInactive_IncludesStoreName()
        {
            await SeedInactiveStoreUserAsync();
            var service = CreateUserService();

            var result = await service.GetUsersOptimizedAsync(
                new UserQueryDto { Page = 1, PageSize = 20 }
            );

            Assert.True(result.Success);
            var user = Assert.Single(result.Data!.Items!, item => item.UserGUID == "inactive-store-user");
            Assert.Contains("Inactive Store", user.StoreNames);
        }

        [Fact]
        public async Task GetUserStoresAsync_WhenLinkedStoreInactive_ReturnsStoreWithInactiveStatus()
        {
            await SeedInactiveStoreUserAsync();
            var service = CreateUserService();

            var result = await service.GetUserStoresAsync("inactive-store-user");

            Assert.True(result.Success);
            var store = Assert.Single(result.Data!);
            Assert.Equal("inactive-store", store.StoreGUID);
            Assert.False(store.IsActive);
        }

        [Theory]
        [InlineData("店长", false)]
        [InlineData("店长", true)]
        [InlineData("经理", false)]
        [InlineData("经理", true)]
        public async Task GetUsers_WhenChineseStoreManagerHasManagedStores_OnlyReturnsScopedUsers(
            string roleName,
            bool useOptimized
        )
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await SeedAliasedStoreManagerAsync(
                "manager-cn-1",
                "role-store-manager-cn",
                roleName,
                true
            );

            var service = CreateUserService(
                CreateCurrentUserManageableStoreScopeService("manager-cn-1", roleName)
            );

            var result = await GetUsersAsync(
                service,
                useOptimized,
                new UserQueryDto { Page = 1, PageSize = 20 }
            );

            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            Assert.Equal(3, result.Data!.Total);
            Assert.Equal(
                new[] { "dual-user", "manager-cn-1", "scoped-user" },
                result.Data.Items!
                    .Select(item => item.UserGUID)
                    .OrderBy(item => item)
                    .ToArray()
            );
            Assert.DoesNotContain(result.Data.Items!, item => item.UserGUID == "foreign-user");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetUsers_WhenChineseStoreManagerHasNoManagedStores_ReturnsEmptyPage(
            bool useOptimized
        )
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await SeedAliasedStoreManagerAsync(
                "manager-cn-no-store",
                "role-store-manager-manager",
                "经理",
                false
            );

            var service = CreateUserService(
                CreateCurrentUserManageableStoreScopeService("manager-cn-no-store", "经理")
            );

            var result = await GetUsersAsync(
                service,
                useOptimized,
                new UserQueryDto { Page = 1, PageSize = 20 }
            );

            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            Assert.Empty(result.Data!.Items!);
            Assert.Equal(0, result.Data.Total);
        }

        [Theory]
        [InlineData("Admin")]
        [InlineData("管理员")]
        [InlineData("SuperAdmin")]
        [InlineData("超级管理员")]
        [InlineData("WarehouseManager")]
        [InlineData("仓库经理")]
        [InlineData("Warehouse")]
        [InlineData("仓库管理员")]
        [InlineData("WarehouseAdmin")]
        public async Task GetScopeAsync_WhenCurrentUserIsUnrestrictedAlias_ReturnsAdminScope(
            string roleName
        )
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(CreateUser("admin-1", "admin@example.com")).ExecuteCommandAsync();
            var service = CreateCurrentUserManageableStoreScopeService("admin-1", roleName);

            var scope = await service.GetScopeAsync();

            Assert.True(scope.IsAllowed);
            Assert.True(scope.IsAuthenticated);
            Assert.True(scope.IsAdmin);
            Assert.Equal("admin-1", scope.UserGuid);
        }

        [Theory]
        [InlineData("Admin")]
        [InlineData("管理员")]
        [InlineData("SuperAdmin")]
        [InlineData("超级管理员")]
        public async Task GetUserStores_WhenPrivilegedAliasViewsOtherUser_ReturnsStores(
            string roleName
        )
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await SeedAliasedStoreManagerAsync(
                "operator-1",
                $"role-operator-{roleName}",
                roleName,
                false
            );
            var controller = new UsersController(
                CreateUserService(),
                Mock.Of<IRoleService>(),
                NullLogger<UsersController>.Instance,
                CreateSqlSugarContext(_db)
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreatePrincipal("operator-1", roleName),
                    },
                },
            };

            var result = await controller.GetUserStores(
                "scoped-user",
                new FakeCurrentUserService("operator-1")
            );

            Assert.IsType<OkObjectResult>(result);
        }

        [Theory]
        [InlineData("WarehouseManager")]
        [InlineData("仓库经理")]
        [InlineData("Warehouse")]
        [InlineData("仓库管理员")]
        [InlineData("WarehouseAdmin")]
        public async Task GetUserStores_WarehouseAliasWithoutPrimaryStoreCannotBypassScope(
            string roleName
        )
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await SeedAliasedStoreManagerAsync(
                "warehouse-1",
                $"role-warehouse-{roleName}",
                roleName,
                false
            );
            var roleService = new Mock<IRoleService>();
            roleService
                .Setup(service => service.UserHasPermissionAsync("warehouse-1", It.IsAny<string>()))
                .ReturnsAsync(ApiResponse<bool>.OK(true));
            var controller = new UsersController(
                CreateUserService(),
                roleService.Object,
                NullLogger<UsersController>.Instance,
                CreateSqlSugarContext(_db)
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreatePrincipal("warehouse-1", roleName),
                    },
                },
            };

            var result = await controller.GetUserStores(
                "scoped-user",
                new FakeCurrentUserService("warehouse-1")
            );

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task GetUserStores_WarehouseAliasUsesPrimaryStoreIntersection()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await SeedAliasedStoreManagerAsync(
                "warehouse-1",
                "role-warehouse-scoped",
                "WarehouseManager",
                true
            );
            var roleService = new Mock<IRoleService>();
            roleService
                .Setup(service => service.UserHasPermissionAsync(
                    "warehouse-1",
                    Permissions.Users.ManageStores
                ))
                .ReturnsAsync(ApiResponse<bool>.OK(true));
            var controller = new UsersController(
                CreateUserService(),
                roleService.Object,
                NullLogger<UsersController>.Instance,
                CreateSqlSugarContext(_db)
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreatePrincipal("warehouse-1", "WarehouseManager"),
                    },
                },
            };

            var result = await controller.GetUserStores(
                "dual-user",
                new FakeCurrentUserService("warehouse-1")
            );

            Assert.IsType<ForbidResult>(result);
        }

        [Theory]
        [InlineData("Admin")]
        [InlineData("管理员")]
        [InlineData("SuperAdmin")]
        [InlineData("超级管理员")]
        [InlineData("WarehouseManager")]
        [InlineData("仓库经理")]
        [InlineData("Warehouse")]
        [InlineData("仓库管理员")]
        [InlineData("WarehouseAdmin")]
        public async Task GetUserStoresAsync_PrivilegedTargetAliasesReturnAllStores(
            string roleName
        )
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await SeedAliasedStoreManagerAsync(
                "privileged-target",
                $"role-{roleName}",
                roleName,
                false
            );

            var result = await CreateUserService().GetUserStoresAsync("privileged-target");

            Assert.True(result.Success);
            Assert.Equal(2, result.Data!.Count);
        }

        [Theory]
        [InlineData(Permissions.Users.ManageStores)]
        [InlineData(Permissions.Users.ManagePosTerminalPermissions)]
        public async Task GetUserStores_WhenStoreManagerHasPermission_ReturnsManagedStoreIntersection(
            string grantedPermission
        )
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var roleService = new Mock<IRoleService>();
            roleService
                .Setup(service => service.UserHasPermissionAsync("manager-1", It.IsAny<string>()))
                .ReturnsAsync((string _, string permission) =>
                    ApiResponse<bool>.OK(permission == grantedPermission)
                );
            var controller = new UsersController(
                CreateUserService(),
                roleService.Object,
                NullLogger<UsersController>.Instance,
                CreateSqlSugarContext(_db)
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreatePrincipal("manager-1", "StoreManager"),
                    },
                },
            };
            var scopeService = new FakeManageableStoreScopeService(
                new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                }
            );

            var method = typeof(UsersController).GetMethod(nameof(UsersController.GetUserStores));
            var task = Assert.IsAssignableFrom<Task<IActionResult>>(
                method!.Invoke(
                    controller,
                    new object?[]
                    {
                        "dual-user",
                        new FakeCurrentUserService("manager-1"),
                        scopeService,
                    }
                )
            );
            var result = Assert.IsType<OkObjectResult>(await task);
            var response = Assert.IsType<ApiResponse<List<UserStoreDto>>>(result.Value);
            var store = Assert.Single(response.Data!);
            Assert.Equal("store-1", store.StoreGUID);
        }

        [Theory]
        [InlineData("foreign-user", true)]
        [InlineData("dual-user", false)]
        public async Task GetUserStores_WhenStoreManagerIsOutOfScopeOrMissingPermission_ReturnsForbid(
            string targetUserGuid,
            bool hasPermission
        )
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var roleService = new Mock<IRoleService>();
            roleService
                .Setup(service => service.UserHasPermissionAsync(
                    "manager-1",
                    Permissions.Users.ManagePosTerminalPermissions
                ))
                .ReturnsAsync(ApiResponse<bool>.OK(hasPermission));
            var controller = new UsersController(
                CreateUserService(),
                roleService.Object,
                NullLogger<UsersController>.Instance,
                CreateSqlSugarContext(_db)
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreatePrincipal("manager-1", "StoreManager"),
                    },
                },
            };

            var result = await controller.GetUserStores(
                targetUserGuid,
                new FakeCurrentUserService("manager-1"),
                new FakeManageableStoreScopeService(
                    new CurrentUserManageableStoreScope
                    {
                        IsAllowed = true,
                        IsAuthenticated = true,
                        UserGuid = "manager-1",
                        StoreGuids = new[] { "store-1" },
                    }
                )
            );

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task GetUserStores_WhenStoreManagerTargetsHighPrivilegeUser_ReturnsForbid()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await SeedAliasedStoreManagerAsync(
                "manager-2",
                "role-store-manager-2",
                "StoreManager",
                true
            );
            var roleService = new Mock<IRoleService>();
            roleService
                .Setup(service => service.UserHasPermissionAsync(
                    "manager-2",
                    Permissions.Users.ManagePosTerminalPermissions
                ))
                .ReturnsAsync(ApiResponse<bool>.OK(true));
            var controller = new UsersController(
                CreateUserService(),
                roleService.Object,
                NullLogger<UsersController>.Instance,
                CreateSqlSugarContext(_db)
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreatePrincipal("manager-2", "StoreManager"),
                    },
                },
            };

            var result = await controller.GetUserStores(
                "manager-1",
                new FakeCurrentUserService("manager-2"),
                new FakeManageableStoreScopeService(
                    new CurrentUserManageableStoreScope
                    {
                        IsAllowed = true,
                        IsAuthenticated = true,
                        UserGuid = "manager-2",
                        StoreGuids = new[] { "store-1" },
                    }
                )
            );

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task GetUserRoles_WhenTargetOutsidePrimaryStoreScope_ReturnsForbid()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var roleService = new Mock<IRoleService>();
            roleService
                .Setup(service => service.UserHasPermissionAsync(
                    "manager-1",
                    Permissions.Users.ManageRoles
                ))
                .ReturnsAsync(ApiResponse<bool>.OK(true));
            var controller = new UsersController(
                CreateUserService(),
                roleService.Object,
                NullLogger<UsersController>.Instance,
                CreateSqlSugarContext(_db)
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreatePrincipal("manager-1", "StoreManager"),
                    },
                },
            };

            var result = await controller.GetUserRoles("foreign-user");

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task GetUserRolesAsync_WhenTargetAlsoBelongsToOutsideStore_ReturnsScopeDenied()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.GetUserRolesAsync("dual-user");

            Assert.False(result.Success);
            Assert.Equal("USER_SCOPE_DENIED", result.ErrorCode);
        }

        [Fact]
        public async Task GetUserRolesAsync_WhenReadingSelf_PreservesActiveRoleContract()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new Role
            {
                RoleGUID = "role-inactive-self",
                RoleName = "InactiveSelfRole",
                IsActive = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole("viewer-1", "role-inactive-self"))
                .ExecuteCommandAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = false,
                    IsAuthenticated = true,
                    UserGuid = "viewer-1",
                })
            );

            var result = await service.GetUserRolesAsync("viewer-1");

            Assert.True(result.Success);
            var role = Assert.Single(result.Data!);
            Assert.Equal("Viewer", role.RoleName);
        }

        [Fact]
        public async Task GetUserRolesAsync_WhenStoreManagerReadsSelf_ReturnsStoreManagerRole()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.GetUserRolesAsync("manager-1");

            Assert.True(result.Success);
            var role = Assert.Single(result.Data!);
            Assert.Equal("StoreManager", role.RoleName);
        }

        [Fact]
        public async Task GetUserRolesAsync_WhenStoreManagerReadsTarget_ReturnsOnlyActiveEmployeeRoles()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await _db.Insertable(new[]
            {
                new Role
                {
                    RoleGUID = "role-employee-visible",
                    RoleName = "StoreStaff",
                    Description = "员工角色",
                    IsActive = true,
                },
                new Role
                {
                    RoleGUID = "role-custom-hidden",
                    RoleName = "CustomOrdinaryRole",
                    Description = "不应向店长泄露",
                    IsActive = true,
                },
                new Role
                {
                    RoleGUID = "role-employee-inactive",
                    RoleName = "Employee",
                    Description = "已停用员工角色",
                    IsActive = false,
                },
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                CreateUserRole("scoped-user", "role-employee-visible"),
                CreateUserRole("scoped-user", "role-custom-hidden"),
                CreateUserRole("scoped-user", "role-employee-inactive"),
            }).ExecuteCommandAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.GetUserRolesAsync("scoped-user");

            Assert.True(result.Success);
            var role = Assert.Single(result.Data!);
            Assert.Equal("StoreStaff", role.RoleName);
            Assert.Equal("员工角色", role.Description);
        }

        [Fact]
        public async Task GetUserRolesAsync_WhenAdminReadsTarget_ReturnsAllActiveRoles()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var adminScope = await SeedAdminScopeAsync();
            await _db.Insertable(new[]
            {
                new Role
                {
                    RoleGUID = "role-admin-read-employee",
                    RoleName = "Employee",
                    IsActive = true,
                },
                new Role
                {
                    RoleGUID = "role-admin-read-custom",
                    RoleName = "CustomOrdinaryRole",
                    IsActive = true,
                },
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                CreateUserRole("scoped-user", "role-admin-read-employee"),
                CreateUserRole("scoped-user", "role-admin-read-custom"),
            }).ExecuteCommandAsync();

            var result = await CreateUserService(adminScope).GetUserRolesAsync("scoped-user");

            Assert.True(result.Success);
            Assert.Equal(
                new[] { "CustomOrdinaryRole", "Employee" },
                result.Data!.Select(item => item.RoleName).OrderBy(item => item).ToArray()
            );
        }

        [Fact]
        public void GetUserRolesAsync_查询契约_受限角色过滤必须与基础条件分离()
        {
            var sourcePath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "../../../../BlazorApp.Api/Services/UserService.cs"
            ));
            var source = File.ReadAllText(sourcePath);
            var methodStart = source.IndexOf(
                "public async Task<ApiResponse<List<RoleDto>>> GetUserRolesAsync",
                StringComparison.Ordinal
            );
            var methodEnd = source.IndexOf(
                "public async Task<ApiResponse<List<UserStoreDto>>> GetUserStoresAsync",
                methodStart,
                StringComparison.Ordinal
            );
            var methodSource = source[methodStart..methodEnd];

            Assert.Contains("var roleQuery =", methodSource, StringComparison.Ordinal);
            Assert.Contains("if (restrictedEmployeeRead)", methodSource, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "!restrictedEmployeeRead ||",
                methodSource,
                StringComparison.Ordinal
            );
        }

        [Fact]
        public async Task GetUserRoles_WhenStoreManagerTargetsManagedStoreStaff_ReturnsRoles()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var roleService = new Mock<IRoleService>();
            roleService
                .Setup(service => service.UserHasPermissionAsync(
                    "manager-1",
                    Permissions.Users.ManageRoles
                ))
                .ReturnsAsync(ApiResponse<bool>.OK(false));
            roleService
                .Setup(service => service.UserHasPermissionAsync(
                    "manager-1",
                    Permissions.Users.ManagePosTerminalPermissions
                ))
                .ReturnsAsync(ApiResponse<bool>.OK(true));
            var controller = new UsersController(
                CreateUserService(
                    new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                    {
                        IsAllowed = true,
                        IsAuthenticated = true,
                        UserGuid = "manager-1",
                        StoreGuids = new[] { "store-1" },
                    })
                ),
                roleService.Object,
                NullLogger<UsersController>.Instance,
                CreateSqlSugarContext(_db)
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreatePrincipal("manager-1", "StoreManager"),
                    },
                },
            };

            var result = await controller.GetUserRoles("scoped-user");

            Assert.IsType<OkObjectResult>(result);
        }

        [Fact]
        public async Task GetUserPermissionState_WhenTargetMissing_ReturnsNotFound()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var roleService = new Mock<IRoleService>();
            roleService
                .Setup(service => service.GetUserPermissionStateAsync("missing-user"))
                .ReturnsAsync(ApiResponse<UserPermissionStateDto>.Error(
                    "用户不存在",
                    "USER_NOT_FOUND"
                ));
            roleService
                .Setup(service => service.UserHasPermissionAsync(
                    "manager-1",
                    Permissions.Users.ManageRoles
                ))
                .ReturnsAsync(ApiResponse<bool>.OK(true));
            var controller = new UsersController(
                CreateUserService(),
                roleService.Object,
                NullLogger<UsersController>.Instance,
                CreateSqlSugarContext(_db)
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreatePrincipal("manager-1", "StoreManager"),
                    },
                },
            };

            var result = await controller.GetUserPermissionState("missing-user");

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task GetUserPermissionState_WhenReadingSelf_DoesNotRequireManageRoles()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var roleService = new Mock<IRoleService>();
            roleService
                .Setup(service => service.GetUserPermissionStateAsync("manager-1"))
                .ReturnsAsync(ApiResponse<UserPermissionStateDto>.OK(
                    new UserPermissionStateDto { UserGuid = "manager-1" }
                ));
            var controller = new UsersController(
                CreateUserService(),
                roleService.Object,
                NullLogger<UsersController>.Instance,
                CreateSqlSugarContext(_db)
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreatePrincipal("manager-1", "StoreManager"),
                    },
                },
            };

            var result = await controller.GetUserPermissionState("manager-1");

            Assert.IsType<OkObjectResult>(result);
            roleService.Verify(service => service.UserHasPermissionAsync(
                It.IsAny<string>(),
                It.IsAny<string>()
            ), Times.Never);
        }

        [Theory]
        [InlineData("USER_SCOPE_DENIED")]
        [InlineData("ROLE_DELEGATION_DENIED")]
        [InlineData("EMPLOYEE_ROLE_HAS_POS_PERMISSIONS")]
        public async Task AssignRolesToUser_WhenServiceRejectsLimitedAccess_ReturnsHttpForbid(
            string errorCode
        )
        {
            var userService = new Mock<IUserService>();
            userService
                .Setup(service => service.AssignRolesToUserAsync(
                    "target-1",
                    It.IsAny<UserRoleAssignmentDto>()
                ))
                .ReturnsAsync(ApiResponse<bool>.Error("无权修改该用户", errorCode));
            var controller = new UsersController(
                userService.Object,
                Mock.Of<IRoleService>(),
                NullLogger<UsersController>.Instance
            );

            var result = await controller.AssignRolesToUser(
                "target-1",
                new UserRoleAssignmentDto()
            );

            Assert.Equal(
                StatusCodes.Status403Forbidden,
                Assert.IsType<ObjectResult>(result).StatusCode
            );
        }

        [Fact]
        public async Task CreateUser_WhenServiceRequiresAdminReturnsHttp403WithStableErrorBody()
        {
            var userService = new Mock<IUserService>();
            userService
                .Setup(service => service.CreateUserAsync(It.IsAny<CreateUserDto>()))
                .ReturnsAsync(ApiResponse<UserDto>.Error(
                    "只有管理员可以维护全局角色与权限定义",
                    "ADMIN_REQUIRED"
                ));
            var controller = new UsersController(
                userService.Object,
                Mock.Of<IRoleService>(),
                NullLogger<UsersController>.Instance
            );

            var result = await controller.CreateUser(new CreateUserDto
            {
                Username = "new-user",
                Email = "new-user@example.test",
                Password = "secret1",
            });

            var forbidden = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
            Assert.Equal(
                "ADMIN_REQUIRED",
                Assert.IsType<ApiResponse<UserDto>>(forbidden.Value).ErrorCode
            );
        }

        [Fact]
        public async Task BatchManageUsers_DeleteRequiresUsersDeletePermission()
        {
            var roleService = new Mock<IRoleService>();
            roleService
                .Setup(service => service.UserHasPermissionAsync("manager-1", Permissions.Users.Delete))
                .ReturnsAsync(ApiResponse<bool>.OK(false));
            var userService = new Mock<IUserService>();
            var controller = new UsersController(
                userService.Object,
                roleService.Object,
                NullLogger<UsersController>.Instance
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreatePrincipal("manager-1", "StoreManager"),
                    },
                },
            };

            var result = await controller.BatchManageUsers(new BatchUserOperationDto
            {
                Operation = "delete",
                UserGuids = new List<string> { "scoped-user" },
            });

            Assert.Equal(
                StatusCodes.Status403Forbidden,
                Assert.IsType<ObjectResult>(result).StatusCode
            );
            userService.Verify(
                service => service.BatchManageUsersAsync(It.IsAny<BatchUserOperationDto>()),
                Times.Never
            );
        }

        [Fact]
        public async Task AssignStoresToUser_WhenTargetMissing_ReturnsHttpNotFound()
        {
            var userService = new Mock<IUserService>();
            userService
                .Setup(service => service.AssignStoresToUserAsync(
                    "missing-user",
                    It.IsAny<List<UserStoreAssignmentDto>>()
                ))
                .ReturnsAsync(ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND"));
            var controller = new UsersController(
                userService.Object,
                Mock.Of<IRoleService>(),
                NullLogger<UsersController>.Instance
            );

            var result = await controller.AssignStoresToUser(
                "missing-user",
                new List<UserStoreAssignmentDto>()
            );

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Theory]
        [InlineData("PERMISSION_ESCALATION_DENIED")]
        [InlineData("POS_PERMISSION_DELEGATION_DENIED")]
        [InlineData("EMPLOYEE_TARGET_REQUIRED")]
        public async Task AssignPermissionsToUser_WhenServiceRejectsLimitedAccess_ReturnsHttpForbid(
            string errorCode
        )
        {
            var roleService = new Mock<IRoleService>();
            roleService
                .Setup(service => service.AssignPermissionsToUserAsync(
                    "target-1",
                    It.IsAny<UserPermissionAssignmentDto>()
                ))
                .ReturnsAsync(ApiResponse<bool>.Error(
                    "不能授予当前账号自身不具备的权限",
                    errorCode
                ));
            var controller = new UsersController(
                Mock.Of<IUserService>(),
                roleService.Object,
                NullLogger<UsersController>.Instance
            );

            var result = await controller.AssignPermissionsToUser(
                "target-1",
                new UserPermissionAssignmentDto()
            );

            Assert.Equal(
                StatusCodes.Status403Forbidden,
                Assert.IsType<ObjectResult>(result).StatusCode
            );
        }

        [Fact]
        public async Task RemoveRoleFromUser_WhenServiceRejectsHighPrivilegeTargetReturnsHttpForbid()
        {
            var userService = new Mock<IUserService>();
            userService
                .Setup(service => service.RemoveRoleFromUserAsync("target-1", "role-1"))
                .ReturnsAsync(ApiResponse<bool>.Error(
                    "不能修改高权限账号",
                    "HIGH_PRIVILEGE_TARGET_DENIED"
                ));
            var controller = new UsersController(
                userService.Object,
                Mock.Of<IRoleService>(),
                NullLogger<UsersController>.Instance
            );

            var result = await controller.RemoveRoleFromUser("target-1", "role-1");

            Assert.Equal(
                StatusCodes.Status403Forbidden,
                Assert.IsType<ObjectResult>(result).StatusCode
            );
        }

        [Fact]
        public async Task RemoveStoreFromUser_WhenTargetMissingReturnsHttpNotFound()
        {
            var userService = new Mock<IUserService>();
            userService
                .Setup(service => service.RemoveStoreFromUserAsync("missing-user", "store-1"))
                .ReturnsAsync(ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND"));
            var controller = new UsersController(
                userService.Object,
                Mock.Of<IRoleService>(),
                NullLogger<UsersController>.Instance
            );

            var result = await controller.RemoveStoreFromUser("missing-user", "store-1");

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task StoresController_AddUserSecurityDenialReturnsHttpForbid()
        {
            var storeService = new Mock<IStoreService>();
            storeService
                .Setup(service => service.AddUserToStoreAsync(
                    "store-1",
                    It.IsAny<AddUserToStoreDto>()
                ))
                .ReturnsAsync(ApiResponse<bool>.Error("无权修改该用户", "USER_SCOPE_DENIED"));
            var controller = CreateStoresController(storeService.Object);

            var result = await controller.AddUserToStore(
                "store-1",
                new AddUserToStoreDto { UserGUID = "user-1" }
            );

            Assert.Equal(
                StatusCodes.Status403Forbidden,
                Assert.IsType<ObjectResult>(result).StatusCode
            );
        }

        [Fact]
        public async Task StoresController_RemoveMissingUserReturnsHttpNotFound()
        {
            var storeService = new Mock<IStoreService>();
            storeService
                .Setup(service => service.RemoveUserFromStoreAsync("store-1", "missing-user"))
                .ReturnsAsync(ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND"));
            var controller = CreateStoresController(storeService.Object);

            var result = await controller.RemoveUserFromStore("store-1", "missing-user");

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task StoresController_SetMissingRelationshipReturnsHttpNotFound()
        {
            var storeService = new Mock<IStoreService>();
            storeService
                .Setup(service => service.SetPrimaryUserAsync("store-1", "user-1", false))
                .ReturnsAsync(ApiResponse<bool>.Error(
                    "用户未关联到该分店",
                    "USER_NOT_ASSIGNED"
                ));
            var controller = CreateStoresController(storeService.Object);

            var result = await controller.SetPrimaryUser("store-1", "user-1", false);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task StoresController_BatchValidationFailureReturnsHttpBadRequest()
        {
            var storeService = new Mock<IStoreService>();
            storeService
                .Setup(service => service.BatchManageUsersAsync(
                    "store-1",
                    It.IsAny<BatchUserOperationDto>()
                ))
                .ReturnsAsync(ApiResponse<bool>.Error(
                    "不支持的批量操作",
                    "INVALID_BATCH_ACTION"
                ));
            var controller = CreateStoresController(storeService.Object);

            var result = await controller.BatchManageUsers(
                "store-1",
                new BatchUserOperationDto
                {
                    Action = "invalid",
                    UserGUIDs = new List<string> { "user-1" },
                }
            );

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task StoresController_BatchStoreValidationFailureReturnsHttpBadRequest()
        {
            var storeService = new Mock<IStoreService>();
            storeService
                .Setup(service => service.BatchUpdateStoresAsync(
                    It.IsAny<BatchUpdateStoresDto>()
                ))
                .ReturnsAsync(ApiResponse<BatchUpdateStoresResultDto>.Error(
                    "批量修改字段无效",
                    "INVALID_STORE_BATCH_FIELDS"
                ));
            var controller = CreateStoresController(storeService.Object);

            var result = await controller.BatchUpdateStores(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-1" },
                    Fields = new List<string> { "unknown" },
                }
            );

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Theory]
        [InlineData("STORE_BATCH_TARGET_INVALID")]
        [InlineData("STORE_BATCH_CONFLICT")]
        public async Task StoresController_BatchStoreConflictReturnsHttpConflict(string errorCode)
        {
            var storeService = new Mock<IStoreService>();
            storeService
                .Setup(service => service.BatchUpdateStoresAsync(
                    It.IsAny<BatchUpdateStoresDto>()
                ))
                .ReturnsAsync(ApiResponse<BatchUpdateStoresResultDto>.Error(
                    "批量修改冲突",
                    errorCode
                ));
            var controller = CreateStoresController(storeService.Object);

            var result = await controller.BatchUpdateStores(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-1" },
                    Fields = new List<string> { "brandName" },
                    BrandName = "Brand",
                }
            );

            Assert.IsType<ConflictObjectResult>(result);
        }

        [Fact]
        public async Task StoresController_BatchStoreUnexpectedFailureReturnsHttpInternalServerError()
        {
            var storeService = new Mock<IStoreService>();
            storeService
                .Setup(service => service.BatchUpdateStoresAsync(
                    It.IsAny<BatchUpdateStoresDto>()
                ))
                .ReturnsAsync(ApiResponse<BatchUpdateStoresResultDto>.Error(
                    "批量修改分店失败",
                    "BATCH_UPDATE_STORES_ERROR"
                ));
            var controller = CreateStoresController(storeService.Object);

            var result = await controller.BatchUpdateStores(
                new BatchUpdateStoresDto
                {
                    StoreGuids = new List<string> { "store-1" },
                    Fields = new List<string> { "brandName" },
                    BrandName = "Brand",
                }
            );

            Assert.Equal(
                StatusCodes.Status500InternalServerError,
                Assert.IsType<ObjectResult>(result).StatusCode
            );
        }

        [Theory]
        [InlineData(nameof(StoresController.BatchUpdateStores))]
        [InlineData(nameof(StoresController.AddUserToStore))]
        [InlineData(nameof(StoresController.RemoveUserFromStore))]
        [InlineData(nameof(StoresController.SetPrimaryUser))]
        [InlineData(nameof(StoresController.BatchManageUsers))]
        public void StoresController_UserMutationEndpointsRequireStoreEdit(string methodName)
        {
            var method = typeof(StoresController).GetMethod(methodName);

            var authorize = method?.GetCustomAttribute<AuthorizeAttribute>();

            Assert.NotNull(authorize);
            Assert.Equal(Permissions.Stores.Edit, authorize!.Policy);
        }

        [Fact]
        public void StoresController_BatchUpdateStoresUsesPatchBatchRoute()
        {
            var method = typeof(StoresController).GetMethod(
                nameof(StoresController.BatchUpdateStores)
            );

            var route = method?.GetCustomAttribute<HttpPatchAttribute>();

            Assert.NotNull(route);
            Assert.Equal("batch", route!.Template);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetUsers_WhenStoreManagerHasManagedStores_OnlyReturnsScopedUsers(
            bool useOptimized
        )
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(
                    new CurrentUserManageableStoreScope
                    {
                        IsAllowed = true,
                        IsAuthenticated = true,
                        UserGuid = "manager-1",
                        StoreGuids = new[] { "store-1" },
                        StoreCodes = new[] { "S001" },
                    }
                )
            );

            var result = await GetUsersAsync(
                service,
                useOptimized,
                new UserQueryDto { Page = 1, PageSize = 20 }
            );

            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            Assert.Equal(3, result.Data!.Total);
            Assert.Equal(
                new[] { "dual-user", "manager-1", "scoped-user" },
                result.Data.Items!
                    .Select(item => item.UserGUID)
                    .OrderBy(item => item)
                    .ToArray()
            );
            Assert.DoesNotContain(
                result.Data.Items!,
                item => item.UserGUID == "soft-deleted-scoped-user"
            );

            var dualUser = Assert.Single(result.Data.Items!, item => item.UserGUID == "dual-user");
            Assert.Equal(new[] { "Store 1" }, dualUser.StoreNames);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetUsers_WhenStoreManagerRequestsOutOfScopeStore_ReturnsEmptyPage(
            bool useOptimized
        )
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(
                    new CurrentUserManageableStoreScope
                    {
                        IsAllowed = true,
                        IsAuthenticated = true,
                        UserGuid = "manager-1",
                        StoreGuids = new[] { "store-1" },
                        StoreCodes = new[] { "S001" },
                    }
                )
            );

            var result = await GetUsersAsync(
                service,
                useOptimized,
                new UserQueryDto { Page = 1, PageSize = 20, StoreGuid = "store-2" }
            );

            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            Assert.Empty(result.Data!.Items!);
            Assert.Equal(0, result.Data.Total);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetUsers_WhenStoreManagerHasNoManagedStores_ReturnsEmptyPage(
            bool useOptimized
        )
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(
                    new CurrentUserManageableStoreScope
                    {
                        IsAllowed = false,
                        IsAuthenticated = true,
                        IsStoreManager = true,
                        UserGuid = "manager-no-store",
                        Message = "当前店长未分配任何可管理分店",
                    }
                )
            );

            var result = await GetUsersAsync(
                service,
                useOptimized,
                new UserQueryDto { Page = 1, PageSize = 20 }
            );

            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            Assert.Empty(result.Data!.Items!);
            Assert.Equal(0, result.Data.Total);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetUsers_WhenViewerIsNotStoreManager_RemainsUnrestricted(bool useOptimized)
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(
                    new CurrentUserManageableStoreScope
                    {
                        IsAllowed = false,
                        IsAuthenticated = true,
                        UserGuid = "viewer-1",
                        Message = "当前账号没有店员管理权限",
                    }
                )
            );

            var result = await GetUsersAsync(
                service,
                useOptimized,
                new UserQueryDto { Page = 1, PageSize = 20 }
            );

            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            Assert.Equal(7, result.Data!.Total);
            Assert.Contains(result.Data.Items!, item => item.UserGUID == "foreign-user");
            Assert.Contains(result.Data.Items!, item => item.UserGUID == "viewer-1");
        }

        [Fact]
        public async Task AssignStoresToUser_WhenScopedManagerAddsOutOfScopeStore_ReturnsError()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(
                    new CurrentUserManageableStoreScope
                    {
                        IsAllowed = true,
                        IsAuthenticated = true,
                        UserGuid = "manager-1",
                        StoreGuids = new[] { "store-1" },
                        StoreCodes = new[] { "S001" },
                    }
                )
            );

            var result = await service.AssignStoresToUserAsync(
                "scoped-user",
                new List<UserStoreAssignmentDto>
                {
                    new() { StoreGUID = "store-2", IsPrimary = true },
                }
            );

            Assert.False(result.Success);
            Assert.Equal("STORE_SCOPE_DENIED", result.ErrorCode);
            Assert.False(await _db.Queryable<UserStore>().AnyAsync(item =>
                item.UserGUID == "scoped-user" && item.StoreGUID == "store-2"
            ));
        }

        [Fact]
        public async Task CreateUserAsync_StoreManagerCannotUseGenericCreateEvenWithoutRelationships()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.CreateUserAsync(new CreateUserDto
            {
                Username = "new-staff",
                Email = "new-staff@example.test",
                Password = "secret1",
            });

            Assert.Equal("ADMIN_REQUIRED", result.ErrorCode);
            Assert.False(await _db.Queryable<User>().AnyAsync(item => item.Username == "new-staff"));
        }

        [Fact]
        public async Task BatchCreateUsersAsync_StoreManagerCannotWriteDefaultRoleOrStoreRelationships()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    UserGuid = "manager-1",
                    StoreGuids = new[] { "store-1" },
                })
            );

            var result = await service.BatchCreateUsersAsync(new BatchCreateUserRequestDto
            {
                DefaultRoleGuid = "role-viewer",
                Items = new List<BatchCreateUserItemDto>
                {
                    new() { StoreCode = "S001", Username = "batch-staff", Password = "secret1" },
                },
            });

            Assert.Equal("ADMIN_REQUIRED", result.ErrorCode);
            Assert.False(await _db.Queryable<User>().AnyAsync(item => item.Username == "batch-staff"));
        }

        [Fact]
        public async Task CreateUser_WhenScopedManagerAssignsOutOfScopeStore_ReturnsError()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(
                    new CurrentUserManageableStoreScope
                    {
                        IsAllowed = true,
                        IsAuthenticated = true,
                        UserGuid = "manager-1",
                        StoreGuids = new[] { "store-1" },
                        StoreCodes = new[] { "S001" },
                    }
                )
            );

            var result = await service.CreateUserAsync(
                new CreateUserDto
                {
                    Username = "new-scoped-user",
                    Email = "new-scoped@example.com",
                    Password = "secret1",
                    IsActive = true,
                    StoreGuids = new List<string> { "store-2" },
                }
            );

            Assert.False(result.Success);
            Assert.Equal("ADMIN_REQUIRED", result.ErrorCode);
            Assert.False(await _db.Queryable<User>().AnyAsync(item =>
                item.Username == "new-scoped-user"
            ));
        }

        [Fact]
        public async Task AssignStoresToUser_WhenChineseStoreManagerAddsOutOfScopeStore_ReturnsError()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            await SeedAliasedStoreManagerAsync(
                "manager-cn-1",
                "role-store-manager-cn",
                "店长",
                true
            );
            var service = CreateUserService(
                CreateCurrentUserManageableStoreScopeService("manager-cn-1", "店长")
            );

            var result = await service.AssignStoresToUserAsync(
                "scoped-user",
                new List<UserStoreAssignmentDto>
                {
                    new() { StoreGUID = "store-2", IsPrimary = true },
                }
            );

            Assert.False(result.Success);
            Assert.Equal("STORE_SCOPE_DENIED", result.ErrorCode);
        }

        [Fact]
        public async Task AssignStoresToUser_WhenScopedManagerReceivesHiddenExistingStore_PreservesIt()
        {
            await SeedUsersRolesAndStoresForScopeTestsAsync();
            var service = CreateUserService(
                new FakeManageableStoreScopeService(
                    new CurrentUserManageableStoreScope
                    {
                        IsAllowed = true,
                        IsAuthenticated = true,
                        UserGuid = "manager-1",
                        StoreGuids = new[] { "store-1" },
                        StoreCodes = new[] { "S001" },
                    }
                )
            );

            var result = await service.AssignStoresToUserAsync(
                "dual-user",
                new List<UserStoreAssignmentDto>
                {
                    new() { StoreGUID = "store-1", IsPrimary = false },
                    new() { StoreGUID = "store-2", IsPrimary = true },
                }
            );

            Assert.True(result.Success);
            Assert.False((await FindUserStoreAsync("dual-user", "store-1")).IsPrimary);
            Assert.False((await FindUserStoreAsync("dual-user", "store-2")).IsPrimary);
        }

        public void Dispose()
        {
            _hqDb.Dispose();
            _hqSqliteConnection.Dispose();
            if (File.Exists(_hqDbPath))
            {
                SqliteTempFileCleanup.DeleteIfExists(_hqDbPath);
            }

            _db.Dispose();
            _sqliteConnection.Dispose();
            if (File.Exists(_dbPath))
            {
                SqliteTempFileCleanup.DeleteIfExists(_dbPath);
            }
        }

        private UserService CreateUserService(
            ICurrentUserManageableStoreScopeService? manageableStoreScopeService = null
        )
        {
            return new UserService(
                CreateSqlSugarContext(_db),
                NullLogger<UserService>.Instance,
                manageableStoreScopeService
            );
        }

        private async Task<ICurrentUserManageableStoreScopeService> SeedAdminScopeAsync()
        {
            const string adminUserGuid = "access-admin";
            await _db.Insertable(CreateUser(adminUserGuid, "access-admin@example.test"))
                .ExecuteCommandAsync();
            await _db.Insertable(new Role
            {
                RoleGUID = "role-access-admin",
                RoleName = "Admin",
                IsActive = true,
            }).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole(adminUserGuid, "role-access-admin"))
                .ExecuteCommandAsync();

            return new FakeManageableStoreScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true,
                IsAuthenticated = true,
                IsAdmin = true,
                UserGuid = adminUserGuid,
            });
        }

        private StoreService CreateStoreService(
            ICurrentUserManageableStoreScopeService? manageableStoreScopeService = null
        )
        {
            return new StoreService(
                CreateSqlSugarContext(_db),
                NullLogger<StoreService>.Instance,
                null,
                manageableStoreScopeService
            );
        }

        private static StoresController CreateStoresController(IStoreService storeService)
        {
            return new StoresController(
                storeService,
                syncService: null!,
                Mock.Of<IAuthorizationService>(),
                NullLogger<StoresController>.Instance
            );
        }

        private StoreService CreateStoreServiceWithHq()
        {
            return new StoreService(
                CreateSqlSugarContext(_db),
                NullLogger<StoreService>.Instance,
                CreateHqSqlSugarContext(_hqDb)
            );
        }

        private static HqSqlSugarContext CreateHqSqlSugarContext(ISqlSugarClient db)
        {
            var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
                typeof(HqSqlSugarContext)
            );
            typeof(HqSqlSugarContext)
                .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(context, db);
            return context;
        }

        private static SqlSugarClient CreateSqliteSqlSugarClient(string connectionString)
        {
            return new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = connectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });
        }

        private async Task SeedUsersAndStoresAsync()
        {
            await _db.Insertable(new[]
            {
                new User
                {
                    UserGUID = "user-1",
                    Username = "user_1",
                    Email = "user1@example.com",
                    PasswordHash = "hashed",
                    IsActive = true,
                },
            }).ExecuteCommandAsync();

            await _db.Insertable(new[]
            {
                new Store
                {
                    StoreGUID = "store-1",
                    StoreCode = "S001",
                    StoreName = "Store 1",
                    IsActive = true,
                },
                new Store
                {
                    StoreGUID = "store-2",
                    StoreCode = "S002",
                    StoreName = "Store 2",
                    IsActive = true,
                },
            }).ExecuteCommandAsync();

            await _db.Insertable(
                new Role
                {
                    RoleGUID = "role-store-manager",
                    RoleName = "StoreManager",
                    IsActive = true,
                }
            ).ExecuteCommandAsync();
        }

        private async Task SeedStoresForListQueryAsync()
        {
            await _db.Insertable(new[]
            {
                new Store
                {
                    StoreGUID = "store-hot-a",
                    StoreCode = "S001",
                    StoreName = "Hot Store A",
                    BrandName = "Hot Bargain",
                    Phone = "0731000001",
                    IsActive = true,
                },
                new Store
                {
                    StoreGUID = "store-hot-b",
                    StoreCode = "S002",
                    StoreName = "Hot Store B",
                    BrandName = "Hot Bargain",
                    Phone = "0731000002",
                    IsActive = false,
                },
                new Store
                {
                    StoreGUID = "store-other",
                    StoreCode = "S003",
                    StoreName = "Other Store",
                    BrandName = "Other Brand",
                    Phone = "0731000003",
                    IsActive = true,
                },
            }).ExecuteCommandAsync();
        }

        private async Task SeedStoresForTimeZoneFilterAsync()
        {
            await _db.Insertable(new[]
            {
                new Store
                {
                    StoreGUID = "store-tz-bris-active",
                    StoreCode = "TZ01",
                    StoreName = "Brisbane Active",
                    BrandName = "Hot Bargain",
                    TimeZoneId = StoreTimeZonePolicy.Brisbane,
                    IsActive = true,
                },
                new Store
                {
                    StoreGUID = "store-tz-bris-inactive",
                    StoreCode = "TZ02",
                    StoreName = "Brisbane Inactive",
                    BrandName = "Hot Bargain",
                    TimeZoneId = StoreTimeZonePolicy.Brisbane,
                    IsActive = false,
                },
                new Store
                {
                    StoreGUID = "store-tz-sydney",
                    StoreCode = "TZ03",
                    StoreName = "Sydney Store",
                    BrandName = "Hot Bargain",
                    TimeZoneId = StoreTimeZonePolicy.Sydney,
                    IsActive = true,
                },
                new Store
                {
                    StoreGUID = "store-tz-melbourne",
                    StoreCode = "TZ04",
                    StoreName = "Melbourne Store",
                    BrandName = "Other Brand",
                    TimeZoneId = StoreTimeZonePolicy.Melbourne,
                    IsActive = true,
                },
                new Store
                {
                    StoreGUID = "store-tz-null",
                    StoreCode = "TZ05",
                    StoreName = "Null TimeZone Store",
                    BrandName = "Hot Bargain",
                    TimeZoneId = null,
                    IsActive = true,
                },
                new Store
                {
                    StoreGUID = "store-tz-empty",
                    StoreCode = "TZ06",
                    StoreName = "Empty TimeZone Store",
                    BrandName = "Other Brand",
                    TimeZoneId = string.Empty,
                    IsActive = false,
                },
            }).ExecuteCommandAsync();
        }

        private async Task SeedInactiveStoreUserAsync()
        {
            await _db.Insertable(CreateUser("inactive-store-user", "inactive-store-user@example.com"))
                .ExecuteCommandAsync();

            await _db.Insertable(
                new Store
                {
                    StoreGUID = "inactive-store",
                    StoreCode = "INACTIVE",
                    StoreName = "Inactive Store",
                    IsActive = false,
                    IsDeleted = false,
                    CreatedAt = DateTime.UtcNow,
                }
            ).ExecuteCommandAsync();

            await _db.Insertable(CreateUserStore("inactive-store-user", "inactive-store", false))
                .ExecuteCommandAsync();
        }

        private async Task SeedUsersRolesAndStoresForScopeTestsAsync()
        {
            await _db.Insertable(
                new[]
                {
                    CreateUser("manager-1", "manager1@example.com"),
                    CreateUser("manager-no-store", "manager2@example.com"),
                    CreateUser("viewer-1", "viewer@example.com"),
                    CreateUser("scoped-user", "scoped@example.com"),
                    CreateUser("soft-deleted-scoped-user", "deleted-scoped@example.com"),
                    CreateUser("foreign-user", "foreign@example.com"),
                    CreateUser("dual-user", "dual@example.com"),
                }
            ).ExecuteCommandAsync();

            await _db.Insertable(
                new[]
                {
                    new Role
                    {
                        RoleGUID = "role-store-manager",
                        RoleName = "StoreManager",
                        IsActive = true,
                    },
                    new Role
                    {
                        RoleGUID = "role-viewer",
                        RoleName = "Viewer",
                        IsActive = true,
                    },
                }
            ).ExecuteCommandAsync();

            await _db.Insertable(
                new[]
                {
                    new Store
                    {
                        StoreGUID = "store-1",
                        StoreCode = "S001",
                        StoreName = "Store 1",
                        IsActive = true,
                    },
                    new Store
                    {
                        StoreGUID = "store-2",
                        StoreCode = "S002",
                        StoreName = "Store 2",
                        IsActive = true,
                    },
                }
            ).ExecuteCommandAsync();

            await _db.Insertable(
                new[]
                {
                    CreateUserRole("manager-1", "role-store-manager"),
                    CreateUserRole("manager-no-store", "role-store-manager"),
                    CreateUserRole("viewer-1", "role-viewer"),
                }
            ).ExecuteCommandAsync();

            await _db.Insertable(
                new[]
                {
                    CreateUserStore("manager-1", "store-1", true),
                    CreateUserStore("scoped-user", "store-1", false),
                    CreateUserStore("soft-deleted-scoped-user", "store-1", false, true),
                    CreateUserStore("foreign-user", "store-2", false),
                    CreateUserStore("dual-user", "store-1", false),
                    CreateUserStore("dual-user", "store-2", false),
                }
            ).ExecuteCommandAsync();
        }

        private async Task SeedAliasedStoreManagerAsync(
            string userGuid,
            string roleGuid,
            string roleName,
            bool assignManagedStore
        )
        {
            await _db.Insertable(CreateUser(userGuid, $"{userGuid}@example.com")).ExecuteCommandAsync();
            await _db.Insertable(
                new Role
                {
                    RoleGUID = roleGuid,
                    RoleName = roleName,
                    IsActive = true,
                }
            ).ExecuteCommandAsync();
            await _db.Insertable(CreateUserRole(userGuid, roleGuid)).ExecuteCommandAsync();

            if (assignManagedStore)
            {
                await _db.Insertable(CreateUserStore(userGuid, "store-1", true)).ExecuteCommandAsync();
            }
        }

        private static User CreateUser(string userGuid, string email)
        {
            return new User
            {
                UserGUID = userGuid,
                Username = userGuid,
                Email = email,
                PasswordHash = "hashed",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            };
        }

        private static UserRole CreateUserRole(string userGuid, string roleGuid)
        {
            return new UserRole
            {
                UserRoleGUID = Guid.NewGuid().ToString(),
                UserGUID = userGuid,
                RoleGUID = roleGuid,
                AssignedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            };
        }

        private static UserStore CreateUserStore(
            string userGuid,
            string storeGuid,
            bool isPrimary,
            bool isDeleted = false
        )
        {
            return new UserStore
            {
                UserStoreGUID = Guid.NewGuid().ToString(),
                UserGUID = userGuid,
                StoreGUID = storeGuid,
                IsPrimary = isPrimary,
                IsDeleted = isDeleted,
                AssignedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
        }

        private static Task<ApiResponse<PagedResult<UserDto>>> GetUsersAsync(
            UserService service,
            bool useOptimized,
            UserQueryDto query
        )
        {
            return useOptimized ? service.GetUsersOptimizedAsync(query) : service.GetUsersAsync(query);
        }

        private async Task InsertUserStoreAsync(string userGuid, string storeGuid, bool isPrimary)
        {
            await _db.Insertable(new UserStore
            {
                UserStoreGUID = Guid.NewGuid().ToString(),
                UserGUID = userGuid,
                StoreGUID = storeGuid,
                IsPrimary = isPrimary,
                AssignedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
        }

        private async Task SeedLoginRecordUsersAsync()
        {
            await _db.Insertable(new[]
            {
                new User
                {
                    UserGUID = "login-user",
                    Username = "login-user",
                    Email = "login-user@example.com",
                    PasswordHash = "hashed",
                    IsActive = true,
                    IsDeleted = false,
                },
                new User
                {
                    UserGUID = "other-user",
                    Username = "other-user",
                    Email = "other-user@example.com",
                    PasswordHash = "hashed",
                    IsActive = true,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
        }

        private Task SeedRefreshTokenAsync(
            string sessionId,
            string userGuid,
            DateTime createdAt,
            string ipAddress,
            bool isRevoked = false,
            DateTime? expiresAt = null
        )
        {
            return _db.Insertable(new RefreshToken
            {
                RefreshTokenGUID = sessionId,
                UserGUID = userGuid,
                Token = $"{sessionId}-token",
                IpAddress = ipAddress,
                UserAgent = $"{sessionId}-agent",
                ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(1),
                IsRevoked = isRevoked,
                IsDeleted = false,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            }).ExecuteCommandAsync();
        }

        private async Task<UserStore> FindUserStoreAsync(string userGuid, string storeGuid)
        {
            var userStore = await _db.Queryable<UserStore>()
                .FirstAsync(item => item.UserGUID == userGuid && item.StoreGUID == storeGuid);
            Assert.NotNull(userStore);
            return userStore!;
        }

        private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
        {
            var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
                typeof(SqlSugarContext)
            );

            var dbField = typeof(SqlSugarContext).GetField(
                "_db",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            dbField!.SetValue(context, db);

            return context;
        }

        private CurrentUserManageableStoreScopeService CreateCurrentUserManageableStoreScopeService(
            string userGuid,
            string roleName
        )
        {
            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = CreatePrincipal(userGuid, roleName),
                },
            };

            return new CurrentUserManageableStoreScopeService(
                CreateSqlSugarContext(_db),
                new FakeCurrentUserService(userGuid),
                httpContextAccessor
            );
        }

        private static ClaimsPrincipal CreatePrincipal(string userGuid, string roleName)
        {
            var claims = new List<Claim>
            {
                new("userGuid", userGuid),
                new(ClaimTypes.NameIdentifier, userGuid),
                new(ClaimTypes.Name, userGuid),
                new(ClaimTypes.Role, roleName),
            };

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType"));
        }

        private sealed class FakeManageableStoreScopeService
            : ICurrentUserManageableStoreScopeService
        {
            private readonly CurrentUserManageableStoreScope _scope;

            public FakeManageableStoreScopeService(CurrentUserManageableStoreScope scope)
            {
                if (!scope.IsAdmin && scope.StoreGuids.Count > 0)
                {
                    scope.IsStoreManager = true;
                }
                _scope = scope;
            }

            public Task<CurrentUserManageableStoreScope> GetScopeAsync() => Task.FromResult(_scope);

            public Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync() =>
                Task.FromResult(_scope.StoreCodes);

            public Task<bool> CanAccessStoreCodeAsync(string storeCode) =>
                Task.FromResult(_scope.CanAccessStoreCode(storeCode));

            public Task<bool> CanAccessOrderAsync(string orderGuid) => Task.FromResult(false);

            public Task<bool> CanManageStoreAsync(string storeGuid) =>
                Task.FromResult(_scope.CanAccessStoreGuid(storeGuid));

            public Task<bool> CanManageUserAsync(string userGuid) => Task.FromResult(false);
        }

        private sealed class FakeCurrentUserService : ICurrentUserService
        {
            private readonly string _userGuid;

            public FakeCurrentUserService(string userGuid)
            {
                _userGuid = userGuid;
            }

            public string GetCurrentUsername() => _userGuid;

            public string GetCurrentUserGuid() => _userGuid;
        }
    }
}
