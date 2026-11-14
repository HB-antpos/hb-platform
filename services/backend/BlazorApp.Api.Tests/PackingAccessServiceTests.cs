using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Packing;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 打包的数据范围：分店店员只能操作自己关联的分店，管理员是全部分店加总仓，
/// 仓库账号只有总仓；没有任何打包权限的账号一律 403。
/// </summary>
public sealed class PackingAccessServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public PackingAccessServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(typeof(Store), typeof(UserStore));
        _db.Insertable(new List<Store>
        {
            new() { StoreGUID = "s-1013", StoreCode = "1013", StoreName = "Sunnybank" },
            new() { StoreGUID = "s-1020", StoreCode = "1020", StoreName = "Inala" },
            new() { StoreGUID = "s-1030", StoreCode = "1030", StoreName = "Perth" },
            new() { StoreGUID = "s-closed", StoreCode = "1099", StoreName = "Closed Store", IsActive = false },
        }).ExecuteCommand();
        _db.Insertable(new List<UserStore>
        {
            new() { UserStoreGUID = "us-1", UserGUID = "u-staff", StoreGUID = "s-1013", IsPrimary = true },
            new() { UserStoreGUID = "us-2", UserGUID = "u-mgr", StoreGUID = "s-1013", IsPrimary = true },
            // 店长管两家店，其中一家是非主分店：打包要两家都能操作。
            new() { UserStoreGUID = "us-3", UserGUID = "u-mgr", StoreGUID = "s-1020", IsPrimary = false },
            new() { UserStoreGUID = "us-4", UserGUID = "u-mgr", StoreGUID = "s-1099", IsPrimary = false },
        }).ExecuteCommand();
    }

    [Fact]
    public async Task 分店店员_只能操作自己关联的分店_不是经理()
    {
        var result = await ResolveAsync(User("u-staff"), PackingPermissions(Permissions.StorePacking.Use));

        var op = result.Operator!;
        Assert.Equal(new[] { "1013" }, op.Owners.Select(owner => owner.Code));
        Assert.Equal("Sunnybank", op.Owners[0].Name);
        Assert.False(op.IsManager);
        Assert.False(op.IsAdmin);
        Assert.Equal("Staff Name", op.Name);
    }

    [Fact]
    public async Task 店长_可操作全部关联分店含非主分店_停用的分店不算_是经理()
    {
        var result = await ResolveAsync(User("u-mgr", role: "StoreManager"), PackingPermissions(Permissions.StorePacking.Use));

        var op = result.Operator!;
        Assert.Equal(new[] { "1013", "1020" }, op.Owners.Select(owner => owner.Code).OrderBy(code => code));
        Assert.True(op.IsManager);
        Assert.False(op.IsAdmin);
    }

    [Fact]
    public async Task 管理员_全部启用的分店加总仓_是经理也是管理员()
    {
        var result = await ResolveAsync(User("u-admin", role: Permissions.SuperAdminRoleNames[0]), PackingPermissions());

        var op = result.Operator!;
        Assert.Equal(new[] { "1013", "1020", "1030", PackOwners.Warehouse }, op.Owners.Select(owner => owner.Code).OrderBy(code => code, StringComparer.Ordinal));
        Assert.True(op.Owners.Single(owner => owner.Code == PackOwners.Warehouse).IsWarehouse);
        Assert.Equal("HB TEST WAREHOUSE", op.Owners.Single(owner => owner.IsWarehouse).Name);
        Assert.True(op.IsAdmin);
        Assert.True(op.IsManager);
    }

    [Fact]
    public async Task 仓库账号_只有总仓_持有管理仓库权限时是经理()
    {
        var plain = await ResolveAsync(User("u-wh"), PackingPermissions(Permissions.Warehouse.Packing));
        var manager = await ResolveAsync(User("u-wh2"), PackingPermissions(Permissions.Warehouse.Packing, Permissions.Warehouse.Manage));

        Assert.Equal(new[] { PackOwners.Warehouse }, plain.Operator!.Owners.Select(owner => owner.Code));
        Assert.False(plain.Operator.IsManager);
        Assert.True(manager.Operator!.IsManager);
        Assert.False(manager.Operator.IsAdmin);
    }

    [Fact]
    public async Task 同时有分店和仓库权限_两类地点都能操作()
    {
        var result = await ResolveAsync(
            User("u-staff"),
            PackingPermissions(Permissions.StorePacking.Use, Permissions.Warehouse.Packing)
        );

        Assert.Equal(new[] { "1013", PackOwners.Warehouse }, result.Operator!.Owners.Select(owner => owner.Code).OrderBy(code => code, StringComparer.Ordinal));
    }

    [Fact]
    public async Task 没有打包权限_或没有关联任何分店_或是订货助手令牌_一律拒绝()
    {
        var noPermission = await ResolveAsync(User("u-staff"), PackingPermissions());
        var noStores = await ResolveAsync(User("u-nobody"), PackingPermissions(Permissions.StorePacking.Use));
        var extension = await ResolveAsync(User("u-staff", extraClaim: new Claim("token_use", "browser_extension")), PackingPermissions(Permissions.StorePacking.Use));
        var anonymous = await ResolveAsync(new ClaimsPrincipal(new ClaimsIdentity()), PackingPermissions(Permissions.StorePacking.Use));

        foreach (var result in new[] { noPermission, noStores, extension, anonymous })
        {
            Assert.Null(result.Operator);
            Assert.Equal(PackingErrorCodes.NotAllowed, result.ErrorCode);
            Assert.Equal(403, result.StatusCode);
        }
    }

    private Task<PackingAccessResult> ResolveAsync(ClaimsPrincipal user, IAuthorizationService authorization)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Packing:WarehouseName"] = "HB TEST WAREHOUSE" })
            .Build();
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, _db);
        return new PackingAccessService(context, authorization, configuration).ResolveAsync(user);
    }

    /// <summary>只放行指定的权限码，其余一律拒绝；管理员角色靠角色名判断，不走这里。</summary>
    private static IAuthorizationService PackingPermissions(params string[] granted)
    {
        var mock = new Mock<IAuthorizationService>();
        mock.Setup(service => service.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync((ClaimsPrincipal _, object? _, string policy) =>
                granted.Contains(policy) ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return mock.Object;
    }

    private static ClaimsPrincipal User(string userGuid, string? role = null, Claim? extraClaim = null)
    {
        var claims = new List<Claim> { new("userId", userGuid), new("fullName", userGuid == "u-staff" ? "Staff Name" : userGuid) };
        if (role != null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (extraClaim != null)
        {
            claims.Add(extraClaim);
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }
}
