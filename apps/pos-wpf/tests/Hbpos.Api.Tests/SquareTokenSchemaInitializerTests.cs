using Hbpos.Api.Services;

namespace Hbpos.Api.Tests;

public sealed class SquareTokenSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_executes_idempotent_table_and_index_ddl()
    {
        var executor = new CapturingSquareTokenSchemaSqlExecutor();
        var initializer = new SqlSugarSquareTokenSchemaInitializer(executor);

        await initializer.InitializeAsync();

        // 建表、加列、移除旧索引、建新索引各自独立成批。
        Assert.Equal(4, executor.SqlStatements.Count);
        var combinedSql = string.Join(Environment.NewLine, executor.SqlStatements);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_SquareToken]', N'U') IS NULL", combinedSql);
        Assert.Contains("[Id] BIGINT IDENTITY(1,1)", combinedSql);
        Assert.Contains("[Environment] NVARCHAR(32) NOT NULL", combinedSql);
        Assert.Contains("[AccessToken] NVARCHAR(2048) NOT NULL", combinedSql);
        Assert.Contains("[IsEnabled] BIT NOT NULL", combinedSql);
        Assert.Contains("[UpdatedAt] DATETIME2(7) NOT NULL", combinedSql);
        Assert.Contains("[UpdatedBy] NVARCHAR(128) NULL", combinedSql);
        Assert.Contains("CHECK ([Environment] IN (N'Production', N'Sandbox'))", combinedSql);
        Assert.Contains("WHERE [IsEnabled] = 1", combinedSql);
    }

    [Fact]
    public async Task InitializeAsync_adds_nullable_store_column_and_replaces_environment_only_unique_index()
    {
        var executor = new CapturingSquareTokenSchemaSqlExecutor();
        var initializer = new SqlSugarSquareTokenSchemaInitializer(executor);

        await initializer.InitializeAsync();

        // 加列必须先于任何引用 [StoreCode] 的索引语句，且各占一批，否则同批编译会报列名无效。
        var columnIndex = executor.SqlStatements.FindIndex(sql => sql.Contains("ADD [StoreCode] NVARCHAR(32) NULL"));
        var dropIndex = executor.SqlStatements.FindIndex(sql => sql.Contains("DROP INDEX [UX_POSM_SquareToken_Environment_Enabled]"));
        var createIndex = executor.SqlStatements.FindIndex(sql =>
            sql.Contains("CREATE UNIQUE INDEX [UX_POSM_SquareToken_Environment_Store_Enabled]")
            && sql.Contains("([Environment], [StoreCode])")
            && sql.Contains("WHERE [IsEnabled] = 1"));

        Assert.True(columnIndex >= 0, "缺少 StoreCode 可空列 DDL");
        Assert.True(dropIndex >= 0, "缺少旧唯一索引的移除 DDL");
        Assert.True(createIndex > columnIndex && createIndex > dropIndex, "新唯一索引必须在加列和移除旧索引之后创建");
    }

    private sealed class CapturingSquareTokenSchemaSqlExecutor : ISquareTokenSchemaSqlExecutor
    {
        public List<string> SqlStatements { get; } = [];

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            SqlStatements.Add(sql);
            return Task.CompletedTask;
        }
    }
}
