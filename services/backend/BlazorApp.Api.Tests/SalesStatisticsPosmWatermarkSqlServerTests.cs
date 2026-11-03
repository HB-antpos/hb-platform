using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using Microsoft.Data.SqlClient;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class SalesStatisticsPosmWatermarkSqlServerFactAttribute : FactAttribute
{
    private const string ConnectionEnvironmentVariable =
        "SALES_STATISTICS_RECOVERY_SQLSERVER_TEST_CONNECTION";

    public SalesStatisticsPosmWatermarkSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过 POSM 水位 SQL Server 验证。";
    }
}

/// <summary>
/// 用真实 SQL Server 核对 POSM 每日水位的筛选、空值和快照事务语义。
/// 每个用例只在核验过的本机容器内创建独立临时数据库。
/// </summary>
public sealed class SalesStatisticsPosmWatermarkSqlServerTests
{
    private static readonly DateTime TargetDate = new(2026, 9, 20);

    [SalesStatisticsPosmWatermarkSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 空库及有效订单的空上传时间返回Null()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Null(await fixture.QueryAsync(TargetDate));

        await fixture.AddOrderAsync("null-watermark", 1, TargetDate.AddHours(12), null);
        Assert.Null(await fixture.QueryAsync(TargetDate));
        await fixture.AddPaymentAsync("null-payment", "null-watermark", null);
        await fixture.AddDetailAsync("null-detail", "null-watermark", null);
        Assert.Null(await fixture.QueryAsync(TargetDate));
    }

    [SalesStatisticsPosmWatermarkSqlServerFact]
    [Trait("Category", "SQL")]
    // #710 起销售统计按净额并入旧 POS 退款单（Status=3），水位口径同为 SalesOrderStatusRules.NetSalesStatuses（1、3、4）；
    // 退款单的晚上传也必须推动当日水位，否则净额统计不会重算。
    public async Task 状态及自然日边界只计入状态1_3_4的当日订单()
    {
        await using var fixture = await Fixture.CreateAsync();
        var veryLate = TargetDate.AddDays(10);
        await fixture.AddOrderAsync("status-1-start", 1, TargetDate, TargetDate.AddHours(1));
        await fixture.AddOrderAsync("status-4-end", 4, TargetDate.AddDays(1).AddMilliseconds(-10), TargetDate.AddHours(2));
        await fixture.AddOrderAsync("previous-day", 1, TargetDate.AddMilliseconds(-10), veryLate);
        await fixture.AddOrderAsync("next-day", 4, TargetDate.AddDays(1), veryLate);
        await fixture.AddOrderAsync("null-date", 1, null, veryLate);
        await fixture.AddOrderAsync("null-status", null, TargetDate.AddHours(8), veryLate);
        await fixture.AddOrderAsync("status-0", 0, TargetDate.AddHours(8), veryLate);
        await fixture.AddOrderAsync("status-2", 2, TargetDate.AddHours(8), veryLate);
        await fixture.AddOrderAsync("status-3-refund", 3, TargetDate.AddHours(8), TargetDate.AddHours(3));

        // 方法接收带时分秒的日期时仍应按该日零点到次日零点查询；
        // 退款单上传时间最晚（03:00），若状态 3 被漏掉结果会退回 02:00。
        Assert.Equal(TargetDate.AddHours(3), await fixture.QueryAsync(TargetDate.AddHours(15)));
    }

    [SalesStatisticsPosmWatermarkSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 当日订单的付款和明细晚上传分别推动水位_跨日上传仍计入()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddOrderAsync("target", 1, TargetDate.AddHours(10), TargetDate.AddHours(11));
        Assert.Equal(TargetDate.AddHours(11), await fixture.QueryAsync(TargetDate));

        var paymentUpload = TargetDate.AddDays(2).AddHours(1);
        await fixture.AddPaymentAsync("target-payment", "target", paymentUpload);
        Assert.Equal(paymentUpload, await fixture.QueryAsync(TargetDate));

        var detailUpload = TargetDate.AddDays(3).AddHours(1);
        await fixture.AddDetailAsync("target-detail", "target", detailUpload);
        Assert.Equal(detailUpload, await fixture.QueryAsync(TargetDate));

        // 高水位噪声必须被订单日期、状态及关联关系共同挡住。
        var noiseUpload = TargetDate.AddDays(30);
        await fixture.AddOrderAsync("other-day", 1, TargetDate.AddDays(1), null);
        await fixture.AddPaymentAsync("other-day-payment", "other-day", noiseUpload);
        await fixture.AddDetailAsync("other-day-detail", "other-day", noiseUpload);
        await fixture.AddOrderAsync("wrong-status", 2, TargetDate.AddHours(9), null);
        await fixture.AddPaymentAsync("wrong-status-payment", "wrong-status", noiseUpload);
        await fixture.AddDetailAsync("wrong-status-detail", "wrong-status", noiseUpload);
        await fixture.AddPaymentAsync("orphan-payment", "missing-order", noiseUpload);
        await fixture.AddDetailAsync("orphan-detail", "missing-order", noiseUpload);
        Assert.Equal(detailUpload, await fixture.QueryAsync(TargetDate));
    }

    [SalesStatisticsPosmWatermarkSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 快照事务内读取同一上下文的未提交水位_回滚后消失()
    {
        await using var fixture = await Fixture.CreateAsync();
        var db = fixture.Db;
        var expected = TargetDate.AddHours(17);
        await db.Ado.BeginTranAsync(System.Data.IsolationLevel.Snapshot);
        try
        {
            await fixture.AddOrderAsync("in-transaction", 4, TargetDate.AddHours(9), expected);
            Assert.Equal(expected, await fixture.QueryAsync(TargetDate));
        }
        finally
        {
            if (db.Ado.Transaction != null)
                await db.Ado.RollbackTranAsync();
        }

        Assert.Null(await fixture.QueryAsync(TargetDate));

        // 快照第一次读后，另一连接提交的付款上传只能在下一次查询轮次可见。
        var baseUpload = TargetDate.AddHours(11);
        var laterUpload = TargetDate.AddDays(1).AddHours(12);
        await fixture.AddOrderAsync("committed-order", 1, TargetDate.AddHours(10), baseUpload);
        await db.Ado.BeginTranAsync(System.Data.IsolationLevel.Snapshot);
        try
        {
            Assert.Equal(baseUpload, await fixture.QueryAsync(TargetDate));
            await fixture.AddPaymentOutsideTransactionAsync("concurrent-payment", "committed-order", laterUpload);
            Assert.Equal(baseUpload, await fixture.QueryAsync(TargetDate));
            await db.Ado.CommitTranAsync();
        }
        finally
        {
            if (db.Ado.Transaction != null)
                await db.Ado.RollbackTranAsync();
        }
        Assert.Equal(laterUpload, await fixture.QueryAsync(TargetDate));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string ConnectionEnvironmentVariable =
            "SALES_STATISTICS_RECOVERY_SQLSERVER_TEST_CONNECTION";
        private readonly string _masterConnection;
        private readonly string _databaseName;
        private readonly string _databaseConnection;
        private readonly SqlSugarClient _db;
        private readonly POSMSqlSugarContext _context;

        private Fixture(string masterConnection, string databaseConnection, string databaseName)
        {
            _masterConnection = masterConnection;
            _databaseConnection = databaseConnection;
            _databaseName = databaseName;
            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = databaseConnection,
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
                MoreSettings = new ConnMoreSettings
                {
                    IsWithNoLockQuery = true,
                    DisableWithNoLockWithTran = true,
                },
            });
            _context = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(POSMSqlSugarContext));
            typeof(POSMSqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_context, _db);
        }

        internal SqlSugarClient Db => _db;

        internal static async Task<Fixture> CreateAsync()
        {
            var configured = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
            Assert.False(string.IsNullOrWhiteSpace(configured));
            var builder = new SqlConnectionStringBuilder(configured);
            // 生产硬禁止：CI 可使用其他本机端口，但绝不连接非 loopback SQL Server。
            var dataSource = builder.DataSource.Trim();
            if (dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
                dataSource = dataSource[4..];
            var host = dataSource.Split(',', 2)[0].Trim('[', ']');
            Assert.Contains(host,
                new[] { "127.0.0.1", "localhost", "::1" },
                StringComparer.OrdinalIgnoreCase);

            var databaseName = "HbPosmWatermark_" + Guid.NewGuid().ToString("N");
            var masterConnection = new SqlConnectionStringBuilder(configured)
            {
                InitialCatalog = "master",
                ConnectRetryCount = 0,
            }.ConnectionString;
            var databaseConnection = new SqlConnectionStringBuilder(configured)
            {
                InitialCatalog = databaseName,
                ConnectRetryCount = 0,
            }.ConnectionString;
            await ExecuteAsync(masterConnection, $"CREATE DATABASE [{databaseName}]");
            var fixture = new Fixture(masterConnection, databaseConnection, databaseName);
            try
            {
                await fixture._db.Ado.ExecuteCommandAsync(
                    "ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;");
                await fixture._db.Ado.ExecuteCommandAsync("""
                    CREATE TABLE dbo.sales_order (
                        OrderGuid nvarchar(64) NOT NULL PRIMARY KEY,
                        Status int NULL,
                        OrderTime datetime2(7) NULL,
                        LastUploadTime datetime2(7) NULL
                    );
                    CREATE TABLE dbo.payment_detail (
                        PaymentGuid nvarchar(64) NOT NULL PRIMARY KEY,
                        OrderGuid nvarchar(64) NULL,
                        LastUploadTime datetime2(7) NULL
                    );
                    CREATE TABLE dbo.sales_order_detail (
                        OrderDetailGuid nvarchar(64) NOT NULL PRIMARY KEY,
                        OrderGuid nvarchar(64) NULL,
                        LastUploadTime datetime2(7) NULL
                    );
                    """);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal Task<DateTime?> QueryAsync(DateTime date) =>
            SalesStatisticsProductStoreDailyStateSlice.QueryDailyPosmSourceWatermarkAsync(_context, date);

        internal Task<int> AddOrderAsync(string id, int? status, DateTime? orderTime, DateTime? uploadTime) =>
            _db.Ado.ExecuteCommandAsync(
                "INSERT dbo.sales_order (OrderGuid, Status, OrderTime, LastUploadTime) VALUES (@Id, @Status, @OrderTime, @UploadTime)",
                new SugarParameter("@Id", id),
                new SugarParameter("@Status", status),
                new SugarParameter("@OrderTime", orderTime),
                new SugarParameter("@UploadTime", uploadTime));

        internal Task<int> AddPaymentAsync(string id, string orderId, DateTime? uploadTime) =>
            _db.Ado.ExecuteCommandAsync(
                "INSERT dbo.payment_detail (PaymentGuid, OrderGuid, LastUploadTime) VALUES (@Id, @OrderId, @UploadTime)",
                new SugarParameter("@Id", id),
                new SugarParameter("@OrderId", orderId),
                new SugarParameter("@UploadTime", uploadTime));

        internal Task<int> AddDetailAsync(string id, string orderId, DateTime? uploadTime) =>
            _db.Ado.ExecuteCommandAsync(
                "INSERT dbo.sales_order_detail (OrderDetailGuid, OrderGuid, LastUploadTime) VALUES (@Id, @OrderId, @UploadTime)",
                new SugarParameter("@Id", id),
                new SugarParameter("@OrderId", orderId),
                new SugarParameter("@UploadTime", uploadTime));

        internal async Task AddPaymentOutsideTransactionAsync(string id, string orderId, DateTime uploadTime)
        {
            await using var connection = new SqlConnection(_databaseConnection);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT dbo.payment_detail (PaymentGuid, OrderGuid, LastUploadTime) VALUES (@Id, @OrderId, @UploadTime)";
            command.Parameters.Add("@Id", System.Data.SqlDbType.NVarChar, 64).Value = id;
            command.Parameters.Add("@OrderId", System.Data.SqlDbType.NVarChar, 64).Value = orderId;
            command.Parameters.Add("@UploadTime", System.Data.SqlDbType.DateTime2).Value = uploadTime;
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _db.Dispose();
            await ExecuteAsync(_masterConnection,
                $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]");
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
}
