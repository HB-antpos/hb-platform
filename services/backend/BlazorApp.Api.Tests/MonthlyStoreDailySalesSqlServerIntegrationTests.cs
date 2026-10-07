using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class MonthlyStoreDailySalesSqlServerFactAttribute : FactAttribute
{
    public MonthlyStoreDailySalesSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MonthlyStoreDailySalesSqlServerIntegrationTests.ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {MonthlyStoreDailySalesSqlServerIntegrationTests.ConnectionEnvironmentVariable}，跳过真实 SQL Server 月度日销售验证。";
        }
    }
}

/// <summary>
/// 在真实 SQL Server 上执行月度日销售的 POSM 支付整月聚合：SqlSugar 在 SQLite 与 SQL Server 上生成的
/// 日期分组、状态过滤、联表 SQL 方言不同，这里确认 SQL Server 方言下口径与 SQLite 单测一致。
/// 表结构按生产的 varchar / datetime / decimal(18,4) 建立，库排序规则与生产 POSM 一致。
/// </summary>
public sealed class MonthlyStoreDailySalesSqlServerIntegrationTests
{
    public const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    [MonthlyStoreDailySalesSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 整月聚合按墙钟日分组_状态过滤_退款净额_设备回填分店()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.GetMonthlyStoreDailySalesAsync("2026-09", null, false, default);

        var s1 = Assert.Single(result.Stores, store => store.BranchCode == "S1");
        // 9-1：已支付单 现金30+刷卡60+代金券10，分期付单 刷卡40；已取消单与 8-31、10-1 的订单不计入
        var day1 = s1.Days[0];
        Assert.Equal(140m, day1.Revenue);
        Assert.Equal(100m, day1.Card);
        Assert.Equal(30m, day1.Cash);
        Assert.Equal(10m, day1.Other);
        // 9-2 零点整的订单属于 9-2，不能被算进 9-1；退款支付按净额进入对应方式
        var day2 = s1.Days[1];
        Assert.Equal(30m, day2.Revenue);
        Assert.Equal(50m, day2.Card);
        Assert.Equal(-20m, day2.Cash);
        Assert.Equal(0m, day2.Other);
        // 9-1 23:59:59 的订单仍属于 9-1（上面的 140 已含它）；9-3 没有销售但已发布 → 0
        Assert.Equal(0m, s1.Days[2].Revenue);

        // S2 的订单头没有分店编码，按设备注册表回填后与统计对上
        var s2 = Assert.Single(result.Stores, store => store.BranchCode == "S2");
        Assert.Equal(80m, s2.Days[0].Revenue);
        Assert.Equal(80m, s2.Days[0].Card);
        Assert.Equal(0m, s2.Days[0].Cash);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _masterConnectionString;
        private readonly string _databaseName;
        private readonly SqlSugarClient _db;

        private Fixture(string masterConnectionString, string databaseName, string databaseConnectionString)
        {
            _masterConnectionString = masterConnectionString;
            _databaseName = databaseName;
            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = databaseConnectionString,
                DbType = SqlSugar.DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
                MoreSettings = new ConnMoreSettings { IsWithNoLockQuery = true },
            });
        }

        public static async Task<Fixture> CreateAsync()
        {
            var baseConnectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)!;
            EnsureLoopbackSqlServer(baseConnectionString);
            var databaseName = $"HbMonthlySales_{Guid.NewGuid():N}";
            var master = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = "master" }.ConnectionString;
            var database = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = databaseName }.ConnectionString;
            await ExecuteAsync(master, $"CREATE DATABASE [{databaseName}] COLLATE Chinese_PRC_90_CI_AS;");
            var fixture = new Fixture(master, databaseName, database);
            try
            {
                await ExecuteAsync(database, SchemaSql);
                fixture._db.CodeFirst.InitTables(
                    typeof(StoreSalesStatistic),
                    typeof(SalesStatisticRefreshState),
                    typeof(BlazorApp.Shared.Models.POSM.POSM_设备注册信息表));
                await fixture.SeedAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public MonthlyStoreDailySalesReactService CreateService() =>
            new(
                CreateContext<SqlSugarContext>(_db),
                CreateContext<POSMSqlSugarContext>(_db),
                new MemoryCache(new MemoryCacheOptions()))
            {
                BusinessTodayProvider = () => new DateTime(2026, 10, 7),
            };

        private async Task SeedAsync()
        {
            // 9 月每天一条已发布状态，统计行按日分店插入
            for (var day = new DateTime(2026, 9, 1); day <= new DateTime(2026, 9, 30); day = day.AddDays(1))
            {
                await _db.Insertable(new SalesStatisticRefreshState
                {
                    StatisticType = SalesStatisticType.StoreSales,
                    Date = day,
                    Status = SalesStatisticRefreshStatus.Fresh,
                    LastAggregatedAtUtc = DateTime.UtcNow,
                    CompletedAtUtc = DateTime.UtcNow,
                }).ExecuteCommandAsync();
            }

            await _db.Insertable(new List<StoreSalesStatistic>
            {
                new() { Date = new DateTime(2026, 9, 1), BranchCode = "S1", BranchName = "分店一", TotalAmount = 140m },
                new() { Date = new DateTime(2026, 9, 2), BranchCode = "S1", BranchName = "分店一", TotalAmount = 30m },
                new() { Date = new DateTime(2026, 9, 1), BranchCode = "S2", BranchName = "分店二", TotalAmount = 80m },
            }).ExecuteCommandAsync();
            await _db.Insertable(new BlazorApp.Shared.Models.POSM.POSM_设备注册信息表
            {
                系统设备编号 = "D2",
                设备硬件识别码 = "D2-hardware",
                分店代码 = "S2",
                设备类型 = "POS",
                设备系统 = "Windows",
                设备状态 = 1,
                设备授权码 = "D2-auth",
            }).ExecuteCommandAsync();

            await Order("a1", "2026-09-01 09:00:00", "S1", "D1", 1, (1, 30m), (2, 60m), (3, 10m));
            await Order("a2", "2026-09-01 23:59:59", "S1", "D1", 4, (2, 40m));
            await Order("a3", "2026-09-01 11:00:00", "S1", "D1", 2, (2, 999m));
            await Order("a4", "2026-09-02 00:00:00", "S1", "D1", 1, (2, 50m), (1, -20m));
            await Order("b1", "2026-08-31 23:59:59", "S1", "D1", 1, (2, 777m));
            await Order("b2", "2026-10-01 00:00:00", "S1", "D1", 1, (2, 888m));
            await Order("c1", "2026-09-01 10:00:00", null, "D2", 1, (2, 80m));
        }

        private async Task Order(string guid, string time, string? branch, string device, int status, params (int Method, decimal Amount)[] payments)
        {
            await _db.Ado.ExecuteCommandAsync(
                "INSERT INTO sales_order (OrderGuid, OrderTime, BranchCode, DeviceCode, Status) VALUES (@g, @t, @b, @d, @s)",
                new { g = guid, t = DateTime.Parse(time), b = branch, d = device, s = status });
            foreach (var (method, amount) in payments)
            {
                await _db.Ado.ExecuteCommandAsync(
                    "INSERT INTO payment_detail (PaymentGuid, OrderGuid, PaymentMethod, Amount) VALUES (@id, @o, @m, @a)",
                    new { id = Guid.NewGuid().ToString("D"), o = guid, m = method, a = amount });
            }
        }

        public async ValueTask DisposeAsync()
        {
            _db.Dispose();
            SqlConnection.ClearAllPools();
            await ExecuteAsync(_masterConnectionString, $"""
                IF DB_ID(N'{_databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{_databaseName}];
                END;
                """);
        }

        private const string SchemaSql = """
            CREATE TABLE dbo.sales_order (
                OrderGuid varchar(255) NOT NULL PRIMARY KEY,
                OrderTime datetime NOT NULL,
                BranchCode varchar(20) NULL,
                DeviceCode varchar(20) NULL,
                Status int NULL
            );
            CREATE TABLE dbo.payment_detail (
                PaymentGuid varchar(50) NOT NULL PRIMARY KEY,
                OrderGuid varchar(255) NULL,
                PaymentMethod int NOT NULL,
                Amount decimal(18, 4) NULL
            );
            """;
    }

    private static TContext CreateContext<TContext>(ISqlSugarClient db)
        where TContext : class
    {
        var context = (TContext)RuntimeHelpers.GetUninitializedObject(typeof(TContext));
        typeof(TContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static void EnsureLoopbackSqlServer(string connectionString)
    {
        // 集成测试会建库删库，只允许本机 SQL Server，绝不能误连生产。
        var dataSource = new SqlConnectionStringBuilder(connectionString).DataSource;
        var host = dataSource.Split(',')[0].Replace("tcp:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        Assert.True(
            host is "127.0.0.1" or "localhost" or "::1" or "(local)" or ".",
            "SQL Server 集成测试只允许连接本机实例。");
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
