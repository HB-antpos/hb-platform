using Hbpos.Client.Wpf.Services;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class LocalSchemaServiceTests
{
    [Fact]
    public async Task InitializeAsync_adds_frozen_promotion_and_manual_price_columns_to_existing_suspended_order_schema()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-suspended-order-schema-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await using (var connection = await store.OpenConnectionAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE SuspendedOrders (
                        SuspendedOrderGuid TEXT PRIMARY KEY,
                        StoreCode TEXT NOT NULL,
                        DeviceCode TEXT NOT NULL,
                        CashierId TEXT NOT NULL,
                        CashierName TEXT NOT NULL,
                        SuspendedAt TEXT NOT NULL,
                        TotalAmount TEXT NOT NULL,
                        DiscountAmount TEXT NOT NULL,
                        ActualAmount TEXT NOT NULL,
                        Status INTEGER NOT NULL
                    );
                    CREATE TABLE SuspendedOrderLines (
                        SuspendedOrderLineGuid TEXT PRIMARY KEY,
                        SuspendedOrderGuid TEXT NOT NULL,
                        StoreCode TEXT NOT NULL,
                        ProductCode TEXT NOT NULL,
                        ReferenceCode TEXT NULL,
                        DisplayName TEXT NOT NULL,
                        LookupCode TEXT NOT NULL,
                        ItemNumber TEXT NULL,
                        ProductImage TEXT NULL,
                        Quantity TEXT NOT NULL,
                        UnitPrice TEXT NOT NULL,
                        DiscountAmount TEXT NOT NULL,
                        ActualAmount TEXT NOT NULL,
                        PriceSource INTEGER NOT NULL,
                        PriceSourceLabel TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await new LocalSchemaService(store).InitializeAsync();

            await using var verification = await store.OpenConnectionAsync();
            Assert.True(await HasColumnAsync(verification, "SuspendedOrders", "FrozenPromotionRulesJson"));
            Assert.True(await HasColumnAsync(verification, "SuspendedOrderLines", "IsManualPrice"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_recovers_only_interrupted_order_uploads()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-local-schema-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var interruptedOrderGuid = Guid.NewGuid().ToString("D");
            var unrelatedOrderGuid = Guid.NewGuid().ToString("D");

            await schema.InitializeAsync();
            await using (var connection = await store.OpenConnectionAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO LocalOrders (
                        OrderGuid, StoreCode, DeviceCode, CashierId, CashierName, SoldAt,
                        TotalAmount, DiscountAmount, ActualAmount, SyncStatus)
                    VALUES
                        ($InterruptedOrderGuid, 'S001', 'POS-01', 'C001', 'Alice', '2026-07-21T10:00:00+10:00', '1.00', '0.00', '1.00', 'Syncing'),
                        ($UnrelatedOrderGuid, 'S001', 'POS-01', 'C001', 'Alice', '2026-07-21T10:01:00+10:00', '2.00', '0.00', '2.00', 'Syncing');

                    INSERT INTO SyncQueue (EntityId, EntityType, Status, CreatedAt)
                    VALUES
                        ($InterruptedOrderGuid, 'Order', 'Syncing', '2026-07-21T10:00:00+10:00'),
                        ($UnrelatedOrderGuid, 'Catalog', 'Syncing', '2026-07-21T10:01:00+10:00');
                    """;
                command.Parameters.AddWithValue("$InterruptedOrderGuid", interruptedOrderGuid);
                command.Parameters.AddWithValue("$UnrelatedOrderGuid", unrelatedOrderGuid);
                await command.ExecuteNonQueryAsync();
            }

            await schema.InitializeAsync();

            await using var verificationConnection = await store.OpenConnectionAsync();
            await using var verificationCommand = verificationConnection.CreateCommand();
            verificationCommand.CommandText =
                """
                SELECT
                    (SELECT SyncStatus FROM LocalOrders WHERE OrderGuid = $InterruptedOrderGuid),
                    (SELECT Status FROM SyncQueue WHERE EntityId = $InterruptedOrderGuid AND EntityType = 'Order'),
                    (SELECT SyncStatus FROM LocalOrders WHERE OrderGuid = $UnrelatedOrderGuid),
                    (SELECT Status FROM SyncQueue WHERE EntityId = $UnrelatedOrderGuid AND EntityType = 'Catalog');
                """;
            verificationCommand.Parameters.AddWithValue("$InterruptedOrderGuid", interruptedOrderGuid);
            verificationCommand.Parameters.AddWithValue("$UnrelatedOrderGuid", unrelatedOrderGuid);

            await using var reader = await verificationCommand.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Pending", reader.GetString(0));
            Assert.Equal("Pending", reader.GetString(1));
            Assert.Equal("Syncing", reader.GetString(2));
            Assert.Equal("Syncing", reader.GetString(3));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task EnsureInitializedAsync_initializes_each_database_file_only_once_per_process()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-local-schema-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orderGuid = Guid.NewGuid().ToString("D");

            await schema.EnsureInitializedAsync();
            await using (var connection = await store.OpenConnectionAsync())
            {
                await ExecuteSqlAsync(
                    connection,
                    $"""
                    INSERT INTO LocalOrders (
                        OrderGuid, StoreCode, DeviceCode, CashierId, CashierName, SoldAt,
                        TotalAmount, DiscountAmount, ActualAmount, SyncStatus)
                    VALUES ('{orderGuid}', 'S001', 'POS-01', 'C001', 'Alice', '2026-09-29T08:00:00+10:00', '1.00', '0.00', '1.00', 'Syncing');

                    INSERT INTO SyncQueue (EntityId, EntityType, Status, CreatedAt)
                    VALUES ('{orderGuid}', 'Order', 'Syncing', '2026-09-29T08:00:00+10:00');
                    """);
            }

            // 启动链路第二次调用（主界面初始化）直接返回：不再重复全表回填，也不会把进行中的上传误恢复成 Pending。
            await schema.EnsureInitializedAsync();
            await using (var connection = await store.OpenConnectionAsync())
            {
                Assert.Equal(
                    "Syncing",
                    await ScalarAsync(connection, $"SELECT SyncStatus FROM LocalOrders WHERE OrderGuid = '{orderGuid}';"));
            }

            // InitializeAsync 仍然每次完整执行，服务器热切换与模拟重启依赖这一点。
            await schema.InitializeAsync();
            await using (var connection = await store.OpenConnectionAsync())
            {
                Assert.Equal(
                    "Pending",
                    await ScalarAsync(connection, $"SELECT SyncStatus FROM LocalOrders WHERE OrderGuid = '{orderGuid}';"));
            }
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task EnsureInitializedAsync_does_not_remember_failed_attempts()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-local-schema-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();

            // Host 启动时的初始化失败只记日志；主界面初始化时必须重新完整执行一遍。
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => schema.EnsureInitializedAsync(canceled.Token));
            await schema.EnsureInitializedAsync();

            await using var connection = await store.OpenConnectionAsync();
            Assert.Contains(
                "LocalOrders",
                await QueryStringsAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table';"));
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task EnsureInitializedAsync_initializes_again_after_switching_database_file()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hbpos-local-schema-switch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var state = new ApiRuntimeEndpointState("https://first.example.com/pos-api/");
            var resolver = new ApiEndpointDatabasePartitionResolver(root, state.CurrentAddress.AbsoluteUri);
            var store = new LocalSqliteStore(state, resolver);
            var schema = new LocalSchemaService(store);
            await schema.EnsureInitializedAsync();

            var prepared = await store.PrepareSwitchAsync("https://second.example.com/pos-api/", CancellationToken.None);
            store.Switch(prepared);
            await schema.EnsureInitializedAsync();

            Assert.Equal(prepared.TargetDatabasePath, store.ActiveDatabasePath);
            await using var connection = await store.OpenConnectionAsync();
            Assert.Contains(
                "LocalOrders",
                await QueryStringsAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table';"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($TableName) WHERE name = $ColumnName;";
        command.Parameters.AddWithValue("$TableName", tableName);
        command.Parameters.AddWithValue("$ColumnName", columnName);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task InsertPublicationAsync(
        SqliteConnection connection,
        string holdGuid,
        string status,
        string? errorCode,
        string? shareRequestedAtIso,
        bool withRemote = false)
    {
        var payloadExpression = status is "PendingPublish" or "Published" ? "X'0102'" : "NULL";
        var remoteExpression = withRemote ? "7, '2026-07-28T00:00:00.000Z'" : "NULL, NULL";
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO SharedHeldOrderPublications (
                LocalHoldGuid, StoreCode, DeviceCode, Status, Revision, RetryCount,
                ErrorCode, ErrorMessage, PayloadCiphertext, HeldAtIso, CreatedAtIso, UpdatedAtIso,
                LastAttemptAtIso, NextAttemptAtIso, RemoteRevision, RemoteUpdatedAtIso, ShareRequestedAtIso)
            VALUES ($Guid, 'S001', 'POS-01', $Status, 1, 0, $ErrorCode, NULL,
                    {payloadExpression}, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z',
                    NULL, NULL, {remoteExpression}, $ShareRequestedAtIso);
            """;
        command.Parameters.AddWithValue("$Guid", holdGuid);
        command.Parameters.AddWithValue("$Status", status);
        command.Parameters.AddWithValue("$ErrorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$ShareRequestedAtIso", (object?)shareRequestedAtIso ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdatePublicationStatusAsync(
        SqliteConnection connection,
        string holdGuid,
        string status,
        string? shareRequestedAtIso)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE SharedHeldOrderPublications
            SET Status = $Status,
                ShareRequestedAtIso = $ShareRequestedAtIso,
                PayloadCiphertext = X'0102',
                UpdatedAtIso = '2026-07-28T00:03:00.000Z'
            WHERE LocalHoldGuid = $Guid;
            """;
        command.Parameters.AddWithValue("$Guid", holdGuid);
        command.Parameters.AddWithValue("$Status", status);
        command.Parameters.AddWithValue("$ShareRequestedAtIso", (object?)shareRequestedAtIso ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetShareRequestedAsync(
        SqliteConnection connection,
        string holdGuid,
        string? shareRequestedAtIso)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE SharedHeldOrderPublications
            SET ShareRequestedAtIso = $ShareRequestedAtIso,
                UpdatedAtIso = '2026-07-28T00:03:00.000Z'
            WHERE LocalHoldGuid = $Guid;
            """;
        command.Parameters.AddWithValue("$Guid", holdGuid);
        command.Parameters.AddWithValue("$ShareRequestedAtIso", (object?)shareRequestedAtIso ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertSqliteConstraintAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<SqliteException>(action);
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task InitializeAsync_creates_shared_held_order_schema_idempotently()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-shared-held-schema-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);

            await schema.InitializeAsync();
            await schema.InitializeAsync();

            await using var connection = await store.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT name
                FROM sqlite_master
                WHERE name IN (
                    'SharedHeldOrderPublications',
                    'SharedHeldOrderClaims',
                    'LocalOrderHeldOrderSources',
                    'UX_SharedHeldOrderClaims_OpenFence_PerDevice',
                    'UX_SharedHeldOrderClaims_ActivateKey',
                    'UX_SharedHeldOrderClaims_ReleaseKey',
                    'UX_SharedHeldOrderClaims_SupersedeKey',
                    'IX_SharedHeldOrderPublications_Due',
                    'IX_LocalOrderHeldOrderSources_Hold',
                    'TRG_SharedHeldOrderClaims_StatusMachine',
                    'TRG_SharedHeldOrderClaims_ActiveBindingOnly',
                    'TRG_SharedHeldOrderPublications_ShareRequestGate_Insert',
                    'TRG_SharedHeldOrderPublications_ShareRequestGate_Update',
                    'TRG_LocalOrderHeldOrderSources_Immutable')
                ORDER BY name;
                """;
            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }

            Assert.Equal(
                [
                    "IX_LocalOrderHeldOrderSources_Hold",
                    "IX_SharedHeldOrderPublications_Due",
                    "LocalOrderHeldOrderSources",
                    "SharedHeldOrderClaims",
                    "SharedHeldOrderPublications",
                    "TRG_LocalOrderHeldOrderSources_Immutable",
                    "TRG_SharedHeldOrderClaims_ActiveBindingOnly",
                    "TRG_SharedHeldOrderClaims_StatusMachine",
                    "TRG_SharedHeldOrderPublications_ShareRequestGate_Insert",
                    "TRG_SharedHeldOrderPublications_ShareRequestGate_Update",
                    "UX_SharedHeldOrderClaims_ActivateKey",
                    "UX_SharedHeldOrderClaims_OpenFence_PerDevice",
                    "UX_SharedHeldOrderClaims_ReleaseKey",
                    "UX_SharedHeldOrderClaims_SupersedeKey"
                ],
                names);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_shared_held_publication_states_align_with_ipad_m40()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-shared-held-states-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);

            await schema.InitializeAsync();

            await using var connection = await store.OpenConnectionAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO SharedHeldOrderPublications (
                        LocalHoldGuid, StoreCode, DeviceCode, Status, Revision, RetryCount,
                        ErrorCode, ErrorMessage, PayloadCiphertext, HeldAtIso, CreatedAtIso, UpdatedAtIso,
                        LastAttemptAtIso, NextAttemptAtIso, RemoteRevision, RemoteUpdatedAtIso, ShareRequestedAtIso)
                    VALUES
                        ('aaaaaaaa-0000-0000-0000-000000000001', 'S001', 'POS-01', 'NeedsEvaluation', 1, 0, NULL, NULL, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', NULL, NULL, NULL, NULL, NULL),
                        ('aaaaaaaa-0000-0000-0000-000000000002', 'S001', 'POS-01', 'PendingPublish', 1, 1, 'HttpTimeout', '上游超时', X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:30.000Z', '2026-07-28T00:01:00.000Z', NULL, NULL, '2026-07-28T00:00:00.000Z'),
                        ('aaaaaaaa-0000-0000-0000-000000000003', 'S001', 'POS-01', 'Published', 1, 0, NULL, NULL, X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', NULL, NULL, 7, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z'),
                        ('aaaaaaaa-0000-0000-0000-000000000004', 'S001', 'POS-01', 'Blocked', 1, 0, 'PromotionRulesMissing', '缺少冻结促销规则', NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', NULL, NULL, NULL, NULL, '2026-07-28T00:00:00.000Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            // 旧错误状态名、缺 payload/remote 的非法组合必须被 CHECK 拒绝。
            var invalidInserts = new[]
            {
                """
                INSERT INTO SharedHeldOrderPublications (
                    LocalHoldGuid, StoreCode, DeviceCode, Status, Revision, RetryCount,
                    ErrorCode, ErrorMessage, PayloadCiphertext, HeldAtIso, CreatedAtIso, UpdatedAtIso)
                VALUES ('aaaaaaaa-0000-0000-0000-000000000005', 'S001', 'POS-01', 'Pending', 1, 0, NULL, NULL, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                """,
                """
                INSERT INTO SharedHeldOrderPublications (
                    LocalHoldGuid, StoreCode, DeviceCode, Status, Revision, RetryCount,
                    ErrorCode, ErrorMessage, PayloadCiphertext, HeldAtIso, CreatedAtIso, UpdatedAtIso)
                VALUES ('aaaaaaaa-0000-0000-0000-000000000006', 'S001', 'POS-01', 'PendingPublish', 1, 0, NULL, NULL, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                """,
                """
                INSERT INTO SharedHeldOrderPublications (
                    LocalHoldGuid, StoreCode, DeviceCode, Status, Revision, RetryCount,
                    ErrorCode, ErrorMessage, PayloadCiphertext, HeldAtIso, CreatedAtIso, UpdatedAtIso)
                VALUES ('aaaaaaaa-0000-0000-0000-000000000007', 'S001', 'POS-01', 'Published', 1, 0, NULL, NULL, X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                """,
                """
                INSERT INTO SharedHeldOrderPublications (
                    LocalHoldGuid, StoreCode, DeviceCode, Status, Revision, RetryCount,
                    ErrorCode, ErrorMessage, PayloadCiphertext, HeldAtIso, CreatedAtIso, UpdatedAtIso,
                    RemoteRevision, RemoteUpdatedAtIso)
                VALUES ('aaaaaaaa-0000-0000-0000-000000000008', 'S001', 'POS-01', 'Published', 1, 0, NULL, NULL, X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', -1, '2026-07-28T00:00:00.000Z');
                """,
                """
                INSERT INTO SharedHeldOrderPublications (
                    LocalHoldGuid, StoreCode, DeviceCode, Status, Revision, RetryCount,
                    ErrorCode, ErrorMessage, PayloadCiphertext, HeldAtIso, CreatedAtIso, UpdatedAtIso,
                    RemoteRevision, RemoteUpdatedAtIso)
                VALUES ('aaaaaaaa-0000-0000-0000-000000000009', 'S001', 'POS-01', 'NeedsEvaluation', 1, 0, NULL, NULL, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', 3, '2026-07-28T00:00:00.000Z');
                """
            };

            foreach (var invalidInsert in invalidInserts)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = invalidInsert;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAsync_adds_or_repairs_share_requested_at_column_and_backfills_active_states(
        bool shareRequestColumnAlreadyExists)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-shared-held-request-schema-{shareRequestColumnAlreadyExists}-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            // 旧库 publication 表没有 ShareRequestedAtIso 列，且同时存在各状态数据。
            await using (var connection = await store.OpenConnectionAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE SharedHeldOrderPublications (
                        LocalHoldGuid TEXT PRIMARY KEY,
                        StoreCode TEXT NOT NULL,
                        DeviceCode TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        Revision INTEGER NOT NULL DEFAULT 1,
                        RetryCount INTEGER NOT NULL DEFAULT 0,
                        ErrorCode TEXT NULL,
                        ErrorMessage TEXT NULL,
                        PayloadCiphertext BLOB NULL,
                        HeldAtIso TEXT NOT NULL,
                        CreatedAtIso TEXT NOT NULL,
                        UpdatedAtIso TEXT NOT NULL,
                        LastAttemptAtIso TEXT NULL,
                        NextAttemptAtIso TEXT NULL,
                        RemoteRevision INTEGER NULL,
                        RemoteUpdatedAtIso TEXT NULL,
                        ConsumedAtIso TEXT NULL);

                    INSERT INTO SharedHeldOrderPublications (
                        LocalHoldGuid, StoreCode, DeviceCode, Status, Revision, RetryCount,
                        ErrorCode, ErrorMessage, PayloadCiphertext, HeldAtIso, CreatedAtIso, UpdatedAtIso,
                        RemoteRevision, RemoteUpdatedAtIso)
                    VALUES
                        ('aaaaaaaa-0000-0000-0000-000000000001', 'S001', 'POS-01', 'NeedsEvaluation', 1, 0, NULL, NULL, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', NULL, NULL),
                        ('aaaaaaaa-0000-0000-0000-000000000002', 'S001', 'POS-01', 'PendingPublish', 1, 1, 'HttpTimeout', '上游超时', X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', NULL, NULL),
                        ('aaaaaaaa-0000-0000-0000-000000000003', 'S001', 'POS-01', 'Published', 1, 0, NULL, NULL, X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', 7, '2026-07-28T00:00:00.000Z'),
                        ('aaaaaaaa-0000-0000-0000-000000000004', 'S001', 'POS-01', 'Blocked', 1, 0, 'PromotionRulesMissing', '缺少冻结促销规则', NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', NULL, NULL);
                    """;
                await command.ExecuteNonQueryAsync();

                if (shareRequestColumnAlreadyExists)
                {
                    // 模拟上次启动在 ALTER 成功、回填前中断；重启必须继续修复。
                    await using var partialMigration = connection.CreateCommand();
                    partialMigration.CommandText =
                        "ALTER TABLE SharedHeldOrderPublications ADD COLUMN ShareRequestedAtIso TEXT NULL;";
                    await partialMigration.ExecuteNonQueryAsync();
                }
            }

            // 幂等初始化：补列 + 回填 + 触发器都不得破坏旧数据。
            var schema = new LocalSchemaService(store);
            await schema.InitializeAsync();
            await schema.InitializeAsync();

            await using var verification = await store.OpenConnectionAsync();
            Assert.True(await HasColumnAsync(verification, "SharedHeldOrderPublications", "ShareRequestedAtIso"));
            await using var read = verification.CreateCommand();
            read.CommandText =
                """
                SELECT Status, ShareRequestedAtIso
                FROM SharedHeldOrderPublications
                ORDER BY LocalHoldGuid;
                """;
            await using var reader = await read.ExecuteReaderAsync();
            var rows = new List<(string Status, string? ShareRequestedAtIso)>();
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }

            Assert.Equal(4, rows.Count);
            // NeedsEvaluation 回填留空；PendingPublish/Published/Blocked 回填非空。
            Assert.Equal("NeedsEvaluation", rows[0].Status);
            Assert.Null(rows[0].ShareRequestedAtIso);
            Assert.All(rows.Skip(1), row =>
            {
                Assert.NotNull(row.ShareRequestedAtIso);
                Assert.Equal("2026-07-28T00:00:00.000Z", row.ShareRequestedAtIso);
            });
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_share_request_gate_is_fail_closed_and_request_time_is_immutable()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-shared-held-request-gate-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            await using var connection = await store.OpenConnectionAsync();

            // 未请求的 NeedsEvaluation（本地先存路径）与删除暂存 Blocked 例外允许。
            await InsertPublicationAsync(connection, "aaaaaaaa-0000-0000-0000-000000000001", "NeedsEvaluation", null, null);
            await InsertPublicationAsync(connection, "aaaaaaaa-0000-0000-0000-000000000002", "Blocked", "LOCAL_DELETE_PENDING_REMOTE", null);
            await InsertPublicationAsync(connection, "aaaaaaaa-0000-0000-0000-000000000003", "Blocked", "LOCAL_DELETE_PENDING_LOCAL", null);
            // 已请求的 NeedsEvaluation（TryRequestShareAsync 路径）允许。
            await InsertPublicationAsync(connection, "aaaaaaaa-0000-0000-0000-000000000004", "NeedsEvaluation", null, "2026-07-28T00:00:00.000Z");

            // 未请求不得进入 PendingPublish/Published/普通 Blocked（fail-closed）。
            await AssertSqliteConstraintAsync(() =>
                InsertPublicationAsync(connection, "aaaaaaaa-0000-0000-0000-000000000005", "PendingPublish", null, null));
            await AssertSqliteConstraintAsync(() =>
                InsertPublicationAsync(connection, "aaaaaaaa-0000-0000-0000-000000000006", "Published", null, null, withRemote: true));
            await AssertSqliteConstraintAsync(() =>
                InsertPublicationAsync(connection, "aaaaaaaa-0000-0000-0000-000000000007", "Blocked", "PromotionRulesMissing", null));
            // SQLite 的 NULL NOT IN 结果是 UNKNOWN；NULL ErrorCode 也必须 fail-closed。
            await AssertSqliteConstraintAsync(() =>
                InsertPublicationAsync(connection, "aaaaaaaa-0000-0000-0000-00000000000a", "Blocked", null, null));
            // 空白字符串不构成共享意图，不能绕过数据库闸门；NeedsEvaluation 本身也不接受坏值。
            await AssertSqliteConstraintAsync(() =>
                InsertPublicationAsync(connection, "aaaaaaaa-0000-0000-0000-00000000000b", "PendingPublish", null, "   "));
            await AssertSqliteConstraintAsync(() =>
                InsertPublicationAsync(connection, "aaaaaaaa-0000-0000-0000-00000000000c", "NeedsEvaluation", null, "   "));

            // 已请求的 PendingPublish/普通 Blocked 允许。
            await InsertPublicationAsync(connection, "aaaaaaaa-0000-0000-0000-000000000008", "PendingPublish", null, "2026-07-28T00:00:00.000Z");
            await InsertPublicationAsync(connection, "aaaaaaaa-0000-0000-0000-000000000009", "Blocked", "PromotionRulesMissing", "2026-07-28T00:00:00.000Z");

            // UPDATE 同样 fail-closed：未请求的 NeedsEvaluation 不能进入 PendingPublish。
            await AssertSqliteConstraintAsync(() => UpdatePublicationStatusAsync(
                connection,
                "aaaaaaaa-0000-0000-0000-000000000001",
                "PendingPublish",
                null));

            // 未请求 -> 设置请求时间允许（幂等 request 入口），之后再进入 PendingPublish 也允许。
            await SetShareRequestedAsync(connection, "aaaaaaaa-0000-0000-0000-000000000001", "2026-07-28T00:01:00.000Z");
            await UpdatePublicationStatusAsync(
                connection,
                "aaaaaaaa-0000-0000-0000-000000000001",
                "PendingPublish",
                "2026-07-28T00:01:00.000Z");

            // 请求时间一旦非空不可改写或清空。
            await AssertSqliteConstraintAsync(() => SetShareRequestedAsync(
                connection,
                "aaaaaaaa-0000-0000-0000-000000000004",
                "2026-07-28T00:02:00.000Z"));
            await AssertSqliteConstraintAsync(() => SetShareRequestedAsync(
                connection,
                "aaaaaaaa-0000-0000-0000-000000000004",
                null));
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_shared_held_claims_enforce_hold_guid_source_keys_and_state_consistency()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-shared-held-claim-schema-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);

            await schema.InitializeAsync();

            await using var connection = await store.OpenConnectionAsync();

            // 新列族：HoldGuid、Source、三把幂等键，旧 IdempotencyKey 单列不存在。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(SharedHeldOrderClaims);";
                var columns = new List<string>();
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(1));
                }

                Assert.Contains("HoldGuid", columns);
                Assert.Contains("Source", columns);
                Assert.Contains("PrepareIdempotencyKey", columns);
                Assert.Contains("ActivateIdempotencyKey", columns);
                Assert.Contains("ReleaseIdempotencyKey", columns);
                Assert.Contains("SupersedeIdempotencyKey", columns);
                Assert.DoesNotContain(columns, column => string.Equals(column, "IdempotencyKey", StringComparison.Ordinal));
            }

            // 合法 Prepared 行可插入。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO SharedHeldOrderClaims (
                        ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                        PrepareIdempotencyKey, PayloadCiphertext, CreatedAtIso, UpdatedAtIso)
                    VALUES (
                        'bbbbbbbb-0000-0000-0000-000000000001', 'bbbbbbbb-0000-0000-0000-000000000011',
                        'S001', 'POS-01', 'OfflineOrigin', 'Prepared',
                        'idem-prepare-1', X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            // 状态一致性 CHECK：Prepared 带 activate key / Active 缺 activate key /
            // Completed 缺绑定 / Released 带绑定，全部拒绝；payload 只允许密文列。
            var invalidInserts = new[]
            {
                """
                INSERT INTO SharedHeldOrderClaims (
                    ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                    PrepareIdempotencyKey, ActivateIdempotencyKey, PayloadCiphertext, CreatedAtIso, UpdatedAtIso)
                VALUES (
                    'bbbbbbbb-0000-0000-0000-000000000002', 'bbbbbbbb-0000-0000-0000-000000000012',
                    'S001', 'POS-02', 'OfflineOrigin', 'Prepared',
                    'idem-invalid-1', 'idem-act', X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                """,
                """
                INSERT INTO SharedHeldOrderClaims (
                    ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                    PrepareIdempotencyKey, PayloadCiphertext, CreatedAtIso, UpdatedAtIso)
                VALUES (
                    'bbbbbbbb-0000-0000-0000-000000000003', 'bbbbbbbb-0000-0000-0000-000000000013',
                    'S001', 'POS-02', 'RemoteClaim', 'Active',
                    'idem-invalid-2', X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                """,
                """
                INSERT INTO SharedHeldOrderClaims (
                    ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                    PrepareIdempotencyKey, ActivateIdempotencyKey, ReleaseIdempotencyKey,
                    PayloadCiphertext, CreatedAtIso, UpdatedAtIso)
                VALUES (
                    'bbbbbbbb-0000-0000-0000-000000000004', 'bbbbbbbb-0000-0000-0000-000000000014',
                    'S001', 'POS-02', 'RemoteClaim', 'Completed',
                    'idem-invalid-3', 'idem-act', 'idem-release', X'0102',
                    '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                """,
                """
                INSERT INTO SharedHeldOrderClaims (
                    ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                    PrepareIdempotencyKey, ActivateIdempotencyKey, ReleaseIdempotencyKey,
                    PayloadCiphertext, BoundOrderGuid, CreatedAtIso, UpdatedAtIso)
                VALUES (
                    'bbbbbbbb-0000-0000-0000-000000000005', 'bbbbbbbb-0000-0000-0000-000000000015',
                    'S001', 'POS-02', 'RemoteClaim', 'Released',
                    'idem-invalid-4', 'idem-act', 'idem-release', X'0102',
                    'bbbbbbbb-0000-0000-0000-000000000099',
                    '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                """,
                """
                INSERT INTO SharedHeldOrderClaims (
                    ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                    PrepareIdempotencyKey, ActivateIdempotencyKey, ReleaseIdempotencyKey,
                    PayloadCiphertext, BoundOrderGuid, CreatedAtIso, UpdatedAtIso)
                VALUES (
                    'bbbbbbbb-0000-0000-0000-000000000006', 'bbbbbbbb-0000-0000-0000-000000000016',
                    'S001', 'POS-02', 'RemoteClaim', 'Superseded',
                    'idem-invalid-5', 'idem-act', 'idem-release', X'0102',
                    'bbbbbbbb-0000-0000-0000-000000000099',
                    '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                """,
                """
                INSERT INTO SharedHeldOrderClaims (
                    ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                    PrepareIdempotencyKey, ActivateIdempotencyKey, PayloadCiphertext,
                    ServerRevision, CreatedAtIso, UpdatedAtIso)
                VALUES (
                    'bbbbbbbb-0000-0000-0000-000000000007', 'bbbbbbbb-0000-0000-0000-000000000017',
                    'S001', 'POS-02', 'RemoteClaim', 'Active',
                    'idem-invalid-6', 'idem-act', X'0102',
                    -1, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                """,
                """
                INSERT INTO SharedHeldOrderClaims (
                    ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                    PrepareIdempotencyKey, PayloadCiphertext, CreatedAtIso, UpdatedAtIso)
                VALUES (
                    'bbbbbbbb-0000-0000-0000-000000000008', 'bbbbbbbb-0000-0000-0000-000000000018',
                    'S001', 'POS-02', 'OfflineOrigin', 'Prepared',
                    'idem-invalid-7', X'', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                """,
                """
                INSERT INTO SharedHeldOrderClaims (
                    ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                    PrepareIdempotencyKey, ActivateIdempotencyKey, PayloadCiphertext,
                    CreatedAtIso, UpdatedAtIso)
                VALUES (
                    'bbbbbbbb-0000-0000-0000-000000000009', 'bbbbbbbb-0000-0000-0000-000000000019',
                    'S001', 'POS-02', 'RemoteClaim', 'Active',
                    'idem-invalid-8', '   ', X'0102',
                    '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                """
            };

            foreach (var invalidInsert in invalidInserts)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = invalidInsert;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }

            // Superseded 终态允许直接插入：必须带 supersede key 且 release/bound 全空；
            // activate key 可为空（Prepared 调和）或非空（Active 调和）。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO SharedHeldOrderClaims (
                        ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                        PrepareIdempotencyKey, SupersedeIdempotencyKey, PayloadCiphertext, CreatedAtIso, UpdatedAtIso)
                    VALUES (
                        'bbbbbbbb-0000-0000-0000-00000000000a', 'bbbbbbbb-0000-0000-0000-00000000001a',
                        'S001', 'POS-01', 'RemoteClaim', 'Superseded',
                        'idem-superseded', 'supersede-1', X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            // Active 调和后的 Superseded 保留 activate key（release/bound 仍必须为空）。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO SharedHeldOrderClaims (
                        ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                        PrepareIdempotencyKey, ActivateIdempotencyKey, SupersedeIdempotencyKey,
                        PayloadCiphertext, CreatedAtIso, UpdatedAtIso)
                    VALUES (
                        'bbbbbbbb-0000-0000-0000-00000000000d', 'bbbbbbbb-0000-0000-0000-00000000001d',
                        'S001', 'POS-04', 'RemoteClaim', 'Superseded',
                        'idem-superseded-active', 'idem-act-superseded', 'supersede-2',
                        X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            // Superseded 缺 supersede key 或带 release/bound 均拒绝。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO SharedHeldOrderClaims (
                        ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                        PrepareIdempotencyKey, PayloadCiphertext, CreatedAtIso, UpdatedAtIso)
                    VALUES (
                        'bbbbbbbb-0000-0000-0000-00000000000e', 'bbbbbbbb-0000-0000-0000-00000000001e',
                        'S001', 'POS-05', 'RemoteClaim', 'Superseded',
                        'idem-superseded-missing-key', X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                    """;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }

            // partial unique fence：同 store+device 第二个 Prepared/Active 拒绝。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO SharedHeldOrderClaims (
                        ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                        PrepareIdempotencyKey, PayloadCiphertext, CreatedAtIso, UpdatedAtIso)
                    VALUES (
                        'bbbbbbbb-0000-0000-0000-00000000000b', 'bbbbbbbb-0000-0000-0000-00000000001b',
                        'S001', 'POS-01', 'OfflineOrigin', 'Prepared',
                        'idem-fence-2', X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                    """;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }

            // activate/release 键全局唯一：跨 claim 重复被拒绝（输家）。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO SharedHeldOrderClaims (
                        ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                        PrepareIdempotencyKey, ActivateIdempotencyKey, PayloadCiphertext,
                        CreatedAtIso, UpdatedAtIso)
                    VALUES (
                        'bbbbbbbb-0000-0000-0000-00000000000c', 'bbbbbbbb-0000-0000-0000-00000000001c',
                        'S001', 'POS-03', 'RemoteClaim', 'Active',
                        'idem-key-win', 'idem-act-dup', X'0102',
                        '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO SharedHeldOrderClaims (
                        ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                        PrepareIdempotencyKey, ActivateIdempotencyKey, PayloadCiphertext,
                        CreatedAtIso, UpdatedAtIso)
                    VALUES (
                        'bbbbbbbb-0000-0000-0000-00000000000d', 'bbbbbbbb-0000-0000-0000-00000000001d',
                        'S001', 'POS-04', 'RemoteClaim', 'Active',
                        'idem-key-lose', 'idem-act-dup', X'0102',
                        '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                    """;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO SharedHeldOrderClaims (
                        ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                        PrepareIdempotencyKey, ActivateIdempotencyKey, ReleaseIdempotencyKey,
                        PayloadCiphertext, CreatedAtIso, UpdatedAtIso)
                    VALUES (
                        'bbbbbbbb-0000-0000-0000-00000000000e', 'bbbbbbbb-0000-0000-0000-00000000001e',
                        'S001', 'POS-03', 'RemoteClaim', 'Released',
                        'idem-rel-win', 'idem-act-rel-win', 'idem-rel-dup', X'0102',
                        '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO SharedHeldOrderClaims (
                        ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                        PrepareIdempotencyKey, ActivateIdempotencyKey, ReleaseIdempotencyKey,
                        PayloadCiphertext, CreatedAtIso, UpdatedAtIso)
                    VALUES (
                        'bbbbbbbb-0000-0000-0000-00000000000f', 'bbbbbbbb-0000-0000-0000-00000000001f',
                        'S001', 'POS-04', 'RemoteClaim', 'Released',
                        'idem-rel-lose', 'idem-act-rel-lose', 'idem-rel-dup', X'0102',
                        '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                    """;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }

            // transition trigger：Active 行状态不变时只允许首次绑定，
            // 其余 Active -> Active 触碰被拒绝。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    UPDATE SharedHeldOrderClaims
                    SET Status = 'Active',
                        ActivateIdempotencyKey = 'idem-act-reopen',
                        UpdatedAtIso = '2026-07-28T00:00:01.000Z'
                    WHERE ClaimId = 'bbbbbbbb-0000-0000-0000-00000000000c';
                    """;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }
            await using (var command = connection.CreateCommand())
            {
                // 合法 Completed 行可插入；但 Completed 是终态，重开为 Active 被 trigger 拒绝。
                command.CommandText =
                    """
                    INSERT INTO SharedHeldOrderClaims (
                        ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                        PrepareIdempotencyKey, ActivateIdempotencyKey, ReleaseIdempotencyKey,
                        PayloadCiphertext, BoundOrderGuid, CreatedAtIso, UpdatedAtIso)
                    VALUES (
                        'bbbbbbbb-0000-0000-0000-000000000010', 'bbbbbbbb-0000-0000-0000-000000000020',
                        'S001', 'POS-05', 'RemoteClaim', 'Completed',
                        'idem-complete', 'idem-act-complete', 'idem-rel-complete', X'0102',
                        'bbbbbbbb-0000-0000-0000-000000000099',
                        '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    UPDATE SharedHeldOrderClaims
                    SET Status = 'Active',
                        ReleaseIdempotencyKey = NULL,
                        BoundOrderGuid = NULL,
                        UpdatedAtIso = '2026-07-28T00:00:01.000Z'
                    WHERE ClaimId = 'bbbbbbbb-0000-0000-0000-000000000010';
                    """;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    UPDATE SharedHeldOrderClaims
                    SET Status = 'Prepared',
                        ActivateIdempotencyKey = NULL,
                        ReleaseIdempotencyKey = NULL,
                        UpdatedAtIso = '2026-07-28T00:00:01.000Z'
                    WHERE ClaimId = 'bbbbbbbb-0000-0000-0000-00000000000e';
                    """;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }
            await using (var command = connection.CreateCommand())
            {
                // Active -> Active 仅允许首次绑定；完成后再次触碰 Active 行被拒绝。
                command.CommandText =
                    """
                    UPDATE SharedHeldOrderClaims
                    SET BoundOrderGuid = 'bbbbbbbb-0000-0000-0000-000000000099',
                        UpdatedAtIso = '2026-07-28T00:00:01.000Z'
                    WHERE ClaimId = 'bbbbbbbb-0000-0000-0000-00000000000c';
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    UPDATE SharedHeldOrderClaims
                    SET UpdatedAtIso = '2026-07-28T00:00:02.000Z'
                    WHERE ClaimId = 'bbbbbbbb-0000-0000-0000-00000000000c';
                    """;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_publication_consumed_column_and_order_source_table_are_idempotent()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-held-source-schema-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);

            await schema.InitializeAsync();
            await schema.InitializeAsync();

            await using var connection = await store.OpenConnectionAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(SharedHeldOrderPublications);";
                var columns = new List<string>();
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(1));
                }

                Assert.Contains("ConsumedAtIso", columns);
            }

            // 订单来源表：RemoteClaim(1) 必须带 claim；OfflineOrigin(2) 必须不带 claim。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO LocalOrders (
                        OrderGuid, StoreCode, DeviceCode, CashierId, CashierName, SoldAt,
                        TotalAmount, DiscountAmount, ActualAmount, SyncStatus)
                    VALUES
                        ('cccccccc-0000-0000-0000-000000000001', 'S001', 'POS-01', 'C001', 'Alice', '2026-07-28T00:00:00+00:00', '11.00', '0.00', '11.00', 'Pending'),
                        ('cccccccc-0000-0000-0000-000000000002', 'S001', 'POS-01', 'C001', 'Alice', '2026-07-28T00:01:00+00:00', '11.00', '0.00', '11.00', 'Pending');

                    INSERT INTO LocalOrderHeldOrderSources (
                        OrderGuid, HoldGuid, ClaimGuid, SourceKind, CreatedAtIso)
                    VALUES
                        ('cccccccc-0000-0000-0000-000000000001', 'cccccccc-0000-0000-0000-000000000010', 'cccccccc-0000-0000-0000-000000000020', 1, '2026-07-28T00:00:00.000Z'),
                        ('cccccccc-0000-0000-0000-000000000002', 'cccccccc-0000-0000-0000-000000000011', NULL, 2, '2026-07-28T00:01:00.000Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            // 来源行不可变：任何列改写都被 trigger 拒绝。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    UPDATE LocalOrderHeldOrderSources
                    SET HoldGuid = 'cccccccc-0000-0000-0000-000000000099'
                    WHERE OrderGuid = 'cccccccc-0000-0000-0000-000000000001';
                    """;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }

            // RemoteClaim 缺 claim / OfflineOrigin 带 claim 的非法组合被 CHECK 拒绝。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO LocalOrderHeldOrderSources (
                        OrderGuid, HoldGuid, ClaimGuid, SourceKind, CreatedAtIso)
                    VALUES (
                        'cccccccc-0000-0000-0000-000000000003', 'cccccccc-0000-0000-0000-000000000012', NULL, 1, '2026-07-28T00:00:00.000Z');
                    """;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO LocalOrderHeldOrderSources (
                        OrderGuid, HoldGuid, ClaimGuid, SourceKind, CreatedAtIso)
                    VALUES (
                        'cccccccc-0000-0000-0000-000000000004', 'cccccccc-0000-0000-0000-000000000013', 'cccccccc-0000-0000-0000-000000000021', 2, '2026-07-28T00:00:00.000Z');
                    """;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_supersede_transition_keeps_activate_key_and_blocks_bound_active()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-supersede-schema-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);

            await schema.InitializeAsync();

            await using var connection = await store.OpenConnectionAsync();
            // 两个 Prepared claim：一个原样 supersede（activate 空），一个先激活再 supersede。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO SharedHeldOrderClaims (
                        ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                        PrepareIdempotencyKey, PayloadCiphertext, CreatedAtIso, UpdatedAtIso)
                    VALUES
                        ('dddddddd-0000-0000-0000-000000000001', 'dddddddd-0000-0000-0000-000000000011', 'S001', 'POS-01', 'OfflineOrigin', 'Prepared', 'idem-p-1', X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z'),
                        ('dddddddd-0000-0000-0000-000000000002', 'dddddddd-0000-0000-0000-000000000012', 'S001', 'POS-02', 'RemoteClaim', 'Prepared', 'idem-p-2', X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z'),
                        ('dddddddd-0000-0000-0000-000000000003', 'dddddddd-0000-0000-0000-000000000013', 'S001', 'POS-03', 'RemoteClaim', 'Prepared', 'idem-p-3', X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            // Prepared -> Superseded：activate 保持空。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    UPDATE SharedHeldOrderClaims
                    SET Status = 'Superseded',
                        SupersedeIdempotencyKey = 'supersede-prepared',
                        UpdatedAtIso = '2026-07-28T00:01:00.000Z'
                    WHERE ClaimId = 'dddddddd-0000-0000-0000-000000000001';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            // Active -> Superseded：必须保留原 activate key。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    UPDATE SharedHeldOrderClaims
                    SET Status = 'Active',
                        ActivateIdempotencyKey = 'idem-act-2',
                        UpdatedAtIso = '2026-07-28T00:00:30.000Z'
                    WHERE ClaimId = 'dddddddd-0000-0000-0000-000000000002';
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    UPDATE SharedHeldOrderClaims
                    SET Status = 'Superseded',
                        SupersedeIdempotencyKey = 'supersede-active',
                        UpdatedAtIso = '2026-07-28T00:01:00.000Z'
                    WHERE ClaimId = 'dddddddd-0000-0000-0000-000000000002';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT ActivateIdempotencyKey, SupersedeIdempotencyKey
                    FROM SharedHeldOrderClaims
                    WHERE ClaimId = 'dddddddd-0000-0000-0000-000000000002';
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("idem-act-2", reader.GetString(0));
                Assert.Equal("supersede-active", reader.GetString(1));
            }

            // Active 已绑定订单不允许 supersede（trigger 拒绝带绑定的 Superseded）。
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    UPDATE SharedHeldOrderClaims
                    SET Status = 'Active',
                        ActivateIdempotencyKey = 'idem-act-3',
                        UpdatedAtIso = '2026-07-28T00:00:30.000Z'
                    WHERE ClaimId = 'dddddddd-0000-0000-0000-000000000003';
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    UPDATE SharedHeldOrderClaims
                    SET BoundOrderGuid = 'dddddddd-0000-0000-0000-000000000099',
                        UpdatedAtIso = '2026-07-28T00:00:40.000Z'
                    WHERE ClaimId = 'dddddddd-0000-0000-0000-000000000003';
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    UPDATE SharedHeldOrderClaims
                    SET Status = 'Superseded',
                        SupersedeIdempotencyKey = 'supersede-bound',
                        UpdatedAtIso = '2026-07-28T00:01:00.000Z'
                    WHERE ClaimId = 'dddddddd-0000-0000-0000-000000000003';
                    """;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_migrates_legacy_shared_held_claims_preserving_data_and_enabling_supersede()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-legacy-migrate-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await using (var connection = await store.OpenConnectionAsync())
            {
                await ExecuteSqlAsync(connection, LegacySharedHeldOrderPublicationsSchema);
                await ExecuteSqlAsync(connection, LegacySharedHeldOrderClaimsSchema);
                await ExecuteSqlAsync(connection, LegacySharedHeldOrderClaimsSeed);
                await ExecuteSqlAsync(connection, LegacySharedHeldOrderPublicationsSeed);

                // 旧表确实没有 SupersedeIdempotencyKey。
                var legacyColumns = await ReadColumnNamesAsync(connection, "SharedHeldOrderClaims");
                Assert.DoesNotContain(
                    legacyColumns,
                    column => string.Equals(column, "SupersedeIdempotencyKey", StringComparison.Ordinal));

                // 迁移前复现 bug：旧 trigger 阻止 Active -> Superseded。
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        """
                        UPDATE SharedHeldOrderClaims
                        SET Status = 'Superseded',
                            ActivateIdempotencyKey = NULL,
                            UpdatedAtIso = '2026-07-28T02:00:00.000Z'
                        WHERE ClaimId = 'eeeeeeee-0000-0000-0000-000000000002';
                        """;
                    var exception = await Assert.ThrowsAsync<SqliteException>(
                        () => command.ExecuteNonQueryAsync());
                    Assert.Equal(19, exception.SqliteErrorCode);
                }
            }

            var schema = new LocalSchemaService(store);
            await schema.InitializeAsync();

            List<string> snapshotBeforeSecondInit;
            await using (var connection = await store.OpenConnectionAsync())
            {
                // 新列生效，旧同名 trigger/index 已被替换，legacy 换名表无残留。
                var columns = await ReadColumnNamesAsync(connection, "SharedHeldOrderClaims");
                Assert.Contains("SupersedeIdempotencyKey", columns);

                var objects = await QueryStringsAsync(
                    connection,
                    """
                    SELECT name
                    FROM sqlite_master
                    WHERE type IN ('index', 'trigger')
                      AND name IN (
                        'UX_SharedHeldOrderClaims_OpenFence_PerDevice',
                        'UX_SharedHeldOrderClaims_ActivateKey',
                        'UX_SharedHeldOrderClaims_ReleaseKey',
                        'UX_SharedHeldOrderClaims_SupersedeKey',
                        'IX_SharedHeldOrderClaims_MineRecovery',
                        'TRG_SharedHeldOrderClaims_StatusMachine',
                        'TRG_SharedHeldOrderClaims_ActiveBindingOnly')
                    ORDER BY name;
                    """);
                Assert.Equal(
                    new[]
                    {
                        "IX_SharedHeldOrderClaims_MineRecovery",
                        "TRG_SharedHeldOrderClaims_ActiveBindingOnly",
                        "TRG_SharedHeldOrderClaims_StatusMachine",
                        "UX_SharedHeldOrderClaims_ActivateKey",
                        "UX_SharedHeldOrderClaims_OpenFence_PerDevice",
                        "UX_SharedHeldOrderClaims_ReleaseKey",
                        "UX_SharedHeldOrderClaims_SupersedeKey"
                    },
                    objects);

                var statusMachineSql = (string?)await ScalarAsync(
                    connection,
                    """
                    SELECT sql
                    FROM sqlite_master
                    WHERE type = 'trigger' AND name = 'TRG_SharedHeldOrderClaims_StatusMachine';
                    """);
                Assert.Contains("NEW.Status = 'Superseded'", statusMachineSql, StringComparison.Ordinal);

                Assert.Equal(
                    0,
                    Convert.ToInt32(
                        await ScalarAsync(
                            connection,
                            """
                            SELECT COUNT(*)
                            FROM sqlite_master
                            WHERE type = 'table' AND name = 'SharedHeldOrderClaims_legacy';
                            """)));

                // 所有兼容行/密文/keys/revision/time 原值保留；
                // 旧 Superseded 行补稳定 migration-only key，其他行 supersede key 保持 NULL。
                var rows = await ReadClaimRowsAsync(connection);
                Assert.Equal(
                    ToKeyStrings(
                        [
                            new[]
                            {
                                "eeeeeeee-0000-0000-0000-000000000001", "eeeeeeee-0000-0000-0000-000000000011",
                                "S001", "POS-01", "OfflineOrigin", "Prepared", "idem-prep-1",
                                "<NULL>", "<NULL>", "<NULL>", "01020304", "<NULL>",
                                "2026-07-30T00:00:00.000Z", "<NULL>",
                                "2026-07-28T00:00:00.000Z", "2026-07-28T01:00:00.000Z"
                            },
                            new[]
                            {
                                "eeeeeeee-0000-0000-0000-000000000002", "eeeeeeee-0000-0000-0000-000000000012",
                                "S001", "POS-02", "RemoteClaim", "Active", "idem-prep-2",
                                "idem-act-2", "<NULL>", "<NULL>", "0506", "7",
                                "2026-07-30T01:00:00.000Z", "<NULL>",
                                "2026-07-28T00:00:00.000Z", "2026-07-28T01:00:00.000Z"
                            },
                            new[]
                            {
                                "eeeeeeee-0000-0000-0000-000000000003", "eeeeeeee-0000-0000-0000-000000000013",
                                "S001", "POS-03", "RemoteClaim", "Active", "idem-prep-3",
                                "idem-act-3", "<NULL>", "<NULL>", "070809", "8",
                                "2026-07-30T02:00:00.000Z", "eeeeeeee-0000-0000-0000-000000000099",
                                "2026-07-28T00:00:00.000Z", "2026-07-28T01:00:00.000Z"
                            },
                            new[]
                            {
                                "eeeeeeee-0000-0000-0000-000000000004", "eeeeeeee-0000-0000-0000-000000000014",
                                "S001", "POS-04", "RemoteClaim", "Completed", "idem-prep-4",
                                "idem-act-4", "idem-rel-4", "<NULL>", "0A0B", "9",
                                "2026-07-30T03:00:00.000Z", "eeeeeeee-0000-0000-0000-000000000098",
                                "2026-07-28T00:00:00.000Z", "2026-07-28T01:00:00.000Z"
                            },
                            new[]
                            {
                                "eeeeeeee-0000-0000-0000-000000000005", "eeeeeeee-0000-0000-0000-000000000015",
                                "S001", "POS-05", "OfflineOrigin", "Superseded", "idem-prep-5",
                                "<NULL>", "<NULL>", "migrated-supersede:eeeeeeee-0000-0000-0000-000000000005",
                                "0C0D", "5", "2026-07-30T04:00:00.000Z", "<NULL>",
                                "2026-07-28T00:00:00.000Z", "2026-07-28T01:00:00.000Z"
                            }
                        ]).OrderBy(value => value, StringComparer.Ordinal),
                    ToKeyStrings(rows).OrderBy(value => value, StringComparer.Ordinal));

                // 旧 publication 表无损：ConsumedAtIso 后补列，行数据原样保留。
                var publicationColumns = await ReadColumnNamesAsync(connection, "SharedHeldOrderPublications");
                Assert.Contains("ConsumedAtIso", publicationColumns);
                var publicationRow = await QuerySingleRowAsync(
                    connection,
                    """
                    SELECT LocalHoldGuid, Status, HEX(PayloadCiphertext), RemoteRevision, RemoteUpdatedAtIso
                    FROM SharedHeldOrderPublications
                    WHERE LocalHoldGuid = 'ffffffff-0000-0000-0000-000000000001';
                    """);
                Assert.Equal(
                    new[] { "ffffffff-0000-0000-0000-000000000001", "Published", "DEADBEEF", "7", "2026-07-28T00:00:00.000Z" },
                    publicationRow);

                // 未绑定 Active -> Superseded：保留原 activate key 并成功。
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        """
                        UPDATE SharedHeldOrderClaims
                        SET Status = 'Superseded',
                            SupersedeIdempotencyKey = 'supersede-active-2',
                            UpdatedAtIso = '2026-07-28T02:00:00.000Z'
                        WHERE ClaimId = 'eeeeeeee-0000-0000-0000-000000000002';
                        """;
                    await command.ExecuteNonQueryAsync();
                }
                var supersededActive = await QuerySingleRowAsync(
                    connection,
                    """
                    SELECT Status, ActivateIdempotencyKey, SupersedeIdempotencyKey
                    FROM SharedHeldOrderClaims
                    WHERE ClaimId = 'eeeeeeee-0000-0000-0000-000000000002';
                    """);
                Assert.Equal(
                    new[] { "Superseded", "idem-act-2", "supersede-active-2" },
                    supersededActive);

                // 已绑定 Active -> Superseded 仍被拒绝。
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        """
                        UPDATE SharedHeldOrderClaims
                        SET Status = 'Superseded',
                            SupersedeIdempotencyKey = 'supersede-bound-3',
                            UpdatedAtIso = '2026-07-28T02:00:00.000Z'
                        WHERE ClaimId = 'eeeeeeee-0000-0000-0000-000000000003';
                        """;
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(19, exception.SqliteErrorCode);
            }

            snapshotBeforeSecondInit = ToKeyStrings(await ReadClaimRowsAsync(connection));
            }

            // 重复 Initialize 幂等：不重建、不丢行、不重写 migration key。
            await schema.InitializeAsync();

            await using (var connection = await store.OpenConnectionAsync())
            {
                Assert.Equal(
                    snapshotBeforeSecondInit,
                    ToKeyStrings(await ReadClaimRowsAsync(connection)));

                // 新 CHECK 生效：Superseded 缺 supersede key 拒绝。
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        """
                        INSERT INTO SharedHeldOrderClaims (
                            ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                            PrepareIdempotencyKey, PayloadCiphertext, CreatedAtIso, UpdatedAtIso)
                        VALUES (
                            'eeeeeeee-0000-0000-0000-000000000006', 'eeeeeeee-0000-0000-0000-000000000016',
                            'S001', 'POS-06', 'RemoteClaim', 'Superseded',
                            'idem-prep-6', X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T01:00:00.000Z');
                        """;
                    var exception = await Assert.ThrowsAsync<SqliteException>(
                        () => command.ExecuteNonQueryAsync());
                    Assert.Equal(19, exception.SqliteErrorCode);
                }

                // 新 CHECK 允许 Active 调和后的 Superseded（activate + supersede 键）。
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        """
                        INSERT INTO SharedHeldOrderClaims (
                            ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                            PrepareIdempotencyKey, ActivateIdempotencyKey, SupersedeIdempotencyKey,
                            PayloadCiphertext, CreatedAtIso, UpdatedAtIso)
                        VALUES (
                            'eeeeeeee-0000-0000-0000-000000000007', 'eeeeeeee-0000-0000-0000-000000000017',
                            'S001', 'POS-07', 'RemoteClaim', 'Superseded',
                            'idem-prep-7', 'idem-act-7', 'supersede-extra-7',
                            X'0102', '2026-07-28T00:00:00.000Z', '2026-07-28T01:00:00.000Z');
                        """;
                    await command.ExecuteNonQueryAsync();
                }
            }
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_migrates_legacy_shared_held_claims_after_buggy_alter_add()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-legacy-alter-add-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await using (var connection = await store.OpenConnectionAsync())
            {
                // 复现当前旧开发库状态：旧表 + 仅 ALTER ADD 补列，旧 CHECK/trigger/index 仍在。
                await ExecuteSqlAsync(connection, LegacySharedHeldOrderClaimsSchema);
                await ExecuteSqlAsync(
                    connection,
                    "ALTER TABLE SharedHeldOrderClaims ADD COLUMN SupersedeIdempotencyKey TEXT NULL;");
                await ExecuteSqlAsync(connection, BuggyAlterAddClaimsSeed);
            }

            var schema = new LocalSchemaService(store);
            await schema.InitializeAsync();
            await schema.InitializeAsync();

            await using (var connection = await store.OpenConnectionAsync())
            {
                // 行/密文/keys/revision/time 保留；缺 key 的旧 Superseded 行补稳定 key，
                // 已带 key 的 Superseded 行原样保留。
                var rows = await ReadClaimRowsAsync(connection);
                Assert.Equal(
                    ToKeyStrings(
                        [
                            new[]
                            {
                                "eeeeeeee-0000-0000-0000-000000000101", "eeeeeeee-0000-0000-0000-000000000111",
                                "S001", "POS-01", "RemoteClaim", "Active", "idem-prep-a1",
                                "idem-act-a1", "<NULL>", "<NULL>", "0102", "3",
                                "2026-07-30T00:00:00.000Z", "<NULL>",
                                "2026-07-28T00:00:00.000Z", "2026-07-28T01:00:00.000Z"
                            },
                            new[]
                            {
                                "eeeeeeee-0000-0000-0000-000000000102", "eeeeeeee-0000-0000-0000-000000000112",
                                "S001", "POS-02", "OfflineOrigin", "Superseded", "idem-prep-s1",
                                "<NULL>", "<NULL>", "migrated-supersede:eeeeeeee-0000-0000-0000-000000000102",
                                "0304", "1", "2026-07-30T01:00:00.000Z", "<NULL>",
                                "2026-07-28T00:00:00.000Z", "2026-07-28T01:00:00.000Z"
                            },
                            new[]
                            {
                                "eeeeeeee-0000-0000-0000-000000000103", "eeeeeeee-0000-0000-0000-000000000113",
                                "S001", "POS-03", "RemoteClaim", "Superseded", "idem-prep-s2",
                                "<NULL>", "<NULL>", "existing-supersede-key", "0506", "2",
                                "2026-07-30T02:00:00.000Z", "<NULL>",
                                "2026-07-28T00:00:00.000Z", "2026-07-28T01:00:00.000Z"
                            }
                        ]),
                    ToKeyStrings(rows));

                // ALTER ADD 变体迁移后 Active -> Superseded 同样可用且保留 activate key。
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        """
                        UPDATE SharedHeldOrderClaims
                        SET Status = 'Superseded',
                            SupersedeIdempotencyKey = 'supersede-active-a1',
                            UpdatedAtIso = '2026-07-28T02:00:00.000Z'
                        WHERE ClaimId = 'eeeeeeee-0000-0000-0000-000000000101';
                        """;
                    await command.ExecuteNonQueryAsync();
                }
                var supersededActive = await QuerySingleRowAsync(
                    connection,
                    """
                    SELECT Status, ActivateIdempotencyKey, SupersedeIdempotencyKey
                    FROM SharedHeldOrderClaims
                    WHERE ClaimId = 'eeeeeeee-0000-0000-0000-000000000101';
                    """);
                Assert.Equal(
                    new[] { "Superseded", "idem-act-a1", "supersede-active-a1" },
                    supersededActive);
            }
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_legacy_shared_held_claims_migration_failure_rolls_back_and_recovers()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-legacy-rollback-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await using (var connection = await store.OpenConnectionAsync())
            {
                await ExecuteSqlAsync(connection, LegacySharedHeldOrderClaimsSchema);
                await ExecuteSqlAsync(connection, LegacyClaimsRollbackSeed);
            }

            var schema = new LocalSchemaService(store);
            // 旧 Superseded 行带 BoundOrderGuid：旧 CHECK 允许、新 CHECK 拒绝 -> 迁移失败。
            var migrationException = await Assert.ThrowsAsync<SqliteException>(
                () => schema.InitializeAsync());
            Assert.Equal(19, migrationException.SqliteErrorCode);

            await using (var connection = await store.OpenConnectionAsync())
            {
                // 回滚后仍是旧表：DDL/列/trigger 原样，无 legacy 换名残留，数据完整。
                var tableSql = (string?)await ScalarAsync(
                    connection,
                    """
                    SELECT sql
                    FROM sqlite_master
                    WHERE type = 'table' AND name = 'SharedHeldOrderClaims';
                    """);
                Assert.NotNull(tableSql);
                Assert.DoesNotContain("SupersedeIdempotencyKey", tableSql, StringComparison.Ordinal);
                Assert.Contains(
                    "Status = 'Superseded' AND ActivateIdempotencyKey IS NULL",
                    tableSql,
                    StringComparison.Ordinal);

                Assert.Equal(
                    0,
                    Convert.ToInt32(
                        await ScalarAsync(
                            connection,
                            """
                            SELECT COUNT(*)
                            FROM sqlite_master
                            WHERE type = 'table' AND name = 'SharedHeldOrderClaims_legacy';
                            """)));

                var columns = await ReadColumnNamesAsync(connection, "SharedHeldOrderClaims");
                Assert.DoesNotContain(
                    columns,
                    column => string.Equals(column, "SupersedeIdempotencyKey", StringComparison.Ordinal));

                var legacyRows = await ReadLegacyClaimRowsAsync(connection);
                Assert.Equal(4, legacyRows.Count);
                Assert.Equal(
                    new[]
                    {
                        "eeeeeeee-0000-0000-0000-000000000204", "eeeeeeee-0000-0000-0000-000000000214",
                        "S001", "POS-04", "RemoteClaim", "Superseded", "idem-prep-204",
                        "<NULL>", "<NULL>", "0708", "1", "2026-07-30T02:00:00.000Z",
                        "eeeeeeee-0000-0000-0000-000000000299",
                        "2026-07-28T00:00:00.000Z", "2026-07-28T01:00:00.000Z"
                    },
                    legacyRows[^1]);

                var oldTriggerSql = (string?)await ScalarAsync(
                    connection,
                    """
                    SELECT sql
                    FROM sqlite_master
                    WHERE type = 'trigger' AND name = 'TRG_SharedHeldOrderClaims_StatusMachine';
                    """);
                Assert.NotNull(oldTriggerSql);
                Assert.DoesNotContain("NEW.Status = 'Superseded'", oldTriggerSql, StringComparison.Ordinal);
            }

            // 人工清理不兼容行后重试，旧表可恢复并完成迁移。
            await using (var connection = await store.OpenConnectionAsync())
            {
                await ExecuteSqlAsync(
                    connection,
                    """
                    DELETE FROM SharedHeldOrderClaims
                    WHERE ClaimId = 'eeeeeeee-0000-0000-0000-000000000204';
                    """);
            }

            await schema.InitializeAsync();

            await using (var connection = await store.OpenConnectionAsync())
            {
                var columns = await ReadColumnNamesAsync(connection, "SharedHeldOrderClaims");
                Assert.Contains("SupersedeIdempotencyKey", columns);

                var rows = await ReadClaimRowsAsync(connection);
                Assert.Equal(
                    ToKeyStrings(
                        [
                            new[]
                            {
                                "eeeeeeee-0000-0000-0000-000000000201", "eeeeeeee-0000-0000-0000-000000000211",
                                "S001", "POS-01", "OfflineOrigin", "Prepared", "idem-prep-201",
                                "<NULL>", "<NULL>", "<NULL>", "0102", "<NULL>",
                                "<NULL>", "<NULL>",
                                "2026-07-28T00:00:00.000Z", "2026-07-28T01:00:00.000Z"
                            },
                            new[]
                            {
                                "eeeeeeee-0000-0000-0000-000000000202", "eeeeeeee-0000-0000-0000-000000000212",
                                "S001", "POS-02", "RemoteClaim", "Active", "idem-prep-202",
                                "idem-act-202", "<NULL>", "<NULL>", "0304", "6",
                                "2026-07-30T00:00:00.000Z", "<NULL>",
                                "2026-07-28T00:00:00.000Z", "2026-07-28T01:00:00.000Z"
                            },
                            new[]
                            {
                                "eeeeeeee-0000-0000-0000-000000000203", "eeeeeeee-0000-0000-0000-000000000213",
                                "S001", "POS-03", "OfflineOrigin", "Superseded", "idem-prep-203",
                                "<NULL>", "<NULL>", "migrated-supersede:eeeeeeee-0000-0000-0000-000000000203",
                                "0506", "4", "2026-07-30T01:00:00.000Z", "<NULL>",
                                "2026-07-28T00:00:00.000Z", "2026-07-28T01:00:00.000Z"
                            }
                        ]),
                    ToKeyStrings(rows));
            }
        }
        finally
        {
            CleanupDatabase(databasePath);
        }
    }

    private const string LegacySharedHeldOrderPublicationsSchema =
        """
        CREATE TABLE SharedHeldOrderPublications (
            LocalHoldGuid TEXT PRIMARY KEY,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            Status TEXT NOT NULL CHECK (Status IN ('NeedsEvaluation', 'PendingPublish', 'Published', 'Blocked')),
            Revision INTEGER NOT NULL DEFAULT 1 CHECK (Revision >= 1),
            RetryCount INTEGER NOT NULL DEFAULT 0 CHECK (RetryCount >= 0),
            ErrorCode TEXT NULL,
            ErrorMessage TEXT NULL,
            PayloadCiphertext BLOB NULL,
            HeldAtIso TEXT NOT NULL,
            CreatedAtIso TEXT NOT NULL,
            UpdatedAtIso TEXT NOT NULL,
            LastAttemptAtIso TEXT NULL,
            NextAttemptAtIso TEXT NULL,
            RemoteRevision INTEGER NULL,
            RemoteUpdatedAtIso TEXT NULL
        );

        CREATE INDEX IX_SharedHeldOrderPublications_Due
            ON SharedHeldOrderPublications (Status, NextAttemptAtIso, UpdatedAtIso);
        """;

    private const string LegacySharedHeldOrderPublicationsSeed =
        """
        INSERT INTO SharedHeldOrderPublications (
            LocalHoldGuid, StoreCode, DeviceCode, Status, Revision, RetryCount,
            PayloadCiphertext, HeldAtIso, CreatedAtIso, UpdatedAtIso,
            RemoteRevision, RemoteUpdatedAtIso)
        VALUES (
            'ffffffff-0000-0000-0000-000000000001', 'S001', 'POS-01', 'Published', 1, 0,
            X'DEADBEEF', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z',
            7, '2026-07-28T00:00:00.000Z');
        """;

    /// <summary>
    /// 旧开发库 SharedHeldOrderClaims：无 SupersedeIdempotencyKey；
    /// 旧 CHECK 要求 Superseded 的 ActivateIdempotencyKey 为空（BoundOrderGuid 不受限），
    /// 旧 trigger 阻止 Active -> Superseded；旧 index/trigger 与新表同名。
    /// </summary>
    private const string LegacySharedHeldOrderClaimsSchema =
        """
        CREATE TABLE SharedHeldOrderClaims (
            ClaimId TEXT PRIMARY KEY,
            HoldGuid TEXT NOT NULL,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            Source TEXT NOT NULL CHECK (Source IN ('RemoteClaim', 'OfflineOrigin')),
            Status TEXT NOT NULL CHECK (Status IN ('Prepared', 'Active', 'Completed', 'Released', 'Superseded')),
            PrepareIdempotencyKey TEXT NOT NULL UNIQUE,
            ActivateIdempotencyKey TEXT NULL,
            ReleaseIdempotencyKey TEXT NULL,
            PayloadCiphertext BLOB NOT NULL,
            ServerRevision INTEGER NULL,
            ExpiresAtIso TEXT NULL,
            BoundOrderGuid TEXT NULL,
            CreatedAtIso TEXT NOT NULL,
            UpdatedAtIso TEXT NOT NULL,
            CHECK (TRIM(ClaimId) <> ''),
            CHECK (TRIM(HoldGuid) <> ''),
            CHECK (TRIM(StoreCode) <> ''),
            CHECK (TRIM(DeviceCode) <> ''),
            CHECK (TRIM(PrepareIdempotencyKey) <> ''),
            CHECK (ActivateIdempotencyKey IS NULL OR (TRIM(ActivateIdempotencyKey) <> '' AND LENGTH(ActivateIdempotencyKey) > 0)),
            CHECK (ReleaseIdempotencyKey IS NULL OR (TRIM(ReleaseIdempotencyKey) <> '' AND LENGTH(ReleaseIdempotencyKey) > 0)),
            CHECK (LENGTH(PayloadCiphertext) > 0),
            CHECK (ServerRevision IS NULL OR ServerRevision >= 0),
            CHECK (
                (Status = 'Prepared' AND ActivateIdempotencyKey IS NULL AND ReleaseIdempotencyKey IS NULL AND BoundOrderGuid IS NULL)
                OR (Status = 'Active' AND ActivateIdempotencyKey IS NOT NULL AND ReleaseIdempotencyKey IS NULL)
                OR (Status = 'Completed' AND ActivateIdempotencyKey IS NOT NULL AND ReleaseIdempotencyKey IS NOT NULL AND BoundOrderGuid IS NOT NULL)
                OR (Status = 'Released' AND ReleaseIdempotencyKey IS NOT NULL AND BoundOrderGuid IS NULL)
                OR (Status = 'Superseded' AND ActivateIdempotencyKey IS NULL AND ReleaseIdempotencyKey IS NULL)
            )
        );

        CREATE UNIQUE INDEX UX_SharedHeldOrderClaims_OpenFence_PerDevice
            ON SharedHeldOrderClaims (StoreCode, DeviceCode)
            WHERE Status IN ('Prepared', 'Active');

        CREATE UNIQUE INDEX UX_SharedHeldOrderClaims_ActivateKey
            ON SharedHeldOrderClaims (ActivateIdempotencyKey)
            WHERE ActivateIdempotencyKey IS NOT NULL;

        CREATE UNIQUE INDEX UX_SharedHeldOrderClaims_ReleaseKey
            ON SharedHeldOrderClaims (ReleaseIdempotencyKey)
            WHERE ReleaseIdempotencyKey IS NOT NULL;

        CREATE INDEX IX_SharedHeldOrderClaims_MineRecovery
            ON SharedHeldOrderClaims (StoreCode, DeviceCode, Status, UpdatedAtIso);

        CREATE TRIGGER TRG_SharedHeldOrderClaims_StatusMachine
        BEFORE UPDATE OF Status ON SharedHeldOrderClaims
        FOR EACH ROW
        WHEN NEW.Status <> OLD.Status
        BEGIN
            SELECT CASE
                WHEN OLD.Status = 'Prepared' AND NEW.Status IN ('Active', 'Released') THEN 0
                WHEN OLD.Status = 'Active' AND NEW.Status IN ('Completed', 'Released') THEN 0
                ELSE RAISE(ABORT, 'illegal shared held order claim status transition')
            END;
        END;

        CREATE TRIGGER TRG_SharedHeldOrderClaims_ActiveBindingOnly
        BEFORE UPDATE ON SharedHeldOrderClaims
        FOR EACH ROW
        WHEN OLD.Status = 'Active' AND NEW.Status = 'Active'
        BEGIN
            SELECT CASE
                WHEN NEW.BoundOrderGuid IS NOT NULL AND OLD.BoundOrderGuid IS NULL THEN 0
                ELSE RAISE(ABORT, 'active claim may only change through first bind')
            END;
        END;
        """;

    private const string LegacySharedHeldOrderClaimsSeed =
        """
        INSERT INTO SharedHeldOrderClaims (
            ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
            PrepareIdempotencyKey, ActivateIdempotencyKey, ReleaseIdempotencyKey,
            PayloadCiphertext, ServerRevision, ExpiresAtIso, BoundOrderGuid, CreatedAtIso, UpdatedAtIso)
        VALUES
            ('eeeeeeee-0000-0000-0000-000000000001', 'eeeeeeee-0000-0000-0000-000000000011', 'S001', 'POS-01', 'OfflineOrigin', 'Prepared', 'idem-prep-1', NULL, NULL, X'01020304', NULL, '2026-07-30T00:00:00.000Z', NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T01:00:00.000Z'),
            ('eeeeeeee-0000-0000-0000-000000000002', 'eeeeeeee-0000-0000-0000-000000000012', 'S001', 'POS-02', 'RemoteClaim', 'Active', 'idem-prep-2', 'idem-act-2', NULL, X'0506', 7, '2026-07-30T01:00:00.000Z', NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T01:00:00.000Z'),
            ('eeeeeeee-0000-0000-0000-000000000003', 'eeeeeeee-0000-0000-0000-000000000013', 'S001', 'POS-03', 'RemoteClaim', 'Active', 'idem-prep-3', 'idem-act-3', NULL, X'070809', 8, '2026-07-30T02:00:00.000Z', 'eeeeeeee-0000-0000-0000-000000000099', '2026-07-28T00:00:00.000Z', '2026-07-28T01:00:00.000Z'),
            ('eeeeeeee-0000-0000-0000-000000000004', 'eeeeeeee-0000-0000-0000-000000000014', 'S001', 'POS-04', 'RemoteClaim', 'Completed', 'idem-prep-4', 'idem-act-4', 'idem-rel-4', X'0a0b', 9, '2026-07-30T03:00:00.000Z', 'eeeeeeee-0000-0000-0000-000000000098', '2026-07-28T00:00:00.000Z', '2026-07-28T01:00:00.000Z'),
            ('eeeeeeee-0000-0000-0000-000000000005', 'eeeeeeee-0000-0000-0000-000000000015', 'S001', 'POS-05', 'OfflineOrigin', 'Superseded', 'idem-prep-5', NULL, NULL, X'0c0d', 5, '2026-07-30T04:00:00.000Z', NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T01:00:00.000Z');
        """;

    private const string BuggyAlterAddClaimsSeed =
        """
        INSERT INTO SharedHeldOrderClaims (
            ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
            PrepareIdempotencyKey, ActivateIdempotencyKey, ReleaseIdempotencyKey, SupersedeIdempotencyKey,
            PayloadCiphertext, ServerRevision, ExpiresAtIso, BoundOrderGuid, CreatedAtIso, UpdatedAtIso)
        VALUES
            ('eeeeeeee-0000-0000-0000-000000000101', 'eeeeeeee-0000-0000-0000-000000000111', 'S001', 'POS-01', 'RemoteClaim', 'Active', 'idem-prep-a1', 'idem-act-a1', NULL, NULL, X'0102', 3, '2026-07-30T00:00:00.000Z', NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T01:00:00.000Z'),
            ('eeeeeeee-0000-0000-0000-000000000102', 'eeeeeeee-0000-0000-0000-000000000112', 'S001', 'POS-02', 'OfflineOrigin', 'Superseded', 'idem-prep-s1', NULL, NULL, NULL, X'0304', 1, '2026-07-30T01:00:00.000Z', NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T01:00:00.000Z'),
            ('eeeeeeee-0000-0000-0000-000000000103', 'eeeeeeee-0000-0000-0000-000000000113', 'S001', 'POS-03', 'RemoteClaim', 'Superseded', 'idem-prep-s2', NULL, NULL, 'existing-supersede-key', X'0506', 2, '2026-07-30T02:00:00.000Z', NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T01:00:00.000Z');
        """;

    private const string LegacyClaimsRollbackSeed =
        """
        INSERT INTO SharedHeldOrderClaims (
            ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
            PrepareIdempotencyKey, ActivateIdempotencyKey, ReleaseIdempotencyKey,
            PayloadCiphertext, ServerRevision, ExpiresAtIso, BoundOrderGuid, CreatedAtIso, UpdatedAtIso)
        VALUES
            ('eeeeeeee-0000-0000-0000-000000000201', 'eeeeeeee-0000-0000-0000-000000000211', 'S001', 'POS-01', 'OfflineOrigin', 'Prepared', 'idem-prep-201', NULL, NULL, X'0102', NULL, NULL, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T01:00:00.000Z'),
            ('eeeeeeee-0000-0000-0000-000000000202', 'eeeeeeee-0000-0000-0000-000000000212', 'S001', 'POS-02', 'RemoteClaim', 'Active', 'idem-prep-202', 'idem-act-202', NULL, X'0304', 6, '2026-07-30T00:00:00.000Z', NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T01:00:00.000Z'),
            ('eeeeeeee-0000-0000-0000-000000000203', 'eeeeeeee-0000-0000-0000-000000000213', 'S001', 'POS-03', 'OfflineOrigin', 'Superseded', 'idem-prep-203', NULL, NULL, X'0506', 4, '2026-07-30T01:00:00.000Z', NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T01:00:00.000Z'),
            ('eeeeeeee-0000-0000-0000-000000000204', 'eeeeeeee-0000-0000-0000-000000000214', 'S001', 'POS-04', 'RemoteClaim', 'Superseded', 'idem-prep-204', NULL, NULL, X'0708', 1, '2026-07-30T02:00:00.000Z', 'eeeeeeee-0000-0000-0000-000000000299', '2026-07-28T00:00:00.000Z', '2026-07-28T01:00:00.000Z');
        """;

    private static async Task ExecuteSqlAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ReadColumnNamesAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<List<string>> QueryStringsAsync(SqliteConnection connection, string sql)
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

    private static async Task<string[]?> QuerySingleRowAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return ReadRow(reader);
    }

    private static async Task<List<string[]>> QueryRowsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    private static string[] ReadRow(System.Data.Common.DbDataReader reader)
    {
        var row = new string[reader.FieldCount];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = reader.IsDBNull(i) ? "<NULL>" : reader.GetValue(i).ToString() ?? "<NULL>";
        }

        return row;
    }

    private static Task<List<string[]>> ReadClaimRowsAsync(SqliteConnection connection) =>
        QueryRowsAsync(
            connection,
            """
            SELECT
                ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                PrepareIdempotencyKey, ActivateIdempotencyKey, ReleaseIdempotencyKey, SupersedeIdempotencyKey,
                HEX(PayloadCiphertext), ServerRevision, ExpiresAtIso, BoundOrderGuid, CreatedAtIso, UpdatedAtIso
            FROM SharedHeldOrderClaims
            ORDER BY ClaimId;
            """);

    private static Task<List<string[]>> ReadLegacyClaimRowsAsync(SqliteConnection connection) =>
        QueryRowsAsync(
            connection,
            """
            SELECT
                ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                PrepareIdempotencyKey, ActivateIdempotencyKey, ReleaseIdempotencyKey,
                HEX(PayloadCiphertext), ServerRevision, ExpiresAtIso, BoundOrderGuid, CreatedAtIso, UpdatedAtIso
            FROM SharedHeldOrderClaims
            ORDER BY ClaimId;
            """);

    private static List<string> ToKeyStrings(List<string[]> rows) =>
        rows.Select(row => string.Join('\u0001', row)).ToList();

    private static void CleanupDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
