using BlazorApp.Shared.Models;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

public sealed class SpecialProductsLiveSqlServerFactAttribute : FactAttribute
{
    public SpecialProductsLiveSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DailyCloseSqlServerFactAttribute.ConnectionVariable)))
        {
            Skip = "未配置隔离 SQL Server，跳过特殊商品实时查询真实 SQL 测试。";
        }
    }
}

/// <summary>
/// 真实 SQL Server 上验证特殊商品实时查询：生产用 SQL Server + SqlSugar，SQL 生成与 SQLite 不同
/// （全局 NOLOCK 表提示、SNAPSHOT 事务、IN 参数上限）。每个测试类实例自建独立数据库，结束时删除。
/// 连接串取自环境变量 HB_TEST_SQLSERVER_CONNECTION（本机可用 LocalDB）。
/// </summary>
[Trait("Category", "SQL")]
public sealed class CatalogSpecialProductsLiveSqlServerIntegrationTests : IAsyncLifetime
{
    private string? masterConnection;
    private string connection = string.Empty;
    private string database = string.Empty;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(DailyCloseSqlServerFactAttribute.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        database = $"HbSpecialLive_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        masterConnection = builder.ConnectionString;
        await ExecuteAtAsync(masterConnection, $"CREATE DATABASE [{database}]");
        // 目录快照读取要求库开启快照隔离，与生产一致；实时查询同样放在 SNAPSHOT 事务里。
        await ExecuteAtAsync(masterConnection, $"ALTER DATABASE [{database}] SET ALLOW_SNAPSHOT_ISOLATION ON");
        builder.InitialCatalog = database;
        connection = builder.ConnectionString;

        var db = CreateContext().MainDb;
        db.CodeFirst.InitTables<Store, Product, StoreRetailPrice>();
        db.CodeFirst.InitTables<StoreMultiCodeProduct, StoreClearancePrice, ProductSetCode>();
        await SpecialProductsLiveTestData.SeedRichCatalogAsync(db);
    }

    public async Task DisposeAsync()
    {
        if (masterConnection is null)
        {
            return;
        }

        SqlConnection.ClearAllPools();
        await ExecuteAtAsync(
            masterConnection,
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
    }

    [SpecialProductsLiveSqlServerFact]
    public async Task Live_pages_match_artifact_pages_on_real_sql_server()
    {
        foreach (var storeCode in new[] { "S01", "S02" })
        {
            foreach (var pageSize in new[] { 1, 3, 5000 })
            {
                var service = CreateService(CreateContext());
                var pages = await CatalogSpecialProductsLiveAssertions.AssertLivePagesMatchArtifactAsync(
                    service,
                    storeCode,
                    pageSize);
                Assert.NotEmpty(pages);
            }
        }

        var s01 = await CreateService(CreateContext()).GetSpecialProductsPageAsync("S01", null, 5000, CancellationToken.None);
        Assert.Equal(15, s01!.TotalCount);
        Assert.Null(await CreateService(CreateContext()).GetSpecialProductsPageAsync("S03", null, 500, CancellationToken.None));
        Assert.Null(await CreateService(CreateContext()).GetSpecialProductsPageAsync("S04", null, 500, CancellationToken.None));
        Assert.Null(await CreateService(CreateContext()).GetSpecialProductsPageAsync("S99", null, 500, CancellationToken.None));
    }

    [SpecialProductsLiveSqlServerFact]
    public async Task Live_query_issues_no_nolock_hints_even_though_the_connection_enables_them_globally()
    {
        var context = CreateContext();
        var statements = new List<string>();
        context.MainDb.Aop.OnLogExecuting = (sql, _) =>
        {
            lock (statements)
            {
                statements.Add(sql);
            }
        };

        // 对照：普通查询会带上全局 NOLOCK，说明抓取与全局配置都有效。
        await context.MainDb.Queryable<Store>().ToListAsync();
        Assert.Contains(statements, sql => sql.Contains("NOLOCK", StringComparison.OrdinalIgnoreCase));
        statements.Clear();

        var page = await CreateService(context).GetSpecialProductsPageAsync("S01", null, 5000, CancellationToken.None);

        Assert.NotNull(page);
        Assert.NotEmpty(statements);
        Assert.All(statements, sql => Assert.DoesNotContain("NOLOCK", sql, StringComparison.OrdinalIgnoreCase));
    }

    [SpecialProductsLiveSqlServerFact]
    public async Task Live_query_neither_blocks_on_nor_dirty_reads_an_uncommitted_admin_change()
    {
        // 模拟后台管理系统：在另一个连接里把 SP01 的特殊标记改掉，事务未提交，行上持有排他锁。
        await using var admin = new SqlConnection(connection);
        await admin.OpenAsync();
        await using var transaction = (SqlTransaction)await admin.BeginTransactionAsync();
        await using (var update = new SqlCommand(
            "UPDATE [StoreRetailPrice] SET [IsSpecialProduct] = 0 WHERE [StoreCode] = 'S01' AND [ProductCode] = 'SP01'",
            admin,
            transaction))
        {
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        try
        {
            // 对照 1：连接级全局 NOLOCK 的普通读取会读到这条未提交的修改（脏读）——这正是实时查询必须去掉 NOLOCK 的原因。
            var dirtyRead = await CreateContext().MainDb.Queryable<StoreRetailPrice>()
                .Where(x => x.StoreCode == "S01" && x.ProductCode == "SP01")
                .FirstAsync();
            Assert.False(dirtyRead.IsSpecialProduct);

            // 对照 2：显式 READCOMMITTED 在库未开启 READ_COMMITTED_SNAPSHOT 时会被这条排他锁挡住，命令超时
            // （说明实时查询不用 READCOMMITTED、而用不加锁的 SNAPSHOT）。库已开启行版本读时它不会阻塞，此时跳过该对照。
            var readCommittedSnapshotOn = await ExecuteScalarAsync(
                connection,
                "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()");
            if (!readCommittedSnapshotOn)
            {
                var blockedContext = CreateContext();
                blockedContext.MainDb.Ado.CommandTimeOut = 3;
                await Assert.ThrowsAnyAsync<Exception>(() => blockedContext.MainDb.Queryable<StoreRetailPrice>()
                    .With("WITH(READCOMMITTED)")
                    .Where(x => x.StoreCode == "S01" && x.ProductCode == "SP01")
                    .FirstAsync());
            }

            // 实时查询：SNAPSHOT 读已提交的版本——既不阻塞，也读不到未提交的修改。
            var livePageTask = CreateService(CreateContext()).GetSpecialProductsPageAsync("S01", null, 5000, CancellationToken.None);
            var finished = await Task.WhenAny(livePageTask, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.Same(livePageTask, finished);
            var livePage = await livePageTask;
            Assert.Contains(livePage!.Items, x => x.ProductCode == "SP01");
            Assert.Equal(15, livePage.TotalCount);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [SpecialProductsLiveSqlServerFact]
    public async Task Live_query_sees_direct_sql_changes_while_the_cached_artifact_stays_stale()
    {
        var cache = new CatalogIndexCache();
        var service = CreateService(CreateContext(), cache);
        var warm = await service.GetSellableItemsPageAsync("S01", null, null, 5000, CancellationToken.None);
        Assert.NotNull(warm);
        Assert.Contains(warm!.Items, x => x.ProductCode == "SP03" && x.IsSpecialProduct);

        // 不经过任何 InvalidateStore，直接改库：SP03 取消特殊、SP20 在 S01 改为特殊并调价。
        await ExecuteAtAsync(
            connection,
            "UPDATE [StoreRetailPrice] SET [IsSpecialProduct] = 0 WHERE [StoreCode] = 'S01' AND [ProductCode] = 'SP03';" +
            "UPDATE [StoreRetailPrice] SET [IsSpecialProduct] = 1, [StoreRetailPriceValue] = 12.34, [UpdatedAt] = SYSUTCDATETIME() " +
            "WHERE [StoreCode] = 'S01' AND [ProductCode] = 'SP20';");

        var live = await service.GetSpecialProductsPageAsync("S01", null, 5000, CancellationToken.None);
        Assert.DoesNotContain(live!.Items, x => x.ProductCode == "SP03");
        var sp20 = Assert.Single(live.Items, x => x.ProductCode == "SP20");
        Assert.Equal(12.34m, sp20.RetailPrice);

        // 对照：目录分页仍是旧缓存。
        var cached = await service.GetSellableItemsPageAsync("S01", null, null, 5000, CancellationToken.None);
        Assert.Contains(cached!.Items, x => x.ProductCode == "SP03" && x.IsSpecialProduct);
        Assert.DoesNotContain(cached.Items, x => x.ProductCode == "SP20" && x.IsSpecialProduct);
    }

    [SpecialProductsLiveSqlServerFact]
    public async Task Large_special_set_is_chunked_below_the_sql_parameter_limit_and_matches_the_artifact()
    {
        var db = CreateContext().MainDb;
        const int count = 2_600;
        var products = new List<Product>(count);
        var prices = new List<StoreRetailPrice>(count);
        for (var i = 0; i < count; i++)
        {
            products.Add(new Product
            {
                UUID = $"PU-BULK{i:D4}",
                ProductCode = $"BULK{i:D4}",
                ProductName = $"Bulk {i}",
                Barcode = $"BULKBAR-{i:D4}",
                ItemNumber = $"BULKITEM-{i:D4}",
                RetailPrice = 10m + i,
                CreatedAt = SpecialProductsLiveTestData.At(0),
                UpdatedAt = SpecialProductsLiveTestData.At(1)
            });
            prices.Add(new StoreRetailPrice
            {
                UUID = $"SRP-BULK{i:D4}",
                StoreCode = "S01",
                ProductCode = $"BULK{i:D4}",
                StoreRetailPriceValue = 5m + i,
                IsSpecialProduct = i % 9 != 0,
                CreatedAt = SpecialProductsLiveTestData.At(0),
                UpdatedAt = SpecialProductsLiveTestData.At(10)
            });
        }

        await db.Insertable(products).PageSize(100).ExecuteCommandAsync();
        await db.Insertable(prices).PageSize(100).ExecuteCommandAsync();
        for (var i = 0; i < count; i += 50)
        {
            await SpecialProductsLiveTestData.AddClearanceAsync(db, "S01", "OTHER", $"BULKBAR-{i:D4}", 1m, 12);
            await SpecialProductsLiveTestData.AddProductAsync(db, $"RIVAL{i:D4}", $"BULKBAR-{i + 1:D4}", null, 1m, 1);
            await SpecialProductsLiveTestData.AddStorePriceAsync(db, "S01", $"RIVAL{i:D4}", 2m, 30, special: false);
        }

        var service = CreateService(CreateContext());
        var pages = await CatalogSpecialProductsLiveAssertions.AssertLivePagesMatchArtifactAsync(service, "S01", 1_000);

        Assert.True(pages.Count >= 3);
        Assert.True(pages[0].TotalCount > 2_000);
    }

    private static CatalogService CreateService(HbposSqlSugarContext context, ICatalogIndexCache? cache = null) =>
        new(context, new PriceIndexBuilder(), cache ?? new CatalogIndexCache(), new CatalogBaseDataCache());

    // 与生产相同的上下文：MARS 连接 + 全局 IsWithNoLockQuery。
    private HbposSqlSugarContext CreateContext()
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:MainConnection"] = connection,
            ["ConnectionStrings:PosmConnection"] = connection,
            ["Database:CommandTimeoutSeconds"] = "60",
        };

        return new HbposSqlSugarContext(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            NullLogger<HbposSqlSugarContext>.Instance);
    }

    private static async Task<bool> ExecuteScalarAsync(string connectionString, string sql)
    {
        await using var sqlConnection = new SqlConnection(connectionString);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection) { CommandTimeout = 60 };
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAtAsync(string connectionString, string sql)
    {
        await using var sqlConnection = new SqlConnection(connectionString);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }
}
