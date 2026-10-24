using Hbpos.Api.Data;

namespace Hbpos.Api.Services;

public interface IStoreSchemaInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IStoreSchemaSqlExecutor
{
    Task ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}

public sealed class SqlSugarStoreSchemaInitializer(
    IStoreSchemaSqlExecutor sqlExecutor) : IStoreSchemaInitializer
{
    // 共享 Store 实体已包含 ContactEmail；POS API 启动时补齐旧库列，避免默认查询全字段时报“列名无效”。
    internal const string EnsureContactEmailColumnSql = """
        IF OBJECT_ID(N'[dbo].[Store]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.Store', N'ContactEmail') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Store]
                ADD [ContactEmail] NVARCHAR(100) NULL;
        END;
        """;

    // 共享 Store 实体将新增 ReturnPolicy；启动时幂等补齐旧库列，不回填任何旧行。
    internal const string EnsureReturnPolicyColumnSql = """
        IF OBJECT_ID(N'[dbo].[Store]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.Store', N'ReturnPolicy') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Store]
                ADD [ReturnPolicy] NVARCHAR(500) NULL;
        END;
        """;

    // 退款代金券「使用说明」与分期小票「分期条款」的门店级正文（多行纯文本，上限 600，可空＝按内置默认文案）；
    // 启动时幂等补齐旧库列，不回填任何旧行。Release 表的同名列由 HBweb 迁移添加，Hbpos.Api 不建不改 Release 表。
    internal const string EnsureVoucherTermsColumnSql = """
        IF OBJECT_ID(N'[dbo].[Store]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.Store', N'VoucherTerms') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Store]
                ADD [VoucherTerms] NVARCHAR(600) NULL;
        END;
        """;

    internal const string EnsureInstallmentTermsColumnSql = """
        IF OBJECT_ID(N'[dbo].[Store]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.Store', N'InstallmentTerms') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Store]
                ADD [InstallmentTerms] NVARCHAR(600) NULL;
        END;
        """;

    // 后台按分店设置收银端 POS API 首选入口（edge / aws / direct，NULL＝未设置）的三列；与 VoucherTerms 同样的处理方式：
    // 启动时幂等补齐旧库列，不回填任何旧行。**迁移须先于代码上线**：正式迁移 20261024.003-store-pos-api-entry
    // 由 HBweb 主 API 的 --schema=migrate 执行，它还会加 CHECK 约束 CK_Store_PosApiEntryMode（只允许 NULL/edge/aws/direct）；
    // 这里只补列、不建约束，所以先发 Hbpos.Api 也不会因列缺失而 500，但约束要等迁移执行后才有。
    internal const string EnsurePosApiEntryColumnsSql = """
        IF OBJECT_ID(N'[dbo].[Store]', N'U') IS NOT NULL
        BEGIN
            IF COL_LENGTH(N'dbo.Store', N'PosApiEntryMode') IS NULL
                ALTER TABLE [dbo].[Store] ADD [PosApiEntryMode] NVARCHAR(16) NULL;
            IF COL_LENGTH(N'dbo.Store', N'PosApiEntryUpdatedAt') IS NULL
                ALTER TABLE [dbo].[Store] ADD [PosApiEntryUpdatedAt] DATETIME2 NULL;
            IF COL_LENGTH(N'dbo.Store', N'PosApiEntryUpdatedBy') IS NULL
                ALTER TABLE [dbo].[Store] ADD [PosApiEntryUpdatedBy] NVARCHAR(64) NULL;
        END;
        """;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return sqlExecutor.ExecuteAsync(
            EnsureContactEmailColumnSql + "\n" + EnsureReturnPolicyColumnSql
                + "\n" + EnsureVoucherTermsColumnSql + "\n" + EnsureInstallmentTermsColumnSql
                + "\n" + EnsurePosApiEntryColumnsSql,
            cancellationToken);
    }
}

public sealed class SqlSugarStoreSchemaSqlExecutor(
    HbposSqlSugarContext dbContext) : IStoreSchemaSqlExecutor
{
    public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        return dbContext.MainDb.Ado.ExecuteCommandAsync(sql);
    }
}
