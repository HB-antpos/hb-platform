using System.Globalization;
using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public interface ILocalInstallmentOperationRepository
{
    Task<LocalInstallmentOperation> CreateOrGetAsync(LocalInstallmentOperation operation, CancellationToken cancellationToken = default);

    Task<LocalInstallmentOperation> CreateCancelOrGetAsync(
        LocalInstallmentOperation operation,
        IReadOnlyList<LocalInstallmentRefundStep> steps,
        CancellationToken cancellationToken = default);

    Task<LocalInstallmentOperation?> GetAsync(Guid operationGuid, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalInstallmentOperation>> GetRecoverableAsync(string storeCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalInstallmentRefundStep>> GetRefundStepsAsync(Guid operationGuid, CancellationToken cancellationToken = default);

    Task<LocalInstallmentRefundStep?> GetRefundStepAsync(
        Guid refundStepGuid,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<LocalInstallmentRefundStep?>(null);

    Task<IReadOnlyList<LocalInstallmentRefundStep>> GetRefundStepsForInstallmentAsync(Guid installmentGuid, CancellationToken cancellationToken = default);

    Task<bool> TryTransitionAsync(
        Guid operationGuid,
        IReadOnlyCollection<LocalInstallmentOperationState> expected,
        LocalInstallmentOperationState next,
        DateTimeOffset updatedAt,
        string? requestJson = null,
        string? terminalAttemptGuid = null,
        string? terminalProcessor = null,
        string? responseJson = null,
        string? failureMessage = null,
        CancellationToken cancellationToken = default);

    Task<bool> TryClaimApiAsync(
        Guid operationGuid,
        string apiClaimToken,
        bool allowStaleApiSubmittingClaim,
        DateTimeOffset claimedAt,
        TimeSpan staleAfter,
        CancellationToken cancellationToken = default);

    Task<bool> TryTransitionRefundStepAsync(
        Guid refundStepGuid,
        IReadOnlyCollection<LocalInstallmentRefundStepState> expected,
        LocalInstallmentRefundStepState next,
        DateTimeOffset updatedAt,
        string? refundReference = null,
        string? cardTransactionsJson = null,
        string? failureMessage = null,
        CancellationToken cancellationToken = default);

    Task<bool> TryRecordRefundEvidenceAsync(
        Guid refundStepGuid,
        IReadOnlyCollection<LocalInstallmentRefundStepState> expected,
        string refundReference,
        string providerEnvironment,
        string cardTransactionsJson,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryResetRefundStepAfterDeclineAsync(
        Guid refundStepGuid,
        IReadOnlyCollection<LocalInstallmentRefundStepState> expected,
        string nextIdempotencyKey,
        DateTimeOffset updatedAt,
        string? failureMessage = null,
        CancellationToken cancellationToken = default);

    Task<bool> ResolveRefundStepAsync(
        Guid refundStepGuid,
        InstallmentRefundSupervisorResolution resolution,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default);

    Task<bool> ResolveRefundStepWithJournalAsync(
        Guid refundStepGuid,
        InstallmentRefundSupervisorResolution resolution,
        LocalFinancialSupervisorResolution journal,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default) =>
        ResolveRefundStepAsync(refundStepGuid, resolution, resolvedAt, cancellationToken);

    Task<bool> CompleteWithSnapshotAsync(
        Guid operationGuid,
        IReadOnlyCollection<LocalInstallmentOperationState> expected,
        LocalInstallmentOrder snapshot,
        string? responseJson,
        bool completeRefundSteps,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 将分期金融状态和服务端快照一起落盘。所有抢占/状态切换都以受影响行数作为 CAS 判定。
/// </summary>
public sealed class LocalInstallmentOperationRepository(LocalSqliteStore store) : ILocalInstallmentOperationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LocalInstallmentOperation> CreateOrGetAsync(LocalInstallmentOperation operation, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await InsertOperationIgnoreAsync(connection, transaction, operation, cancellationToken);
        var result = await GetAsync(connection, transaction, operation.OperationGuid, cancellationToken)
            ?? throw new InvalidOperationException("分期操作持久化后无法读取。");
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<LocalInstallmentOperation> CreateCancelOrGetAsync(
        LocalInstallmentOperation operation,
        IReadOnlyList<LocalInstallmentRefundStep> steps,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var inserted = await InsertOperationIgnoreAsync(connection, transaction, operation, cancellationToken);
        if (inserted)
        {
            foreach (var step in steps)
            {
                await InsertRefundStepAsync(connection, transaction, step, cancellationToken);
            }
        }

        var result = await GetAsync(connection, transaction, operation.OperationGuid, cancellationToken)
            ?? throw new InvalidOperationException("取消分期操作持久化后无法读取。");
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<LocalInstallmentOperation?> GetAsync(Guid operationGuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        return await GetAsync(connection, null, operationGuid, cancellationToken);
    }

    public async Task<IReadOnlyList<LocalInstallmentOperation>> GetRecoverableAsync(string storeCode, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT *
            FROM LocalInstallmentOperations
            WHERE StoreCode = $StoreCode
              AND State IN ($Prepared, $TerminalSubmitting, $ResultUnknown, $TerminalApproved, $ApiSubmitting)
            ORDER BY UpdatedAt, CreatedAt;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$Prepared", LocalInstallmentOperationState.Prepared.ToString());
        command.Parameters.AddWithValue("$TerminalSubmitting", LocalInstallmentOperationState.TerminalSubmitting.ToString());
        command.Parameters.AddWithValue("$ResultUnknown", LocalInstallmentOperationState.ResultUnknown.ToString());
        command.Parameters.AddWithValue("$TerminalApproved", LocalInstallmentOperationState.TerminalApproved.ToString());
        command.Parameters.AddWithValue("$ApiSubmitting", LocalInstallmentOperationState.ApiSubmitting.ToString());
        var operations = new List<LocalInstallmentOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            operations.Add(ReadOperation(reader));
        }

        return operations;
    }

    public async Task<IReadOnlyList<LocalInstallmentRefundStep>> GetRefundStepsAsync(Guid operationGuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM LocalInstallmentRefundSteps WHERE OperationGuid = $OperationGuid ORDER BY CreatedAt, RefundStepGuid;";
        command.Parameters.AddWithValue("$OperationGuid", operationGuid.ToString());
        var steps = new List<LocalInstallmentRefundStep>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            steps.Add(ReadRefundStep(reader));
        }

        return steps;
    }

    public async Task<LocalInstallmentRefundStep?> GetRefundStepAsync(
        Guid refundStepGuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM LocalInstallmentRefundSteps WHERE RefundStepGuid = $RefundStepGuid;";
        command.Parameters.AddWithValue("$RefundStepGuid", refundStepGuid.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadRefundStep(reader)
            : null;
    }

    public async Task<IReadOnlyList<LocalInstallmentRefundStep>> GetRefundStepsForInstallmentAsync(Guid installmentGuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT steps.*
            FROM LocalInstallmentRefundSteps AS steps
            INNER JOIN LocalInstallmentOperations AS operations ON operations.OperationGuid = steps.OperationGuid
            WHERE operations.InstallmentGuid = $InstallmentGuid
              AND operations.Kind = $Cancel
              AND steps.State = $ResultUnknown
            ORDER BY steps.UpdatedAt, steps.CreatedAt, steps.RefundStepGuid;
            """;
        command.Parameters.AddWithValue("$InstallmentGuid", installmentGuid.ToString());
        command.Parameters.AddWithValue("$Cancel", LocalInstallmentOperationKind.Cancel.ToString());
        command.Parameters.AddWithValue("$ResultUnknown", LocalInstallmentRefundStepState.ResultUnknown.ToString());
        var steps = new List<LocalInstallmentRefundStep>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            steps.Add(ReadRefundStep(reader));
        }

        return steps;
    }

    public async Task<bool> TryTransitionAsync(
        Guid operationGuid,
        IReadOnlyCollection<LocalInstallmentOperationState> expected,
        LocalInstallmentOperationState next,
        DateTimeOffset updatedAt,
        string? requestJson = null,
        string? terminalAttemptGuid = null,
        string? terminalProcessor = null,
        string? responseJson = null,
        string? failureMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (expected.Count == 0)
        {
            throw new ArgumentException("状态转换必须指定期望状态。", nameof(expected));
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE LocalInstallmentOperations
            SET State = $Next,
                RequestJson = COALESCE($RequestJson, RequestJson),
                TerminalAttemptGuid = COALESCE($TerminalAttemptGuid, TerminalAttemptGuid),
                TerminalProcessor = COALESCE($TerminalProcessor, TerminalProcessor),
                ResponseJson = COALESCE($ResponseJson, ResponseJson),
                FailureMessage = $FailureMessage,
                ApiClaimToken = CASE WHEN $Next = $ApiSubmitting THEN ApiClaimToken ELSE NULL END,
                ApiClaimedAt = CASE WHEN $Next = $ApiSubmitting THEN ApiClaimedAt ELSE NULL END,
                UpdatedAt = $UpdatedAt
            WHERE OperationGuid = $OperationGuid
              AND State IN ({string.Join(", ", expected.Select((_, index) => "$Expected" + index))});
            """;
        command.Parameters.AddWithValue("$Next", next.ToString());
        command.Parameters.AddWithValue("$RequestJson", (object?)requestJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$TerminalAttemptGuid", (object?)terminalAttemptGuid ?? DBNull.Value);
        command.Parameters.AddWithValue("$TerminalProcessor", (object?)terminalProcessor ?? DBNull.Value);
        command.Parameters.AddWithValue("$ResponseJson", (object?)responseJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$FailureMessage", (object?)failureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$ApiSubmitting", LocalInstallmentOperationState.ApiSubmitting.ToString());
        command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$OperationGuid", operationGuid.ToString());
        var index = 0;
        foreach (var state in expected)
        {
            command.Parameters.AddWithValue("$Expected" + index++, state.ToString());
        }

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<bool> TryClaimApiAsync(
        Guid operationGuid,
        string apiClaimToken,
        bool allowStaleApiSubmittingClaim,
        DateTimeOffset claimedAt,
        TimeSpan staleAfter,
        CancellationToken cancellationToken = default)
    {
        var staleBefore = claimedAt - staleAfter;
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE LocalInstallmentOperations
            SET State = $ApiSubmitting,
                ApiClaimToken = $ApiClaimToken,
                ApiClaimedAt = $ClaimedAt,
                FailureMessage = NULL,
                UpdatedAt = $ClaimedAt
            WHERE OperationGuid = $OperationGuid
              AND (
                  State IN ($TerminalApproved, $ResultUnknown)
                  OR (
                      $AllowStaleApiSubmittingClaim = 1
                      AND State = $ApiSubmitting
                      AND (
                          ApiClaimToken IS NULL
                          OR ApiClaimToken <> $ApiClaimToken
                          OR ApiClaimedAt IS NULL
                          OR ApiClaimedAt < $StaleBefore
                      )
                  )
              );
            """;
        command.Parameters.AddWithValue("$ApiSubmitting", LocalInstallmentOperationState.ApiSubmitting.ToString());
        command.Parameters.AddWithValue("$ApiClaimToken", apiClaimToken);
        command.Parameters.AddWithValue("$ClaimedAt", claimedAt.ToString("O"));
        command.Parameters.AddWithValue("$OperationGuid", operationGuid.ToString());
        command.Parameters.AddWithValue("$TerminalApproved", LocalInstallmentOperationState.TerminalApproved.ToString());
        command.Parameters.AddWithValue("$ResultUnknown", LocalInstallmentOperationState.ResultUnknown.ToString());
        command.Parameters.AddWithValue("$AllowStaleApiSubmittingClaim", allowStaleApiSubmittingClaim ? 1 : 0);
        command.Parameters.AddWithValue("$StaleBefore", staleBefore.ToString("O"));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<bool> TryTransitionRefundStepAsync(
        Guid refundStepGuid,
        IReadOnlyCollection<LocalInstallmentRefundStepState> expected,
        LocalInstallmentRefundStepState next,
        DateTimeOffset updatedAt,
        string? refundReference = null,
        string? cardTransactionsJson = null,
        string? failureMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (expected.Count == 0)
        {
            throw new ArgumentException("退款步骤转换必须指定期望状态。", nameof(expected));
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE LocalInstallmentRefundSteps
            SET State = $Next,
                RefundReference = COALESCE($RefundReference, RefundReference),
                CardTransactionsJson = COALESCE($CardTransactionsJson, CardTransactionsJson),
                FailureMessage = $FailureMessage,
                UpdatedAt = $UpdatedAt
            WHERE RefundStepGuid = $RefundStepGuid
              AND State IN ({string.Join(", ", expected.Select((_, index) => "$Expected" + index))});
            """;
        command.Parameters.AddWithValue("$Next", next.ToString());
        command.Parameters.AddWithValue("$RefundReference", (object?)refundReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$CardTransactionsJson", (object?)cardTransactionsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$FailureMessage", (object?)failureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$RefundStepGuid", refundStepGuid.ToString());
        var index = 0;
        foreach (var state in expected)
        {
            command.Parameters.AddWithValue("$Expected" + index++, state.ToString());
        }

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<bool> TryRecordRefundEvidenceAsync(
        Guid refundStepGuid,
        IReadOnlyCollection<LocalInstallmentRefundStepState> expected,
        string refundReference,
        string providerEnvironment,
        string cardTransactionsJson,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (expected.Count == 0)
        {
            throw new ArgumentException("退款证据写入必须指定期望状态。", nameof(expected));
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE LocalInstallmentRefundSteps
            SET RefundReference = COALESCE(RefundReference, $RefundReference),
                ProviderEnvironment = COALESCE(ProviderEnvironment, $ProviderEnvironment),
                CardTransactionsJson = $CardTransactionsJson,
                UpdatedAt = $UpdatedAt
            WHERE RefundStepGuid = $RefundStepGuid
              AND State IN ({string.Join(", ", expected.Select((_, index) => "$Expected" + index))})
              AND (RefundReference IS NULL OR RefundReference = $RefundReference)
              AND (ProviderEnvironment IS NULL OR ProviderEnvironment = $ProviderEnvironment);
            """;
        command.Parameters.AddWithValue("$RefundReference", refundReference);
        command.Parameters.AddWithValue("$ProviderEnvironment", providerEnvironment);
        command.Parameters.AddWithValue("$CardTransactionsJson", cardTransactionsJson);
        command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$RefundStepGuid", refundStepGuid.ToString());
        var index = 0;
        foreach (var state in expected)
        {
            command.Parameters.AddWithValue("$Expected" + index++, state.ToString());
        }

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<bool> TryResetRefundStepAfterDeclineAsync(
        Guid refundStepGuid,
        IReadOnlyCollection<LocalInstallmentRefundStepState> expected,
        string nextIdempotencyKey,
        DateTimeOffset updatedAt,
        string? failureMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (expected.Count == 0)
        {
            throw new ArgumentException("退款拒绝重置必须指定期望状态。", nameof(expected));
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE LocalInstallmentRefundSteps
            SET State = $Prepared,
                IdempotencyKey = $NextIdempotencyKey,
                RefundReference = NULL,
                ProviderEnvironment = NULL,
                CardTransactionsJson = NULL,
                FailureMessage = $FailureMessage,
                UpdatedAt = $UpdatedAt
            WHERE RefundStepGuid = $RefundStepGuid
              AND State IN ({string.Join(", ", expected.Select((_, index) => "$Expected" + index))});
            """;
        command.Parameters.AddWithValue("$Prepared", LocalInstallmentRefundStepState.Prepared.ToString());
        command.Parameters.AddWithValue("$NextIdempotencyKey", nextIdempotencyKey);
        command.Parameters.AddWithValue("$FailureMessage", (object?)failureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$RefundStepGuid", refundStepGuid.ToString());
        var index = 0;
        foreach (var state in expected)
        {
            command.Parameters.AddWithValue("$Expected" + index++, state.ToString());
        }

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected == 1;
    }

    public Task<bool> ResolveRefundStepAsync(
        Guid refundStepGuid,
        InstallmentRefundSupervisorResolution resolution,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default) =>
        ResolveRefundStepCoreAsync(refundStepGuid, resolution, journal: null, resolvedAt, cancellationToken);

    public Task<bool> ResolveRefundStepWithJournalAsync(
        Guid refundStepGuid,
        InstallmentRefundSupervisorResolution resolution,
        LocalFinancialSupervisorResolution journal,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Target != LocalFinancialSupervisorResolutionTarget.InstallmentRefund ||
            journal.RefundStepGuid != refundStepGuid ||
            journal.OperationGuid is null)
        {
            throw new ArgumentException("主管结案 journal 与分期退款步骤不匹配。", nameof(journal));
        }

        return ResolveRefundStepCoreAsync(refundStepGuid, resolution, journal, resolvedAt, cancellationToken);
    }

    private async Task<bool> ResolveRefundStepCoreAsync(
        Guid refundStepGuid,
        InstallmentRefundSupervisorResolution resolution,
        LocalFinancialSupervisorResolution? journal,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resolution.OperatorId) || string.IsNullOrWhiteSpace(resolution.Reason))
        {
            throw new ArgumentException("主管结案必须记录操作人和原因。", nameof(resolution));
        }

        if (resolution.Decision == InstallmentRefundSupervisorDecision.ConfirmRefunded &&
            string.IsNullOrWhiteSpace(resolution.RefundReference) && string.IsNullOrWhiteSpace(resolution.Reason))
        {
            throw new ArgumentException("确认已退款必须填写退款引用或备注。", nameof(resolution));
        }

        if (resolution.Decision == InstallmentRefundSupervisorDecision.ConfirmNotRefunded && string.IsNullOrWhiteSpace(resolution.Evidence))
        {
            throw new ArgumentException("确认未退款必须记录银行证据。", nameof(resolution));
        }

        var next = resolution.Decision switch
        {
            InstallmentRefundSupervisorDecision.ConfirmRefunded => LocalInstallmentRefundStepState.SupervisorConfirmedRefunded,
            InstallmentRefundSupervisorDecision.ConfirmNotRefunded => LocalInstallmentRefundStepState.Prepared,
            _ => LocalInstallmentRefundStepState.ResultUnknown
        };

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE LocalInstallmentRefundSteps
            SET State = $State,
                -- 中文注释：主管确认银行明确未退款后，下一次 provider 重试必须使用全新幂等身份。
                IdempotencyKey = CASE
                    WHEN $SupervisorDecision = $ConfirmNotRefunded THEN $RetryIdempotencyKey
                    ELSE IdempotencyKey
                END,
                RefundReference = CASE
                    WHEN $SupervisorDecision = $ConfirmNotRefunded THEN NULL
                    ELSE COALESCE($RefundReference, RefundReference)
                END,
                ProviderEnvironment = CASE
                    WHEN $SupervisorDecision = $ConfirmNotRefunded THEN NULL
                    ELSE ProviderEnvironment
                END,
                CardTransactionsJson = CASE
                    WHEN $SupervisorDecision = $ConfirmNotRefunded
                         AND (OriginalReference LIKE 'SQ:%' OR ProviderEnvironment IS NOT NULL) THEN NULL
                    ELSE CardTransactionsJson
                END,
                SupervisorDecision = $SupervisorDecision,
                SupervisorUserId = $SupervisorUserId,
                SupervisorReason = $SupervisorReason,
                SupervisorEvidence = $SupervisorEvidence,
                ResolvedAt = $ResolvedAt,
                UpdatedAt = $ResolvedAt
            WHERE RefundStepGuid = $RefundStepGuid
              AND State = $ResultUnknown
              AND ($JournalOperationGuid IS NULL OR OperationGuid = $JournalOperationGuid)
              AND EXISTS (
                  SELECT 1
                  FROM LocalInstallmentOperations AS operations
                  WHERE operations.OperationGuid = LocalInstallmentRefundSteps.OperationGuid
                    AND operations.State = $OperationResultUnknown
              );
            """;
        command.Parameters.AddWithValue("$State", next.ToString());
        command.Parameters.AddWithValue("$RetryIdempotencyKey", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$RefundReference", (object?)resolution.RefundReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$SupervisorDecision", resolution.Decision.ToString());
        command.Parameters.AddWithValue("$ConfirmNotRefunded", InstallmentRefundSupervisorDecision.ConfirmNotRefunded.ToString());
        command.Parameters.AddWithValue("$SupervisorUserId", resolution.OperatorId.Trim());
        command.Parameters.AddWithValue("$SupervisorReason", resolution.Reason.Trim());
        command.Parameters.AddWithValue("$SupervisorEvidence", (object?)resolution.Evidence?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("$ResolvedAt", resolvedAt.ToString("O"));
        command.Parameters.AddWithValue("$RefundStepGuid", refundStepGuid.ToString());
        command.Parameters.AddWithValue("$JournalOperationGuid", journal?.OperationGuid is { } journalOperationGuid
            ? journalOperationGuid.ToString()
            : DBNull.Value);
        command.Parameters.AddWithValue("$ResultUnknown", LocalInstallmentRefundStepState.ResultUnknown.ToString());
        command.Parameters.AddWithValue("$OperationResultUnknown", LocalInstallmentOperationState.ResultUnknown.ToString());
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (journal is not null)
        {
            await LocalFinancialSupervisorResolutionRepository.InsertAsync(
                connection,
                transaction,
                journal,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CompleteWithSnapshotAsync(
        Guid operationGuid,
        IReadOnlyCollection<LocalInstallmentOperationState> expected,
        LocalInstallmentOrder snapshot,
        string? responseJson,
        bool completeRefundSteps,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        if (expected.Count == 0)
        {
            throw new ArgumentException("完成操作必须指定期望状态。", nameof(expected));
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE LocalInstallmentOperations
            SET State = $Completed,
                ResponseJson = $ResponseJson,
                FailureMessage = NULL,
                UpdatedAt = $CompletedAt
            WHERE OperationGuid = $OperationGuid
              AND State IN ({string.Join(", ", expected.Select((_, index) => "$Expected" + index))});
            """;
        command.Parameters.AddWithValue("$Completed", LocalInstallmentOperationState.Completed.ToString());
        command.Parameters.AddWithValue("$ResponseJson", (object?)responseJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$CompletedAt", completedAt.ToString("O"));
        command.Parameters.AddWithValue("$OperationGuid", operationGuid.ToString());
        var index = 0;
        foreach (var state in expected)
        {
            command.Parameters.AddWithValue("$Expected" + index++, state.ToString());
        }

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await UpsertSnapshotAsync(connection, transaction, snapshot, cancellationToken);
        if (completeRefundSteps)
        {
            await using var steps = connection.CreateCommand();
            steps.Transaction = transaction;
            steps.CommandText = """
                UPDATE LocalInstallmentRefundSteps
                SET State = $Completed,
                    UpdatedAt = $CompletedAt
                WHERE OperationGuid = $OperationGuid
                  AND State IN ($Approved, $SupervisorConfirmedRefunded);
                """;
            steps.Parameters.AddWithValue("$Completed", LocalInstallmentRefundStepState.Completed.ToString());
            steps.Parameters.AddWithValue("$CompletedAt", completedAt.ToString("O"));
            steps.Parameters.AddWithValue("$OperationGuid", operationGuid.ToString());
            steps.Parameters.AddWithValue("$Approved", LocalInstallmentRefundStepState.Approved.ToString());
            steps.Parameters.AddWithValue("$SupervisorConfirmedRefunded", LocalInstallmentRefundStepState.SupervisorConfirmedRefunded.ToString());
            await steps.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return affected == 1;
    }

    private static async Task<bool> InsertOperationIgnoreAsync(SqliteConnection connection, SqliteTransaction transaction, LocalInstallmentOperation operation, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO LocalInstallmentOperations
            (OperationGuid, Kind, InstallmentGuid, PaymentGuid, StoreCode, DeviceCode, CashierId, IdempotencyKey,
             RequestJson, State, TerminalAttemptGuid, TerminalProcessor, ResponseJson, FailureMessage, CreatedAt, UpdatedAt)
            VALUES
            ($OperationGuid, $Kind, $InstallmentGuid, $PaymentGuid, $StoreCode, $DeviceCode, $CashierId, $IdempotencyKey,
             $RequestJson, $State, $TerminalAttemptGuid, $TerminalProcessor, $ResponseJson, $FailureMessage, $CreatedAt, $UpdatedAt);
            """;
        AddOperationParameters(command, operation);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task InsertRefundStepAsync(SqliteConnection connection, SqliteTransaction transaction, LocalInstallmentRefundStep step, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO LocalInstallmentRefundSteps
            (RefundStepGuid, OperationGuid, OriginalPaymentGuid, Method, Amount, OriginalReference, IdempotencyKey, State,
             RefundReference, ProviderEnvironment, CardTransactionsJson, FailureMessage, SupervisorDecision, SupervisorUserId, SupervisorReason,
             SupervisorEvidence, ResolvedAt, CreatedAt, UpdatedAt, OriginalMethod)
            VALUES
            ($RefundStepGuid, $OperationGuid, $OriginalPaymentGuid, $Method, $Amount, $OriginalReference, $IdempotencyKey, $State,
             $RefundReference, $ProviderEnvironment, $CardTransactionsJson, $FailureMessage, $SupervisorDecision, $SupervisorUserId, $SupervisorReason,
             $SupervisorEvidence, $ResolvedAt, $CreatedAt, $UpdatedAt, $OriginalMethod);
            """;
        AddRefundStepParameters(command, step);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<LocalInstallmentOperation?> GetAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid operationGuid, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM LocalInstallmentOperations WHERE OperationGuid = $OperationGuid;";
        command.Parameters.AddWithValue("$OperationGuid", operationGuid.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadOperation(reader) : null;
    }

    private static async Task UpsertSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction, LocalInstallmentOrder order, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO LocalOrderInstallments
            (InstallmentGuid, OrderGuid, InstallmentNumber, StoreCode, DeviceCode, CashierId, CashierName, CustomerName, CustomerPhone,
             CreatedAt, UpdatedAt, TotalAmount, MinimumDownPayment, DownPaymentAmount, PaidAmount, BalanceAmount, Status, LinesJson,
             PaymentsJson, PickupInfoJson, CancellationInfoJson, Note)
            VALUES
            ($InstallmentGuid, $OrderGuid, $InstallmentNumber, $StoreCode, $DeviceCode, $CashierId, $CashierName, $CustomerName, $CustomerPhone,
             $CreatedAt, $UpdatedAt, $TotalAmount, $MinimumDownPayment, $DownPaymentAmount, $PaidAmount, $BalanceAmount, $Status, $LinesJson,
             $PaymentsJson, $PickupInfoJson, $CancellationInfoJson, $Note)
            ON CONFLICT(InstallmentGuid) DO UPDATE SET
                OrderGuid = excluded.OrderGuid, InstallmentNumber = excluded.InstallmentNumber, StoreCode = excluded.StoreCode,
                DeviceCode = excluded.DeviceCode, CashierId = excluded.CashierId, CashierName = excluded.CashierName,
                CustomerName = excluded.CustomerName, CustomerPhone = excluded.CustomerPhone, CreatedAt = excluded.CreatedAt,
                UpdatedAt = excluded.UpdatedAt, TotalAmount = excluded.TotalAmount, MinimumDownPayment = excluded.MinimumDownPayment,
                DownPaymentAmount = excluded.DownPaymentAmount, PaidAmount = excluded.PaidAmount, BalanceAmount = excluded.BalanceAmount,
                Status = excluded.Status, LinesJson = excluded.LinesJson, PaymentsJson = excluded.PaymentsJson,
                PickupInfoJson = excluded.PickupInfoJson, CancellationInfoJson = excluded.CancellationInfoJson, Note = excluded.Note;
            """;
        command.Parameters.AddWithValue("$InstallmentGuid", order.InstallmentGuid.ToString());
        command.Parameters.AddWithValue("$OrderGuid", order.OrderGuid.ToString());
        command.Parameters.AddWithValue("$InstallmentNumber", order.InstallmentNumber);
        command.Parameters.AddWithValue("$StoreCode", order.StoreCode);
        command.Parameters.AddWithValue("$DeviceCode", order.DeviceCode);
        command.Parameters.AddWithValue("$CashierId", order.CashierId);
        command.Parameters.AddWithValue("$CashierName", order.CashierName);
        command.Parameters.AddWithValue("$CustomerName", order.CustomerName);
        command.Parameters.AddWithValue("$CustomerPhone", order.CustomerPhone);
        command.Parameters.AddWithValue("$CreatedAt", order.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$UpdatedAt", order.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$TotalAmount", order.TotalAmount);
        command.Parameters.AddWithValue("$MinimumDownPayment", order.MinimumDownPayment);
        command.Parameters.AddWithValue("$DownPaymentAmount", order.DownPaymentAmount);
        command.Parameters.AddWithValue("$PaidAmount", order.PaidAmount);
        command.Parameters.AddWithValue("$BalanceAmount", order.BalanceAmount);
        command.Parameters.AddWithValue("$Status", (int)order.Status);
        command.Parameters.AddWithValue("$LinesJson", JsonSerializer.Serialize(order.Lines, JsonOptions));
        command.Parameters.AddWithValue("$PaymentsJson", JsonSerializer.Serialize(order.Payments, JsonOptions));
        command.Parameters.AddWithValue("$PickupInfoJson", order.PickupInfo is null ? DBNull.Value : JsonSerializer.Serialize(order.PickupInfo, JsonOptions));
        command.Parameters.AddWithValue("$CancellationInfoJson", order.CancellationInfo is null ? DBNull.Value : JsonSerializer.Serialize(order.CancellationInfo, JsonOptions));
        command.Parameters.AddWithValue("$Note", (object?)order.Note ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddOperationParameters(SqliteCommand command, LocalInstallmentOperation operation)
    {
        command.Parameters.AddWithValue("$OperationGuid", operation.OperationGuid.ToString());
        command.Parameters.AddWithValue("$Kind", operation.Kind.ToString());
        command.Parameters.AddWithValue("$InstallmentGuid", operation.InstallmentGuid.ToString());
        command.Parameters.AddWithValue("$PaymentGuid", operation.PaymentGuid?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$StoreCode", operation.StoreCode);
        command.Parameters.AddWithValue("$DeviceCode", operation.DeviceCode);
        command.Parameters.AddWithValue("$CashierId", operation.CashierId);
        command.Parameters.AddWithValue("$IdempotencyKey", operation.IdempotencyKey);
        command.Parameters.AddWithValue("$RequestJson", operation.RequestJson);
        command.Parameters.AddWithValue("$State", operation.State.ToString());
        command.Parameters.AddWithValue("$TerminalAttemptGuid", (object?)operation.TerminalAttemptGuid ?? DBNull.Value);
        command.Parameters.AddWithValue("$TerminalProcessor", (object?)operation.TerminalProcessor ?? DBNull.Value);
        command.Parameters.AddWithValue("$ResponseJson", (object?)operation.ResponseJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$FailureMessage", (object?)operation.FailureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$CreatedAt", operation.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$UpdatedAt", operation.UpdatedAt.ToString("O"));
    }

    private static void AddRefundStepParameters(SqliteCommand command, LocalInstallmentRefundStep step)
    {
        command.Parameters.AddWithValue("$RefundStepGuid", step.RefundStepGuid.ToString());
        command.Parameters.AddWithValue("$OperationGuid", step.OperationGuid.ToString());
        command.Parameters.AddWithValue("$OriginalPaymentGuid", step.OriginalPaymentGuid.ToString());
        command.Parameters.AddWithValue("$Method", (int)step.Method);
        command.Parameters.AddWithValue("$Amount", step.Amount);
        command.Parameters.AddWithValue("$OriginalReference", (object?)step.OriginalReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$IdempotencyKey", step.IdempotencyKey);
        command.Parameters.AddWithValue("$State", step.State.ToString());
        command.Parameters.AddWithValue("$RefundReference", (object?)step.RefundReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$ProviderEnvironment", (object?)step.ProviderEnvironment ?? DBNull.Value);
        command.Parameters.AddWithValue("$CardTransactionsJson", (object?)step.CardTransactionsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$FailureMessage", (object?)step.FailureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$SupervisorDecision", step.SupervisorDecision?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$SupervisorUserId", (object?)step.SupervisorUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$SupervisorReason", (object?)step.SupervisorReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$SupervisorEvidence", (object?)step.SupervisorEvidence ?? DBNull.Value);
        command.Parameters.AddWithValue("$ResolvedAt", step.ResolvedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$CreatedAt", step.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$UpdatedAt", step.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$OriginalMethod", step.OriginalMethod is { } originalMethod ? (int)originalMethod : DBNull.Value);
    }

    private static LocalInstallmentOperation ReadOperation(SqliteDataReader reader) => new(
        ReadGuid(reader, "OperationGuid"),
        Enum.Parse<LocalInstallmentOperationKind>(ReadString(reader, "Kind")),
        ReadGuid(reader, "InstallmentGuid"),
        ReadNullableGuid(reader, "PaymentGuid"),
        ReadString(reader, "StoreCode"),
        ReadString(reader, "DeviceCode"),
        ReadString(reader, "CashierId"),
        ReadString(reader, "IdempotencyKey"),
        ReadString(reader, "RequestJson"),
        Enum.Parse<LocalInstallmentOperationState>(ReadString(reader, "State")),
        ReadNullableString(reader, "TerminalAttemptGuid"),
        ReadNullableString(reader, "TerminalProcessor"),
        ReadNullableString(reader, "ResponseJson"),
        ReadNullableString(reader, "FailureMessage"),
        ReadDateTimeOffset(reader, "CreatedAt"),
        ReadDateTimeOffset(reader, "UpdatedAt"));

    private static LocalInstallmentRefundStep ReadRefundStep(SqliteDataReader reader) => new(
        ReadGuid(reader, "RefundStepGuid"),
        ReadGuid(reader, "OperationGuid"),
        ReadGuid(reader, "OriginalPaymentGuid"),
        (PaymentMethodKind)ReadInt32(reader, "Method"),
        ReadDecimal(reader, "Amount"),
        ReadNullableString(reader, "OriginalReference"),
        ReadString(reader, "IdempotencyKey"),
        Enum.Parse<LocalInstallmentRefundStepState>(ReadString(reader, "State")),
        ReadNullableString(reader, "RefundReference"),
        ReadNullableString(reader, "CardTransactionsJson"),
        ReadNullableString(reader, "FailureMessage"),
        ReadNullableEnum<InstallmentRefundSupervisorDecision>(reader, "SupervisorDecision"),
        ReadNullableString(reader, "SupervisorUserId"),
        ReadNullableString(reader, "SupervisorReason"),
        ReadNullableString(reader, "SupervisorEvidence"),
        ReadNullableDateTimeOffset(reader, "ResolvedAt"),
        ReadDateTimeOffset(reader, "CreatedAt"),
        ReadDateTimeOffset(reader, "UpdatedAt"),
        ReadNullableString(reader, "ProviderEnvironment"),
        ReadNullableInt32(reader, "OriginalMethod") is { } originalMethod ? (PaymentMethodKind)originalMethod : null);

    private static Guid ReadGuid(SqliteDataReader reader, string name) => Guid.Parse(ReadString(reader, name));
    private static Guid? ReadNullableGuid(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value) ? null : Guid.Parse(value);
    }
    private static string ReadString(SqliteDataReader reader, string name) => reader.GetString(reader.GetOrdinal(name));
    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
    private static int ReadInt32(SqliteDataReader reader, string name) => Convert.ToInt32(reader.GetValue(reader.GetOrdinal(name)), CultureInfo.InvariantCulture);
    private static int? ReadNullableInt32(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }
    private static decimal ReadDecimal(SqliteDataReader reader, string name) => Convert.ToDecimal(reader.GetValue(reader.GetOrdinal(name)), CultureInfo.InvariantCulture);
    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, string name) => DateTimeOffset.Parse(ReadString(reader, name), CultureInfo.InvariantCulture);
    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value) ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }
    private static TEnum? ReadNullableEnum<TEnum>(SqliteDataReader reader, string name) where TEnum : struct, Enum
    {
        var value = ReadNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value) ? null : Enum.Parse<TEnum>(value);
    }
}
