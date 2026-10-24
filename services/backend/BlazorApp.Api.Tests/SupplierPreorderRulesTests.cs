using BlazorApp.Api.Services.React.SupplierPreorders;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>订单表行规范化、商品匹配、下单权限判定、权限码登记。</summary>
public sealed class SupplierPreorderRulesTests
{
    private static readonly SupplierPreorderColumnMappingDto Mapping = new()
    {
        ItemCode = 0, Name = 1, Price = 2, CartonQty = 3, Barcode = 4, PrevYearItemCode = 5, Image = 6,
    };

    private static SupplierPreorderParsedRowDto Row(int rowNo, params string?[] cells) => new() { RowNo = rowNo, Cells = cells.ToList() };

    [Fact]
    public void 规范化_空行静默跳过_缺字段标记不能导入_科学计数条码忽略_NEW不是货号()
    {
        var rows = new[]
        {
            Row(10, null, null, "0", "0", null, null, null),                                   // 表头下的合计 0 行
            Row(11, "XD1", "Bauble", "1.10", "24", "9333527865090", "NEW", "https://x/XD1.jpg"),
            Row(12, "XD2", null, "abc", "2.5", "9.33353E+12", "XD0001", "Click Image"),
            Row(13, "XD3", "Bow", "$0.58", "48", null, "XD0099", null),
        };
        var items = SupplierPreorderItemRules.Normalize(rows, Mapping);

        Assert.Equal(3, items.Count);
        Assert.False(items[0].Invalid);
        Assert.Null(items[0].PrevYearItemCode);
        Assert.Equal("https://x/XD1.jpg", items[0].ImageSourceUrl);
        Assert.Equal(1, items[0].Seq);

        Assert.True(items[1].Invalid);
        Assert.Contains("缺少名称", items[1].Issues);
        Assert.Contains("进价无效", items[1].Issues);
        Assert.Contains(items[1].Issues, i => i.StartsWith("箱规无效"));
        Assert.Contains(items[1].Issues, i => i.Contains("科学计数法"));
        Assert.Null(items[1].Barcode);
        Assert.Null(items[1].ImageSourceUrl);

        Assert.Equal(0.58m, items[2].Price);
        Assert.Equal("XD0099", items[2].PrevYearItemCode);
    }

    private static SupplierPreorderNormalizedRow N(int rowNo, string code, string? barcode = null, string? prev = null) =>
        new() { RowNo = rowNo, SupplierItemCode = code, Barcode = barcode, PrevYearItemCode = prev, Name = code, Price = 1, CartonQty = 12 };

    [Fact]
    public void 匹配_条码优先_同条码多商品优先同供应商_货号不同给提示()
    {
        var products = new List<SupplierPreorderProductCandidate>
        {
            new() { ProductCode = "P-OTHER", ItemNumber = "ZZ1", Barcode = "111", LocalSupplierCode = "999", IsActive = true },
            new() { ProductCode = "P-KFC", ItemNumber = "XB4393W", Barcode = "111", LocalSupplierCode = "257", IsActive = true },
            new() { ProductCode = "P-ITEM", ItemNumber = "XD2", Barcode = "222", LocalSupplierCode = "257", IsActive = true },
            new() { ProductCode = "P-PREV", ItemNumber = "XD0001", LocalSupplierCode = "257", IsActive = true },
        };
        var rows = new List<SupplierPreorderNormalizedRow>
        {
            N(1, "XB4393", "111"),
            N(2, "XD2", "333"),
            N(3, "XD9", null, "XD0001"),
            N(4, "XD8"),
            N(5, "XD2"),
        };
        var results = SupplierPreorderItemRules.Match(rows, products, "257");

        Assert.Equal(SupplierPreorderMatchMethods.Barcode, results[0].MatchMethod);
        Assert.Equal("P-KFC", results[0].Product!.ProductCode);
        Assert.Contains(results[0].Warnings, w => w.Contains("XB4393W"));

        Assert.Equal(SupplierPreorderMatchMethods.ItemNumber, results[1].MatchMethod);
        Assert.Contains(results[1].Warnings, w => w.Contains("条码不同"));

        Assert.Equal(SupplierPreorderMatchMethods.PrevYearItemCode, results[2].MatchMethod);
        Assert.Equal("P-PREV", results[2].Product!.ProductCode);

        Assert.Equal(SupplierPreorderMatchMethods.None, results[3].MatchMethod);
        Assert.Null(results[3].Product);

        Assert.Contains(results[4].Warnings, w => w.Contains("表内货号重复"));
    }

    [Fact]
    public void 进价变动比例()
    {
        Assert.Equal(0.0588m, SupplierPreorderItemRules.PriceChangeRate(1.08m, 1.02m));
        Assert.Null(SupplierPreorderItemRules.PriceChangeRate(1.08m, null));
        Assert.Null(SupplierPreorderItemRules.PriceChangeRate(1.08m, 0m));
    }

    private static readonly DateTime Now = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Deadline = new(2026, 3, 6, 12, 59, 59, DateTimeKind.Utc);

    [Theory]
    // status, deadline 已过, isManager, isOrderer, enabled → canEdit, reason 包含
    [InlineData("Open", false, false, true, true, true, null)]
    [InlineData("Open", false, false, false, true, false, "没有被授权")]
    [InlineData("Open", false, false, true, false, false, "没有开启本店")]
    [InlineData("Open", true, false, true, true, false, "已过下单截止")]
    [InlineData("Closed", false, false, true, true, false, "截止锁定")]
    [InlineData("Draft", false, true, true, true, false, "尚未发布")]
    [InlineData("Open", true, true, false, true, true, null)]
    [InlineData("Closed", true, true, false, true, true, null)]
    [InlineData("Open", false, true, true, false, false, "没有开启本店")]
    public void 下单权限_两层授权相交_管理员不受截止限制(string status, bool pastDeadline, bool isManager, bool isOrderer, bool enabled, bool canEdit, string? reason)
    {
        var decision = SupplierPreorderAccessRules.EvaluateStoreEdit(status, Deadline, isManager, isOrderer, enabled, pastDeadline ? Deadline.AddSeconds(1) : Now);
        Assert.Equal(canEdit, decision.CanEdit);
        if (reason is null)
        {
            Assert.Null(decision.ReadOnlyReason);
        }
        else
        {
            Assert.Contains(reason, decision.ReadOnlyReason);
        }
    }

    [Theory]
    [InlineData("Draft", "Open", 10, false, true)]
    [InlineData("Draft", "Open", 0, false, false)]
    [InlineData("Open", "Closed", 10, true, true)]
    [InlineData("Closed", "Open", 10, true, true)]
    [InlineData("Open", "Draft", 10, false, true)]
    [InlineData("Open", "Draft", 10, true, false)]
    [InlineData("Draft", "Closed", 10, false, false)]
    public void 状态流转(string from, string to, int itemCount, bool hasLines, bool allowed)
    {
        Assert.Equal(allowed, SupplierPreorderAccessRules.ValidateStatusTransition(from, to, itemCount, hasLines) is null);
    }

    [Fact]
    public void 权限码已登记_无别名_无默认角色_仅Web_入库SQL只登记不授予()
    {
        foreach (var code in new[] { Permissions.SupplierPreorder.Manage, Permissions.SupplierPreorder.Order })
        {
            Assert.Contains(PermissionSeedData.AllPermissions, seed => seed.Code == code);
            Assert.True(PermissionSurfaceCatalog.IsRegistered(code));
            Assert.Equal(new[] { "web" }, PermissionSurfaceCatalog.ResolveNames(code));
            foreach (var template in PermissionSeedData.RolePermissionTemplates)
            {
                Assert.DoesNotContain(code, template.PermissionCodes);
            }
        }

        var expanded = Permissions.ExpandPermissionCodes(new[] { Permissions.Warehouse.Manage, Permissions.Warehouse.ManageOrders });
        Assert.DoesNotContain(Permissions.SupplierPreorder.Manage, expanded);
        Assert.DoesNotContain(Permissions.SupplierPreorder.Order, expanded);

        var script = File.ReadAllText(ResolveMigrationPath("20261023_AddSupplierPreorderPermissions.sql"));
        Assert.Contains("N'SupplierPreorder.Manage'", script);
        Assert.Contains("N'SupplierPreorder.Order'", script);
        Assert.Contains("DB_NAME() <> N'HBweb'", script);
        Assert.DoesNotContain("HbwebSysRolePermissions", script.Split("-- 回退方式")[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HbwebSysUserPermissions", script.Split("-- 回退方式")[0], StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveMigrationPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "BlazorApp.Api", "Data", "Migrations", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(fileName);
    }
}
