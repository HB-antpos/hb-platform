using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class SalesStatisticsHistoricalCostConcurrencySqlServerFactAttribute : FactAttribute
{
    private const string ConnectionEnvironmentVariable =
        "SET_CHILD_PURCHASE_PRICE_SQLSERVER_TEST_CONNECTION";

    public SalesStatisticsHistoricalCostConcurrencySqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过真实 SQL Server 历史成本并发验证。";
        }
    }
}

/// <summary>
/// 使用独立本地 SQL Server 数据库验证历史统计写入的真实事务路径。
/// 测试同时持有同商品成本 applock，确保不是只调用 ResolveCurrentCostProductCodes 的纯单元测试。
/// </summary>
public sealed class SalesStatisticsHistoricalCostConcurrencySqlServerTests
{
    private const string ConnectionEnvironmentVariable =
        "SET_CHILD_PURCHASE_PRICE_SQLSERVER_TEST_CONNECTION";

    [SalesStatisticsHistoricalCostConcurrencySqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 历史行已有完整成本快照时_同商品成本锁持有期间仍成功且保留历史成本()
    {
        await using var fixture = await Fixture.CreateAsync();
        var date = fixture.UniqueHistoricalDate;
        var productCode = fixture.ProductCode("COMPLETE");
        var previous = Statistic(date, productCode, quantity: 2, amount: 20m, unitCost: 3m, totalCost: 6m);
        var rebuilt = Statistic(date, productCode, quantity: 2, amount: 20m, unitCost: 99m, totalCost: 198m);
        await fixture.SeedAsync(previous, rebuilt);

        using var blocker = fixture.OpenDatabase();
        await blocker.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireProductsAsync(blocker, new[] { productCode });

        var writerTask = fixture.PersistAsync(
            date,
            new[] { SourceRow(date, productCode, quantity: 2m, amount: 20m) });
        try
        {
            // blocker 到 finally 才回滚；写入若错误地等待成本锁，会一直等到预算耗尽而失败。
            var completed = await Task.WhenAny(writerTask, Task.Delay(AsyncTestWaitSupport.DefaultTimeout));
            Assert.Same(writerTask, completed);
            var result = await writerTask;
            Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.Status.Status);
        }
        finally
        {
            if (blocker.Ado.Transaction != null)
                await blocker.Ado.RollbackTranAsync();
            await writerTask;
        }

        using var verify = fixture.OpenDatabase();
        var persisted = await verify.Queryable<ProductStoreDailySalesStatistic>().SingleAsync();
        Assert.Equal(3m, persisted.UnitCostSnapshot);
        Assert.Equal(6m, persisted.TotalCost);
        Assert.Equal(14m, persisted.GrossProfit);
        Assert.Equal(20m, persisted.TotalAmount);
        Assert.Equal("ProductPurchasePrice", persisted.CostSource);
    }

    [SalesStatisticsHistoricalCostConcurrencySqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 历史缺失成本及新增销售行仍在同商品成本锁内_释放后才完成并读取当前成本()
    {
        await using var fixture = await Fixture.CreateAsync();
        var date = fixture.UniqueHistoricalDate;
        var missingCode = fixture.ProductCode("MISSING");
        var newCode = fixture.ProductCode("NEW");
        var previousMissing = Statistic(date, missingCode, quantity: 2, amount: 20m, unitCost: null, totalCost: null);
        var rebuiltMissing = Statistic(date, missingCode, quantity: 2, amount: 20m, unitCost: 99m, totalCost: 198m);
        var rebuiltNew = Statistic(date, newCode, quantity: 1, amount: 10m, unitCost: 99m, totalCost: 99m);
        await fixture.SeedAsync(previousMissing, rebuiltMissing, rebuiltNew);

        using var blocker = fixture.OpenDatabase();
        await blocker.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
            blocker,
            new[] { missingCode, newCode });

        var (writerTask, writerSpid) = await fixture.StartPersistAsync(
            date,
            new[]
            {
                SourceRow(date, missingCode, quantity: 2m, amount: 20m),
                SourceRow(date, newCode, quantity: 1m, amount: 10m),
            });
        try
        {
            Assert.True(
                await fixture.WaitForProductLockWaitAsync(writerSpid, new[] { missingCode, newCode }),
                "未观察到缺失成本或新增销售行正在等待同商品成本 applock");

            await blocker.Ado.RollbackTranAsync();
            var result = await writerTask;
            Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.Status.Status);
        }
        finally
        {
            if (blocker.Ado.Transaction != null)
                await blocker.Ado.RollbackTranAsync();
            await writerTask;
        }

        using var verify = fixture.OpenDatabase();
        var persisted = await verify.Queryable<ProductStoreDailySalesStatistic>()
            .Where(row => row.Date == date)
            .OrderBy(row => row.ProductCode)
            .ToListAsync();
        Assert.Equal(2, persisted.Count);
        Assert.Equal(7m, persisted[0].UnitCostSnapshot);
        Assert.Equal(14m, persisted[0].TotalCost);
        Assert.Equal(20m, persisted[0].TotalAmount);
        Assert.Equal("ProductPurchasePrice", persisted[0].CostSource);
        Assert.Equal(7m, persisted[1].UnitCostSnapshot);
        Assert.Equal(7m, persisted[1].TotalCost);
        Assert.Equal(10m, persisted[1].TotalAmount);
        Assert.Equal("ProductPurchasePrice", persisted[1].CostSource);
    }

    private static ProductStoreDailySalesStatistic Statistic(
        DateTime date,
        string productCode,
        int quantity,
        decimal amount,
        decimal? unitCost,
        decimal? totalCost) => new()
    {
        Date = date,
        BranchCode = "BRANCH-01",
        SupplierCode = "SUP-01",
        ProductCode = productCode,
        ProductName = productCode,
        TotalQuantity = quantity,
        TotalAmount = amount,
        OrderCount = 1,
        UnitCostSnapshot = unitCost,
        TotalCost = totalCost,
        GrossProfit = totalCost.HasValue ? amount - totalCost.Value : null,
        GrossMarginRate = totalCost.HasValue && amount > 0m
            ? (amount - totalCost.Value) / amount
            : null,
        CostSource = unitCost.HasValue ? "ProductPurchasePrice" : "Missing",
        UpdateTime = DateTime.UtcNow,
    };

    private static ProductStoreDailySourceRow SourceRow(
        DateTime date,
        string productCode,
        decimal quantity,
        decimal amount) => new()
    {
        IsHBSalesSource = true,
        Date = date,
        OrderGuid = "ORDER-" + productCode,
        DetailGuid = "DETAIL-" + productCode,
        BranchCode = "BRANCH-01",
        ProductCode = productCode,
        SupplierCode = "SUP-01",
        ProductName = productCode,
        Quantity = quantity,
        ActualAmount = amount,
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _adminConnection;
        private readonly string _databaseName =
            "HbSalesHistoryCost_" + Guid.NewGuid().ToString("N");
        private string _databaseConnection = string.Empty;

        private Fixture(string adminConnection) => _adminConnection = adminConnection;

        internal DateTime UniqueHistoricalDate =>
            SalesStatisticsBusinessDate.Today().AddDays(-2);

        internal static async Task<Fixture> CreateAsync()
        {
            var configured = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
            Assert.False(string.IsNullOrWhiteSpace(configured));
            var builder = new SqlConnectionStringBuilder(configured);
            var dataSource = builder.DataSource.Trim();
            // CI 使用 1433，本地任务容器使用 14337；两者都必须精确匹配环回地址。
            Assert.True(
                new[] { "127.0.0.1,14337", "localhost,14337", "127.0.0.1,1433", "localhost,1433" }
                    .Contains(dataSource, StringComparer.OrdinalIgnoreCase),
                $"SQL Server 集成测试只允许本地 Docker，实际 DataSource={dataSource}");

            var fixture = new Fixture(configured!);
            var master = new SqlConnection(new SqlConnectionStringBuilder(configured)
            {
                InitialCatalog = "master",
            }.ConnectionString);
            await master.OpenAsync();
            await using (master)
            await using (var command = master.CreateCommand())
            {
                command.CommandText = $"CREATE DATABASE [{fixture._databaseName}]";
                await command.ExecuteNonQueryAsync();
            }

            fixture._databaseConnection = new SqlConnectionStringBuilder(configured)
            {
                InitialCatalog = fixture._databaseName,
            }.ConnectionString;
            try
            {
                using var local = fixture.OpenDatabase();
                using var posm = fixture.OpenDatabase();
                local.CodeFirst.InitTables(
                    typeof(Product),
                    typeof(WarehouseProduct),
                    typeof(StoreRetailPrice),
                    typeof(StoreLocalSupplierInvoiceDetails),
                    typeof(ProductStoreDailySalesStatistic),
                    typeof(StoreSalesStatistic),
                    typeof(SalesStatisticRefreshState),
                    typeof(AustralianSupplierStoreSalesDetail),
                    typeof(ChinaSupplierStoreSalesDetail),
                    typeof(HBLocalSupplier),
                    typeof(ChinaSupplier));
                posm.CodeFirst.InitTables(typeof(PosmProductSupplierMapping));
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal string ProductCode(string suffix) =>
            $"HISTORY-{suffix}-{_databaseName[^8..].ToUpperInvariant()}";

        internal SqlSugarClient OpenDatabase() => new(new ConnectionConfig
        {
            ConnectionString = _databaseConnection,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });

        internal async Task<bool> WaitForProductLockWaitAsync(
            int writerSpid,
            IReadOnlyCollection<string> productCodes)
        {
            using var observer = OpenDatabase();
            var resources = productCodes
                .Select(code => TruncateApplicationResource(
                    "HB:SetChildPurchasePrice:Product:" + code))
                .ToArray();
            var deadline = DateTime.UtcNow.Add(AsyncTestWaitSupport.DefaultTimeout);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var resource in resources)
                {
                    var waiting = await observer.Ado.SqlQuerySingleAsync<int>(
                        """
                        SELECT COUNT(*)
                        FROM sys.dm_tran_locks
                        WHERE resource_type = N'APPLICATION'
                          AND resource_database_id = DB_ID()
                          AND request_session_id = @SessionId
                          AND resource_description LIKE N'%' + @Resource + N'%'
                          AND request_status = N'WAIT';
                        """,
                        new SugarParameter("@Resource", resource),
                        new SugarParameter("@SessionId", writerSpid));
                    if (waiting > 0)
                        return true;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
            return false;
        }

        private static string TruncateApplicationResource(string resource) =>
            resource.Length <= 32 ? resource : resource[..32];

        internal async Task SeedAsync(
            ProductStoreDailySalesStatistic previous,
            params ProductStoreDailySalesStatistic[] rebuilt)
        {
            using var db = OpenDatabase();
            var products = rebuilt.Select(row => new Product
            {
                UUID = row.ProductCode,
                ProductCode = row.ProductCode,
                LocalSupplierCode = row.SupplierCode,
                ProductName = row.ProductName ?? row.ProductCode,
                PurchasePrice = 7m,
                IsActive = true,
                IsDeleted = false,
            }).ToList();
            await db.Insertable(products).ExecuteCommandAsync();
            await db.Insertable(previous).ExecuteCommandAsync();
            await db.Insertable(new StoreSalesStatistic
            {
                Date = previous.Date,
                BranchCode = previous.BranchCode,
                BranchName = previous.BranchCode,
                TotalAmount = rebuilt.Sum(row => row.TotalAmount),
                TotalQuantity = rebuilt.Sum(row => row.TotalQuantity),
                OrderCount = rebuilt.Sum(row => row.OrderCount),
                CustomerCount = 1,
                AverageOrderValue = rebuilt.Sum(row => row.TotalAmount),
                UpdateTime = DateTime.UtcNow,
            }).ExecuteCommandAsync();
        }

        internal async Task<SalesStatisticsProductStoreDailyCommandWriter.PersistResult> PersistAsync(
            DateTime date,
            IReadOnlyList<ProductStoreDailySourceRow> rawRows)
        {
            var localDb = OpenDatabase();
            var posmDb = OpenDatabase();
            return await PersistWithClientsAsync(localDb, posmDb, date, rawRows);
        }

        internal async Task<
            (Task<SalesStatisticsProductStoreDailyCommandWriter.PersistResult> PersistTask, int WriterSpid)>
            StartPersistAsync(
                DateTime date,
                IReadOnlyList<ProductStoreDailySourceRow> rawRows)
        {
            var localDb = OpenDatabase();
            var posmDb = OpenDatabase();
            try
            {
                var writerSpid = await localDb.Ado.SqlQuerySingleAsync<int>("SELECT @@SPID");
                var persistTask = PersistWithClientsAsync(localDb, posmDb, date, rawRows);
                return (persistTask, writerSpid);
            }
            catch
            {
                localDb.Dispose();
                posmDb.Dispose();
                throw;
            }
        }

        private static async Task<SalesStatisticsProductStoreDailyCommandWriter.PersistResult>
            PersistWithClientsAsync(
                SqlSugarClient localDb,
                SqlSugarClient posmDb,
                DateTime date,
                IReadOnlyList<ProductStoreDailySourceRow> rawRows)
        {
            try
            {
                var context = CreateContext<SqlSugarContext>(localDb);
                var posmContext = CreateContext<POSMSqlSugarContext>(posmDb);
                var productCosts = rawRows
                    .Where(row => !string.IsNullOrWhiteSpace(row.ProductCode))
                    .Select(row => new ProductCostRow
                    {
                        ProductCode = row.ProductCode,
                        PurchasePrice = 99m,
                    })
                    .ToList();
                var input = new ProductStoreDailyRefreshInput(
                    date,
                    rawRows,
                    new HashSet<ProductStoreDailySourceRow>(),
                    new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Array.Empty<StoreCostRow>(),
                    productCosts,
                    Array.Empty<WarehouseCostRow>(),
                    null);
                var build = new SalesStatisticsProductStoreDailyBuilder().Build(input);

                return await new SalesStatisticsProductStoreDailyCommandWriter().PersistAsync(
                    context,
                    posmContext,
                    NullLogger.Instance,
                    input,
                    build,
                    atomicStoreStatistics: null,
                    sourceWatermarkOverride: null,
                    validateSourceWatermarkBeforeCommitAsync: null,
                    atomicSuccessStatusOverride: null);
            }
            finally
            {
                localDb.Dispose();
                posmDb.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (string.IsNullOrWhiteSpace(_databaseConnection))
                return;
            await using var admin = new SqlConnection(new SqlConnectionStringBuilder(_adminConnection)
            {
                InitialCatalog = "master",
            }.ConnectionString);
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]";
            await command.ExecuteNonQueryAsync();
        }

        private static T CreateContext<T>(ISqlSugarClient db)
        {
            var context = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            typeof(T).GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(context, db);
            return context;
        }
    }
}
