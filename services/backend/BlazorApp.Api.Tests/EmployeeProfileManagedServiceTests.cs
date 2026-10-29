using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>员工个人信息维护（/managed）：范围、过滤、脱敏与非敏感白名单保存。</summary>
public sealed class EmployeeProfileManagedServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public EmployeeProfileManagedServiceTests()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(User),
            typeof(Store),
            typeof(UserStore),
            typeof(Role),
            typeof(UserRole),
            typeof(EmployeeProfile)
        );
    }

    [Fact]
    public async Task GetList_管理员看全部_可按分店和角色过滤_并带分店角色标签()
    {
        await SeedAsync();
        var service = CreateService("admin-user", ["Admin"]);

        var all = await service.GetListAsync(new EmployeeProfileQueryDto { PageSize = 50 });
        Assert.True(all.Success);
        Assert.Equal(7, all.Data!.Total);

        var storeB = await service.GetListAsync(new EmployeeProfileQueryDto { StoreGuid = "store-b" });
        Assert.Equal(
            new[] { "staff-a2", "staff-b1" },
            storeB.Data!.Items.Select(item => item.Username).OrderBy(name => name).ToArray()
        );

        var managers = await service.GetListAsync(new EmployeeProfileQueryDto { RoleGuid = "role-manager" });
        Assert.Equal(
            new[] { "manager-a", "other-manager" },
            managers.Data!.Items.Select(item => item.Username).OrderBy(name => name).ToArray()
        );

        var both = await service.GetListAsync(new EmployeeProfileQueryDto
        {
            StoreGuid = "store-a",
            RoleGuid = "role-staff",
        });
        var staffA2 = both.Data!.Items.Single(item => item.Username == "staff-a2");
        Assert.Equal(new[] { "门店 A", "门店 B" }, staffA2.StoreNames);
        Assert.Equal(new[] { "StoreStaff" }, staffA2.RoleNames);
    }

    [Fact]
    public async Task GetList_店长只看管理分店店员_排除本人和受保护角色_标签只含管理分店()
    {
        await SeedAsync();
        var service = CreateService("manager-a", ["StoreManager"], storeGuids: ["store-a"]);

        var result = await service.GetListAsync(new EmployeeProfileQueryDto { PageSize = 50 });

        Assert.True(result.Success);
        Assert.Equal(
            new[] { "staff-a1", "staff-a2", "staff-a3" },
            result.Data!.Items.Select(item => item.Username).OrderBy(name => name).ToArray()
        );
        // 多分店员工只显示调用者管理范围内的分店，不泄露其他分店归属。
        Assert.Equal(
            new[] { "门店 A" },
            result.Data.Items.Single(item => item.Username == "staff-a2").StoreNames
        );
        // 列表里的银行 / 公积金账号只给脱敏值。
        var staffA1 = result.Data.Items.Single(item => item.Username == "staff-a1");
        Assert.Equal("****6789", staffA1.BankAccountNumber);
        Assert.Equal("****4321", staffA1.SuperannuationAccountNumber);
    }

    [Fact]
    public async Task GetList_店长请求管理范围外分店_返回空页()
    {
        await SeedAsync();
        var service = CreateService("manager-a", ["StoreManager"], storeGuids: ["store-a"]);

        var result = await service.GetListAsync(new EmployeeProfileQueryDto { StoreGuid = "store-b" });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Items);
        Assert.Equal(0, result.Data.Total);
    }

    [Fact]
    public async Task GetList_非管理员非店长_拒绝()
    {
        await SeedAsync();
        // 仓库经理（scope.IsAdmin 为真）也不是超级管理员或店长，不放行。
        var service = CreateService("staff-a1", ["WarehouseManager"], scopeIsAdmin: true);

        var result = await service.GetListAsync(new EmployeeProfileQueryDto());

        Assert.False(result.Success);
        Assert.Equal(EmployeeProfileManagedService.ScopeForbiddenCode, result.ErrorCode);
    }

    [Fact]
    public async Task GetFilterOptions_店长只含管理分店且不含受保护角色()
    {
        await SeedAsync();
        var manager = CreateService("manager-a", ["StoreManager"], storeGuids: ["store-a"]);
        var admin = CreateService("admin-user", ["Admin"]);

        var managerOptions = (await manager.GetFilterOptionsAsync()).Data!;
        var adminOptions = (await admin.GetFilterOptionsAsync()).Data!;

        Assert.Equal(new[] { "store-a" }, managerOptions.Stores.Select(item => item.StoreGuid).ToArray());
        Assert.Equal(new[] { "StoreStaff" }, managerOptions.Roles.Select(item => item.RoleName).ToArray());
        Assert.Equal(2, adminOptions.Stores.Count);
        Assert.Equal(3, adminOptions.Roles.Count);
    }

    [Theory]
    [InlineData("staff-b1")] // 管理范围外的店员
    [InlineData("other-manager")] // 受保护角色（店长）
    [InlineData("manager-a")] // 本人
    [InlineData("admin-user")] // 管理员
    public async Task GetDetail_店长访问范围外_受保护_本人_均拒绝(string targetUserGuid)
    {
        await SeedAsync();
        var service = CreateService("manager-a", ["StoreManager"], storeGuids: ["store-a"]);

        var result = await service.GetDetailAsync(targetUserGuid);

        Assert.False(result.Success);
        Assert.Equal(EmployeeProfileManagedService.ScopeForbiddenCode, result.ErrorCode);
    }

    [Fact]
    public async Task GetDetail_敏感资料只返回脱敏摘要_并带职位类型编辑能力()
    {
        await SeedAsync();
        var service = CreateService("manager-a", ["StoreManager"], storeGuids: ["store-a"], canEditPositionType: true);

        var result = await service.GetDetailAsync("staff-a1");

        Assert.True(result.Success);
        var detail = result.Data!;
        Assert.Equal("0412 345 678", detail.Phone);
        Assert.Equal("****6789", detail.BankAccountNumber);
        Assert.Equal("****4321", detail.SuperannuationAccountNumber);
        Assert.Equal("****7731", detail.IdentityId);
        Assert.True(detail.HasBirthday);
        Assert.True(detail.CanEditEmploymentType);
        Assert.Equal(new[] { "门店 A" }, detail.StoreNames);
    }

    [Fact]
    public async Task Update_只改白名单且只改请求带的列_敏感列与revision不变()
    {
        await SeedAsync();
        var service = CreateService("manager-a", ["StoreManager"], storeGuids: ["store-a"]);

        var result = await service.UpdateAsync("staff-a1", new EmployeeProfileManagedUpsertDto
        {
            Phone = "0400 000 111",
            Gender = "female",
        });

        Assert.True(result.Success);
        var profile = await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "staff-a1");
        Assert.Equal("0400 000 111", profile.Phone);
        Assert.Equal(EmployeeGender.Female, profile.Gender);
        // 没带 Address 就不改；敏感列和 revision 保持原值。
        Assert.Equal("1 Old St", profile.Address);
        Assert.Equal("123456789", profile.BankACC);
        Assert.Equal("9984321", profile.SuperannuationAccount);
        Assert.Equal("P7731", profile.IdentityId);
        Assert.Equal(new DateTime(1999, 3, 14), profile.Birthday);
        Assert.Equal(2, profile.SensitiveRevision);
        Assert.Equal("manager-a", profile.UpdatedBy);
    }

    [Fact]
    public async Task Update_显式传空可清空地址()
    {
        await SeedAsync();
        var service = CreateService("manager-a", ["StoreManager"], storeGuids: ["store-a"]);

        var result = await service.UpdateAsync("staff-a1", new EmployeeProfileManagedUpsertDto { Address = "  " });

        Assert.True(result.Success);
        var profile = await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "staff-a1");
        Assert.Null(profile.Address);
        Assert.Equal("0412 345 678", profile.Phone);
    }

    [Fact]
    public async Task Update_目标还没有资料档案_首次保存时创建()
    {
        await SeedAsync();
        var service = CreateService("manager-a", ["StoreManager"], storeGuids: ["store-a"]);

        var result = await service.UpdateAsync("staff-a3", new EmployeeProfileManagedUpsertDto
        {
            Phone = "0411 222 333",
        });

        Assert.True(result.Success);
        var profile = await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "staff-a3");
        Assert.Equal("0411 222 333", profile.Phone);
        Assert.Equal(0, profile.SensitiveRevision);
        Assert.Null(profile.BankACC);
        Assert.True(result.Data!.HasProfile);
    }

    [Fact]
    public async Task Update_改职位类型需要修改职位类型权限_没有则拒绝且不写入任何字段()
    {
        await SeedAsync();
        var denied = CreateService("manager-a", ["StoreManager"], storeGuids: ["store-a"], canEditPositionType: false);

        var rejected = await denied.UpdateAsync("staff-a1", new EmployeeProfileManagedUpsertDto
        {
            Phone = "0400 999 999",
            EmploymentType = "fullTime",
        });

        Assert.False(rejected.Success);
        Assert.Equal(EmployeeProfileManagedService.PositionTypeForbiddenCode, rejected.ErrorCode);
        var unchanged = await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "staff-a1");
        Assert.Equal("0412 345 678", unchanged.Phone);
        Assert.Equal(EmployeeType.PartTime, unchanged.EmployeeType);

        // 回传相同的职位类型视为没改，不要求该权限。
        var sameType = await denied.UpdateAsync("staff-a1", new EmployeeProfileManagedUpsertDto
        {
            Phone = "0400 999 999",
            EmploymentType = "partTime",
        });
        Assert.True(sameType.Success);

        var allowed = CreateService("manager-a", ["StoreManager"], storeGuids: ["store-a"], canEditPositionType: true);
        var changed = await allowed.UpdateAsync("staff-a1", new EmployeeProfileManagedUpsertDto
        {
            EmploymentType = "fullTime",
        });
        Assert.True(changed.Success);
        var saved = await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "staff-a1");
        Assert.Equal(EmployeeType.FullTime, saved.EmployeeType);
    }

    [Fact]
    public async Task Update_店长改范围外员工或受保护角色_拒绝且数据不变()
    {
        await SeedAsync();
        var service = CreateService("manager-a", ["StoreManager"], storeGuids: ["store-a"]);

        foreach (var target in new[] { "staff-b1", "other-manager", "manager-a" })
        {
            var result = await service.UpdateAsync(target, new EmployeeProfileManagedUpsertDto { Phone = "hacked" });
            Assert.False(result.Success);
            Assert.Equal(EmployeeProfileManagedService.ScopeForbiddenCode, result.ErrorCode);
        }

        Assert.Equal(
            0,
            await _db.Queryable<EmployeeProfile>().CountAsync(item => item.Phone == "hacked")
        );
    }

    [Fact]
    public void Controller_查看接口认ViewManagedStore_保存接口认EditManagedStore_且限定管理员或店长角色()
    {
        foreach (var method in new[]
        {
            nameof(EmployeeProfilesManagedController.GetList),
            nameof(EmployeeProfilesManagedController.GetFilterOptions),
            nameof(EmployeeProfilesManagedController.GetDetail),
        })
        {
            Assert.Contains(
                GetAuthorizeAttributes(method),
                attribute => attribute.Policy == Permissions.EmployeeProfiles.ViewManagedStore
            );
        }
        Assert.Contains(
            GetAuthorizeAttributes(nameof(EmployeeProfilesManagedController.Update)),
            attribute => attribute.Policy == Permissions.EmployeeProfiles.EditManagedStore
        );
        // 角色门禁：超级管理员 + 店长别名，与敏感资料审核一致。
        var roles = GetAuthorizeAttributes(nameof(EmployeeProfilesManagedController.GetList))
            .Select(attribute => attribute.Roles)
            .Single(value => !string.IsNullOrEmpty(value))!;
        Assert.Contains("StoreManager", roles);
        Assert.Contains("Admin", roles);
    }

    [Fact]
    public void 新权限码_已登记使用端并写入种子()
    {
        foreach (var code in new[]
        {
            Permissions.EmployeeProfiles.ViewManagedStore,
            Permissions.EmployeeProfiles.EditManagedStore,
        })
        {
            Assert.Equal(
                PermissionSurface.Web | PermissionSurface.Mobile,
                PermissionSurfaceCatalog.Resolve(code)
            );
            Assert.Contains(PermissionSeedData.AllPermissions, item => item.Code == code);
        }
    }

    private static IEnumerable<AuthorizeAttribute> GetAuthorizeAttributes(string methodName) =>
        typeof(EmployeeProfilesManagedController)
            .GetMethod(methodName)!
            .GetCustomAttributes(inherit: true)
            .OfType<AuthorizeAttribute>();

    private EmployeeProfileManagedService CreateService(
        string userGuid,
        IReadOnlyList<string> roles,
        IReadOnlyList<string>? storeGuids = null,
        bool scopeIsAdmin = false,
        bool canEditPositionType = false
    )
    {
        // 不能用 HttpContextAccessor：它基于 AsyncLocal，同一个测试里先后构造多个服务时后者会覆盖前者的操作者。
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("userGuid", userGuid),
                new Claim(ClaimTypes.NameIdentifier, userGuid),
                new Claim(ClaimTypes.Name, userGuid),
            }.Concat(roles.Select(role => new Claim(ClaimTypes.Role, role))), "TestAuth")),
        };
        var accessor = Mock.Of<IHttpContextAccessor>(service => service.HttpContext == httpContext);
        var currentUser = Mock.Of<ICurrentUserService>(service =>
            service.GetCurrentUserGuid() == userGuid
            && service.GetCurrentUsername() == userGuid
        );
        var scope = new Mock<ICurrentUserManageableStoreScopeService>();
        scope.Setup(service => service.GetScopeAsync()).ReturnsAsync(new CurrentUserManageableStoreScope
        {
            IsAllowed = true,
            IsAuthenticated = true,
            IsAdmin = scopeIsAdmin || roles.Any(role => role is "Admin" or "管理员"),
            IsStoreManager = roles.Contains("StoreManager"),
            UserGuid = userGuid,
            ActorLabel = userGuid,
            StoreGuids = storeGuids ?? [],
        });
        var roleService = new Mock<IRoleService>();
        roleService
            .Setup(service => service.UserHasPermissionAsync(
                userGuid,
                Permissions.EmployeeProfiles.EditPositionType))
            .ReturnsAsync(ApiResponse<bool>.OK(canEditPositionType));
        return new EmployeeProfileManagedService(
            CreateContext(_db),
            currentUser,
            scope.Object,
            accessor,
            NullLogger<EmployeeProfileManagedService>.Instance,
            roleService.Object
        );
    }

    private async Task SeedAsync()
    {
        var now = DateTime.UtcNow;
        foreach (var userGuid in new[]
        {
            "admin-user", "manager-a", "other-manager", "staff-a1", "staff-a2", "staff-a3", "staff-b1",
        })
        {
            await _db.Insertable(new User
            {
                UserGUID = userGuid,
                Username = userGuid,
                Email = $"{userGuid}@example.com",
                PasswordHash = "hashed",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            }).ExecuteCommandAsync();
        }
        await _db.Insertable(new[]
        {
            new Store { StoreGUID = "store-a", StoreCode = "A", StoreName = "门店 A", CreatedAt = now, UpdatedAt = now },
            new Store { StoreGUID = "store-b", StoreCode = "B", StoreName = "门店 B", CreatedAt = now, UpdatedAt = now },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Role { RoleGUID = "role-admin", RoleName = "Admin", CreatedAt = now, UpdatedAt = now },
            new Role { RoleGUID = "role-manager", RoleName = "StoreManager", CreatedAt = now, UpdatedAt = now },
            new Role { RoleGUID = "role-staff", RoleName = "StoreStaff", CreatedAt = now, UpdatedAt = now },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new UserRole { UserGUID = "admin-user", RoleGUID = "role-admin", CreatedAt = now, UpdatedAt = now },
            new UserRole { UserGUID = "manager-a", RoleGUID = "role-manager", CreatedAt = now, UpdatedAt = now },
            new UserRole { UserGUID = "other-manager", RoleGUID = "role-manager", CreatedAt = now, UpdatedAt = now },
            new UserRole { UserGUID = "staff-a1", RoleGUID = "role-staff", CreatedAt = now, UpdatedAt = now },
            new UserRole { UserGUID = "staff-a2", RoleGUID = "role-staff", CreatedAt = now, UpdatedAt = now },
            new UserRole { UserGUID = "staff-a3", RoleGUID = "role-staff", CreatedAt = now, UpdatedAt = now },
            new UserRole { UserGUID = "staff-b1", RoleGUID = "role-staff", CreatedAt = now, UpdatedAt = now },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new UserStore { UserGUID = "manager-a", StoreGUID = "store-a", IsPrimary = true, CreatedAt = now, UpdatedAt = now },
            new UserStore { UserGUID = "other-manager", StoreGUID = "store-a", IsPrimary = true, CreatedAt = now, UpdatedAt = now },
            new UserStore { UserGUID = "staff-a1", StoreGUID = "store-a", IsPrimary = true, CreatedAt = now, UpdatedAt = now },
            new UserStore { UserGUID = "staff-a2", StoreGUID = "store-a", IsPrimary = true, CreatedAt = now, UpdatedAt = now },
            new UserStore { UserGUID = "staff-a2", StoreGUID = "store-b", CreatedAt = now, UpdatedAt = now },
            new UserStore { UserGUID = "staff-a3", StoreGUID = "store-a", IsPrimary = true, CreatedAt = now, UpdatedAt = now },
            new UserStore { UserGUID = "staff-b1", StoreGUID = "store-b", IsPrimary = true, CreatedAt = now, UpdatedAt = now },
        }).ExecuteCommandAsync();
        // staff-a3 故意没有资料档案，用来覆盖「首次保存时创建」。
        foreach (var userGuid in new[] { "staff-a1", "staff-a2", "staff-b1", "manager-a", "other-manager" })
        {
            await _db.Insertable(new EmployeeProfile
            {
                UserGUID = userGuid,
                Phone = userGuid == "staff-a1" ? "0412 345 678" : null,
                Address = userGuid == "staff-a1" ? "1 Old St" : null,
                BankACC = userGuid == "staff-a1" ? "123456789" : null,
                SuperannuationAccount = userGuid == "staff-a1" ? "9984321" : null,
                IdentityId = userGuid == "staff-a1" ? "P7731" : null,
                Birthday = userGuid == "staff-a1" ? new DateTime(1999, 3, 14) : null,
                EmployeeType = userGuid == "staff-a1" ? EmployeeType.PartTime : null,
                SensitiveRevision = 2,
                CreatedAt = now,
                UpdatedAt = now,
            }).ExecuteCommandAsync();
        }
    }

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
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }
}
