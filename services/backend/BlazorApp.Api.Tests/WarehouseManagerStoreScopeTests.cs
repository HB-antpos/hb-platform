using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 开关 Access:WarehouseManagerStoreScoped：关闭（默认）时仓库经理视同管理员不限分店；
/// 打开后门店管理类按 UserStore 收口（IsPrimary = 管理），仓库本职业务仍不限分店。
/// </summary>
public sealed class WarehouseManagerStoreScopeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public WarehouseManagerStoreScopeTests()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(typeof(Store), typeof(UserStore), typeof(User), typeof(UserRole), typeof(Role));
    }

    [Fact]
    public void Policy_default_keeps_warehouse_manager_unrestricted()
    {
        using var _ = StoreScopePolicy.OverrideForTest(false);

        Assert.True(StoreScopePolicy.HasAllStoreScope(["WarehouseManager"]));
        Assert.True(StoreScopePolicy.HasWarehouseWideScope(["仓库经理"]));
        Assert.False(StoreScopePolicy.IsStoreScopedManager(["WarehouseManager"]));
        Assert.True(StoreScopePolicy.IsStoreScopedManager(["StoreManager"]));
    }

    [Fact]
    public void Policy_scoped_only_admin_keeps_all_stores_and_warehouse_manager_becomes_store_scoped()
    {
        using var _ = StoreScopePolicy.OverrideForTest(true);

        Assert.True(StoreScopePolicy.HasAllStoreScope(["Admin"]));
        Assert.True(StoreScopePolicy.HasAllStoreScope(["管理员", "WarehouseManager"]));
        Assert.False(StoreScopePolicy.HasAllStoreScope(["WarehouseManager"]));
        Assert.False(StoreScopePolicy.HasAllStoreScope(["仓库管理员", "StoreManager"]));
        // 中文注释：仓库本职业务不受开关影响。
        Assert.True(StoreScopePolicy.HasWarehouseWideScope(["WarehouseAdmin"]));
        Assert.False(StoreScopePolicy.HasWarehouseWideScope(["StoreManager"]));
        Assert.True(StoreScopePolicy.IsStoreScopedManager(["WarehouseManager"]));
        Assert.False(StoreScopePolicy.IsStoreScopedManager(["StoreStaff"]));
    }

    [Fact]
    public async Task Scope_service_default_treats_warehouse_manager_as_admin()
    {
        await SeedStoresAsync();
        using var _ = StoreScopePolicy.OverrideForTest(false);
        var service = CreateService("wm-1", ["WarehouseManager"]);

        var scope = await service.GetScopeAsync();

        Assert.True(scope.IsAllowed);
        Assert.True(scope.IsAdmin);
        Assert.True(scope.IsWarehouseWide);
        Assert.True(scope.CanAccessStoreCode("S003"));
    }

    [Fact]
    public async Task Scope_service_scoped_warehouse_manager_manages_primary_views_linked_and_warehouse_scope_stays_all()
    {
        await SeedStoresAsync();
        using var _ = StoreScopePolicy.OverrideForTest(true);
        var service = CreateService("wm-1", ["WarehouseManager"]);

        var manageable = await service.GetScopeAsync();
        var assigned = await service.GetAssignedStoreScopeAsync();
        var warehouse = await service.GetWarehouseScopeAsync();

        // 中文注释：门店管理口径只认「管理」（主分店），查看口径含全部关联分店，均不再是管理员。
        Assert.True(manageable.IsAllowed);
        Assert.False(manageable.IsAdmin);
        Assert.True(manageable.IsStoreManager);
        Assert.True(manageable.IsWarehouseWide);
        Assert.Equal(["S001"], manageable.StoreCodes);
        Assert.False(manageable.CanAccessStoreCode("S002"));
        Assert.Equal(["S001", "S002"], assigned.StoreCodes.Order(StringComparer.Ordinal));
        Assert.False(assigned.CanAccessStoreCode("S003"));
        // 中文注释：仓库口径（订货 / 商品 / 货柜新品）仍不限分店。
        Assert.True(warehouse.IsAdmin);
        Assert.True(warehouse.CanAccessStoreCode("S003"));
        Assert.True(await service.CanAccessStoreCodeForWarehouseAsync("S003"));
        Assert.False(await service.CanAccessStoreCodeAsync("S003"));
    }

    [Fact]
    public async Task Scope_service_scoped_warehouse_manager_without_assignments_is_denied_for_store_management_only()
    {
        await SeedStoresAsync();
        using var _ = StoreScopePolicy.OverrideForTest(true);
        var service = CreateService("wm-unassigned", ["仓库经理"]);

        var manageable = await service.GetScopeAsync();
        var assigned = await service.GetAssignedStoreScopeAsync();
        var warehouse = await service.GetWarehouseScopeAsync();

        Assert.False(manageable.IsAllowed);
        Assert.False(assigned.IsAllowed);
        Assert.True(manageable.IsWarehouseWide);
        Assert.True(warehouse.IsAllowed);
        Assert.True(warehouse.IsAdmin);
    }

    [Fact]
    public async Task Scope_service_scoped_admin_stays_unrestricted()
    {
        await SeedStoresAsync();
        using var _ = StoreScopePolicy.OverrideForTest(true);

        var scope = await CreateService("admin-1", ["Admin", "WarehouseManager"]).GetScopeAsync();

        Assert.True(scope.IsAdmin);
        Assert.True(scope.CanAccessStoreCode("S003"));
    }

    [Fact]
    public async Task Approver_candidates_follow_switch()
    {
        await SeedStoresAsync();
        await SeedRoleMemberAsync("admin-1", "Admin");
        await SeedRoleMemberAsync("wm-1", "WarehouseManager");
        await SeedRoleMemberAsync("wm-unassigned", "WarehouseManager");
        await SeedRoleMemberAsync("manager-1", "StoreManager");
        await _db.Insertable(Link("manager-1", "store-2", primary: true)).ExecuteCommandAsync();

        using (StoreScopePolicy.OverrideForTest(false))
        {
            var unrestricted = await AttendanceApproverResolver.BuildUnrestrictedCandidateQuery(_db).ToListAsync();
            var storeOne = await AttendanceApproverResolver.BuildStoreManagerCandidateQuery(_db, "S001").ToListAsync();
            Assert.Equal(["admin-1", "wm-1", "wm-unassigned"], unrestricted.Order(StringComparer.Ordinal));
            Assert.Empty(storeOne);
        }

        using (StoreScopePolicy.OverrideForTest(true))
        {
            var unrestricted = await AttendanceApproverResolver.BuildUnrestrictedCandidateQuery(_db).ToListAsync();
            var storeOne = await AttendanceApproverResolver.BuildStoreManagerCandidateQuery(_db, "S001").ToListAsync();
            var storeTwo = await AttendanceApproverResolver.BuildStoreManagerCandidateQuery(_db, "S002").ToListAsync();
            // 中文注释：仓库经理不再审批所有门店，只审自己「管理」的门店（S001）；只「查看」的门店（S002）不算。
            Assert.Equal(["admin-1"], unrestricted);
            Assert.Equal(["wm-1"], storeOne);
            Assert.Equal(["manager-1"], storeTwo);
        }
    }

    [Fact]
    public async Task User_access_actor_follows_switch()
    {
        await SeedStoresAsync();
        await SeedRoleMemberAsync("wm-1", "WarehouseManager");
        await SeedRoleMemberAsync("wm-unassigned", "WarehouseManager");

        using (StoreScopePolicy.OverrideForTest(false))
        {
            var actor = await UserAccessMutationSecurity.ResolveActorByUserGuidAsync(_db, "wm-1");
            Assert.True(actor.IsWarehouseManager);
            Assert.True(actor.HasUnrestrictedStoreScope);
            Assert.True(actor.CanGrantManageableStores);
        }

        using (StoreScopePolicy.OverrideForTest(true))
        {
            // 中文注释：开关打开后仓库经理与店长同口径——只维护自己「管理」分店里的员工，不能授「管理」关系。
            var scoped = await UserAccessMutationSecurity.ResolveActorByUserGuidAsync(_db, "wm-1");
            Assert.True(scoped.IsStoreManager);
            Assert.False(scoped.HasUnrestrictedStoreScope);
            Assert.False(scoped.CanGrantManageableStores);
            Assert.Equal(["store-1"], scoped.StoreGuids);

            var unassigned = await UserAccessMutationSecurity.ResolveActorByUserGuidAsync(_db, "wm-unassigned");
            Assert.Equal(UserAccessMutationActorKind.Denied, unassigned.Kind);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private CurrentUserManageableStoreScopeService CreateService(string userGuid, string[] roles)
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userGuid),
                        new Claim("userId", userGuid),
                        new Claim(ClaimTypes.Name, userGuid),
                        .. roles.Select(role => new Claim(ClaimTypes.Role, role)),
                    ],
                    "TestAuth"
                )),
            },
        };
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, _db);
        return new CurrentUserManageableStoreScopeService(context, new CurrentUserService(accessor), accessor);
    }

    private async Task SeedStoresAsync()
    {
        var now = DateTime.UtcNow;
        await _db.Insertable(new[]
        {
            new Store { StoreGUID = "store-1", StoreCode = "S001", StoreName = "Managed", IsActive = true, CreatedAt = now },
            new Store { StoreGUID = "store-2", StoreCode = "S002", StoreName = "Viewed", IsActive = true, CreatedAt = now },
            new Store { StoreGUID = "store-3", StoreCode = "S003", StoreName = "Unlinked", IsActive = true, CreatedAt = now },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            Link("wm-1", "store-1", primary: true),
            Link("wm-1", "store-2", primary: false),
        }).ExecuteCommandAsync();
    }

    private async Task SeedRoleMemberAsync(string userGuid, string roleName)
    {
        var now = DateTime.UtcNow;
        var roleGuid = $"role-{roleName}";
        if (!await _db.Queryable<Role>().AnyAsync(role => role.RoleGUID == roleGuid))
        {
            await _db.Insertable(new Role { RoleGUID = roleGuid, RoleName = roleName, IsActive = true, CreatedAt = now })
                .ExecuteCommandAsync();
        }
        await _db.Insertable(new User
        {
            UserGUID = userGuid,
            Username = userGuid,
            Email = $"{userGuid}@example.com",
            PasswordHash = "hashed",
            IsActive = true,
            CreatedAt = now,
        }).ExecuteCommandAsync();
        await _db.Insertable(new UserRole
        {
            UserRoleGUID = Guid.NewGuid().ToString(),
            UserGUID = userGuid,
            RoleGUID = roleGuid,
            AssignedAt = now,
            CreatedAt = now,
        }).ExecuteCommandAsync();
    }

    private static UserStore Link(string userGuid, string storeGuid, bool primary) => new()
    {
        UserStoreGUID = $"{userGuid}-{storeGuid}",
        UserGUID = userGuid,
        StoreGUID = storeGuid,
        IsPrimary = primary,
    };
}
