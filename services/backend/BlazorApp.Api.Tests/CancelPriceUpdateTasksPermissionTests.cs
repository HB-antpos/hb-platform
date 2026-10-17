using System.Reflection;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 撤销价格变更任务的独立权限码 Warehouse.CancelPriceUpdateTasks：
/// 原先由 Warehouse.ManageProducts 守卫（Web 价格变更任务页已在用），拆码后必须做到
/// ① 已有 ManageProducts / Warehouse.Manage 的账号经别名自动具备，Web 原有撤销能力不丢；
/// ② 反过来只被授予新码的账号不会因此获得商品管理能力；
/// ③ 只有 cancel 接口换成新码，其余监控接口（查看、设置）仍是 ManageProducts。
/// </summary>
public sealed class CancelPriceUpdateTasksPermissionTests
{
    private const string Code = "Warehouse.CancelPriceUpdateTasks";

    [Fact]
    public void 权限码字符串固定且在种子里恰好登记一次()
    {
        Assert.Equal(Code, Permissions.Warehouse.CancelPriceUpdateTasks);

        var rows = PermissionSeedData.AllPermissions
            .Where(seed => string.Equals(seed.Code, Code, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var row = Assert.Single(rows);
        Assert.Equal("仓库管理", row.Category);
        Assert.False(string.IsNullOrWhiteSpace(row.Name));
        Assert.Contains("所有分店", row.Description);
        Assert.Contains("不可恢复", row.Description);
    }

    [Fact]
    public void 校验新码时ManageProducts与Warehouse_Manage同样放行()
    {
        var accepted = Permissions.GetEquivalentPermissionCodes(Code);

        Assert.Contains(Code, accepted);
        Assert.Contains(Permissions.Warehouse.ManageProducts, accepted);
        Assert.Contains(Permissions.Warehouse.Manage, accepted);
    }

    [Theory]
    [InlineData("Warehouse.ManageProducts")]
    [InlineData("Warehouse.Manage")]
    public void 已有仓库商品管理权限的账号展开后自动具备新码(string held)
    {
        var effective = Permissions.ExpandPermissionCodes(new[] { held });

        Assert.Contains(Code, effective, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void 只授予新码不会反向获得商品管理或仓库总权限()
    {
        var effective = Permissions.ExpandPermissionCodes(new[] { Code });

        Assert.Contains(Code, effective, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Permissions.Warehouse.ManageProducts, effective, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Permissions.Warehouse.Manage, effective, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void 只有cancel接口换成新码其余监控接口仍由ManageProducts守卫()
    {
        string? PolicyOf(string methodName) =>
            typeof(ReactStorePriceUpdateTaskMonitorController)
                .GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)!
                .GetCustomAttributes<AuthorizeAttribute>()
                .Select(attribute => attribute.Policy)
                .SingleOrDefault(policy => !string.IsNullOrEmpty(policy));

        Assert.Equal(Code, PolicyOf("Cancel"));
        Assert.Equal(Permissions.Warehouse.ManageProducts, PolicyOf("GetByProduct"));
        Assert.Equal(Permissions.Warehouse.ManageProducts, PolicyOf("GetTasks"));
        Assert.Equal(Permissions.Warehouse.ManageProducts, PolicyOf("GetSettings"));
    }
}
