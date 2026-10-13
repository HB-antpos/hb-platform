using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 考勤审批人解析：与审批接口的真实授权口径一致——持有审批权限码，且范围覆盖该门店
/// （管理员 / 仓库经理不限门店；店长只认主分店），申请人本人不算。
/// </summary>
public sealed class AttendanceApproverResolverTests : IDisposable
{
    private const string Bri = "BRI";

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly Mock<IRoleService> _roleService = new(MockBehavior.Strict);
    // 持有审批权限码的用户（模拟 UserHasPermissionAsync 的结果，含直授）。
    private readonly HashSet<string> _permitted = new(StringComparer.OrdinalIgnoreCase);
    // 权限查询失败的用户（模拟服务异常）。
    private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);
    // 实际被逐人查过权限的用户：用来证明只查了 SQL 筛出的候选，没有全表逐人查。
    private readonly List<string> _permissionChecked = [];

    public AttendanceApproverResolverTests()
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
        _db.CodeFirst.InitTables(typeof(User), typeof(Role), typeof(UserRole), typeof(Store), typeof(UserStore));

        _roleService
            .Setup(service => service.UserHasPermissionAsync(
                It.IsAny<string>(),
                Permissions.Attendance.Approval.ReviewManagedStore))
            .Returns((string userGuid, string _) =>
            {
                _permissionChecked.Add(userGuid);
                return Task.FromResult(_failing.Contains(userGuid)
                    ? ApiResponse<bool>.Error("查询失败", "CHECK_USER_PERMISSION_FAILED")
                    : ApiResponse<bool>.OK(_permitted.Contains(userGuid)));
            });

        SeedBaseData();
    }

    [Fact]
    public async Task Resolve_管理员与仓库经理不限门店_店长只认该店主分店()
    {
        var approvers = await CreateResolver().ResolveApproverUserGuidsAsync(Bri, "applicant-staff");

        Assert.Equal(
            new[] { "admin", "admin-zh", "mgr-bri", "warehouse" }.Order(StringComparer.Ordinal),
            approvers);
    }

    [Fact]
    public async Task Resolve_非主分店的店长_别的门店的店长_都不收()
    {
        var approvers = await CreateResolver().ResolveApproverUserGuidsAsync(Bri, "applicant-staff");

        Assert.DoesNotContain("mgr-nonprimary", approvers); // 只在 BRI 有非主分店关联
        Assert.DoesNotContain("mgr-syd", approvers);
    }

    [Fact]
    public async Task Resolve_没有审批权限码的人不收_即使是店长()
    {
        var approvers = await CreateResolver().ResolveApproverUserGuidsAsync(Bri, "applicant-staff");

        Assert.DoesNotContain("mgr-noperm", approvers);
        Assert.Contains("mgr-noperm", _permissionChecked); // 进了候选，但权限判断为否
    }

    [Fact]
    public async Task Resolve_直授权限但不是店长或管理员角色的人不收()
    {
        // 生产里这个权限码主要靠直接授权：直授 + 主分店，但角色不是店长，接口范围校验会判无权，通知也不能发。
        var approvers = await CreateResolver().ResolveApproverUserGuidsAsync(Bri, "applicant-staff");

        Assert.DoesNotContain("direct-marketing", approvers);
        Assert.DoesNotContain("direct-marketing", _permissionChecked);
    }

    [Fact]
    public async Task Resolve_停用或已删除的账号_停用的角色_已删除的关联_都不收()
    {
        var approvers = await CreateResolver().ResolveApproverUserGuidsAsync(Bri, "applicant-staff");

        Assert.DoesNotContain("mgr-inactive", approvers);
        Assert.DoesNotContain("mgr-deleted-user", approvers);
        Assert.DoesNotContain("mgr-role-inactive", approvers);
        Assert.DoesNotContain("mgr-link-deleted", approvers);
        Assert.DoesNotContain("admin-userrole-deleted", approvers);
    }

    [Fact]
    public async Task Resolve_申请人本人不算审批人_即使是有权限的店长()
    {
        var approvers = await CreateResolver().ResolveApproverUserGuidsAsync(Bri, "mgr-bri");

        Assert.DoesNotContain("mgr-bri", approvers);
        Assert.DoesNotContain("mgr-bri", _permissionChecked); // 申请人在查权限之前就被剔除
        Assert.Contains("admin", approvers);
    }

    [Fact]
    public async Task Resolve_只对候选逐人查权限_不全表逐人查()
    {
        await CreateResolver().ResolveApproverUserGuidsAsync(Bri, "applicant-staff");

        Assert.Equal(
            new[] { "admin", "admin-zh", "mgr-bri", "mgr-noperm", "warehouse" }.Order(StringComparer.Ordinal),
            _permissionChecked.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Resolve_权限查询失败时跳过该人_其余照常返回()
    {
        _failing.Add("admin");

        var approvers = await CreateResolver().ResolveApproverUserGuidsAsync(Bri, "applicant-staff");

        Assert.DoesNotContain("admin", approvers);
        Assert.Contains("mgr-bri", approvers);
    }

    [Fact]
    public async Task Resolve_角色名不区分大小写()
    {
        await SeedUserWithRoleAsync("mgr-lower", "storemanager", Bri, isPrimary: true, permitted: true);

        var approvers = await CreateResolver().ResolveApproverUserGuidsAsync(Bri, "applicant-staff");

        Assert.Contains("mgr-lower", approvers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Resolve_门店为空时不返回任何人(string? storeCode)
    {
        var approvers = await CreateResolver().ResolveApproverUserGuidsAsync(storeCode!, "applicant-staff");

        Assert.Empty(approvers);
        Assert.Empty(_permissionChecked);
    }

    [Fact]
    public void CandidateQueries_SqlServer方言_按小写角色名与主分店筛选()
    {
        // 候选查询在 SQLite 和 SQL Server 上都要能翻译：SQLite 用例跑真实数据，这里断言 SQL Server 方言的 SQL 形状。
        using var sqlServerDb = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString =
                "Server=127.0.0.1;Database=HBweb;User Id=test;Password=test;TrustServerCertificate=True;",
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });

        var unrestricted = AttendanceApproverResolver.BuildUnrestrictedCandidateQuery(sqlServerDb).ToSql();
        var storeManagers = AttendanceApproverResolver.BuildStoreManagerCandidateQuery(sqlServerDb, Bri).ToSql();

        Assert.Contains("LOWER([role].[RoleName])", unrestricted.Key);
        Assert.Contains("'admin'", unrestricted.Key);
        Assert.Contains("'warehousemanager'", unrestricted.Key);
        Assert.DoesNotContain("[IsPrimary]", unrestricted.Key); // 不限门店的角色不看门店关联
        Assert.Contains("LOWER([role].[RoleName])", storeManagers.Key);
        Assert.Contains("'storemanager'", storeManagers.Key);
        Assert.DoesNotContain("'admin'", storeManagers.Key);
        Assert.Contains("[link].[IsPrimary]=1", storeManagers.Key);
        Assert.Contains(storeManagers.Value, parameter => Equals(parameter.Value, Bri));
    }

    private AttendanceApproverResolver CreateResolver() => new(
        CreateSqlSugarContext(_db),
        _roleService.Object,
        NullLogger<AttendanceApproverResolver>.Instance);

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private void SeedBaseData()
    {
        _db.Insertable(new List<Store>
        {
            new() { StoreGUID = "store-bri", StoreCode = Bri },
            new() { StoreGUID = "store-syd", StoreCode = "SYD" },
        }).ExecuteCommand();

        // 不限门店：英文 / 中文别名的管理员、仓库经理；都不挂门店也应收到。
        SeedUserWithRoleAsync("admin", "Admin", null, false, true).GetAwaiter().GetResult();
        SeedUserWithRoleAsync("admin-zh", "管理员", null, false, true).GetAwaiter().GetResult();
        SeedUserWithRoleAsync("warehouse", "WarehouseManager", null, false, true).GetAwaiter().GetResult();
        // 店长：BRI 主分店且有权限 → 收。
        SeedUserWithRoleAsync("mgr-bri", "StoreManager", Bri, true, true).GetAwaiter().GetResult();
        // 其它门店的店长 / BRI 只有非主分店关联的店长 → 不收。
        SeedUserWithRoleAsync("mgr-syd", "StoreManager", "SYD", true, true).GetAwaiter().GetResult();
        SeedUserWithRoleAsync("mgr-nonprimary", "StoreManager", Bri, false, true).GetAwaiter().GetResult();
        // BRI 主分店店长但没有审批权限码 → 不收。
        SeedUserWithRoleAsync("mgr-noperm", "StoreManager", Bri, true, false).GetAwaiter().GetResult();
        // 直授权限 + 主分店，但角色不是店长 / 管理员 / 仓库经理 → 不收，也不应进入候选。
        SeedUserWithRoleAsync("direct-marketing", "Marketing Manager", Bri, true, true).GetAwaiter().GetResult();
        // 申请人：普通员工，不在候选里。
        SeedUserWithRoleAsync("applicant-staff", "StoreStaff", Bri, true, false).GetAwaiter().GetResult();

        // 各种失效情形。
        SeedUserWithRoleAsync("mgr-inactive", "StoreManager", Bri, true, true, userActive: false).GetAwaiter().GetResult();
        SeedUserWithRoleAsync("mgr-deleted-user", "StoreManager", Bri, true, true, userDeleted: true).GetAwaiter().GetResult();
        // 角色名是店长别名（中文），但角色本身已停用 → 不算店长。
        SeedUserWithRoleAsync("mgr-role-inactive", "店长", Bri, true, true, roleActive: false).GetAwaiter().GetResult();
        SeedUserWithRoleAsync("mgr-link-deleted", "StoreManager", Bri, true, true, linkDeleted: true).GetAwaiter().GetResult();
        SeedUserWithRoleAsync("admin-userrole-deleted", "Admin", null, false, true, userRoleDeleted: true).GetAwaiter().GetResult();
    }

    private async Task SeedUserWithRoleAsync(
        string userGuid,
        string roleName,
        string? storeCode,
        bool isPrimary,
        bool permitted,
        bool userActive = true,
        bool userDeleted = false,
        bool linkDeleted = false,
        bool userRoleDeleted = false,
        bool roleActive = true)
    {
        await _db.Insertable(new User
        {
            UserGUID = userGuid,
            Username = userGuid,
            Email = $"{userGuid}@example.com",
            PasswordHash = "hash",
            IsActive = userActive,
            IsDeleted = userDeleted,
        }).ExecuteCommandAsync();

        var role = await _db.Queryable<Role>().FirstAsync(item => item.RoleName == roleName);
        if (role == null)
        {
            role = new Role { RoleGUID = $"role-{roleName}", RoleName = roleName, IsActive = roleActive };
            await _db.Insertable(role).ExecuteCommandAsync();
        }

        await _db.Insertable(new UserRole
        {
            UserRoleGUID = $"{userGuid}-{role.RoleGUID}",
            UserGUID = userGuid,
            RoleGUID = role.RoleGUID,
            IsDeleted = userRoleDeleted,
        }).ExecuteCommandAsync();

        if (storeCode != null)
        {
            await _db.Insertable(new UserStore
            {
                UserStoreGUID = $"{userGuid}-{storeCode}",
                UserGUID = userGuid,
                StoreGUID = storeCode == Bri ? "store-bri" : "store-syd",
                IsPrimary = isPrimary,
                IsDeleted = linkDeleted,
            }).ExecuteCommandAsync();
        }

        if (permitted)
        {
            _permitted.Add(userGuid);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }
}
