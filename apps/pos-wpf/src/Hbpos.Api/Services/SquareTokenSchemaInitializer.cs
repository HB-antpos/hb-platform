using Hbpos.Api.Data;

namespace Hbpos.Api.Services;

public interface ISquareTokenSchemaInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface ISquareTokenSchemaSqlExecutor
{
    Task ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}

public sealed class SqlSugarSquareTokenSchemaInitializer(
    ISquareTokenSchemaSqlExecutor sqlExecutor) : ISquareTokenSchemaInitializer
{
    internal const string EnsureTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_SquareToken]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_SquareToken] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_SquareToken] PRIMARY KEY,
                [Environment] NVARCHAR(32) NOT NULL,
                [AccessToken] NVARCHAR(2048) NOT NULL,
                [IsEnabled] BIT NOT NULL CONSTRAINT [DF_POSM_SquareToken_IsEnabled] DEFAULT (0),
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_SquareToken_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedBy] NVARCHAR(128) NULL,
                CONSTRAINT [CK_POSM_SquareToken_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox'))
            );
        END;
        """;

    // StoreCode 为 NULL 表示「全部门店共用」，既有生产行自动成为共用 token，无需数据迁移。
    internal const string EnsureStoreCodeColumnSql = """
        IF COL_LENGTH(N'[dbo].[POSM_SquareToken]', N'StoreCode') IS NULL
        BEGIN
            ALTER TABLE [dbo].[POSM_SquareToken] ADD [StoreCode] NVARCHAR(32) NULL;
        END;
        """;

    // 旧索引只允许每个环境一条启用行，会挡住门店专用 token，需先移除再换成按 (环境, 门店) 唯一。
    internal const string DropLegacyEnabledTokenIndexSql = """
        IF EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = N'UX_POSM_SquareToken_Environment_Enabled'
              AND object_id = OBJECT_ID(N'[dbo].[POSM_SquareToken]')
        )
        BEGIN
            DROP INDEX [UX_POSM_SquareToken_Environment_Enabled] ON [dbo].[POSM_SquareToken];
        END;
        """;

    // SQL Server 唯一索引把 NULL 视为同一个值：每个环境最多一条共用行，每家门店每个环境最多一条专用行。
    internal const string EnsureEnabledTokenIndexSql = """
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = N'UX_POSM_SquareToken_Environment_Store_Enabled'
              AND object_id = OBJECT_ID(N'[dbo].[POSM_SquareToken]')
        )
        BEGIN
            CREATE UNIQUE INDEX [UX_POSM_SquareToken_Environment_Store_Enabled]
            ON [dbo].[POSM_SquareToken]([Environment], [StoreCode])
            WHERE [IsEnabled] = 1;
        END;
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // 加列与建索引必须分批执行：同一批里引用刚加的列会在编译期报「列名无效」。
        await sqlExecutor.ExecuteAsync(EnsureTableSql, cancellationToken);
        await sqlExecutor.ExecuteAsync(EnsureStoreCodeColumnSql, cancellationToken);
        await sqlExecutor.ExecuteAsync(DropLegacyEnabledTokenIndexSql, cancellationToken);
        await sqlExecutor.ExecuteAsync(EnsureEnabledTokenIndexSql, cancellationToken);
    }
}

public sealed class SqlSugarSquareTokenSchemaSqlExecutor(
    HbposSqlSugarContext dbContext) : ISquareTokenSchemaSqlExecutor
{
    public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(sql);
    }
}
