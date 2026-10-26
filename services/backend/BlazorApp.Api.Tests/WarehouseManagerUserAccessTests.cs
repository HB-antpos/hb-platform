using System.Reflection;
using System.Runtime.CompilerServices;
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

namespace BlazorApp.Api.Tests;

/// <summary>
/// 仓库管理在「用户的角色与分店」上的委派边界：
/// 能给任意分店的用户授「管理 / 查看」分店关系（管理关系会派生店长角色），能把店长调整成店员，
/// 角色只能授员工角色；永远不能碰管理员 / 仓库管理账号、本人，也不能授 Admin / 仓库管理角色。
/// </summary>
public sealed class WarehouseManagerUserAccessTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public WarehouseManagerUserAccessTests()
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
        _db.CodeFirst.InitTables<User, Store, UserStore, Role, UserRole>();
        _db.CodeFirst.InitTables<SysPermission, SysRolePermission, SysUserPermission>();
        _db.CodeFirst.InitTables<SysUserStorePosPermission, UserLoginRestriction, RefreshToken>();
    }

    [Fact]
    public async Task AssignStores_仓库管理可对任意分店授管理和查看关系并派生店长角色()
    {
        await SeedAsync();
        var service = CreateService();

        var result = await service.AssignStoresToUserAsync(
            "staff",
            [
                new UserStoreAssignmentDto { StoreGUID = "store-a", IsPrimary = true },
                new UserStoreAssignmentDto { StoreGUID = "store-b", IsPrimary = false },
            ]
        );

        Assert.True(result.Success, result.Message);
        var rows = await _db.Queryable<UserStore>()
            .Where(item => item.UserGUID == "staff" && !item.IsDeleted)
            .ToListAsync();
        Assert.Contains(rows, item => item.StoreGUID == "store-a" && item.IsPrimary);
        Assert.Contains(rows, item => item.StoreGUID == "store-b" && !item.IsPrimary);
        // 管理关系派生店长角色：目标原来只有员工角色，现在多了 StoreManager。
        Assert.True(await HasRoleAsync("staff", "role-store-manager"));
        Assert.True(await HasRoleAsync("staff", "role-staff"));
    }

    [Fact]
    public async Task AssignStores_仓库管理把店长降为店员时撤掉管理关系并同步移除店长角色()
    {
        await SeedAsync();
        var service = CreateService();

        var result = await service.AssignStoresToUserAsync(
            "store-manager",
            [new UserStoreAssignmentDto { StoreGUID = "store-a", IsPrimary = false }]
        );

        Assert.True(result.Success, result.Message);
        Assert.False(await _db.Queryable<UserStore>().AnyAsync(item =>
            item.UserGUID == "store-manager" && item.IsPrimary && !item.IsDeleted));
        Assert.False(await HasRoleAsync("store-manager", "role-store-manager"));
    }

    [Theory]
    [InlineData("admin-target", "HIGH_PRIVILEGE_TARGET_DENIED")]
    [InlineData("other-warehouse", "HIGH_PRIVILEGE_TARGET_DENIED")]
    [InlineData("warehouse", "SELF_ACCESS_MANAGEMENT_DENIED")]
    public async Task AssignStores_拒绝管理员_仓库管理账号和本人(string targetGuid, string expectedCode)
    {
        await SeedAsync();

        var result = await CreateService().AssignStoresToUserAsync(
            targetGuid,
            [new UserStoreAssignmentDto { StoreGUID = "store-a", IsPrimary = true }]
        );

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.False(await _db.Queryable<UserStore>().AnyAsync(item =>
            item.UserGUID == targetGuid && item.StoreGUID == "store-a" && item.IsPrimary));
    }

    [Fact]
    public async Task AssignStores_直接带提升类权限码的账号同样受保护()
    {
        await SeedAsync();
        // 普通员工被直接授予了 Roles.Edit：能改角色定义的人不能被仓库管理改动。
        await _db.Insertable(new SysUserPermission
        {
            Id = "elevated-direct",
            UserGuid = "staff",
            PermissionCode = Permissions.Roles.Edit,
        }).ExecuteCommandAsync();

        var result = await CreateService().AssignStoresToUserAsync(
            "staff",
            [new UserStoreAssignmentDto { StoreGUID = "store-a", IsPrimary = true }]
        );

        Assert.False(result.Success);
        Assert.Equal("HIGH_PRIVILEGE_TARGET_DENIED", result.ErrorCode);
    }

    [Fact]
    public async Task AssignRoles_仓库管理可授员工角色()
    {
        await SeedAsync();
        await _db.Deleteable<UserRole>().Where(item => item.UserGUID == "staff").ExecuteCommandAsync();

        var result = await CreateService().AssignRolesToUserAsync(
            "staff",
            new UserRoleAssignmentDto { RoleGuids = ["role-staff"] }
        );

        Assert.True(result.Success, result.Message);
        Assert.True(await HasRoleAsync("staff", "role-staff"));
    }

    [Theory]
    [InlineData("role-admin")]
    [InlineData("role-warehouse-manager")]
    [InlineData("role-warehouse-staff")]
    [InlineData("role-order")]
    public async Task AssignRoles_仓库管理不能授管理员_仓库管理或其他非员工角色(string roleGuid)
    {
        await SeedAsync();

        var result = await CreateService().AssignRolesToUserAsync(
            "staff",
            new UserRoleAssignmentDto { RoleGuids = ["role-staff", roleGuid] }
        );

        Assert.False(result.Success);
        Assert.Equal("ROLE_DELEGATION_DENIED", result.ErrorCode);
        Assert.False(await HasRoleAsync("staff", roleGuid));
    }

    [Fact]
    public async Task AssignRoles_仓库管理不能改管理员账号的角色()
    {
        await SeedAsync();

        var result = await CreateService().AssignRolesToUserAsync(
            "admin-target",
            new UserRoleAssignmentDto { RoleGuids = ["role-staff"] }
        );

        Assert.False(result.Success);
        Assert.Equal("HIGH_PRIVILEGE_TARGET_DENIED", result.ErrorCode);
    }

    [Fact]
    public async Task RemoveStore_仓库管理可移除分店关系_管理员账号受保护()
    {
        await SeedAsync();
        var service = CreateService();

        var removed = await service.RemoveStoreFromUserAsync("store-manager", "store-a");
        var denied = await service.RemoveStoreFromUserAsync("admin-target", "store-a");

        Assert.True(removed.Success, removed.Message);
        Assert.False(await HasRoleAsync("store-manager", "role-store-manager"));
        Assert.False(denied.Success);
        Assert.Equal("HIGH_PRIVILEGE_TARGET_DENIED", denied.ErrorCode);
    }

    [Fact]
    public async Task RemoveRole_仍然只有管理员可以单独移除角色()
    {
        await SeedAsync();

        var result = await CreateService().RemoveRoleFromUserAsync("staff", "role-staff");

        Assert.False(result.Success);
        Assert.Equal("ADMIN_REQUIRED", result.ErrorCode);
    }

    [Fact]
    public async Task 同时有店长和仓库管理角色时以仓库管理为准_可授管理关系()
    {
        await SeedAsync();
        // 与线上 sean 账号一致：同时持有两个角色，主分店只有 store-b。
        await _db.Insertable(new UserRole
        {
            UserRoleGUID = "warehouse-also-store-manager",
            UserGUID = "warehouse",
            RoleGUID = "role-store-manager",
        }).ExecuteCommandAsync();
        await _db.Insertable(new UserStore
        {
            UserStoreGUID = "warehouse-store-b",
            UserGUID = "warehouse",
            StoreGUID = "store-b",
            IsPrimary = true,
        }).ExecuteCommandAsync();

        var result = await CreateService().AssignStoresToUserAsync(
            "staff",
            [new UserStoreAssignmentDto { StoreGUID = "store-a", IsPrimary = true }]
        );

        // store-a 不在其主分店里，店长口径会被拒；仓库管理口径放行。
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task 店长仍然不能授管理关系_行为不变()
    {
        await SeedAsync();

        var result = await CreateService(
            actorGuid: "plain-store-manager",
            isAdmin: false,
            storeGuids: ["store-a"]
        ).AssignStoresToUserAsync(
            "staff",
            [new UserStoreAssignmentDto { StoreGUID = "store-a", IsPrimary = true }]
        );

        Assert.False(result.Success);
        Assert.Equal("MANAGEABLE_STORE_GRANT_DENIED", result.ErrorCode);
    }

    [Fact]
    public async Task 读取目标_仓库管理不限分店但要求委派权限_管理员账号不可读()
    {
        await SeedAsync();

        var staff = await UserAccessMutationSecurity.ValidateReadTargetAsync(_db, "warehouse", "staff");
        var storeManager = await UserAccessMutationSecurity.ValidateReadTargetAsync(
            _db, "warehouse", "store-manager");
        var admin = await UserAccessMutationSecurity.ValidateReadTargetAsync(_db, "warehouse", "admin-target");
        var otherWarehouse = await UserAccessMutationSecurity.ValidateReadTargetAsync(
            _db, "warehouse", "other-warehouse");

        Assert.True(staff.IsAllowed);
        Assert.True(staff.RequiresDelegatedPermission);
        Assert.Null(staff.VisibleStoreGuids);
        Assert.True(storeManager.IsAllowed);
        Assert.False(admin.IsAllowed);
        Assert.False(otherWarehouse.IsAllowed);
    }

    [Fact]
    public async Task 操作者解析_仓库管理不是超管_不能走管理员分支()
    {
        await SeedAsync();

        var actor = await UserAccessMutationSecurity.ResolveActorByUserGuidAsync(_db, "warehouse");

        Assert.True(actor.IsWarehouseManager);
        Assert.False(actor.IsSuperAdmin);
        Assert.True(actor.HasUnrestrictedStoreScope);
        Assert.True(actor.CanGrantManageableStores);
    }

    private async Task<bool> HasRoleAsync(string userGuid, string roleGuid) =>
        await _db.Queryable<UserRole>().AnyAsync(item =>
            item.UserGUID == userGuid && item.RoleGUID == roleGuid && !item.IsDeleted);

    private async Task SeedAsync()
    {
        await _db.Insertable(new[]
        {
            Store("store-a"),
            Store("store-b"),
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            Role("role-admin", "Admin"),
            Role("role-warehouse-manager", "WarehouseManager"),
            Role("role-warehouse-staff", "WarehouseStaff"),
            Role("role-store-manager", "StoreManager"),
            Role("role-staff", "StoreStaff"),
            Role("role-order", "Order"),
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            User("warehouse"),
            User("other-warehouse"),
            User("admin-target"),
            User("staff"),
            User("store-manager"),
            User("plain-store-manager"),
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            UserRoleRow("warehouse", "role-warehouse-manager"),
            UserRoleRow("other-warehouse", "role-warehouse-manager"),
            UserRoleRow("admin-target", "role-admin"),
            UserRoleRow("staff", "role-staff"),
            UserRoleRow("store-manager", "role-store-manager"),
            UserRoleRow("plain-store-manager", "role-store-manager"),
        }).ExecuteCommandAsync();
        // 店长角色自带 Users.* 店员管理码（线上 StoreManager 角色就是这样）：
        // 完整高权限口径下店长账号是受保护目标，仓库管理的窄口径下不是。
        await _db.Insertable(new[]
        {
            RolePermission("store-manager-view", "role-store-manager", Permissions.Users.ViewStoreStaff),
            RolePermission("store-manager-create", "role-store-manager", Permissions.Users.Create),
            RolePermission("store-manager-stores", "role-store-manager", Permissions.Users.ManageStores),
            RolePermission("store-manager-reset", "role-store-manager", Permissions.Users.ResetPassword),
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            UserStoreRow("staff", "store-a", false),
            UserStoreRow("store-manager", "store-a", true),
            UserStoreRow("admin-target", "store-a", false),
            UserStoreRow("other-warehouse", "store-a", false),
            UserStoreRow("plain-store-manager", "store-a", true),
        }).ExecuteCommandAsync();
    }

    private UserService CreateService(
        string actorGuid = "warehouse",
        bool isAdmin = true,
        string[]? storeGuids = null
    ) =>
        new(
            CreateContext(_db),
            NullLogger<UserService>.Instance,
            new FakeScopeService(new CurrentUserManageableStoreScope
            {
                IsAllowed = true,
                IsAuthenticated = true,
                // 与 CurrentUserManageableStoreScopeService 对仓库管理的真实输出一致：按管理员口径、不限分店。
                IsAdmin = isAdmin,
                IsStoreManager = !isAdmin,
                UserGuid = actorGuid,
                StoreGuids = storeGuids ?? Array.Empty<string>(),
            })
        );

    private static Store Store(string guid) => new()
    {
        StoreGUID = guid,
        StoreCode = guid,
        StoreName = guid,
        IsActive = true,
    };

    private static Role Role(string guid, string name) => new()
    {
        RoleGUID = guid,
        RoleName = name,
        IsActive = true,
    };

    private static User User(string guid) => new()
    {
        UserGUID = guid,
        Username = guid,
        Email = $"{guid}@example.test",
        PasswordHash = "hash",
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
    };

    private static UserRole UserRoleRow(string userGuid, string roleGuid) => new()
    {
        UserRoleGUID = $"{userGuid}-{roleGuid}",
        UserGUID = userGuid,
        RoleGUID = roleGuid,
        AssignedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
    };

    private static UserStore UserStoreRow(string userGuid, string storeGuid, bool primary) => new()
    {
        UserStoreGUID = $"{userGuid}-{storeGuid}",
        UserGUID = userGuid,
        StoreGUID = storeGuid,
        IsPrimary = primary,
    };

    private static SysRolePermission RolePermission(string id, string roleGuid, string code) => new()
    {
        Id = id,
        RoleGuid = roleGuid,
        PermissionCode = code,
    };

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
