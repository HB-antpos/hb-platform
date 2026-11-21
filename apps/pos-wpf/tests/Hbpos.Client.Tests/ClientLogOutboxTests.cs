using Hbpos.Client.Wpf.Services;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class ClientLogOutboxTests
{
    [Fact]
    public async Task Initialize_creates_independent_dual_outbox_schema_with_required_pragmas()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            var store = new ClientLogOutboxStore(databasePath);

            await store.InitializeAsync(CancellationToken.None);

            await using (var connection = await store.OpenConnectionAsync(CancellationToken.None))
            {
                Assert.Equal("wal", await ExecuteScalarAsync<string>(connection, "PRAGMA journal_mode;"));
                Assert.Equal(5000L, await ExecuteScalarAsync<long>(connection, "PRAGMA busy_timeout;"));
                Assert.Equal(1L, await ExecuteScalarAsync<long>(connection, "PRAGMA user_version;"));
                Assert.True(await TableExistsAsync(connection, "RuntimeLogOutbox"));
                Assert.True(await TableExistsAsync(connection, "OperationAuditOutbox"));
            }
        }
        finally
        {
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Pending_records_are_idempotent_and_read_oldest_first()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var store = new ClientLogOutboxStore(databasePath);
            await store.InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
            var olderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var newerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

            await store.EnqueueAsync(ClientLogOutboxKind.Runtime, newerId, now, "{\"message\":\"new\"}", now, CancellationToken.None);
            await store.EnqueueAsync(ClientLogOutboxKind.Runtime, olderId, now.AddMinutes(-1), "{\"message\":\"old\"}", now, CancellationToken.None);
            await store.EnqueueAsync(ClientLogOutboxKind.Runtime, olderId, now.AddMinutes(-1), "{\"message\":\"duplicate\"}", now, CancellationToken.None);

            var pending = await store.ReadPendingAsync(ClientLogOutboxKind.Runtime, now, 100, CancellationToken.None);

            Assert.Equal([olderId, newerId], pending.Select(item => item.EventId));
            Assert.Equal("{\"message\":\"old\"}", pending[0].PayloadJson);
        }
        finally
        {
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Apply_results_deletes_completed_and_quarantines_permanent_rejection()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var store = new ClientLogOutboxStore(databasePath);
            await store.InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
            var acceptedId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var duplicateId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
            var rejectedId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
            foreach (var eventId in new[] { acceptedId, duplicateId, rejectedId })
            {
                await store.EnqueueAsync(ClientLogOutboxKind.OperationAudit, eventId, now, "{}", now, CancellationToken.None);
            }

            await store.ApplyResultsAsync(
                ClientLogOutboxKind.OperationAudit,
                [acceptedId, duplicateId],
                [new ClientLogRejection(rejectedId, "INVALID_EVENT", "invalid payload")],
                now,
                CancellationToken.None);

            Assert.Empty(await store.ReadPendingAsync(ClientLogOutboxKind.OperationAudit, now, 100, CancellationToken.None));
            var rejected = await store.ReadRejectedAsync(ClientLogOutboxKind.OperationAudit, 100, CancellationToken.None);
            var item = Assert.Single(rejected);
            Assert.Equal(rejectedId, item.EventId);
            Assert.Equal("INVALID_EVENT", item.LastErrorCode);
            Assert.Equal("invalid payload", item.LastErrorMessage);
        }
        finally
        {
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Retry_is_not_due_until_next_attempt_and_increments_attempt_count()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var store = new ClientLogOutboxStore(databasePath);
            await store.InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
            var eventId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
            await store.EnqueueAsync(ClientLogOutboxKind.Runtime, eventId, now, "{}", now, CancellationToken.None);

            await store.ScheduleRetryAsync(
                ClientLogOutboxKind.Runtime,
                [eventId],
                now.AddMinutes(5),
                "HTTP_503",
                "temporary failure",
                CancellationToken.None);

            Assert.Empty(await store.ReadPendingAsync(ClientLogOutboxKind.Runtime, now, 100, CancellationToken.None));
            var due = Assert.Single(await store.ReadPendingAsync(
                ClientLogOutboxKind.Runtime,
                now.AddMinutes(5),
                100,
                CancellationToken.None));
            Assert.Equal(1, due.AttemptCount);
            Assert.Equal("HTTP_503", due.LastErrorCode);
        }
        finally
        {
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Runtime_cap_drops_oldest_but_operation_audit_is_never_capacity_trimmed()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var store = new ClientLogOutboxStore(databasePath, runtimePendingLimit: 2);
            await store.InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
            var ids = new[]
            {
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                Guid.Parse("10000000-0000-0000-0000-000000000002"),
                Guid.Parse("10000000-0000-0000-0000-000000000003")
            };

            for (var index = 0; index < ids.Length; index++)
            {
                var occurredAt = now.AddSeconds(index);
                await store.EnqueueAsync(ClientLogOutboxKind.Runtime, ids[index], occurredAt, "{}", occurredAt, CancellationToken.None);
                await store.EnqueueAsync(ClientLogOutboxKind.OperationAudit, ids[index], occurredAt, "{}", occurredAt, CancellationToken.None);
            }

            var runtime = await store.ReadPendingAsync(ClientLogOutboxKind.Runtime, now.AddMinutes(1), 100, CancellationToken.None);
            var audits = await store.ReadPendingAsync(ClientLogOutboxKind.OperationAudit, now.AddMinutes(1), 100, CancellationToken.None);
            Assert.Equal(ids[1..], runtime.Select(item => item.EventId));
            Assert.Equal(ids, audits.Select(item => item.EventId));
            Assert.Equal(1, store.RuntimeDroppedCount);
        }
        finally
        {
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Runtime_cap_is_trimmed_in_batches_so_the_overshoot_is_bounded_and_the_oldest_still_go_first()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            // 上限 16 → 每 2 条做一次裁剪（上限的 1/8）：偶数条时精确，奇数条时最多多出 1 条。
            var store = new ClientLogOutboxStore(databasePath, runtimePendingLimit: 16);
            await store.InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.Parse("2026-07-10T01:00:00Z");
            var ids = Enumerable.Range(1, 18)
                .Select(index => Guid.Parse($"20000000-0000-0000-0000-{index:D12}"))
                .ToArray();

            for (var index = 0; index < 17; index++)
            {
                var occurredAt = now.AddSeconds(index);
                await store.EnqueueAsync(ClientLogOutboxKind.Runtime, ids[index], occurredAt, "{}", occurredAt, CancellationToken.None);
            }

            // 第 17 条没赶上裁剪点：暂时多出 1 条，没有丢任何日志。
            Assert.Equal(17, await store.CountPendingAsync(ClientLogOutboxKind.Runtime, CancellationToken.None));
            Assert.Equal(0, store.RuntimeDroppedCount);

            var eighteenth = now.AddSeconds(17);
            await store.EnqueueAsync(ClientLogOutboxKind.Runtime, ids[17], eighteenth, "{}", eighteenth, CancellationToken.None);

            // 第 18 条赶上裁剪点：回到上限，丢掉的是最旧的两条。
            var runtime = await store.ReadPendingAsync(ClientLogOutboxKind.Runtime, now.AddMinutes(1), 100, CancellationToken.None);
            Assert.Equal(ids[2..], runtime.Select(item => item.EventId));
            Assert.Equal(2, store.RuntimeDroppedCount);
        }
        finally
        {
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Cleanup_removes_rejected_records_older_than_thirty_days()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var store = new ClientLogOutboxStore(databasePath);
            await store.InitializeAsync(CancellationToken.None);
            var eventId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
            var rejectedAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
            await store.EnqueueAsync(ClientLogOutboxKind.OperationAudit, eventId, rejectedAt, "{}", rejectedAt, CancellationToken.None);
            await store.ApplyResultsAsync(
                ClientLogOutboxKind.OperationAudit,
                [],
                [new ClientLogRejection(eventId, "INVALID", null)],
                rejectedAt,
                CancellationToken.None);

            await store.DeleteExpiredRejectedAsync(rejectedAt.AddDays(31), CancellationToken.None);

            Assert.Empty(await store.ReadRejectedAsync(ClientLogOutboxKind.OperationAudit, 100, CancellationToken.None));
        }
        finally
        {
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Selected_audits_are_scoped_and_reset_for_immediate_retry()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var store = new ClientLogOutboxStore(databasePath);
            await store.InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.Parse("2026-07-21T01:00:00Z");
            var selectedId = Guid.NewGuid();
            var otherDeviceId = Guid.NewGuid();
            await store.EnqueueAsync(ClientLogOutboxKind.OperationAudit, selectedId, now,
                "{\"storeCode\":\"S001\",\"deviceCode\":\"POS-01\"}", now, CancellationToken.None);
            await store.EnqueueAsync(ClientLogOutboxKind.OperationAudit, otherDeviceId, now,
                "{\"storeCode\":\"S001\",\"deviceCode\":\"POS-02\"}", now, CancellationToken.None);
            await store.ApplyResultsAsync(
                ClientLogOutboxKind.OperationAudit,
                [],
                [new ClientLogRejection(selectedId, "REJECTED", "old error")],
                now,
                CancellationToken.None);

            var scoped = Assert.Single(await store.ReadOperationForScopeAsync(
                "S001", "POS-01", 100, CancellationToken.None));
            Assert.Equal("Rejected", scoped.State);

            Assert.Equal(1, await store.ResetOperationForRetryAsync([selectedId], now.AddMinutes(1), CancellationToken.None));
            var retry = Assert.Single(await store.ReadOperationForScopeAsync(
                "S001", "POS-01", 100, CancellationToken.None));
            Assert.Equal("Pending", retry.State);
            Assert.Equal(0, retry.AttemptCount);
            Assert.Null(retry.LastErrorCode);
            Assert.Null(retry.LastErrorMessage);
            Assert.Equal(now.AddMinutes(1), retry.NextAttemptAtUtc);
        }
        finally
        {
            await DeleteDatabaseFilesAsync(databasePath);
        }
    }

    private static string CreateDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-logs-test-{Guid.NewGuid():N}.db");
    }

    private static async Task<T> ExecuteScalarAsync<T>(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T));
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private static Task DeleteDatabaseFilesAsync(string databasePath)
    {
        // Windows 上连接 Dispose 后句柄可能短暂保留，统一走共享的 best-effort 清理，避免清理阶段误报。
        return SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
    }
}
