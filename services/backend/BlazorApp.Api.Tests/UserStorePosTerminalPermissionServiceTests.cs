using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class UserStorePosTerminalPermissionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public UserStorePosTerminalPermissionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables<User, Store, UserStore, Role>();
        _db.CodeFirst.InitTables<UserRole, SysRolePermission, SysUserPermission>();
        _db.CodeFirst.InitTables<SysUserStorePosPermission>();
    }

    [Fact]
    public async Task StoreManager_空快照更新和删除分别覆盖与恢复继承()
    {
        await SeedActorAndTargetAsync();
        await _db.Insertable(new SysRolePermission
        {
            Id = "target-line-permission",
            RoleGuid = "role-staff",
            PermissionCode = Permissions.PosTerminal.Sales.LineManualDiscount,
        }).ExecuteCommandAsync();
        var service = CreateService("store-a");

        var inherited = await service.GetAsync("target", "store-a");
        Assert.Equal("Inherited", inherited.Data!.Mode);
        Assert.Contains(Permissions.PosTerminal.Sales.LineManualDiscount, inherited.Data.EffectivePermissionCodes);

        var updated = await service.UpdateAsync(
            "target",
            "store-a",
            new UpdateUserStorePosTerminalPermissionsRequest
            {
                GrantedPermissionCodes =
                [Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent],
            }
        );
        Assert.True(updated.Success);
        Assert.Equal("Override", updated.Data!.Mode);
        Assert.Equal(2, updated.Data.OverriddenPermissionCodes.Count);
        Assert.DoesNotContain(Permissions.PosTerminal.Sales.LineManualDiscount, updated.Data.EffectivePermissionCodes);
        Assert.Contains(Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent, updated.Data.EffectivePermissionCodes);

        var firstRows = await _db.Queryable<SysUserStorePosPermission>()
            .Where(item => item.UserGuid == "target" && item.StoreGuid == "store-a")
            .ToListAsync();
        var firstAudit = firstRows.ToDictionary(
            item => item.PermissionCode,
            item => (item.Id, item.CreatedAt),
            StringComparer.OrdinalIgnoreCase
        );

        var savedAgain = await service.UpdateAsync(
            "target",
            "store-a",
            new UpdateUserStorePosTerminalPermissionsRequest
            {
                GrantedPermissionCodes =
                [Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent],
            }
        );
        Assert.True(savedAgain.Success);
        var secondRows = await _db.Queryable<SysUserStorePosPermission>()
            .Where(item => item.UserGuid == "target" && item.StoreGuid == "store-a")
            .ToListAsync();
        Assert.Equal(firstRows.Count, secondRows.Count);
        Assert.All(secondRows, item =>
        {
            Assert.Equal(firstAudit[item.PermissionCode].Id, item.Id);
            Assert.Equal(firstAudit[item.PermissionCode].CreatedAt, item.CreatedAt);
        });

        var deleted = await service.DeleteAsync("target", "store-a");
        Assert.True(deleted.Success);
        Assert.Equal("Inherited", deleted.Data!.Mode);
        Assert.Empty(deleted.Data.OverriddenPermissionCodes);
        Assert.Contains(Permissions.PosTerminal.Sales.LineManualDiscount, deleted.Data.EffectivePermissionCodes);
        var tombstones = await _db.Queryable<SysUserStorePosPermission>()
            .Where(item => item.UserGuid == "target" && item.StoreGuid == "store-a")
            .ToListAsync();
        Assert.Equal(secondRows.Count, tombstones.Count);
        Assert.All(tombstones, item =>
        {
            Assert.True(item.IsDeleted);
            Assert.Equal("actor", item.UpdatedBy);
            Assert.Equal(firstAudit[item.PermissionCode].Id, item.Id);
            Assert.Equal(firstAudit[item.PermissionCode].CreatedAt, item.CreatedAt);
        });

        var restoredOverride = await service.UpdateAsync(
            "target",
            "store-a",
            new UpdateUserStorePosTerminalPermissionsRequest
            {
                GrantedPermissionCodes =
                [Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent],
            }
        );
        Assert.True(restoredOverride.Success);
        var resurrectedRows = await _db.Queryable<SysUserStorePosPermission>()
            .Where(item => item.UserGuid == "target" && item.StoreGuid == "store-a")
            .ToListAsync();
        Assert.Equal(tombstones.Count, resurrectedRows.Count);
        Assert.All(resurrectedRows, item =>
        {
            Assert.False(item.IsDeleted);
            Assert.Equal(firstAudit[item.PermissionCode].Id, item.Id);
            Assert.Equal(firstAudit[item.PermissionCode].CreatedAt, item.CreatedAt);
        });
    }

    [Fact]
    public async Task StoreManager_拒绝非管理分店和高权目标()
    {
        await SeedActorAndTargetAsync();
        await SeedStoreAsync("store-b");
        await SeedUserStoreAsync("target", "store-b");

        var foreign = await CreateService("store-a").GetAsync("target", "store-b");
        Assert.False(foreign.Success);
        Assert.Equal("POS_PERMISSION_FORBIDDEN", foreign.ErrorCode);

        await _db.Insertable(new UserRole
        {
            UserRoleGUID = "target-manager-role",
            UserGUID = "target",
            RoleGUID = "role-manager",
        }).ExecuteCommandAsync();

        var privileged = await CreateService("store-a").GetAsync("target", "store-a");
        Assert.False(privileged.Success);
        Assert.Equal("POS_PERMISSION_FORBIDDEN", privileged.ErrorCode);
    }

    [Fact]
    public async Task StoreManager_只替换业务白名单并保留系统级覆盖()
    {
        await SeedActorAndTargetAsync();
        await _db.Insertable(new SysUserStorePosPermission
        {
            Id = "admin-system-override",
            UserGuid = "target",
            StoreGuid = "store-a",
            PermissionCode = Permissions.PosTerminal.Settings.AppUpdate,
            IsGranted = true,
        }).ExecuteCommandAsync();

        var result = await CreateService("store-a").UpdateAsync(
            "target",
            "store-a",
            new UpdateUserStorePosTerminalPermissionsRequest()
        );

        Assert.True(result.Success);
        Assert.True(await _db.Queryable<SysUserStorePosPermission>().AnyAsync(item =>
            item.Id == "admin-system-override" && item.IsGranted));
        Assert.DoesNotContain(
            Permissions.PosTerminal.Settings.AppUpdate,
            result.Data!.EffectivePermissionCodes
        );
    }

    [Fact]
    public async Task StoreManager_只能覆盖本人有效的Pos收银权限()
    {
        await SeedActorAndTargetAsync();
        var service = CreateService("store-a");

        var state = await service.GetAsync("target", "store-a");
        var denied = await service.UpdateAsync(
            "target",
            "store-a",
            new UpdateUserStorePosTerminalPermissionsRequest
            {
                GrantedPermissionCodes = [Permissions.PosTerminal.Sales.AddOpenItem],
            }
        );

        Assert.True(state.Success);
        Assert.Equal(
            new[]
            {
                Permissions.PosTerminal.Sales.LineManualDiscount,
                Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent,
            }.OrderBy(item => item),
            state.Data!.AssignablePermissions.Select(item => item.Code).OrderBy(item => item)
        );
        Assert.False(denied.Success);
        Assert.Equal("POS_PERMISSION_INVALID_CODES", denied.ErrorCode);
    }

    [Fact]
    public async Task StoreManager_非员工目标的查询和更新均拒绝()
    {
        await SeedActorAndTargetAsync();
        await _db.Deleteable<UserRole>()
            .Where(item => item.UserGUID == "target" && item.RoleGUID == "role-staff")
            .ExecuteCommandAsync();
        var service = CreateService("store-a");

        var getResult = await service.GetAsync("target", "store-a");
        var updateResult = await service.UpdateAsync(
            "target",
            "store-a",
            new UpdateUserStorePosTerminalPermissionsRequest()
        );

        Assert.False(getResult.Success);
        Assert.Equal("EMPLOYEE_TARGET_REQUIRED", getResult.ErrorCode);
        Assert.False(updateResult.Success);
        Assert.Equal("EMPLOYEE_TARGET_REQUIRED", updateResult.ErrorCode);
    }

    [Fact]
    public async Task StoreManager_可维护自己分店内的跨店员工Pos覆盖()
    {
        await SeedActorAndTargetAsync();
        await SeedStoreAsync("store-b");
        await SeedUserStoreAsync("target", "store-b");

        var result = await CreateService("store-a").GetAsync("target", "store-a");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public async Task WarehouseManager_可维护任意分店员工的Pos业务权限且不依赖本人持有()
    {
        await SeedWarehouseActorAndTargetAsync();
        // 仓库管理不属于目标员工的主分店，也没有任何 POS 业务码；范围服务把它视为不限分店。
        await SeedStoreAsync("store-b");
        await SeedUserStoreAsync("target", "store-b");
        var service = CreateWarehouseService();

        var state = await service.GetAsync("target", "store-b");
        var updated = await service.UpdateAsync(
            "target",
            "store-b",
            new UpdateUserStorePosTerminalPermissionsRequest
            {
                GrantedPermissionCodes = [Permissions.PosTerminal.Sales.AddOpenItem],
            }
        );

        Assert.True(state.Success);
        // 可分配范围 = 完整业务白名单，而不是「本人持有」的空集合。
        Assert.Equal(
            PermissionSeedData.PosTerminalBusinessPermissionCodes.OrderBy(item => item),
            state.Data!.AssignablePermissions.Select(item => item.Code).OrderBy(item => item)
        );
        Assert.True(updated.Success);
        Assert.Contains(
            Permissions.PosTerminal.Sales.AddOpenItem,
            updated.Data!.GrantedPermissionCodes
        );
    }

    [Fact]
    public async Task WarehouseManager_不能分配系统级Pos权限并保留既有系统级覆盖()
    {
        await SeedWarehouseActorAndTargetAsync();
        await _db.Insertable(new SysUserStorePosPermission
        {
            Id = "admin-system-override",
            UserGuid = "target",
            StoreGuid = "store-a",
            PermissionCode = Permissions.PosTerminal.Settings.AppUpdate,
            IsGranted = true,
        }).ExecuteCommandAsync();
        var service = CreateWarehouseService();

        var denied = await service.UpdateAsync(
            "target",
            "store-a",
            new UpdateUserStorePosTerminalPermissionsRequest
            {
                GrantedPermissionCodes = [Permissions.PosTerminal.Settings.AppUpdate],
            }
        );
        var saved = await service.UpdateAsync(
            "target",
            "store-a",
            new UpdateUserStorePosTerminalPermissionsRequest()
        );

        Assert.False(denied.Success);
        Assert.Equal("POS_PERMISSION_INVALID_CODES", denied.ErrorCode);
        Assert.True(saved.Success);
        Assert.True(await _db.Queryable<SysUserStorePosPermission>().AnyAsync(item =>
            item.Id == "admin-system-override" && item.IsGranted));
    }

    [Theory]
    [InlineData("WarehouseManager")]
    [InlineData("Admin")]
    public async Task WarehouseManager_拒绝管理员和仓库管理目标以及非员工目标(string targetRoleName)
    {
        await SeedWarehouseActorAndTargetAsync();
        await _db.Insertable(new Role
        {
            RoleGUID = "role-privileged",
            RoleName = targetRoleName,
            IsActive = true,
        }).ExecuteCommandAsync();
        await _db.Insertable(new UserRole
        {
            UserRoleGUID = "target-privileged-role",
            UserGUID = "target",
            RoleGUID = "role-privileged",
        }).ExecuteCommandAsync();

        var result = await CreateWarehouseService().GetAsync("target", "store-a");

        Assert.False(result.Success);
        Assert.Equal("POS_PERMISSION_FORBIDDEN", result.ErrorCode);

        // 目标去掉员工角色后（只剩非员工角色）同样不能维护。
        await _db.Deleteable<UserRole>()
            .Where(item => item.UserGUID == "target" && item.RoleGUID == "role-staff")
            .ExecuteCommandAsync();
        await _db.Deleteable<UserRole>()
            .Where(item => item.UserGUID == "target" && item.RoleGUID == "role-privileged")
            .ExecuteCommandAsync();
        var nonEmployee = await CreateWarehouseService().GetAsync("target", "store-a");
        Assert.False(nonEmployee.Success);
        Assert.Equal("EMPLOYEE_TARGET_REQUIRED", nonEmployee.ErrorCode);
    }

    [Fact]
    public async Task WarehouseManager_可维护店长账号的Pos业务权限()
    {
        await SeedWarehouseActorAndTargetAsync();
        // 目标从店员改为店长（线上店长角色自带 Users.* 店员管理码，完整高权限口径下它本身是受保护目标）。
        await _db.Deleteable<UserRole>()
            .Where(item => item.UserGUID == "target" && item.RoleGUID == "role-staff")
            .ExecuteCommandAsync();
        await _db.Insertable(new UserRole
        {
            UserRoleGUID = "target-store-manager",
            UserGUID = "target",
            RoleGUID = "role-manager",
        }).ExecuteCommandAsync();
        var service = CreateWarehouseService();

        var state = await service.GetAsync("target", "store-a");
        var updated = await service.UpdateAsync(
            "target",
            "store-a",
            new UpdateUserStorePosTerminalPermissionsRequest
            {
                GrantedPermissionCodes = [Permissions.PosTerminal.Sales.AddOpenItem],
            }
        );

        Assert.True(state.Success, state.Message);
        Assert.True(updated.Success, updated.Message);
        Assert.Contains(Permissions.PosTerminal.Sales.AddOpenItem, updated.Data!.GrantedPermissionCodes);
    }

    [Fact]
    public async Task 既非店长也非仓库管理的账号仍被拒绝()
    {
        await SeedWarehouseActorAndTargetAsync();
        // 操作者降为普通员工角色，即使范围服务放行也不能改别人的 POS 权限。
        await _db.Deleteable<UserRole>()
            .Where(item => item.UserGUID == "actor")
            .ExecuteCommandAsync();
        await _db.Insertable(new UserRole
        {
            UserRoleGUID = "actor-staff-role",
            UserGUID = "actor",
            RoleGUID = "role-staff",
        }).ExecuteCommandAsync();

        var result = await CreateWarehouseService().GetAsync("target", "store-a");

        Assert.False(result.Success);
        Assert.Equal("POS_PERMISSION_FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public void WarehouseManager模板包含管理Pos权限且不含其他员工管理码()
    {
        var template = Assert.Single(
            PermissionSeedData.RolePermissionTemplates,
            item => item.RoleName == "WarehouseManager"
        );

        Assert.Contains(Permissions.Users.ManagePosTerminalPermissions, template.PermissionCodes);
        Assert.DoesNotContain(Permissions.Users.EditStoreStaff, template.PermissionCodes);
        Assert.DoesNotContain(Permissions.Users.ManageRoles, template.PermissionCodes);
        Assert.DoesNotContain(Permissions.Users.ManageStores, template.PermissionCodes);
    }

    private async Task SeedWarehouseActorAndTargetAsync()
    {
        await SeedActorAndTargetAsync();
        // 复用店长场景的目标员工与分店，只把操作者换成仓库管理并去掉其店长身份和本人 POS 权限。
        await _db.Deleteable<UserRole>().Where(item => item.UserGUID == "actor").ExecuteCommandAsync();
        await _db.Deleteable<SysRolePermission>().Where(item => item.RoleGuid == "role-manager").ExecuteCommandAsync();
        await _db.Deleteable<UserStore>().Where(item => item.UserGUID == "actor").ExecuteCommandAsync();
        await _db.Insertable(new Role
        {
            RoleGUID = "role-warehouse-manager",
            RoleName = "WarehouseManager",
            IsActive = true,
        }).ExecuteCommandAsync();
        await _db.Insertable(new UserRole
        {
            UserRoleGUID = "actor-warehouse-role",
            UserGUID = "actor",
            RoleGUID = "role-warehouse-manager",
        }).ExecuteCommandAsync();
    }

    private UserStorePosTerminalPermissionService CreateWarehouseService() =>
        new(
            CreateContext(_db),
            new FakeScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true,
                IsAuthenticated = true,
                // 与 CurrentUserManageableStoreScopeService 对仓库管理的真实输出一致：不限分店。
                IsAdmin = true,
                UserGuid = "actor",
            }),
            new FakeCurrentUserService()
        );

    private async Task SeedActorAndTargetAsync()
    {
        await SeedStoreAsync("store-a");
        await _db.Insertable(new[]
        {
            CreateUser("actor"),
            CreateUser("target"),
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Role { RoleGUID = "role-manager", RoleName = "StoreManager", IsActive = true },
            new Role { RoleGUID = "role-staff", RoleName = "StoreStaff", IsActive = true },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new UserRole { UserRoleGUID = "actor-role", UserGUID = "actor", RoleGUID = "role-manager" },
            new UserRole { UserRoleGUID = "target-role", UserGUID = "target", RoleGUID = "role-staff" },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new SysRolePermission
            {
                Id = "actor-line-discount",
                RoleGuid = "role-manager",
                PermissionCode = Permissions.PosTerminal.Sales.LineManualDiscount,
            },
            new SysRolePermission
            {
                Id = "actor-order-discount",
                RoleGuid = "role-manager",
                PermissionCode = Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent,
            },
        }).ExecuteCommandAsync();
        await SeedUserStoreAsync("actor", "store-a", true);
        await SeedUserStoreAsync("target", "store-a");
    }

    private async Task SeedStoreAsync(string storeGuid) =>
        await _db.Insertable(new Store
        {
            StoreGUID = storeGuid,
            StoreCode = storeGuid,
            StoreName = storeGuid,
            IsActive = true,
        }).ExecuteCommandAsync();

    private async Task SeedUserStoreAsync(string userGuid, string storeGuid, bool primary = false) =>
        await _db.Insertable(new UserStore
        {
            UserStoreGUID = $"{userGuid}-{storeGuid}",
            UserGUID = userGuid,
            StoreGUID = storeGuid,
            IsPrimary = primary,
        }).ExecuteCommandAsync();

    private static User CreateUser(string guid) => new()
    {
        UserGUID = guid,
        Username = guid,
        Email = $"{guid}@example.com",
        PasswordHash = "hash",
        IsActive = true,
    };

    private UserStorePosTerminalPermissionService CreateService(params string[] storeGuids) =>
        new(
            CreateContext(_db),
            new FakeScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true,
                IsAuthenticated = true,
                UserGuid = "actor",
                StoreGuids = storeGuids,
            }),
            new FakeCurrentUserService()
        );

    private static SqlSugarContext CreateContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class FakeCurrentUserService : ICurrentUserService
    {
        public string GetCurrentUsername() => "actor";
        public string GetCurrentUserGuid() => "actor";
    }

    private sealed class FakeScopeService(CurrentUserManageableStoreScope scope)
        : ICurrentUserManageableStoreScopeService
    {
        public Task<CurrentUserManageableStoreScope> GetScopeAsync() => Task.FromResult(scope);
        public Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync() => throw new NotSupportedException();
        public Task<bool> CanAccessStoreCodeAsync(string storeCode) => throw new NotSupportedException();
        public Task<bool> CanAccessOrderAsync(string orderGuid) => throw new NotSupportedException();
        public Task<bool> CanManageStoreAsync(string storeGuid) => throw new NotSupportedException();
        public Task<bool> CanManageUserAsync(string userGuid) => throw new NotSupportedException();
    }
}
