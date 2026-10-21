using System.Security.Claims;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 新权限 Warehouse.StoreOrderReturn（分店订单退货）：默认无人具备、管理员手动授权、不设别名；
/// 与 Warehouse.StartPicking 同样的登记方式（权限常量、种子目录、使用端登记、生产 SQL 脚本只登记不授予）。
/// </summary>
public sealed class StoreOrderReturnPermissionTests
{
    [Fact]
    public void 权限码已登记_且不被管理仓库权限别名带出()
    {
        Assert.Equal("Warehouse.StoreOrderReturn", Permissions.Warehouse.StoreOrderReturn);
        Assert.Contains(PermissionSeedData.AllPermissions, seed => seed.Code == Permissions.Warehouse.StoreOrderReturn);

        // 持有 Manage / ManageOrders / ManageProducts 不会自动具备，必须显式授予。
        var expanded = Permissions.ExpandPermissionCodes(
            new[]
            {
                Permissions.Warehouse.Manage,
                Permissions.Warehouse.ManageOrders,
                Permissions.Warehouse.ManageProducts,
                Permissions.Orders.Edit,
            }
        );
        Assert.DoesNotContain(Permissions.Warehouse.StoreOrderReturn, expanded);
    }

    [Fact]
    public void 默认没有任何角色模板带上该权限()
    {
        foreach (var template in PermissionSeedData.RolePermissionTemplates)
        {
            Assert.DoesNotContain(Permissions.Warehouse.StoreOrderReturn, template.PermissionCodes);
        }
    }

    [Fact]
    public void 使用端登记为后台Web()
    {
        Assert.True(PermissionSurfaceCatalog.IsRegistered(Permissions.Warehouse.StoreOrderReturn));
        Assert.Equal(new[] { "web" }, PermissionSurfaceCatalog.ResolveNames(Permissions.Warehouse.StoreOrderReturn));
    }

    [Fact]
    public void 生产权限SQL只登记权限_不授予任何角色或用户()
    {
        var script = File.ReadAllText(ResolveMigrationPath("20261021_AddWarehouseStoreOrderReturnPermission.sql"));

        Assert.Contains("N'Warehouse.StoreOrderReturn'", script);
        Assert.Contains("dbo.HbwebSysPermissions", script);
        Assert.Contains("DB_NAME() <> N'HBweb'", script);
        Assert.DoesNotContain("INSERT INTO dbo.HbwebSysRolePermissions", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO dbo.HBwebSysRolePermissions", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO dbo.HbwebSysUserPermissions", script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("WarehouseManager")]
    [InlineData("WarehouseStaff")]
    [InlineData("Admin")]
    public async Task 只认显式权限_宽泛的仓库订单权限不能顶替(string role)
    {
        var broadOnly = CreatePolicy(
            new[] { role },
            new[] { Permissions.Warehouse.Manage, Permissions.Warehouse.ManageOrders, Permissions.Orders.Edit }
        );
        var explicitGrant = CreatePolicy(new[] { role }, new[] { Permissions.Warehouse.StoreOrderReturn });

        Assert.False(await broadOnly.CanRecordStoreOrderReturnsAsync());
        Assert.True(await explicitGrant.CanRecordStoreOrderReturnsAsync());
    }

    [Fact]
    public async Task 未注册权限判定时按无权限处理_保存层不会放开退货()
    {
        // 旧构造路径 / 测试里没有注入 IStoreOrderReturnPermission：StoreOrderManagementPersistence 默认 deny。
        var context = (BlazorApp.Api.Data.SqlSugarContext)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(BlazorApp.Api.Data.SqlSugarContext));
        var persistence = new BlazorApp.Api.Features.StoreOrders.OrderManagement.Infrastructure.StoreOrderManagementPersistence(
            context,
            null!,
            null!
        );

        Assert.False(await persistence.CanRecordReturnsAsync());
    }

    private static StoreOrderAccessPolicy CreatePolicy(string[] roles, string[] grantedPermissions)
    {
        var claims = roles.Select(role => new Claim(ClaimTypes.Role, role)).ToList();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        };
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.SetupGet(item => item.HttpContext).Returns(httpContext);

        // 只按策略名（权限码）放行：与真实环境里「策略 = 权限码」的授权方式一致。
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(item => item.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync((ClaimsPrincipal _, object? _, string policyName) =>
                grantedPermissions.Contains(policyName, StringComparer.OrdinalIgnoreCase)
                    ? AuthorizationResult.Success()
                    : AuthorizationResult.Failed());

        return new StoreOrderAccessPolicy(
            new StoreOrderActorContext(httpContextAccessor.Object),
            authorization.Object,
            Mock.Of<ICurrentUserManageableStoreScopeService>(),
            Mock.Of<IUserService>(),
            null!,
            httpContextAccessor.Object,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<StoreOrderAccessPolicy>.Instance
        );
    }

    private static string ResolveMigrationPath(
        string fileName,
        [System.Runtime.CompilerServices.CallerFilePath] string testFilePath = ""
    )
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("无法解析测试文件目录");
        return Path.GetFullPath(
            Path.Combine(testDirectory, "..", "BlazorApp.Api", "Data", "Migrations", fileName)
        );
    }
}
