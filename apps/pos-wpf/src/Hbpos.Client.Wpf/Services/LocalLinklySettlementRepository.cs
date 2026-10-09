using System.Globalization;
using System.Text.Json;
using Hbpos.Contracts.Linkly;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public enum LocalLinklySettlementStatus
{
    Pending,
    Succeeded,
    Failed,
    Unknown
}

public enum LocalLinklySettlementUploadStatus
{
    Pending,
    Uploading,
    Synced,
    Rejected
}

public enum LocalLinklySettlementManualResolution
{
    ConfirmedSucceeded,
    ConfirmedFailed,
    ConfirmedNotSubmitted
}

public sealed record LocalLinklySettlementRecord(
    Guid SettlementGuid,
    string StoreCode,
    string DeviceCode,
    DateTime BusinessDate,
    string ConnectionMode,
    string Environment,
    string? ProviderSessionId,
    LocalLinklySettlementStatus Status,
    string? ResponseCode,
    string? ResponseText,
    string? SettlementData,
    IReadOnlyList<string> ReceiptTexts,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? FirstPrintedAt,
    DateTimeOffset? LastPrintedAt,
    int PrintCount,
    string? LastPrintError)
{
    public string RequestedAtDisplay => RequestedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public LocalLinklySettlementUploadStatus UploadStatus { get; init; } = LocalLinklySettlementUploadStatus.Pending;

    public long PayloadRevision { get; init; } = 1;

    public long UploadedRevision { get; init; }

    public int UploadAttemptCount { get; init; }

    public DateTimeOffset? NextUploadAt { get; init; }

    public DateTimeOffset? LastUploadAttemptAt { get; init; }

    public string? UploadErrorCode { get; init; }

    public string? UploadErrorMessage { get; init; }

    public DateTimeOffset? UploadedAt { get; init; }

    public ProviderSubmissionState ProviderSubmissionState { get; init; } = ProviderSubmissionState.Unknown;
}

public sealed record LocalLinklySettlementUploadLease(
    LocalLinklySettlementRecord Settlement,
    long PayloadRevision);

public sealed record LinklySettlementUploadOverview(
    int PendingCount,
    int FailedCount,
    int UploadingCount,
    string? LastError);

public sealed record LinklySettlementUploadQueueItem(
    Guid SettlementGuid,
    string StoreCode,
    string DeviceCode,
    DateTime BusinessDate,
    LocalLinklySettlementUploadStatus Status,
    DateTimeOffset CreatedAt,
    long PayloadRevision,
    long UploadedRevision,
    int UploadAttemptCount,
    DateTimeOffset? NextUploadAt,
    DateTimeOffset? LastTriedAt,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? UploadedAt,
    string ConnectionMode = "",
    LocalLinklySettlementStatus SettlementStatus = LocalLinklySettlementStatus.Unknown,
    ProviderSubmissionState ProviderSubmissionState = ProviderSubmissionState.Unknown);

public sealed record LocalLinklySettlementCompletion(
    LocalLinklySettlementStatus Status,
    string? ResponseCode,
    string? ResponseText,
    string? SettlementData,
    IReadOnlyList<string>? ReceiptTexts,
    DateTimeOffset CompletedAt,
    ProviderSubmissionState ProviderSubmissionState = ProviderSubmissionState.Unknown);

public interface ILinklyUnresolvedSettlementReader
{
    Task<bool> HasUnresolvedAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken = default);
}

public interface ILocalLinklySettlementRepository : ILinklyUnresolvedSettlementReader
{

    Task CreatePendingAsync(LocalLinklySettlementRecord settlement, CancellationToken cancellationToken = default);

    Task<bool> TryCreatePendingAsync(LocalLinklySettlementRecord settlement, CancellationToken cancellationToken = default);

    Task BindProviderSessionAsync(
        Guid settlementGuid,
        string providerSessionId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteUnboundPendingAsync(Guid settlementGuid, CancellationToken cancellationToken = default);

    Task CompleteAsync(
        Guid settlementGuid,
        LocalLinklySettlementCompletion completion,
        CancellationToken cancellationToken = default);

    Task<bool> TryResolveUncertainAsync(
        Guid settlementGuid,
        long expectedPayloadRevision,
        LocalLinklySettlementManualResolution resolution,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default);

    Task MarkPrintedAsync(Guid settlementGuid, DateTimeOffset printedAt, CancellationToken cancellationToken = default);

    Task MarkPrintFailedAsync(
        Guid settlementGuid,
        string error,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken = default);

    Task<LocalLinklySettlementRecord?> GetByProviderSessionIdAsync(
        string providerSessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalLinklySettlementRecord>> GetByBusinessDateAsync(
        string storeCode,
        string deviceCode,
        DateTime businessDate,
        CancellationToken cancellationToken = default);

    // 不分营业日的未决（Pending/Unknown）记录，按营业日、请求时间升序：前一天遗留的未决结算靠它找回。
    Task<IReadOnlyList<LocalLinklySettlementRecord>> GetUnresolvedAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken = default);

    Task<LinklySettlementUploadOverview> GetUploadOverviewAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LinklySettlementUploadQueueItem>> GetActiveUploadItemsAsync(
        int take = 20,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetDueUploadSettlementGuidsAsync(
        int take,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<LocalLinklySettlementUploadLease?> TryClaimUploadAsync(
        Guid settlementGuid,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default);

    Task MarkUploadSucceededAsync(
        Guid settlementGuid,
        long payloadRevision,
        DateTimeOffset uploadedAt,
        CancellationToken cancellationToken = default);

    Task MarkUploadPendingAsync(
        Guid settlementGuid,
        long payloadRevision,
        DateTimeOffset nextUploadAt,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    Task MarkUploadRejectedAsync(
        Guid settlementGuid,
        long payloadRevision,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    Task RecoverExpiredUploadingAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset nextUploadAt,
        CancellationToken cancellationToken = default);

    Task ResetUploadForRetryAsync(
        Guid settlementGuid,
        DateTimeOffset nextUploadAt,
        CancellationToken cancellationToken = default);
}

public sealed class LocalLinklySettlementRepository(LocalSqliteStore store) : ILocalLinklySettlementRepository
{
    public async Task<bool> HasUnresolvedAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS
            (
                SELECT 1
                FROM LinklySettlementRecords
                WHERE StoreCode = $StoreCode
                  AND DeviceCode = $DeviceCode
                  AND Environment = $Environment
                  AND Status IN ($PendingStatus, $UnknownStatus)
            );
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$Environment", environment);
        command.Parameters.AddWithValue("$PendingStatus", LocalLinklySettlementStatus.Pending.ToString());
        command.Parameters.AddWithValue("$UnknownStatus", LocalLinklySettlementStatus.Unknown.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public async Task CreatePendingAsync(LocalLinklySettlementRecord settlement, CancellationToken cancellationToken = default)
    {
        if (!await TryCreatePendingAsync(settlement, cancellationToken))
        {
            throw new InvalidOperationException("An unresolved Linkly settlement already exists for this business date.");
        }
    }

    public async Task<bool> TryCreatePendingAsync(LocalLinklySettlementRecord settlement, CancellationToken cancellationToken = default)
    {
        if (settlement.Status != LocalLinklySettlementStatus.Pending)
        {
            throw new ArgumentException("Only pending Linkly settlements can be created.", nameof(settlement));
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LinklySettlementRecords
            (
                SettlementGuid, StoreCode, DeviceCode, BusinessDate, ConnectionMode, Environment,
                ProviderSessionId, Status, ResponseCode, ResponseText, SettlementData, ReceiptTextsJson,
                RequestedAt, CompletedAt, FirstPrintedAt, LastPrintedAt, PrintCount, LastPrintError, NextUploadAt,
                ProviderSubmissionState
            )
            SELECT
                $SettlementGuid, $StoreCode, $DeviceCode, $BusinessDate, $ConnectionMode, $Environment,
                $ProviderSessionId, $Status, $ResponseCode, $ResponseText, $SettlementData, $ReceiptTextsJson,
                $RequestedAt, $CompletedAt, $FirstPrintedAt, $LastPrintedAt, $PrintCount, $LastPrintError, $NextUploadAt,
                $ProviderSubmissionState
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM LinklySettlementRecords
                WHERE StoreCode = $StoreCode
                  AND DeviceCode = $DeviceCode
                  AND BusinessDate = $BusinessDate
                  AND Status IN ($PendingStatus, $UnknownStatus)
            );
            """;
        AddRecordParameters(command, settlement);
        command.Parameters.AddWithValue("$PendingStatus", LocalLinklySettlementStatus.Pending.ToString());
        command.Parameters.AddWithValue("$UnknownStatus", LocalLinklySettlementStatus.Unknown.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task BindProviderSessionAsync(
        Guid settlementGuid,
        string providerSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerSessionId);

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LinklySettlementRecords
            SET ProviderSessionId = $ProviderSessionId,
                ProviderSubmissionState = 'Submitted',
                UploadStatus = 'Pending',
                PayloadRevision = PayloadRevision + 1,
                UploadAttemptCount = 0,
                NextUploadAt = $NextUploadAt,
                LastUploadAttemptAt = NULL,
                UploadErrorCode = NULL,
                UploadErrorMessage = NULL,
                UploadedAt = NULL
            WHERE SettlementGuid = $SettlementGuid
              AND (ProviderSessionId IS NULL OR ProviderSessionId = $ProviderSessionId);
            """;
        command.Parameters.AddWithValue("$SettlementGuid", settlementGuid.ToString("D"));
        command.Parameters.AddWithValue("$ProviderSessionId", providerSessionId);
        command.Parameters.AddWithValue("$NextUploadAt", DateTimeOffset.UtcNow.ToString("O"));
        await EnsureSingleUpdateAsync(command, cancellationToken);
    }

    public async Task<bool> DeleteUnboundPendingAsync(Guid settlementGuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM LinklySettlementRecords
            WHERE SettlementGuid = $SettlementGuid
              AND Status = $PendingStatus
              AND ProviderSessionId IS NULL;
            """;
        command.Parameters.AddWithValue("$SettlementGuid", settlementGuid.ToString("D"));
        command.Parameters.AddWithValue("$PendingStatus", LocalLinklySettlementStatus.Pending.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task CompleteAsync(
        Guid settlementGuid,
        LocalLinklySettlementCompletion completion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LinklySettlementRecords
            SET Status = $Status,
                ProviderSubmissionState = $ProviderSubmissionState,
                ResponseCode = $ResponseCode,
                ResponseText = $ResponseText,
                SettlementData = $SettlementData,
                ReceiptTextsJson = $ReceiptTextsJson,
                CompletedAt = $CompletedAt,
                UploadStatus = 'Pending',
                PayloadRevision = PayloadRevision + 1,
                UploadAttemptCount = 0,
                NextUploadAt = $NextUploadAt,
                LastUploadAttemptAt = NULL,
                UploadErrorCode = NULL,
                UploadErrorMessage = NULL,
                UploadedAt = NULL
            WHERE SettlementGuid = $SettlementGuid;
            """;
        command.Parameters.AddWithValue("$SettlementGuid", settlementGuid.ToString("D"));
        command.Parameters.AddWithValue("$Status", completion.Status.ToString());
        command.Parameters.AddWithValue("$ProviderSubmissionState", completion.ProviderSubmissionState.ToString());
        command.Parameters.AddWithValue("$ResponseCode", (object?)completion.ResponseCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$ResponseText", ToDbValue(LinklyReceiptTextSanitizer.Sanitize(completion.ResponseText)));
        command.Parameters.AddWithValue("$SettlementData", ToDbValue(LinklyReceiptTextSanitizer.SanitizeSettlementData(completion.SettlementData)));
        command.Parameters.AddWithValue("$ReceiptTextsJson", SerializeReceiptTexts(completion.ReceiptTexts));
        command.Parameters.AddWithValue("$CompletedAt", completion.CompletedAt.ToString("O"));
        command.Parameters.AddWithValue("$NextUploadAt", completion.CompletedAt.ToString("O"));
        await EnsureSingleUpdateAsync(command, cancellationToken);
    }

    public async Task<bool> TryResolveUncertainAsync(
        Guid settlementGuid,
        long expectedPayloadRevision,
        LocalLinklySettlementManualResolution resolution,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
    {
        var (status, submissionState, responseText) = resolution switch
        {
            LocalLinklySettlementManualResolution.ConfirmedSucceeded => (
                LocalLinklySettlementStatus.Succeeded,
                ProviderSubmissionState.Submitted,
                "Settlement outcome was manually confirmed as succeeded."),
            LocalLinklySettlementManualResolution.ConfirmedFailed => (
                LocalLinklySettlementStatus.Failed,
                ProviderSubmissionState.Submitted,
                "Settlement outcome was manually confirmed as failed."),
            LocalLinklySettlementManualResolution.ConfirmedNotSubmitted => (
                LocalLinklySettlementStatus.Failed,
                ProviderSubmissionState.NotSubmitted,
                "Settlement was manually confirmed as not submitted."),
            _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, null)
        };

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LinklySettlementRecords
            SET Status = $Status,
                ProviderSubmissionState = $ProviderSubmissionState,
                ResponseCode = NULL,
                ResponseText = $ResponseText,
                CompletedAt = $CompletedAt,
                UploadStatus = 'Pending',
                PayloadRevision = PayloadRevision + 1,
                UploadAttemptCount = 0,
                NextUploadAt = $CompletedAt,
                LastUploadAttemptAt = NULL,
                UploadErrorCode = NULL,
                UploadErrorMessage = NULL,
                UploadedAt = NULL
            WHERE SettlementGuid = $SettlementGuid
              AND PayloadRevision = $ExpectedPayloadRevision
              AND Status IN ('Pending', 'Unknown');
            """;
        command.Parameters.AddWithValue("$SettlementGuid", settlementGuid.ToString("D"));
        command.Parameters.AddWithValue("$ExpectedPayloadRevision", expectedPayloadRevision);
        command.Parameters.AddWithValue("$Status", status.ToString());
        command.Parameters.AddWithValue("$ProviderSubmissionState", submissionState.ToString());
        command.Parameters.AddWithValue("$ResponseText", responseText);
        command.Parameters.AddWithValue("$CompletedAt", resolvedAt.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task MarkPrintedAsync(Guid settlementGuid, DateTimeOffset printedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LinklySettlementRecords
            SET FirstPrintedAt = COALESCE(FirstPrintedAt, $PrintedAt),
                LastPrintedAt = $PrintedAt,
                PrintCount = PrintCount + 1,
                LastPrintError = NULL,
                UploadStatus = 'Pending',
                PayloadRevision = PayloadRevision + 1,
                UploadAttemptCount = 0,
                NextUploadAt = $PrintedAt,
                LastUploadAttemptAt = NULL,
                UploadErrorCode = NULL,
                UploadErrorMessage = NULL,
                UploadedAt = NULL
            WHERE SettlementGuid = $SettlementGuid;
            """;
        command.Parameters.AddWithValue("$SettlementGuid", settlementGuid.ToString("D"));
        command.Parameters.AddWithValue("$PrintedAt", printedAt.ToString("O"));
        await EnsureSingleUpdateAsync(command, cancellationToken);
    }

    public async Task MarkPrintFailedAsync(
        Guid settlementGuid,
        string error,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LinklySettlementRecords
            SET LastPrintError = $LastPrintError,
                UploadStatus = 'Pending',
                PayloadRevision = PayloadRevision + 1,
                UploadAttemptCount = 0,
                NextUploadAt = $NextUploadAt,
                LastUploadAttemptAt = NULL,
                UploadErrorCode = NULL,
                UploadErrorMessage = NULL,
                UploadedAt = NULL
            WHERE SettlementGuid = $SettlementGuid;
        """;
        command.Parameters.AddWithValue("$SettlementGuid", settlementGuid.ToString("D"));
        command.Parameters.AddWithValue("$NextUploadAt", failedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$LastPrintError",
            LinklyReceiptTextSanitizer.Sanitize(string.IsNullOrWhiteSpace(error) ? "Print failed." : error));
        await EnsureSingleUpdateAsync(command, cancellationToken);
    }

    public async Task<LocalLinklySettlementRecord?> GetByProviderSessionIdAsync(
        string providerSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerSessionId);

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT *
            FROM LinklySettlementRecords
            WHERE ProviderSessionId = $ProviderSessionId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ProviderSessionId", providerSessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    public async Task<IReadOnlyList<LocalLinklySettlementRecord>> GetByBusinessDateAsync(
        string storeCode,
        string deviceCode,
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT *
            FROM LinklySettlementRecords
            WHERE StoreCode = $StoreCode
              AND DeviceCode = $DeviceCode
              AND BusinessDate = $BusinessDate
            ORDER BY RequestedAt DESC;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$BusinessDate", businessDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var settlements = new List<LocalLinklySettlementRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            settlements.Add(ReadRecord(reader));
        }

        return settlements;
    }

    public async Task<IReadOnlyList<LocalLinklySettlementRecord>> GetUnresolvedAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT *
            FROM LinklySettlementRecords
            WHERE StoreCode = $StoreCode
              AND DeviceCode = $DeviceCode
              AND Environment = $Environment
              AND Status IN ($PendingStatus, $UnknownStatus)
            ORDER BY BusinessDate ASC, RequestedAt ASC;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$Environment", environment);
        command.Parameters.AddWithValue("$PendingStatus", LocalLinklySettlementStatus.Pending.ToString());
        command.Parameters.AddWithValue("$UnknownStatus", LocalLinklySettlementStatus.Unknown.ToString());

        var settlements = new List<LocalLinklySettlementRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            settlements.Add(ReadRecord(reader));
        }

        return settlements;
    }

    public async Task<LinklySettlementUploadOverview> GetUploadOverviewAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN UploadStatus = 'Pending' THEN 1 ELSE 0 END) AS PendingCount,
                SUM(CASE WHEN UploadStatus = 'Uploading' THEN 1 ELSE 0 END) AS UploadingCount,
                SUM(CASE WHEN UploadStatus = 'Rejected' THEN 1 ELSE 0 END) AS RejectedCount,
                (
                    SELECT UploadErrorMessage
                    FROM LinklySettlementRecords
                    WHERE UploadErrorMessage IS NOT NULL AND TRIM(UploadErrorMessage) <> ''
                    ORDER BY COALESCE(LastUploadAttemptAt, RequestedAt) DESC
                    LIMIT 1
                ) AS LastError
            FROM LinklySettlementRecords;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new LinklySettlementUploadOverview(0, 0, 0, null);
        }

        return new LinklySettlementUploadOverview(
            ReadInt(reader, "PendingCount"),
            ReadInt(reader, "RejectedCount"),
            ReadInt(reader, "UploadingCount"),
            ReadNullableString(reader, "LastError"));
    }

    public async Task<IReadOnlyList<LinklySettlementUploadQueueItem>> GetActiveUploadItemsAsync(
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SettlementGuid, StoreCode, DeviceCode, BusinessDate, RequestedAt, UploadStatus, PayloadRevision, UploadedRevision,
                   UploadAttemptCount, NextUploadAt, LastUploadAttemptAt, UploadErrorCode, UploadErrorMessage, UploadedAt,
                   ConnectionMode, Status, ProviderSubmissionState
            FROM LinklySettlementRecords
            WHERE UploadStatus IN ('Pending', 'Uploading', 'Rejected')
            ORDER BY RequestedAt DESC
            LIMIT $Take;
            """;
        command.Parameters.AddWithValue("$Take", take == int.MaxValue ? -1 : Math.Clamp(take, 1, 100));

        var items = new List<LinklySettlementUploadQueueItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new LinklySettlementUploadQueueItem(
                Guid.Parse(reader.GetString(reader.GetOrdinal("SettlementGuid"))),
                reader.GetString(reader.GetOrdinal("StoreCode")),
                reader.GetString(reader.GetOrdinal("DeviceCode")),
                DateTime.Parse(reader.GetString(reader.GetOrdinal("BusinessDate")), CultureInfo.InvariantCulture).Date,
                Enum.Parse<LocalLinklySettlementUploadStatus>(reader.GetString(reader.GetOrdinal("UploadStatus")), ignoreCase: true),
                ReadDateTimeOffset(reader, "RequestedAt"),
                reader.GetInt64(reader.GetOrdinal("PayloadRevision")),
                reader.GetInt64(reader.GetOrdinal("UploadedRevision")),
                reader.GetInt32(reader.GetOrdinal("UploadAttemptCount")),
                ReadNullableDateTimeOffset(reader, "NextUploadAt"),
                ReadNullableDateTimeOffset(reader, "LastUploadAttemptAt"),
                ReadNullableString(reader, "UploadErrorCode"),
                ReadNullableString(reader, "UploadErrorMessage"),
                ReadNullableDateTimeOffset(reader, "UploadedAt"),
                reader.GetString(reader.GetOrdinal("ConnectionMode")),
                ReadSettlementStatus(reader),
                ReadProviderSubmissionState(reader)));
        }

        return items;
    }

    public async Task<IReadOnlyList<Guid>> GetDueUploadSettlementGuidsAsync(
        int take,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SettlementGuid
            FROM LinklySettlementRecords
            WHERE UploadStatus = 'Pending'
              AND (NextUploadAt IS NULL OR NextUploadAt <= $Now)
            ORDER BY COALESCE(NextUploadAt, RequestedAt), RequestedAt
            LIMIT $Take;
            """;
        command.Parameters.AddWithValue("$Now", now.ToString("O"));
        command.Parameters.AddWithValue("$Take", Math.Clamp(take, 1, 100));

        var settlementGuids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            settlementGuids.Add(Guid.Parse(reader.GetString(0)));
        }

        return settlementGuids;
    }

    public async Task<LocalLinklySettlementUploadLease?> TryClaimUploadAsync(
        Guid settlementGuid,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        LocalLinklySettlementRecord? settlement;
        await using (var readCommand = connection.CreateCommand())
        {
            readCommand.Transaction = transaction;
            readCommand.CommandText = """
                SELECT *
                FROM LinklySettlementRecords
                WHERE SettlementGuid = $SettlementGuid
                  AND UploadStatus = 'Pending'
                  AND (NextUploadAt IS NULL OR NextUploadAt <= $AttemptedAt)
                LIMIT 1;
                """;
            readCommand.Parameters.AddWithValue("$SettlementGuid", settlementGuid.ToString("D"));
            readCommand.Parameters.AddWithValue("$AttemptedAt", attemptedAt.ToString("O"));
            await using var reader = await readCommand.ExecuteReaderAsync(cancellationToken);
            settlement = await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
        }

        if (settlement is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var claimCommand = connection.CreateCommand())
        {
            claimCommand.Transaction = transaction;
            claimCommand.CommandText = """
                UPDATE LinklySettlementRecords
                SET UploadStatus = 'Uploading',
                    UploadAttemptCount = UploadAttemptCount + 1,
                    LastUploadAttemptAt = $AttemptedAt,
                    NextUploadAt = NULL,
                    UploadErrorCode = NULL,
                    UploadErrorMessage = NULL
                WHERE SettlementGuid = $SettlementGuid
                  AND UploadStatus = 'Pending'
                  AND PayloadRevision = $PayloadRevision;
                """;
            claimCommand.Parameters.AddWithValue("$SettlementGuid", settlementGuid.ToString("D"));
            claimCommand.Parameters.AddWithValue("$PayloadRevision", settlement.PayloadRevision);
            claimCommand.Parameters.AddWithValue("$AttemptedAt", attemptedAt.ToString("O"));
            if (await claimCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new LocalLinklySettlementUploadLease(
            settlement with
            {
                UploadStatus = LocalLinklySettlementUploadStatus.Uploading,
                UploadAttemptCount = settlement.UploadAttemptCount + 1,
                LastUploadAttemptAt = attemptedAt,
                NextUploadAt = null,
                UploadErrorCode = null,
                UploadErrorMessage = null
            },
            settlement.PayloadRevision);
    }

    public async Task MarkUploadSucceededAsync(
        Guid settlementGuid,
        long payloadRevision,
        DateTimeOffset uploadedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LinklySettlementRecords
            SET UploadedRevision = CASE WHEN UploadedRevision < $PayloadRevision THEN $PayloadRevision ELSE UploadedRevision END,
                UploadStatus = CASE WHEN PayloadRevision = $PayloadRevision THEN 'Synced' ELSE UploadStatus END,
                NextUploadAt = CASE WHEN PayloadRevision = $PayloadRevision THEN NULL ELSE NextUploadAt END,
                UploadErrorCode = CASE WHEN PayloadRevision = $PayloadRevision THEN NULL ELSE UploadErrorCode END,
                UploadErrorMessage = CASE WHEN PayloadRevision = $PayloadRevision THEN NULL ELSE UploadErrorMessage END,
                UploadedAt = CASE WHEN PayloadRevision = $PayloadRevision THEN $UploadedAt ELSE UploadedAt END
            WHERE SettlementGuid = $SettlementGuid;
            """;
        command.Parameters.AddWithValue("$SettlementGuid", settlementGuid.ToString("D"));
        command.Parameters.AddWithValue("$PayloadRevision", payloadRevision);
        command.Parameters.AddWithValue("$UploadedAt", uploadedAt.ToString("O"));
        await EnsureSingleUpdateAsync(command, cancellationToken);
    }

    public Task MarkUploadPendingAsync(
        Guid settlementGuid,
        long payloadRevision,
        DateTimeOffset nextUploadAt,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        return UpdateUploadFailureAsync(
            settlementGuid,
            payloadRevision,
            LocalLinklySettlementUploadStatus.Pending,
            nextUploadAt,
            errorCode,
            errorMessage,
            cancellationToken);
    }

    public Task MarkUploadRejectedAsync(
        Guid settlementGuid,
        long payloadRevision,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        return UpdateUploadFailureAsync(
            settlementGuid,
            payloadRevision,
            LocalLinklySettlementUploadStatus.Rejected,
            nextUploadAt: null,
            errorCode,
            errorMessage,
            cancellationToken);
    }

    public async Task RecoverExpiredUploadingAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset nextUploadAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LinklySettlementRecords
            SET UploadStatus = 'Pending',
                NextUploadAt = $NextUploadAt,
                UploadErrorCode = 'UPLOAD_LEASE_EXPIRED',
                UploadErrorMessage = 'The previous upload lease expired and was queued for retry.'
            WHERE UploadStatus = 'Uploading'
              AND (LastUploadAttemptAt IS NULL OR LastUploadAttemptAt <= $StaleBefore);
            """;
        command.Parameters.AddWithValue("$StaleBefore", staleBefore.ToString("O"));
        command.Parameters.AddWithValue("$NextUploadAt", nextUploadAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResetUploadForRetryAsync(
        Guid settlementGuid,
        DateTimeOffset nextUploadAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LinklySettlementRecords
            SET UploadStatus = 'Pending',
                NextUploadAt = $NextUploadAt,
                UploadErrorCode = NULL,
                UploadErrorMessage = NULL
            WHERE SettlementGuid = $SettlementGuid
              AND UploadStatus IN ('Pending', 'Rejected');
            """;
        command.Parameters.AddWithValue("$SettlementGuid", settlementGuid.ToString("D"));
        command.Parameters.AddWithValue("$NextUploadAt", nextUploadAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddRecordParameters(SqliteCommand command, LocalLinklySettlementRecord settlement)
    {
        command.Parameters.AddWithValue("$SettlementGuid", settlement.SettlementGuid.ToString("D"));
        command.Parameters.AddWithValue("$StoreCode", settlement.StoreCode);
        command.Parameters.AddWithValue("$DeviceCode", settlement.DeviceCode);
        command.Parameters.AddWithValue("$BusinessDate", settlement.BusinessDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$ConnectionMode", settlement.ConnectionMode);
        command.Parameters.AddWithValue("$Environment", settlement.Environment);
        command.Parameters.AddWithValue("$ProviderSessionId", (object?)settlement.ProviderSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$Status", settlement.Status.ToString());
        command.Parameters.AddWithValue("$ProviderSubmissionState", ProviderSubmissionState.Unknown.ToString());
        command.Parameters.AddWithValue("$ResponseCode", (object?)settlement.ResponseCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$ResponseText", ToDbValue(LinklyReceiptTextSanitizer.Sanitize(settlement.ResponseText)));
        command.Parameters.AddWithValue("$SettlementData", ToDbValue(LinklyReceiptTextSanitizer.SanitizeSettlementData(settlement.SettlementData)));
        command.Parameters.AddWithValue("$ReceiptTextsJson", SerializeReceiptTexts(settlement.ReceiptTexts));
        command.Parameters.AddWithValue("$RequestedAt", settlement.RequestedAt.ToString("O"));
        command.Parameters.AddWithValue("$CompletedAt", settlement.CompletedAt is { } completedAt ? completedAt.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$FirstPrintedAt", settlement.FirstPrintedAt is { } firstPrintedAt ? firstPrintedAt.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$LastPrintedAt", settlement.LastPrintedAt is { } lastPrintedAt ? lastPrintedAt.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$PrintCount", settlement.PrintCount);
        command.Parameters.AddWithValue("$LastPrintError", ToDbValue(LinklyReceiptTextSanitizer.Sanitize(settlement.LastPrintError)));
        // 未绑定云端会话的临时记录可能被并发请求清理，延迟首次上传以避免上传孤儿结算单。
        command.Parameters.AddWithValue("$NextUploadAt", settlement.RequestedAt.AddMinutes(5).ToString("O"));
    }

    private static async Task EnsureSingleUpdateAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Linkly settlement record was not found or was changed concurrently.");
        }
    }

    private static LocalLinklySettlementRecord ReadRecord(SqliteDataReader reader)
    {
        var settlement = new LocalLinklySettlementRecord(
            Guid.Parse(reader.GetString(reader.GetOrdinal("SettlementGuid"))),
            reader.GetString(reader.GetOrdinal("StoreCode")),
            reader.GetString(reader.GetOrdinal("DeviceCode")),
            DateTime.Parse(reader.GetString(reader.GetOrdinal("BusinessDate")), CultureInfo.InvariantCulture).Date,
            reader.GetString(reader.GetOrdinal("ConnectionMode")),
            reader.GetString(reader.GetOrdinal("Environment")),
            ReadNullableString(reader, "ProviderSessionId"),
            ReadSettlementStatus(reader),
            ReadNullableString(reader, "ResponseCode"),
            ReadNullableString(reader, "ResponseText"),
            ReadNullableString(reader, "SettlementData"),
            DeserializeReceiptTexts(reader.GetString(reader.GetOrdinal("ReceiptTextsJson"))),
            ReadDateTimeOffset(reader, "RequestedAt"),
            ReadNullableDateTimeOffset(reader, "CompletedAt"),
            ReadNullableDateTimeOffset(reader, "FirstPrintedAt"),
            ReadNullableDateTimeOffset(reader, "LastPrintedAt"),
            reader.GetInt32(reader.GetOrdinal("PrintCount")),
            ReadNullableString(reader, "LastPrintError"));

        return settlement with
        {
            UploadStatus = Enum.TryParse<LocalLinklySettlementUploadStatus>(
                reader.GetString(reader.GetOrdinal("UploadStatus")),
                ignoreCase: true,
                out var uploadStatus)
                ? uploadStatus
                : LocalLinklySettlementUploadStatus.Pending,
            PayloadRevision = reader.GetInt64(reader.GetOrdinal("PayloadRevision")),
            UploadedRevision = reader.GetInt64(reader.GetOrdinal("UploadedRevision")),
            UploadAttemptCount = reader.GetInt32(reader.GetOrdinal("UploadAttemptCount")),
            NextUploadAt = ReadNullableDateTimeOffset(reader, "NextUploadAt"),
            LastUploadAttemptAt = ReadNullableDateTimeOffset(reader, "LastUploadAttemptAt"),
            UploadErrorCode = ReadNullableString(reader, "UploadErrorCode"),
            UploadErrorMessage = ReadNullableString(reader, "UploadErrorMessage"),
            UploadedAt = ReadNullableDateTimeOffset(reader, "UploadedAt"),
            ProviderSubmissionState = ReadProviderSubmissionState(reader)
        };
    }

    private async Task UpdateUploadFailureAsync(
        Guid settlementGuid,
        long payloadRevision,
        LocalLinklySettlementUploadStatus uploadStatus,
        DateTimeOffset? nextUploadAt,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LinklySettlementRecords
            SET UploadStatus = $UploadStatus,
                NextUploadAt = $NextUploadAt,
                UploadErrorCode = $UploadErrorCode,
                UploadErrorMessage = $UploadErrorMessage
            WHERE SettlementGuid = $SettlementGuid
              AND PayloadRevision = $PayloadRevision
              AND UploadStatus = 'Uploading';
            """;
        command.Parameters.AddWithValue("$SettlementGuid", settlementGuid.ToString("D"));
        command.Parameters.AddWithValue("$PayloadRevision", payloadRevision);
        command.Parameters.AddWithValue("$UploadStatus", uploadStatus.ToString());
        command.Parameters.AddWithValue("$NextUploadAt", nextUploadAt is { } dueAt ? dueAt.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$UploadErrorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$UploadErrorMessage", (object?)errorMessage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static object ToDbValue(string? value) => string.IsNullOrEmpty(value) ? DBNull.Value : value;

    private static LocalLinklySettlementStatus ReadSettlementStatus(SqliteDataReader reader)
    {
        return Enum.TryParse<LocalLinklySettlementStatus>(
            reader.GetString(reader.GetOrdinal("Status")),
            ignoreCase: true,
            out var status)
            ? status
            : LocalLinklySettlementStatus.Unknown;
    }

    private static ProviderSubmissionState ReadProviderSubmissionState(SqliteDataReader reader)
    {
        var ordinal = reader.GetOrdinal("ProviderSubmissionState");
        return !reader.IsDBNull(ordinal) && Enum.TryParse<ProviderSubmissionState>(
            reader.GetString(ordinal),
            ignoreCase: true,
            out var state)
            ? state
            : ProviderSubmissionState.Unknown;
    }

    private static int ReadInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, string name)
    {
        return DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal(name)), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);
    }

    private static string SerializeReceiptTexts(IReadOnlyList<string>? receiptTexts)
    {
        return JsonSerializer.Serialize(LinklyReceiptTextSanitizer.SanitizeReceipts(receiptTexts));
    }

    private static IReadOnlyList<string> DeserializeReceiptTexts(string? receiptTextsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(receiptTextsJson ?? "[]") ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
