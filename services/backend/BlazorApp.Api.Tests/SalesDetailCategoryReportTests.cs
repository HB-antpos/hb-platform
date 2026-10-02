using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 销售明细「澳洲供应商分类」页签的业务口径：分类树逐级累加、同期、未归类、200 仓库分类与节点商品分页。
/// 只使用本地 SQLite 文件。
/// </summary>
public sealed class SalesDetailCategoryReportTests : IDisposable
{
    private static readonly DateTime Current = new(2026, 9, 10);
    private static readonly DateTime Previous = new(2025, 9, 10);
    private readonly string _localPath = Path.Combine(Path.GetTempPath(), $"sales-detail-category-local-{Guid.NewGuid():N}.db");
    private readonly string _posmPath = Path.Combine(Path.GetTempPath(), $"sales-detail-category-posm-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _posmConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarClient _posmDb;
    private readonly HashSet<DateTime> _states = new();

    public SalesDetailCategoryReportTests()
    {
        _localConnection = new SqliteConnection($"Data Source={_localPath}");
        _posmConnection = new SqliteConnection($"Data Source={_posmPath}");
        _localConnection.Open();
        _posmConnection.Open();
        _localDb = new SqlSugarClient(Config(_localConnection.ConnectionString));
        _posmDb = new SqlSugarClient(Config(_posmConnection.ConnectionString));
        _localDb.CodeFirst.InitTables(new[]
        {
            typeof(Store), typeof(HBLocalSupplier), typeof(ChinaSupplier), typeof(Product),
            typeof(ProductStoreDailySalesStatistic), typeof(SalesStatisticRefreshState),
            typeof(LocalSupplierCategory), typeof(LocalSupplierCategoryProductAssignment), typeof(WarehouseCategory),
        });
        _posmDb.CodeFirst.InitTables(new[] { typeof(PosmProductSupplierMapping) });
    }

    [Fact]
    public async Task 分类树按父级累加本期同期并把无分类和已删分类计入未归类()
    {
        await SeedScenarioAsync();

        var response = await CreateService().GetSalesDetailCategoryReportAsync(
            Period(), new[] { "A1", "200" }, new() { "S1" });

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, response.StatisticStatus);
        var data = response.Data!;
        // A1：10+20+40+50+60=180，同期 5+8；200：30+70+15=115，同期 12。
        Assert.Equal(295m, data.Summary.Revenue);
        Assert.Equal(25m, data.Summary.CompareRevenue);
        Assert.Equal(new[] { "A1", "200" }, data.Suppliers.Select(item => item.SupplierCode));

        var a1 = data.Suppliers[0];
        Assert.Equal("Supplier A1", a1.SupplierName);
        Assert.Equal(SalesDetailCategorySources.Supplier, a1.CategorySource);
        Assert.Equal(180m, a1.Revenue);
        var root = Assert.Single(a1.Categories); // 无销售的 B 分类不出现
        Assert.Equal("cat-p", root.CategoryGuid);
        Assert.Equal(30m, root.Revenue); // 子分类 10 + 直接挂在父分类上的 20
        Assert.Equal(13m, root.CompareRevenue);
        Assert.Equal(2, root.ProductCount);
        Assert.Equal(0, root.Depth);
        var child = Assert.Single(root.Children);
        Assert.Equal(("cat-a", 10m, 5m, 1), (child.CategoryGuid, child.Revenue, child.CompareRevenue!.Value, child.Depth));
        // 无分配 40、分类已删除 50、分配的供应商与商品当前供应商不一致 60。
        Assert.Equal(150m, a1.Unassigned!.Revenue);
        Assert.Equal(3, a1.Unassigned.ProductCount);
        Assert.Equal(SalesDetailCategorySources.UnassignedKey, a1.Unassigned.CategoryGuid);

        var hb = data.Suppliers[1];
        Assert.Equal(SalesDetailCategorySources.Warehouse, hb.CategorySource);
        Assert.Equal(115m, hb.Revenue);
        var warehouseRoot = Assert.Single(hb.Categories);
        Assert.Equal("WC-ROOT", warehouseRoot.CategoryGuid);
        Assert.Equal(100m, warehouseRoot.Revenue); // 原始码 200 与国内供应商码都归入 200
        Assert.Equal(100m, Assert.Single(warehouseRoot.Children).Revenue);
        Assert.Equal(15m, hb.Unassigned!.Revenue); // 商品当前供应商不是 200，不借用仓库分类
        Assert.Equal(165m, data.Unassigned.Revenue);
    }

    [Fact]
    public async Task 节点商品分页包含子分类且未归类与整供应商可单独查看()
    {
        await SeedScenarioAsync();
        var service = CreateService();

        var parent = await service.GetSalesDetailCategoryReportAsync(Period(), new[] { "A1", "200" }, new() { "S1" },
            nodeSupplierCode: "A1", nodeCategoryGuid: "CAT-P", includeTree: false);
        Assert.Empty(parent.Data!.Suppliers);
        Assert.Equal(new[] { "P-A2", "P-A1" }, parent.Data.Products!.Rows.Select(row => row.Code)); // 按本期营业额降序
        Assert.Equal(2, parent.Data.Products.Total);
        Assert.Equal(5m, parent.Data.Products.Rows[1].CompareRevenue); // P-A1 同期

        var unassigned = await service.GetSalesDetailCategoryReportAsync(Period(), new[] { "A1" }, new() { "S1" },
            nodeSupplierCode: "A1", nodeCategoryGuid: SalesDetailCategorySources.UnassignedKey, includeTree: false);
        Assert.Equal(new[] { "P-A5", "P-A4", "P-A3" }, unassigned.Data!.Products!.Rows.Select(row => row.Code));

        var whole = await service.GetSalesDetailCategoryReportAsync(Period(), new[] { "A1" }, new() { "S1" },
            nodeSupplierCode: "A1", pageSize: 2, pageIndex: 2, includeTree: false);
        Assert.Equal(5, whole.Data!.Products!.Total);
        Assert.Equal(new[] { "P-A3", "P-A2" }, whole.Data.Products.Rows.Select(row => row.Code));

        var warehouse = await service.GetSalesDetailCategoryReportAsync(Period(), new[] { "200" }, new() { "S1" },
            nodeSupplierCode: "200", nodeCategoryGuid: "WC-ROOT", search: "Name P-CN", includeTree: false);
        Assert.Equal("P-CN", Assert.Single(warehouse.Data!.Products!.Rows).Code);
    }

    [Fact]
    public async Task 分类节点汇总与销售明细澳洲分类筛选口径一致()
    {
        await SeedScenarioAsync();
        var service = CreateService();
        var tree = await service.GetSalesDetailCategoryReportAsync(Period(), new[] { "A1", "200" }, new() { "S1" });

        foreach (var (supplier, guid) in new[] { ("A1", "cat-p"), ("A1", "cat-a"), ("200", "WC-ROOT"), ("200", "WC-1") })
        {
            var node = Find(tree.Data!.Suppliers.Single(item => item.SupplierCode == supplier).Categories, guid);
            var detail = await service.GetSalesDetailReportFilteredAsync(Period(), SalesDetailKind.Australia, new() { "S1" },
                selectedSupplierCodes: new() { supplier }, supplierCategoryGuids: new() { guid },
                sections: new[] { SalesDetailSection.Summary });
            Assert.Equal(detail.Data!.Summary!.Summary!.Revenue, node.Revenue);
            Assert.Equal(detail.Data.Summary.Summary.CompareRevenue, node.CompareRevenue);
        }
    }

    [Fact]
    public async Task 分店范围与失败日按销售明细口径过滤()
    {
        await SeedScenarioAsync();
        await SeedStatisticAsync(Current, "S2", "A1", "P-A1", 9, 900m);
        var failed = Current.AddDays(1);
        await SeedStatisticAsync(failed, "S1", "A1", "P-A1", 1, 1000m);
        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => new SalesStatisticRefreshState { Status = SalesStatisticRefreshStatus.Failed })
            .Where(row => row.Date == failed).ExecuteCommandAsync();
        var range = new DateRangeDto { StartDate = Current, EndDate = failed, CompareStartDate = Previous, CompareEndDate = Previous.AddDays(1) };

        var response = await CreateService().GetSalesDetailCategoryReportAsync(range, new[] { "A1" }, new() { "S1" });

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, response.StatisticStatus);
        Assert.Contains("已跳过", response.StatisticMessage);
        Assert.Equal(180m, response.Data!.Summary.Revenue);
    }

    [Fact]
    public async Task 商品页支持按数量升降序且非法排序字段明确拒绝()
    {
        await SeedScenarioAsync();
        await SeedStatisticAsync(Current, "S2", "A1", "P-A2", 30, 1m); // 另一家店的 P-A2：数量最多、营业额仍低
        var service = CreateService();

        var byQuantity = await service.GetSalesDetailCategoryReportAsync(Period(), new[] { "A1" }, new() { "S1", "S2" },
            nodeSupplierCode: "A1", includeTree: false, sortBy: "quantity");
        Assert.Equal("P-A2", byQuantity.Data!.Products!.Rows[0].Code);
        var ascending = await service.GetSalesDetailCategoryReportAsync(Period(), new[] { "A1" }, new() { "S1", "S2" },
            nodeSupplierCode: "A1", includeTree: false, sortBy: "quantity", sortAscending: true);
        Assert.Equal("P-A1", ascending.Data!.Products!.Rows[0].Code);
        var byRevenue = await service.GetSalesDetailCategoryReportAsync(Period(), new[] { "A1" }, new() { "S1", "S2" },
            nodeSupplierCode: "A1", includeTree: false);
        Assert.Equal("P-A5", byRevenue.Data!.Products!.Rows[0].Code);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetSalesDetailCategoryReportAsync(
            Period(), new[] { "A1" }, nodeSupplierCode: "A1", sortBy: "margin"));
    }

    [Fact]
    public async Task 同一请求按统计版本命中缓存且统计重新发布后重新读取()
    {
        await SeedScenarioAsync();
        var service = CreateService();
        var first = await service.GetSalesDetailCategoryReportAsync(Period(), new[] { "A1" }, new() { "S1" });
        Assert.Same(first, await service.GetSalesDetailCategoryReportAsync(Period(), new[] { "A1" }, new() { "S1" }));

        // 统计重新发布：补一笔销售并刷新发布时间，版本号变化后必须读到新数据。
        var current = Current;
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = Current, BranchCode = "S2", SupplierCode = "A1", ProductCode = "P-A1", ProductName = "P-A1",
            TotalQuantity = 1, TotalAmount = 100m, OrderCount = 1, CostSource = "Test", UpdateTime = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => new SalesStatisticRefreshState { LastAggregatedAtUtc = DateTime.UtcNow.AddMinutes(1) })
            .Where(row => row.Date == current).ExecuteCommandAsync();
        var refreshed = await service.GetSalesDetailCategoryReportAsync(Period(), new[] { "A1" }, new() { "S1", "S2" });
        var again = await service.GetSalesDetailCategoryReportAsync(Period(), new[] { "A1" }, new() { "S1" });
        Assert.NotSame(first, again);
        Assert.NotEqual(first.CacheVersion, again.CacheVersion);
        Assert.Equal(280m, refreshed.Data!.Summary.Revenue);
    }

    [Fact]
    public async Task 商品数据集内存分页与SQL分页逐项一致()
    {
        await SeedScenarioAsync();
        await SeedStatisticAsync(Current, "S2", "A1", "P-A2", 30, 1m);
        var viaDataset = CreateService();
        var viaSql = CreateService();
        viaSql.CategoryProductDatasetEnabled = false;
        static string Json(object? value) => System.Text.Json.JsonSerializer.Serialize(value);
        foreach (var (supplier, guid) in new (string, string?)[] { ("A1", null), ("A1", "cat-p"), ("A1", "cat-a"), ("A1", SalesDetailCategorySources.UnassignedKey), ("200", null), ("200", "WC-ROOT") })
        foreach (var (sortBy, ascending) in new (string?, bool)[] { (null, false), ("quantity", false), ("quantity", true), ("revenue", true) })
        foreach (var (pageIndex, pageSize) in new[] { (1, 20), (1, 2), (2, 2) })
        {
            var expected = await viaSql.GetSalesDetailCategoryReportAsync(Period(), new[] { "A1", "200" }, new() { "S1", "S2" },
                nodeSupplierCode: supplier, nodeCategoryGuid: guid, pageIndex: pageIndex, pageSize: pageSize, includeTree: false, sortBy: sortBy, sortAscending: ascending);
            var actual = await viaDataset.GetSalesDetailCategoryReportAsync(Period(), new[] { "A1", "200" }, new() { "S1", "S2" },
                nodeSupplierCode: supplier, nodeCategoryGuid: guid, pageIndex: pageIndex, pageSize: pageSize, includeTree: false, sortBy: sortBy, sortAscending: ascending);
            Assert.True(Json(expected.Data) == Json(actual.Data), $"{supplier}/{guid}/{sortBy}/{ascending}/{pageIndex}x{pageSize}\nSQL: {Json(expected.Data)}\n数据集: {Json(actual.Data)}");
        }
    }

    [Fact]
    public async Task 不在范围内的节点供应商与空供应商明确拒绝()
    {
        var service = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetSalesDetailCategoryReportAsync(Period(), Array.Empty<string>()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetSalesDetailCategoryReportAsync(
            Period(), new[] { "A1" }, nodeSupplierCode: "A2"));
    }

    [Fact]
    public async Task 供应商选项带分类数并把有分类的排在前面()
    {
        await SeedScenarioAsync();
        await _localDb.Insertable(new HBLocalSupplier { Guid = "s-z", LocalSupplierCode = "Z9", Name = "No categories" }).ExecuteCommandAsync();

        await _localDb.Insertable(new[]
        {
            new Store { StoreGUID = "st-1", StoreCode = "S1", StoreName = "Store One" },
            new Store { StoreGUID = "st-2", StoreCode = "S2", StoreName = "Store Two" },
        }).ExecuteCommandAsync();

        var all = await CreateService().GetSalesDetailCategoryOptionsAsync(null);
        var scoped = await CreateService().GetSalesDetailCategoryOptionsAsync(new() { "S2" });
        var options = all.Suppliers;

        Assert.Equal(new[] { "S1", "S2" }, all.Stores.Select(store => store.StoreCode));
        Assert.Equal("Store Two", Assert.Single(scoped.Stores).StoreName);

        Assert.Equal(new[] { "200", "A1", "Z9" }, options.Select(item => item.SupplierCode));
        Assert.Equal((SalesDetailCategorySources.Warehouse, 2, 2), (options[0].CategorySource, options[0].CategoryCount, options[0].AssignedProductCount));
        Assert.Equal((3, 2), (options[1].CategoryCount, options[1].AssignedProductCount)); // 已删分类不计；P-A4 归到已删分类、P-A5 归属与商品供应商不一致，都不算已归类
        Assert.Equal(0, options[2].CategoryCount);
    }

    private static SalesDetailCategoryNodeDto Find(IEnumerable<SalesDetailCategoryNodeDto> nodes, string guid)
        => nodes.SelectMany(node => new[] { node }.Concat(node.Children)).Single(node => node.CategoryGuid == guid);

    private async Task SeedScenarioAsync()
    {
        await _localDb.Insertable(new[]
        {
            new HBLocalSupplier { Guid = "s-a1", LocalSupplierCode = "A1", Name = "Supplier A1" },
            new HBLocalSupplier { Guid = "s-200", LocalSupplierCode = "200", Name = "Hot Bargain" },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ChinaSupplier { Guid = "guid-cn1", SupplierCode = "CN1", SupplierName = "国内一" }).ExecuteCommandAsync();
        await _localDb.Insertable(new[]
        {
            Product("P-A1", "A1"), Product("P-A2", "A1"), Product("P-A3", "A1"), Product("P-A4", "A1"), Product("P-A5", "A2"),
            Product("P-HB", "200", "WC-1"), Product("P-CN", null, "WC-1"), Product("P-HB2", "240", "WC-1"),
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new[]
        {
            new LocalSupplierCategory { CategoryGUID = "cat-p", LocalSupplierCode = "A1", CategoryName = "Parent" },
            new LocalSupplierCategory { CategoryGUID = "cat-a", ParentGUID = "cat-p", LocalSupplierCode = "A1", CategoryName = "Child", Depth = 1 },
            new LocalSupplierCategory { CategoryGUID = "cat-b", LocalSupplierCode = "A1", CategoryName = "No sales" },
            new LocalSupplierCategory { CategoryGUID = "cat-x", LocalSupplierCode = "A1", CategoryName = "Deleted", IsDeleted = true },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new[]
        {
            new LocalSupplierCategoryProductAssignment { ProductCode = "P-A1", LocalSupplierCode = "A1", CategoryGUID = "cat-a" },
            new LocalSupplierCategoryProductAssignment { ProductCode = "P-A2", LocalSupplierCode = "A1", CategoryGUID = "cat-p" },
            new LocalSupplierCategoryProductAssignment { ProductCode = "P-A4", LocalSupplierCode = "A1", CategoryGUID = "cat-x" },
            new LocalSupplierCategoryProductAssignment { ProductCode = "P-A5", LocalSupplierCode = "A1", CategoryGUID = "cat-a" },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new[]
        {
            new WarehouseCategory { CategoryGUID = "WC-ROOT", CategoryName = "仓库父类" },
            new WarehouseCategory { CategoryGUID = "WC-1", ParentGUID = "WC-ROOT", CategoryName = "仓库子类" },
        }).ExecuteCommandAsync();

        await SeedStatisticAsync(Current, "S1", "A1", "P-A1", 1, 10m);
        await SeedStatisticAsync(Previous, "S1", "A1", "P-A1", 1, 5m);
        await SeedStatisticAsync(Current, "S1", "A1", "P-A2", 2, 20m);
        await SeedStatisticAsync(Previous, "S1", "A1", "P-A2", 1, 8m);
        await SeedStatisticAsync(Current, "S1", "A1", "P-A3", 4, 40m);
        await SeedStatisticAsync(Current, "S1", "A1", "P-A4", 5, 50m);
        await SeedStatisticAsync(Current, "S1", "A1", "P-A5", 6, 60m);
        await SeedStatisticAsync(Current, "S1", "200", "P-HB", 3, 30m);
        await SeedStatisticAsync(Previous, "S1", "200", "P-HB", 1, 12m);
        await SeedStatisticAsync(Current, "S1", "CN1", "P-CN", 7, 70m);
        await SeedStatisticAsync(Current, "S1", "200", "P-HB2", 1, 15m);
        await SeedStatisticAsync(Current, "S1", "A9", "P-OTHER", 1, 999m); // 未选供应商不计入
    }

    private static Product Product(string code, string? supplier, string? warehouseCategory = null) => new()
    {
        UUID = $"uuid-{code}", ProductCode = code, ProductName = $"Name {code}", ItemNumber = $"item-{code}",
        LocalSupplierCode = supplier, WarehouseCategoryGUID = warehouseCategory,
    };

    private static DateRangeDto Period() => new()
    {
        StartDate = Current, EndDate = Current, CompareStartDate = Previous, CompareEndDate = Previous,
    };

    private async Task SeedStatisticAsync(DateTime date, string branch, string supplier, string product, int quantity, decimal amount)
    {
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = date, BranchCode = branch, SupplierCode = supplier, ProductCode = product, ProductName = product,
            TotalQuantity = quantity, TotalAmount = amount, OrderCount = 1, CostSource = "Test", UpdateTime = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        if (!_states.Add(date.Date)) return;
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily, Date = date.Date,
            Status = SalesStatisticRefreshStatus.Fresh, LastAggregatedAtUtc = DateTime.UtcNow, CompletedAtUtc = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private SalesDashboardReactService CreateService()
    {
        var local = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(local, _localDb);
        var posm = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(POSMSqlSugarContext));
        typeof(POSMSqlSugarContext).GetField("_db", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(posm, _posmDb);
        return new SalesDashboardReactService(local, posm, null!, NullLogger<SalesDashboardReactService>.Instance,
            new MemoryCache(new MemoryCacheOptions()), null, new ConfigurationBuilder().Build());
    }

    private static ConnectionConfig Config(string connectionString) => new()
    {
        ConnectionString = connectionString, DbType = DbType.Sqlite,
        IsAutoCloseConnection = false, InitKeyType = InitKeyType.Attribute,
    };

    public void Dispose()
    {
        _localConnection.Dispose(); _posmConnection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_localPath); SqliteTempFileCleanup.DeleteIfExists(_posmPath);
    }
}
