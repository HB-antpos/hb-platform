using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BlazorApp.Api.Authorization;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public class NavigationServiceTests
{
    private const string InstallmentOrdersPermission = "InstallmentOrders.View";
    private const string StoreVouchersPermission = "StoreVouchers.View";

    private readonly NavigationService _service = new();

    public static IEnumerable<object[]> SalesDashboardPageMenuCases()
    {
        yield return new object[] { "SalesDashboard.SalesData.View", "/executive-sales-intelligence/overview" };
        yield return new object[] { "SalesDashboard.SalesDetail.View", "/executive-sales-intelligence/sales-detail-v2" };
        yield return new object[] { "SalesDashboard.CompactBoard.View", "/executive-sales-intelligence/compact-sales-board" };
        yield return new object[] { "SalesDashboard.MonthlyDailySalesDownload.View", "/executive-sales-intelligence/monthly-daily-sales-download" };
        yield return new object[] { "SalesDashboard.ProductMovement.View", "/executive-sales-intelligence/product-movement-report" };
        // 批量货号销量与分店进货销量分析已合并为「进货销量分析」一页，任一权限都只点亮这一项。
        yield return new object[] { "SalesDashboard.BatchProductSales.View", "/executive-sales-intelligence/purchase-sales-analysis" };
        yield return new object[] { "SalesDashboard.WarehouseFlow.View", "/executive-sales-intelligence/warehouse-product-flow-analysis" };
        yield return new object[] { "SalesDashboard.LocalProductAnalysis.View", "/executive-sales-intelligence/local-product-sales-analysis" };
        yield return new object[] { "SalesDashboard.PurchaseAmount.View", "/executive-sales-intelligence/purchase-amount-dashboard" };
        yield return new object[] { "SalesDashboard.LocalSupplierPurchaseSales.View", "/executive-sales-intelligence/purchase-sales-analysis" };
    }

    [Theory]
    [MemberData(nameof(SalesDashboardPageMenuCases))]
    public void BuildMenu_SalesDashboardPermissionShowsOnlyItsOwnPage(
        string permission,
        string expectedPath
    )
    {
        var menu = _service.BuildMenu(CreateUser(new Claim("permission", permission)));

        var salesMenu = Assert.Single(
            menu,
            item => item.Path == "/executive-sales-intelligence"
        );
        Assert.Equal(new[] { expectedPath }, salesMenu.Children!.Select(item => item.Path));
    }

    [Fact]
    public void BuildMenu_PurchaseSalesAnalysisShowsOnceWhenBothTabPermissionsGranted()
    {
        var menu = _service.BuildMenu(CreateUser(
            new Claim("permission", Permissions.SalesDashboard.BatchProductSalesView),
            new Claim("permission", Permissions.SalesDashboard.LocalSupplierPurchaseSalesView)
        ));

        var salesMenu = Assert.Single(menu, item => item.Path == "/executive-sales-intelligence");
        var entry = Assert.Single(salesMenu.Children!);
        Assert.Equal("/executive-sales-intelligence/purchase-sales-analysis", entry.Path);
        Assert.Equal("menu.purchaseSalesAnalysis", entry.TitleKey);
        // 旧的两个独立入口不能与合并入口同时出现。
        Assert.DoesNotContain(salesMenu.Children!, item =>
            item.Path.EndsWith("/batch-product-sales-analysis")
            || item.Path.EndsWith("/local-supplier-purchase-sales-analysis"));
    }

    [Theory]
    [InlineData(Permissions.Reports.View)]
    [InlineData(Permissions.Reports.ProductMovementView)]
    [InlineData(Permissions.LocalPurchase.View)]
    public void BuildMenu_LegacyBroadPermissionDoesNotGrantSalesDashboardPages(string permission)
    {
        var menu = _service.BuildMenu(CreateUser(new Claim("permission", permission)));

        Assert.DoesNotContain(menu, item => item.Path == "/executive-sales-intelligence");
    }

    [Fact]
    public void BuildMenu_HidesBackendNavigationWithoutDashboardPermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.Attendance.Schedule.ViewStore));

        var menu = _service.BuildMenu(user);

        Assert.Empty(menu);
    }

    [Fact]
    public void BuildMenu_ShowsProductMovementReportWithoutDashboardPermission()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.SalesDashboard.ProductMovementView)
        );

        var menu = _service.BuildMenu(user);

        var salesIntelligence = Assert.Single(menu, item => item.Path == "/executive-sales-intelligence");
        Assert.Contains(
            salesIntelligence.Children!,
            item => item.Path == "/executive-sales-intelligence/product-movement-report"
        );
        Assert.DoesNotContain(
            salesIntelligence.Children!,
            item => item.Path == "/executive-sales-intelligence/sales-detail-v2"
        );
    }

    [Fact]
    public void BuildMenu_WarehouseFlowClaimShowsWarehouseFlowPage()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.SalesDashboard.WarehouseFlowView)
        );

        var menu = _service.BuildMenu(user);

        var salesIntelligence = Assert.Single(
            menu,
            item => item.Path == "/executive-sales-intelligence"
        );
        var salesPage = Assert.Single(salesIntelligence.Children!);
        Assert.Equal(
            "/executive-sales-intelligence/warehouse-product-flow-analysis",
            salesPage.Path
        );
    }

    [Fact]
    public void BuildMenu_WarehouseStaffWithSalesPermissionShowsOnlyThatSalesPage()
    {
        var user = CreateUser(
            new Claim(ClaimTypes.Role, "WarehouseStaff"),
            new Claim("permission", Permissions.SalesDashboard.PurchaseAmountView)
        );

        var menu = _service.BuildMenu(user);

        var salesMenu = Assert.Single(menu);
        Assert.Equal("/executive-sales-intelligence", salesMenu.Path);
        var salesPage = Assert.Single(salesMenu.Children!);
        Assert.Equal(
            "/executive-sales-intelligence/purchase-amount-dashboard",
            salesPage.Path
        );
    }

    [Fact]
    public async Task BuildMenu_LegacyProductMovementPermissionDoesNotShowNewSalesPages()
    {
        using var harness = new NavigationTestHarness();
        await harness.SeedUserWithRoleAsync(
            "user-1",
            "role-user",
            "User",
            Permissions.Reports.View
        );

        var service = harness.CreateNavigationService();
        var user = CreateUserWithId("user-1");

        var menu = service.BuildMenu(user);

        Assert.DoesNotContain(menu, item => item.Path == "/executive-sales-intelligence");
    }

    [Fact]
    public async Task BuildMenu_WarehouseFlowShownWithIndependentPermission()
    {
        using var harness = new NavigationTestHarness();
        await harness.SeedUserWithRoleAsync(
            "user-1",
            "role-user",
            "User",
            Permissions.SalesDashboard.WarehouseFlowView
        );

        var service = harness.CreateNavigationService();
        var user = CreateUserWithId("user-1");

        var menu = service.BuildMenu(user);

        var salesIntelligence = Assert.Single(
            menu,
            item => item.Path == "/executive-sales-intelligence"
        );
        var productSalesAnalysis = Assert.Single(
            salesIntelligence.Children!,
            item => item.Path == "/executive-sales-intelligence/warehouse-product-flow-analysis"
        );
        Assert.Equal("menu.warehouseProductFlowAnalysis", productSalesAnalysis.TitleKey);
        Assert.Equal(Permissions.SalesDashboard.WarehouseFlowView, productSalesAnalysis.Permission);
        Assert.False(productSalesAnalysis.RequireExactPermission);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("管理员")]
    [InlineData("SuperAdmin")]
    [InlineData("超级管理员")]
    public async Task BuildMenu_SuperAdminAliasShowsProductSalesAnalysis(string roleName)
    {
        using var harness = new NavigationTestHarness();
        await harness.SeedUserWithRoleAsync("user-1", "role-admin", roleName);

        var service = harness.CreateNavigationService();
        var user = CreateUserWithId("user-1");

        var menu = service.BuildMenu(user);

        var salesIntelligence = Assert.Single(
            menu,
            item => item.Path == "/executive-sales-intelligence"
        );
        Assert.Equal(
            // 两个页面权限共用「进货销量分析」一个入口，期望路径需去重。
            SalesDashboardPageMenuCases()
                .Select(values => Assert.IsType<string>(values[1]))
                .Distinct()
                .OrderBy(path => path),
            salesIntelligence.Children!.Select(item => item.Path).OrderBy(path => path)
        );
    }

    [Fact]
    public void BuildMenu_LocalPurchaseViewKeepsInvoiceMenusWithoutNewSalesPages()
    {
        var user = CreateUser(new Claim("permission", Permissions.LocalPurchase.View));

        var menu = _service.BuildMenu(user);

        Assert.DoesNotContain(menu, item => item.Path == "/executive-sales-intelligence");
        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        Assert.Contains(
            posAdmin.Children!,
            item => item.Path == "/pos-admin/local-supplier-invoices"
        );
    }

    [Fact]
    public void BuildMenu_ShowsSystemNavigationWithViewAppDownloadsWithoutDashboardPermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.System.ViewAppDownloads));

        var menu = _service.BuildMenu(user);

        var systemMenu = Assert.Single(menu, item => item.Path == "/system");
        Assert.Contains(systemMenu.Children!, item => item.Path == "/system/app-downloads");
        Assert.DoesNotContain(systemMenu.Children!, item => item.Path == "/system/wpf-versions");
        Assert.DoesNotContain(menu, item => item.Path == "/dashboard");
    }

    [Fact]
    public void BuildMenu_ShowsSystemNavigationWithManageAppDownloadsWithoutDashboardPermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.System.ManageAppDownloads));

        var menu = _service.BuildMenu(user);

        var systemMenu = Assert.Single(menu, item => item.Path == "/system");
        Assert.Contains(systemMenu.Children!, item => item.Path == "/system/app-downloads");
        Assert.DoesNotContain(systemMenu.Children!, item => item.Path == "/system/wpf-versions");
        Assert.DoesNotContain(menu, item => item.Path == "/dashboard");
    }

    [Fact]
    public void BuildMenu_SystemUsersRequiresWebConsolePermissionInsteadOfUsersView()
    {
        // Users.View 只点亮移动端员工列表/用户管理，Web 后台用户管理菜单需要独立的 Users.ViewWebConsole。
        // Dashboard.View 仅用于满足后台导航准入门槛，与用户管理菜单本身无关。
        var authorized = CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.Users.ViewWebConsole)
        );
        var usersViewOnly = CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.Users.View)
        );

        var authorizedSystem = Assert.Single(
            _service.BuildMenu(authorized),
            item => item.Path == "/system"
        );
        // 用户管理已并入「账号与权限」合并页：合并页只认 Users.ViewWebConsole，不认 Users.View。
        var usersMenu = Assert.Single(
            authorizedSystem.Children!,
            item => item.Path == "/system/accounts"
        );
        Assert.Contains(Permissions.Users.ViewWebConsole, usersMenu.AnyPermissions!);
        Assert.DoesNotContain(Permissions.Users.View, usersMenu.AnyPermissions!);

        Assert.DoesNotContain(
            _service.BuildMenu(usersViewOnly).SelectMany(item => item.Children ?? new List<NavigationMenuDto>()),
            item => item.Path == "/system/accounts"
        );
    }

    [Fact]
    public void BuildMenu_EmergencyLoginKeysRequiresSystemManageSettings()
    {
        var authorized = CreateUser(
            new Claim("permission", Permissions.System.ManageSettings)
        );
        var unauthorized = CreateUser(
            new Claim("permission", Permissions.System.ViewAppDownloads)
        );

        var authorizedSystem = Assert.Single(
            _service.BuildMenu(authorized),
            item => item.Path == "/system"
        );
        // 紧急登录密钥已并入「设备与远程」合并页：菜单项是合并页，System.ManageSettings 即可见。
        var keyMenu = Assert.Single(
            authorizedSystem.Children!,
            item => item.Path == "/system/devices"
        );
        Assert.Equal("menu.groupDevicesRemote", keyMenu.TitleKey);
        Assert.Contains(Permissions.System.ManageSettings, keyMenu.AnyPermissions!);

        var unauthorizedSystem = Assert.Single(
            _service.BuildMenu(unauthorized),
            item => item.Path == "/system"
        );
        Assert.DoesNotContain(
            unauthorizedSystem.Children!,
            item => item.Path == "/system/devices"
        );
    }

    [Fact]
    public async Task BuildMenu_UsesDatabasePermissionsInsteadOfStalePermissionClaims()
    {
        using var harness = new NavigationTestHarness();
        await harness.SeedUserWithRoleAsync("user-1", "role-store", "StoreManager");

        var service = harness.CreateNavigationService();
        var user = CreateUserWithId(
            "user-1",
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.Attendance.Schedule.ViewStore)
        );

        var menu = service.BuildMenu(user);

        Assert.Empty(menu);
    }

    [Fact]
    public async Task BuildMenu_UsesDatabaseAggregatedPermissionsForAuthenticatedUser()
    {
        using var harness = new NavigationTestHarness();
        await harness.SeedUserWithRoleAsync(
            "user-1",
            "role-store",
            "StoreManager",
            Permissions.Dashboard.View,
            Permissions.Attendance.Schedule.ViewStore
        );

        var service = harness.CreateNavigationService();
        var user = CreateUserWithId("user-1");

        var menu = service.BuildMenu(user);

        Assert.Contains(menu, item => item.Path == "/dashboard");
        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        Assert.Contains(posAdmin.Children!, item => item.Path == "/pos-admin/attendance");
    }

    [Fact]
    public async Task BuildMenu_UsesDatabaseAdminRoleWithoutExplicitPermissions()
    {
        using var harness = new NavigationTestHarness();
        await harness.SeedUserWithRoleAsync("user-1", "role-admin", "管理员");

        var service = harness.CreateNavigationService();
        var user = CreateUserWithId("user-1");

        var menu = service.BuildMenu(user);

        Assert.Equal(7, menu.Count);
        Assert.Contains(menu, item => item.Path == "/dashboard");
        Assert.Contains(menu, item => item.Path == "/seasonal-preorders");
        Assert.Contains(menu, item => item.Path == "/system");
        Assert.Contains(menu, item => item.Path == "/warehouse");
        Assert.Contains(menu, item => item.Path == "/domestic-purchase");
        Assert.Contains(menu, item => item.Path == "/executive-sales-intelligence");
        Assert.Contains(menu, item => item.Path == "/pos-admin");
    }

    [Fact]
    public void BuildMenu_ShowsFullMenuForAdmin()
    {
        var user = CreateUser(new Claim(ClaimTypes.Role, "Admin"));

        var menu = _service.BuildMenu(user);

        Assert.Equal(7, menu.Count);
        Assert.Contains(menu, item => item.Path == "/dashboard");
        Assert.Contains(menu, item => item.Path == "/seasonal-preorders");
        Assert.Contains(menu, item => item.Path == "/system");
        Assert.Contains(menu, item => item.Path == "/warehouse");
        Assert.Contains(menu, item => item.Path == "/domestic-purchase");
        Assert.Contains(menu, item => item.Path == "/executive-sales-intelligence");
        Assert.Contains(menu, item => item.Path == "/pos-admin");

        var systemMenu = Assert.Single(menu, item => item.Path == "/system");
        Assert.Contains(systemMenu.Children!, item => item.Path == "/system/accounts");
        Assert.Contains(systemMenu.Children!, item => item.Path == "/system/app-downloads");

        var posAdminMenu = Assert.Single(menu, item => item.Path == "/pos-admin");
        Assert.Contains(posAdminMenu.Children!, item => item.Path == "/pos-admin/products-pricing");
        Assert.Contains(posAdminMenu.Children!, item => item.Path == "/pos-admin/local-supplier-invoices");
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("管理员")]
    [InlineData("SuperAdmin")]
    [InlineData("超级管理员")]
    public void BuildMenu_四种系统管理员别名均显示结算与现金合并页(string roleName)
    {
        var user = CreateUser(new Claim(ClaimTypes.Role, roleName));

        var menu = _service.BuildMenu(user);

        // Linkly 结算记录（仅管理员）已并入「结算与现金」合并页，页内标签仍按管理员身份显示。
        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        var item = Assert.Single(
            posAdmin.Children!,
            child => child.Path == "/pos-admin/settlement-cash"
        );
        Assert.Equal("menu.groupSettlementCash", item.TitleKey);
        Assert.Equal("MoneyCollectOutlined", item.Icon);
        Assert.DoesNotContain(posAdmin.Children!, child => child.Path == "/pos-admin/linkly-settlements");
    }

    [Theory]
    [InlineData(Permissions.Dashboard.View)]
    [InlineData(Permissions.PosTerminal.Audit.View)]
    [InlineData(Permissions.System.ManageSettings)]
    public void BuildMenu_普通后台用户不显示Linkly结算(string permission)
    {
        var user = CreateUser(new Claim("permission", permission));

        var menu = _service.BuildMenu(user);

        Assert.DoesNotContain(
            menu.SelectMany(item => item.Children ?? new List<NavigationMenuDto>()),
            child => child.Path == "/pos-admin/linkly-settlements"
        );
    }

    [Fact]
    public void FilterMenu_复制父叶节点时保留RequireAdmin标记()
    {
        var contextType = typeof(NavigationService).GetNestedType(
            "NavigationPermissionContext",
            BindingFlags.NonPublic
        ) ?? throw new InvalidOperationException("NavigationPermissionContext was not found.");
        var context = Activator.CreateInstance(contextType)
            ?? throw new InvalidOperationException("NavigationPermissionContext could not be created.");
        contextType.GetProperty("IsAdmin")!.SetValue(context, true);

        var filterMenu = typeof(NavigationService).GetMethod(
            "FilterMenu",
            BindingFlags.NonPublic | BindingFlags.Static
        ) ?? throw new InvalidOperationException("FilterMenu was not found.");
        var source = new List<NavigationMenuDto>
        {
            new()
            {
                Path = "/admin-parent",
                TitleKey = "menu.adminParent",
                Icon = "SettingOutlined",
                RequireAdmin = true,
                Children = new List<NavigationMenuDto>
                {
                    new()
                    {
                        Path = "/admin-parent/admin-leaf",
                        TitleKey = "menu.adminLeaf",
                        Icon = "ReconciliationOutlined",
                        RequireAdmin = true,
                    },
                },
            },
        };

        var filtered = (List<NavigationMenuDto>)filterMenu.Invoke(
            null,
            new[] { source, context }
        )!;

        var parent = Assert.Single(filtered);
        Assert.True(parent.RequireAdmin);
        Assert.True(Assert.Single(parent.Children!).RequireAdmin);
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("超级管理员")]
    public void BuildMenu_ShowsFullMenuForSuperAdminAliases(string roleName)
    {
        var user = CreateUser(new Claim(ClaimTypes.Role, roleName));

        var menu = _service.BuildMenu(user);

        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        Assert.Contains(posAdmin.Children!, item => item.Path == "/pos-admin/operation-logs");
        Assert.Contains(menu, item => item.Path == "/system");
    }

    [Fact]
    public void BuildMenu_ShowsDashboardAndAuthorizedModuleWithDashboardPermission()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.Attendance.Schedule.ViewStore)
        );

        var menu = _service.BuildMenu(user);

        Assert.Contains(menu, item => item.Path == "/dashboard");
        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        Assert.Contains(posAdmin.Children!, item => item.Path == "/pos-admin/attendance");
        Assert.DoesNotContain(posAdmin.Children!, item => item.Path == "/pos-admin/sales-orders");
    }

    [Fact]
    public void BuildMenu_ShowsAttendanceGroupForApprovalReviewPermissionOnly()
    {
        // 考勤审核已并入「排班考勤」合并页：审核权限显示合并页入口（页内只剩审核标签），不显示工时与工资
        var reviewer = _service.BuildMenu(CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.Attendance.Approval.ReviewManagedStore)));
        var reviewerPosAdmin = Assert.Single(reviewer, item => item.Path == "/pos-admin");
        var attendance = Assert.Single(reviewerPosAdmin.Children!, item => item.Path == "/pos-admin/attendance");
        Assert.Equal("menu.scheduleAttendance", attendance.TitleKey);
        Assert.Equal("CalendarOutlined", attendance.Icon);
        Assert.DoesNotContain(reviewerPosAdmin.Children!, item => item.Path == "/pos-admin/payroll");
        // 旧叶子不再单独出现在菜单里
        Assert.DoesNotContain(reviewerPosAdmin.Children!, item => item.Path == "/pos-admin/attendance-review");

        // 只有查看审批、请假审核权限：与 Web 一致，不显示审核工作台（合并页也不出现）
        var viewer = _service.BuildMenu(CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.Attendance.Approval.ViewManagedStore),
            new Claim("permission", Permissions.Attendance.Leave.ReviewManagedStore)));
        Assert.DoesNotContain(
            viewer.SelectMany(item => item.Children ?? new List<NavigationMenuDto>()),
            item => item.Path == "/pos-admin/attendance");
    }

    [Theory]
    [InlineData(Permissions.Attendance.Punch.ViewManagedStore)]
    [InlineData(Permissions.Attendance.Payroll.View)]
    [InlineData(Permissions.Myob.Connection.Manage)]
    public void BuildMenu_ShowsPayrollGroupWithPunchViewPayrollViewOrMyobPermission(string permission)
    {
        // 工时与工资合并页含工时统计 / 工资表 / 时薪表 / MYOB 对接，任一权限即可见
        var menu = _service.BuildMenu(CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", permission)));

        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        var payroll = Assert.Single(posAdmin.Children!, item => item.Path == "/pos-admin/payroll");
        Assert.Equal("menu.attendancePayroll", payroll.TitleKey);
        Assert.Equal("AccountBookOutlined", payroll.Icon);
        Assert.DoesNotContain(posAdmin.Children!, item => item.Path == "/pos-admin/attendance");
        // 旧叶子不再单独出现在菜单里
        Assert.DoesNotContain(posAdmin.Children!, item => item.Path == "/pos-admin/attendance-payroll");
        Assert.DoesNotContain(posAdmin.Children!, item => item.Path == "/pos-admin/myob");
    }

    [Theory]
    [InlineData(Permissions.Attendance.Payroll.Edit)]
    [InlineData(Permissions.Attendance.Payroll.Unlock)]
    [InlineData(Permissions.Attendance.Admin.View)]
    public void BuildMenu_HidesPayrollGroupWithoutPunchViewPayrollViewOrMyobPermission(string permission)
    {
        // 工资编辑 / 解锁不隐含查看；Attendance.Admin.View 也不连带工资权限与 MYOB 对接
        var menu = _service.BuildMenu(CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", permission)));

        Assert.DoesNotContain(
            menu.SelectMany(item => item.Children ?? new List<NavigationMenuDto>()),
            item => item.Path == "/pos-admin/payroll");
    }

    [Fact]
    public void BuildMenu_ShowsLocalSupplierInvoicesWithLocalPurchasePermission()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.LocalPurchase.View)
        );

        var menu = _service.BuildMenu(user);

        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        var item = Assert.Single(
            posAdmin.Children!,
            child => child.Path == "/pos-admin/local-supplier-invoices"
        );
        Assert.Equal(Permissions.LocalPurchase.View, item.Permission);

        // 分店进货销量分析已并入销售看板「进货销量分析」并使用独立权限，本地进货权限不再点亮它，也不显示销售看板父菜单。
        Assert.DoesNotContain(
            posAdmin.Children!,
            child => child.Path.Contains("purchase-sales-analysis")
        );
        Assert.DoesNotContain(menu, item => item.Path == "/executive-sales-intelligence");
    }

    [Fact]
    public void BuildMenu_ShowsAdvertisementsWithAdvertisementViewPermission()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.Advertisements.View)
        );

        var menu = _service.BuildMenu(user);

        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        Assert.Contains(
            posAdmin.Children!,
            item =>
                item.Path == "/pos-admin/advertisements"
                && item.Permission == Permissions.Advertisements.View
        );
    }

    [Theory]
    [InlineData(Permissions.PosTerminal.Audit.View)]
    [InlineData(Permissions.LegacyEmployeeLogs.View)]
    public void BuildMenu_员工操作日志合并入口_老收银或新收银任一权限可见(string permission)
    {
        var user = CreateUser(new Claim("permission", permission));

        var menu = _service.BuildMenu(user);

        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        var operationLogs = Assert.Single(posAdmin.Children!);
        Assert.Equal("/pos-admin/operation-logs", operationLogs.Path);
        Assert.Equal("menu.operationLogs", operationLogs.TitleKey);
        Assert.Equal(
            new[] { Permissions.LegacyEmployeeLogs.View, Permissions.PosTerminal.Audit.View },
            operationLogs.AnyPermissions
        );
    }

    [Fact]
    public void BuildMenu_日结记录仅凭独立查看权限可见且位于收银管理分组()
    {
        var user = CreateUser(new Claim("permission", Permissions.DailyCloseRecords.View));

        var menu = _service.BuildMenu(user);

        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        // 日结记录已并入「结算与现金」合并页，仅凭 DailyCloseRecords.View 即可见。
        var dailyCloses = Assert.Single(posAdmin.Children!);
        Assert.Equal("/pos-admin/settlement-cash", dailyCloses.Path);
        Assert.Equal("menu.groupSettlementCash", dailyCloses.TitleKey);
        Assert.Equal("MoneyCollectOutlined", dailyCloses.Icon);
        Assert.Contains(Permissions.DailyCloseRecords.View, dailyCloses.AnyPermissions!);
        Assert.False(dailyCloses.RequireAdmin);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("管理员")]
    [InlineData("SuperAdmin")]
    [InlineData("超级管理员")]
    public void BuildMenu_系统管理员别名均显示日结记录(string roleName)
    {
        var user = CreateUser(new Claim(ClaimTypes.Role, roleName));

        var menu = _service.BuildMenu(user);

        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        Assert.Single(posAdmin.Children!, child => child.Path == "/pos-admin/settlement-cash");
    }

    [Theory]
    [InlineData(Permissions.Dashboard.View)]
    [InlineData(Permissions.PosTerminal.Audit.View)]
    [InlineData(Permissions.LegacyEmployeeLogs.View)]
    // 收银机上操作日结的权限（PosTerminal.DailyClose.*）与后台查看日结记录是两回事，不能互相授予。
    [InlineData(Permissions.PosTerminal.DailyClose.View)]
    [InlineData(Permissions.PosTerminal.DailyClose.Save)]
    [InlineData(Permissions.PosTerminal.DailyClose.Reprint)]
    public void BuildMenu_没有日结记录查看权限的用户不显示日结记录(string permission)
    {
        var user = CreateUser(new Claim("permission", permission));

        var menu = _service.BuildMenu(user);

        Assert.DoesNotContain(
            menu.SelectMany(item => item.Children ?? new List<NavigationMenuDto>()),
            child => child.Path == "/pos-admin/daily-closes"
        );
    }

    [Fact]
    public void BuildMenu_贺卡填报统计仅凭查看全部分店权限可见且位于收银管理分组()
    {
        // 只有 ViewAllStores 的总部账号也要拿到后台菜单（HasBackendNavigationAccess），且只看到这一页。
        var user = CreateUser(new Claim("permission", Permissions.SeasonalCards.Remaining.ViewAllStores));

        var menu = _service.BuildMenu(user);

        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        var stats = Assert.Single(posAdmin.Children!);
        Assert.Equal("/pos-admin/seasonal-card-stats", stats.Path);
        Assert.Equal("menu.seasonalCardStats", stats.TitleKey);
        Assert.Equal("GiftOutlined", stats.Icon);
        Assert.Equal(Permissions.SeasonalCards.Remaining.ViewAllStores, stats.Permission);
        Assert.False(stats.RequireAdmin);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("管理员")]
    [InlineData("SuperAdmin")]
    [InlineData("超级管理员")]
    public void BuildMenu_系统管理员别名均显示贺卡填报统计(string roleName)
    {
        var user = CreateUser(new Claim(ClaimTypes.Role, roleName));

        var menu = _service.BuildMenu(user);

        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        Assert.Single(posAdmin.Children!, child => child.Path == "/pos-admin/seasonal-card-stats");
    }

    [Theory]
    [InlineData(Permissions.Dashboard.View)]
    // 店长在移动端填报 / 查看本店的权限，不能打开全部分店统计。
    [InlineData(Permissions.SeasonalCards.Remaining.ViewManagedStore)]
    [InlineData(Permissions.SeasonalCards.Remaining.SubmitManagedStore)]
    public void BuildMenu_没有查看全部分店权限的用户不显示贺卡填报统计(string permission)
    {
        var user = CreateUser(new Claim("permission", permission));

        var menu = _service.BuildMenu(user);

        Assert.DoesNotContain(
            menu.SelectMany(item => item.Children ?? new List<NavigationMenuDto>()),
            child => child.Path == "/pos-admin/seasonal-card-stats"
        );
    }

    [Fact]
    public void BuildMenu_不再单列老系统操作日志入口()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.PosTerminal.Audit.View),
            new Claim("permission", Permissions.LegacyEmployeeLogs.View)
        );

        var menu = _service.BuildMenu(user);

        var children = menu.SelectMany(item => item.Children ?? new List<NavigationMenuDto>()).ToList();
        Assert.DoesNotContain(children, child => child.Path == "/pos-admin/legacy-employee-logs");
        Assert.Single(children, child => child.Path == "/pos-admin/operation-logs");
    }

    [Fact]
    public void BuildMenu_ShowsWarehouseContainerWithContainerViewPermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.Container.View));

        var menu = _service.BuildMenu(user);

        var warehouse = Assert.Single(menu, item => item.Path == "/warehouse");
        var item = Assert.Single(warehouse.Children!);
        Assert.Equal("/warehouse/containers", item.Path);
        Assert.Equal(Permissions.Container.View, item.Permission);
    }

    [Fact]
    public void BuildMenu_ShowsPreorderManagementWithWarehouseOrderPermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.Warehouse.ManageOrders));

        var menu = _service.BuildMenu(user);

        var warehouse = Assert.Single(menu, item => item.Path == "/warehouse");
        var preorder = Assert.Single(
            warehouse.Children!,
            item => item.Path == "/warehouse/preorders"
        );
        Assert.Equal("menu.preorders", preorder.TitleKey);
        Assert.Equal(Permissions.Warehouse.ManageOrders, preorder.Permission);
    }

    [Fact]
    public void BuildMenu_DoesNotUnlockNavigationForWarehouseManagerRoleWithoutPermissionClaims()
    {
        var user = CreateUser(new Claim(ClaimTypes.Role, "WarehouseManager"));

        var menu = _service.BuildMenu(user);

        Assert.Empty(menu);
    }

    [Fact]
    public void BuildMenu_LimitsWarehouseStaffClaimsToStoreOrdersOnly()
    {
        var user = CreateUser(
            new Claim(ClaimTypes.Role, "WarehouseStaff"),
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.Warehouse.Manage),
            new Claim("permission", Permissions.Warehouse.ManageProducts),
            new Claim("permission", Permissions.Warehouse.ManageLocations),
            new Claim("permission", Permissions.Orders.View)
        );

        var menu = _service.BuildMenu(user);

        AssertWarehouseStaffStoreOrderMenuOnly(menu);
    }

    [Fact]
    public async Task BuildMenu_LimitsWarehouseStaffDatabasePermissionsToStoreOrdersOnly()
    {
        using var harness = new NavigationTestHarness();
        await harness.SeedUserWithRoleAsync(
            "user-1",
            "role-warehouse-staff",
            "WarehouseStaff",
            Permissions.Dashboard.View,
            Permissions.Warehouse.Manage,
            Permissions.Warehouse.ManageProducts,
            Permissions.Warehouse.ManageLocations,
            Permissions.Orders.View
        );

        var service = harness.CreateNavigationService();
        var user = CreateUserWithId("user-1");

        var menu = service.BuildMenu(user);

        AssertWarehouseStaffStoreOrderMenuOnly(menu);
    }

    [Fact]
    public void BuildMenu_ShowsDeviceRegistrationWithDeviceRegistrationViewPermission()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.DeviceRegistration.View)
        );

        var menu = _service.BuildMenu(user);

        var systemMenu = Assert.Single(menu, item => item.Path == "/system");
        // 设备管理已并入「设备与远程」合并页。
        var item = Assert.Single(
            systemMenu.Children!,
            child => child.Path == "/system/devices"
        );
        Assert.Contains(Permissions.DeviceRegistration.View, item.AnyPermissions!);
    }

    [Fact]
    public void BuildAppMenu_ShowsDeviceManagementWithMobileActivationCodePermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.DeviceRegistration.MobileActivationCodes.Manage));

        var menu = _service.BuildAppMenu(user);

        Assert.Contains(menu, item => item.RouteName == "device-management");
    }

    [Fact]
    public void BuildMenu_ShowsAppDownloadsWithAppDownloadsPermission()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.System.ViewAppDownloads)
        );

        var menu = _service.BuildMenu(user);

        var systemMenu = Assert.Single(menu, item => item.Path == "/system");
        var item = Assert.Single(systemMenu.Children!, child => child.Path == "/system/app-downloads");
        Assert.Equal(Permissions.System.ViewAppDownloads, item.Permission);

        // WPF 版本已并入版本发布中心，不再单独出现在 Web 菜单。
        Assert.DoesNotContain(systemMenu.Children!, child => child.Path == "/system/wpf-versions");
    }

    [Fact]
    public void BuildMenu_HidesAppDownloadsWithoutAppDownloadsPermission()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.DeviceRegistration.View)
        );

        var menu = _service.BuildMenu(user);

        var systemMenu = Assert.Single(menu, item => item.Path == "/system");
        Assert.DoesNotContain(systemMenu.Children!, child => child.Path == "/system/app-downloads");
        Assert.DoesNotContain(systemMenu.Children!, child => child.Path == "/system/wpf-versions");
    }

    [Fact]
    public void BuildMenu_ShowsEmployeeProfilesWithEmployeeProfileViewManagedStorePermission()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.EmployeeProfiles.ViewManagedStore)
        );

        var menu = _service.BuildMenu(user);

        var systemMenu = Assert.Single(menu, item => item.Path == "/system");
        // 员工个人信息已并入「账号与权限」合并页。
        Assert.Contains(
            systemMenu.Children!,
            item =>
                item.Path == "/system/accounts"
                && item.AnyPermissions!.Contains(Permissions.EmployeeProfiles.ViewManagedStore)
                && !item.AnyPermissions!.Contains(Permissions.EmployeeProfiles.View)
        );
    }

    [Fact]
    public void BuildMenu_ShowsPosProductsWithPosProductsViewPermission()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.PosProducts.View)
        );

        var menu = _service.BuildMenu(user);

        var posAdminMenu = Assert.Single(menu, item => item.Path == "/pos-admin");
        // 商品管理已并入「商品与价格」合并页。
        Assert.Contains(
            posAdminMenu.Children!,
            item =>
                item.Path == "/pos-admin/products-pricing"
                && item.AnyPermissions!.Contains(Permissions.PosProducts.View)
        );
    }

    [Fact]
    public void BuildMenu_HidesDeviceRegistrationWithStoreManageOperationsPermissionOnly()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.Store.ManageOperations)
        );

        var menu = _service.BuildMenu(user);

        var systemMenu = menu.SingleOrDefault(item => item.Path == "/system");
        Assert.True(
            systemMenu is null
                || systemMenu.Children is null
                || !systemMenu.Children.Any(child => child.Path == "/system/device-registration")
        );
    }

    [Fact]
    public void BuildMenu_HidesBackendNavigationForOrderFrontOnlyUser()
    {
        var user = CreateUser(new Claim("permission", Permissions.OrderFront.View));

        var menu = _service.BuildMenu(user);

        Assert.Empty(menu);
    }

    [Fact]
    public void BuildMenu_PureOrderRolesDoNotReceiveWebNavigation()
    {
        var user = CreateUser(
            new Claim(ClaimTypes.Role, "Order"),
            new Claim(ClaimTypes.Role, "订货员"),
            new Claim("permission", Permissions.Dashboard.View),
            new Claim("permission", Permissions.SalesDashboard.SalesDataView)
        );

        var menu = _service.BuildMenu(user);

        Assert.Empty(menu);
    }

    [Fact]
    public void BuildMenu_OrderRoleWithAnotherRoleKeepsItsNormalAuthorizedNavigation()
    {
        var user = CreateUser(
            new Claim(ClaimTypes.Role, "Order"),
            new Claim(ClaimTypes.Role, "StoreManager"),
            new Claim("permission", Permissions.Dashboard.View)
        );

        var menu = _service.BuildMenu(user);

        Assert.Contains(menu, item => item.Path == "/dashboard");
    }

    [Fact]
    public void BuildAppMenu_HidesEmployeeProfileWithoutEmployeeProfilePermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.Orders.View));

        var menu = _service.BuildAppMenu(user);

        Assert.DoesNotContain(menu, item => item.RouteName == "employee-profile");
    }

    [Fact]
    public async Task BuildAppMenu_UsesDatabasePermissionsInsteadOfStaleClaims()
    {
        using var harness = new NavigationTestHarness();
        await harness.SeedUserWithRoleAsync("user-1", "role-user", "User");

        var service = harness.CreateNavigationService();
        var user = CreateUserWithId("user-1", new Claim("permission", Permissions.LocalPurchase.View));

        var menu = service.BuildAppMenu(user);

        Assert.DoesNotContain(menu, item => item.RouteName == "local-supplier-invoices");
    }

    [Fact]
    public async Task BuildAppMenu_UsesDatabaseAliasPermissionsForAuthenticatedUser()
    {
        using var harness = new NavigationTestHarness();
        await harness.SeedUserWithRoleAsync(
            "user-1",
            "role-user",
            "User",
            "LocalInvocie.View"
        );

        var service = harness.CreateNavigationService();
        var user = CreateUserWithId("user-1");

        var menu = service.BuildAppMenu(user);

        Assert.Contains(menu, item => item.RouteName == "local-supplier-invoices");
    }

    [Fact]
    public void BuildMenu_只有现金查看权限也能进后台且只看到现金管理页()
    {
        var menu = _service.BuildMenu(CreateUser(new Claim("permission", Permissions.Cash.OverviewView)));

        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        // 现金管理已并入「结算与现金」合并页；页内只显示有权限的标签。
        Assert.Equal(new[] { "/pos-admin/settlement-cash" }, posAdmin.Children!.Select(item => item.Path));
    }

    [Fact]
    public void BuildMenu_只有预订单下单入口也能进后台且只看到下单页()
    {
        var menu = _service.BuildMenu(CreateUser(new Claim("permission", Permissions.SupplierPreorder.Order)));

        var group = Assert.Single(menu, item => item.Path == "/seasonal-preorders");
        Assert.Equal(new[] { "/supplier-preorder-ordering" }, group.Children!.Select(item => item.Path));
    }

    [Fact]
    public void BuildMenu_预订单管理权限看到下单页订单表与下单人员授权()
    {
        var menu = _service.BuildMenu(CreateUser(new Claim("permission", Permissions.SupplierPreorder.Manage)));

        var group = Assert.Single(menu, item => item.Path == "/seasonal-preorders");
        Assert.Equal(
            new[] { "/supplier-preorder-ordering", "/supplier-preorders", "/supplier-preorders/orderers" },
            group.Children!.Select(item => item.Path));
    }

    [Fact]
    public void BuildAppMenu_ShowsFullMenuForAdmin()
    {
        var user = CreateUser(new Claim(ClaimTypes.Role, "Admin"));

        var menu = _service.BuildAppMenu(user);

        // 管理员可见完整 App 菜单；商品查询与同权限的商品进销查询都必须保留。
        Assert.Equal(47, menu.Count);
        Assert.Contains(menu, item => item.RouteName == "daily-closes");
        Assert.Contains(menu, item => item.RouteName == "payslips");
        Assert.Contains(menu, item => item.RouteName == "pricing-strategies");
        Assert.Contains(menu, item => item.RouteName == "face-hub");
        Assert.Contains(menu, item => item.RouteName == "face-unwelcome");
        Assert.Contains(menu, item => item.RouteName == "face-match-settings");
        Assert.Contains(menu, item => item.RouteName == "face-capture-results");
        Assert.Contains(menu, item => item.RouteName == "notifications");
        Assert.Contains(menu, item => item.RouteName == "app-install");
        Assert.Contains(menu, item => item.RouteName == "store-cash");
        Assert.Contains(menu, item => item.RouteName == "containers");
        Assert.Contains(menu, item => item.RouteName == "warehouse-picking");
        Assert.Contains(menu, item => item.RouteName == "cash-register-users");
        Assert.Contains(menu, item => item.RouteName == "seasonal-product-insights");
        Assert.Contains(menu, item => item.RouteName == "price-updates");
        Assert.Contains(menu, item => item.RouteName == "sales-orders");
        Assert.Contains(menu, item => item.RouteName == "permissions");
        Assert.Contains(menu, item => item.RouteName == "product-query");
        Assert.Contains(menu, item => item.RouteName == "legacy-employee-logs");
        Assert.Contains(menu, item => item.RouteName == "product-insights");
        Assert.Contains(menu, item => item.RouteName == "warehouse-product-insights");
        Assert.Contains(menu, item => item.RouteName == "users");
        Assert.Contains(menu, item => item.RouteName == "user-admin");
        Assert.Contains(menu, item => item.RouteName == "roles");
        Assert.Contains(menu, item => item.RouteName == "employee-profile");
        Assert.Contains(menu, item => item.RouteName == "device-management");
        Assert.Contains(menu, item => item.RouteName == "reports");
        Assert.Contains(menu, item => item.RouteName == "traffic-stats");
        Assert.Contains(menu, item => item.RouteName == "attendance-personal");
        Assert.Contains(menu, item => item.RouteName == "attendance-management");
        Assert.Contains(menu, item => item.RouteName == "attendance-hours");
        Assert.Contains(menu, item => item.RouteName == "seasonal-cards");
        Assert.Contains(menu, item => item.RouteName == "advertisements");
        Assert.Contains(menu, item => item.RouteName == "promotions");
        Assert.DoesNotContain(menu, item => item.RouteName == "attendance");
        Assert.Contains(menu, item => item.RouteName == "local-supplier-invoices");
        Assert.Contains(menu, item => item.RouteName == "warehouse");
    }

    [Fact]
    public void BuildAppMenu_ShowsSalesOrdersOnlyWithSalesOrdersViewPermission()
    {
        var salesOrdersUser = CreateUser(new Claim("permission", Permissions.SalesOrders.View));
        var ordersUser = CreateUser(new Claim("permission", Permissions.Orders.View));

        var salesOrdersMenu = _service.BuildAppMenu(salesOrdersUser);
        var ordersMenu = _service.BuildAppMenu(ordersUser);

        // 移动端销售订单查询只认独立权限；Web 收银记录页的 Orders.View 不得顺带放行。
        Assert.Single(salesOrdersMenu, item => item.RouteName == "sales-orders");
        Assert.DoesNotContain(ordersMenu, item => item.RouteName == "sales-orders");
    }

    [Fact]
    public void BuildAppMenu_ShowsWarehousePickingForPickingOrWarehouseManagePermissions()
    {
        var pickingUser = CreateUser(new Claim("permission", Permissions.Warehouse.Picking));
        var manageUser = CreateUser(new Claim("permission", Permissions.Warehouse.Manage));
        var ordersUser = CreateUser(new Claim("permission", Permissions.Warehouse.ManageOrders));
        var productsOnlyUser = CreateUser(new Claim("permission", Permissions.Warehouse.ManageProducts));

        // 拣货入口按拣货权限或其别名（管理仓库、管理仓库订货）放行；只管商品的账号看不到。
        Assert.Single(_service.BuildAppMenu(pickingUser), item => item.RouteName == "warehouse-picking");
        Assert.Single(_service.BuildAppMenu(manageUser), item => item.RouteName == "warehouse-picking");
        Assert.Single(_service.BuildAppMenu(ordersUser), item => item.RouteName == "warehouse-picking");
        Assert.DoesNotContain(_service.BuildAppMenu(productsOnlyUser), item => item.RouteName == "warehouse-picking");
    }

    [Fact]
    public void BuildDeviceAppMenu_ShowsWarehousePickingOnlyForWarehouseDevices()
    {
        // 纯设备会话没有个人账号，拣货人靠扫员工码确认；只有仓库类型设备放出入口。
        Assert.Contains(_service.BuildDeviceAppMenu("PDA-Warehouse"), item => item.RouteName == "warehouse-picking");
        Assert.DoesNotContain(_service.BuildDeviceAppMenu("Mobile"), item => item.RouteName == "warehouse-picking");
    }

    [Fact]
    public void BuildAppMenu_ShowsWarehouseProductInsightsOnlyWithWarehouseFlowPermission()
    {
        var flowUser = CreateUser(new Claim("permission", Permissions.SalesDashboard.WarehouseFlowView));
        var warehouseOnlyUser = CreateUser(new Claim("permission", Permissions.Warehouse.ManageProducts));

        var flowMenu = _service.BuildAppMenu(flowUser);
        var warehouseOnlyMenu = _service.BuildAppMenu(warehouseOnlyUser);

        // 仓库商品进销读的是跨分店销售，必须与 Web 仓库商品流转分析同权限，仓库查看权限不得顺带放行。
        Assert.Single(flowMenu, item => item.RouteName == "warehouse-product-insights");
        Assert.DoesNotContain(warehouseOnlyMenu, item => item.RouteName == "warehouse-product-insights");
    }

    [Fact]
    public void BuildAppMenu_ShowsContainersOnlyWithContainerViewPermission()
    {
        var containerUser = CreateUser(new Claim("permission", Permissions.Container.View));
        var productsOnlyUser = CreateUser(new Claim("permission", Permissions.Warehouse.ManageProducts));

        var containerMenu = _service.BuildAppMenu(containerUser);
        var productsOnlyMenu = _service.BuildAppMenu(productsOnlyUser);

        // 货柜管理是独立入口：只凭 Container.View 可见，且不再顺带放出商品和货位管理（warehouse）。
        var item = Assert.Single(containerMenu, item => item.RouteName == "containers");
        Assert.Equal("tabs.containers", item.TitleKey);
        Assert.Equal(Permissions.Container.View, item.Permission);
        Assert.DoesNotContain(containerMenu, item => item.RouteName == "warehouse");

        // 只管商品的账号看得到商品和货位管理，看不到货柜管理。
        var warehouse = Assert.Single(productsOnlyMenu, item => item.RouteName == "warehouse");
        Assert.Equal(Permissions.Warehouse.ManageProducts, warehouse.Permission);
        Assert.DoesNotContain(productsOnlyMenu, item => item.RouteName == "containers");
    }

    [Fact]
    public void BuildDeviceAppMenu_HidesContainers()
    {
        // 纯设备会话没有 Container.View，仓库设备也只放出商品和货位管理与拣货。
        Assert.DoesNotContain(_service.BuildDeviceAppMenu("PDA-Warehouse"), item => item.RouteName == "containers");
        Assert.DoesNotContain(_service.BuildDeviceAppMenu("Mobile"), item => item.RouteName == "containers");
    }

    [Fact]
    public void BuildAppMenu_ShowsContainerNewProductsOnlyWithDedicatedPermissionAfterWarehouse()
    {
        var authorized = _service.BuildAppMenu(
            CreateUser(new Claim("permission", Permissions.Container.MobileNewProductsView))
        );
        var unauthorized = _service.BuildAppMenu(
            CreateUser(new Claim("permission", Permissions.Container.View))
        );

        var item = Assert.Single(
            authorized,
            menu => menu.RouteName == "container-new-products"
        );
        Assert.Equal("tabs.containerNewProducts", item.TitleKey);
        Assert.Equal(Permissions.Container.MobileNewProductsView, item.Permission);
        Assert.Equal(41, item.Order);
        Assert.DoesNotContain(
            unauthorized,
            menu => menu.RouteName == "container-new-products"
        );
        Assert.DoesNotContain(
            authorized,
            menu => menu.RouteName == "warehouse"
        );
        Assert.DoesNotContain(
            authorized,
            menu => menu.RouteName == "price-updates"
        );
    }

    [Fact]
    public void BuildAppMenu_ShowsEmployeeProfileWithEmployeeProfileViewPermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.EmployeeProfiles.View));

        var menu = _service.BuildAppMenu(user);

        Assert.Contains(menu, item => item.RouteName == "employee-profile");
    }

    [Fact]
    public void BuildAppMenu_敏感资料审核仅按独立权限显示且设备菜单不出现()
    {
        var authorized = CreateUser(new Claim(
            "permission",
            Permissions.EmployeeProfiles.ReviewSensitiveManagedStore
        ));
        var unauthorized = CreateUser(new Claim("permission", Permissions.EmployeeProfiles.View));

        var item = Assert.Single(
            _service.BuildAppMenu(authorized),
            menu => menu.RouteName == "employee-profile-review"
        );
        Assert.Equal("tabs.employeeProfileReview", item.TitleKey);
        Assert.Equal("account-check-outline", item.Icon);
        Assert.Equal(Permissions.EmployeeProfiles.ReviewSensitiveManagedStore, item.Permission);
        Assert.DoesNotContain(
            _service.BuildAppMenu(unauthorized),
            menu => menu.RouteName == "employee-profile-review"
        );
        Assert.DoesNotContain(
            _service.BuildDeviceAppMenu("Mobile"),
            menu => menu.RouteName == "employee-profile-review"
        );
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("管理员")]
    [InlineData("SuperAdmin")]
    [InlineData("超级管理员")]
    public void BuildAppMenu_四种系统管理员别名均显示敏感资料审核(string roleName)
    {
        var user = CreateUser(new Claim(ClaimTypes.Role, roleName));

        Assert.Contains(
            _service.BuildAppMenu(user),
            menu => menu.RouteName == "employee-profile-review"
        );
    }

    [Fact]
    public void BuildAppMenu_ShowsLocalSupplierInvoicesWithLocalPurchaseViewPermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.LocalPurchase.View));

        var menu = _service.BuildAppMenu(user);

        var item = Assert.Single(menu, item => item.RouteName == "local-supplier-invoices");
        Assert.Equal("tabs.localSupplierInvoices", item.TitleKey);
        Assert.Equal("receipt-text-outline", item.Icon);
        Assert.Equal(Permissions.LocalPurchase.View, item.Permission);
    }

    [Fact]
    public void BuildAppMenu_ProductInsightsUsesTheProductQueryPermission()
    {
        var authorized = _service.BuildAppMenu(
            CreateUser(new Claim("permission", Permissions.StoreProducts.View))
        );
        var unauthorized = _service.BuildAppMenu(
            CreateUser(new Claim("permission", Permissions.Orders.View))
        );

        var item = Assert.Single(authorized, item => item.RouteName == "product-insights");
        Assert.Equal("tabs.productInsights", item.TitleKey);
        Assert.Equal("chart-timeline-variant", item.Icon);
        Assert.Equal(Permissions.StoreProducts.View, item.Permission);
        Assert.DoesNotContain(unauthorized, item => item.RouteName == "product-insights");
    }

    [Fact]
    public void BuildAppMenu_PriceUpdatesRequiresDedicatedPermission()
    {
        var authorized = _service.BuildAppMenu(
            CreateUser(new Claim("permission", Permissions.StoreProducts.PriceUpdates))
        );
        // 编辑分店商品权限不再附带价格更新入口。
        var editOnly = _service.BuildAppMenu(
            CreateUser(
                new Claim("permission", Permissions.StoreProducts.Edit),
                new Claim("permission", Permissions.StoreProducts.View)
            )
        );

        var item = Assert.Single(authorized, item => item.RouteName == "price-updates");
        Assert.Equal("tabs.priceUpdates", item.TitleKey);
        Assert.Equal(Permissions.StoreProducts.PriceUpdates, item.Permission);
        Assert.DoesNotContain(editOnly, item => item.RouteName == "price-updates");
    }

    [Fact]
    public void BuildDeviceAppMenu_IncludesPriceUpdatesForBoundStoreDevices()
    {
        var menu = _service.BuildDeviceAppMenu("Mobile");

        Assert.Contains(menu, item => item.RouteName == "price-updates");
    }

    [Fact]
    public void BuildDeviceAppMenu_IncludesProductInsightsWithProductQuery()
    {
        var menu = _service.BuildDeviceAppMenu("Mobile");

        Assert.Contains(menu, item => item.RouteName == "product-query");
        Assert.Contains(menu, item => item.RouteName == "product-insights");
    }

    [Fact]
    public void BuildAppMenu_ShowsLocalSupplierInvoicesWithLegacyLocalInvoiceViewPermission()
    {
        var user = CreateUser(new Claim("permission", "LocalInvocie.View"));

        var menu = _service.BuildAppMenu(user);

        Assert.Contains(menu, item => item.RouteName == "local-supplier-invoices");
    }

    [Fact]
    public void BuildAppMenu_HidesLocalSupplierInvoicesWithoutLocalPurchaseViewPermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.Orders.View));

        var menu = _service.BuildAppMenu(user);

        Assert.DoesNotContain(menu, item => item.RouteName == "local-supplier-invoices");
    }

    [Theory]
    [InlineData(Permissions.OrderFront.View)]
    [InlineData(Permissions.Orders.View)]
    [InlineData(Permissions.Warehouse.ManageOrders)]
    [InlineData(Permissions.Warehouse.Manage)]
    public void BuildAppMenu_PdaOrdersAcceptsEveryOrderReadPermission(string permission)
    {
        var user = CreateUser(new Claim("permission", permission));

        var menu = _service.BuildAppMenu(user);

        Assert.Contains(menu, item => item.RouteName == "orders");
    }

    [Theory]
    [InlineData("LocalPurchase.MobileView")]
    [InlineData(Permissions.LocalPurchase.View)]
    [InlineData("LocalInvocie.View")]
    public void BuildAppMenu_PdaLocalSupplierInvoicesAcceptsMobileAndExistingReadPermissions(
        string permission
    )
    {
        var user = CreateUser(new Claim("permission", permission));

        var menu = _service.BuildAppMenu(user);

        Assert.Contains(menu, item => item.RouteName == "local-supplier-invoices");
    }

    [Fact]
    public void BuildAppMenu_ShowsReportsWithReportsViewPermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.Reports.View));

        var menu = _service.BuildAppMenu(user);

        var item = Assert.Single(menu, item => item.RouteName == "reports");
        Assert.Equal("tabs.reports", item.TitleKey);
        Assert.Equal("chart-box-outline", item.Icon);
        Assert.Equal(Permissions.Reports.ProductMovementView, item.Permission);
    }

    [Fact]
    public void BuildAppMenu_ShowsReportsWithProductMovementPermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.Reports.ProductMovementView));

        var menu = _service.BuildAppMenu(user);

        var item = Assert.Single(menu, item => item.RouteName == "reports");
        Assert.Equal("tabs.reports", item.TitleKey);
        Assert.Equal("chart-box-outline", item.Icon);
        Assert.Equal(Permissions.Reports.ProductMovementView, item.Permission);
    }

    [Fact]
    public void BuildAppMenu_仅凭客流统计查看权限可见客流统计入口()
    {
        var menu = _service.BuildAppMenu(CreateUser(new Claim("permission", Permissions.FaceMatch.Traffic.View)));

        var item = Assert.Single(menu, entry => entry.RouteName == "traffic-stats");
        Assert.Equal("tabs.trafficStats", item.TitleKey);
        Assert.Equal("chart-line", item.Icon);
        Assert.Equal(Permissions.FaceMatch.Traffic.View, item.Permission);
        // 独立入口：不顺带打开人脸比对合并入口；没有该权限则看不到客流统计。
        Assert.DoesNotContain(menu, entry => entry.RouteName == "face-hub");
        Assert.DoesNotContain(_service.BuildAppMenu(CreateUser()), entry => entry.RouteName == "traffic-stats");
    }

    [Fact]
    public void BuildDeviceAppMenu_设备模式不含客流统计()
    {
        foreach (var deviceType in new[] { "Mobile", "Warehouse", "PDA-Warehouse" })
        {
            Assert.DoesNotContain(_service.BuildDeviceAppMenu(deviceType), entry => entry.RouteName == "traffic-stats");
        }
    }

    [Fact]
    public void BuildAppMenu_ShowsAdvertisementsWithAdvertisementViewPermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.Advertisements.View));

        var menu = _service.BuildAppMenu(user);

        var item = Assert.Single(menu, item => item.RouteName == "advertisements");
        Assert.Equal("tabs.advertisements", item.TitleKey);
        Assert.Equal(Permissions.Advertisements.View, item.Permission);
    }

    [Fact]
    public void BuildAppMenu_ShowsAttendancePersonalWithViewSelfPermission()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.Attendance.Schedule.ViewSelf)
        );

        var menu = _service.BuildAppMenu(user);

        var item = Assert.Single(menu, item => item.RouteName == "attendance-personal");
        Assert.Equal("tabs.attendancePersonal", item.TitleKey);
        Assert.Equal("calendar-clock", item.Icon);
        Assert.Equal(Permissions.Attendance.Schedule.ViewSelf, item.Permission);
        Assert.DoesNotContain(menu, item => item.RouteName == "attendance-management");
        Assert.DoesNotContain(menu, item => item.RouteName == "attendance");
    }

    [Fact]
    public void BuildAppMenu_ShowsAttendanceManagementWithManagedAttendancePermission()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.Attendance.Approval.ReviewManagedStore)
        );

        var menu = _service.BuildAppMenu(user);

        var item = Assert.Single(menu, item => item.RouteName == "attendance-management");
        Assert.Equal("tabs.attendanceManagement", item.TitleKey);
        Assert.Equal("calendar-clock", item.Icon);
        Assert.Equal(Permissions.Attendance.Schedule.ViewStore, item.Permission);
        Assert.DoesNotContain(menu, item => item.RouteName == "attendance-personal");
        Assert.DoesNotContain(menu, item => item.RouteName == "attendance");
    }

    [Fact]
    public void BuildAppMenu_仅凭查看管理分店打卡权限可见工时统计入口()
    {
        var menu = _service.BuildAppMenu(CreateUser(new Claim("permission", Permissions.Attendance.Punch.ViewManagedStore)));

        var item = Assert.Single(menu, entry => entry.RouteName == "attendance-hours");
        Assert.Equal("tabs.attendanceHours", item.TitleKey);
        Assert.Equal("clock-outline", item.Icon);
        Assert.Equal(Permissions.Attendance.Punch.ViewManagedStore, item.Permission);
        // 只有审核权限、没有查看打卡权限的账号看不到工时统计。
        Assert.DoesNotContain(
            _service.BuildAppMenu(CreateUser(new Claim("permission", Permissions.Attendance.Approval.ReviewManagedStore))),
            entry => entry.RouteName == "attendance-hours");
    }

    [Fact]
    public void BuildAppMenu_仅凭任一工资单查看权限可见工资单入口_导出权限本身不够()
    {
        foreach (var permission in new[] { Permissions.Myob.Payslip.ViewManagedStore, Permissions.Myob.Payslip.ViewAllStores })
        {
            var menu = _service.BuildAppMenu(CreateUser(new Claim("permission", permission)));
            var item = Assert.Single(menu, entry => entry.RouteName == "payslips");
            Assert.Equal("tabs.payslips", item.TitleKey);
            Assert.Equal("receipt-text-outline", item.Icon);
        }

        // 只有下载 / 邮件权限、没有查看权限：看不到入口（接口里也需要同时具备查看权限）。
        foreach (var permission in new[] { Permissions.Myob.Payslip.Download, Permissions.Myob.Payslip.Email })
        {
            Assert.DoesNotContain(
                _service.BuildAppMenu(CreateUser(new Claim("permission", permission))),
                entry => entry.RouteName == "payslips");
        }
    }

    [Fact]
    public void BuildDeviceAppMenu_设备模式不含工资单()
    {
        foreach (var deviceType in new[] { "Mobile", "Warehouse", "PDA-Warehouse" })
        {
            Assert.DoesNotContain(_service.BuildDeviceAppMenu(deviceType), entry => entry.RouteName == "payslips");
        }
    }

    [Fact]
    public void BuildDeviceAppMenu_设备模式不含工时统计()
    {
        foreach (var deviceType in new[] { "Mobile", "Warehouse", "PDA-Warehouse" })
        {
            Assert.DoesNotContain(_service.BuildDeviceAppMenu(deviceType), entry => entry.RouteName == "attendance-hours");
        }
    }

    [Fact]
    public void BuildAppMenu_HidesAttendanceRoutesWithoutAttendancePermissions()
    {
        var user = CreateUser(new Claim("permission", Permissions.Orders.View));

        var menu = _service.BuildAppMenu(user);

        Assert.DoesNotContain(menu, item => item.RouteName == "attendance-personal");
        Assert.DoesNotContain(menu, item => item.RouteName == "attendance-management");
        Assert.DoesNotContain(menu, item => item.RouteName == "attendance-hours");
        Assert.DoesNotContain(menu, item => item.RouteName == "attendance");
    }

    [Fact]
    public void BuildAppMenu_ShowsOrderRoutesWithoutAttendanceForOrderPermissions()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.OrderFront.View),
            new Claim("permission", Permissions.Orders.View),
            new Claim("permission", Permissions.Orders.Create)
        );

        var menu = _service.BuildAppMenu(user);

        Assert.Contains(menu, item => item.RouteName == "home");
        Assert.Contains(menu, item => item.RouteName == "orders");
        Assert.Contains(menu, item => item.RouteName == "cart");
        Assert.DoesNotContain(menu, item => item.RouteName == "attendance-personal");
        Assert.DoesNotContain(menu, item => item.RouteName == "attendance-management");
        Assert.DoesNotContain(menu, item => item.RouteName == "attendance");
    }

    [Fact]
    public void BuildAppMenu_ShowsSeasonalCardsWithViewPermission()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.SeasonalCards.Remaining.ViewManagedStore)
        );

        var menu = _service.BuildAppMenu(user);

        var item = Assert.Single(menu, node => node.RouteName == "seasonal-cards");
        Assert.Equal("tabs.seasonalCards", item.TitleKey);
        Assert.Equal(Permissions.SeasonalCards.Remaining.ViewManagedStore, item.Permission);
    }

    [Fact]
    public void BuildAppMenu_ShowsSeasonalCardsWithSubmitPermission()
    {
        var user = CreateUser(
            new Claim("permission", Permissions.SeasonalCards.Remaining.SubmitManagedStore)
        );

        var menu = _service.BuildAppMenu(user);

        Assert.Contains(menu, item => item.RouteName == "seasonal-cards");
    }

    [Fact]
    public void BuildDeviceAppMenu_HidesLocalSupplierInvoicesForDeviceMode()
    {
        var menu = _service.BuildDeviceAppMenu("Mobile");

        Assert.DoesNotContain(menu, item => item.RouteName == "local-supplier-invoices");
    }

    [Fact]
    public void BuildDeviceAppMenu_HidesReportsForDeviceMode()
    {
        var menu = _service.BuildDeviceAppMenu("Mobile");

        Assert.DoesNotContain(menu, item => item.RouteName == "reports");
    }

    [Fact]
    public void BuildAppMenu_HidesStoreFinanceRoutesWithoutDedicatedPermissions()
    {
        var user = CreateUser(new Claim("permission", Permissions.Orders.View));

        var menu = _service.BuildAppMenu(user);

        Assert.DoesNotContain(menu, item => item.RouteName == "installment-orders");
        Assert.DoesNotContain(menu, item => item.RouteName == "store-vouchers");
    }

    [Fact]
    public void BuildAppMenu_ShowsStoreFinanceRoutesWithDedicatedPermissions()
    {
        var user = CreateUser(
            new Claim("permission", InstallmentOrdersPermission),
            new Claim("permission", StoreVouchersPermission)
        );

        var menu = _service.BuildAppMenu(user);

        var installmentOrders = Assert.Single(menu, item => item.RouteName == "installment-orders");
        Assert.Equal("tabs.installmentOrders", installmentOrders.TitleKey);
        Assert.Equal("cash-clock", installmentOrders.Icon);
        Assert.Equal(InstallmentOrdersPermission, installmentOrders.Permission);

        var storeVouchers = Assert.Single(menu, item => item.RouteName == "store-vouchers");
        Assert.Equal("tabs.storeVouchers", storeVouchers.TitleKey);
        Assert.Equal("ticket-percent-outline", storeVouchers.Icon);
        Assert.Equal(StoreVouchersPermission, storeVouchers.Permission);
    }

    [Fact]
    public void BuildAppMenu_HidesUsersForStoreManagerWithoutUsersViewPermission()
    {
        var user = CreateUser(new Claim(ClaimTypes.Role, "StoreManager"));

        var menu = _service.BuildAppMenu(user);

        Assert.DoesNotContain(menu, item => item.RouteName == "users");
    }

    [Fact]
    public void BuildAppMenu_DoesNotUnlockWarehouseForWarehouseManagerRoleWithoutPermissionClaims()
    {
        var user = CreateUser(new Claim(ClaimTypes.Role, "WarehouseManager"));

        var menu = _service.BuildAppMenu(user);

        Assert.DoesNotContain(menu, item => item.RouteName == "warehouse");
    }

    [Fact]
    public void BuildAppMenu_ShowsUsersForStoreManagerWithUsersViewPermission()
    {
        var user = CreateUser(
            new Claim(ClaimTypes.Role, "StoreManager"),
            new Claim("permission", Permissions.Users.View)
        );

        var menu = _service.BuildAppMenu(user);

        Assert.Contains(menu, item => item.RouteName == "users");
    }

    [Fact]
    public void BuildAppMenu_ShowsUsersWithUsersViewPermissionWithoutRoleGate()
    {
        var user = CreateUser(new Claim("permission", Permissions.Users.View));

        var menu = _service.BuildAppMenu(user);

        Assert.Contains(menu, item => item.RouteName == "users");
    }

    [Fact]
    public void BuildAppMenu_仅有ViewStoreStaff时只显示员工管理_不显示用户管理()
    {
        var user = CreateUser(new Claim("permission", Permissions.Users.ViewStoreStaff));

        var menu = _service.BuildAppMenu(user);

        Assert.Contains(menu, item => item.RouteName == "users");
        Assert.DoesNotContain(menu, item => item.RouteName == "user-admin");
    }

    [Fact]
    public void BuildAppMenu_仅有ViewManagedStore时可进入人员管理资料页签_不显示用户管理()
    {
        var user = CreateUser(new Claim("permission", Permissions.EmployeeProfiles.ViewManagedStore));

        var menu = _service.BuildAppMenu(user);

        Assert.Contains(menu, item => item.RouteName == "users");
        Assert.DoesNotContain(menu, item => item.RouteName == "user-admin");
    }

    [Fact]
    public void BuildAppMenu_持有UsersView时经别名同时显示员工管理与用户管理()
    {
        var user = CreateUser(new Claim("permission", Permissions.Users.View));

        var menu = _service.BuildAppMenu(user);

        Assert.Contains(menu, item => item.RouteName == "users");
        Assert.Contains(menu, item => item.RouteName == "user-admin");
        // 「人员管理」合并入口改为多权限任一可见（ViewStoreStaff 或「资料」页签的 ViewManagedStore）；
        // 移动端菜单 DTO 只带单个 Permission，多权限入口由服务端过滤后下发，Permission 为空。
        Assert.Null(Assert.Single(menu, item => item.RouteName == "users").Permission);
        Assert.Equal(Permissions.Users.View, Assert.Single(menu, item => item.RouteName == "user-admin").Permission);
    }

    [Fact]
    public void BuildAppMenu_员工操作日志_老收银或新收银任一查看权限可见()
    {
        var legacyOnly = CreateUser(new Claim("permission", Permissions.LegacyEmployeeLogs.View));
        var auditOnly = CreateUser(new Claim("permission", Permissions.PosTerminal.Audit.View));
        var neither = CreateUser(new Claim("permission", Permissions.Users.View));

        // 移动端「员工操作日志」页内切换老收银 / 新收银，任一来源的查看权限即可进入；路由名沿用 legacy-employee-logs。
        foreach (var user in new[] { legacyOnly, auditOnly })
        {
            var item = Assert.Single(
                _service.BuildAppMenu(user),
                menu => menu.RouteName == "legacy-employee-logs"
            );
            Assert.Equal("tabs.legacyEmployeeLogs", item.TitleKey);
            Assert.Equal("clipboard-text-clock-outline", item.Icon);
        }
        Assert.DoesNotContain(_service.BuildAppMenu(auditOnly), menu => menu.RouteName == "pos-operation-logs");
        Assert.DoesNotContain(_service.BuildAppMenu(neither), menu => menu.RouteName == "legacy-employee-logs");
    }

    [Theory]
    [InlineData(Permissions.CashRegisterUsers.MobileManage, true)]
    [InlineData(Permissions.CashRegisterUsers.MobilePrint, true)]
    [InlineData(Permissions.Store.ManageOperations, false)]
    [InlineData(Permissions.Users.View, false)]
    public void BuildAppMenu_CashRegisterUsersOnlyForMobileIndependentPermissions(
        string permission,
        bool expectedVisible
    )
    {
        // 移动端收银用户条码入口只认独立的移动端管理/打印权限，Web 的 Store.ManageOperations 不自动带出。
        var menu = _service.BuildAppMenu(CreateUser(new Claim("permission", permission)));

        var item = menu.SingleOrDefault(entry => entry.RouteName == "cash-register-users");
        Assert.Equal(expectedVisible, item != null);
        if (item != null)
        {
            Assert.Equal("tabs.cashRegisterUsers", item.TitleKey);
            Assert.Equal("barcode", item.Icon);
        }
    }

    [Theory]
    [InlineData(Permissions.SeasonalProductInsights.View, true)]
    [InlineData(Permissions.StoreProducts.View, false)]
    [InlineData(Permissions.SalesDashboard.WarehouseFlowView, false)]
    public void BuildAppMenu_SeasonalProductInsightsOnlyForIndependentPermission(
        string permission,
        bool expectedVisible
    )
    {
        // 季节商品查询会展示其他分店库存，只认独立权限，商品查询/仓库流转的查看权限不自动带出。
        var menu = _service.BuildAppMenu(CreateUser(new Claim("permission", permission)));

        var item = menu.SingleOrDefault(entry => entry.RouteName == "seasonal-product-insights");
        Assert.Equal(expectedVisible, item != null);
        if (item != null)
        {
            Assert.Equal("tabs.seasonalProductInsights", item.TitleKey);
            Assert.Equal("calendar-star", item.Icon);
        }
    }

    [Fact]
    public void BuildDeviceAppMenu_HidesSeasonalProductInsightsForDeviceMode()
    {
        Assert.DoesNotContain(
            _service.BuildDeviceAppMenu("Mobile"),
            item => item.RouteName == "seasonal-product-insights"
        );
    }

    [Fact]
    public void BuildDeviceAppMenu_HidesCashRegisterUsersForDeviceMode()
    {
        Assert.DoesNotContain(
            _service.BuildDeviceAppMenu("Mobile"),
            item => item.RouteName == "cash-register-users"
        );
    }

    [Fact]
    public void BuildAppMenu_日结记录仅凭独立查看权限可见()
    {
        var viewer = CreateUser(new Claim("permission", Permissions.DailyCloseRecords.View));
        var item = Assert.Single(_service.BuildAppMenu(viewer), menu => menu.RouteName == "daily-closes");
        Assert.Equal("tabs.dailyCloses", item.TitleKey);
        Assert.Equal("cash-register", item.Icon);

        // 收银机上的日结权限、员工操作日志权限、无关权限都不能打开它。
        foreach (var permission in new[]
                 {
                     Permissions.PosTerminal.DailyClose.View,
                     Permissions.PosTerminal.DailyClose.Save,
                     Permissions.PosTerminal.DailyClose.Reprint,
                     Permissions.PosTerminal.Audit.View,
                     Permissions.LegacyEmployeeLogs.View,
                     Permissions.Users.View,
                 })
        {
            Assert.DoesNotContain(
                _service.BuildAppMenu(CreateUser(new Claim("permission", permission))),
                menu => menu.RouteName == "daily-closes"
            );
        }
    }

    [Fact]
    public void BuildAppMenu_自动价格策略仅凭查看权限可见()
    {
        var viewer = CreateUser(new Claim("permission", Permissions.PricingStrategy.View));
        var item = Assert.Single(_service.BuildAppMenu(viewer), menu => menu.RouteName == "pricing-strategies");
        Assert.Equal("tabs.pricingStrategies", item.TitleKey);

        // 只有编辑权限、改价任务权限、分店商品权限都不能单独打开入口（与 Web 菜单同口径：入口只认 View）。
        foreach (var permission in new[]
                 {
                     Permissions.PricingStrategy.Edit,
                     Permissions.StoreProducts.PriceUpdates,
                     Permissions.StoreProducts.View,
                 })
        {
            Assert.DoesNotContain(
                _service.BuildAppMenu(CreateUser(new Claim("permission", permission))),
                menu => menu.RouteName == "pricing-strategies"
            );
        }
        Assert.DoesNotContain(_service.BuildDeviceAppMenu("Mobile"), menu => menu.RouteName == "pricing-strategies");
    }

    [Fact]
    public void BuildDeviceAppMenu_HidesDailyClosesForDeviceMode()
    {
        // 设备会话没有用户账号，日结记录按账号关联分店授权，所以设备模式菜单不应暴露该入口。
        var menu = _service.BuildDeviceAppMenu("Mobile");

        Assert.DoesNotContain(menu, item => item.RouteName == "daily-closes");
    }

    // ---------- 人脸比对合并入口 face-hub ----------

    [Theory]
    [InlineData(Permissions.FaceCapture.Images.View)]
    [InlineData(Permissions.FaceMatch.Unwelcome.Manage)]
    [InlineData(Permissions.FaceMatch.Unwelcome.Approve)]
    [InlineData(Permissions.FaceMatch.Unwelcome.Review)]
    [InlineData(Permissions.FaceMatch.Manage)]
    [InlineData(Permissions.FaceCapture.Cameras.Manage)]
    public void BuildAppMenu_人脸比对合并入口_六个权限码任一就可见(string permission)
    {
        var menu = _service.BuildAppMenu(CreateUser(new Claim("permission", permission)));

        var item = Assert.Single(menu, entry => entry.RouteName == "face-hub");
        Assert.Equal("tabs.faceHub", item.TitleKey);
        Assert.Equal("face-recognition", item.Icon);
    }

    [Fact]
    public void BuildAppMenu_人脸比对合并入口_没有任何人脸权限不可见_客流统计权限不算()
    {
        Assert.DoesNotContain(_service.BuildAppMenu(CreateUser()), entry => entry.RouteName == "face-hub");
        // 客流统计权限（FaceMatch.Traffic.View）与 Users.View 都不在六个码里：不能顺带打开合并入口。
        foreach (var other in new[] { Permissions.FaceMatch.Traffic.View, Permissions.Users.View })
        {
            Assert.DoesNotContain(
                _service.BuildAppMenu(CreateUser(new Claim("permission", other))),
                entry => entry.RouteName == "face-hub"
            );
        }
    }

    [Fact]
    public void BuildAppMenu_人脸比对合并入口_管理员别名都可见_设备菜单不含()
    {
        foreach (var role in new[] { "Admin", "管理员", "SuperAdmin", "超级管理员" })
        {
            Assert.Contains(
                _service.BuildAppMenu(CreateUser(new Claim(ClaimTypes.Role, role))),
                entry => entry.RouteName == "face-hub"
            );
        }

        // 生物识别入口：设备会话没有用户账号与权限，设备模式菜单（含仓库设备）不能暴露合并入口。
        foreach (var deviceType in new[] { "Mobile", "Warehouse", "PDA-Warehouse" })
        {
            Assert.DoesNotContain(_service.BuildDeviceAppMenu(deviceType), entry => entry.RouteName == "face-hub");
        }
    }

    [Fact]
    public void BuildAppMenu_人脸比对合并入口_旧三个入口保持原权限口径不变_兼容旧版App()
    {
        // 只有看图权限：看得到合并入口与旧的「抓拍与比对」，看不到旧的名单与设置入口。
        var imagesOnly = _service.BuildAppMenu(CreateUser(new Claim("permission", Permissions.FaceCapture.Images.View)));
        Assert.Contains(imagesOnly, entry => entry.RouteName == "face-hub");
        Assert.Contains(imagesOnly, entry => entry.RouteName == "face-capture-results");
        Assert.DoesNotContain(imagesOnly, entry => entry.RouteName is "face-unwelcome" or "face-match-settings");

        // 名单权限：旧名单入口仍在，旧设置入口仍不在。
        var review = _service.BuildAppMenu(CreateUser(new Claim("permission", Permissions.FaceMatch.Unwelcome.Review)));
        Assert.Contains(review, entry => entry.RouteName == "face-hub");
        Assert.Contains(review, entry => entry.RouteName == "face-unwelcome");
        Assert.DoesNotContain(review, entry => entry.RouteName is "face-match-settings" or "face-capture-results");

        // 设置权限：旧设置入口仍在。
        var manage = _service.BuildAppMenu(CreateUser(new Claim("permission", Permissions.FaceMatch.Manage)));
        Assert.Contains(manage, entry => entry.RouteName == "face-hub");
        Assert.Contains(manage, entry => entry.RouteName == "face-match-settings");
        Assert.DoesNotContain(manage, entry => entry.RouteName is "face-unwelcome" or "face-capture-results");
    }

    [Fact]
    public void BuildAppMenu_人脸比对合并入口_排在旧三项之前()
    {
        // Web 角色菜单预览（expoRoleMenuPreview.ts）按本菜单的源码顺序逐项对照，顺序变了那边的测试会失败。
        var names = _service.BuildAppMenu(CreateUser(new Claim(ClaimTypes.Role, "Admin"))).Select(entry => entry.RouteName).ToList();

        Assert.True(names.IndexOf("face-hub") >= 0);
        Assert.True(names.IndexOf("face-hub") < names.IndexOf("face-unwelcome"));
    }

    [Theory]
    [InlineData(Permissions.FaceMatch.Unwelcome.Manage)]
    [InlineData(Permissions.FaceMatch.Unwelcome.Approve)]
    [InlineData(Permissions.FaceMatch.Unwelcome.Review)]
    public void BuildAppMenu_不受欢迎人员名单_仅凭三个名单权限之一可见(string permission)
    {
        var item = Assert.Single(
            _service.BuildAppMenu(CreateUser(new Claim("permission", permission))),
            menu => menu.RouteName == "face-unwelcome"
        );
        Assert.Equal("tabs.faceUnwelcome", item.TitleKey);
        Assert.Equal("account-cancel-outline", item.Icon);
        // 名单权限不能顺带打开设置页：两个入口的权限口径互不相通。
        Assert.DoesNotContain(
            _service.BuildAppMenu(CreateUser(new Claim("permission", permission))),
            menu => menu.RouteName == "face-match-settings"
        );
    }

    [Theory]
    [InlineData(Permissions.FaceMatch.Manage)]
    [InlineData(Permissions.FaceMatch.Traffic.View)]
    [InlineData(Permissions.FaceCapture.Cameras.Manage)]
    [InlineData(Permissions.FaceCapture.Images.View)]
    [InlineData(Permissions.Users.View)]
    public void BuildAppMenu_不受欢迎人员名单_其他权限或无权限不可见(string permission)
    {
        // 档案管理、客流统计、摄像机与抓拍图片权限都不能打开名单页（名单页展示参考照片与命中抓拍，只认名单权限）。
        Assert.DoesNotContain(
            _service.BuildAppMenu(CreateUser(new Claim("permission", permission))),
            menu => menu.RouteName == "face-unwelcome"
        );
    }

    [Fact]
    public void BuildAppMenu_抓拍与比对_只认看图权限_其他人脸权限不能打开()
    {
        var item = Assert.Single(
            _service.BuildAppMenu(CreateUser(new Claim("permission", Permissions.FaceCapture.Images.View))),
            menu => menu.RouteName == "face-capture-results"
        );
        Assert.Equal("tabs.faceCaptureResults", item.TitleKey);
        foreach (var other in new[] { Permissions.FaceMatch.Manage, Permissions.FaceMatch.Unwelcome.Review, Permissions.FaceCapture.Cameras.Manage })
        {
            Assert.DoesNotContain(
                _service.BuildAppMenu(CreateUser(new Claim("permission", other))),
                menu => menu.RouteName == "face-capture-results"
            );
        }
    }

    [Fact]
    public void BuildAppMenu_通知收件箱_任何登录账号可见_设备菜单不含()
    {
        Assert.Contains(_service.BuildAppMenu(CreateUser()), menu => menu.RouteName == "notifications");
        Assert.DoesNotContain(_service.BuildDeviceAppMenu("Mobile"), menu => menu.RouteName is "notifications" or "face-capture-results");
    }

    [Fact]
    public void BuildAppMenu_不受欢迎人员名单_没有任何权限的账号不可见()
    {
        Assert.DoesNotContain(
            _service.BuildAppMenu(CreateUser()),
            menu => menu.RouteName == "face-unwelcome"
        );
    }

    [Theory]
    [InlineData(Permissions.FaceMatch.Manage)]
    [InlineData(Permissions.FaceCapture.Cameras.Manage)]
    public void BuildAppMenu_人脸比对设置_仅凭档案管理或摄像机管理权限可见(string permission)
    {
        var menu = _service.BuildAppMenu(CreateUser(new Claim("permission", permission)));
        var item = Assert.Single(menu, entry => entry.RouteName == "face-match-settings");
        Assert.Equal("tabs.faceMatchSettings", item.TitleKey);
        Assert.Equal("face-recognition", item.Icon);
        // 设置页的权限也不能顺带打开名单页。
        Assert.DoesNotContain(menu, entry => entry.RouteName == "face-unwelcome");
    }

    [Theory]
    [InlineData(Permissions.FaceMatch.Traffic.View)]
    [InlineData(Permissions.FaceMatch.Unwelcome.Manage)]
    [InlineData(Permissions.FaceMatch.Unwelcome.Approve)]
    [InlineData(Permissions.FaceMatch.Unwelcome.Review)]
    [InlineData(Permissions.FaceCapture.Images.View)]
    [InlineData(Permissions.Users.View)]
    public void BuildAppMenu_人脸比对设置_其他权限不可见(string permission)
    {
        Assert.DoesNotContain(
            _service.BuildAppMenu(CreateUser(new Claim("permission", permission))),
            menu => menu.RouteName == "face-match-settings"
        );
    }

    [Fact]
    public void BuildAppMenu_人脸比对设置_没有任何权限的账号不可见()
    {
        Assert.DoesNotContain(
            _service.BuildAppMenu(CreateUser()),
            menu => menu.RouteName == "face-match-settings"
        );
    }

    [Fact]
    public void BuildAppMenu_人脸两个入口_管理员别名都可见()
    {
        // 生产里这些权限没有挂在任何角色模板上，Admin 靠角色放行：几种管理员别名都必须看到两个入口。
        foreach (var role in new[] { "Admin", "管理员", "SuperAdmin", "超级管理员" })
        {
            var menu = _service.BuildAppMenu(CreateUser(new Claim(ClaimTypes.Role, role)));
            Assert.Contains(menu, item => item.RouteName == "face-unwelcome");
            Assert.Contains(menu, item => item.RouteName == "face-match-settings");
        }
    }

    [Fact]
    public void BuildDeviceAppMenu_HidesFaceMatchEntriesForDeviceMode()
    {
        // 参考照片与命中抓拍是生物识别信息，设备会话没有用户账号与权限，设备模式菜单（含仓库设备）不能暴露这两个入口。
        foreach (var deviceType in new[] { "Mobile", "Warehouse", "PDA-Warehouse" })
        {
            var menu = _service.BuildDeviceAppMenu(deviceType);
            Assert.DoesNotContain(menu, item => item.RouteName == "face-unwelcome");
            Assert.DoesNotContain(menu, item => item.RouteName == "face-match-settings");
        }
    }

    [Fact]
    public void BuildDeviceAppMenu_HidesLegacyEmployeeLogsForDeviceMode()
    {
        // 设备会话没有用户账号，老收银日志按账号可管理分店授权，因此设备模式菜单不应暴露该入口。
        var menu = _service.BuildDeviceAppMenu("Mobile");

        Assert.DoesNotContain(menu, item => item.RouteName == "legacy-employee-logs");
    }

    [Fact]
    public void BuildAppMenu_ShowsPermissionsOnlyWithRolesViewPermission()
    {
        var authorized = CreateUser(new Claim("permission", Permissions.Roles.View));
        var unauthorized = CreateUser(new Claim("permission", Permissions.Users.View));

        // 权限管理入口与 Web 端 /system/permissions 一致，只认 Roles.View；仅有用户查看权限的人不应看到。
        var item = Assert.Single(
            _service.BuildAppMenu(authorized),
            menu => menu.RouteName == "permissions"
        );
        Assert.Equal("tabs.permissions", item.TitleKey);
        Assert.Equal("key-outline", item.Icon);
        Assert.Equal(Permissions.Roles.View, item.Permission);
        Assert.DoesNotContain(
            _service.BuildAppMenu(unauthorized),
            menu => menu.RouteName == "permissions"
        );
    }

    [Fact]
    public void BuildAppMenu_ShowsAppInstallOnlyWithDedicatedPermissionWithoutAdmin()
    {
        var authorized = _service.BuildAppMenu(
            CreateUser(new Claim("permission", Permissions.System.ViewMobileAppInstallLinks))
        );
        // 版本管理查看权限不代表安装页权限，两者独立授权。
        var appDownloadsOnly = _service.BuildAppMenu(
            CreateUser(new Claim("permission", Permissions.System.ViewAppDownloads))
        );

        var item = Assert.Single(authorized, menu => menu.RouteName == "app-install");
        Assert.Equal("tabs.appInstall", item.TitleKey);
        Assert.Equal("qrcode", item.Icon);
        Assert.Equal(Permissions.System.ViewMobileAppInstallLinks, item.Permission);
        Assert.Equal(59, item.Order);
        Assert.DoesNotContain(authorized, menu => menu.RouteName == "app-downloads");
        Assert.DoesNotContain(appDownloadsOnly, menu => menu.RouteName == "app-install");
    }

    [Fact]
    public void BuildDeviceAppMenu_HidesAppInstallForDeviceMode()
    {
        Assert.DoesNotContain(
            _service.BuildDeviceAppMenu("Mobile"),
            item => item.RouteName == "app-install"
        );
    }

    [Fact]
    public void BuildDeviceAppMenu_HidesPermissionsForDeviceMode()
    {
        // 设备会话没有用户角色，RoleService 的管理员校验会直接拒绝，设备模式菜单不应暴露该入口。
        var menu = _service.BuildDeviceAppMenu("Mobile");

        Assert.DoesNotContain(menu, item => item.RouteName == "permissions");
    }

    [Fact]
    public void BuildAppMenu_ShowsUserAdminWithUsersViewPermissionWithoutUnlockingRoles()
    {
        var menu = _service.BuildAppMenu(CreateUser(new Claim("permission", Permissions.Users.View)));

        Assert.Contains(menu, item => item.RouteName == "user-admin");
        Assert.DoesNotContain(menu, item => item.RouteName == "roles");
    }

    [Fact]
    public void BuildAppMenu_ShowsRolesWithRolesViewPermissionWithoutUnlockingUserAdmin()
    {
        var menu = _service.BuildAppMenu(CreateUser(new Claim("permission", Permissions.Roles.View)));

        Assert.Contains(menu, item => item.RouteName == "roles");
        Assert.DoesNotContain(menu, item => item.RouteName == "user-admin");
    }

    [Fact]
    public void BuildAppMenu_HidesGlobalIdentityRoutesWithoutTheirViewPermissions()
    {
        var menu = _service.BuildAppMenu(CreateUser(new Claim(ClaimTypes.Role, "StoreManager")));

        Assert.DoesNotContain(menu, item => item.RouteName == "user-admin");
        Assert.DoesNotContain(menu, item => item.RouteName == "roles");
    }

    [Fact]
    public void BuildAppMenu_ShowsDeviceManagementForAdmin()
    {
        var user = CreateUser(new Claim(ClaimTypes.Role, "Admin"));

        var menu = _service.BuildAppMenu(user);

        var item = Assert.Single(menu, item => item.RouteName == "device-management");
        Assert.Equal("tabs.deviceManagement", item.TitleKey);
        Assert.Equal("cellphone-cog", item.Icon);
        Assert.Equal(59, item.Order);
        Assert.Equal(Permissions.DeviceRegistration.View, item.Permission);
    }

    [Fact]
    public void BuildAppMenu_ShowsDeviceManagementWithDeviceRegistrationViewPermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.DeviceRegistration.View));

        var menu = _service.BuildAppMenu(user);

        var item = Assert.Single(menu, item => item.RouteName == "device-management");
        Assert.Equal(Permissions.DeviceRegistration.View, item.Permission);
    }

    [Fact]
    public void BuildAppMenu_ShowsDeviceManagementForChineseAdmin()
    {
        var user = CreateUser(new Claim(ClaimTypes.Role, "管理员"));

        var menu = _service.BuildAppMenu(user);

        Assert.Contains(menu, item => item.RouteName == "device-management");
    }

    [Fact]
    public void BuildAppMenu_HidesDeviceManagementForStoreManagerWithManageOperationsPermission()
    {
        var user = CreateUser(
            new Claim(ClaimTypes.Role, "StoreManager"),
            new Claim("permission", Permissions.Store.ManageOperations)
        );

        var menu = _service.BuildAppMenu(user);

        Assert.DoesNotContain(menu, item => item.RouteName == "device-management");
    }

    [Fact]
    public void BuildAppMenu_HidesDeviceManagementForUserWithManageOperationsPermission()
    {
        var user = CreateUser(new Claim("permission", Permissions.Store.ManageOperations));

        var menu = _service.BuildAppMenu(user);

        Assert.DoesNotContain(menu, item => item.RouteName == "device-management");
    }

    [Fact]
    public void BuildDeviceAppMenu_HidesDeviceManagementForMobileDevice()
    {
        var menu = _service.BuildDeviceAppMenu("Mobile");

        Assert.DoesNotContain(menu, item => item.RouteName == "device-management");
    }

    [Fact]
    public void BuildDeviceAppMenu_HidesAttendanceRoutesForDeviceMode()
    {
        var menu = _service.BuildDeviceAppMenu("Mobile");

        Assert.DoesNotContain(menu, item => item.RouteName == "attendance");
        Assert.DoesNotContain(menu, item => item.RouteName == "attendance-personal");
        Assert.DoesNotContain(menu, item => item.RouteName == "attendance-management");
    }

    [Fact]
    public void BuildDeviceAppMenu_HidesDeviceManagementForWarehousePdaButKeepsWarehouse()
    {
        var menu = _service.BuildDeviceAppMenu("WarehousePDA");

        Assert.DoesNotContain(menu, item => item.RouteName == "device-management");
        Assert.Contains(menu, item => item.RouteName == "warehouse");
    }

    [Fact]
    public void EmployeeProfileSelfRead_RequiresEmployeeProfileViewPermission()
    {
        var authorizeAttribute = GetMethodAuthorizeAttribute(nameof(EmployeeProfilesController.GetSelf));

        Assert.Equal(Permissions.EmployeeProfiles.View, authorizeAttribute.Policy);
    }

    [Fact]
    public void EmployeeProfileSelfSave_RequiresEmployeeProfileEditPermission()
    {
        var authorizeAttribute = GetMethodAuthorizeAttribute(nameof(EmployeeProfilesController.UpsertSelf));

        Assert.Equal(Permissions.EmployeeProfiles.Edit, authorizeAttribute.Policy);
    }

    [Theory]
    [InlineData(nameof(EmployeeProfilesController.GetCashierBarcode))]
    [InlineData(nameof(EmployeeProfilesController.RefreshCashierBarcode))]
    [InlineData(nameof(EmployeeProfilesController.ConfirmCashierBarcodePrint))]
    public void EmployeeCashierBarcode_RequiresAuthenticatedSelfService(string methodName)
    {
        var controllerType = typeof(EmployeeProfilesController);
        var method = controllerType.GetMethod(methodName)!;
        var authorizeAttribute = GetMethodAuthorizeAttribute(methodName);

        Assert.Equal(EmployeeCashierBarcodeSelfServicePolicy.Name, authorizeAttribute.Policy);
        Assert.Contains(
            controllerType.GetCustomAttributes<AuthorizeAttribute>(),
            attribute => string.IsNullOrWhiteSpace(attribute.Policy)
        );
        Assert.Empty(controllerType.GetCustomAttributes<AllowAnonymousAttribute>());
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());
    }

    [Fact]
    public void StoreUsersGrid_RequiresViewStoreStaffPermission()
    {
        var authorizeAttribute = GetMethodAuthorizeAttribute(
            typeof(ReactStoreUsersController),
            nameof(ReactStoreUsersController.Grid)
        );

        Assert.Equal(Permissions.Users.ViewStoreStaff, authorizeAttribute.Policy);
    }

    [Fact]
    public void StoreUsersCreate_RequiresUsersCreatePermission()
    {
        var authorizeAttribute = GetMethodAuthorizeAttribute(
            typeof(ReactStoreUsersController),
            nameof(ReactStoreUsersController.Create)
        );

        Assert.Equal(Permissions.Users.CreateStoreStaff, authorizeAttribute.Policy);
    }

    [Fact]
    public void StoreUsersUpdate_RequiresUsersEditPermission()
    {
        var authorizeAttribute = GetMethodAuthorizeAttribute(
            typeof(ReactStoreUsersController),
            nameof(ReactStoreUsersController.Update)
        );

        Assert.Equal(Permissions.Users.EditStoreStaff, authorizeAttribute.Policy);
    }

    [Fact]
    public void StoreUsersPassword_RequiresUsersResetPasswordPermission()
    {
        var authorizeAttribute = GetMethodAuthorizeAttribute(
            typeof(ReactStoreUsersController),
            nameof(ReactStoreUsersController.UpdatePassword)
        );

        Assert.Equal(Permissions.Users.ResetStoreStaffPassword, authorizeAttribute.Policy);
    }

    [Theory]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.Grid))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetInvoice))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetDetails))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetDetailsGrid))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetBarcodeAbnormalDetails))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetProductsByBarcode))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetProductsByProductCode))]
    public void LocalSupplierInvoiceReadEndpoints_UseManualMobileOrExistingReadAuthorization(
        string methodName
    )
    {
        var method = typeof(ReactLocalSupplierInvoicesController).GetMethod(methodName)
            ?? throw new InvalidOperationException($"Method {methodName} was not found.");

        Assert.DoesNotContain(
            method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                .OfType<AuthorizeAttribute>(),
            attribute => !string.IsNullOrWhiteSpace(attribute.Policy)
        );
    }

    // 分店进货销量分析三个接口改为方法内校验「销售看板新权限码或 LocalPurchase.View」，
    // 其授权行为由 ReactLocalSupplierInvoiceSalesAnalysisShopEndpointTests 覆盖；这里只保留仍挂策略的单据销量分析接口。
    [Theory]
    [InlineData(nameof(ReactLocalSupplierInvoiceSalesAnalysisController.GetSalesAnalysis))]
    public void LocalSupplierInvoiceSalesAnalysisEndpoints_RequireLocalPurchaseViewPermission(
        string methodName
    )
    {
        var authorizeAttribute = GetMethodAuthorizeAttribute(
            typeof(ReactLocalSupplierInvoiceSalesAnalysisController),
            methodName
        );

        Assert.Equal(Permissions.LocalPurchase.View, authorizeAttribute.Policy);
    }

    [Theory]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.Create))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.Update))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.BatchUpsertDetails))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.Delete))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.DetectSupplierItem))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.DetectBarcode))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.UpdateToStorePrices))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.StartUpdateToStorePricesJob))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetUpdateToStorePricesJob))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.CheckProducts))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.PasteDetails))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.UpdateDetailAction))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.BatchUpdateDetailAction))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.DeleteDetails))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.CheckInvoiceNoExists))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.BatchExecuteActions))]
    public void LocalSupplierInvoiceWriteEndpoints_RequireLocalPurchaseEditPermission(
        string methodName
    )
    {
        var authorizeAttribute = GetMethodAuthorizeAttribute(
            typeof(ReactLocalSupplierInvoicesController),
            methodName
        );

        Assert.Equal(Permissions.LocalPurchase.Edit, authorizeAttribute.Policy);
    }

    [Fact]
    public void WarehouseCategoryTree_AllowsAnonymousReadForDeviceHome()
    {
        var method = typeof(ReactWarehouseCategoriesController).GetMethod(
            nameof(ReactWarehouseCategoriesController.GetTree)
        ) ?? throw new InvalidOperationException("GetTree was not found.");

        Assert.NotEmpty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false));
    }

    [Theory]
    [InlineData(nameof(DeviceRegistrationController.GetDevicesPaged), Permissions.DeviceRegistration.View)]
    [InlineData(nameof(DeviceRegistrationController.GetMobileAdminDevicesPaged), Permissions.DeviceRegistration.View)]
    [InlineData(nameof(DeviceRegistrationController.ActivateDevice), Permissions.DeviceRegistration.Manage)]
    [InlineData(nameof(DeviceRegistrationController.ActivateMobileAdminDevice), Permissions.DeviceRegistration.Manage)]
    [InlineData(nameof(DeviceRegistrationController.DisableDevice), Permissions.DeviceRegistration.Manage)]
    [InlineData(nameof(DeviceRegistrationController.DisableMobileAdminDevice), Permissions.DeviceRegistration.Manage)]
    [InlineData(nameof(DeviceRegistrationController.LockDevice), Permissions.DeviceRegistration.Manage)]
    [InlineData(nameof(DeviceRegistrationController.LockMobileAdminDevice), Permissions.DeviceRegistration.Manage)]
    public void DeviceManagementEndpoints_RequireExpectedPermissionPolicy(
        string methodName,
        string expectedPolicy
    )
    {
        var authorizeAttribute = GetMethodAuthorizeAttribute(
            typeof(DeviceRegistrationController),
            methodName
        );

        Assert.Equal(expectedPolicy, authorizeAttribute.Policy);
    }

    private static ClaimsPrincipal CreateUser(params Claim[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static ClaimsPrincipal CreateUserWithId(string userGuid, params Claim[] claims)
    {
        var identityClaims = new List<Claim> { new(ClaimTypes.NameIdentifier, userGuid) };
        identityClaims.AddRange(claims);
        return CreateUser(identityClaims.ToArray());
    }

    private static void AssertWarehouseStaffStoreOrderMenuOnly(List<NavigationMenuDto> menu)
    {
        var warehouseMenu = Assert.Single(menu);
        Assert.Equal("/warehouse", warehouseMenu.Path);

        var children = warehouseMenu.Children!;
        Assert.Equal(
            new[]
            {
                "/warehouse/store-orders",
            },
            children.Select(item => item.Path).ToArray()
        );
        Assert.DoesNotContain(menu, item => item.Path == "/dashboard");
        Assert.DoesNotContain(menu, item => item.Path == "/pos-admin");
        Assert.DoesNotContain(
            warehouseMenu.Children!,
            item => item.Path == "/warehouse/products"
        );
        Assert.DoesNotContain(
            warehouseMenu.Children!,
            item => item.Path == "/warehouse/categories"
        );
        Assert.DoesNotContain(
            warehouseMenu.Children!,
            item => item.Path == "/warehouse/locations"
        );
    }

    private static AuthorizeAttribute GetMethodAuthorizeAttribute(string methodName)
    {
        return GetMethodAuthorizeAttribute(typeof(EmployeeProfilesController), methodName);
    }

    private static AuthorizeAttribute GetMethodAuthorizeAttribute(Type controllerType, string methodName)
    {
        var method = controllerType.GetMethod(methodName)
            ?? throw new InvalidOperationException($"Method {methodName} was not found.");
        return method
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .OfType<AuthorizeAttribute>()
            .Single(item => !string.IsNullOrWhiteSpace(item.Policy));
    }

    private sealed class NavigationTestHarness : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;

        public NavigationTestHarness()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
            _sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
            _sqliteConnection.Open();

            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _sqliteConnection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            });

            _db.CodeFirst.InitTables(
                typeof(User),
                typeof(Role),
                typeof(UserRole),
                typeof(SysPermission),
                typeof(SysRolePermission),
                typeof(SysUserPermission)
            );
        }

        public NavigationService CreateNavigationService()
        {
            var roleService = new RoleService(
                CreateSqlSugarContext(_db),
                NullLogger<RoleService>.Instance,
                new HttpContextAccessor()
            );

            return new NavigationService(roleService);
        }

        public async Task SeedUserWithRoleAsync(
            string userGuid,
            string roleGuid,
            string roleName,
            params string[] permissions
        )
        {
            await _db.Insertable(new User
            {
                UserGUID = userGuid,
                Username = userGuid,
                Email = $"{userGuid}@example.test",
                PasswordHash = "hash",
                IsActive = true,
            }).ExecuteCommandAsync();

            await _db.Insertable(new Role
            {
                RoleGUID = roleGuid,
                RoleName = roleName,
                IsActive = true,
            }).ExecuteCommandAsync();

            await _db.Insertable(new UserRole
            {
                UserRoleGUID = $"{userGuid}-{roleGuid}",
                UserGUID = userGuid,
                RoleGUID = roleGuid,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            if (permissions.Length == 0)
            {
                return;
            }

            var rolePermissions = permissions
                .Select(permission => new SysRolePermission
                {
                    Id = $"{roleGuid}-{permission}",
                    RoleGuid = roleGuid,
                    PermissionCode = permission,
                    IsDeleted = false,
                })
                .ToList();

            await _db.Insertable(rolePermissions).ExecuteCommandAsync();
        }

        public void Dispose()
        {
            _db.Dispose();
            _sqliteConnection.Dispose();

            if (File.Exists(_dbPath))
            {
                SqliteTempFileCleanup.DeleteIfExists(_dbPath);
            }
        }

        private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
        {
            var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
                typeof(SqlSugarContext)
            );

            var dbField = typeof(SqlSugarContext).GetField(
                "_db",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            dbField!.SetValue(context, db);

            return context;
        }
    }

}
