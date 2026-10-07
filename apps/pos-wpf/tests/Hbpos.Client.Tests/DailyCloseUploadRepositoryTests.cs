using System.Globalization;
using Hbpos.Client.Wpf.Services;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

/// <summary>日结上传仓储与本地库补列：真实 SQLite，不用假实现。</summary>
public sealed class DailyCloseUploadRepositoryTests
{
    private static readonly string[] UploadColumns =
    [
        "UploadStatus", "UploadAttemptCount", "NextUploadAt", "LastUploadAttemptAt",
        "UploadErrorCode", "UploadErrorMessage", "UploadedAt"
    ];

    // 旧版（本 PR 之前）LocalDailyCloses 的建表语句：没有任何上传列。
    private const string LegacyCreateTableSql = """
        CREATE TABLE LocalDailyCloses (
            DailyCloseGuid TEXT PRIMARY KEY,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            CashierId TEXT NOT NULL,
            CashierName TEXT NOT NULL,
            BusinessDate TEXT NOT NULL,
            PeriodFrom TEXT NOT NULL,
            PeriodTo TEXT NOT NULL,
            SavedAt TEXT NOT NULL,
            OrderCount INTEGER NOT NULL,
            CashSalesAmount TEXT NOT NULL,
            CashRefundAmount TEXT NOT NULL,
            CashNetAmount TEXT NOT NULL,
            CardSalesAmount TEXT NOT NULL,
            CardRefundAmount TEXT NOT NULL,
            CardNetAmount TEXT NOT NULL,
            VoucherSalesAmount TEXT NOT NULL,
            VoucherRefundAmount TEXT NOT NULL,
            VoucherNetAmount TEXT NOT NULL,
            RefundAmount TEXT NOT NULL,
            ReturnQuantity TEXT NOT NULL,
            NoteSubtotal TEXT NOT NULL,
            CoinSubtotal TEXT NOT NULL,
            CountedCashAmount TEXT NOT NULL,
            CashDifference TEXT NOT NULL
        );
        """;

    [Fact]
    public async Task New_database_creates_daily_close_upload_columns_index_and_pending_default()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();

        await using var connection = await fixture.Store.OpenConnectionAsync();
        var columns = await ReadTableInfoAsync(connection, "LocalDailyCloses");
        foreach (var column in UploadColumns)
        {
            Assert.Contains(column, columns.Keys);
        }

        // UploadStatus / UploadAttemptCount 必须 NOT NULL 且带默认值：历史行靠这个默认值自动变成 Pending。
        Assert.Equal(("TEXT", true, "'Pending'"), columns["UploadStatus"]);
        Assert.Equal(("INTEGER", true, "0"), columns["UploadAttemptCount"]);
        Assert.False(columns["NextUploadAt"].NotNull);
        Assert.Contains(
            "IX_LocalDailyCloses_UploadDue",
            await ReadStringsAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'index';"));

        // 新保存的日结（只写原有 25 列）默认是 Pending、尝试 0 次、立即到期。
        var guid = await fixture.InsertDailyCloseAsync();
        var row = await fixture.ReadUploadRowAsync(guid);
        Assert.Equal("Pending", row.Status);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.NextUploadAt);
        Assert.Null(row.UploadedAt);
    }

    [Fact]
    public async Task Existing_database_without_upload_columns_gets_them_and_every_historical_row_becomes_pending_and_due()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-daily-close-legacy-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            var legacyGuids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            await using (var connection = await store.OpenConnectionAsync())
            {
                await ExecuteAsync(connection, LegacyCreateTableSql);
                foreach (var (guid, index) in legacyGuids.Select((guid, index) => (guid, index)))
                {
                    await ExecuteAsync(
                        connection,
                        $"""
                        INSERT INTO LocalDailyCloses
                        VALUES ('{guid}', 'S001', 'POS-01', 'C001', 'Alice', '2026-05-2{index + 1}',
                                '2026-05-2{index + 1}T00:00:00.0000000+10:00', '2026-05-2{index + 2}T00:00:00.0000000+10:00',
                                '2026-05-2{index + 1}T22:00:00.0000000+10:00', 1, '10', '0', '10', '0', '0', '0', '0', '0', '0',
                                '0', '0', '0', '0', '0', '0');
                        """);
                }

                // 升级前确认：旧表真的没有上传列。
                var before = await ReadTableInfoAsync(connection, "LocalDailyCloses");
                Assert.DoesNotContain("UploadStatus", before.Keys);
            }

            await new LocalSchemaService(store).InitializeAsync();

            await using var verification = await store.OpenConnectionAsync();
            var columns = await ReadTableInfoAsync(verification, "LocalDailyCloses");
            foreach (var column in UploadColumns)
            {
                Assert.Contains(column, columns.Keys);
            }

            // 关键：没有任何回填 UPDATE，历史行靠列默认值自动变 Pending 且 NextUploadAt 为 NULL（立即到期）。
            var repository = new LocalDailyCloseRepository(store);
            foreach (var guid in legacyGuids)
            {
                Assert.Equal(
                    ("Pending", 0L, true),
                    await ReadUploadStateAsync(verification, guid));
            }

            var due = await repository.GetDueUploadGuidsAsync(
                "S001",
                "POS-01",
                take: 20,
                now: DateTimeOffset.UtcNow);
            Assert.Equal(legacyGuids.OrderBy(guid => guid), due.OrderBy(guid => guid));
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Upload_column_migration_is_idempotent_and_does_not_reset_synced_or_rejected_rows()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var synced = await fixture.InsertDailyCloseAsync();
        var rejected = await fixture.InsertDailyCloseAsync();
        await fixture.ExecuteSqlAsync(
            "UPDATE LocalDailyCloses SET UploadStatus = 'Synced', UploadedAt = '2026-10-07T00:00:00.0000000+00:00' WHERE DailyCloseGuid = $G;",
            ("$G", synced.ToString()));
        await fixture.ExecuteSqlAsync(
            "UPDATE LocalDailyCloses SET UploadStatus = 'Rejected', UploadErrorCode = 'INVALID_CASH_COUNTS' WHERE DailyCloseGuid = $G;",
            ("$G", rejected.ToString()));

        // 应用每次启动都会重新执行 InitializeAsync：补列必须幂等，不能把已终结的行重新排队。
        await new LocalSchemaService(fixture.Store).InitializeAsync();
        await new LocalSchemaService(fixture.Store).InitializeAsync();

        Assert.Equal("Synced", (await fixture.ReadUploadRowAsync(synced)).Status);
        var rejectedRow = await fixture.ReadUploadRowAsync(rejected);
        Assert.Equal("Rejected", rejectedRow.Status);
        Assert.Equal("INVALID_CASH_COUNTS", rejectedRow.ErrorCode);
    }

    [Fact]
    public async Task GetDueUploadGuidsAsync_returns_only_pending_due_rows_of_the_current_device_scope()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var now = new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
        var due = await fixture.InsertDailyCloseAsync();
        var otherDevice = await fixture.InsertDailyCloseAsync(deviceCode: "POS-02");
        var otherStore = await fixture.InsertDailyCloseAsync(storeCode: "S002");
        var future = await fixture.InsertDailyCloseAsync();
        var synced = await fixture.InsertDailyCloseAsync();
        var rejected = await fixture.InsertDailyCloseAsync();
        var uploading = await fixture.InsertDailyCloseAsync();
        await fixture.ExecuteSqlAsync(
            "UPDATE LocalDailyCloses SET NextUploadAt = $At WHERE DailyCloseGuid = $G;",
            ("$At", now.AddSeconds(5).ToString("O", CultureInfo.InvariantCulture)),
            ("$G", future.ToString()));
        await fixture.ExecuteSqlAsync(
            "UPDATE LocalDailyCloses SET UploadStatus = 'Synced' WHERE DailyCloseGuid = $G;",
            ("$G", synced.ToString()));
        await fixture.ExecuteSqlAsync(
            "UPDATE LocalDailyCloses SET UploadStatus = 'Rejected' WHERE DailyCloseGuid = $G;",
            ("$G", rejected.ToString()));
        await fixture.ExecuteSqlAsync(
            "UPDATE LocalDailyCloses SET UploadStatus = 'Uploading' WHERE DailyCloseGuid = $G;",
            ("$G", uploading.ToString()));

        var guids = await fixture.Repository.GetDueUploadGuidsAsync("S001", "POS-01", 20, now);

        // 别的设备范围的行保持 Pending 不动，也不会被取出来：否则设备换绑后旧行会 403 并阻塞整个批次。
        Assert.Equal([due], guids);
        Assert.Equal("Pending", (await fixture.ReadUploadRowAsync(otherDevice)).Status);
        Assert.Equal("Pending", (await fixture.ReadUploadRowAsync(otherStore)).Status);

        // 退避到期之后，未来行才会出现。
        var later = await fixture.Repository.GetDueUploadGuidsAsync("S001", "POS-01", 20, now.AddSeconds(5));
        Assert.Equal(new[] { due, future }.OrderBy(guid => guid), later.OrderBy(guid => guid));
    }

    [Fact]
    public async Task GetDueUploadGuidsAsync_honors_take_and_orders_never_failed_rows_before_retries_by_saved_time()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var now = new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
        var newer = await fixture.InsertDailyCloseAsync(businessDate: "2026-06-02");
        var older = await fixture.InsertDailyCloseAsync(businessDate: "2026-06-01");
        var retry = await fixture.InsertDailyCloseAsync(businessDate: "2026-05-01");
        await fixture.ExecuteSqlAsync(
            "UPDATE LocalDailyCloses SET NextUploadAt = $At WHERE DailyCloseGuid = $G;",
            ("$At", now.AddSeconds(-30).ToString("O", CultureInfo.InvariantCulture)),
            ("$G", retry.ToString()));

        var guids = await fixture.Repository.GetDueUploadGuidsAsync("S001", "POS-01", 20, now);
        var limited = await fixture.Repository.GetDueUploadGuidsAsync("S001", "POS-01", 2, now);

        Assert.Equal([older, newer, retry], guids);
        Assert.Equal([older, newer], limited);
    }

    [Fact]
    public async Task TryClaimUploadAsync_has_exactly_one_winner_under_concurrent_claims()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guid = await fixture.InsertDailyCloseAsync();
        var attemptedAt = new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
        using var start = new ManualResetEventSlim(false);

        // 16 个并发调用方同时抢同一条记录：CAS 保证只有一个拿到租约。
        var claims = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait(AsyncTestWaitSupport.DefaultTimeout);
                return await fixture.Repository.TryClaimUploadAsync(guid, "S001", "POS-01", attemptedAt);
            }))
            .ToArray();
        start.Set();
        var leases = await Task.WhenAll(claims);

        var winner = Assert.Single(leases, lease => lease is not null);
        Assert.Equal(guid, winner!.DailyCloseGuid);
        Assert.Equal(1, winner.UploadAttemptCount);
        var row = await fixture.ReadUploadRowAsync(guid);
        Assert.Equal("Uploading", row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.NotNull(row.LastUploadAttemptAt);
    }

    [Fact]
    public async Task TryClaimUploadAsync_refuses_not_due_foreign_scope_and_already_claimed_rows()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var now = new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
        var notDue = await fixture.InsertDailyCloseAsync();
        var foreign = await fixture.InsertDailyCloseAsync(deviceCode: "POS-02");
        var claimed = await fixture.InsertDailyCloseAsync();
        await fixture.ExecuteSqlAsync(
            "UPDATE LocalDailyCloses SET NextUploadAt = $At WHERE DailyCloseGuid = $G;",
            ("$At", now.AddSeconds(30).ToString("O", CultureInfo.InvariantCulture)),
            ("$G", notDue.ToString()));

        Assert.Null(await fixture.Repository.TryClaimUploadAsync(notDue, "S001", "POS-01", now));
        // 范围不符：即使 Guid 对得上也不能认领。
        Assert.Null(await fixture.Repository.TryClaimUploadAsync(foreign, "S001", "POS-01", now));
        Assert.NotNull(await fixture.Repository.TryClaimUploadAsync(claimed, "S001", "POS-01", now));
        Assert.Null(await fixture.Repository.TryClaimUploadAsync(claimed, "S001", "POS-01", now));

        Assert.Equal(0, (await fixture.ReadUploadRowAsync(notDue)).AttemptCount);
        Assert.Equal(0, (await fixture.ReadUploadRowAsync(foreign)).AttemptCount);
        Assert.Equal(1, (await fixture.ReadUploadRowAsync(claimed)).AttemptCount);
    }

    [Fact]
    public async Task RecoverExpiredUploadingAsync_requeues_only_expired_leases()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var now = new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
        var stale = await fixture.InsertDailyCloseAsync();
        var active = await fixture.InsertDailyCloseAsync();
        Assert.NotNull(await fixture.Repository.TryClaimUploadAsync(stale, "S001", "POS-01", now.AddMinutes(-3)));
        Assert.NotNull(await fixture.Repository.TryClaimUploadAsync(active, "S001", "POS-01", now.AddSeconds(-10)));

        await fixture.Repository.RecoverExpiredUploadingAsync(now.AddMinutes(-2), now);

        var staleRow = await fixture.ReadUploadRowAsync(stale);
        Assert.Equal("Pending", staleRow.Status);
        Assert.Equal("UPLOAD_LEASE_EXPIRED", staleRow.ErrorCode);
        Assert.Equal(1, staleRow.AttemptCount);
        Assert.Equal("Uploading", (await fixture.ReadUploadRowAsync(active)).Status);
    }

    [Fact]
    public async Task Upload_state_transitions_follow_the_lease()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var now = new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
        var guid = await fixture.InsertDailyCloseAsync();

        // 没有租约（仍是 Pending）时，失败标记不得改状态：迟到的旧请求结果不能覆盖别处的状态。
        await fixture.Repository.MarkUploadRejectedAsync(guid, "X", "late");
        await fixture.Repository.MarkUploadPendingAsync(guid, now.AddSeconds(5), "X", "late");
        Assert.Equal("Pending", (await fixture.ReadUploadRowAsync(guid)).Status);
        Assert.Null((await fixture.ReadUploadRowAsync(guid)).ErrorCode);

        // 失败退避：回到 Pending，保留尝试次数，设置下次到期时间与错误信息。
        Assert.NotNull(await fixture.Repository.TryClaimUploadAsync(guid, "S001", "POS-01", now));
        await fixture.Repository.MarkUploadPendingAsync(guid, now.AddSeconds(5), "NETWORK", "boom");
        var pending = await fixture.ReadUploadRowAsync(guid);
        Assert.Equal("Pending", pending.Status);
        Assert.Equal(1, pending.AttemptCount);
        Assert.Equal(now.AddSeconds(5).ToString("O", CultureInfo.InvariantCulture), pending.NextUploadAt);
        Assert.Equal("NETWORK", pending.ErrorCode);
        Assert.Equal("boom", pending.ErrorMessage);

        // 退避没到期不能被认领；到期后再认领，尝试次数累加。
        Assert.Null(await fixture.Repository.TryClaimUploadAsync(guid, "S001", "POS-01", now.AddSeconds(4)));
        var second = await fixture.Repository.TryClaimUploadAsync(guid, "S001", "POS-01", now.AddSeconds(5));
        Assert.Equal(2, second!.UploadAttemptCount);

        // 永久拒绝：Rejected，不再有到期时间，不会再被取出。
        await fixture.Repository.MarkUploadRejectedAsync(guid, "INVALID_CASH_COUNTS", "bad counts");
        var rejected = await fixture.ReadUploadRowAsync(guid);
        Assert.Equal("Rejected", rejected.Status);
        Assert.Null(rejected.NextUploadAt);
        Assert.Equal("INVALID_CASH_COUNTS", rejected.ErrorCode);
        Assert.Empty(await fixture.Repository.GetDueUploadGuidsAsync("S001", "POS-01", 20, now.AddDays(1)));

        // 成功：Synced，清掉错误并记录上传时间（独立一条走一遍）。
        var other = await fixture.InsertDailyCloseAsync();
        Assert.NotNull(await fixture.Repository.TryClaimUploadAsync(other, "S001", "POS-01", now));
        await fixture.Repository.MarkUploadSucceededAsync(other, now.AddSeconds(2));
        var synced = await fixture.ReadUploadRowAsync(other);
        Assert.Equal("Synced", synced.Status);
        Assert.Null(synced.ErrorCode);
        Assert.Equal(now.AddSeconds(2).ToString("O", CultureInfo.InvariantCulture), synced.UploadedAt);
    }

    [Fact]
    public async Task ReleaseUploadWithoutAttemptAsync_returns_to_pending_and_takes_back_the_attempt()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var now = new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
        var guid = await fixture.InsertDailyCloseAsync();
        Assert.NotNull(await fixture.Repository.TryClaimUploadAsync(guid, "S001", "POS-01", now));

        await fixture.Repository.ReleaseUploadWithoutAttemptAsync(guid, "HTTP_401", "unauthorized");

        var row = await fixture.ReadUploadRowAsync(guid);
        Assert.Equal("Pending", row.Status);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.NextUploadAt);
        Assert.Equal("HTTP_401", row.ErrorCode);
        Assert.Equal([guid], await fixture.Repository.GetDueUploadGuidsAsync("S001", "POS-01", 20, now));
    }

    [Fact]
    public async Task GetArchiveForUploadAsync_returns_all_eleven_denominations_and_pads_missing_rows_with_zero()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guid = await fixture.InsertDailyCloseAsync();
        await fixture.ExecuteSqlAsync(
            "DELETE FROM LocalDailyCloseCashCounts WHERE DailyCloseGuid = $G AND DenominationValue = '2';",
            ("$G", guid.ToString()));

        var archive = await fixture.Repository.GetArchiveForUploadAsync(guid);

        Assert.NotNull(archive);
        Assert.Equal(DailyCloseService.AustralianDenominations.Count, archive.CashCounts.Count);
        Assert.Equal(
            DailyCloseService.AustralianDenominations.Select(item => item.Value),
            archive.CashCounts.Select(item => item.Value));
        Assert.Equal(1, archive.CashCounts.Single(item => item.Value == 100m).Quantity);
        Assert.Equal(3, archive.CashCounts.Single(item => item.Value == 0.05m).Quantity);
        Assert.Equal(0, archive.CashCounts.Single(item => item.Value == 2m).Quantity);
        Assert.Equal(100.15m, archive.CountedCashAmount);
        Assert.Equal("S001", archive.Report.StoreCode);
        Assert.Equal(new DateTime(2026, 5, 28), archive.Report.BusinessDate);
        Assert.Null(await fixture.Repository.GetArchiveForUploadAsync(Guid.NewGuid()));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<string, (string Type, bool NotNull, string? Default)>> ReadTableInfoAsync(
        SqliteConnection connection,
        string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        var columns = new Dictionary<string, (string, bool, string?)>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns[reader.GetString(1)] = (reader.GetString(2), reader.GetInt32(3) == 1, reader.IsDBNull(4) ? null : reader.GetString(4));
        }

        return columns;
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>读 (UploadStatus, UploadAttemptCount, NextUploadAt 是否为 NULL)。</summary>
    private static async Task<(string Status, long AttemptCount, bool NextUploadAtIsNull)> ReadUploadStateAsync(
        SqliteConnection connection,
        Guid guid)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT UploadStatus, UploadAttemptCount, NextUploadAt IS NULL FROM LocalDailyCloses WHERE DailyCloseGuid = $G;";
        command.Parameters.AddWithValue("$G", guid.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2) == 1);
    }
}
