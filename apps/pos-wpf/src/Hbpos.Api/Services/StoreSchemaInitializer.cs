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

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return sqlExecutor.ExecuteAsync(
            EnsureContactEmailColumnSql + "\n" + EnsureReturnPolicyColumnSql
                + "\n" + EnsureVoucherTermsColumnSql + "\n" + EnsureInstallmentTermsColumnSql,
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
