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
    public async Task Upgrade_adds_OrderGuid_and_CompletedAt_and_leaves_historical_sessions_out_of_reconciliation()
    {
        // 还原成迁移前的表结构：去掉过滤索引、默认值约束和三个新增列。
        await ExecuteAtAsync(
            connection,
            """
            DROP INDEX [IX_POSM_LinklyCloudBackendSession_UnlinkedApproved] ON [dbo].[POSM_LinklyCloudBackendSession];
            ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession] DROP CONSTRAINT [DF_POSM_LinklyCloudBackendSession_CreatedAt];
            ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession] DROP COLUMN [OrderGuid], [CreatedAt], [CompletedAt];
            """);
        await InsertSessionAsync("S01", "POS01", "legacy");

        var initializer = new SqlSugarLinklyCloudBackendAsyncSchemaInitializer(
            new SqlSugarLinklyCloudBackendAsyncSchemaSqlExecutor(CreateContext()));
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        // 历史行：CreatedAt 被升级迁移用 UpdatedAt 回填，OrderGuid 与 CompletedAt 保持 NULL。
        Assert.Equal(
            1L,
            await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[POSM_LinklyCloudBackendSession] WHERE [OrderGuid] IS NULL AND [CompletedAt] IS NULL AND [CreatedAt] = [UpdatedAt];"));
        // 因为 CompletedAt 为 NULL，已批准的历史会话不会被对账当成孤儿（否则上线后会全量误报）。
        var legacyScan = await new SqlSugarCardTenderReconciliationRepository(CreateContext())
            .FindApprovedSessionsWithoutOrderAsync(DateTime.UtcNow.AddYears(-5), DateTime.UtcNow.AddMinutes(1), 100, CancellationToken.None);
        Assert.Empty(legacyScan);
        await InsertSessionAsync("S01", "POS01", "fresh");
        Assert.Equal(
            1L,
            await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[POSM_LinklyCloudBackendSession] WHERE [SessionId] = N'fresh' AND [CreatedAt] IS NOT NULL;"));
        Assert.Equal(
            1L,
            await ScalarAsync("SELECT COUNT_BIG(*) FROM sys.indexes WHERE [name] = N'IX_POSM_LinklyCloudBackendSession_UnlinkedApproved' AND has_filter = 1;"));
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
        await InsertSessionAsync("S01", "POS01", "sess-1", status: "SupervisorResolved", success: false, txnRef: "2610090001");
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

    [CardTenderReconciliationSqlServerFact]
    public async Task Upsert_session_stamps_CreatedAt_on_insert_and_CompletedAt_only_once_on_first_terminal_status()
    {
        var repository = new SqlSugarLinklyCloudBackendAsyncRepository(CreateContext());
        var pendingAt = new DateTimeOffset(2026, 10, 9, 1, 0, 0, TimeSpan.Zero);
        var completedAt = pendingAt.AddMinutes(2);
        var ackAt = pendingAt.AddMinutes(30);

        await repository.UpsertSessionAsync(Session("sess-1", "Pending", pendingAt), CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[POSM_LinklyCloudBackendSession] WHERE [CreatedAt] IS NOT NULL AND [CompletedAt] IS NULL;"));

        await repository.UpsertSessionAsync(Session("sess-1", "Completed", completedAt), CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync($"SELECT COUNT_BIG(*) FROM [dbo].[POSM_LinklyCloudBackendSession] WHERE [CompletedAt] = '{completedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}';"));

        // ack/回执打印会再次 upsert 并刷新 UpdatedAt，CompletedAt 必须保持首次进入终态的时间。
        await repository.UpsertSessionAsync(Session("sess-1", "Completed", ackAt), CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync($"SELECT COUNT_BIG(*) FROM [dbo].[POSM_LinklyCloudBackendSession] WHERE [CompletedAt] = '{completedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}' AND [UpdatedAt] = '{ackAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}';"));
    }

    [CardTenderReconciliationSqlServerFact]
    public async Task Orphan_query_returns_only_old_enough_approved_unlinked_sessions_completed_after_the_migration()
    {
        var now = DateTime.UtcNow;
        await InsertSessionAsync("S01", "POS01", "orphan-old", completedAt: now.AddHours(-5));
        await InsertSessionAsync("S01", "POS01", "orphan-older", completedAt: now.AddHours(-9));
        await InsertSessionAsync("S01", "POS01", "too-recent", completedAt: now.AddMinutes(-10));
        await InsertSessionAsync("S01", "POS01", "outside-lookback", completedAt: now.AddDays(-30));
        await InsertSessionAsync("S01", "POS01", "legacy-no-completed-at", completedAt: null, updatedAt: now.AddHours(-5));
        await InsertSessionAsync("S01", "POS01", "linked", completedAt: now.AddHours(-5), orderGuid: Guid.NewGuid().ToString("D"));
        await InsertSessionAsync("S01", "POS01", "declined", completedAt: now.AddHours(-5), status: "Completed", success: false);
        await InsertSessionAsync("S01", "POS01", "supervisor", completedAt: now.AddHours(-5), status: "SupervisorResolved", success: true);
        var repository = new SqlSugarCardTenderReconciliationRepository(CreateContext());

        var found = await repository.FindApprovedSessionsWithoutOrderAsync(
            now.AddDays(-14), now.AddMinutes(-60), 10, CancellationToken.None);

        Assert.Equal(["orphan-older", "orphan-old"], found.Select(candidate => candidate.SessionId));
        Assert.Equal(1250, found[0].RequestAmountCents);
        Assert.Single(await repository.FindApprovedSessionsWithoutOrderAsync(now.AddDays(-14), now.AddMinutes(-60), 1, CancellationToken.None));
    }

    [CardTenderReconciliationSqlServerFact]
    public async Task Orphan_query_ignores_approved_sessions_without_CompletedAt_even_when_UpdatedAt_is_old()
    {
        var now = DateTime.UtcNow;
        await InsertSessionAsync("S01", "POS01", "no-completed-at", completedAt: null, updatedAt: now.AddHours(-3));
        var repository = new SqlSugarCardTenderReconciliationRepository(CreateContext());

        var found = await repository.FindApprovedSessionsWithoutOrderAsync(
            now.AddDays(-14), now.AddMinutes(-60), 10, CancellationToken.None);

        Assert.Empty(found);
    }

    [CardTenderReconciliationSqlServerFact]
    public async Task Find_order_by_backend_payment_matches_session_and_environment_with_wildcards_escaped()
    {
        await CreatePaymentDetailTableAsync();
        var orderGuid = Guid.NewGuid().ToString("D");
        var reference = LinklyBackendPaymentReference.Format("2610090001", "sess_1%", "Production", "RFN 1");
        await ExecuteAtAsync(connection, $"INSERT INTO [dbo].[payment_detail] VALUES (N'p1', N'{orderGuid}', N'{reference}'), (N'p2', N'other', N'ANZBACKEND:1:session=sessX1:environment=Production');");
        var repository = new SqlSugarCardTenderReconciliationRepository(CreateContext());

        Assert.Equal(orderGuid, await repository.FindOrderGuidByBackendPaymentAsync("Production", "sess_1%", CancellationToken.None));
        // _ 和 % 不能当通配符：sess_1 不应匹配 sessX1。
        Assert.Null(await repository.FindOrderGuidByBackendPaymentAsync("Production", "sess_1", CancellationToken.None));
        Assert.Null(await repository.FindOrderGuidByBackendPaymentAsync("Sandbox", "sess_1%", CancellationToken.None));
    }

    [CardTenderReconciliationSqlServerFact]
    public async Task Reconciliation_service_reports_orphans_then_resolves_them_once_the_session_is_linked()
    {
        await CreatePaymentDetailTableAsync();
        await InsertSessionAsync("S01", "POS01", "orphan", completedAt: DateTime.UtcNow.AddHours(-5));
        var service = CreateReconciliationService();

        var first = await service.RunAsync(CancellationToken.None);
        var second = await service.RunAsync(CancellationToken.None);

        Assert.Equal(1, first.Reported);
        Assert.Equal(1, second.Reported);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[POSM_CardTenderReconciliationIssue] WHERE [Status] = N'Open' AND [IssueType] = N'ApprovedSessionWithoutOrder' AND [OccurrenceCount] = 2;"));

        await ExecuteAtAsync(connection, $"UPDATE [dbo].[POSM_LinklyCloudBackendSession] SET [OrderGuid] = N'{Guid.NewGuid():D}';");
        var third = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, third.Reported);
        Assert.Equal(1, third.Resolved);
        Assert.Equal("Resolved", await StringAsync("SELECT [Status] FROM [dbo].[POSM_CardTenderReconciliationIssue];"));
    }

    [CardTenderReconciliationSqlServerFact]
    public async Task Reconciliation_service_links_sessions_whose_order_is_already_stored_instead_of_reporting()
    {
        await CreatePaymentDetailTableAsync();
        await InsertSessionAsync("S01", "POS01", "sess-1", completedAt: DateTime.UtcNow.AddHours(-5));
        var orderGuid = Guid.NewGuid().ToString("D");
        var reference = LinklyBackendPaymentReference.Format("2610090001", "sess-1", "Sandbox", null);
        await ExecuteAtAsync(connection, $"INSERT INTO [dbo].[payment_detail] VALUES (N'p1', N'{orderGuid}', N'{reference}');");
        var service = CreateReconciliationService();

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(new CardTenderReconciliationResult(1, 1, 0, 0), result);
        Assert.Equal(orderGuid, await StringAsync("SELECT [OrderGuid] FROM [dbo].[POSM_LinklyCloudBackendSession];"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[POSM_CardTenderReconciliationIssue];"));
    }

    private CardTenderReconciliationService CreateReconciliationService() => new(
        new SqlSugarCardTenderReconciliationRepository(CreateContext()),
        Microsoft.Extensions.Options.Options.Create(new CardTenderReconciliationOptions()),
        TimeProvider.System,
        NullLogger<CardTenderReconciliationService>.Instance);

    private Task CreatePaymentDetailTableAsync() => ExecuteAtAsync(
        connection,
        "CREATE TABLE [dbo].[payment_detail] ([PaymentGuid] NVARCHAR(50) NULL, [OrderGuid] NVARCHAR(50) NULL, [Reference] NVARCHAR(2000) NULL);");

    private static LinklyCloudBackendSessionRecord Session(string sessionId, string status, DateTimeOffset updatedAt) => new()
    {
        Environment = "Sandbox",
        StoreCode = "S01",
        DeviceCode = "POS01",
        SessionId = sessionId,
        Status = status,
        TxnRef = "2610090001",
        RequestTxnType = "P",
        RequestAmountCents = 1250,
        TransactionSuccess = status == "Completed" ? true : null,
        OperationType = "Transaction",
        UpdatedAt = updatedAt
    };

    private int txnRefSequence;

    private async Task InsertSessionAsync(
        string store,
        string device,
        string sessionId,
        string status = "Completed",
        bool success = true,
        DateTime? completedAt = null,
        DateTime? updatedAt = null,
        string? orderGuid = null,
        string? txnRef = null)
    {
        txnRef ??= $"26100900{++txnRefSequence:D4}";
        await ExecuteAtAsync(
            connection,
            $"""
            INSERT INTO [dbo].[POSM_LinklyCloudBackendSession]
                ([Environment], [StoreCode], [DeviceCode], [SessionId], [Status], [TxnRef], [RequestTxnType], [RequestAmountCents],
                 [TransactionSuccess], [OperationType], [IsActive])
            VALUES (N'Sandbox', N'{store}', N'{device}', N'{sessionId}', N'{status}', N'{txnRef}', N'P', 1250,
                    {(success ? 1 : 0)}, N'Transaction', 0);
            """);
        if (completedAt is not null || updatedAt is not null || orderGuid is not null)
        {
            // 用显式时间覆盖默认值，便于构造「批准了多久」的场景。
            var completed = completedAt is null ? "NULL" : $"'{completedAt:yyyy-MM-dd HH:mm:ss}'";
            var updated = updatedAt ?? completedAt;
            await ExecuteAtAsync(
                connection,
                $"""
                UPDATE [dbo].[POSM_LinklyCloudBackendSession]
                SET [CompletedAt] = {completed},
                    [UpdatedAt] = {(updated is null ? "[UpdatedAt]" : $"'{updated:yyyy-MM-dd HH:mm:ss}'")},
                    [OrderGuid] = {(orderGuid is null ? "NULL" : $"N'{orderGuid}'")}
                WHERE [SessionId] = N'{sessionId}';
                """);
        }
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
