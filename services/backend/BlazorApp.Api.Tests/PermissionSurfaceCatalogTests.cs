using System;
using System.Linq;
using BlazorApp.Shared.Constants;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class PermissionSurfaceCatalogTests
{
    [Fact]
    public void Resolve_PosTerminalPrefix_IsPosOnly()
    {
        Assert.Equal(
            PermissionSurface.Pos,
            PermissionSurfaceCatalog.Resolve("Permissions.PosTerminal.Sales.AddItem")
        );
        Assert.Equal(
            new[] { "pos" },
            PermissionSurfaceCatalog.ResolveNames(Permissions.PosTerminal.Sales.OrderManualDiscount)
        );
    }

    [Theory]
    // 「POS 管理 / POS 日结 / POS 审计」分类里的后台与 App 权限不能因为分类名叫 POS 就算收银端
    [InlineData("PosProducts.View", "web")]
    [InlineData("LegacyEmployeeLogs.View", "web")]
    [InlineData("DailyCloseRecords.View", "web,mobile")]
    // 考勤自助项只在移动端「个人考勤」生效；管理类两端共用
    [InlineData("Attendance.Punch.Self", "mobile")]
    [InlineData("Attendance.Schedule.ViewStore", "web,mobile")]
    [InlineData("Users.CreateStoreStaff", "mobile")]
    [InlineData("Users.View", "web,mobile")]
    [InlineData("Warehouse.CancelPriceUpdateTasks", "web,mobile")]
    [InlineData("SalesDashboard.SalesData.View", "web")]
    public void ResolveNames_KnownCodes_ReturnExpectedSurfaces(string code, string expected)
    {
        Assert.Equal(expected.Split(','), PermissionSurfaceCatalog.ResolveNames(code));
    }

    [Fact]
    public void Resolve_UnregisteredOrBlankCode_DefaultsToWeb()
    {
        Assert.Equal(PermissionSurface.Web, PermissionSurfaceCatalog.Resolve("Custom.Hand.Created"));
        Assert.Equal(PermissionSurface.Web, PermissionSurfaceCatalog.Resolve(null));
        Assert.Equal(PermissionSurface.Web, PermissionSurfaceCatalog.Resolve("  "));
        Assert.False(PermissionSurfaceCatalog.IsRegistered("Custom.Hand.Created"));
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        Assert.Equal(
            PermissionSurfaceCatalog.Resolve("Users.View"),
            PermissionSurfaceCatalog.Resolve("users.view")
        );
    }

    [Fact]
    public void EverySeededPermissionCode_IsRegisteredExplicitly()
    {
        // 新增权限码必须同步登记使用端，否则会静默落到默认的「后台」。
        var missing = PermissionSeedData.AllPermissions
            .Select(seed => seed.Code)
            .Where(code => !PermissionSurfaceCatalog.IsRegistered(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, "未登记使用端的权限码：" + string.Join(", ", missing));
    }
}
