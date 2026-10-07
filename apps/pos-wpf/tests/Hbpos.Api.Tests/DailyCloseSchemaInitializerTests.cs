using Hbpos.Api;
using Hbpos.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Api.Tests;

public sealed class DailyCloseSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_creates_idempotent_POSM_DailyClose_schema_in_one_locked_transaction()
    {
        var executor = new CapturingExecutor();
        var initializer = new SqlSugarDailyCloseSchemaInitializer(executor);

        await initializer.InitializeAsync();

        var sql = Assert.Single(executor.Commands);
        Assert.Contains("SET XACT_ABORT ON", sql, StringComparison.Ordinal);
        Assert.Contains("BEGIN TRANSACTION", sql, StringComparison.Ordinal);
        Assert.Contains("sys.sp_getapplock", sql, StringComparison.Ordinal);
        Assert.Contains("N'Hbpos.DailyClose.Schema.v1'", sql, StringComparison.Ordinal);
        Assert.Contains("@LockOwner = N'Transaction'", sql, StringComparison.Ordinal);
        Assert.Contains("COMMIT TRANSACTION", sql, StringComparison.Ordinal);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_DailyClose]', N'U') IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [dbo].[POSM_DailyClose]", sql, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT [PK_POSM_DailyClose] PRIMARY KEY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_declares_every_column_with_the_agreed_type_and_nullability()
    {
        var sql = Flat(SqlSugarDailyCloseSchemaInitializer.EnsureTableSql);

        foreach (var column in new[]
                 {
                     "[Id] BIGINT IDENTITY(1,1) NOT NULL",
                     "[DailyCloseGuid] UNIQUEIDENTIFIER NOT NULL",
                     "[StoreCode] NVARCHAR(32) NOT NULL",
                     "[DeviceCode] NVARCHAR(64) NOT NULL",
                     "[ClientKind] NVARCHAR(16) NOT NULL",
                     "[DetailLevel] NVARCHAR(16) NOT NULL",
                     "[DataSource] NVARCHAR(24) NOT NULL",
                     "[BackfillBatch] NVARCHAR(64) NULL",
                     "[BusinessDate] DATE NOT NULL",
                     "[BusinessDateInferred] BIT NOT NULL CONSTRAINT [DF_POSM_DailyClose_BusinessDateInferred] DEFAULT (0)",
                     "[PeriodFromUtc] DATETIME2(7) NULL",
                     "[PeriodToUtc] DATETIME2(7) NULL",
                     "[CashierId] NVARCHAR(64) NOT NULL",
                     "[CashierName] NVARCHAR(128) NOT NULL",
                     "[SavedAtUtc] DATETIME2(7) NOT NULL",
                     "[AppVersion] NVARCHAR(64) NULL",
                     "[OrderCount] INT NULL",
                     "[ReturnQuantity] DECIMAL(18,3) NULL",
                     "[CashSalesAmount] DECIMAL(18,2) NULL",
                     "[CashRefundAmount] DECIMAL(18,2) NULL",
                     "[CashNetAmount] DECIMAL(18,2) NULL",
                     "[CardSalesAmount] DECIMAL(18,2) NULL",
                     "[CardRefundAmount] DECIMAL(18,2) NULL",
                     "[CardNetAmount] DECIMAL(18,2) NULL",
                     "[VoucherSalesAmount] DECIMAL(18,2) NULL",
                     "[VoucherRefundAmount] DECIMAL(18,2) NULL",
                     "[VoucherNetAmount] DECIMAL(18,2) NULL",
                     "[RefundAmount] DECIMAL(18,2) NULL",
                     "[ExpectedCashAmount] DECIMAL(18,2) NULL",
                     "[CountedCashAmount] DECIMAL(18,2) NULL",
                     "[CashDifference] DECIMAL(18,2) NULL",
                     "[NoteSubtotal] DECIMAL(18,2) NULL",
                     "[CoinSubtotal] DECIMAL(18,2) NULL",
                     "[CashCountsJson] NVARCHAR(MAX) NULL",
                     "[ReceivedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_DailyClose_ReceivedAtUtc] DEFAULT (SYSUTCDATETIME())",
                     "[UpdatedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_DailyClose_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME())"
                 })
        {
            Assert.Contains(column, sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Schema_declares_the_three_check_constraints()
    {
        var sql = Flat(SqlSugarDailyCloseSchemaInitializer.EnsureTableSql);

        Assert.Contains("CONSTRAINT [CK_POSM_DailyClose_ClientKind]", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK ([ClientKind] IN (N'Wpf', N'Handheld', N'Ipad'))", sql, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT [CK_POSM_DailyClose_DetailLevel]", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK ([DetailLevel] IN (N'Full', N'CashOnly', N'TraceOnly'))", sql, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT [CK_POSM_DailyClose_DataSource]", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK ([DataSource] IN (N'ClientUpload', N'AuditBackfill'))", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_creates_the_guid_unique_index_and_both_query_indexes_idempotently()
    {
        var sql = Flat(SqlSugarDailyCloseSchemaInitializer.EnsureTableSql);

        Assert.Contains(
            "CREATE UNIQUE INDEX [UX_POSM_DailyClose_Guid] ON [dbo].[POSM_DailyClose] ([DailyCloseGuid]);",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE INDEX [IX_POSM_DailyClose_StoreBusinessDate] ON [dbo].[POSM_DailyClose] ([StoreCode], [BusinessDate] DESC, [DeviceCode], [SavedAtUtc] DESC);",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE INDEX [IX_POSM_DailyClose_BusinessDate] ON [dbo].[POSM_DailyClose] ([BusinessDate] DESC, [SavedAtUtc] DESC);",
            sql,
            StringComparison.Ordinal);

        // 每个索引都带 IF NOT EXISTS 守卫，重复启动不会失败。
        foreach (var indexName in new[]
                 {
                     "UX_POSM_DailyClose_Guid",
                     "IX_POSM_DailyClose_StoreBusinessDate",
                     "IX_POSM_DailyClose_BusinessDate"
                 })
        {
            Assert.Contains($"AND [name] = N'{indexName}')", sql, StringComparison.Ordinal);
        }

        Assert.Equal(3, CountOccurrences(sql, "IF NOT EXISTS ("));
    }

    [Fact]
    public void Schema_does_not_touch_other_tables()
    {
        var sql = SqlSugarDailyCloseSchemaInitializer.EnsureTableSql;

        Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("POSM_LinklySettlement", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Executor_failure_is_not_swallowed_so_startup_stops()
    {
        var initializer = new SqlSugarDailyCloseSchemaInitializer(new ThrowingExecutor());

        await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync());
    }

    [Fact]
    public void AddHbposApiServices_registers_daily_close_sync_repository_and_schema_services()
    {
        var services = new ServiceCollection();

        services.AddHbposApiServices();

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IDailyCloseSyncService) &&
            descriptor.ImplementationType == typeof(DailyCloseSyncService) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IDailyCloseRepository) &&
            descriptor.ImplementationType == typeof(SqlSugarDailyCloseRepository) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IDailyCloseSchemaInitializer) &&
            descriptor.ImplementationType == typeof(SqlSugarDailyCloseSchemaInitializer));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IDailyCloseSchemaSqlExecutor) &&
            descriptor.ImplementationType == typeof(SqlSugarDailyCloseSchemaSqlExecutor));
    }

    // ---- 仓储 SQL 文本 ----

    [Fact]
    public void Select_reads_by_the_globally_unique_guid_without_scope_filter()
    {
        var sql = SqlSugarDailyCloseRepository.SelectByGuidSql;

        Assert.Contains("FROM [dbo].[POSM_DailyClose]", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE [DailyCloseGuid] = @DailyCloseGuid", sql, StringComparison.Ordinal);
        // 不能带 StoreCode / DeviceCode 条件，否则无法识别「Guid 已被别的设备占用」。
        Assert.DoesNotContain("[StoreCode] = @StoreCode", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[DeviceCode] = @DeviceCode", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_names_every_uploaded_column_once_and_binds_matching_parameters()
    {
        var sql = SqlSugarDailyCloseRepository.InsertSql;
        var columns = ExtractColumnList(sql);

        Assert.Equal(columns.Count, columns.Distinct().Count());
        Assert.Equal(35, columns.Count);
        Assert.DoesNotContain("Id", columns);
        foreach (var column in columns)
        {
            // 每一列都要有同名参数；用边界断言避免 @CashNetAmount 被更长的参数名误匹配。
            Assert.Matches($@"@{column}(?![A-Za-z0-9_])", sql);
        }

        Assert.Contains("[dbo].[POSM_DailyClose]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Replace_placeholder_updates_by_guid_and_scope_and_guards_against_full_rows()
    {
        var sql = SqlSugarDailyCloseRepository.ReplacePlaceholderSql;

        Assert.Contains("UPDATE [dbo].[POSM_DailyClose]", sql, StringComparison.Ordinal);
        Assert.Contains("[DetailLevel] = N'Full'", sql, StringComparison.Ordinal);
        Assert.Contains("[DataSource] = N'ClientUpload'", sql, StringComparison.Ordinal);
        Assert.Contains("[BackfillBatch] = NULL", sql, StringComparison.Ordinal);
        Assert.Contains("[BusinessDateInferred] = 0", sql, StringComparison.Ordinal);
        Assert.Contains("[CashCountsJson] = @CashCountsJson", sql, StringComparison.Ordinal);
        Assert.Contains("[ReceivedAtUtc] = @ReceivedAtUtc", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE [DailyCloseGuid] = @DailyCloseGuid", sql, StringComparison.Ordinal);
        Assert.Contains("AND [StoreCode] = @StoreCode", sql, StringComparison.Ordinal);
        Assert.Contains("AND [DeviceCode] = @DeviceCode", sql, StringComparison.Ordinal);
        // 并发守卫：只有仍是占位（非 Full）的行才会被覆盖。
        Assert.Contains("AND [DetailLevel] <> N'Full'", sql, StringComparison.Ordinal);

        // 归属字段与主键不允许被覆盖改写。
        var setClause = sql[..sql.IndexOf("WHERE", StringComparison.Ordinal)];
        Assert.DoesNotContain("[StoreCode] =", setClause, StringComparison.Ordinal);
        Assert.DoesNotContain("[DeviceCode] =", setClause, StringComparison.Ordinal);
        Assert.DoesNotContain("[DailyCloseGuid] =", setClause, StringComparison.Ordinal);
    }

    [Fact]
    public void Replace_placeholder_sets_exactly_the_columns_that_insert_writes_minus_identity_columns()
    {
        var inserted = ExtractColumnList(SqlSugarDailyCloseRepository.InsertSql)
            .Except(["DailyCloseGuid", "StoreCode", "DeviceCode"])
            .OrderBy(column => column, StringComparer.Ordinal)
            .ToArray();
        var setClause = SqlSugarDailyCloseRepository.ReplacePlaceholderSql;
        setClause = setClause[setClause.IndexOf("SET", StringComparison.Ordinal)..setClause.IndexOf("WHERE", StringComparison.Ordinal)];
        var updated = System.Text.RegularExpressions.Regex.Matches(setClause, @"\[(\w+)\] =")
            .Select(match => match.Groups[1].Value)
            .OrderBy(column => column, StringComparer.Ordinal)
            .ToArray();

        // 覆盖时漏掉任何一列都会让「占位被替换」后残留回填数据。
        Assert.Equal(inserted, updated);
    }

    [Fact]
    public void Unique_violation_detection_only_matches_sql_server_duplicate_key_failures()
    {
        Assert.True(SqlSugarDailyCloseRepository.IsUniqueConstraintViolation(
            new InvalidOperationException(
                "wrapped",
                new InvalidOperationException("Cannot insert duplicate key row in object 'dbo.POSM_DailyClose' with unique index 'UX_POSM_DailyClose_Guid'."))));
        Assert.False(SqlSugarDailyCloseRepository.IsUniqueConstraintViolation(
            new TimeoutException("Execution Timeout Expired.")));
        Assert.False(SqlSugarDailyCloseRepository.IsUniqueConstraintViolation(
            new InvalidOperationException("The UPDATE statement conflicted with the CHECK constraint \"CK_POSM_DailyClose_ClientKind\".")));
    }

    private static List<string> ExtractColumnList(string insertSql)
    {
        var start = insertSql.IndexOf('(') + 1;
        var end = insertSql.IndexOf(')', start);
        return insertSql[start..end]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(column => column.Trim('[', ']', ' ', '\n', '\r'))
            .ToList();
    }

    /// <summary>把连续空白折叠成单个空格，断言不依赖原始 SQL 的缩进与换行。</summary>
    private static string Flat(string sql) =>
        System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ");

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private sealed class CapturingExecutor : IDailyCloseSchemaSqlExecutor
    {
        public List<string> Commands { get; } = [];

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            Commands.Add(sql);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingExecutor : IDailyCloseSchemaSqlExecutor
    {
        public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("schema failed");
    }
}

/// <summary>
/// 给会真正执行 Program 启动流程、又配置了 PosmConnection 的测试宿主用：避免在没有 SQL Server 的机器上建表。
/// </summary>
internal sealed class TestNoOpDailyCloseSchemaInitializer : IDailyCloseSchemaInitializer
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
