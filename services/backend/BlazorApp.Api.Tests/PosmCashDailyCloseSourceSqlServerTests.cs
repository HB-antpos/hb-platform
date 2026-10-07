using BlazorApp.Api.Services.StoreCash;
using Microsoft.Data.SqlClient;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class PosmCashDailyCloseSourceSqlServerFactAttribute : FactAttribute
{
    private const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public PosmCashDailyCloseSourceSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过真实 SQL Server 现金日结取数验证。";
    }
}

/// <summary>
/// 现金日结取数口在真实 SQL Server 上的翻译：分店 IN 列表、DATE 列区间、可空金额过滤、匿名投影与表存在性检查。
/// 建表语句与 Hbpos.Api 的 DailyCloseSchemaInitializer 列名、类型一致（DATE / UNIQUEIDENTIFIER / DECIMAL(18,2)）。
/// </summary>
[Trait("Category", "SQL")]
public sealed class PosmCashDailyCloseSourceSqlServerTests
{
    private const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    [PosmCashDailyCloseSourceSqlServerFact]
    public async Task 真实SQLServer_只取区间内有实点金额的日结_分店代码不区分大小写_表不存在时视为未接入()
    {
        var master = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)!)
        {
            InitialCatalog = "master",
        }.ConnectionString;
        var databaseName = $"HbCashCloseSource_{Guid.NewGuid():N}";
        await ExecuteAsync(master, $"CREATE DATABASE [{databaseName}] COLLATE Chinese_PRC_90_CI_AS;");
        var connectionString = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        try
        {
            using var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = connectionString,
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });
            var source = new PosmCashDailyCloseSource(db);

            // 表还没建：按未接入处理，不报错。
            Assert.False(source.IsConnected);
            Assert.Empty(await source.GetArchivesAsync(new[] { "S001" }, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 7), default));

            await ExecuteAsync(connectionString, """
                CREATE TABLE [dbo].[POSM_DailyClose] (
                    [Id] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [DailyCloseGuid] UNIQUEIDENTIFIER NOT NULL,
                    [StoreCode] NVARCHAR(32) NOT NULL,
                    [DeviceCode] NVARCHAR(64) NOT NULL,
                    [ClientKind] NVARCHAR(16) NOT NULL,
                    [DetailLevel] NVARCHAR(16) NOT NULL,
                    [DataSource] NVARCHAR(24) NOT NULL,
                    [BackfillBatch] NVARCHAR(64) NULL,
                    [BusinessDate] DATE NOT NULL,
                    [BusinessDateInferred] BIT NOT NULL DEFAULT (0),
                    [PeriodFromUtc] DATETIME2(7) NULL,
                    [PeriodToUtc] DATETIME2(7) NULL,
                    [CashierId] NVARCHAR(64) NOT NULL,
                    [CashierName] NVARCHAR(128) NOT NULL,
                    [SavedAtUtc] DATETIME2(7) NOT NULL,
                    [ExpectedCashAmount] DECIMAL(18,2) NULL,
                    [CountedCashAmount] DECIMAL(18,2) NULL,
                    [CashDifference] DECIMAL(18,2) NULL,
                    [ReceivedAtUtc] DATETIME2(7) NOT NULL DEFAULT (SYSUTCDATETIME()),
                    [UpdatedAtUtc] DATETIME2(7) NOT NULL DEFAULT (SYSUTCDATETIME())
                );
                INSERT [dbo].[POSM_DailyClose]
                    ([DailyCloseGuid], [StoreCode], [DeviceCode], [ClientKind], [DetailLevel], [DataSource], [BusinessDate],
                     [PeriodFromUtc], [PeriodToUtc], [CashierId], [CashierName], [SavedAtUtc],
                     [ExpectedCashAmount], [CountedCashAmount], [CashDifference])
                VALUES
                    ('11111111-1111-1111-1111-111111111111', N's001', N'POS_1', N'Wpf', N'Full', N'ClientUpload', '2026-10-07',
                     '2026-10-06T21:00:00', '2026-10-07T09:00:00', N'1001', N'Alice', '2026-10-07T09:00:05', 310.00, 300.50, -9.50),
                    ('22222222-2222-2222-2222-222222222222', N'S001', N'POS_2', N'Handheld', N'CashOnly', N'AuditBackfill', '2026-10-05',
                     NULL, NULL, N'1002', N'Bob', '2026-10-05T08:00:00', NULL, 100.00, -5.00),
                    ('33333333-3333-3333-3333-333333333333', N'S001', N'POS_1', N'Wpf', N'TraceOnly', N'AuditBackfill', '2026-10-06',
                     NULL, NULL, N'1001', N'Alice', '2026-10-06T08:00:00', NULL, NULL, NULL),
                    ('44444444-4444-4444-4444-444444444444', N'S001', N'POS_1', N'Wpf', N'Full', N'ClientUpload', '2026-09-30',
                     NULL, NULL, N'1001', N'Alice', '2026-09-30T08:00:00', 1.00, 1.00, 0.00),
                    ('55555555-5555-5555-5555-555555555555', N'S009', N'POS_9', N'Wpf', N'Full', N'ClientUpload', '2026-10-07',
                     NULL, NULL, N'1009', N'Eve', '2026-10-07T08:00:00', 9.00, 9.00, 0.00);
                """);

            Assert.True(source.IsConnected);
            var archives = await source.GetArchivesAsync(new[] { "S001" }, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 7), default);

            Assert.Equal(2, archives.Count);
            var full = Assert.Single(archives, item => item.CloseId == "11111111-1111-1111-1111-111111111111");
            Assert.Equal(new DateOnly(2026, 10, 7), full.BusinessDate);
            Assert.Equal(300.50m, full.CountedCash);
            Assert.Equal(310.00m, full.ExpectedCash);
            Assert.Equal(new DateTime(2026, 10, 6, 21, 0, 0), full.PeriodFromUtc);
            var backfill = Assert.Single(archives, item => item.CloseId == "22222222-2222-2222-2222-222222222222");
            Assert.Equal(105.00m, backfill.ExpectedCash);
            Assert.Equal(new DateTime(2026, 10, 5), backfill.PeriodFromUtc.Date);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(master, $"""
                IF DB_ID(N'{databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{databaseName}];
                END;
                """);
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }
}
