using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public interface ILocalCashMovementRepository
{
    Task<IReadOnlyList<LocalCashMovement>> ListAsync(
        string storeCode,
        string deviceCode,
        DateTimeOffset periodFrom,
        DateTimeOffset periodTo,
        CancellationToken cancellationToken = default);

    Task<LocalCashMovement?> GetAsync(Guid movementGuid, CancellationToken cancellationToken = default);

    /// <summary>插入一条记录；该原记录已被冲正（冲正唯一索引冲突）时返回 false。</summary>
    Task<bool> TryInsertAsync(LocalCashMovement movement, CancellationToken cancellationToken = default);

    /// <summary>本机下一个展示流水号（CM-0001 起，按设备累计）。</summary>
    Task<string> NextDisplayNumberAsync(string storeCode, string deviceCode, CancellationToken cancellationToken = default);

    /// <summary>该营业日是否在 <paramref name="after"/> 之后保存过日结（之后的冲正会让已打印的日结对不上，所以禁止）。</summary>
    Task<bool> HasDailyCloseSavedAfterAsync(
        string storeCode,
        string deviceCode,
        DateTime businessDate,
        DateTimeOffset after,
        CancellationToken cancellationToken = default);

    /// <summary>到期待上传的记录，按发生时间排序（原记录总在其冲正之前上传）。</summary>
    Task<IReadOnlyList<LocalCashMovement>> GetDueUploadsAsync(
        string storeCode,
        string deviceCode,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default);

    Task MarkUploadResultAsync(
        Guid movementGuid,
        string status,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset attemptedAt,
        DateTimeOffset? nextUploadAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 本机现金进出表 LocalCashMovements 的读写。记录只增不改（上传状态列除外）。
/// 日结统计也从这里取数，见 <see cref="ReadPeriodAsync"/>。
/// </summary>
public sealed class LocalCashMovementRepository(LocalSqliteStore store) : ILocalCashMovementRepository
{
    private const string Columns = """
        MovementGuid, StoreCode, DeviceCode, Direction, ReasonCode, Amount, Note, VoidsMovementGuid, BusinessDate,
        OccurredAt, CashierId, CashierName, AuthorizedById, AuthorizedByName, DisplayNumber, UploadStatus
        """;

    public async Task<IReadOnlyList<LocalCashMovement>> ListAsync(
        string storeCode,
        string deviceCode,
        DateTimeOffset periodFrom,
        DateTimeOffset periodTo,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        return await ReadPeriodAsync(connection, storeCode, deviceCode, periodFrom, periodTo, cancellationToken);
    }

    /// <summary>
    /// 本机在 [from, to) 内发生的全部现金进出（含冲正），与日结订单同口径的本地时间半开区间。
    /// 日结仓储在同一连接里调用它，保证点钞快照与保存时重算用的是同一份数据。
    /// </summary>
    internal static async Task<IReadOnlyList<LocalCashMovement>> ReadPeriodAsync(
        SqliteConnection connection,
        string storeCode,
        string deviceCode,
        DateTimeOffset periodFrom,
        DateTimeOffset periodTo,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
            FROM LocalCashMovements
            WHERE StoreCode = $StoreCode
              AND DeviceCode = $DeviceCode
              AND julianday(OccurredAt) >= julianday($PeriodFrom)
              AND julianday(OccurredAt) < julianday($PeriodTo)
            ORDER BY julianday(OccurredAt), DisplayNumber;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$PeriodFrom", periodFrom.ToString("O"));
        command.Parameters.AddWithValue("$PeriodTo", periodTo.ToString("O"));
        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task<LocalCashMovement?> GetAsync(Guid movementGuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM LocalCashMovements WHERE MovementGuid = $MovementGuid;";
        command.Parameters.AddWithValue("$MovementGuid", movementGuid.ToString("D"));
        var rows = await ReadAllAsync(command, cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<bool> TryInsertAsync(LocalCashMovement movement, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LocalCashMovements
            (MovementGuid, StoreCode, DeviceCode, Direction, ReasonCode, Amount, Note, VoidsMovementGuid, BusinessDate,
             OccurredAt, CashierId, CashierName, AuthorizedById, AuthorizedByName, DisplayNumber, UploadStatus, UploadAttemptCount)
            VALUES
            ($MovementGuid, $StoreCode, $DeviceCode, $Direction, $ReasonCode, $Amount, $Note, $VoidsMovementGuid, $BusinessDate,
             $OccurredAt, $CashierId, $CashierName, $AuthorizedById, $AuthorizedByName, $DisplayNumber, 'Pending', 0);
            """;
        command.Parameters.AddWithValue("$MovementGuid", movement.MovementGuid.ToString("D"));
        command.Parameters.AddWithValue("$StoreCode", movement.StoreCode);
        command.Parameters.AddWithValue("$DeviceCode", movement.DeviceCode);
        command.Parameters.AddWithValue("$Direction", CashMovementReasons.ToContractDirection(movement.Direction));
        command.Parameters.AddWithValue("$ReasonCode", movement.ReasonCode);
        command.Parameters.AddWithValue("$Amount", movement.Amount.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$Note", (object?)movement.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$VoidsMovementGuid", (object?)movement.VoidsMovementGuid?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$BusinessDate", movement.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$OccurredAt", movement.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$CashierId", movement.CashierId);
        command.Parameters.AddWithValue("$CashierName", movement.CashierName);
        command.Parameters.AddWithValue("$AuthorizedById", (object?)movement.AuthorizedById ?? DBNull.Value);
        command.Parameters.AddWithValue("$AuthorizedByName", (object?)movement.AuthorizedByName ?? DBNull.Value);
        command.Parameters.AddWithValue("$DisplayNumber", movement.DisplayNumber);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && movement.VoidsMovementGuid is not null)
        {
            // 19 = SQLITE_CONSTRAINT：冲正唯一索引冲突，说明这条原记录刚被别的操作冲正过。
            return false;
        }
    }

    public async Task<string> NextDisplayNumberAsync(
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM LocalCashMovements WHERE StoreCode = $StoreCode AND DeviceCode = $DeviceCode;";
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return $"CM-{count + 1:0000}";
    }

    public async Task<bool> HasDailyCloseSavedAfterAsync(
        string storeCode,
        string deviceCode,
        DateTime businessDate,
        DateTimeOffset after,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM LocalDailyCloses
            WHERE StoreCode = $StoreCode
              AND DeviceCode = $DeviceCode
              AND BusinessDate = $BusinessDate
              AND julianday(SavedAt) > julianday($After);
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$BusinessDate", businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$After", after.ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    public async Task<IReadOnlyList<LocalCashMovement>> GetDueUploadsAsync(
        string storeCode,
        string deviceCode,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
            FROM LocalCashMovements
            WHERE StoreCode = $StoreCode
              AND DeviceCode = $DeviceCode
              AND UploadStatus = 'Pending'
              AND (NextUploadAt IS NULL OR julianday(NextUploadAt) <= julianday($Now))
            ORDER BY julianday(OccurredAt), DisplayNumber
            LIMIT $Limit;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$Now", now.ToString("O"));
        command.Parameters.AddWithValue("$Limit", limit);
        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task MarkUploadResultAsync(
        Guid movementGuid,
        string status,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset attemptedAt,
        DateTimeOffset? nextUploadAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LocalCashMovements
            SET UploadStatus = $Status,
                UploadAttemptCount = UploadAttemptCount + 1,
                LastUploadAttemptAt = $AttemptedAt,
                NextUploadAt = $NextUploadAt,
                UploadErrorCode = $ErrorCode,
                UploadErrorMessage = $ErrorMessage,
                UploadedAt = CASE WHEN $Status = 'Synced' THEN $AttemptedAt ELSE UploadedAt END
            WHERE MovementGuid = $MovementGuid;
            """;
        command.Parameters.AddWithValue("$MovementGuid", movementGuid.ToString("D"));
        command.Parameters.AddWithValue("$Status", status);
        command.Parameters.AddWithValue("$AttemptedAt", attemptedAt.ToString("O"));
        command.Parameters.AddWithValue("$NextUploadAt", (object?)nextUploadAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$ErrorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$ErrorMessage", (object?)Truncate(errorMessage, 500) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<LocalCashMovement>> ReadAllAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<LocalCashMovement>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LocalCashMovement(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                string.Equals(reader.GetString(3), "In", StringComparison.Ordinal) ? CashMovementDirection.In : CashMovementDirection.Out,
                reader.GetString(4),
                decimal.Parse(Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
                DateTime.ParseExact(reader.GetString(8), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
                reader.GetString(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetString(14),
                reader.GetString(15)));
        }

        return result;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
