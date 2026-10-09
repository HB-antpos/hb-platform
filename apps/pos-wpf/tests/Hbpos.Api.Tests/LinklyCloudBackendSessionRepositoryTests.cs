using BlazorApp.Shared.Security;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

/// <summary>
/// 后端会话仓储的“终态保护”语义：同一组场景分别跑在 InMemory 仓储（始终运行）和 SQL Server MERGE 上
/// （配置 LINKLY_LINE_SQLSERVER_TEST_CONNECTION 时运行）。此前 InMemory 与 SQL 语义不一致，
/// 导致 H3 这类“SQL 拦掉回调补写 ResponseCode”的问题单测发现不了。
/// </summary>
public sealed class LinklyCloudBackendSessionInMemoryRepositoryTests
{
    [Fact]
    public Task Completed_session_without_result_accepts_late_result_evidence() =>
        BackendSessionRepositoryScenarios.CompletedWithoutResultAcceptsResultEvidence(
            new InMemoryLinklyCloudBackendAsyncRepository());

    [Fact]
    public Task Completed_session_rejects_conflicting_stale_and_erasing_writes() =>
        BackendSessionRepositoryScenarios.CompletedRejectsConflictingWrites(
            new InMemoryLinklyCloudBackendAsyncRepository());

    [Fact]
    public Task Supervisor_resolved_session_keeps_status_but_accepts_same_status_writes() =>
        BackendSessionRepositoryScenarios.SupervisorResolvedKeepsStatus(
            new InMemoryLinklyCloudBackendAsyncRepository());

    [Fact]
    public Task Acknowledged_failure_keeps_status_but_unacknowledged_failure_accepts_the_real_result() =>
        BackendSessionRepositoryScenarios.AcknowledgedFailureKeepsStatus(
            new InMemoryLinklyCloudBackendAsyncRepository());

    [Fact]
    public Task Late_final_result_is_written_once_and_survives_full_row_writes() =>
        BackendSessionRepositoryScenarios.LateFinalResultIsWrittenOnce(
            new InMemoryLinklyCloudBackendAsyncRepository());

    [Fact]
    public Task Official_query_reject_count_round_trips() =>
        BackendSessionRepositoryScenarios.OfficialQueryRejectCountRoundTrips(
            new InMemoryLinklyCloudBackendAsyncRepository());
}

[Trait("Category", "SQL")]
public sealed class LinklyCloudBackendSessionSqlServerRepositoryTests : IAsyncLifetime
{
    private string? masterConnection;
    private string connection = string.Empty;
    private string database = string.Empty;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(LinklyLineSqlServerFactAttribute.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured)) return;
        database = $"HbLinklySessions_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        masterConnection = builder.ConnectionString;
        await ExecuteAtAsync(masterConnection, $"CREATE DATABASE [{database}]");
        builder.InitialCatalog = database;
        connection = builder.ConnectionString;
        await ExecuteAsync(SqlSugarLinklyCloudBackendAsyncSchemaInitializer.EnsureTableSql);
    }

    public async Task DisposeAsync()
    {
        if (masterConnection is null) return;
        // 名称只来自本用例生成的 GUID，销毁范围严格限于本用例数据库。
        await ExecuteAtAsync(masterConnection,
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    [LinklyLineSqlServerFact]
    public Task Completed_session_without_result_accepts_late_result_evidence() =>
        BackendSessionRepositoryScenarios.CompletedWithoutResultAcceptsResultEvidence(Repository());

    [LinklyLineSqlServerFact]
    public Task Completed_session_rejects_conflicting_stale_and_erasing_writes() =>
        BackendSessionRepositoryScenarios.CompletedRejectsConflictingWrites(Repository());

    [LinklyLineSqlServerFact]
    public Task Supervisor_resolved_session_keeps_status_but_accepts_same_status_writes() =>
        BackendSessionRepositoryScenarios.SupervisorResolvedKeepsStatus(Repository());

    [LinklyLineSqlServerFact]
    public Task Acknowledged_failure_keeps_status_but_unacknowledged_failure_accepts_the_real_result() =>
        BackendSessionRepositoryScenarios.AcknowledgedFailureKeepsStatus(Repository());

    [LinklyLineSqlServerFact]
    public Task Late_final_result_is_written_once_and_survives_full_row_writes() =>
        BackendSessionRepositoryScenarios.LateFinalResultIsWrittenOnce(Repository());

    [LinklyLineSqlServerFact]
    public Task Official_query_reject_count_round_trips() =>
        BackendSessionRepositoryScenarios.OfficialQueryRejectCountRoundTrips(Repository());

    [LinklyLineSqlServerFact]
    public async Task Schema_script_is_idempotent_and_creates_the_environment_session_index_once()
    {
        // M35：回调按 (Environment, SessionId) 查会话。重复执行初始化脚本不能报错也不能重复建索引。
        await ExecuteAsync(SqlSugarLinklyCloudBackendAsyncSchemaInitializer.EnsureTableSql);
        await ExecuteAsync(SqlSugarLinklyCloudBackendAsyncSchemaInitializer.EnsureTableSql);

        var indexes = await QueryAsync("""
            SELECT c.[name]
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
            JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
            WHERE i.[object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]')
              AND i.[name] = N'IX_POSM_LinklyCloudBackendSession_EnvSession'
              AND ic.[is_included_column] = 0
            ORDER BY ic.[key_ordinal];
            """);
        Assert.Equal(["Environment", "SessionId"], indexes);
    }

    [LinklyLineSqlServerFact]
    public async Task Schema_script_upgrades_a_table_created_before_the_new_columns_existed()
    {
        await ExecuteAsync("""
            ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession] DROP CONSTRAINT [DF_POSM_LinklyCloudBackendSession_OfficialQueryRejectCount_Upgrade];
            DROP INDEX [IX_POSM_LinklyCloudBackendSession_EnvSession] ON [dbo].[POSM_LinklyCloudBackendSession];
            ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession] DROP COLUMN [LateFinalAt], [LateFinalTransactionSuccess], [LateFinalResponseCode], [LateFinalResponseText], [OfficialQueryRejectCount];
            """);

        await ExecuteAsync(SqlSugarLinklyCloudBackendAsyncSchemaInitializer.EnsureTableSql);

        var columns = await QueryAsync("""
            SELECT [name] FROM sys.columns
            WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]')
              AND [name] IN (N'LateFinalAt', N'LateFinalTransactionSuccess', N'LateFinalResponseCode', N'LateFinalResponseText', N'OfficialQueryRejectCount')
            ORDER BY [name];
            """);
        Assert.Equal(
            ["LateFinalAt", "LateFinalResponseCode", "LateFinalResponseText", "LateFinalTransactionSuccess", "OfficialQueryRejectCount"],
            columns);
    }

    [LinklyLineSqlServerFact]
    public async Task Callback_lookup_by_environment_and_session_id_uses_the_dedicated_index()
    {
        var repository = Repository();
        await repository.UpsertSessionAsync(
            BackendSessionRepositoryScenarios.Session("lookup-session", "Pending", s => s.IsActive = true),
            CancellationToken.None);

        var found = await repository.GetSessionByEnvironmentSessionIdAsync("Sandbox", "lookup-session", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("S01", found!.StoreCode);
    }

    [LinklyLineSqlServerFact]
    public async Task Probe_lease_shortening_only_applies_to_the_matching_lease_and_never_extends()
    {
        var terminalId = Guid.NewGuid();
        var lease = Guid.NewGuid();
        var longExpiry = DateTime.UtcNow.AddMinutes(9);
        await ExecuteAsync("""
            INSERT INTO [dbo].[POSM_LinklyCloudTerminal]
                ([TerminalId],[Environment],[StoreCode],[LaneNo],[DisplayName],[Username],[Password],
                 [Secret],[PosId],[PairingState],[PairingAttemptId],[PairingLeaseExpiresAt])
            VALUES (@Id,N'Sandbox',N'S01',1,N'Front',N'front',N'pw',N'secret',N'pos',N'Ready',@Lease,@Expiry);
            """,
            new SqlParameter("@Id", terminalId),
            new SqlParameter("@Lease", lease),
            new SqlParameter("@Expiry", System.Data.SqlDbType.DateTime2) { Value = longExpiry });
        var repository = new SqlSugarLinklyCloudTerminalRepository(CreateContext(), new PassthroughProtector());
        var shortExpiry = DateTime.UtcNow.AddSeconds(60);

        await repository.ShortenOperationLeaseAsync("Sandbox", "S01", terminalId, Guid.NewGuid(), shortExpiry, default);
        Assert.True((await repository.GetAsync("Sandbox", "S01", terminalId, default))!.PairingLeaseExpiresAt > DateTime.UtcNow.AddMinutes(8));

        await repository.ShortenOperationLeaseAsync("Sandbox", "S01", terminalId, lease, shortExpiry, default);
        var shortened = (await repository.GetAsync("Sandbox", "S01", terminalId, default))!.PairingLeaseExpiresAt;
        Assert.True(shortened <= DateTime.UtcNow.AddSeconds(61));
        Assert.True(shortened > DateTime.UtcNow);

        await repository.ShortenOperationLeaseAsync("Sandbox", "S01", terminalId, lease, DateTime.UtcNow.AddMinutes(5), default);
        Assert.Equal(shortened, (await repository.GetAsync("Sandbox", "S01", terminalId, default))!.PairingLeaseExpiresAt);
    }

    private SqlSugarLinklyCloudBackendAsyncRepository Repository() => new(CreateContext());

    private HbposSqlSugarContext CreateContext()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MainConnection"] = connection,
            ["ConnectionStrings:PosmConnection"] = connection,
            ["Database:CommandTimeoutSeconds"] = "20",
        }).Build();
        return new HbposSqlSugarContext(config, NullLogger<HbposSqlSugarContext>.Instance);
    }

    private Task ExecuteAsync(string sql, params SqlParameter[] parameters) => ExecuteAtAsync(connection, sql, parameters);

    private static async Task ExecuteAtAsync(string connectionString, string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> QueryAsync(string sql)
    {
        var values = new List<string>();
        await using var sqlConnection = new SqlConnection(connection);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private sealed class PassthroughProtector : ILinklyCloudTerminalCredentialProtector
    {
        public string ProtectPassword(string value) => value;
        public string UnprotectPassword(string value) => value;
        public string ProtectSecret(string value) => value;
        public string UnprotectSecret(string value) => value;
    }
}

internal static class BackendSessionRepositoryScenarios
{
    public static LinklyCloudBackendSessionRecord Session(
        string sessionId,
        string status,
        Action<LinklyCloudBackendSessionRecord>? edit = null)
    {
        var session = new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = status,
            // 表列 TxnRef 只有 16 个字符。
            TxnRef = ("TXN-" + sessionId)[..Math.Min(16, 4 + sessionId.Length)],
            OperationType = "Transaction",
            IsActive = false,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        edit?.Invoke(session);
        return session;
    }

    private static Task<LinklyCloudBackendSessionRecord?> GetAsync(ILinklyCloudBackendAsyncRepository repository, string sessionId) =>
        repository.GetSessionAsync("Sandbox", "S01", "POS-01", sessionId, CancellationToken.None);

    // H3：sendkey 200 曾把会话提前写成缺少结果的 Completed；随后到达的批准回调必须能补写 ResponseCode，
    // 不能被 MERGE 当成 NULL <> '00' 的冲突拦掉。
    public static async Task CompletedWithoutResultAcceptsResultEvidence(ILinklyCloudBackendAsyncRepository repository)
    {
        await repository.UpsertSessionAsync(Session("h3", "Completed"), CancellationToken.None);

        await repository.UpsertSessionAsync(Session("h3", "Completed", s =>
        {
            s.TransactionSuccess = true;
            s.ResponseCode = "00";
            s.ResponseText = "APPROVED";
        }), CancellationToken.None);
        var filled = await GetAsync(repository, "h3");
        Assert.Equal("00", filled!.ResponseCode);
        Assert.Equal("APPROVED", filled.ResponseText);
        Assert.True(filled.TransactionSuccess);

        // 有 ResponseCode 但还没有 ResponseText：同一结果的文本也允许补写。
        await repository.UpsertSessionAsync(Session("h3-text", "Completed", s => s.ResponseCode = "00"), CancellationToken.None);
        await repository.UpsertSessionAsync(Session("h3-text", "Completed", s =>
        {
            s.ResponseCode = "00";
            s.ResponseText = "APPROVED";
            s.TransactionSuccess = true;
        }), CancellationToken.None);
        Assert.Equal("APPROVED", (await GetAsync(repository, "h3-text"))!.ResponseText);
    }

    public static async Task CompletedRejectsConflictingWrites(ILinklyCloudBackendAsyncRepository repository)
    {
        await repository.UpsertSessionAsync(Session("done", "Completed", s =>
        {
            s.TransactionSuccess = true;
            s.ResponseCode = "00";
            s.ResponseText = "APPROVED";
        }), CancellationToken.None);

        // 不同的结果、被抹掉的结果、倒退成非终态、改 TxnRef：都必须原样保留已完成的结果。
        await repository.UpsertSessionAsync(Session("done", "Completed", s =>
        {
            s.TransactionSuccess = false;
            s.ResponseCode = "05";
            s.ResponseText = "DECLINED";
        }), CancellationToken.None);
        await repository.UpsertSessionAsync(Session("done", "Completed"), CancellationToken.None);
        await repository.UpsertSessionAsync(Session("done", "Pending", s => s.IsActive = true), CancellationToken.None);
        await repository.UpsertSessionAsync(Session("done", "Completed", s =>
        {
            s.TxnRef = "OTHER";
            s.TransactionSuccess = true;
            s.ResponseCode = "00";
            s.ResponseText = "APPROVED";
        }), CancellationToken.None);

        var persisted = await GetAsync(repository, "done");
        Assert.Equal("Completed", persisted!.Status);
        Assert.Equal("00", persisted.ResponseCode);
        Assert.Equal("APPROVED", persisted.ResponseText);
        Assert.True(persisted.TransactionSuccess);
        Assert.Equal("TXN-done", persisted.TxnRef);
        Assert.False(persisted.IsActive);
    }

    // H1：主管结案后，迟到的 Linkly 结果（或来自旧快照的写入）不得改写 Status，也不得把会话重新激活。
    public static async Task SupervisorResolvedKeepsStatus(ILinklyCloudBackendAsyncRepository repository)
    {
        await repository.UpsertSessionAsync(Session("sup", "Pending", s => s.IsActive = true), CancellationToken.None);
        await repository.AcknowledgeSessionAsync(
            "Sandbox", "S01", "POS-01", "sup", DateTimeOffset.UtcNow, supervisorResolved: true, CancellationToken.None);

        await repository.UpsertSessionAsync(Session("sup", "Completed", s =>
        {
            s.TransactionSuccess = true;
            s.ResponseCode = "00";
        }), CancellationToken.None);
        await repository.UpsertSessionAsync(Session("sup", "Pending", s => s.IsActive = true), CancellationToken.None);
        var kept = await GetAsync(repository, "sup");
        Assert.Equal("SupervisorResolved", kept!.Status);
        Assert.False(kept.IsActive);
        Assert.Null(kept.ResponseCode);
        Assert.Null(kept.TransactionSuccess);
        Assert.NotNull(kept.ClientAcknowledgedAt);

        // 同状态写入（小票、显示等辅助字段）照常放行。
        await repository.UpsertSessionAsync(Session("sup", "SupervisorResolved", s => s.ReceiptText = "CUSTOMER COPY"), CancellationToken.None);
        var updated = await GetAsync(repository, "sup");
        Assert.Equal("SupervisorResolved", updated!.Status);
        Assert.Equal("CUSTOMER COPY", updated.ReceiptText);
        Assert.NotNull(updated.ClientAcknowledgedAt);
    }

    public static async Task AcknowledgedFailureKeepsStatus(ILinklyCloudBackendAsyncRepository repository)
    {
        await repository.UpsertSessionAsync(Session("acked", "Failed", s => s.ClientAcknowledgedAt = DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.UpsertSessionAsync(Session("acked", "Completed", s =>
        {
            s.TransactionSuccess = true;
            s.ResponseCode = "00";
        }), CancellationToken.None);
        var acked = await GetAsync(repository, "acked");
        Assert.Equal("Failed", acked!.Status);
        Assert.Null(acked.ResponseCode);

        // 未 ack 的失败，客户端还没据此行动：真实的 Linkly 结果仍然覆盖它。
        await repository.UpsertSessionAsync(Session("open", "Failed"), CancellationToken.None);
        await repository.UpsertSessionAsync(Session("open", "Completed", s =>
        {
            s.TransactionSuccess = true;
            s.ResponseCode = "00";
        }), CancellationToken.None);
        Assert.Equal("Completed", (await GetAsync(repository, "open"))!.Status);
    }

    public static async Task LateFinalResultIsWrittenOnce(ILinklyCloudBackendAsyncRepository repository)
    {
        await repository.UpsertSessionAsync(Session("late", "Pending", s => s.IsActive = true), CancellationToken.None);
        await repository.AcknowledgeSessionAsync(
            "Sandbox", "S01", "POS-01", "late", DateTimeOffset.UtcNow, supervisorResolved: true, CancellationToken.None);
        var before = await GetAsync(repository, "late");

        var first = await repository.RecordLateFinalResultAsync(
            "Sandbox", "S01", "POS-01", "late", DateTimeOffset.UtcNow, true, "00", "APPROVED", CancellationToken.None);
        var second = await repository.RecordLateFinalResultAsync(
            "Sandbox", "S01", "POS-01", "late", DateTimeOffset.UtcNow, false, "05", "DECLINED", CancellationToken.None);
        var recorded = await GetAsync(repository, "late");

        Assert.True(first);
        Assert.False(second);
        Assert.NotNull(recorded!.LateFinalAt);
        Assert.True(recorded.LateFinalTransactionSuccess);
        Assert.Equal("00", recorded.LateFinalResponseCode);
        Assert.Equal("APPROVED", recorded.LateFinalResponseText);
        // 不触碰 Status / ack / 活动标记。
        Assert.Equal("SupervisorResolved", recorded.Status);
        Assert.Equal(before!.ClientAcknowledgedAt, recorded.ClientAcknowledgedAt);
        Assert.False(recorded.IsActive);

        // 来自旧快照（没有 LateFinal 字段）的整行写回不能把它抹掉。
        await repository.UpsertSessionAsync(Session("late", "SupervisorResolved", s => s.ReceiptText = "COPY"), CancellationToken.None);
        var afterFullRowWrite = await GetAsync(repository, "late");
        Assert.Equal("00", afterFullRowWrite!.LateFinalResponseCode);
        Assert.Equal("COPY", afterFullRowWrite.ReceiptText);

        Assert.False(await repository.RecordLateFinalResultAsync(
            "Sandbox", "S01", "POS-01", "missing", DateTimeOffset.UtcNow, true, "00", null, CancellationToken.None));
    }

    public static async Task OfficialQueryRejectCountRoundTrips(ILinklyCloudBackendAsyncRepository repository)
    {
        await repository.UpsertSessionAsync(Session("count", "Pending", s =>
        {
            s.IsActive = true;
            s.OfficialQueryRejectCount = 3;
        }), CancellationToken.None);
        Assert.Equal(3, (await GetAsync(repository, "count"))!.OfficialQueryRejectCount);

        await repository.UpsertSessionAsync(Session("count", "Pending", s =>
        {
            s.IsActive = true;
            s.OfficialQueryRejectCount = 0;
        }), CancellationToken.None);
        Assert.Equal(0, (await GetAsync(repository, "count"))!.OfficialQueryRejectCount);
    }
}
