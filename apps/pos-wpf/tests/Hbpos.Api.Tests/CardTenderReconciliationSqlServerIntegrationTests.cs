using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

/// <summary>
/// 真实 SQL Server 上验证对账表建表脚本幂等、会话表 OrderGuid 迁移，以及仓储的查找、回链与异常登记 SQL。
/// 每个用例自建独立数据库并在结束时删除，库名只来自本用例生成的 GUID。
/// </summary>
[Trait("Category", "SQL")]
public sealed class CardTenderReconciliationSqlServerIntegrationTests : IAsyncLifetime
{
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

        database = $"HbCardTender_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        masterConnection = builder.ConnectionString;
        await ExecuteAtAsync(masterConnection, $"CREATE DATABASE [{database}]");
        builder.InitialCatalog = database;
        connection = builder.ConnectionString;
        var context = CreateContext();
        await new SqlSugarLinklyCloudBackendAsyncSchemaInitializer(
            new SqlSugarLinklyCloudBackendAsyncSchemaSqlExecutor(context)).InitializeAsync();
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

    [CardTenderReconciliationSqlServerFact]
    public async Task Schema_initializers_are_idempotent_and_session_table_has_nullable_OrderGuid()
    {
        var context = CreateContext();
        await new SqlSugarLinklyCloudBackendAsyncSchemaInitializer(
            new SqlSugarLinklyCloudBackendAsyncSchemaSqlExecutor(context)).InitializeAsync();

        Assert.Equal(
            1L,
            await ScalarAsync(
                "SELECT COUNT_BIG(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]') AND [name] = N'OrderGuid' AND is_nullable = 1;"));
    }

    [CardTenderReconciliationSqlServerFact]
    public async Task Upgrade_adds_OrderGuid_to_an_existing_session_table_without_touching_rows()
    {
        await ExecuteAtAsync(connection, "ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession] DROP COLUMN [OrderGuid];");
        await InsertSessionAsync("S01", "POS01", "sess-1");

        await new SqlSugarLinklyCloudBackendAsyncSchemaInitializer(
            new SqlSugarLinklyCloudBackendAsyncSchemaSqlExecutor(CreateContext())).InitializeAsync();

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[POSM_LinklyCloudBackendSession] WHERE [OrderGuid] IS NULL;"));
    }

    [CardTenderReconciliationSqlServerFact]
    public async Task Repository_finds_sessions_in_store_then_falls_back_to_any_store()
    {
        await InsertSessionAsync("S01", "POS01", "sess-1");
        await InsertSessionAsync("S02", "POS02", "sess-2");
        var repository = new SqlSugarCardTenderReconciliationRepository(CreateContext());

        var inStore = await repository.FindSessionsAsync("Sandbox", "S01", "sess-1", CancellationToken.None);
        var crossStore = await repository.FindSessionsAsync("Sandbox", "S01", "sess-2", CancellationToken.None);
        var missing = await repository.FindSessionsAsync("Production", "S01", "sess-1", CancellationToken.None);

        var found = Assert.Single(inStore);
        Assert.Equal("S01", found.StoreCode);
        Assert.Equal("Completed", found.Status);
        Assert.True(found.TransactionSuccess);
        Assert.Equal(1250, found.RequestAmountCents);
        Assert.Equal("S02", Assert.Single(crossStore).StoreCode);
        Assert.Empty(missing);
    }

    [CardTenderReconciliationSqlServerFact]
    public async Task Link_session_keeps_the_first_order_and_reports_the_existing_one()
    {
        await InsertSessionAsync("S01", "POS01", "sess-1");
        var repository = new SqlSugarCardTenderReconciliationRepository(CreateContext());
        var session = Assert.Single(await repository.FindSessionsAsync("Sandbox", "S01", "sess-1", CancellationToken.None));
        var first = Guid.NewGuid().ToString("D");

        var firstLink = await repository.TryLinkSessionToOrderAsync(session.Id, first, CancellationToken.None);
        var secondLink = await repository.TryLinkSessionToOrderAsync(session.Id, Guid.NewGuid().ToString("D"), CancellationToken.None);
        var repeatLink = await repository.TryLinkSessionToOrderAsync(session.Id, first, CancellationToken.None);

        Assert.Equal(first, firstLink);
        Assert.Equal(first, secondLink);
        Assert.Equal(first, repeatLink);
    }

    [CardTenderReconciliationSqlServerFact]
    public async Task Upsert_issues_merges_repeats_and_does_not_reopen_dismissed_issues()
    {
        var repository = new SqlSugarCardTenderReconciliationRepository(CreateContext());
        var issue = new CardTenderIssue(
            CardTenderIssueTypes.SessionNotApproved, CardTenderIssueSeverities.Error, CardTenderIssueSources.OrderSync,
            "S01", "POS01", "Sandbox", "sess-1", "2610090001", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 12.5m, "first");

        await repository.UpsertIssuesAsync([issue], CancellationToken.None);
        await repository.UpsertIssuesAsync([issue with { Detail = "second" }], CancellationToken.None);

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[POSM_CardTenderReconciliationIssue];"));
        Assert.Equal(2L, await ScalarAsync("SELECT CAST([OccurrenceCount] AS BIGINT) FROM [dbo].[POSM_CardTenderReconciliationIssue];"));
        Assert.Equal("second", await StringAsync("SELECT [Detail] FROM [dbo].[POSM_CardTenderReconciliationIssue];"));

        await ExecuteAtAsync(connection, "UPDATE [dbo].[POSM_CardTenderReconciliationIssue] SET [Status] = N'Resolved', [ResolvedAt] = SYSUTCDATETIME();");
        await repository.UpsertIssuesAsync([issue], CancellationToken.None);
        Assert.Equal("Open", await StringAsync("SELECT [Status] FROM [dbo].[POSM_CardTenderReconciliationIssue];"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[POSM_CardTenderReconciliationIssue] WHERE [ResolvedAt] IS NOT NULL;"));

        await ExecuteAtAsync(connection, "UPDATE [dbo].[POSM_CardTenderReconciliationIssue] SET [Status] = N'Dismissed';");
        await repository.UpsertIssuesAsync([issue], CancellationToken.None);
        Assert.Equal("Dismissed", await StringAsync("SELECT [Status] FROM [dbo].[POSM_CardTenderReconciliationIssue];"));
    }

    [CardTenderReconciliationSqlServerFact]
    public async Task Verifier_end_to_end_links_session_and_records_issue_for_unapproved_session()
    {
        await InsertSessionAsync("S01", "POS01", "sess-1", status: "SupervisorResolved", success: false);
        var verifier = new CardTenderOrderVerifier(new SqlSugarCardTenderReconciliationRepository(CreateContext()));
        var payment = new PaymentSyncDto(
            Guid.NewGuid(),
            PaymentMethodKind.Card,
            12.50m,
            LinklyBackendPaymentReference.Format("2610090001", "sess-1", "Sandbox", null),
            CardTransactions: [CardTenderTestData.Transaction(12.50m)]);
        var request = CardTenderTestData.Request(payment);

        await verifier.VerifyAsync(request);

        Assert.Equal(request.OrderGuid.ToString("D"), await StringAsync("SELECT [OrderGuid] FROM [dbo].[POSM_LinklyCloudBackendSession];"));
        Assert.Equal(CardTenderIssueTypes.SessionNotApproved, await StringAsync("SELECT [IssueType] FROM [dbo].[POSM_CardTenderReconciliationIssue];"));
    }

    private Task InsertSessionAsync(string store, string device, string sessionId, string status = "Completed", bool success = true)
    {
        return ExecuteAtAsync(
            connection,
            $"""
            INSERT INTO [dbo].[POSM_LinklyCloudBackendSession]
                ([Environment], [StoreCode], [DeviceCode], [SessionId], [Status], [TxnRef], [RequestTxnType], [RequestAmountCents],
                 [TransactionSuccess], [OperationType], [IsActive])
            VALUES (N'Sandbox', N'{store}', N'{device}', N'{sessionId}', N'{status}', N'2610090001', N'P', 1250,
                    {(success ? 1 : 0)}, N'Transaction', 0);
            """);
    }

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

    private async Task<long> ScalarAsync(string sql) => Convert.ToInt64(await ScalarObjectAsync(sql));

    private async Task<string?> StringAsync(string sql) => Convert.ToString(await ScalarObjectAsync(sql));

    private async Task<object?> ScalarObjectAsync(string sql)
    {
        await using var sqlConnection = new SqlConnection(connection);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection) { CommandTimeout = 60 };
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAtAsync(string connectionString, string sql)
    {
        await using var sqlConnection = new SqlConnection(connectionString);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }
}
