using Hbpos.Api.Services;
using Microsoft.Data.SqlClient;

namespace Hbpos.Api.Tests;

/// <summary>
/// 真实 SQL Server 上验证用量统计的 upsert：不存在的行插入、已存在的行累加（而不是覆盖）、分店为空串的合法性、
/// 超过单批行数上限的分批写入、多个写入方并发写同一批键时不丢数也不撞主键。
/// 表结构由主 API 迁移 20261026.004-pos-api-entry-usage 创建；这里的建表语句与该迁移保持一致（Hbpos.Api.Tests 不引用主 API 项目）。
/// </summary>
[Collection(DeviceActivationSqlServerCollection.Name)]
[Trait("Category", "SQL")]
public sealed class PosApiEntryUsageSqlServerIntegrationTests(DeviceActivationSqlServerFixture fixture)
{
    private static readonly DateOnly D1 = new(2026, 10, 24);
    private static readonly DateOnly D2 = new(2026, 10, 25);
    private static readonly DateTime Now = new(2026, 10, 24, 5, 0, 0, DateTimeKind.Utc);

    private const string CreateTableSql = """
        IF OBJECT_ID(N'dbo.PosApiEntryUsageDaily', N'U') IS NOT NULL DROP TABLE [dbo].[PosApiEntryUsageDaily];
        CREATE TABLE [dbo].[PosApiEntryUsageDaily]
        (
            [UsageDate] date NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [Entry] nvarchar(16) NOT NULL,
            [RequestCount] bigint NOT NULL CONSTRAINT [DF_PosApiEntryUsageDaily_RequestCount] DEFAULT (0),
            [Status4xxCount] bigint NOT NULL CONSTRAINT [DF_PosApiEntryUsageDaily_Status4xxCount] DEFAULT (0),
            [Status5xxCount] bigint NOT NULL CONSTRAINT [DF_PosApiEntryUsageDaily_Status5xxCount] DEFAULT (0),
            [UpdatedAt] datetime2 NOT NULL,
            CONSTRAINT [PK_PosApiEntryUsageDaily] PRIMARY KEY CLUSTERED ([UsageDate], [StoreCode], [Entry]),
            CONSTRAINT [CK_PosApiEntryUsageDaily_Entry] CHECK ([Entry] IN (N'edge', N'aws', N'direct', N'unknown'))
        );
        """;

    private static KeyValuePair<PosApiEntryUsageKey, PosApiEntryUsageDelta> Row(
        DateOnly date, string store, string entry, long requests, long s4xx = 0, long s5xx = 0) =>
        new(new PosApiEntryUsageKey(date, store, entry), new PosApiEntryUsageDelta(requests, s4xx, s5xx));

    private async Task<(long Requests, long S4xx, long S5xx)?> ReadAsync(DateOnly date, string store, string entry)
    {
        await using var connection = new SqlConnection(fixture.MainConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT [RequestCount], [Status4xxCount], [Status5xxCount]
            FROM [dbo].[PosApiEntryUsageDaily]
            WHERE [UsageDate] = @D AND [StoreCode] = @S AND [Entry] = @E
            """,
            connection);
        command.Parameters.Add(new SqlParameter("@D", System.Data.SqlDbType.Date) { Value = date.ToDateTime(TimeOnly.MinValue) });
        command.Parameters.AddWithValue("@S", store);
        command.Parameters.AddWithValue("@E", entry);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)) : null;
    }

    private async Task<long> CountRowsAsync()
    {
        await using var connection = new SqlConnection(fixture.MainConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT COUNT(*) FROM [dbo].[PosApiEntryUsageDaily]", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [DeviceActivationSqlServerFact]
    public async Task Upsert_inserts_new_rows_and_accumulates_existing_ones()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteMainAsync(CreateTableSql);
        var writer = new SqlSugarPosApiEntryUsageWriter(fixture.CreateContext());

        await writer.UpsertAsync([Row(D1, "S001", "edge", 10, 2, 1), Row(D1, "", "unknown", 3, 3, 0)], Now, CancellationToken.None);
        Assert.Equal((10L, 2L, 1L), await ReadAsync(D1, "S001", "edge"));
        // 未鉴权请求的分店是空串，必须能作为主键一部分写入。
        Assert.Equal((3L, 3L, 0L), await ReadAsync(D1, "", "unknown"));

        // 再来一批同键增量：累加而不是覆盖；其它行不受影响。
        await writer.UpsertAsync([Row(D1, "S001", "edge", 5, 0, 4), Row(D2, "S001", "edge", 1)], Now.AddMinutes(1), CancellationToken.None);
        Assert.Equal((15L, 2L, 5L), await ReadAsync(D1, "S001", "edge"));
        Assert.Equal((1L, 0L, 0L), await ReadAsync(D2, "S001", "edge"));
        Assert.Equal((3L, 3L, 0L), await ReadAsync(D1, "", "unknown"));
    }

    [DeviceActivationSqlServerFact]
    public async Task Upsert_splits_large_batches_and_loses_nothing()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteMainAsync(CreateTableSql);
        var writer = new SqlSugarPosApiEntryUsageWriter(fixture.CreateContext());

        // 250 个不同分店 → 三批（100 + 100 + 50），每批一个事务。
        var rows = Enumerable.Range(0, 250).Select(i => Row(D1, $"S{i:000}", "aws", i + 1)).ToList();
        await writer.UpsertAsync(rows, Now, CancellationToken.None);

        Assert.Equal(250, await CountRowsAsync());
        Assert.Equal((250L, 0L, 0L), await ReadAsync(D1, "S249", "aws"));
    }

    [DeviceActivationSqlServerFact]
    public async Task Upsert_from_several_writers_at_once_neither_loses_counts_nor_collides_on_the_primary_key()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteMainAsync(CreateTableSql);

        // 模拟 POS API 多实例 / 重启交接时同时写同一批键。
        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            var writer = new SqlSugarPosApiEntryUsageWriter(fixture.CreateContext());
            for (var round = 0; round < 5; round++)
            {
                await writer.UpsertAsync(
                    [Row(D1, "S001", "edge", 1, 1, 0), Row(D1, "S002", "direct", 2, 0, 1)],
                    Now,
                    CancellationToken.None);
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal((30L, 30L, 0L), await ReadAsync(D1, "S001", "edge"));
        Assert.Equal((60L, 0L, 30L), await ReadAsync(D1, "S002", "direct"));
        Assert.Equal(2, await CountRowsAsync());
    }

    [DeviceActivationSqlServerFact]
    public async Task A_failing_batch_rolls_back_as_a_whole_so_a_retry_cannot_double_count()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteMainAsync(CreateTableSql);
        var writer = new SqlSugarPosApiEntryUsageWriter(fixture.CreateContext());
        await writer.UpsertAsync([Row(D1, "S001", "edge", 10)], Now, CancellationToken.None);

        // 第二行的入口违反 CHECK 约束 → 整批回滚，第一行的累加也不能留下。
        await Assert.ThrowsAnyAsync<Exception>(() => writer.UpsertAsync(
            [Row(D1, "S001", "edge", 5), Row(D1, "S001", "tencent", 1)], Now, CancellationToken.None));

        Assert.Equal((10L, 0L, 0L), await ReadAsync(D1, "S001", "edge"));
    }
}
