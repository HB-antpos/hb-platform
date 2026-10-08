using Hbpos.Api.Services;

namespace Hbpos.Api.Tests;

public sealed class StoreReceiptProfileSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_executes_idempotent_return_policy_column_ddl()
    {
        var executor = new CapturingStoreSchemaSqlExecutor();
        var initializer = new SqlSugarStoreSchemaInitializer(executor);

        await initializer.InitializeAsync();

        var sql = Assert.Single(executor.SqlStatements);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[Store]', N'U') IS NOT NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.Store', N'ReturnPolicy') IS NULL", sql);
        Assert.Contains("ALTER TABLE [dbo].[Store]", sql);
        Assert.Contains("ADD [ReturnPolicy] NVARCHAR(500) NULL", sql);
    }

    [Fact]
    public async Task InitializeAsync_executes_idempotent_voucher_and_installment_terms_column_ddl()
    {
        var executor = new CapturingStoreSchemaSqlExecutor();
        var initializer = new SqlSugarStoreSchemaInitializer(executor);

        await initializer.InitializeAsync();

        // 与既有补列并在同一批里执行（一次往返），两列都是可空 NVARCHAR(600)，已存在则不动。
        var sql = Assert.Single(executor.SqlStatements);
        Assert.Contains("COL_LENGTH(N'dbo.Store', N'VoucherTerms') IS NULL", sql);
        Assert.Contains("ADD [VoucherTerms] NVARCHAR(600) NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.Store', N'InstallmentTerms') IS NULL", sql);
        Assert.Contains("ADD [InstallmentTerms] NVARCHAR(600) NULL", sql);
        // 既有的两列补齐不能被挤掉。
        Assert.Contains("ADD [ContactEmail] NVARCHAR(100) NULL", sql);
        Assert.Contains("ADD [ReturnPolicy] NVARCHAR(500) NULL", sql);
    }

    [Fact]
    public async Task InitializeAsync_does_not_touch_the_release_table()
    {
        var executor = new CapturingStoreSchemaSqlExecutor();
        var initializer = new SqlSugarStoreSchemaInitializer(executor);

        await initializer.InitializeAsync();

        // Release 表（含新列）由 HBweb 迁移负责，Hbpos.Api 不建不改。
        var sql = Assert.Single(executor.SqlStatements);
        Assert.DoesNotContain("StoreReceiptProfileRelease", sql);
    }

    [Fact]
    public async Task InitializeAsync_does_not_backfill_existing_rows()
    {
        var executor = new CapturingStoreSchemaSqlExecutor();
        var initializer = new SqlSugarStoreSchemaInitializer(executor);

        await initializer.InitializeAsync();

        var sql = Assert.Single(executor.SqlStatements);
        Assert.DoesNotContain("UPDATE [dbo].[Store]", sql);
        Assert.DoesNotContain("SET [ReturnPolicy]", sql);
        Assert.DoesNotContain("SET [VoucherTerms]", sql);
        Assert.DoesNotContain("SET [InstallmentTerms]", sql);
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
