using Hbpos.Api.Services;

namespace Hbpos.Api.Tests;

public sealed class CardTenderReconciliationSchemaInitializerTests
{
    [Fact]
    public void Backend_schema_script_creates_the_issue_table_idempotently()
    {
        var sql = SqlSugarLinklyCloudBackendAsyncSchemaInitializer.EnsureTableSql;

        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_CardTenderReconciliationIssue]', N'U') IS NULL", sql);
        Assert.Contains("[DedupKey] NVARCHAR(256) NOT NULL", sql);
        Assert.Contains("UX_POSM_CardTenderReconciliationIssue_DedupKey", sql);
        Assert.Contains("CHECK ([Status] IN (N'Open', N'Resolved', N'Dismissed'))", sql);
        Assert.Contains("IX_POSM_CardTenderReconciliationIssue_Status", sql);
    }

    [Fact]
    public void Session_table_gets_a_nullable_OrderGuid_column_in_both_create_and_upgrade_paths()
    {
        var sql = SqlSugarLinklyCloudBackendAsyncSchemaInitializer.EnsureTableSql;

        Assert.Contains("[OrderGuid] NVARCHAR(50) NULL,", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'OrderGuid') IS NULL", sql);
        Assert.Contains("ADD [OrderGuid] NVARCHAR(50) NULL;", sql);
    }

    [Fact]
    public void Repository_sql_upserts_by_dedup_key_and_never_reopens_dismissed_issues()
    {
        var sql = SqlSugarCardTenderReconciliationRepository.UpsertIssueSql;

        Assert.Contains("MERGE [dbo].[POSM_CardTenderReconciliationIssue] WITH (HOLDLOCK)", sql);
        Assert.Contains("target.[DedupKey] = source.[DedupKey]", sql);
        Assert.Contains("WHEN target.[Status] = N'Dismissed' THEN target.[Status]", sql);
        Assert.Contains("[OccurrenceCount] = target.[OccurrenceCount] + 1", sql);
    }

    [Fact]
    public void Link_session_sql_only_fills_an_empty_order_guid()
    {
        var sql = SqlSugarCardTenderReconciliationRepository.LinkSessionSql;

        Assert.Contains("WHERE [Id] = @Id AND [OrderGuid] IS NULL", sql);
    }
}

public sealed class CardTenderReconciliationSqlServerFactAttribute : FactAttribute
{
    public CardTenderReconciliationSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DailyCloseSqlServerFactAttribute.ConnectionVariable)))
        {
            Skip = "未配置隔离 SQL Server，跳过卡付款对账真实 SQL 测试。";
        }
    }
}
