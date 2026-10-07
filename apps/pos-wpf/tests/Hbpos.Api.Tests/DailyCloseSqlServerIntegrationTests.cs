using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.DailyClose;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

public sealed class DailyCloseSqlServerFactAttribute : FactAttribute
{
    public const string ConnectionVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public DailyCloseSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
        {
            Skip = "未配置隔离 SQL Server，跳过日结记录真实 SQL 测试。";
        }
    }
}

/// <summary>
/// 真实 SQL Server 上验证建表脚本、参数类型（DATETIME2 精度、NVARCHAR 中文）、唯一键并发与占位覆盖。
/// 每个用例自建独立数据库并在结束时删除，库名只来自本用例生成的 GUID。
/// </summary>
[Trait("Category", "SQL")]
public sealed class DailyCloseSqlServerIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan StoreOffset = TimeSpan.FromHours(11);
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

        database = $"HbDailyClose_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        masterConnection = builder.ConnectionString;
        await ExecuteAtAsync(masterConnection, $"CREATE DATABASE [{database}]");
        builder.InitialCatalog = database;
        connection = builder.ConnectionString;
        await new SqlSugarDailyCloseSchemaInitializer(
            new SqlSugarDailyCloseSchemaSqlExecutor(CreateContext())).InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (masterConnection is null)
        {
            return;
        }

        await ExecuteAtAsync(
            masterConnection,
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    [DailyCloseSqlServerFact]
    public async Task Schema_initializer_is_idempotent_and_creates_every_index_and_constraint()
    {
        // 再跑两次不应报错，也不应重复建对象。
        var initializer = new SqlSugarDailyCloseSchemaInitializer(
            new SqlSugarDailyCloseSchemaSqlExecutor(CreateContext()));
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        var indexes = await QueryStringsAsync(
            "SELECT [name] FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_DailyClose]') AND [name] IS NOT NULL ORDER BY [name];");
        Assert.Equal(
            ["IX_POSM_DailyClose_BusinessDate", "IX_POSM_DailyClose_StoreBusinessDate", "PK_POSM_DailyClose", "UX_POSM_DailyClose_Guid"],
            indexes);
        var checks = await QueryStringsAsync(
            "SELECT [name] FROM sys.check_constraints WHERE [parent_object_id] = OBJECT_ID(N'[dbo].[POSM_DailyClose]') ORDER BY [name];");
        Assert.Equal(
            ["CK_POSM_DailyClose_ClientKind", "CK_POSM_DailyClose_DataSource", "CK_POSM_DailyClose_DetailLevel"],
            checks);
        Assert.Equal(
            1L,
            await ScalarAsync(
                "SELECT COUNT_BIG(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_DailyClose]') AND [name] = N'UX_POSM_DailyClose_Guid' AND is_unique = 1;"));
    }

    [DailyCloseSqlServerFact]
    public async Task Check_constraints_reject_unknown_enum_values()
    {
        foreach (var (column, value) in new[]
                 {
                     ("ClientKind", "Web"),
                     ("DetailLevel", "Partial"),
                     ("DataSource", "Manual")
                 })
        {
            var sql = InsertRawSql(
                column == "ClientKind" ? value : "Wpf",
                column == "DetailLevel" ? value : "Full",
                column == "DataSource" ? value : "ClientUpload");
            await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(sql));
        }

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[POSM_DailyClose];"));
    }

    [DailyCloseSqlServerFact]
    public async Task Upload_round_trips_with_full_datetime2_precision_decimals_and_unicode_text()
    {
        var request = CreateRequest() with
        {
            // 100ns 精度的保存时间：若参数按 SQL datetime 发送会被截成约 3.33ms，下面的幂等重试就会误判为内容冲突。
            SavedAt = new DateTimeOffset(2026, 10, 7, 20, 15, 30, StoreOffset).AddTicks(1234567),
            CashierName = "张三 Alice",
            CashierId = "收银-001",
            ReturnQuantity = 1.234m
        };

        var first = await CreateService().SyncAsync(request, "S001", "POS-01", CancellationToken.None);
        var retry = await CreateService().SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        Assert.False(first.AlreadySynced);
        Assert.True(retry.AlreadySynced);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[POSM_DailyClose];"));
        Assert.Equal(
            request.SavedAt.UtcDateTime.Ticks,
            (await ScalarDateTimeAsync("SELECT [SavedAtUtc] FROM [dbo].[POSM_DailyClose];")).Ticks);
        Assert.Equal(
            "张三 Alice|收银-001",
            (await QueryStringsAsync("SELECT [CashierName] + N'|' + [CashierId] FROM [dbo].[POSM_DailyClose];")).Single());
        Assert.Equal(
            "2026-10-07|Full|ClientUpload|0|1.234|400.00|401.35|1.35|375.00|26.35",
            (await QueryStringsAsync(
                """
                SELECT CONVERT(NVARCHAR(10), [BusinessDate], 23) + N'|' + [DetailLevel] + N'|' + [DataSource] + N'|'
                    + CAST([BusinessDateInferred] AS NVARCHAR(1)) + N'|' + CAST([ReturnQuantity] AS NVARCHAR(30)) + N'|'
                    + CAST([ExpectedCashAmount] AS NVARCHAR(30)) + N'|' + CAST([CountedCashAmount] AS NVARCHAR(30)) + N'|'
                    + CAST([CashDifference] AS NVARCHAR(30)) + N'|' + CAST([NoteSubtotal] AS NVARCHAR(30)) + N'|'
                    + CAST([CoinSubtotal] AS NVARCHAR(30))
                FROM [dbo].[POSM_DailyClose];
                """)).Single());
        Assert.StartsWith(
            "[{\"denominationCents\":10000,\"quantity\":2},",
            (await QueryStringsAsync("SELECT [CashCountsJson] FROM [dbo].[POSM_DailyClose];")).Single(),
            StringComparison.Ordinal);
    }

    [DailyCloseSqlServerFact]
    public async Task Different_content_for_the_same_guid_conflicts_and_leaves_the_stored_row_untouched()
    {
        var request = CreateRequest();
        await CreateService().SyncAsync(request, "S001", "POS-01", CancellationToken.None);

        var content = await Assert.ThrowsAsync<DailyCloseConflictException>(() =>
            CreateService().SyncAsync(request with { OrderCount = 99 }, "S001", "POS-01", CancellationToken.None));
        var scope = await Assert.ThrowsAsync<DailyCloseConflictException>(() =>
            CreateService().SyncAsync(
                request with { DeviceCode = "POS-02" },
                "S001",
                "POS-02",
                CancellationToken.None));

        Assert.Equal("DAILY_CLOSE_CONTENT_CONFLICT", content.Code);
        Assert.Equal("DAILY_CLOSE_SCOPE_CONFLICT", scope.Code);
        Assert.Equal(12L, await ScalarAsync("SELECT CAST([OrderCount] AS BIGINT) FROM [dbo].[POSM_DailyClose];"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[POSM_DailyClose];"));
    }

    [DailyCloseSqlServerFact]
    public async Task Concurrent_uploads_of_one_guid_insert_exactly_one_row_and_the_rest_are_idempotent()
    {
        var request = CreateRequest();

        // 每个任务各自一份 DbContext / 连接，真实竞争唯一索引。
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => CreateService().SyncAsync(request, "S001", "POS-01", CancellationToken.None))));

        Assert.All(responses, response => Assert.True(response.Accepted));
        Assert.Equal(1, responses.Count(response => !response.AlreadySynced));
        Assert.Equal(7, responses.Count(response => response.AlreadySynced));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[POSM_DailyClose];"));
    }

    [DailyCloseSqlServerFact]
    public async Task Backfill_placeholder_is_replaced_once_with_full_client_data()
    {
        var request = CreateRequest();
        await ExecuteAsync(
            $"""
            INSERT INTO [dbo].[POSM_DailyClose]
                ([DailyCloseGuid], [StoreCode], [DeviceCode], [ClientKind], [DetailLevel], [DataSource], [BackfillBatch],
                 [BusinessDate], [BusinessDateInferred], [CashierId], [CashierName], [SavedAtUtc],
                 [CashNetAmount], [ExpectedCashAmount], [CountedCashAmount], [CashDifference],
                 [ReceivedAtUtc], [UpdatedAtUtc])
            VALUES
                ('{request.DailyCloseGuid}', N'S001', N'POS-01', N'Ipad', N'CashOnly', N'AuditBackfill', N'backfill-20261007',
                 '2026-10-06', 1, N'', N'Backfilled', '2026-10-06T23:31:00',
                 400.00, 400.00, 399.00, -1.00,
                 '2026-10-06T01:00:00', '2026-10-06T01:00:00');
            """);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(() => CreateService().SyncAsync(request, "S001", "POS-01", CancellationToken.None))));

        Assert.Equal(1, responses.Count(response => response.ReplacedPlaceholder));
        Assert.Equal(5, responses.Count(response => response.AlreadySynced));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[POSM_DailyClose];"));
        Assert.Equal(
            "Wpf|Full|ClientUpload|0|0|Alice|1",
            (await QueryStringsAsync(
                """
                SELECT [ClientKind] + N'|' + [DetailLevel] + N'|' + [DataSource] + N'|'
                    + CAST([BusinessDateInferred] AS NVARCHAR(1)) + N'|'
                    + CAST(CASE WHEN [BackfillBatch] IS NULL THEN 0 ELSE 1 END AS NVARCHAR(1)) + N'|'
                    + [CashierName] + N'|' + CAST(CASE WHEN [CashCountsJson] IS NULL THEN 0 ELSE 1 END AS NVARCHAR(1))
                FROM [dbo].[POSM_DailyClose];
                """)).Single());
        Assert.Equal(
            "2026-10-07",
            (await QueryStringsAsync("SELECT CONVERT(NVARCHAR(10), [BusinessDate], 23) FROM [dbo].[POSM_DailyClose];")).Single());
        Assert.Equal(
            1.35m,
            Convert.ToDecimal(await ScalarObjectAsync("SELECT [CashDifference] FROM [dbo].[POSM_DailyClose];")));
    }

    [DailyCloseSqlServerFact]
    public async Task Backfill_placeholder_of_another_device_is_never_overwritten()
    {
        var request = CreateRequest();
        await ExecuteAsync(
            $"""
            INSERT INTO [dbo].[POSM_DailyClose]
                ([DailyCloseGuid], [StoreCode], [DeviceCode], [ClientKind], [DetailLevel], [DataSource], [BackfillBatch],
                 [BusinessDate], [BusinessDateInferred], [CashierId], [CashierName], [SavedAtUtc])
            VALUES
                ('{request.DailyCloseGuid}', N'S002', N'POS-09', N'Wpf', N'TraceOnly', N'AuditBackfill', N'backfill-20261007',
                 '2026-10-06', 1, N'', N'Backfilled', '2026-10-06T23:31:00');
            """);

        var exception = await Assert.ThrowsAsync<DailyCloseConflictException>(() =>
            CreateService().SyncAsync(request, "S001", "POS-01", CancellationToken.None));

        Assert.Equal("DAILY_CLOSE_SCOPE_CONFLICT", exception.Code);
        Assert.Equal(
            "S002|POS-09|TraceOnly|AuditBackfill",
            (await QueryStringsAsync(
                "SELECT [StoreCode] + N'|' + [DeviceCode] + N'|' + [DetailLevel] + N'|' + [DataSource] FROM [dbo].[POSM_DailyClose];")).Single());
    }

    [DailyCloseSqlServerFact]
    public async Task Replace_placeholder_sql_affects_zero_rows_for_a_full_record()
    {
        var request = CreateRequest();
        await CreateService().SyncAsync(request, "S001", "POS-01", CancellationToken.None);
        var repository = new SqlSugarDailyCloseRepository(CreateContext());
        var existing = (await repository.GetByGuidAsync(request.DailyCloseGuid, CancellationToken.None))!;

        // 直接对已是 Full 的行调用覆盖：DetailLevel <> 'Full' 守卫必须让它影响 0 行。
        existing.CashierName = "Hijack";
        var replaced = await repository.TryReplacePlaceholderAsync(existing, CancellationToken.None);

        Assert.False(replaced);
        Assert.Equal(
            "Alice",
            (await QueryStringsAsync("SELECT [CashierName] FROM [dbo].[POSM_DailyClose];")).Single());
    }

    private DailyCloseSyncService CreateService() =>
        new(new SqlSugarDailyCloseRepository(CreateContext()));

    private HbposSqlSugarContext CreateContext()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MainConnection"] = connection,
            ["ConnectionStrings:PosmConnection"] = connection,
            ["Database:CommandTimeoutSeconds"] = "30",
        }).Build();
        return new HbposSqlSugarContext(config, NullLogger<HbposSqlSugarContext>.Instance);
    }

    private static string InsertRawSql(string clientKind, string detailLevel, string dataSource) =>
        $"""
        INSERT INTO [dbo].[POSM_DailyClose]
            ([DailyCloseGuid], [StoreCode], [DeviceCode], [ClientKind], [DetailLevel], [DataSource],
             [BusinessDate], [CashierId], [CashierName], [SavedAtUtc])
        VALUES
            (NEWID(), N'S001', N'POS-01', N'{clientKind}', N'{detailLevel}', N'{dataSource}',
             '2026-10-07', N'', N'', '2026-10-07T00:00:00');
        """;

    /// <summary>一份内部自洽的合法请求：纸币 375.00 + 硬币 26.35 = 实点 401.35，现金净额 400.00，差额 +1.35。</summary>
    private static DailyCloseSyncRequest CreateRequest()
    {
        int[] quantities = [2, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        return new DailyCloseSyncRequest(
            SchemaVersion: 1,
            DailyCloseGuid: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            StoreCode: "S001",
            DeviceCode: "POS-01",
            ClientKind: "Wpf",
            BusinessDate: new DateOnly(2026, 10, 7),
            PeriodFrom: new DateTimeOffset(2026, 10, 7, 0, 0, 0, StoreOffset),
            PeriodTo: new DateTimeOffset(2026, 10, 8, 0, 0, 0, StoreOffset),
            SavedAt: new DateTimeOffset(2026, 10, 7, 10, 30, 0, StoreOffset),
            CashierId: "C001",
            CashierName: "Alice",
            AppVersion: "1.0.47",
            OrderCount: 12,
            ReturnQuantity: 1.5m,
            RefundAmount: 105m,
            Tenders:
            [
                new DailyCloseTenderSync("Cash", 500m, 100m, 400m),
                new DailyCloseTenderSync("Card", 250.50m, 0m, 250.50m),
                new DailyCloseTenderSync("Voucher", 20m, 5m, 15m)
            ],
            CashCounts: DailyCloseContractConstants.DenominationCents
                .Select((denomination, index) => new DailyCloseCashCountSync(denomination, quantities[index]))
                .ToArray(),
            NoteSubtotal: 375m,
            CoinSubtotal: 26.35m,
            CountedCashAmount: 401.35m,
            CashDifference: 1.35m);
    }

    private async Task ExecuteAsync(string sql) => await ExecuteAtAsync(connection, sql);

    private async Task<long> ScalarAsync(string sql) => Convert.ToInt64(await ScalarObjectAsync(sql));

    private async Task<DateTime> ScalarDateTimeAsync(string sql) => (DateTime)(await ScalarObjectAsync(sql))!;

    private async Task<object?> ScalarObjectAsync(string sql)
    {
        await using var sqlConnection = new SqlConnection(connection);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection) { CommandTimeout = 60 };
        return await command.ExecuteScalarAsync();
    }

    private async Task<string[]> QueryStringsAsync(string sql)
    {
        await using var sqlConnection = new SqlConnection(connection);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private static async Task ExecuteAtAsync(string connectionString, string sql)
    {
        await using var sqlConnection = new SqlConnection(connectionString);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }
}
