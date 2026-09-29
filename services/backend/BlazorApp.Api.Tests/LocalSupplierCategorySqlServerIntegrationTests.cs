using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services.LocalSupplierCategories;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class LocalSupplierCategorySqlServerFactAttribute : FactAttribute
{
    public const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public LocalSupplierCategorySqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过真实 SQL Server 供应商分类集成测试。";
        }
    }
}

/// <summary>
/// 真实 SQL Server 上验证迁移建出的三表、事务级应用锁与商品列表的 EXISTS 子查询筛选。
/// 每个用例创建 GUID 命名的独立数据库并在结束时删除。
/// </summary>
[Trait("Category", "SQL")]
public sealed class LocalSupplierCategorySqlServerIntegrationTests
{
    private const string Dats = "240";

    [LocalSupplierCategorySqlServerFact]
    public async Task SQLServer_采集幂等_归类并支持列表子树筛选()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        await SeedProductAsync(db, "P-1", "69798", Dats);
        await SeedProductAsync(db, "P-2", "11111", Dats);
        var capture = CreateCaptureService(db);

        var first = await capture.CaptureAsync(BuildCapture("69798", "72264"), "tester");
        var second = await capture.CaptureAsync(BuildCapture("69798", "72264"), "tester");

        Assert.Equal(2, first.CategoriesCreated);
        Assert.Equal(1, first.AssignedProducts);
        Assert.Equal(0, second.CategoriesCreated);
        Assert.Equal(1, second.UnchangedProducts);
        Assert.Equal(2, await db.Queryable<LocalSupplierCategoryCapture>().Where(item => item.SeenCount == 2).CountAsync());

        var rootGuid = (await db.Queryable<LocalSupplierCategory>().SingleAsync(item => item.Depth == 0)).CategoryGUID;
        var products = CreateProductService(db);
        var byRoot = await products.GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            SupplierCategoryGUIDs = new List<string> { rootGuid },
        });
        Assert.Equal(new[] { "P-1" }, byRoot.Items.Select(item => item.ProductCode));
        Assert.Equal("Office Stationery > Adhesives and Tape", byRoot.Items[0].SupplierCategoryPath);

        var unassigned = await products.GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            SupplierCategoryUnassignedOnly = true,
        });
        Assert.Equal(new[] { "P-2" }, unassigned.Items.Select(item => item.ProductCode));

        var missing = await products.GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            SupplierCategoryGUIDs = new List<string> { "missing" },
        });
        Assert.Empty(missing.Items);
    }

    [LocalSupplierCategorySqlServerFact]
    public async Task SQLServer_同供应商并发采集由应用锁串行且不产生重复分类()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using (var seedDb = database.CreateClient())
        {
            await SeedProductAsync(seedDb, "P-1", "69798", Dats);
        }

        var tasks = Enumerable.Range(0, 6)
            .Select(index => Task.Run(async () =>
            {
                using var db = database.CreateClient();
                return await CreateCaptureService(db).CaptureAsync(BuildCapture("69798", $"ITEM-{index}"), $"tester-{index}");
            }))
            .ToList();
        var results = await Task.WhenAll(tasks);

        using var verifyDb = database.CreateClient();
        Assert.Equal(2, await verifyDb.Queryable<LocalSupplierCategory>().CountAsync());
        Assert.Equal(2, results.Sum(result => result.CategoriesCreated));
        var shared = await verifyDb.Queryable<LocalSupplierCategoryCapture>().SingleAsync(item => item.ItemNumber == "69798");
        Assert.Equal(6, shared.SeenCount);
        Assert.Equal(1, await verifyDb.Queryable<LocalSupplierCategoryProductAssignment>().CountAsync());
    }

    [LocalSupplierCategorySqlServerFact]
    public async Task SQLServer_每晚重新归类在供应商锁内为新商品补归类()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        await SeedProductAsync(db, "P-1", "69798", Dats);
        await CreateCaptureService(db).CaptureAsync(BuildCapture("69798", "72264"), "tester");
        // 采集之后才由 HQ 同步写入的商品。
        await SeedProductAsync(db, "P-NEW", "72264", Dats);

        var result = await CreateReactService(db).ResolveAllSuppliersAsync("System");

        Assert.Empty(result.FailedSuppliers);
        Assert.Equal(1, result.SupplierCount);
        Assert.Equal(1, result.Assigned);
        Assert.Equal(
            2,
            await db.Queryable<LocalSupplierCategoryProductAssignment>().CountAsync()
        );
    }

    private static BrowserExtensionCategoryCaptureRequestDto BuildCapture(params string[] itemNumbers) =>
        new()
        {
            SupplierCode = Dats,
            PageUrl = "https://www.dats.com.au/office-stationery/adhesives-and-tape",
            CategoryPath = new List<BrowserExtensionCategoryPathNodeDto>
            {
                new() { Name = "Office Stationery", Key = "/office-stationery" },
                new() { Name = "Adhesives and Tape", Key = "/office-stationery/adhesives-and-tape" },
            },
            ItemNumbers = itemNumbers.ToList(),
            Mode = BrowserExtensionCategoryCaptureModes.Passive,
        };

    private static async Task SeedProductAsync(ISqlSugarClient db, string productCode, string itemNumber, string supplier)
    {
        await db.Insertable(new Product
        {
            UUID = $"product-{productCode}",
            ProductCode = productCode,
            ItemNumber = itemNumber,
            LocalSupplierCode = supplier,
            ProductName = $"商品{productCode}",
            IsActive = true,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private static LocalSupplierCategoryCaptureService CreateCaptureService(ISqlSugarClient db)
    {
        var options = new Mock<IOptionsSnapshot<BrowserExtensionOptions>>();
        options.Setup(item => item.Value).Returns(new BrowserExtensionOptions());
        return new LocalSupplierCategoryCaptureService(
            WrapContext<SqlSugarContext>(db),
            options.Object,
            NullLogger<LocalSupplierCategoryCaptureService>.Instance
        );
    }

    private static LocalSupplierCategoryReactService CreateReactService(ISqlSugarClient db)
    {
        var options = new Mock<IOptionsSnapshot<BrowserExtensionOptions>>();
        options.Setup(item => item.Value).Returns(new BrowserExtensionOptions());
        return new LocalSupplierCategoryReactService(WrapContext<SqlSugarContext>(db), options.Object);
    }

    private static ProductReactService CreateProductService(ISqlSugarClient db) =>
        new(
            WrapContext<SqlSugarContext>(db),
            WrapContext<HqSqlSugarContext>(db),
            new Mock<AutoMapper.IMapper>().Object,
            NullLogger<ProductReactService>.Instance,
            new Mock<Microsoft.AspNetCore.Http.IHttpContextAccessor>().Object,
            new ProductAuditNoopHistoryService(),
            new ProductAuditSystemCurrentUserService()
        );

    private static T WrapContext<T>(ISqlSugarClient db)
        where T : class
    {
        var context = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        typeof(T).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, db);
        return context;
    }

    private sealed class IsolatedDatabase : IAsyncDisposable
    {
        private readonly string _serverConnectionString;
        private readonly string _databaseName;

        private IsolatedDatabase(string serverConnectionString, string databaseName, string connectionString)
        {
            _serverConnectionString = serverConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<IsolatedDatabase> CreateAsync()
        {
            var server = Environment.GetEnvironmentVariable(LocalSupplierCategorySqlServerFactAttribute.ConnectionEnvironmentVariable)!;
            var databaseName = $"hb_lsc_{Guid.NewGuid():N}";
            await ExecuteAsync(server, $"CREATE DATABASE [{databaseName}];");
            var builder = new SqlConnectionStringBuilder(server) { InitialCatalog = databaseName };
            var database = new IsolatedDatabase(server, databaseName, builder.ConnectionString);

            await ExecuteAsync(database.ConnectionString, LocalSupplierCategorySchema.ApplySql);
            await ExecuteAsync(database.ConnectionString, LocalSupplierCategorySchema.VerifySql);
            using var db = database.CreateClient();
            db.CodeFirst.InitTables(
                typeof(Product),
                typeof(WarehouseCategory),
                typeof(StoreRetailPrice),
                typeof(DomesticProduct),
                typeof(ChinaSupplier)
            );
            return database;
        }

        public SqlSugarClient CreateClient() =>
            new(new ConnectionConfig
            {
                ConnectionString = ConnectionString,
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(
                _serverConnectionString,
                $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];"
            );
        }

        private static async Task ExecuteAsync(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
        }
    }
}
