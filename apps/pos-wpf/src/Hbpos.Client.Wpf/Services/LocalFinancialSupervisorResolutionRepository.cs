using Hbpos.Client.Wpf.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace Hbpos.Client.Wpf.Services;

internal sealed class LocalFinancialSupervisorResolutionRepository(LocalSqliteStore store)
{
    public async Task<IReadOnlyList<LocalFinancialSupervisorResolution>> GetPendingAuditAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT *
            FROM LocalFinancialSupervisorResolutions
            WHERE AuditPersistedAt IS NULL
            ORDER BY ResolvedAt, ResolutionGuid
            LIMIT $Limit;
            """;
        command.Parameters.AddWithValue("$Limit", Math.Clamp(limit, 1, 1000));

        var results = new List<LocalFinancialSupervisorResolution>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(Read(reader));
        }

        return results;
    }

    public async Task<bool> TryMarkAuditPersistedAsync(
        Guid resolutionGuid,
        DateTimeOffset persistedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LocalFinancialSupervisorResolutions
            SET AuditPersistedAt = $AuditPersistedAt
            WHERE ResolutionGuid = $ResolutionGuid
              AND AuditPersistedAt IS NULL;
            """;
        command.Parameters.AddWithValue("$AuditPersistedAt", persistedAt.ToString("O"));
        command.Parameters.AddWithValue("$ResolutionGuid", resolutionGuid.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    internal static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalFinancialSupervisorResolution resolution,
        CancellationToken cancellationToken)
    {
        Validate(resolution);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO LocalFinancialSupervisorResolutions
            (
                ResolutionGuid, Target, Processor, Environment, StoreCode, DeviceCode,
                AttemptGuid, RefundStepGuid, OperationGuid, SessionId, Decision,
                OperatorCashierId, OperatorUserGuid, OperatorName, Reason, Evidence,
                FinancialReference, RetryReference, ResolvedAt, AuditEventId,
                AuditPayloadJson, AuditPersistedAt
            )
            VALUES
            (
                $ResolutionGuid, $Target, $Processor, $Environment, $StoreCode, $DeviceCode,
                $AttemptGuid, $RefundStepGuid, $OperationGuid, $SessionId, $Decision,
                $OperatorCashierId, $OperatorUserGuid, $OperatorName, $Reason, $Evidence,
                $FinancialReference, $RetryReference, $ResolvedAt, $AuditEventId,
                $AuditPayloadJson, $AuditPersistedAt
            );
            """;
        command.Parameters.AddWithValue("$ResolutionGuid", resolution.ResolutionGuid.ToString());
        command.Parameters.AddWithValue("$Target", resolution.Target.ToString());
        command.Parameters.AddWithValue("$Processor", resolution.Processor.Trim());
        command.Parameters.AddWithValue("$Environment", resolution.Environment.Trim());
        command.Parameters.AddWithValue("$StoreCode", resolution.StoreCode.Trim());
        command.Parameters.AddWithValue("$DeviceCode", resolution.DeviceCode.Trim());
        command.Parameters.AddWithValue("$AttemptGuid", DbValue(resolution.AttemptGuid));
        command.Parameters.AddWithValue("$RefundStepGuid", DbValue(resolution.RefundStepGuid));
        command.Parameters.AddWithValue("$OperationGuid", DbValue(resolution.OperationGuid));
        command.Parameters.AddWithValue("$SessionId", DbValue(Normalize(resolution.SessionId)));
        command.Parameters.AddWithValue("$Decision", resolution.Decision.Trim());
        command.Parameters.AddWithValue("$OperatorCashierId", resolution.OperatorCashierId.Trim());
        command.Parameters.AddWithValue("$OperatorUserGuid", DbValue(Normalize(resolution.OperatorUserGuid)));
        command.Parameters.AddWithValue("$OperatorName", DbValue(Normalize(resolution.OperatorName)));
        command.Parameters.AddWithValue("$Reason", resolution.Reason.Trim());
        command.Parameters.AddWithValue("$Evidence", DbValue(Normalize(resolution.Evidence)));
        command.Parameters.AddWithValue("$FinancialReference", DbValue(Normalize(resolution.FinancialReference)));
        command.Parameters.AddWithValue("$RetryReference", DbValue(Normalize(resolution.RetryReference)));
        command.Parameters.AddWithValue("$ResolvedAt", resolution.ResolvedAt.ToString("O"));
        command.Parameters.AddWithValue("$AuditEventId", resolution.AuditEventId.ToString());
        command.Parameters.AddWithValue("$AuditPayloadJson", resolution.AuditPayloadJson);
        command.Parameters.AddWithValue("$AuditPersistedAt", resolution.AuditPersistedAt is { } persistedAt
            ? persistedAt.ToString("O")
            : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Validate(LocalFinancialSupervisorResolution resolution)
    {
        if (resolution.ResolutionGuid == Guid.Empty ||
            resolution.AuditEventId == Guid.Empty ||
            string.IsNullOrWhiteSpace(resolution.Processor) ||
            string.IsNullOrWhiteSpace(resolution.Environment) ||
            string.IsNullOrWhiteSpace(resolution.StoreCode) ||
            string.IsNullOrWhiteSpace(resolution.DeviceCode) ||
            string.IsNullOrWhiteSpace(resolution.Decision) ||
            string.IsNullOrWhiteSpace(resolution.OperatorCashierId) ||
            resolution.Reason is null ||
            (resolution.Target != LocalFinancialSupervisorResolutionTarget.ActiveSession &&
             string.IsNullOrWhiteSpace(resolution.Reason)) ||
            string.IsNullOrWhiteSpace(resolution.AuditPayloadJson))
        {
            throw new ArgumentException("主管结案记录缺少必需的身份、原因或审计数据。", nameof(resolution));
        }

        if (resolution.Target == LocalFinancialSupervisorResolutionTarget.CardRefund &&
            resolution.AttemptGuid is null)
        {
            throw new ArgumentException("刷卡退款主管结案必须关联 attempt。", nameof(resolution));
        }

        if (resolution.Target == LocalFinancialSupervisorResolutionTarget.InstallmentRefund &&
            (resolution.RefundStepGuid is null || resolution.OperationGuid is null))
        {
            throw new ArgumentException("分期退款主管结案必须关联退款步骤和操作。", nameof(resolution));
        }

        if (resolution.Target == LocalFinancialSupervisorResolutionTarget.ActiveSession &&
            string.IsNullOrWhiteSpace(resolution.SessionId))
        {
            throw new ArgumentException("ActiveSession 主管结案必须关联 SessionId。", nameof(resolution));
        }
    }

    private static LocalFinancialSupervisorResolution Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(reader.GetOrdinal("ResolutionGuid"))),
        Enum.Parse<LocalFinancialSupervisorResolutionTarget>(reader.GetString(reader.GetOrdinal("Target"))),
        reader.GetString(reader.GetOrdinal("Processor")),
        reader.GetString(reader.GetOrdinal("Environment")),
        reader.GetString(reader.GetOrdinal("StoreCode")),
        reader.GetString(reader.GetOrdinal("DeviceCode")),
        ReadGuid(reader, "AttemptGuid"),
        ReadGuid(reader, "RefundStepGuid"),
        ReadGuid(reader, "OperationGuid"),
        ReadString(reader, "SessionId"),
        reader.GetString(reader.GetOrdinal("Decision")),
        reader.GetString(reader.GetOrdinal("OperatorCashierId")),
        ReadString(reader, "OperatorUserGuid"),
        ReadString(reader, "OperatorName"),
        reader.GetString(reader.GetOrdinal("Reason")),
        ReadString(reader, "Evidence"),
        ReadString(reader, "FinancialReference"),
        ReadString(reader, "RetryReference"),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("ResolvedAt"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("AuditEventId"))),
        reader.GetString(reader.GetOrdinal("AuditPayloadJson")),
        ReadDateTimeOffset(reader, "AuditPersistedAt"));

    private static Guid? ReadGuid(SqliteDataReader reader, string name)
    {
        var value = ReadString(reader, name);
        return Guid.TryParse(value, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, string name)
    {
        var value = ReadString(reader, name);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? ReadString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static object DbValue(Guid? value) => value is { } guid ? guid.ToString() : DBNull.Value;

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class FinancialSupervisorAuditReplayService
{
    private const int BatchSize = 100;
    private readonly LocalFinancialSupervisorResolutionRepository _resolutions;
    private readonly ClientLogOutboxStore _outbox;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal FinancialSupervisorAuditReplayService(
        LocalFinancialSupervisorResolutionRepository resolutions,
        ClientLogOutboxStore outbox)
    {
        _resolutions = resolutions;
        _outbox = outbox;
    }

    public async Task<bool> PersistAfterCommitAsync(
        LocalFinancialSupervisorResolution resolution,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await PersistCoreAsync(resolution, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplayPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                var pending = await _resolutions.GetPendingAuditAsync(BatchSize, cancellationToken);
                if (pending.Count == 0)
                {
                    return;
                }

                foreach (var resolution in pending)
                {
                    if (!await PersistCoreAsync(resolution, cancellationToken))
                    {
                        return;
                    }
                }

                if (pending.Count < BatchSize)
                {
                    return;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> PersistCoreAsync(
        LocalFinancialSupervisorResolution resolution,
        CancellationToken cancellationToken)
    {
        try
        {
            await _outbox.InitializeAsync(cancellationToken);
            await _outbox.EnqueueAsync(
                ClientLogOutboxKind.OperationAudit,
                resolution.AuditEventId,
                resolution.ResolvedAt,
                resolution.AuditPayloadJson,
                resolution.ResolvedAt,
                cancellationToken);
            await _resolutions.TryMarkAuditPersistedAsync(
                resolution.ResolutionGuid,
                DateTimeOffset.UtcNow,
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and
            not OutOfMemoryException and
            not StackOverflowException)
        {
            ConsoleLog.Write(
                "FinancialSupervisorAudit",
                $"supervisor audit persistence deferred resolutionGuid={resolution.ResolutionGuid:D} error={exception.GetType().Name}");
            return false;
        }
    }
}

internal sealed class FinancialSupervisorAuditReplayHostedService(
    ILocalSchemaService schema,
    FinancialSupervisorAuditReplayService replay) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 与主界面初始化共用同一次表结构初始化，避免启动时对同一数据库完整跑两遍。
            await schema.EnsureInitializedAsync(cancellationToken);
            await replay.ReplayPendingAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and
            not OutOfMemoryException and
            not StackOverflowException)
        {
            ConsoleLog.Write(
                "FinancialSupervisorAudit",
                $"startup supervisor audit replay deferred error={exception.GetType().Name}");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
