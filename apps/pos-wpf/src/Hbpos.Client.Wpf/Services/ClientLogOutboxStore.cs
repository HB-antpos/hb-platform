using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

internal enum ClientLogOutboxKind
{
    Runtime,
    OperationAudit
}

internal sealed record ClientLogOutboxRecord(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    string PayloadJson,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    string? LastErrorCode,
    string? LastErrorMessage);

internal sealed record ClientLogRejection(Guid EventId, string ErrorCode, string? ErrorMessage);

internal sealed record OperationAuditOutboxListItem(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    string State,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    string? LastErrorCode,
    string? LastErrorMessage);

internal sealed class ClientLogOutboxStore
{
    private readonly string _databasePath;
    private readonly int _runtimePendingLimit;

    // 容量裁剪的间隔（每多少条运行日志做一次）。裁剪那条 DELETE 要对全部 Pending 行排序、再跳过上限条数，
    // 积压接近上限（长时间断网）时单次约 60ms；原先每写一条日志就做一次，写日志的队列满了就丢日志。
    // 改为每 N 条做一次：上限的超出量最多 N-1 条（上限的 1/8 与 256 取小，上限很小时每条都裁，保持精确）。
    private readonly int _runtimeTrimInterval;
    private int _runtimeInsertsSinceTrim;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _runtimeDroppedCount;

    public ClientLogOutboxStore(string databasePath, int runtimePendingLimit = 50_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _runtimePendingLimit = Math.Max(1, runtimePendingLimit);
        _runtimeTrimInterval = Math.Clamp(_runtimePendingLimit / 8, 1, 256);
    }

    public long RuntimeDroppedCount => Interlocked.Read(ref _runtimeDroppedCount);

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS RuntimeLogOutbox (
                    ClientEventId TEXT NOT NULL PRIMARY KEY,
                    OccurredAtUtc TEXT NOT NULL,
                    PayloadJson TEXT NOT NULL,
                    State TEXT NOT NULL DEFAULT 'Pending',
                    AttemptCount INTEGER NOT NULL DEFAULT 0,
                    NextAttemptAtUtc TEXT NOT NULL,
                    LastErrorCode TEXT NULL,
                    LastErrorMessage TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_RuntimeLogOutbox_State_NextAttempt_Occurred
                    ON RuntimeLogOutbox(State, NextAttemptAtUtc, OccurredAtUtc);

                CREATE TABLE IF NOT EXISTS OperationAuditOutbox (
                    EventId TEXT NOT NULL PRIMARY KEY,
                    OccurredAtUtc TEXT NOT NULL,
                    PayloadJson TEXT NOT NULL,
                    State TEXT NOT NULL DEFAULT 'Pending',
                    AttemptCount INTEGER NOT NULL DEFAULT 0,
                    NextAttemptAtUtc TEXT NOT NULL,
                    LastErrorCode TEXT NULL,
                    LastErrorMessage TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_OperationAuditOutbox_State_NextAttempt_Occurred
                    ON OperationAuditOutbox(State, NextAttemptAtUtc, OccurredAtUtc);
                PRAGMA user_version=1;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task EnqueueAsync(
        ClientLogOutboxKind kind,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        string payloadJson,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = $"""
                    INSERT OR IGNORE INTO {GetTableName(kind)}
                        ({GetIdColumn(kind)}, OccurredAtUtc, PayloadJson, State, AttemptCount, NextAttemptAtUtc, CreatedAtUtc)
                    VALUES ($eventId, $occurredAtUtc, $payloadJson, 'Pending', 0, $nextAttemptAtUtc, $createdAtUtc);
                    """;
                insert.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
                insert.Parameters.AddWithValue("$occurredAtUtc", FormatUtc(occurredAtUtc));
                insert.Parameters.AddWithValue("$payloadJson", payloadJson);
                insert.Parameters.AddWithValue("$nextAttemptAtUtc", FormatUtc(createdAtUtc));
                insert.Parameters.AddWithValue("$createdAtUtc", FormatUtc(createdAtUtc));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            // _runtimeInsertsSinceTrim 只在持有 _writeGate 时读写；写入失败回滚时不清零，下一条会再次触发裁剪。
            if (kind == ClientLogOutboxKind.Runtime && ++_runtimeInsertsSinceTrim >= _runtimeTrimInterval)
            {
                await using var trim = connection.CreateCommand();
                trim.Transaction = (SqliteTransaction)transaction;
                trim.CommandText = $"""
                    DELETE FROM RuntimeLogOutbox
                    WHERE ClientEventId IN (
                        SELECT ClientEventId
                        FROM RuntimeLogOutbox
                        WHERE State = 'Pending'
                        ORDER BY OccurredAtUtc DESC, CreatedAtUtc DESC
                        LIMIT -1 OFFSET {_runtimePendingLimit}
                    );
                    """;
                var dropped = await trim.ExecuteNonQueryAsync(cancellationToken);
                _runtimeInsertsSinceTrim = 0;
                if (dropped > 0)
                {
                    Interlocked.Add(ref _runtimeDroppedCount, dropped);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<ClientLogOutboxRecord>> ReadPendingAsync(
        ClientLogOutboxKind kind,
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {GetIdColumn(kind)}, OccurredAtUtc, PayloadJson, AttemptCount,
                   NextAttemptAtUtc, LastErrorCode, LastErrorMessage
            FROM {GetTableName(kind)}
            WHERE State = 'Pending' AND NextAttemptAtUtc <= $nowUtc
            ORDER BY OccurredAtUtc, CreatedAtUtc
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$nowUtc", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        return await ReadRecordsAsync(command, cancellationToken);
    }

    public async Task<int> CountPendingAsync(
        ClientLogOutboxKind kind,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {GetTableName(kind)} WHERE State = 'Pending';";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<ClientLogOutboxRecord>> ReadPendingOperationForScopeAsync(
        DateTimeOffset nowUtc,
        string storeCode,
        string deviceCode,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventId, OccurredAtUtc, PayloadJson, AttemptCount,
                   NextAttemptAtUtc, LastErrorCode, LastErrorMessage
            FROM OperationAuditOutbox
            WHERE State = 'Pending' AND NextAttemptAtUtc <= $nowUtc
              AND CASE
                    WHEN json_valid(PayloadJson) = 0 THEN 1
                    WHEN json_extract(PayloadJson, '$.storeCode') = $storeCode
                     AND json_extract(PayloadJson, '$.deviceCode') = $deviceCode THEN 1
                    ELSE 0
                  END = 1
            ORDER BY OccurredAtUtc, CreatedAtUtc
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$nowUtc", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$storeCode", storeCode);
        command.Parameters.AddWithValue("$deviceCode", deviceCode);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        return await ReadRecordsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<ClientLogOutboxRecord>> ReadRejectedAsync(
        ClientLogOutboxKind kind,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {GetIdColumn(kind)}, OccurredAtUtc, PayloadJson, AttemptCount,
                   NextAttemptAtUtc, LastErrorCode, LastErrorMessage
            FROM {GetTableName(kind)}
            WHERE State = 'Rejected'
            ORDER BY OccurredAtUtc, CreatedAtUtc
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        return await ReadRecordsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<OperationAuditOutboxListItem>> ReadOperationForScopeAsync(
        string storeCode,
        string deviceCode,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventId, OccurredAtUtc, State, AttemptCount,
                   NextAttemptAtUtc, LastErrorCode, LastErrorMessage
            FROM OperationAuditOutbox
            WHERE State IN ('Pending', 'Rejected')
              AND CASE
                    WHEN json_valid(PayloadJson) = 0 THEN 1
                    WHEN json_extract(PayloadJson, '$.storeCode') = $storeCode
                     AND json_extract(PayloadJson, '$.deviceCode') = $deviceCode THEN 1
                    ELSE 0
                  END = 1
            ORDER BY OccurredAtUtc, CreatedAtUtc
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$storeCode", storeCode);
        command.Parameters.AddWithValue("$deviceCode", deviceCode);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        var records = new List<OperationAuditOutboxListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new OperationAuditOutboxListItem(
                Guid.Parse(reader.GetString(0)),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(2),
                reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return records;
    }

    public async Task<int> ResetOperationForRetryAsync(
        IReadOnlyCollection<Guid> eventIds,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventIds);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var updated = 0;
            foreach (var eventId in eventIds.Where(id => id != Guid.Empty).Distinct())
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    UPDATE OperationAuditOutbox
                    SET State = 'Pending', AttemptCount = 0, NextAttemptAtUtc = $nowUtc,
                        LastErrorCode = NULL, LastErrorMessage = NULL
                    WHERE EventId = $eventId AND State IN ('Pending', 'Rejected');
                    """;
                command.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
                command.Parameters.AddWithValue("$nowUtc", FormatUtc(nowUtc));
                updated += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return updated;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ApplyResultsAsync(
        ClientLogOutboxKind kind,
        IReadOnlyCollection<Guid> completedEventIds,
        IReadOnlyCollection<ClientLogRejection> rejections,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var eventId in completedEventIds.Distinct())
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = $"DELETE FROM {GetTableName(kind)} WHERE {GetIdColumn(kind)} = $eventId;";
                delete.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var rejection in rejections.GroupBy(item => item.EventId).Select(group => group.First()))
            {
                await using var update = connection.CreateCommand();
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = $"""
                    UPDATE {GetTableName(kind)}
                    SET State = 'Rejected', LastErrorCode = $errorCode,
                        LastErrorMessage = $errorMessage, NextAttemptAtUtc = $nowUtc
                    WHERE {GetIdColumn(kind)} = $eventId;
                    """;
                update.Parameters.AddWithValue("$eventId", rejection.EventId.ToString("D"));
                update.Parameters.AddWithValue("$errorCode", Limit(rejection.ErrorCode, 100));
                update.Parameters.AddWithValue("$errorMessage", (object?)Limit(rejection.ErrorMessage, 500) ?? DBNull.Value);
                update.Parameters.AddWithValue("$nowUtc", FormatUtc(nowUtc));
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ScheduleRetryAsync(
        ClientLogOutboxKind kind,
        IReadOnlyCollection<Guid> eventIds,
        DateTimeOffset nextAttemptAtUtc,
        string errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var eventId in eventIds.Distinct())
            {
                await using var update = connection.CreateCommand();
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = $"""
                    UPDATE {GetTableName(kind)}
                    SET AttemptCount = AttemptCount + 1,
                        NextAttemptAtUtc = $nextAttemptAtUtc,
                        LastErrorCode = $errorCode,
                        LastErrorMessage = $errorMessage
                    WHERE {GetIdColumn(kind)} = $eventId AND State = 'Pending';
                    """;
                update.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
                update.Parameters.AddWithValue("$nextAttemptAtUtc", FormatUtc(nextAttemptAtUtc));
                update.Parameters.AddWithValue("$errorCode", Limit(errorCode, 100) ?? string.Empty);
                update.Parameters.AddWithValue("$errorMessage", (object?)Limit(errorMessage, 500) ?? DBNull.Value);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DeleteExpiredRejectedAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var cutoffUtc = nowUtc.AddDays(-30);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var kind in Enum.GetValues<ClientLogOutboxKind>())
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = $"""
                    DELETE FROM {GetTableName(kind)}
                    WHERE State = 'Rejected' AND NextAttemptAtUtc < $cutoffUtc;
                    """;
                delete.Parameters.AddWithValue("$cutoffUtc", FormatUtc(cutoffUtc));
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString());

        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // busy_timeout 必须在每条连接上设置，不能依赖 SQLite 文件持久化。
        command.CommandText = "PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task<IReadOnlyList<ClientLogOutboxRecord>> ReadRecordsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var records = new List<ClientLogOutboxRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new ClientLogOutboxRecord(
                Guid.Parse(reader.GetString(0)),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(2),
                reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return records;
    }

    private static string GetTableName(ClientLogOutboxKind kind)
    {
        return kind == ClientLogOutboxKind.Runtime ? "RuntimeLogOutbox" : "OperationAuditOutbox";
    }

    private static string GetIdColumn(ClientLogOutboxKind kind)
    {
        return kind == ClientLogOutboxKind.Runtime ? "ClientEventId" : "EventId";
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static string? Limit(string? value, int maximumLength)
    {
        return string.IsNullOrWhiteSpace(value)
            ? value
            : value.Length <= maximumLength ? value : value[..maximumLength];
    }
}
