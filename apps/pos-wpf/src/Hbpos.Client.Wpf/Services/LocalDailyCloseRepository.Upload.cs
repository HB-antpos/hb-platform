using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public enum LocalDailyCloseUploadStatus
{
    Pending,
    Uploading,
    Synced,
    Rejected
}

/// <summary>
/// 认领成功后的上传租约。日结存档落库后内容不可变，所以租约只需要记录"这是第几次尝试"，
/// 用于计算退避；不需要 Linkly 结算单那种 PayloadRevision 快照版本。
/// </summary>
public sealed record LocalDailyCloseUploadLease(Guid DailyCloseGuid, int UploadAttemptCount);

/// <summary>
/// 日结记录上传用的本地仓储。状态流转：Pending →(认领)→ Uploading →(成功)→ Synced；
/// 失败时回到 Pending 并带退避时间，永久拒绝则进入 Rejected 不再重试。
/// </summary>
public interface ILocalDailyCloseUploadRepository
{
    /// <summary>
    /// 取到期待传的日结 Guid。只返回 StoreCode/DeviceCode 与当前设备授权范围完全一致的行：
    /// 其它范围（例如设备换绑前的旧记录）保持 Pending 不动，避免它们被服务端 403 后阻塞整个批次。
    /// </summary>
    Task<IReadOnlyList<Guid>> GetDueUploadGuidsAsync(
        string storeCode,
        string deviceCode,
        int take,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 租约认领（CAS）：只有仍是 Pending 且已到期、范围一致的行才会被改成 Uploading 并累加尝试次数；
    /// 并发认领同一条记录时只有一个调用方拿到租约，其余返回 null。
    /// </summary>
    Task<LocalDailyCloseUploadLease?> TryClaimUploadAsync(
        Guid dailyCloseGuid,
        string storeCode,
        string deviceCode,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default);

    /// <summary>读出完整存档（含 11 档面额，缺档补 0）用于构造上传请求；记录已不存在时返回 null。</summary>
    Task<DailyCloseArchive?> GetArchiveForUploadAsync(
        Guid dailyCloseGuid,
        CancellationToken cancellationToken = default);

    Task MarkUploadSucceededAsync(
        Guid dailyCloseGuid,
        DateTimeOffset uploadedAt,
        CancellationToken cancellationToken = default);

    /// <summary>失败后退回 Pending，保留已累加的尝试次数并设置下次到期时间。</summary>
    Task MarkUploadPendingAsync(
        Guid dailyCloseGuid,
        DateTimeOffset nextUploadAt,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 设备授权问题（401/403）时释放租约：退回 Pending 且撤销本次认领累加的尝试次数、立即到期，
    /// 因为这不是这条记录自己的失败，不能烧掉它的尝试次数/退避。
    /// </summary>
    Task ReleaseUploadWithoutAttemptAsync(
        Guid dailyCloseGuid,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    Task MarkUploadRejectedAsync(
        Guid dailyCloseGuid,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    /// <summary>回收过期租约：进程中途退出会遗留 Uploading，超过租约时间的退回 Pending 重新排队。</summary>
    Task RecoverExpiredUploadingAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset nextUploadAt,
        CancellationToken cancellationToken = default);
}

public sealed partial class LocalDailyCloseRepository : ILocalDailyCloseUploadRepository
{
    public async Task<IReadOnlyList<Guid>> GetDueUploadGuidsAsync(
        string storeCode,
        string deviceCode,
        int take,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // NextUploadAt 为 NULL 表示立即到期：新保存的日结与旧库补列后的历史行都是这个状态。
        // 先传"从未失败过"的（NULL），再按到期时间、保存时间排序。
        command.CommandText = """
            SELECT DailyCloseGuid
            FROM LocalDailyCloses
            WHERE UploadStatus = 'Pending'
              AND StoreCode = $StoreCode
              AND DeviceCode = $DeviceCode
              AND (NextUploadAt IS NULL OR NextUploadAt <= $Now)
            ORDER BY COALESCE(NextUploadAt, ''), julianday(SavedAt)
            LIMIT $Take;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$Now", FormatUtc(now));
        command.Parameters.AddWithValue("$Take", Math.Clamp(take, 1, 100));

        var guids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Guid.TryParse(reader.GetString(0), out var guid))
            {
                guids.Add(guid);
            }
        }

        return guids;
    }

    public async Task<LocalDailyCloseUploadLease?> TryClaimUploadAsync(
        Guid dailyCloseGuid,
        string storeCode,
        string deviceCode,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        // 关键逻辑：认领是单条 UPDATE，条件（Pending + 已到期 + 范围一致）与状态变更在同一条语句里原子完成，
        // 并发认领同一条记录时只有一个能把 Pending 改成 Uploading，其余影响行数为 0。
        await using (var claimCommand = connection.CreateCommand())
        {
            claimCommand.Transaction = transaction;
            claimCommand.CommandText = """
                UPDATE LocalDailyCloses
                SET UploadStatus = 'Uploading',
                    UploadAttemptCount = UploadAttemptCount + 1,
                    LastUploadAttemptAt = $AttemptedAt,
                    NextUploadAt = NULL,
                    UploadErrorCode = NULL,
                    UploadErrorMessage = NULL
                WHERE DailyCloseGuid = $DailyCloseGuid
                  AND StoreCode = $StoreCode
                  AND DeviceCode = $DeviceCode
                  AND UploadStatus = 'Pending'
                  AND (NextUploadAt IS NULL OR NextUploadAt <= $AttemptedAt);
                """;
            claimCommand.Parameters.AddWithValue("$DailyCloseGuid", dailyCloseGuid.ToString());
            claimCommand.Parameters.AddWithValue("$StoreCode", storeCode);
            claimCommand.Parameters.AddWithValue("$DeviceCode", deviceCode);
            claimCommand.Parameters.AddWithValue("$AttemptedAt", FormatUtc(attemptedAt));
            if (await claimCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        int attemptCount;
        await using (var readCommand = connection.CreateCommand())
        {
            readCommand.Transaction = transaction;
            readCommand.CommandText = "SELECT UploadAttemptCount FROM LocalDailyCloses WHERE DailyCloseGuid = $DailyCloseGuid;";
            readCommand.Parameters.AddWithValue("$DailyCloseGuid", dailyCloseGuid.ToString());
            attemptCount = Convert.ToInt32(
                await readCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        await transaction.CommitAsync(cancellationToken);
        return new LocalDailyCloseUploadLease(dailyCloseGuid, attemptCount);
    }

    public async Task<DailyCloseArchive?> GetArchiveForUploadAsync(
        Guid dailyCloseGuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        DailyCloseArchiveRow row;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT DailyCloseGuid, StoreCode, DeviceCode, CashierId, CashierName, BusinessDate, PeriodFrom, PeriodTo, SavedAt,
                       OrderCount, CashSalesAmount, CashRefundAmount, CashNetAmount, CardSalesAmount, CardRefundAmount, CardNetAmount,
                       VoucherSalesAmount, VoucherRefundAmount, VoucherNetAmount, RefundAmount, ReturnQuantity, NoteSubtotal,
                       CoinSubtotal, CountedCashAmount, CashDifference
                FROM LocalDailyCloses
                WHERE DailyCloseGuid = $DailyCloseGuid;
                """;
            command.Parameters.AddWithValue("$DailyCloseGuid", dailyCloseGuid.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            row = ReadArchiveRow(reader);
        }

        // 与 GetArchivesAsync 一致：面额明细缺档时补 0，保证恰好 11 档，不在这里"修正"任何金额。
        var cashCounts = NormalizeCounts(await ReadCashCountsAsync(connection, row.DailyCloseGuid, cancellationToken));
        return new DailyCloseArchive(
            row.DailyCloseGuid,
            row.Report,
            cashCounts,
            row.SavedAt,
            row.NoteSubtotal,
            row.CoinSubtotal,
            row.CountedCashAmount,
            row.CashDifference);
    }

    public async Task MarkUploadSucceededAsync(
        Guid dailyCloseGuid,
        DateTimeOffset uploadedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // 不限定 Uploading：服务端已确认收下，即使租约恰好被回收成 Pending 也应记为已同步，避免重复上传。
        command.CommandText = """
            UPDATE LocalDailyCloses
            SET UploadStatus = 'Synced',
                NextUploadAt = NULL,
                UploadErrorCode = NULL,
                UploadErrorMessage = NULL,
                UploadedAt = $UploadedAt
            WHERE DailyCloseGuid = $DailyCloseGuid;
            """;
        command.Parameters.AddWithValue("$DailyCloseGuid", dailyCloseGuid.ToString());
        command.Parameters.AddWithValue("$UploadedAt", FormatUtc(uploadedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task MarkUploadPendingAsync(
        Guid dailyCloseGuid,
        DateTimeOffset nextUploadAt,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        return UpdateUploadFailureAsync(
            dailyCloseGuid,
            LocalDailyCloseUploadStatus.Pending,
            nextUploadAt,
            errorCode,
            errorMessage,
            cancellationToken);
    }

    public Task MarkUploadRejectedAsync(
        Guid dailyCloseGuid,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        return UpdateUploadFailureAsync(
            dailyCloseGuid,
            LocalDailyCloseUploadStatus.Rejected,
            nextUploadAt: null,
            errorCode,
            errorMessage,
            cancellationToken);
    }

    public async Task ReleaseUploadWithoutAttemptAsync(
        Guid dailyCloseGuid,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LocalDailyCloses
            SET UploadStatus = 'Pending',
                UploadAttemptCount = CASE WHEN UploadAttemptCount > 0 THEN UploadAttemptCount - 1 ELSE 0 END,
                NextUploadAt = NULL,
                UploadErrorCode = $UploadErrorCode,
                UploadErrorMessage = $UploadErrorMessage
            WHERE DailyCloseGuid = $DailyCloseGuid
              AND UploadStatus = 'Uploading';
            """;
        command.Parameters.AddWithValue("$DailyCloseGuid", dailyCloseGuid.ToString());
        command.Parameters.AddWithValue("$UploadErrorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$UploadErrorMessage", (object?)errorMessage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecoverExpiredUploadingAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset nextUploadAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LocalDailyCloses
            SET UploadStatus = 'Pending',
                NextUploadAt = $NextUploadAt,
                UploadErrorCode = 'UPLOAD_LEASE_EXPIRED',
                UploadErrorMessage = 'The previous upload lease expired and was queued for retry.'
            WHERE UploadStatus = 'Uploading'
              AND (LastUploadAttemptAt IS NULL OR LastUploadAttemptAt <= $StaleBefore);
            """;
        command.Parameters.AddWithValue("$StaleBefore", FormatUtc(staleBefore));
        command.Parameters.AddWithValue("$NextUploadAt", FormatUtc(nextUploadAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateUploadFailureAsync(
        Guid dailyCloseGuid,
        LocalDailyCloseUploadStatus uploadStatus,
        DateTimeOffset? nextUploadAt,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // 只有持有租约（Uploading）的一方才能改状态，避免旧请求的迟到结果覆盖已被别处接管的记录。
        command.CommandText = """
            UPDATE LocalDailyCloses
            SET UploadStatus = $UploadStatus,
                NextUploadAt = $NextUploadAt,
                UploadErrorCode = $UploadErrorCode,
                UploadErrorMessage = $UploadErrorMessage
            WHERE DailyCloseGuid = $DailyCloseGuid
              AND UploadStatus = 'Uploading';
            """;
        command.Parameters.AddWithValue("$DailyCloseGuid", dailyCloseGuid.ToString());
        command.Parameters.AddWithValue("$UploadStatus", uploadStatus.ToString());
        command.Parameters.AddWithValue("$NextUploadAt", nextUploadAt is { } dueAt ? FormatUtc(dueAt) : DBNull.Value);
        command.Parameters.AddWithValue("$UploadErrorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$UploadErrorMessage", (object?)errorMessage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 上传状态列里的时间一律写成 UTC 的 ISO "O" 串（固定 7 位小数、+00:00），
    /// 这样 SQL 里按字符串比较"是否到期"与按时间先后比较结果一致。
    /// </summary>
    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}
