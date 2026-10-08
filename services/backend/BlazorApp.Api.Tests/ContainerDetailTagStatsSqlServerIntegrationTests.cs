using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ContainerDetailTagStatsSqlServerFactAttribute : FactAttribute
{
    public const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public ContainerDetailTagStatsSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过真实 SQL Server 货柜明细标签统计集成测试。";
        }
    }
}

/// <summary>
/// 货柜明细标签统计必须在真实 SQL Server 上执行：SQLite 允许在聚合函数里嵌子查询，
/// SQL Server 会报错 130（不能对包含聚合或子查询的表达式执行聚合函数），单元测试测不出来。
/// 每个用例创建 GUID 命名的独立数据库并在结束时删除。
/// </summary>
[Trait("Category", "SQL")]
public sealed class ContainerDetailTagStatsSqlServerIntegrationTests
{
    private const string ContainerCode = "C-OWN";

    [ContainerDetailTagStatsSqlServerFact]
    public async Task SQLServer_分页统计请求应按本柜新品口径返回标签统计()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        await SeedOwnContainerScenarioAsync(db);
        using var hbSalesDb = database.CreateScope();
        var service = CreateService(db, hbSalesDb);

        // 与前端分页模式的统计请求完全一致：只要总数和统计，不要明细行。
        var stats = await service.QueryContainerDetailsAsync(new ContainerDetailQueryDto
        {
            ContainerGuid = ContainerCode,
            PageNumber = 1,
            PageSize = 100,
            IncludeItems = false,
            IncludeTotal = true,
            IncludeStats = true,
        });

        Assert.True(stats.StatsComputed);
        Assert.Equal(5, stats.ItemsTotal);
        Assert.Equal(5, stats.TagStats.All);
        Assert.Equal(3, stats.TagStats.New);
        Assert.Equal(2, stats.TagStats.Existing);
        Assert.Equal(1, stats.TagStats.NoOemPrice);
        Assert.Equal(1, stats.TagStats.AbnormalImport);
        Assert.Equal(3, stats.TagStats.Active);
        // 其它货柜建档但已下架的 1 条 + 仓库未到货（无仓库记录）的 1 条。
        Assert.Equal(2, stats.TagStats.Inactive);
        Assert.Equal(5, stats.TagStats.Normal);
    }

    [ContainerDetailTagStatsSqlServerFact]
    public async Task SQLServer_无仓库记录的新品应计入下架统计与下架筛选总数()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        await SeedOwnContainerScenarioAsync(db);
        using var hbSalesDb = database.CreateScope();
        var service = CreateService(db, hbSalesDb);

        var result = await service.QueryContainerDetailsAsync(new ContainerDetailQueryDto
        {
            ContainerGuid = ContainerCode,
            PageNumber = 1,
            PageSize = 100,
            IncludeItems = false,
            IncludeTotal = true,
            IncludeStats = true,
            SelectedTags = new List<string> { "inactive" },
        });

        Assert.Equal(2, result.TagStats.Inactive);
        Assert.Equal(2, result.ItemsTotal);
    }

    [ContainerDetailTagStatsSqlServerFact]
    public async Task SQLServer_选中标签时统计仍按全量范围且总数按筛选结果()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        await SeedOwnContainerScenarioAsync(db);
        using var hbSalesDb = database.CreateScope();
        var service = CreateService(db, hbSalesDb);

        // 前端分页模式点选标签后的统计请求：分页总数也只来自这条请求。
        var result = await service.QueryContainerDetailsAsync(new ContainerDetailQueryDto
        {
            ContainerGuid = ContainerCode,
            PageNumber = 1,
            PageSize = 100,
            IncludeItems = false,
            IncludeTotal = true,
            IncludeStats = true,
            SelectedTags = new List<string> { "new" },
        });

        // 标签统计排除 SelectedTags，保证各标签计数不随当前选中标签变化；总数是筛选后的行数。
        Assert.Equal(5, result.TagStats.All);
        Assert.Equal(3, result.TagStats.New);
        Assert.Equal(2, result.TagStats.Existing);
        Assert.Equal(3, result.ItemsTotal);
        Assert.False(result.HasMore);
    }

    [ContainerDetailTagStatsSqlServerFact]
    public async Task SQLServer_进口价对比实时进货价的涨跌统计与筛选()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        await SeedOwnContainerScenarioAsync(db);
        await SeedDetailAsync(db, "D-TREND-UP", "P-TREND-UP", "HB401", importPrice: 1.37m, warehouseImportPrice: 1.19m);
        await SeedDetailAsync(db, "D-TREND-DOWN", "P-TREND-DOWN", "HB402", importPrice: 1.66m, warehouseImportPrice: 1.96m);
        using var hbSalesDb = database.CreateScope();
        var service = CreateService(db, hbSalesDb);

        // 分页模式的统计请求：统计不受选中标签影响，总数按涨价筛选结果（明细行筛选由 SQLite 用例覆盖）。
        var result = await service.QueryContainerDetailsAsync(new ContainerDetailQueryDto
        {
            ContainerGuid = ContainerCode,
            PageNumber = 1,
            PageSize = 100,
            IncludeItems = false,
            IncludeTotal = true,
            IncludeStats = true,
            SelectedTags = new List<string> { "priceUp" },
        });

        // 场景里其余 5 条进口价与仓库进货价相同或缺失，不计涨跌。
        Assert.Equal(1, result.TagStats.PriceUp);
        Assert.Equal(1, result.TagStats.PriceDown);
        Assert.Equal(1, result.ItemsTotal);
    }

    /// <summary>
    /// 同一货柜内的四种新旧口径：本柜建档、未建档、其它货柜建档、本柜只更新；
    /// 另有一条仓库未到货（无仓库商品记录）的新品。
    /// </summary>
    private static async Task SeedOwnContainerScenarioAsync(ISqlSugarClient db)
    {
        await db.Insertable(new Container
        {
            ContainerCode = ContainerCode,
            ContainerNumber = "TGBU5893494",
            LoadingDate = new DateTime(2026, 5, 12),
            EstimatedArrivalDate = new DateTime(2026, 6, 2),
            ExchangeRate = 4.5m,
            TotalVolume = 69.868m,
            Status = 1,
        }).ExecuteCommandAsync();

        await SeedDetailAsync(db, "D-OWN-CREATED", "P-OWN-CREATED", "HB301", oemPrice: 0m, localExists: true);
        await SeedHistoryAsync(db, "P-OWN-CREATED", "Create", ContainerCode);
        await SeedDetailAsync(db, "D-OWN-PENDING", "P-OWN-PENDING", "HB302", oemPrice: 0m, localExists: false, importPrice: null);
        await SeedDetailAsync(db, "D-OWN-OTHER", "P-OWN-OTHER", "HB303", localExists: true, isActive: false);
        await SeedHistoryAsync(db, "P-OWN-OTHER", "Create", "C-EARLIER");
        await SeedDetailAsync(db, "D-OWN-UPDATED", "P-OWN-UPDATED", "HB304", localExists: true);
        await SeedHistoryAsync(db, "P-OWN-UPDATED", "BatchUpdate", ContainerCode);
        await SeedDetailAsync(db, "D-OWN-ARRIVING", "P-OWN-ARRIVING", "HB305", localExists: false, warehouseExists: false);
    }

    private static async Task SeedDetailAsync(
        ISqlSugarClient db,
        string detailCode,
        string productCode,
        string itemNumber,
        decimal? oemPrice = 1m,
        decimal? importPrice = 1m,
        bool localExists = true,
        bool isActive = true,
        bool warehouseExists = true,
        decimal? warehouseImportPrice = null
    )
    {
        await db.Insertable(new ContainerDetail
        {
            DetailCode = detailCode,
            ContainerCode = ContainerCode,
            ProductCode = productCode,
            ProductType = "普通商品",
            LoadingPieces = 1m,
            LoadingQuantity = 10m,
            DomesticPrice = 8m,
            AdjustmentRate = 1.1m,
            ImportPrice = importPrice,
            OEMPrice = oemPrice,
            IsDeleted = false,
        }).ExecuteCommandAsync();

        await db.Insertable(new DomesticProduct
        {
            ProductCode = productCode,
            HBProductNo = itemNumber,
            Barcode = $"9300000000{itemNumber}",
            ProductName = $"商品 {itemNumber}",
            ProductType = 0,
            OEMPrice = oemPrice,
            IsDeleted = false,
        }).ExecuteCommandAsync();

        if (warehouseExists)
        {
            await db.Insertable(new WarehouseProduct
            {
                ProductCode = productCode,
                ImportPrice = warehouseImportPrice ?? importPrice,
                OEMPrice = oemPrice,
                IsActive = isActive,
            }).ExecuteCommandAsync();
        }

        if (localExists)
        {
            await db.Insertable(new Product
            {
                UUID = $"LOCAL-{productCode}",
                ProductCode = productCode,
                ProductName = $"本地商品 {itemNumber}",
                IsActive = isActive,
            }).ExecuteCommandAsync();
        }
    }

    private static async Task SeedHistoryAsync(ISqlSugarClient db, string productCode, string action, string sourceReference)
    {
        await db.Insertable(new WarehouseProductChangeHistory
        {
            ProductCode = productCode,
            Action = action,
            Source = "ContainerSubmit",
            SourceReference = sourceReference,
            ActorName = "测试",
            ActorType = "User",
        }).ExecuteCommandAsync();
    }

    private static ContainerReactService CreateService(ISqlSugarClient db, SqlSugarScope hbSalesDb) =>
        new(
            WrapContext<SqlSugarContext>(db),
            WrapContext<HqSqlSugarContext>(new Mock<ISqlSugarClient>().Object),
            // HBSales 只用于推送，查询路径不会访问；字段类型是 SqlSugarScope，不能用 Mock 替代。
            WrapContext<HBSalesSqlSugarContext>(hbSalesDb),
            new ConfigurationBuilder().Build(),
            Mock.Of<IMapper>(),
            NullLogger<ContainerReactService>.Instance,
            Mock.Of<IContainerHqSyncService>(),
            Mock.Of<ITranslationService>(),
            Mock.Of<IWarehouseProductChangeHistoryService>(),
            Mock.Of<ICurrentUserService>()
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
            var server = Environment.GetEnvironmentVariable(ContainerDetailTagStatsSqlServerFactAttribute.ConnectionEnvironmentVariable)!;
            var databaseName = $"hb_cdts_{Guid.NewGuid():N}";
            await ExecuteAsync(server, $"CREATE DATABASE [{databaseName}] COLLATE Chinese_PRC_90_CI_AS;");
            var builder = new SqlConnectionStringBuilder(server) { InitialCatalog = databaseName };
            var database = new IsolatedDatabase(server, databaseName, builder.ConnectionString);

            using var db = database.CreateClient();
            db.CodeFirst.InitTables(
                typeof(Container),
                typeof(ContainerDetail),
                typeof(DomesticProduct),
                typeof(DomesticSetProduct),
                typeof(WarehouseProduct),
                typeof(Product),
                typeof(WarehouseCategory),
                typeof(StoreRetailPrice),
                typeof(ProductSetCode),
                typeof(WarehouseProductChangeHistory)
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

        public SqlSugarScope CreateScope() =>
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
