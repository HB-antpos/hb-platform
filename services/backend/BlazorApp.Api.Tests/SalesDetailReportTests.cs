using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// SalesDetail 一次聚合读取的本地行为契约。测试只使用两个 SQLite 文件，不访问远程数据库。
/// </summary>
public sealed class SalesDetailReportTests : IDisposable
{
    private readonly string _localPath = Path.Combine(Path.GetTempPath(), $"sales-detail-report-local-{Guid.NewGuid():N}.db");
    private readonly string _posmPath = Path.Combine(Path.GetTempPath(), $"sales-detail-report-posm-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _posmConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarClient _posmDb;
    private readonly HashSet<DateTime> _states = new();

    public SalesDetailReportTests()
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
    public async Task 商品分页支持二百和五百条且保持总数及页边界()
    {
        var day = new DateTime(2026, 9, 23);
        var codes = Enumerable.Range(1, 501).Select(index => $"P-{index:D3}").ToArray();
        await _localDb.Insertable(codes.Select(code => new Product
        {
            UUID = $"uuid-{code}", ProductCode = code, ProductName = code, ItemNumber = $"item-{code}",
        }).ToList()).ExecuteCommandAsync();
        await _localDb.Insertable(codes.Select(code => new ProductStoreDailySalesStatistic
        {
            Date = day, BranchCode = "S1", SupplierCode = "A1", ProductCode = code, ProductName = code,
            TotalQuantity = 1, TotalAmount = 10m, OrderCount = 1, CostSource = "Test", UpdateTime = DateTime.UtcNow,
        }).ToList()).ExecuteCommandAsync();
        await SeedStateAsync(day);

        var service = CreateService();
        var first = await service.GetSalesDetailReportAsync(Range(day, day), SalesDetailKind.Australia,
            new() { "S1" }, pageSize: 200, sections: new[] { SalesDetailSection.Products });
        var last = await service.GetSalesDetailReportAsync(Range(day, day), SalesDetailKind.Australia,
            new() { "S1" }, pageIndex: 3, pageSize: 200, sections: new[] { SalesDetailSection.Products });
        var large = await service.GetSalesDetailReportAsync(Range(day, day), SalesDetailKind.Australia,
            new() { "S1" }, pageSize: 500, sections: new[] { SalesDetailSection.Products });
        var largeLast = await service.GetSalesDetailReportAsync(Range(day, day), SalesDetailKind.Australia,
            new() { "S1" }, pageIndex: 2, pageSize: 500, sections: new[] { SalesDetailSection.Products });

        Assert.Equal(501, first.Data!.Products!.Total);
        Assert.Equal(200, first.Data.Products.Rows.Count);
        Assert.Equal(101, last.Data!.Products!.Rows.Count);
        Assert.Equal(500, large.Data!.Products!.Rows.Count);
        Assert.Single(largeLast.Data!.Products!.Rows);
    }

    [Fact]
    public async Task 同一天本期同期仍各保留一份并按数量分页排序()
    {
        var day = new DateTime(2026, 7, 1);
        await SeedCatalogAsync("CN1", "国内一");
        await SeedProductAsync("P-HIGH");
        await SeedStatisticAsync(day, "S1", "200", "P-HIGH", 20, 200m);
        await SeedMappingAsync("P-HIGH", "CN1");

        var response = await CreateService().GetSalesDetailReportAsync(
            Range(day, day), SalesDetailKind.China, new() { "S1" }, pageSize: 1,
            sections: new[] { SalesDetailSection.Products });

        Assert.NotNull(response.Data);
        var row = Assert.Single(response.Data!.Products!.Rows);
        Assert.Equal(200m, row.Revenue);
        Assert.Equal(200m, row.CompareRevenue);
        Assert.Equal(20, row.Quantity);
        Assert.Equal(20, row.CompareQuantity);
        Assert.Equal(1, response.Data.Products.Total);

        var englishSearch = await CreateService().GetSalesDetailReportAsync(
            Range(day, day), SalesDetailKind.China, new() { "S1" }, search: "English",
            sections: new[] { SalesDetailSection.Products });
        Assert.Equal("P-HIGH", Assert.Single(englishSearch.Data!.Products!.Rows).Code);
    }

    [Fact]
    public async Task 四栏反向筛选分母和客单字段保持业务口径()
    {
        var day = new DateTime(2026, 7, 2);
        await _localDb.Insertable(new HBLocalSupplier { Guid = "local-200", LocalSupplierCode = "200", Name = "HB仓库" }).ExecuteCommandAsync();
        await SeedCatalogAsync("CN1", "国内一");
        await SeedCatalogAsync("CN2", "国内二");
        await SeedProductAsync("P-HIGH");
        await SeedProductAsync("P-LOW");
        await SeedStatisticAsync(day, "S1", "200", "P-HIGH", 20, 200m);
        await SeedStatisticAsync(day, "S1", "200", "P-LOW", 5, 50m);
        await SeedStatisticAsync(day, "S1", "A1", "P-HIGH", 10, 100m);
        await SeedMappingAsync("P-HIGH", "CN1");
        await SeedMappingAsync("P-LOW", "CN1");

        var service = CreateService();
        var china = await service.GetSalesDetailReportAsync(
            Range(day, day), SalesDetailKind.China, new() { "S1" }, selectedSupplierCode: "CN1",
            selectedProductCode: "P-HIGH", pageSize: 1);

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, china.StatisticStatus);
        Assert.NotNull(china.Data!.Suppliers);
        Assert.All(china.Data.Suppliers!.Rows, row => Assert.Equal(1, row.OrderCount));
        Assert.All(china.Data.Suppliers.Rows, row => Assert.Equal(200m, row.AverageTransaction));
        var cn1 = Assert.Single(china.Data.Suppliers.Rows);
        Assert.Equal("CN1", cn1.Code);
        Assert.Equal("国内一", cn1.Name);
        var branch = Assert.Single(china.Data.Branches!.Rows);
        Assert.Equal(1, branch.OrderCount);
        Assert.Equal(200m, branch.AverageTransaction);
        Assert.Equal(200m / 250m, cn1.Share!.Value, 6); // 国内标签的供应商占国内分母。
        Assert.Equal(200m / 350m, cn1.ChinaShare!.Value, 6);

        var productPage = china.Data.Products!;
        Assert.Equal("P-HIGH", Assert.Single(productPage.Rows).Code);
        Assert.Equal(1, china.Data.Summary!.Summary!.OrderCount);
        Assert.Equal(200m, china.Data.Summary.Summary.AverageTransaction);

        // 未锁定商品时只有日汇总的订单行数，不能把跨商品订单数相加后冒充去重结果。
        var unscoped = await service.GetSalesDetailReportAsync(
            Range(day, day), SalesDetailKind.China, new() { "S1" }, selectedSupplierCode: "CN1",
            sections: new[] { SalesDetailSection.Summary });
        Assert.Null(unscoped.Data!.Summary!.Summary!.OrderCount);
        Assert.Null(unscoped.Data.Summary.Summary.AverageTransaction);

        var australia = await service.GetSalesDetailReportAsync(
            Range(day, day), SalesDetailKind.Australia, new() { "S1" }, pageSize: 1,
            sections: new[] { SalesDetailSection.Products });
        Assert.Equal("P-HIGH", Assert.Single(australia.Data!.Products!.Rows).Code);
        Assert.Equal(30, australia.Data.Products.Rows[0].Quantity);
        var australiaSuppliers = await service.GetSalesDetailReportAsync(
            Range(day, day), SalesDetailKind.Australia, new() { "S1" }, sections: new[] { SalesDetailSection.Suppliers });
        var localSupplier = Assert.Single(australiaSuppliers.Data!.Suppliers!.Rows, row => row.Code == "200");
        Assert.Equal("HB仓库", localSupplier.Name);
    }

    [Fact]
    public async Task 无销售行但统计状态完整时返回空分页而非未准备()
    {
        var day = new DateTime(2026, 7, 3);
        await SeedStateAsync(day);
        var response = await CreateService().GetSalesDetailReportAsync(
            Range(day, day), SalesDetailKind.Australia, new() { "S1" });

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, response.StatisticStatus);
        Assert.Empty(response.Data!.Products!.Rows);
        Assert.Equal(0, response.Data.Products.Total);
        Assert.Equal(0m, response.Data.Products.Summary!.Revenue);
    }

    [Fact]
    public async Task 统计水位按Utc序列化并保留Z后缀()
    {
        var day = new DateTime(2026, 9, 7);
        var completedAtUtc = new DateTime(2026, 9, 7, 3, 32, 59, DateTimeKind.Utc);
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            Date = day,
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Status = SalesStatisticRefreshStatus.Fresh,
            LastAggregatedAtUtc = completedAtUtc,
            CompletedAtUtc = completedAtUtc,
            SourceProductVersion = "utc-version",
        }).ExecuteCommandAsync();

        var response = await CreateService().GetSalesDetailReportAsync(
            Range(day, day), SalesDetailKind.Australia, new() { "S1" });

        Assert.Equal(DateTimeKind.Utc, response.StatisticUpdatedAt!.Value.Kind);
        Assert.Contains("2026-09-07T03:32:59Z", JsonSerializer.Serialize(response));
    }

    [Fact]
    public void 无效分类哨兵仍生成零匹配分类过滤条件()
    {
        var method = typeof(SalesDashboardReactService).GetMethod(
            "BuildSalesDetailCategoryFilter", BindingFlags.NonPublic | BindingFlags.Static)!;
        var sql = Assert.IsType<string>(method.Invoke(null, new object?[]
        {
            SalesDetailKind.Australia,
            new[] { "__sales_detail_no_matching_category__" },
            Array.Empty<string>(),
            "s.[ProductCode]",
        }));

        Assert.Contains("LocalSupplierCategoryProductAssignment", sql);
        Assert.Contains("@sdrCategorySupplier0", sql);
    }

    [Fact]
    public void 澳洲供应商可见范围将国内供应商事实归并为200()
    {
        var visible = SalesDetailCategoryOptionsController.MapVisibleSupplierCodes(
            new (string? Code, string? ChinaCode)[] { ("CN-01", "CN-01"), ("A1", null), ("200", null) });

        Assert.Contains("200", visible);
        Assert.Contains("A1", visible);
        Assert.DoesNotContain("CN-01", visible);
    }

    [Theory]
    [InlineData("200", null, "200", null)]
    [InlineData("200,A1", null, "200,A1", null)]
    [InlineData("A1,A2", null, "A1,A2", "200")]
    [InlineData("200,A1", "S1,S2", "200,A1,S1,S2", null)]
    public void 澳洲供应商可见范围_SQLServer查询不生成裸布尔参数谓词(
        string supplierCodes,
        string? storeCodes,
        string expectedParameterValues,
        string? unexpectedParameterValue)
    {
        using var sqlServerDb = new SqlSugarClient(new ConnectionConfig
        {
            // ToSql 只生成 SQL，不会连接数据库；方言必须固定为 SQL Server 才能覆盖 4145 回归。
            ConnectionString = "Server=127.0.0.1;Database=SalesDetailCategoryOptionsSqlTests;User Id=unused;Password=unused;TrustServerCertificate=True",
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        var suppliers = supplierCodes.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var stores = storeCodes?.Split(',', StringSplitOptions.RemoveEmptyEntries);

        var command = SalesDetailCategoryOptionsController.BuildVisibleSupplierQuery(
                sqlServerDb,
                suppliers,
                stores)
            .ToSql();
        var generatedCommand = string.Join('\n',
            command.Key,
            string.Join(' ', command.Value.Select(parameter => parameter.Value?.ToString())));

        Assert.Contains("[ProductStoreDailySalesStatistic]", command.Key);
        Assert.Contains("LEFT JOIN [ChinaSupplier]", command.Key, StringComparison.OrdinalIgnoreCase);
        // SQL Server 4145 的根因是 SqlSugar 把 C# bool 闭包生成成 bit 参数后直接放进 AND。
        // 参数清单中不能再出现 bool；其余供应商/门店值可能被 SqlSugar 内联到 IN，所以检查完整生成命令。
        Assert.DoesNotContain(command.Value, parameter => parameter.Value is bool);
        foreach (var expected in expectedParameterValues.Split(','))
            Assert.Contains(expected, generatedCommand, StringComparison.Ordinal);
        if (unexpectedParameterValue != null)
            Assert.DoesNotContain(unexpectedParameterValue, generatedCommand, StringComparison.Ordinal);
        if (stores != null)
            Assert.Contains("[sales].[BranchCode]", command.Key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 多选筛选超过服务端上限时明确拒绝而不执行查询()
    {
        var day = new DateTime(2026, 9, 22);
        var service = CreateService();
        var tooManySuppliers = Enumerable.Range(0, 201).Select(index => $"S-{index}").ToList();
        var tooManyCategories = Enumerable.Range(0, 501).Select(index => $"C-{index}").ToList();

        var supplierError = await Assert.ThrowsAsync<ArgumentException>(() => service.GetSalesDetailReportFilteredAsync(
            Range(day, day), SalesDetailKind.Australia, new() { "S1" }, selectedSupplierCodes: tooManySuppliers));
        Assert.Contains("最多选择 200", supplierError.Message);

        var legacySupplierError = await Assert.ThrowsAsync<ArgumentException>(() => service.GetSalesDetailReportFilteredAsync(
            Range(day, day), SalesDetailKind.Australia, new() { "S1" }, selectedSupplierCode: "S-extra",
            selectedSupplierCodes: tooManySuppliers.Take(200).ToList()));
        Assert.Contains("最多选择 200", legacySupplierError.Message);

        var categoryError = await Assert.ThrowsAsync<ArgumentException>(() => service.GetSalesDetailReportFilteredAsync(
            Range(day, day), SalesDetailKind.Australia, new() { "S1" }, supplierCategoryGuids: tooManyCategories));
        Assert.Contains("最多选择 500", categoryError.Message);
    }

    [Fact]
    public async Task 澳洲供应商分类多选包含自营仓库分类且不串到其他供应商()
    {
        var day = new DateTime(2026, 9, 22);
        await _localDb.Insertable(new[]
        {
            new Product { UUID = "uuid-a", ProductCode = "P-A", ProductName = "A", LocalSupplierCode = "A1" },
            new Product { UUID = "uuid-b", ProductCode = "P-B", ProductName = "B", LocalSupplierCode = "A2" },
            new Product { UUID = "uuid-hb", ProductCode = "P-HB", ProductName = "HB", LocalSupplierCode = "200", WarehouseCategoryGUID = "WC-HB" },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new[]
        {
            new LocalSupplierCategory { CategoryGUID = "CAT-A", LocalSupplierCode = "A1", CategoryName = "A分类" },
            new LocalSupplierCategory { CategoryGUID = "CAT-B", LocalSupplierCode = "A2", CategoryName = "B分类" },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new[]
        {
            new LocalSupplierCategoryProductAssignment { ProductCode = "P-A", LocalSupplierCode = "A1", CategoryGUID = "CAT-A" },
            new LocalSupplierCategoryProductAssignment { ProductCode = "P-B", LocalSupplierCode = "A2", CategoryGUID = "CAT-B" },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new WarehouseCategory { CategoryGUID = "WC-HB", CategoryName = "自营分类" }).ExecuteCommandAsync();
        await SeedStatisticAsync(day, "S1", "A1", "P-A", 1, 10m);
        await SeedStatisticAsync(day, "S1", "A2", "P-A", 5, 50m); // 同码历史销售不可借用 A1 分类。
        await SeedStatisticAsync(day, "S1", "A2", "P-B", 2, 20m);
        await SeedStatisticAsync(day, "S1", "200", "P-HB", 3, 30m);

        var service = CreateService();
        var selected = await service.GetSalesDetailReportFilteredAsync(Range(day, day), SalesDetailKind.Australia,
            new() { "S1" }, selectedSupplierCodes: new() { "A1", "A2", "200" },
            supplierCategoryGuids: new() { "CAT-A", "WC-HB" });
        Assert.Equal(40m, selected.Data!.Summary!.Summary!.Revenue);
        Assert.Equal(new[] { "P-A", "P-HB" }, selected.Data.Products!.Rows.Select(row => row.Code).OrderBy(code => code));

        var onlyWarehouse = await service.GetSalesDetailReportFilteredAsync(Range(day, day), SalesDetailKind.Australia,
            new() { "S1" }, selectedSupplierCodes: new() { "200" }, supplierCategoryGuids: new() { "WC-HB" });
        Assert.Equal(30m, onlyWarehouse.Data!.Summary!.Summary!.Revenue);

        var onlySupplierCodes = await service.GetSalesDetailReportFilteredAsync(Range(day, day), SalesDetailKind.Australia,
            new() { "S1" }, selectedSupplierCodes: new() { "A1" });
        Assert.Equal(10m, onlySupplierCodes.Data!.Summary!.Summary!.Revenue);
    }

    [Fact]
    public void SQLServerBuilder先聚合窄事实再延迟连接元信息()
    {
        var method = typeof(SalesDashboardReactService).GetMethod(
            "BuildSalesDetailReportSql",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        var sql = Assert.IsType<string>(method.Invoke(null, new object?[]
        {
            true,
            null,
            Range(new DateTime(2026, 7, 4), new DateTime(2026, 7, 4)),
            SalesDetailKind.China,
            new List<string> { "S1" },
            null,
            null,
            null,
            "English barcode",
            1,
            20,
            Enum.GetValues<SalesDetailSection>().ToHashSet(),
            null,
        }));

        var groupBy = sql.IndexOf("GROUP BY [Period], [RawSupplierCode]", StringComparison.Ordinal);
        var productJoin = sql.IndexOf("OUTER APPLY (SELECT TOP (1) p0.[ProductName]", StringComparison.Ordinal);
        Assert.True(groupBy >= 0);
        Assert.True(productJoin > groupBy);
        Assert.DoesNotContain("GROUP BY [Period], [RawSupplierCode], [ChinaSupplierCode], [AustralianSupplierCode], [BranchCode], [ProductCode], [ProductImage]", sql);
        Assert.DoesNotContain("GROUP BY [Period], [RawSupplierCode], [ChinaSupplierCode], [AustralianSupplierCode], [BranchCode], [ProductCode], [EnglishName]", sql);
        Assert.Contains("MAX([StatisticProductName]) [StatisticProductName]", sql);
        Assert.Contains("MAX([StatisticBarcode]) [StatisticBarcode]", sql);
        Assert.Contains("p0.[Barcode]", sql);
        Assert.Contains("[StatisticBarcode] LIKE @sdrSearch0", sql);
        Assert.Contains("pSearch.[Barcode] LIKE @sdrSearch0", sql);
        Assert.Contains("INTO #SalesDetailProductSearchMatches", sql);
        Assert.Contains("INTO #SalesDetailSupplierSearchMatches", sql);
        Assert.Equal(2, CountOccurrences(sql, "FROM [Product] pSearch"));
        Assert.Equal(2, CountOccurrences(sql, "FROM [ChinaSupplier] cSearch"));
        Assert.DoesNotContain("EXISTS (SELECT 1 FROM [Product] pSearch", sql);
        Assert.DoesNotContain("EXISTS (SELECT 1 FROM [ChinaSupplier] cSearch", sql);
        Assert.Contains("LEFT JOIN #SalesDetailProductSearchMatches pMatch0", sql);
        Assert.Contains("LEFT JOIN #SalesDetailSupplierSearchMatches cMatch0", sql);
        Assert.Contains("pMatch0.[ProductCode] IS NOT NULL", sql);
        Assert.Contains("cMatch0.[SupplierCode] IS NOT NULL", sql);
        Assert.Contains("INTO #SalesDetailSearchFacts", sql);
        Assert.Equal(1, CountOccurrences(sql, "INTO #SalesDetailSearchFacts"));
        Assert.Contains("NULLIF(LTRIM(RTRIM(local.[Name])), '')", sql);
        Assert.Contains("THEN 'hotbargain' ELSE f.[AustralianSupplierCode] END", sql);
        Assert.Contains("COUNT([TotalCost]) [CostedRowCount]", sql);
        Assert.Contains("ORDER BY a.[Quantity] DESC, a.[CompareQuantity] DESC, a.[ProductCode] ASC", sql);
        Assert.Contains("DROP TABLE #SalesDetailFacts;", sql);

        var narrowSql = Assert.IsType<string>(method.Invoke(null, new object?[]
        {
            true,
            null,
            Range(new DateTime(2026, 7, 4), new DateTime(2026, 7, 4)),
            SalesDetailKind.China,
            new List<string> { "S1" },
            null,
            null,
            null,
            null,
            1,
            20,
            Enum.GetValues<SalesDetailSection>().ToHashSet(),
            null,
        }));
        Assert.DoesNotContain("COUNT(DISTINCT CASE", narrowSql);
        Assert.Contains("MIN(CASE WHEN [Period]=0 THEN [ProductCode] END)", narrowSql);
        Assert.Contains("SUM(s.[TotalAmount]) [Revenue]", narrowSql);
        Assert.Contains("GROUP BY periods.[Period]", narrowSql);
        Assert.Contains("CAST(NULL AS nvarchar(255)) [StatisticProductName]", narrowSql);
        Assert.Contains("stat.[StatisticProductName]", narrowSql);
        Assert.Contains("NULLIF(LTRIM(RTRIM(p.[ProductName])), '') IS NULL AND NULLIF(LTRIM(RTRIM(a.[StatisticProductName])), '') IS NULL", narrowSql);
        Assert.Contains("@sdrCompareStart", narrowSql);
        Assert.Contains("s0.[BranchCode] IN (@sdrBranch0)", narrowSql);
        Assert.Contains("SELECT MAX(sName.[StoreName])", narrowSql);
        Assert.Contains("ORDER BY [Revenue] DESC, [CompareRevenue] DESC, [Code] ASC", narrowSql);
        Assert.Contains("NULLIF(LTRIM(RTRIM((SELECT MAX(lName.[Name])", narrowSql);
        Assert.Contains("THEN 'hotbargain' ELSE f.[SupplierCode] END", narrowSql);
        Assert.DoesNotContain("LEFT JOIN [LocalSupplier]", narrowSql);
        Assert.DoesNotContain("LEFT JOIN [Store] store", narrowSql);
        Assert.Contains("ORDER BY a.[Quantity] DESC, a.[CompareQuantity] DESC, a.[ProductCode] ASC", narrowSql);
        Assert.Contains("GROUP BY GROUPING SETS", narrowSql);
        Assert.Contains("SELECT * INTO #SalesDetailAggregates FROM Grouped", narrowSql);
        Assert.Contains("FROM [#SalesDetailFacts] f WHERE [AustralianSupplierCode] IS NOT NULL AND f.[BranchCode] IN (@sdrBranch0)", narrowSql);
        Assert.Contains("DROP TABLE #SalesDetailAggregates;", narrowSql);
        Assert.Contains("SELECT COUNT(DISTINCT [ProductCode]) FROM #SalesDetailAggregates WHERE [GroupType]=3", narrowSql);

        var productsOnlySql = Assert.IsType<string>(method.Invoke(null, new object?[]
        {
            true,
            null,
            Range(new DateTime(2026, 7, 4), new DateTime(2026, 7, 4)),
            SalesDetailKind.China,
            new List<string> { "S1" },
            null,
            null,
            null,
            null,
            1,
            20,
            new HashSet<SalesDetailSection> { SalesDetailSection.Products },
            null,
        }));
        Assert.Contains("GROUP BY GROUPING SETS", productsOnlySql);
        Assert.Contains("SELECT TOP 0 CAST(NULL AS nvarchar(50)) [Code]", productsOnlySql);

        var hugePageSql = Assert.IsType<string>(method.Invoke(null, new object?[]
        {
            true,
            null,
            Range(new DateTime(2026, 7, 4), new DateTime(2026, 7, 4)),
            SalesDetailKind.China,
            new List<string> { "S1" },
            null,
            null,
            null,
            null,
            int.MaxValue,
            20,
            new HashSet<SalesDetailSection> { SalesDetailSection.Products },
            null,
        }));
        Assert.Contains("OFFSET 42949672920 ROWS", hugePageSql);

        var legacyHugePageSql = Assert.IsType<string>(method.Invoke(null, new object?[]
        {
            false,
            null,
            Range(new DateTime(2026, 7, 4), new DateTime(2026, 7, 4)),
            SalesDetailKind.China,
            new List<string> { "S1" },
            null,
            null,
            null,
            null,
            int.MaxValue,
            20,
            new HashSet<SalesDetailSection> { SalesDetailSection.Products },
            null,
        }));
        Assert.Contains("LIMIT 20 OFFSET 42949672920", legacyHugePageSql);
    }

    [Fact]
    public void SQLServer默认路径先按原始键聚合再去空格重聚合且分组集合走哈希()
    {
        var core = typeof(SalesDashboardReactService).GetMethod(
            "BuildSalesDetailReportSqlServerCore", BindingFlags.NonPublic | BindingFlags.Static)!;
        string Build(string? search) => Assert.IsType<string>(core.Invoke(null, new object?[]
        {
            "POSM", Range(new DateTime(2026, 7, 4), new DateTime(2026, 7, 4)), SalesDetailKind.China,
            new List<string> { "S1" }, null, null, null, search, 1, 20,
            Enum.GetValues<SalesDetailSection>().ToHashSet(), null, false, false,
        }));

        var sql = Build(null);
        // 内层只按事实表原始列分组，扫描才能走列存索引的批处理聚合；去空格放到聚合后的窄行上再分组一次。
        Assert.Contains("GROUP BY periods.[Period], s.[SupplierCode], s.[BranchCode], s.[ProductCode]", sql);
        Assert.Contains("FROM SourceFacts\n GROUP BY [Period], LTRIM(RTRIM(COALESCE([RawSupplierCode],'')))", sql);
        Assert.DoesNotContain("GROUP BY periods.[Period], LTRIM(RTRIM(COALESCE(s.[SupplierCode],'')))", sql);
        Assert.Equal(1, CountOccurrences(sql, "INTO #SalesDetailFacts"));
        Assert.DoesNotContain("#SalesDetailRawFacts", sql);
        // GROUPING SETS 强制哈希聚合，避免为几百个供应商/分店键排序几十万行事实。
        Assert.Contains("SELECT * INTO #SalesDetailAggregates FROM Grouped OPTION (HASH GROUP);", sql);
        Assert.Contains("DROP TABLE #SalesDetailFacts;", sql);

        // 关键词路径仍由宽事实一段式 SELECT INTO 建表。
        var searchSql = Build("English");
        Assert.DoesNotContain("SourceFacts", searchSql);
        Assert.Contains("INTO #SalesDetailFacts", searchSql);
    }

    [Fact]
    public async Task 早于最新状态的历史缺口只提示不阻断而尚未发布的后续日期仍为Pending()
    {
        // 2025-05-04～05-31 生产上从未生成商品日统计；这里用 7-10 缺状态、7-11 已发布模拟。
        var gap = new DateTime(2026, 7, 10);
        var published = new DateTime(2026, 7, 11);
        await SeedProductAsync("P-GAP");
        await SeedStatisticAsync(published, "S1", "A1", "P-GAP", 2, 20m);

        var response = await CreateService().GetSalesDetailReportAsync(
            new DateRangeDto { StartDate = gap, EndDate = published }, SalesDetailKind.Australia, new() { "S1" });
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, response.StatisticStatus);
        Assert.Contains("2026-07-10", response.StatisticMessage);
        Assert.Contains("没有商品日统计", response.StatisticMessage);
        Assert.Equal(20m, response.Data!.Summary!.Summary!.Revenue);

        var future = await CreateService().GetSalesDetailReportAsync(
            new DateRangeDto { StartDate = published, EndDate = published.AddDays(1) }, SalesDetailKind.Australia, new() { "S1" });
        Assert.Equal(SalesStatisticRefreshStatus.Pending, future.StatisticStatus);

        Assert.Equal("2026-05-04～2026-05-06、2026-05-10共 4 天没有商品日统计，该段金额未计入，需另行回填统计。",
            SalesDashboardReactService.DescribeMissingHistoryDates(new[] { new DateTime(2026, 5, 4), new DateTime(2026, 5, 5), new DateTime(2026, 5, 6), new DateTime(2026, 5, 10) }));
    }

    [Fact]
    public void 对账未通过日期提示按时间列出并在超过五天时汇总()
    {
        Assert.Null(SalesDashboardReactService.DescribeReconciliationFailedDates(Array.Empty<DateTime>()));
        var one = SalesDashboardReactService.DescribeReconciliationFailedDates(new[] { new DateTime(2026, 4, 9, 10, 0, 0) });
        Assert.StartsWith("2026-04-09的商品统计", one);
        var many = SalesDashboardReactService.DescribeReconciliationFailedDates(
            Enumerable.Range(0, 7).Select(offset => new DateTime(2026, 4, 15).AddDays(-offset)).ToList());
        Assert.StartsWith("2026-04-09、2026-04-10、2026-04-11、2026-04-12、2026-04-13 等 7 天", many);
    }

    [Fact]
    public void 关键词查询跳过日期提示按连续区间合并()
    {
        Assert.Null(SalesDashboardReactService.DescribeProjectionSkippedDates(Array.Empty<DateTime>()));
        Assert.Equal("2026-04-09共 1 天缺少可用的关键词查询投影，本次搜索已跳过，结果未包含这些日期的销售。",
            SalesDashboardReactService.DescribeProjectionSkippedDates(new[] { new DateTime(2026, 4, 9, 10, 0, 0) }));
        Assert.StartsWith("2025-05-04～2025-05-06、2026-04-09共 4 天",
            SalesDashboardReactService.DescribeProjectionSkippedDates(new[]
                { new DateTime(2026, 4, 9), new DateTime(2025, 5, 5), new DateTime(2025, 5, 4), new DateTime(2025, 5, 6), new DateTime(2026, 4, 9) }));
    }

    [Fact]
    public async Task 国内原始供应商在澳洲栏按本地供应商解析并支持名称搜索()
    {
        var day = new DateTime(2026, 7, 6);
        await _localDb.Insertable(new HBLocalSupplier { Guid = "local-200-search", LocalSupplierCode = "200", Name = "HB仓库" }).ExecuteCommandAsync();
        await SeedCatalogAsync("CN1", "国内一");
        await _localDb.Insertable(new Product
        {
            UUID = "uuid-local-search", ProductCode = "P-LOCAL", ProductName = "HB商品",
            EnglishName = "Local English", LocalSupplierCode = "200", ItemNumber = "local-item",
        }).ExecuteCommandAsync();
        await SeedStatisticAsync(day, "S1", "CN1", "P-LOCAL", 3, 30m);

        var service = CreateService();
        var byCode = await service.GetSalesDetailReportAsync(
            Range(day, day), SalesDetailKind.Australia, new() { "S1" }, search: "200",
            sections: new[] { SalesDetailSection.Products });
        var byName = await service.GetSalesDetailReportAsync(
            Range(day, day), SalesDetailKind.Australia, new() { "S1" }, search: "HB仓库",
            sections: new[] { SalesDetailSection.Products });

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, byCode.StatisticStatus);
        Assert.Equal("P-LOCAL", Assert.Single(byCode.Data!.Products!.Rows).Code);
        Assert.Equal("P-LOCAL", Assert.Single(byName.Data!.Products!.Rows).Code);
    }

    [Theory]
    [InlineData(SalesStatisticRefreshStatus.Queued, true, "published", SalesStatisticRefreshStatus.Fresh)]
    [InlineData(SalesStatisticRefreshStatus.Running, true, "published", SalesStatisticRefreshStatus.Fresh)]
    [InlineData(SalesStatisticRefreshStatus.Queued, true, null, SalesStatisticRefreshStatus.Pending)]
    [InlineData(SalesStatisticRefreshStatus.Running, true, null, SalesStatisticRefreshStatus.Pending)]
    [InlineData(SalesStatisticRefreshStatus.Running, false, null, SalesStatisticRefreshStatus.Pending)]
    [InlineData(SalesStatisticRefreshStatus.Failed, true, "published", SalesStatisticRefreshStatus.Fresh)]
    [InlineData(SalesStatisticRefreshStatus.Failed, true, null, SalesStatisticRefreshStatus.Fresh)]
    [InlineData(SalesStatisticRefreshStatus.Failed, false, null, SalesStatisticRefreshStatus.Fresh)]
    public async Task 单一商品快照按已发布状态读取并跳过失败日(string state, bool published, string? version, string expected)
    {
        var day = new DateTime(2026, 7, 7);
        await SeedProductAsync("P-PUBLISHED");
        await SeedStatisticAsync(day, "S1", "A1", "P-PUBLISHED", 3, 30m);
        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => new SalesStatisticRefreshState
            {
                Status = state,
                LastAggregatedAtUtc = published ? DateTime.UtcNow : null,
                CompletedAtUtc = published ? DateTime.UtcNow : null,
                SourceProductVersion = version,
            }).Where(row => row.Date == day && row.StatisticType == SalesStatisticType.ProductStoreDaily)
            .ExecuteCommandAsync();
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            Date = day, StatisticType = SalesStatisticType.AustralianSupplierStoreSales,
            Status = SalesStatisticRefreshStatus.Failed,
        }).ExecuteCommandAsync();

        var response = await CreateService().GetSalesDetailReportAsync(
            Range(day, day), SalesDetailKind.Australia, new() { "S1" });
        Assert.Equal(expected, response.StatisticStatus);
        // 商品统计失败日期即使保留旧聚合事实，也不能计入明细；提示必须说明金额已跳过。
        if (state == SalesStatisticRefreshStatus.Failed)
        {
            Assert.Contains("2026-07-07", response.StatisticMessage);
            Assert.Contains("已跳过", response.StatisticMessage);
            Assert.Empty(response.Data!.Products!.Rows);
            return;
        }
        else if (expected == SalesStatisticRefreshStatus.Fresh)
            Assert.Null(response.StatisticMessage);
        if (expected == SalesStatisticRefreshStatus.Fresh)
        {
            Assert.Equal(30m, response.Data!.Summary!.Summary!.Revenue);
            Assert.Equal("P-PUBLISHED", Assert.Single(response.Data.Products!.Rows).Code);
            var page = await CreateService().GetSalesDetailReportAsync(
                Range(day, day), SalesDetailKind.Australia, new() { "S1" },
                sections: new[] { SalesDetailSection.Products });
            Assert.Equal(response.CacheVersion, page.CacheVersion);
        }
        else Assert.Null(response.Data!.Products);
    }

    [Fact]
    public async Task 明细范围内失败日不计入四栏且提示日期()
    {
        var failed = new DateTime(2026, 7, 7);
        var fresh = failed.AddDays(1);
        await SeedProductAsync("P-PARTIAL");
        await SeedStatisticAsync(failed, "S1", "A1", "P-PARTIAL", 3, 30m);
        await SeedStatisticAsync(fresh, "S1", "A1", "P-PARTIAL", 2, 20m);
        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => new SalesStatisticRefreshState { Status = SalesStatisticRefreshStatus.Failed })
            .Where(row => row.Date == failed && row.StatisticType == SalesStatisticType.ProductStoreDaily)
            .ExecuteCommandAsync();

        var response = await CreateService().GetSalesDetailReportAsync(
            Range(failed, fresh), SalesDetailKind.Australia, new() { "S1" });

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, response.StatisticStatus);
        Assert.Contains("2026-07-07", response.StatisticMessage);
        Assert.Contains("已跳过", response.StatisticMessage);
        Assert.Equal(20m, response.Data!.Summary!.Summary!.Revenue);
        Assert.Equal(20m, Assert.Single(response.Data.Suppliers!.Rows).Revenue);
        Assert.Equal(20m, Assert.Single(response.Data.Branches!.Rows).Revenue);
        Assert.Equal(20m, Assert.Single(response.Data.Products!.Rows).Revenue);
    }

    [Fact]
    public async Task 分店单商品客单保留且商品反查不要求额外选择供应商()
    {
        var day = new DateTime(2026, 7, 8);
        await SeedProductAsync("P-ONLY");
        await SeedStatisticAsync(day, "S1", "A1", "P-ONLY", 2, 20m);
        var service = CreateService();
        var all = await service.GetSalesDetailReportAsync(Range(day, day), SalesDetailKind.Australia);
        Assert.Equal(1, Assert.Single(all.Data!.Branches!.Rows).OrderCount);
        Assert.Null(all.Data.Branches.Summary!.OrderCount);
        var selected = await service.GetSalesDetailReportAsync(
            Range(day, day), SalesDetailKind.Australia, selectedProductCode: "P-ONLY");
        var row = Assert.Single(selected.Data!.Branches!.Rows);
        Assert.Equal(1, row.OrderCount);
        Assert.Equal(20m, row.AverageTransaction);
    }

    private static DateRangeDto Range(DateTime start, DateTime end) => new()
    {
        StartDate = start, EndDate = end, CompareStartDate = start, CompareEndDate = end,
    };

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        for (var offset = 0; (offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0; offset += value.Length)
            count++;
        return count;
    }

    private async Task SeedCatalogAsync(string code, string name)
        => await _localDb.Insertable(new ChinaSupplier { Guid = $"guid-{code}", SupplierCode = code, SupplierName = name }).ExecuteCommandAsync();

    private async Task SeedProductAsync(string code)
        => await _localDb.Insertable(new Product { UUID = $"uuid-{code}", ProductCode = code, ProductName = code, EnglishName = $"English {code}", ItemNumber = $"item-{code}" }).ExecuteCommandAsync();

    private async Task SeedStatisticAsync(DateTime date, string branch, string supplier, string product, int quantity, decimal amount)
    {
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = date, BranchCode = branch, SupplierCode = supplier, ProductCode = product, ProductName = product,
            TotalQuantity = quantity, TotalAmount = amount, OrderCount = 1, CostSource = "Test", UpdateTime = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await SeedStateAsync(date);
    }

    private async Task SeedStateAsync(DateTime date)
    {
        if (!_states.Add(date.Date)) return;
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily, Date = date.Date,
            Status = SalesStatisticRefreshStatus.Fresh, LastAggregatedAtUtc = DateTime.UtcNow, CompletedAtUtc = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private async Task SeedMappingAsync(string product, string supplier)
        => await _posmDb.Insertable(new PosmProductSupplierMapping { ProductCode = product, LocalSupplierCode = "200", ChinaSupplierCode = supplier, LastUpdateTime = DateTime.UtcNow }).ExecuteCommandAsync();

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
