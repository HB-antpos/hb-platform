using System.Security.Claims;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 「开始配货」（打印配货单时订单 已提交 → 配货中）的授权契约：
/// 纯仓库员工只认显式的 Warehouse.StartPicking，宽泛的仓库/订单权限不会带出它；
/// 管理类账号沿用订单编辑授权，行为不变。
/// </summary>
public sealed class StoreOrderStartPickingAccessTests
{
    private const string OrderGuid = "order-1";

    [Fact]
    public async Task 纯仓库员工_持有开始配货权限_允许()
    {
        var policy = CreatePolicy(new[] { "WarehouseStaff" }, new[] { Permissions.Warehouse.StartPicking });

        Assert.True((await policy.RequireStartPickingAsync(OrderGuid)).IsAllowed);
    }

    [Fact]
    public async Task 纯仓库员工_中文角色名同样适用()
    {
        var policy = CreatePolicy(new[] { "仓库员工" }, new[] { Permissions.Warehouse.StartPicking });

        Assert.True((await policy.RequireStartPickingAsync(OrderGuid)).IsAllowed);
    }

    [Fact]
    public async Task 纯仓库员工_只有宽泛的仓库与订单权限_仍然禁止()
    {
        // 员工角色模板自带 Warehouse.Manage；它与订单编辑权限都不能顶替「开始配货」。
        var policy = CreatePolicy(
            new[] { "WarehouseStaff" },
            new[]
            {
                Permissions.Warehouse.Manage,
                Permissions.Warehouse.ManageOrders,
                Permissions.Orders.Edit,
                Permissions.Warehouse.Picking,
            }
        );

        Assert.True((await policy.RequireStartPickingAsync(OrderGuid)).IsForbidden);
    }

    [Fact]
    public async Task 纯仓库员工_没有任何权限_禁止()
    {
        var policy = CreatePolicy(new[] { "WarehouseStaff" }, Array.Empty<string>());

        Assert.True((await policy.RequireStartPickingAsync(OrderGuid)).IsForbidden);
    }

    [Fact]
    public async Task 开始配货权限不放开员工的其它订单管理动作()
    {
        var policy = CreatePolicy(new[] { "WarehouseStaff" }, new[] { Permissions.Warehouse.StartPicking });

        Assert.True((await policy.RequireOrderEditAsync(OrderGuid)).IsForbidden);
        Assert.True((await policy.RequireOrderManagementEditAsync()).IsForbidden);
        Assert.True((await policy.RequireOrderLineMutationAsync(OrderGuid)).IsForbidden);
    }

    [Theory]
    [InlineData("WarehouseManager")]
    [InlineData("Admin")]
    public async Task 管理类账号_沿用订单编辑授权_无需开始配货权限(string role)
    {
        var policy = CreatePolicy(new[] { role }, new[] { Permissions.Warehouse.ManageOrders });

        Assert.True((await policy.RequireStartPickingAsync(OrderGuid)).IsAllowed);
    }

    [Fact]
    public async Task 非员工账号_没有订单编辑权限_仍然禁止()
    {
        var policy = CreatePolicy(new[] { "WarehouseManager" }, Array.Empty<string>());

        Assert.True((await policy.RequireStartPickingAsync(OrderGuid)).IsForbidden);
    }

    [Fact]
    public void 开始配货权限已登记且不被管理仓库权限别名带出()
    {
        Assert.Equal("Warehouse.StartPicking", Permissions.Warehouse.StartPicking);
        Assert.Contains(PermissionSeedData.AllPermissions, seed => seed.Code == Permissions.Warehouse.StartPicking);

        // 与 Warehouse.Picking 不同：持有 Manage / ManageOrders 不会自动具备「开始配货」，需显式授予。
        var expanded = Permissions.ExpandPermissionCodes(
            new[] { Permissions.Warehouse.Manage, Permissions.Warehouse.ManageOrders }
        );
        Assert.DoesNotContain(Permissions.Warehouse.StartPicking, expanded);
    }

    [Fact]
    public void 仓库员工角色模板默认具备开始配货权限()
    {
        var template = Assert.Single(
            PermissionSeedData.RolePermissionTemplates,
            item => item.RoleName == "WarehouseStaff"
        );

        Assert.Contains(Permissions.Warehouse.StartPicking, template.PermissionCodes);
        // 仍然不含订货管理与订单编辑权限，员工不能借此编辑订单。
        Assert.DoesNotContain(Permissions.Warehouse.ManageOrders, template.PermissionCodes);
        Assert.DoesNotContain(Permissions.Orders.Edit, template.PermissionCodes);
    }

    [Fact]
    public void 权限登记迁移脚本只登记该权限且不授予任何角色()
    {
        var script = File.ReadAllText(ResolveMigrationPath());

        Assert.Contains("N'Warehouse.StartPicking'", script);
        Assert.Contains("dbo.HbwebSysPermissions", script);
        Assert.DoesNotContain("INSERT INTO dbo.HbwebSysRolePermissions", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO dbo.HbwebSysUserPermissions", script, StringComparison.OrdinalIgnoreCase);
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

        var storeScope = new Mock<ICurrentUserManageableStoreScopeService>();
        storeScope
            .Setup(item => item.CanAccessOrderAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        return new StoreOrderAccessPolicy(
            new StoreOrderActorContext(httpContextAccessor.Object),
            authorization.Object,
            storeScope.Object,
            Mock.Of<IUserService>(),
            null!,
            httpContextAccessor.Object,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<StoreOrderAccessPolicy>.Instance
        );
    }

    private static string ResolveMigrationPath([System.Runtime.CompilerServices.CallerFilePath] string testFilePath = "")
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("无法解析测试文件目录");
        return Path.GetFullPath(
            Path.Combine(
                testDirectory,
                "..",
                "BlazorApp.Api",
                "Data",
                "Migrations",
                "20261006_AddWarehouseStartPickingPermission.sql"
            )
        );
    }
}
