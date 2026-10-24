using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class SalesDashboardCostStatusTests
{
    private static readonly DateTime CurrentDate = new(2026, 9, 6);
    private static readonly DateTime CompareDate = new(2025, 9, 7);

    [Theory]
    [InlineData(1, 1, 0, "Missing")]
    [InlineData(0, 0, 0, "NoActivity")]
    [InlineData(1, 1, 1, "Complete")]
    [InlineData(2, 1, 2, "Missing")]
    [InlineData(2, 2, 1, "Missing")]
    public void GetCostStatus_UsesStrictStatisticCostAndGrossProfitCoverage(
        int statisticRowCount,
        int costedRowCount,
        int grossProfitRowCount,
        string expected)
    {
        // 通过反射保持报表服务 helper 的最小可见面，避免为纯状态判断扩展生产 API。
        var method = typeof(SalesDashboardReactService).GetMethod(
            "GetCostStatus",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var actual = method!.Invoke(
            null,
            new object[] { statisticRowCount, costedRowCount, grossProfitRowCount });

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task SqlServer分页服务方法把完整性计数映射到最终商品Dto()
    {
        var path = Path.Combine(Path.GetTempPath(), $"report-cost-status-{Guid.NewGuid():N}.db");
        try
        {
            using var productDb = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={path}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });
            productDb.CodeFirst.InitTables<Product>();
            await productDb.Insertable(new[]
            {
                new Product { ProductCode = "G006243", ProductName = "OPEN ITEM" },
                new Product { ProductCode = "MISSING-COST", ProductName = "缺成本商品" },
            }).ExecuteCommandAsync();

            var sqlRows = new[]
            {
                new SalesDashboardReactService.ProductReportPagingSqlRow
                {
                    HasData = true,
                    TotalCount = 2,
                    ProductCode = "G006243",
                    CurrentQuantity = 1142,
                    CurrentSalesAmount = 5044.4374m,
                    CurrentGrossProfit = 3020.1534m,
                    CurrentStatisticRowCount = 1,
                    CurrentCostedRowCount = 1,
                    CurrentGrossProfitRowCount = 1,
                },
                new SalesDashboardReactService.ProductReportPagingSqlRow
                {
                    HasData = true,
                    TotalCount = 2,
                    ProductCode = "MISSING-COST",
                    CurrentQuantity = 2,
                    CurrentSalesAmount = 20m,
                    CurrentStatisticRowCount = 1,
                    CurrentCostedRowCount = 0,
                    CurrentGrossProfitRowCount = 0,
                    CompareQuantity = 1,
                    CompareSalesAmount = 8m,
                    CompareGrossProfit = 5m,
                    CompareStatisticRowCount = 1,
                    CompareCostedRowCount = 1,
                    CompareGrossProfitRowCount = 1,
                },
            };
            var ado = new Mock<IAdo>(MockBehavior.Strict);
            // 月 / 日投影表不存在：走日统计原路径，本用例验证的就是原路径的映射。
            ado.Setup(value => value.GetIntAsync(It.IsAny<string>(), It.IsAny<SugarParameter[]>())).ReturnsAsync(0);
            ado.Setup(value => value.SqlQueryAsync<SalesDashboardReactService.ProductReportPagingSqlRow>(
                    It.IsAny<string>(), It.IsAny<SugarParameter[]>()))
                .ReturnsAsync(sqlRows.ToList());
            var reportDb = new Mock<ISqlSugarClient>(MockBehavior.Strict);
            reportDb.SetupGet(value => value.CurrentConnectionConfig)
                .Returns(new ConnectionConfig { DbType = DbType.SqlServer });
            reportDb.SetupGet(value => value.Ado).Returns(ado.Object);
            reportDb.Setup(value => value.Queryable<Product>()).Returns(productDb.Queryable<Product>());

            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = CreateService(reportDb.Object, cache);
            var page = await service.GetEnhancedSalesProductDetailsSqlServerAsync(
                new DateRangeDto
                {
                    StartDate = CurrentDate,
                    EndDate = CurrentDate,
                    CompareStartDate = CompareDate,
                    CompareEndDate = CompareDate,
                },
                null,
                null,
                null,
                1,
                20,
                null,
                false
            );

            var openItem = Assert.Single(page.Data, row => row.ProductCode == "G006243");
            Assert.Equal("Complete", openItem.CostStatus);
            Assert.Equal("NoActivity", openItem.CompareCostStatus);
            var missing = Assert.Single(page.Data, row => row.ProductCode == "MISSING-COST");
            Assert.Equal("Missing", missing.CostStatus);
            Assert.Equal("Complete", missing.CompareCostStatus);
        }
        finally
        {
            SqliteTempFileCleanup.DeleteIfExists(path);
        }
    }

    [Theory]
    [InlineData("unitPrice", "asc")]
    [InlineData("quantity", "desc")]
    [InlineData(null, null)]
    public async Task GetEnhancedSalesProductDetailsSqlServerAsync_Sort_把排序后的分页SQL交给数据库(
        string? sortField,
        string? sortOrder
    )
    {
        var sort = ProductReportSort.Parse(sortField, sortOrder);
        string? capturedSql = null;
        var ado = new Mock<IAdo>(MockBehavior.Strict);
        // 月 / 日投影表不存在：走日统计原路径，本用例验证的就是原路径的排序 SQL。
        ado.Setup(value => value.GetIntAsync(It.IsAny<string>(), It.IsAny<SugarParameter[]>())).ReturnsAsync(0);
        ado.Setup(value => value.SqlQueryAsync<SalesDashboardReactService.ProductReportPagingSqlRow>(
                It.IsAny<string>(), It.IsAny<SugarParameter[]>()))
            .Callback<string, SugarParameter[]>((sql, _) => capturedSql = sql)
            // 无数据页也返回一行 HasData=false；严格 Mock 未配置 Queryable<Product>，证明不会读取商品主表。
            .ReturnsAsync(new List<SalesDashboardReactService.ProductReportPagingSqlRow>
            {
                new() { HasData = false, TotalCount = 0 },
            });
        var reportDb = new Mock<ISqlSugarClient>(MockBehavior.Strict);
        reportDb.SetupGet(value => value.CurrentConnectionConfig)
            .Returns(new ConnectionConfig { DbType = DbType.SqlServer });
        reportDb.SetupGet(value => value.Ado).Returns(ado.Object);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(reportDb.Object, cache);
        var page = await service.GetEnhancedSalesProductDetailsSqlServerAsync(
            new DateRangeDto
            {
                StartDate = CurrentDate,
                EndDate = CurrentDate,
                CompareStartDate = CompareDate,
                CompareEndDate = CompareDate,
            },
            null,
            null,
            null,
            1,
            20,
            null,
            false,
            sort
        );

        // 路径 A 的排序只能通过白名单 SQL 生效：实际执行文本必须与同一排序生成的分页 SQL 逐字相同。
        Assert.Equal(SalesDashboardReactService.BuildProductReportPagingSql(includeCompare: true, sort), capturedSql);
        Assert.Contains(SalesDashboardReactService.BuildProductReportRankingOrderBy(sort), capturedSql, StringComparison.Ordinal);
        Assert.Empty(page.Data);
        Assert.Equal(0, page.Total);
        ado.Verify(value => value.SqlQueryAsync<SalesDashboardReactService.ProductReportPagingSqlRow>(
            It.IsAny<string>(), It.IsAny<SugarParameter[]>()), Times.Once);
    }

    private static async Task<string?> CaptureProductPagingSqlAsync(
        bool projectionReady,
        DateRangeDto range,
        List<string>? branchCodes = null,
        string? productSearch = null
    )
    {
        string? capturedSql = null;
        var ado = new Mock<IAdo>(MockBehavior.Strict);
        ado.Setup(value => value.GetIntAsync(It.IsAny<string>(), It.IsAny<SugarParameter[]>()))
            .ReturnsAsync(projectionReady ? 1 : 0);
        ado.Setup(value => value.SqlQueryAsync<SalesDashboardReactService.ProductReportPagingSqlRow>(
                It.IsAny<string>(), It.IsAny<SugarParameter[]>()))
            .Callback<string, SugarParameter[]>((sql, _) => capturedSql = sql)
            .ReturnsAsync(new List<SalesDashboardReactService.ProductReportPagingSqlRow>
            {
                new() { HasData = false, TotalCount = 0 },
            });
        var reportDb = new Mock<ISqlSugarClient>(MockBehavior.Strict);
        reportDb.SetupGet(value => value.CurrentConnectionConfig)
            .Returns(new ConnectionConfig { DbType = DbType.SqlServer });
        reportDb.SetupGet(value => value.Ado).Returns(ado.Object);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(reportDb.Object, cache);
        await service.GetEnhancedSalesProductDetailsSqlServerAsync(
            range, branchCodes, null, null, 1, 20, productSearch, false, default);
        return capturedSql;
    }

    private static DateRangeDto YearWithCompare() => new()
    {
        StartDate = new DateTime(2025, 10, 21),
        EndDate = new DateTime(2026, 10, 21),
        CompareStartDate = new DateTime(2024, 10, 22),
        CompareEndDate = new DateTime(2025, 10, 22),
    };

    [Fact]
    public async Task 投影就绪且全部分店无关键词时执行投影版分页SQL()
    {
        var sql = await CaptureProductPagingSqlAsync(projectionReady: true, YearWithCompare());

        Assert.Equal(SalesDashboardReactService.BuildProductReportProjectionPagingSql(includeCompare: true), sql);
    }

    [Fact]
    public async Task 投影表不存在时退回日统计原路径()
    {
        var sql = await CaptureProductPagingSqlAsync(projectionReady: false, YearWithCompare());

        Assert.Equal(SalesDashboardReactService.BuildProductReportPagingSql(includeCompare: true), sql);
    }

    [Fact]
    public async Task 选了具体分店时即使投影就绪也走原路径()
    {
        // 月 / 日投影没有门店维度，选分店必须读日统计。
        var sql = await CaptureProductPagingSqlAsync(
            projectionReady: true, YearWithCompare(), branchCodes: new List<string> { "1003" });

        Assert.Equal(SalesDashboardReactService.BuildProductReportPagingSql(includeCompare: true), sql);
    }

    [Fact]
    public async Task 带货号或条码关键词时即使投影就绪也走原路径()
    {
        // 投影没有条码，关键词匹配必须读日统计。
        var sql = await CaptureProductPagingSqlAsync(
            projectionReady: true, YearWithCompare(), productSearch: "8801234");

        Assert.Equal(SalesDashboardReactService.BuildProductReportPagingSql(includeCompare: true), sql);
    }

    [Fact]
    public async Task 区间超过731天时即使投影就绪也走原路径()
    {
        var range = new DateRangeDto
        {
            StartDate = new DateTime(2024, 1, 1),
            EndDate = new DateTime(2026, 1, 1),
        };

        var sql = await CaptureProductPagingSqlAsync(projectionReady: true, range);

        Assert.Equal(SalesDashboardReactService.BuildProductReportPagingSql(includeCompare: false), sql);
    }

    [SalesCostBackfillSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task SqlServer完整报表入口保留商品分店与供应商成本状态()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            "COST_BACKFILL_SQLSERVER_TEST_CONNECTION"
        );
        Assert.False(string.IsNullOrWhiteSpace(baseConnectionString));

        var baseBuilder = new SqlConnectionStringBuilder(baseConnectionString!);
        // 该测试会创建并销毁数据库，只允许成本回填专用的本机 SQL Server。
        Assert.Equal("127.0.0.1,11439", baseBuilder.DataSource);
        var databaseName = $"HBreport_cost_test_{Guid.NewGuid():N}";
        var masterConnectionString = BuildConnectionString(baseConnectionString!, "master");
        var databaseConnectionString = BuildConnectionString(baseConnectionString!, databaseName);
        await ExecuteNonQueryAsync(
            masterConnectionString,
            $"CREATE DATABASE {QuoteSqlServerName(databaseName)};"
        );

        try
        {
            using var db = new SqlSugarClient(CreateSqlServerConfig(databaseConnectionString));
            db.CodeFirst.InitTables(
                typeof(Product),
                typeof(ProductStoreDailySalesStatistic),
                typeof(SalesStatisticRefreshState),
                typeof(AustralianSupplierStoreSalesDetail),
                typeof(ChinaSupplierStoreSalesDetail),
                typeof(HBLocalSupplier),
                typeof(ChinaSupplier),
                typeof(Store)
            );
            await SeedCompleteReportAsync(db);

            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = CreateService(db, cache);
            var range = new DateRangeDto
            {
                StartDate = CurrentDate,
                EndDate = CurrentDate,
                CompareStartDate = CompareDate,
                CompareEndDate = CompareDate,
            };

            // 调用控制器使用的公开服务入口，覆盖 SQL Server 分页 SQL、完整快照包络及最终 DTO 映射。
            var page = await service.GetEnhancedSalesProductDetailsAsync(
                range,
                pageIndex: 1,
                pageSize: 20
            );
            var openItem = Assert.Single(page.Data, row => row.ProductCode == "G006243");
            Assert.Equal(5044.4374m, openItem.SalesAmount);
            Assert.Equal(3020.1534m, openItem.GrossProfit);
            Assert.Equal("Complete", openItem.CostStatus);
            Assert.Equal("NoActivity", openItem.CompareCostStatus);

            var missing = Assert.Single(page.Data, row => row.ProductCode == "MISSING-COST");
            Assert.Null(missing.GrossProfit);
            Assert.Equal("Missing", missing.CostStatus);
            Assert.Equal("Complete", missing.CompareCostStatus);

            var branch = Assert.Single(
                await service.GetProductSalesByAllBranchesAsync(range, "G006243")
            );
            Assert.Equal("Complete", branch.CostStatus);
            Assert.Equal("NoActivity", branch.CompareCostStatus);

            var supplier = Assert.Single(await service.GetSupplierSalesRankAsync(range));
            Assert.Equal("Missing", supplier.CostStatus);
            Assert.Equal("Complete", supplier.CompareCostStatus);

            var supplierStore = Assert.Single(
                await service.GetSupplierStoreSalesAsync(range, new List<string> { "240" })
            );
            Assert.Equal("Missing", supplierStore.CostStatus);
            Assert.Equal("Complete", supplierStore.CompareCostStatus);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await DropDatabaseAsync(masterConnectionString, databaseName);
        }
    }

    private static async Task SeedCompleteReportAsync(SqlSugarClient db)
    {
        await db.Insertable(new[]
        {
            new Product
            {
                ProductCode = "G006243",
                ItemNumber = "OPENITEM",
                ProductName = "OPEN ITEM",
                LocalSupplierCode = "240",
            },
            new Product
            {
                ProductCode = "MISSING-COST",
                ItemNumber = "MISSING-COST",
                ProductName = "缺成本商品",
                LocalSupplierCode = "240",
            },
        }).ExecuteCommandAsync();

        await db.Insertable(new[]
        {
            new ProductStoreDailySalesStatistic
            {
                Date = CurrentDate,
                BranchCode = "1015",
                SupplierCode = "240",
                ProductCode = "G006243",
                ProductName = "OPEN ITEM",
                TotalQuantity = 1142,
                TotalAmount = 5044.4374m,
                OrderCount = 200,
                TotalCost = 2024.284m,
                GrossProfit = 3020.1534m,
                CostSource = "OpenItemFormula",
            },
            new ProductStoreDailySalesStatistic
            {
                Date = CurrentDate,
                BranchCode = "1015",
                SupplierCode = "240",
                ProductCode = "MISSING-COST",
                ProductName = "缺成本商品",
                TotalQuantity = 2,
                TotalAmount = 20m,
                OrderCount = 1,
                TotalCost = null,
                GrossProfit = null,
                CostSource = "Missing",
            },
            new ProductStoreDailySalesStatistic
            {
                Date = CompareDate,
                BranchCode = "1015",
                SupplierCode = "240",
                ProductCode = "MISSING-COST",
                ProductName = "缺成本商品",
                TotalQuantity = 1,
                TotalAmount = 8m,
                OrderCount = 1,
                TotalCost = 3m,
                GrossProfit = 5m,
                CostSource = "HistoricalSnapshot",
            },
        }).ExecuteCommandAsync();

        await db.Insertable(new[]
        {
            new AustralianSupplierStoreSalesDetail
            {
                Date = CurrentDate,
                BranchCode = "1015",
                SupplierCode = "240",
                TotalAmount = 5064.4374m,
                TotalQuantity = 1144,
                OrderCount = 201,
                TotalCost = null,
                GrossProfit = null,
                StatisticRowCount = 2,
                CostedRowCount = 1,
                GrossProfitRowCount = 1,
            },
            new AustralianSupplierStoreSalesDetail
            {
                Date = CompareDate,
                BranchCode = "1015",
                SupplierCode = "240",
                TotalAmount = 8m,
                TotalQuantity = 1,
                OrderCount = 1,
                TotalCost = 3m,
                GrossProfit = 5m,
                StatisticRowCount = 1,
                CostedRowCount = 1,
                GrossProfitRowCount = 1,
            },
        }).ExecuteCommandAsync();
        await db.Insertable(new HBLocalSupplier
        {
            Guid = Guid.NewGuid().ToString(),
            LocalSupplierCode = "240",
            Name = "Dats",
        }).ExecuteCommandAsync();
        await db.Insertable(new Store
        {
            StoreGUID = Guid.NewGuid().ToString(),
            StoreCode = "1015",
            StoreName = "测试分店",
        }).ExecuteCommandAsync();

        var publishedAt = DateTime.UtcNow;
        foreach (var date in new[] { CurrentDate, CompareDate })
        {
            var version = $"version-{date:yyyyMMdd}";
            foreach (var type in new[]
            {
                SalesStatisticType.ProductStoreDaily,
                SalesStatisticType.AustralianSupplierStoreSales,
                SalesStatisticType.ChinaSupplierStoreSales,
            })
            {
                await db.Insertable(new SalesStatisticRefreshState
                {
                    Date = date,
                    StatisticType = type,
                    Status = SalesStatisticRefreshStatus.Fresh,
                    SourceProductVersion = version,
                    LastAggregatedAtUtc = publishedAt,
                    CompletedAtUtc = publishedAt,
                }).ExecuteCommandAsync();
            }
        }
    }

    private static SalesDashboardReactService CreateService(
        ISqlSugarClient db,
        IMemoryCache cache
    )
    {
        var context = InjectContext<SqlSugarContext>(db);
        var posmContext = InjectContext<POSMSqlSugarContext>(db);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Reports:UseSupplierRollups"] = "true",
            })
            .Build();
        return new SalesDashboardReactService(
            context,
            posmContext,
            Mock.Of<IMapper>(),
            NullLogger<SalesDashboardReactService>.Instance,
            cache,
            configuration: configuration
        );
    }

    private static T InjectContext<T>(ISqlSugarClient db)
    {
        var context = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        typeof(T).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static ConnectionConfig CreateSqlServerConfig(string connectionString) => new()
    {
        ConnectionString = connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true,
        InitKeyType = InitKeyType.Attribute,
        MoreSettings = new ConnMoreSettings { SqlServerCodeFirstNvarchar = true },
    };

    private static string BuildConnectionString(string connectionString, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = databaseName,
        };
        return builder.ConnectionString;
    }

    private static async Task ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string masterConnectionString, string databaseName)
    {
        var name = QuoteSqlServerName(databaseName);
        await ExecuteNonQueryAsync(
            masterConnectionString,
            $"ALTER DATABASE {name} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {name};"
        );
    }

    private static string QuoteSqlServerName(string name) =>
        $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
}
