using Hbpos.Api.Services;

namespace Hbpos.Api.Tests;

public sealed class StoreSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_executes_idempotent_contact_email_column_ddl()
    {
        var executor = new CapturingStoreSchemaSqlExecutor();
        var initializer = new SqlSugarStoreSchemaInitializer(executor);

        await initializer.InitializeAsync();

        var sql = Assert.Single(executor.SqlStatements);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[Store]', N'U') IS NOT NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.Store', N'ContactEmail') IS NULL", sql);
        Assert.Contains("ALTER TABLE [dbo].[Store]", sql);
        Assert.Contains("ADD [ContactEmail] NVARCHAR(100) NULL", sql);
    }

    [Fact]
    public async Task InitializeAsync_adds_the_pos_api_entry_columns_idempotently_without_a_check_constraint()
    {
        var executor = new CapturingStoreSchemaSqlExecutor();

        await new SqlSugarStoreSchemaInitializer(executor).InitializeAsync();

        var sql = Assert.Single(executor.SqlStatements);
        Assert.Contains("COL_LENGTH(N'dbo.Store', N'PosApiEntryMode') IS NULL", sql);
        Assert.Contains("ADD [PosApiEntryMode] NVARCHAR(16) NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.Store', N'PosApiEntryUpdatedAt') IS NULL", sql);
        Assert.Contains("ADD [PosApiEntryUpdatedAt] DATETIME2 NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.Store', N'PosApiEntryUpdatedBy') IS NULL", sql);
        Assert.Contains("ADD [PosApiEntryUpdatedBy] NVARCHAR(64) NULL", sql);
        // 约束只由正式迁移 20261024.003 添加；启动补列不建约束，也不回填旧行。
        Assert.DoesNotContain("CONSTRAINT", SqlSugarStoreSchemaInitializer.EnsurePosApiEntryColumnsSql);
        Assert.DoesNotContain("UPDATE", SqlSugarStoreSchemaInitializer.EnsurePosApiEntryColumnsSql);
    }

    private sealed class CapturingStoreSchemaSqlExecutor : IStoreSchemaSqlExecutor
    {
        public List<string> SqlStatements { get; } = [];

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            SqlStatements.Add(sql);
            return Task.CompletedTask;
        }
    }
}
