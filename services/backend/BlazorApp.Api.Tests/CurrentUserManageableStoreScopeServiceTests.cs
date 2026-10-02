using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class CurrentUserManageableStoreScopeServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public CurrentUserManageableStoreScopeServiceTests()
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
        _db.CodeFirst.InitTables(typeof(Store), typeof(UserStore));
    }

    [Fact]
    public async Task Store_manager_manageable_scope_stays_primary_only_while_assigned_scope_includes_all_linked_stores()
    {
        await SeedAsync();
        var service = CreateService("manager-1", ["StoreManager"]);

        var manageable = await service.GetScopeAsync();
        var assigned = await service.GetAssignedStoreScopeAsync();

        // 中文注释：可管理分店口径（店员管理等写操作）不变，只认主分店。
        Assert.True(manageable.IsAllowed);
        Assert.Equal(["S001"], manageable.StoreCodes);
        Assert.False(manageable.CanAccessStoreCode("S002"));

        // 中文注释：查看口径取全部关联分店；已删除的关联与已删除的分店都不算。
        Assert.True(assigned.IsAllowed);
        Assert.True(assigned.IsStoreManager);
        Assert.Equal(["S001", "S002"], assigned.StoreCodes.Order(StringComparer.Ordinal));
        Assert.True(assigned.CanAccessStoreCode("s002"));
        Assert.False(assigned.CanAccessStoreCode("S003"));
        Assert.False(assigned.CanAccessStoreCode("S004"));
    }

    [Fact]
    public async Task Store_manager_with_only_non_primary_links_can_view_but_not_manage()
    {
        await SeedAsync();
        var service = CreateService("manager-2", ["StoreManager"]);

        var manageable = await service.GetScopeAsync();
        var assigned = await service.GetAssignedStoreScopeAsync();

        Assert.False(manageable.IsAllowed);
        Assert.True(assigned.IsAllowed);
        Assert.Equal(["S002"], assigned.StoreCodes);
    }

    [Fact]
    public async Task Assigned_scope_keeps_admin_unrestricted_and_other_roles_denied()
    {
        await SeedAsync();

        var admin = await CreateService("admin-1", ["Admin"]).GetAssignedStoreScopeAsync();
        var staff = await CreateService("manager-1", ["StoreStaff"]).GetAssignedStoreScopeAsync();

        Assert.True(admin.IsAllowed);
        Assert.True(admin.CanAccessStoreCode("ANY"));
        Assert.False(staff.IsAllowed);
        Assert.False(staff.CanAccessStoreCode("S001"));
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

    private async Task SeedAsync()
    {
        var now = DateTime.UtcNow;
        await _db.Insertable(new[]
        {
            new Store { StoreGUID = "store-1", StoreCode = "S001", StoreName = "Primary", IsActive = true, CreatedAt = now },
            new Store { StoreGUID = "store-2", StoreCode = "S002", StoreName = "Linked", IsActive = true, CreatedAt = now },
            new Store { StoreGUID = "store-3", StoreCode = "S003", StoreName = "Unlinked", IsActive = true, CreatedAt = now },
            new Store { StoreGUID = "store-4", StoreCode = "S004", StoreName = "Deleted", IsActive = true, IsDeleted = true, CreatedAt = now },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            Link("manager-1", "store-1", primary: true),
            Link("manager-1", "store-2", primary: false),
            Link("manager-1", "store-3", primary: false, deleted: true),
            Link("manager-1", "store-4", primary: false),
            Link("manager-2", "store-2", primary: false),
        }).ExecuteCommandAsync();
    }

    private static UserStore Link(string userGuid, string storeGuid, bool primary, bool deleted = false) => new()
    {
        UserStoreGUID = $"{userGuid}-{storeGuid}",
        UserGUID = userGuid,
        StoreGUID = storeGuid,
        IsPrimary = primary,
        IsDeleted = deleted,
    };
}
