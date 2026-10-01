using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Features.WarehousePicking;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class WarehousePickingSqlServerFactAttribute : FactAttribute
{
    public WarehousePickingSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LocalSupplierCategorySqlServerFactAttribute.ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {LocalSupplierCategorySqlServerFactAttribute.ConnectionEnvironmentVariable}，跳过真实 SQL Server 拣货集成测试。";
        }
    }
}

/// <summary>
/// 真实 SQL Server 上验证拣货迁移与会话行更新锁：多人并发写入时“行合计 = 记录之和”不丢更新，
/// 按旧合计改总数只有一个成功，并发加入只建一个会话。每个用例使用独立数据库。
/// </summary>
[Trait("Category", "SQL")]
public sealed class WarehousePickingSqlServerIntegrationTests
{
    private const string OrderGuid = "order-sql-1";

    [WarehousePickingSqlServerFact]
    public async Task SQLServer_迁移可重复执行_校验通过_权限码只入库一次()
    {
        await using var database = await IsolatedDatabase.CreateAsync();

        await database.ExecuteAsync(WarehouseOrderPickingSchema.ApplySql);
        await database.ExecuteAsync(WarehouseOrderPickingSchema.VerifySql);
        await database.ExecuteAsync(WarehouseOrderPickStockoutSchema.ApplySql);
        await database.ExecuteAsync(WarehouseOrderPickStockoutSchema.ApplySql);
        await database.ExecuteAsync(WarehouseOrderPickStockoutSchema.VerifySql);

        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.HbwebSysPermissions WHERE Code = N'Warehouse.Picking'"));
        Assert.Equal(
            1,
            await database.ScalarAsync(
                "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.WarehouseOrderPickRecord') AND name = N'UX_WarehouseOrderPickRecord_ClientRequestId' AND is_unique = 1"
            )
        );
    }

    [WarehousePickingSqlServerFact]
    public async Task SQLServer_多人并发扫码_行合计等于记录之和且不丢更新()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await SeedOrderAsync(database, minOrderQuantity: 2);
        await CreateService(database).JoinAsync(OrderGuid, Picker("u-1"));

        // 4 个拣货人共 40 次并发扫码，每次 +2。每次请求各用独立连接（与生产每个请求一个作用域一致；
        // SqlSugarClient 本身不是线程安全的，不能让并发请求共用一个客户端）。
        var tasks = Enumerable.Range(0, 40).Select(index =>
            CreateService(database).AppendRecordAsync(OrderGuid, Scan(), Picker($"u-{index % 4 + 1}"))
        );
        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.True(result.Success, result.Message));
        using var db = database.CreateClient();
        Assert.Equal(40, await db.Queryable<WarehouseOrderPickRecord>().CountAsync());
        var progress = await CreateService(database).GetProgressAsync(OrderGuid);
        Assert.Equal(80, progress.Data!.Lines.Single().PickedTotal);
        Assert.Equal(4, progress.Data.Participants.Count);
    }

    [WarehousePickingSqlServerFact]
    public async Task SQLServer_货位没货_标记覆盖原因_又拣到货自动失效_撤销幂等()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await SeedOrderAsync(database, minOrderQuantity: 2);
        await CreateService(database).JoinAsync(OrderGuid, Picker("u-1"));
        await CreateService(database).AppendRecordAsync(OrderGuid, Scan(), Picker("u-1"));

        var marked = await CreateService(database).MarkStockoutAsync(OrderGuid, "d-1", WarehouseOrderPickStockoutReasons.LocationEmpty, Picker("u-1"));
        var remarked = await CreateService(database).MarkStockoutAsync(OrderGuid, "d-1", WarehouseOrderPickStockoutReasons.Damaged, Picker("u-2"));

        Assert.True(marked.Success, marked.Message);
        Assert.Equal(WarehouseOrderPickStockoutReasons.Damaged, remarked.Data!.Line.Stockout!.Reason);
        Assert.Equal(2, remarked.Data.Line.Stockout.PickedAtMark);
        var progress = await CreateService(database).GetProgressAsync(OrderGuid);
        Assert.NotNull(progress.Data!.Lines.Single().Stockout);

        var scanned = await CreateService(database).AppendRecordAsync(OrderGuid, Scan(), Picker("u-3"));
        Assert.Null(scanned.Data!.Line.Stockout);

        var cleared = await CreateService(database).ClearStockoutAsync(OrderGuid, "d-1", Picker("u-1"));
        Assert.True(cleared.Success, cleared.Message);
        using var db = database.CreateClient();
        var row = await db.Queryable<WarehouseOrderPickStockout>().SingleAsync();
        Assert.NotNull(row.ClearedAtUtc);
        // 自动失效时记下又拣到货的人；之后的撤销没有有效标记，不覆盖。
        Assert.Equal("Picker u-3", row.ClearedByName);
    }

    [WarehousePickingSqlServerFact]
    public async Task SQLServer_按同一旧合计并发改总数只有一个成功_其余返回最新合计()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await SeedOrderAsync(database, minOrderQuantity: 2);
        await CreateService(database).JoinAsync(OrderGuid, Picker("u-1"));

        var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(index =>
            CreateService(database).SetLineTotalAsync(
                OrderGuid,
                "d-1",
                new WarehousePickingSetTotalRequestDto { Total = 10 + index, ExpectedTotal = 0, ClientRequestId = Guid.NewGuid() },
                Picker($"u-{index}")
            )
        ));

        var succeeded = Assert.Single(results, result => result.Success);
        var winner = succeeded.Data!.Line.PickedTotal;
        Assert.All(results.Where(result => !result.Success), result =>
        {
            Assert.Equal(WarehousePickingErrorCodes.PickedTotalChanged, result.ErrorCode);
            Assert.Equal(winner, result.Data!.Line.PickedTotal);
        });
    }

    [WarehousePickingSqlServerFact]
    public async Task SQLServer_并发加入只建一个会话_订单只转一次配货中_提交后拒绝写入()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        await SeedOrderAsync(database, minOrderQuantity: 2);

        var joins = await Task.WhenAll(Enumerable.Range(1, 8).Select(index =>
            CreateService(database).JoinAsync(OrderGuid, Picker($"u-{index}"))
        ));
        await CreateService(database).AppendRecordAsync(OrderGuid, Scan(), Picker("u-1"));
        var submitted = await CreateService(database).SubmitAsync(OrderGuid, Picker("u-1"));
        var late = await CreateService(database).AppendRecordAsync(OrderGuid, Scan(), Picker("u-2"));

        Assert.All(joins, result => Assert.True(result.Success, result.Message));
        using var db = database.CreateClient();
        Assert.Equal(1, await db.Queryable<WarehouseOrderPickSession>().CountAsync());
        Assert.Equal(8, await db.Queryable<WarehouseOrderPickParticipant>().CountAsync());
        Assert.Equal(3, (await db.Queryable<WareHouseOrder>().SingleAsync()).FlowStatus);
        Assert.True(submitted.Success, submitted.Message);
        Assert.Equal(2m, (await db.Queryable<WareHouseOrderDetails>().SingleAsync()).AllocQuantity);
        Assert.Equal(WarehousePickingErrorCodes.SessionSubmitted, late.ErrorCode);
    }

    private static WarehousePickerContext Picker(string userGuid) => new(userGuid, $"Picker {userGuid}", false, userGuid, null);

    private static WarehousePickingRecordRequestDto Scan() => new()
    {
        DetailGuid = "d-1",
        Source = WarehouseOrderPickSources.Scan,
        ScannedCode = "9312345678905",
        MatchedBy = WarehouseOrderPickMatchKinds.Barcode,
        ClientRequestId = Guid.NewGuid(),
    };

    private static async Task SeedOrderAsync(IsolatedDatabase database, int minOrderQuantity)
    {
        using var db = database.CreateClient();
        await db.Insertable(new WareHouseOrder { OrderGUID = OrderGuid, OrderNo = "2026-0418", StoreCode = "1013", FlowStatus = 1 }).ExecuteCommandAsync();
        await db.Insertable(new WareHouseOrderDetails
        {
            DetailGUID = "d-1",
            OrderGUID = OrderGuid,
            StoreCode = "1013",
            ProductCode = "P-1",
            Quantity = 36,
            OEMPrice = 1m,
            ImportPrice = 1m,
        }).ExecuteCommandAsync();
        await db.Insertable(new Product { ProductCode = "P-1", ItemNumber = "HB1", Barcode = "9312345678905", ProductName = "Cup" }).ExecuteCommandAsync();
        await db.Insertable(new WarehouseProduct { ProductCode = "P-1", MinOrderQuantity = minOrderQuantity, IsActive = true }).ExecuteCommandAsync();
    }

    private static WarehousePickingService CreateService(IsolatedDatabase database) =>
        new(CreateContext(database.CreateClient()), Mock.Of<IProductWarehouseReactService>(), NullLogger<WarehousePickingService>.Instance);

    private static SqlSugarContext CreateContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, db);
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
            var databaseName = $"hb_pick_{Guid.NewGuid():N}";
            await ExecuteOnAsync(server, $"CREATE DATABASE [{databaseName}];");
            var builder = new SqlConnectionStringBuilder(server) { InitialCatalog = databaseName };
            var database = new IsolatedDatabase(server, databaseName, builder.ConnectionString);

            // 与生产同名同结构的最小权限表，验证迁移里的权限码幂等入库。
            await database.ExecuteAsync("""
CREATE TABLE dbo.HbwebSysPermissions (
    Id nvarchar(36) NOT NULL PRIMARY KEY, Code nvarchar(100) NOT NULL, Name nvarchar(100) NULL,
    Category nvarchar(100) NULL, Description nvarchar(500) NULL, CreatedAt datetime2 NOT NULL,
    CreatedBy nvarchar(100) NULL, UpdatedAt datetime2 NULL, UpdatedBy nvarchar(100) NULL, IsDeleted bit NOT NULL
);
""");
            await database.ExecuteAsync(WarehouseOrderPickingSchema.ApplySql);
            await database.ExecuteAsync(WarehouseOrderPickingSchema.VerifySql);
            await database.ExecuteAsync(WarehouseOrderPickStockoutSchema.ApplySql);
            await database.ExecuteAsync(WarehouseOrderPickStockoutSchema.VerifySql);
            using var db = database.CreateClient();
            db.CodeFirst.InitTables(
                typeof(WareHouseOrder),
                typeof(WareHouseOrderDetails),
                typeof(Product),
                typeof(WarehouseProduct),
                typeof(Store),
                typeof(ProductSetCode),
                typeof(StoreMultiCodeProduct),
                typeof(ProductLocation),
                typeof(Location)
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

        public Task ExecuteAsync(string sql) => ExecuteOnAsync(ConnectionString, sql);

        public async Task<int> ScalarAsync(string sql)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await ExecuteOnAsync(
                _serverConnectionString,
                $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];"
            );
        }

        private static async Task ExecuteOnAsync(string connectionString, string sql)
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
