using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public enum LocalSquarePaymentAttemptStatus
{
    Pending,
    CheckoutCreated,
    Recovering,
    CheckoutCompleted,
    PaymentVerified,
    Canceled,
    TimedOut,
    Failed,
    Unknown,
    OrderCompleted,
    Abandoned
}

public sealed record LocalSquarePaymentAttempt(
    Guid AttemptGuid,
    string? CheckoutId,
    string IdempotencyKey,
    string DeviceId,
    string LocationId,
    string Environment,
    decimal Amount,
    long AmountCents,
    string Currency,
    LocalSquarePaymentAttemptStatus Status,
    string? CheckoutStatus,
    string? CancelReason,
    string OrderDraftJson,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string? PaymentId,
    string? PaymentStatus,
    string? ResponseCode,
    string? ResponseText,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? OrderCompletedAt,
    DateTimeOffset? ResolvedAt,
    string OperationKind = "Sale",
    Guid? OperationGuid = null,
    string? SubmissionToken = null,
    string? RefundBusinessKey = null,
    string RecoveryPhase = CardRecoveryPhases.None,
    LocalSquarePaymentAttemptStatus? RecoveryTargetStatus = null,
    string? SupervisorFinancialReference = null);

public sealed record SquarePaymentAttemptContext(
    Guid AttemptGuid,
    string IdempotencyKey,
    Func<string, string?, DateTimeOffset, CancellationToken, Task>? BindCheckoutAsync = null,
    string? SubmissionToken = null,
    Func<string, string, DateTimeOffset, CancellationToken, Task>? BindRefundAsync = null,
    Func<string, string, DateTimeOffset, CardTerminalEnvironment, CancellationToken, Task>? BindRefundEvidenceAsync = null)
{
    public bool CanBindCheckout => BindCheckoutAsync is not null;

    public bool CanBindRefund => BindRefundEvidenceAsync is not null || BindRefundAsync is not null;
}

public interface ISquarePaymentAttemptContextAccessor
{
    SquarePaymentAttemptContext? Current { get; }

    IDisposable Begin(SquarePaymentAttemptContext context);
}

public sealed class SquarePaymentAttemptContextAccessor : ISquarePaymentAttemptContextAccessor
{
    private readonly AsyncLocal<SquarePaymentAttemptContext?> _current = new();

    public SquarePaymentAttemptContext? Current => _current.Value;

    public IDisposable Begin(SquarePaymentAttemptContext context)
    {
        var previous = _current.Value;
        _current.Value = context;
        return new Scope(this, previous);
    }

    private sealed class Scope(
        SquarePaymentAttemptContextAccessor owner,
        SquarePaymentAttemptContext? previous) : IDisposable
    {
        public void Dispose()
        {
            owner._current.Value = previous;
        }
    }
}

public sealed record SquarePaymentResolution(
    Guid AttemptGuid,
    CardRecoverySupervisorDecision Decision,
    string Reason,
    string? Evidence,
    string? PaymentReference,
    LocalSquarePaymentAttemptStatus ExpectedStatus,
    DateTimeOffset ExpectedUpdatedAt,
    DateTimeOffset ResolvedAt);

public interface ILocalSquarePaymentAttemptRepository
{
    Task CreateAsync(LocalSquarePaymentAttempt attempt, CancellationToken cancellationToken = default);

    async Task<LocalSquarePaymentAttempt> CreateOrGetOpenRefundAsync(
        LocalSquarePaymentAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        await CreateAsync(attempt, cancellationToken);
        return attempt;
    }

    async Task<bool> TryBeginRefundSubmissionAsync(
        Guid attemptGuid,
        DateTimeOffset expectedUpdatedAt,
        string submissionToken,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await MarkRecoveringAsync(attemptGuid, updatedAt, cancellationToken);
        return true;
    }

    async Task<bool> TryMarkRefundCheckoutCreatedAsync(
        Guid attemptGuid,
        string submissionToken,
        string checkoutId,
        string? checkoutStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await MarkCheckoutCreatedAsync(attemptGuid, checkoutId, checkoutStatus, updatedAt, cancellationToken);
        return true;
    }

    Task<bool> TryRecordRefundResponseAsync(
        Guid attemptGuid,
        string submissionToken,
        string refundId,
        string refundStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryRecordRefundResponseAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        string submissionToken,
        string refundId,
        string refundStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        TryRecordRefundResponseAsync(
            attemptGuid,
            submissionToken,
            refundId,
            refundStatus,
            updatedAt,
            cancellationToken);

    async Task<bool> TryMarkRefundPaymentVerifiedAsync(
        Guid attemptGuid,
        string submissionToken,
        string paymentId,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        await MarkPaymentVerifiedAsync(
            attemptGuid,
            paymentId,
            paymentStatus,
            responseCode,
            responseText,
            completedAt,
            cancellationToken);
        return true;
    }

    Task<bool> TryMarkRefundPaymentVerifiedAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        string submissionToken,
        string paymentId,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default) =>
        TryMarkRefundPaymentVerifiedAsync(
            attemptGuid,
            submissionToken,
            paymentId,
            paymentStatus,
            responseCode,
            responseText,
            completedAt,
            cancellationToken);

    Task<bool> TryPersistRefundFailureForFinalizationAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        string submissionToken,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    async Task<bool> TryMarkRefundFailedAsync(
        Guid attemptGuid,
        string submissionToken,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default,
        string? cancelReason = null)
    {
        await MarkFailedAsync(
            attemptGuid,
            status,
            checkoutStatus,
            paymentStatus,
            responseCode,
            responseText,
            resolvedAt,
            cancellationToken,
            cancelReason);
        return true;
    }

    // 未实现版本化 CAS 的仓储必须保守失败，不能退回 token-only 写入覆盖并发金融结果。
    Task<bool> TryMarkRefundFailedAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        string submissionToken,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default,
        string? cancelReason = null) =>
        Task.FromResult(false);

    Task MarkCheckoutCreatedAsync(
        Guid attemptGuid,
        string checkoutId,
        string? checkoutStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    async Task<bool> TryMarkCheckoutCreatedAsync(
        Guid attemptGuid,
        string submissionToken,
        string checkoutId,
        string? checkoutStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await MarkCheckoutCreatedAsync(attemptGuid, checkoutId, checkoutStatus, updatedAt, cancellationToken);
        return true;
    }

    Task MarkRecoveringAsync(Guid attemptGuid, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);

    async Task<bool> TryMarkRecoveringAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await MarkRecoveringAsync(attemptGuid, updatedAt, cancellationToken);
        return true;
    }

    Task UpdateCheckoutStatusAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? cancelReason,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    async Task<bool> TryUpdateCheckoutStatusAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? cancelReason,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await UpdateCheckoutStatusAsync(
            attemptGuid,
            status,
            checkoutStatus,
            cancelReason,
            updatedAt,
            cancellationToken);
        return true;
    }

    Task MarkPaymentVerifiedAsync(
        Guid attemptGuid,
        string paymentId,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    async Task<bool> TryMarkPaymentVerifiedAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        string paymentId,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        await MarkPaymentVerifiedAsync(
            attemptGuid,
            paymentId,
            paymentStatus,
            responseCode,
            responseText,
            completedAt,
            cancellationToken);
        return true;
    }

    Task<bool> TryPersistPaymentVerifiedRecoveryAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        string paymentId,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default) =>
        TryMarkPaymentVerifiedAsync(
            attemptGuid,
            expectedStatus,
            expectedUpdatedAt,
            paymentId,
            paymentStatus,
            responseCode,
            responseText,
            completedAt,
            cancellationToken);

    Task MarkFailedAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default,
        string? cancelReason = null);

    async Task<bool> TryMarkFailedAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default,
        string? cancelReason = null)
    {
        await MarkFailedAsync(
            attemptGuid,
            status,
            checkoutStatus,
            paymentStatus,
            responseCode,
            responseText,
            resolvedAt,
            cancellationToken,
            cancelReason);
        return true;
    }

    Task MarkOrderCompletedAsync(Guid attemptGuid, DateTimeOffset completedAt, CancellationToken cancellationToken = default);

    async Task<bool> TryMarkOrderCompletedAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        await MarkOrderCompletedAsync(attemptGuid, completedAt, cancellationToken);
        return true;
    }

    Task<bool> TryBeginRecoveryFinalizationAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        LocalSquarePaymentAttemptStatus targetStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    async Task<bool> TryCompleteRecoveryFinalizationAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        LocalSquarePaymentAttemptStatus targetStatus,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        if (targetStatus == LocalSquarePaymentAttemptStatus.OrderCompleted)
        {
            await MarkOrderCompletedAsync(attemptGuid, completedAt, cancellationToken);
        }
        else
        {
            await UpdateCheckoutStatusAsync(
                attemptGuid,
                targetStatus,
                checkoutStatus: null,
                cancelReason: null,
                completedAt,
                cancellationToken);
        }

        return true;
    }

    Task<LocalSquarePaymentAttempt?> GetLatestOpenAttemptAsync(
        string storeCode,
        string deviceCode,
        string? cashierId,
        string environment,
        CancellationToken cancellationToken = default);

    Task<LocalSquarePaymentAttempt?> GetLatestOpenSaleAttemptForTerminalAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken = default) =>
        GetLatestOpenAttemptAsync(
            storeCode,
            deviceCode,
            cashierId: null,
            environment,
            cancellationToken);

    Task<IReadOnlyList<LocalSquarePaymentAttempt>> GetOpenRefundAttemptsAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalSquarePaymentAttempt>> GetRecentAttemptsAsync(
        string storeCode, string deviceCode, string environment,
        CancellationToken cancellationToken = default) =>
        GetOpenAttemptsAsync(storeCode, deviceCode, environment, cancellationToken);

    Task<IReadOnlyList<LocalSquarePaymentAttempt>> GetOpenAttemptsAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Open attempt queue is not wired for this repository.");

    Task<bool> ResolveRefundAsync(
        CardRefundAttemptResolution resolution,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<bool> ResolveRefundWithJournalAsync(
        CardRefundAttemptResolution resolution,
        LocalFinancialSupervisorResolution journal,
        CancellationToken cancellationToken = default) =>
        ResolveRefundAsync(resolution, cancellationToken);

    Task<bool> ResolveRefundWithJournalAsync(
        CardRefundAttemptResolution resolution,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        LocalFinancialSupervisorResolution journal,
        CancellationToken cancellationToken = default) =>
        ResolveRefundWithJournalAsync(resolution, journal, cancellationToken);

    Task<bool> ResolvePaymentWithJournalAsync(
        SquarePaymentResolution resolution,
        LocalFinancialSupervisorResolution journal,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Square payment resolution is not wired for this repository.");

    Task<bool> TryTerminalizeNotPaidAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Square not-paid terminalization is not wired for this repository.");

    Task<LocalSquarePaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default);

    /// <summary>
    /// 该 Square 退款号是否已被其他退款 attempt 记录。默认返回 true（保守：视为已被占用），
    /// 只有能真实查询本地库的实现才应覆盖，用于自动找回退款时避免把别人的退款认领成自己的。
    /// </summary>
    Task<bool> IsRefundIdBoundToOtherAttemptAsync(
        string environment,
        string refundId,
        Guid excludingAttemptGuid,
        CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <summary>
    /// 退货单已完成、但 Square 退款仍在结算（PENDING）或结算后被拒（REJECTED/FAILED，未处理）的退款记录。
    /// 只用于结算跟踪与提示，不是金融恢复队列，不能据此重新发起退款。
    /// </summary>
    Task<IReadOnlyList<LocalSquarePaymentAttempt>> GetRefundSettlementsAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocalSquarePaymentAttempt>>([]);

    /// <summary>
    /// 以 PaymentStatus + UpdatedAt 做 CAS，更新已完成退货的 Square 退款结算状态；只允许 OrderCompleted 的退款记录。
    /// </summary>
    Task<bool> TryUpdateRefundSettlementStatusAsync(
        Guid attemptGuid,
        string expectedPaymentStatus,
        DateTimeOffset expectedUpdatedAt,
        string paymentStatus,
        string? responseText,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

/// <summary>已完成退货的 Square 退款结算状态（存于 PaymentStatus）。</summary>
public static class SquareRefundSettlementStatuses
{
    public const string Pending = "PENDING";
    public const string Completed = "COMPLETED";
    public const string Rejected = "REJECTED";
    public const string Failed = "FAILED";
    // 主管确认已改用其他方式退给顾客后的结算失败状态；保留原失败类型便于对账。
    public const string RejectedHandled = "REJECTED_HANDLED";
    public const string FailedHandled = "FAILED_HANDLED";

    public static bool IsPending(string? status) =>
        string.Equals(status?.Trim(), Pending, StringComparison.OrdinalIgnoreCase);

    public static bool IsUnhandledFailure(string? status) =>
        string.Equals(status?.Trim(), Rejected, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status?.Trim(), Failed, StringComparison.OrdinalIgnoreCase);

    public static string HandledFor(string failureStatus) =>
        string.Equals(failureStatus.Trim(), Rejected, StringComparison.OrdinalIgnoreCase) ? RejectedHandled : FailedHandled;
}

public sealed class LocalSquarePaymentAttemptRepository(LocalSqliteStore store) : ILocalSquarePaymentAttemptRepository
{
    private static readonly string[] TerminalStatuses =
    [
        LocalSquarePaymentAttemptStatus.Canceled.ToString(),
        LocalSquarePaymentAttemptStatus.TimedOut.ToString(),
        LocalSquarePaymentAttemptStatus.Failed.ToString(),
        LocalSquarePaymentAttemptStatus.OrderCompleted.ToString(),
        LocalSquarePaymentAttemptStatus.Abandoned.ToString()
    ];

    public async Task CreateAsync(LocalSquarePaymentAttempt attempt, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LocalSquarePaymentAttempts
            (
                AttemptGuid, CheckoutId, IdempotencyKey, DeviceId, LocationId, Environment,
                Amount, AmountCents, Currency, Status, CheckoutStatus, CancelReason,
                OrderDraftJson, StoreCode, DeviceCode, CashierId, PaymentId, PaymentStatus,
                ResponseCode, ResponseText, CreatedAt, UpdatedAt, CompletedAt, OrderCompletedAt, ResolvedAt,
                OperationKind, OperationGuid, SubmissionToken, RefundBusinessKey,
                RecoveryPhase, RecoveryTargetStatus, SupervisorFinancialReference
            )
            VALUES
            (
                $AttemptGuid, $CheckoutId, $IdempotencyKey, $DeviceId, $LocationId, $Environment,
                $Amount, $AmountCents, $Currency, $Status, $CheckoutStatus, $CancelReason,
                $OrderDraftJson, $StoreCode, $DeviceCode, $CashierId, $PaymentId, $PaymentStatus,
                $ResponseCode, $ResponseText, $CreatedAt, $UpdatedAt, $CompletedAt, $OrderCompletedAt, $ResolvedAt,
                $OperationKind, $OperationGuid, $SubmissionToken, $RefundBusinessKey,
                $RecoveryPhase, $RecoveryTargetStatus, $SupervisorFinancialReference
            );
            """;
        AddAttemptParameters(command, attempt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LocalSquarePaymentAttempt> CreateOrGetOpenRefundAsync(
        LocalSquarePaymentAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(attempt.OperationKind, "Refund", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(attempt.RefundBusinessKey))
        {
            throw new InvalidOperationException("退款 attempt 必须包含稳定的 RefundBusinessKey。");
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT OR IGNORE INTO LocalSquarePaymentAttempts
                (
                    AttemptGuid, CheckoutId, IdempotencyKey, DeviceId, LocationId, Environment,
                    Amount, AmountCents, Currency, Status, CheckoutStatus, CancelReason,
                    OrderDraftJson, StoreCode, DeviceCode, CashierId, PaymentId, PaymentStatus,
                    ResponseCode, ResponseText, CreatedAt, UpdatedAt, CompletedAt, OrderCompletedAt, ResolvedAt,
                    OperationKind, OperationGuid, SubmissionToken, RefundBusinessKey,
                    RecoveryPhase, RecoveryTargetStatus, SupervisorFinancialReference
                )
                VALUES
                (
                    $AttemptGuid, $CheckoutId, $IdempotencyKey, $DeviceId, $LocationId, $Environment,
                    $Amount, $AmountCents, $Currency, $Status, $CheckoutStatus, $CancelReason,
                    $OrderDraftJson, $StoreCode, $DeviceCode, $CashierId, $PaymentId, $PaymentStatus,
                    $ResponseCode, $ResponseText, $CreatedAt, $UpdatedAt, $CompletedAt, $OrderCompletedAt, $ResolvedAt,
                    $OperationKind, $OperationGuid, $SubmissionToken, $RefundBusinessKey,
                    $RecoveryPhase, $RecoveryTargetStatus, $SupervisorFinancialReference
                );
                """;
            AddAttemptParameters(insertCommand, attempt);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        LocalSquarePaymentAttempt? persisted;
        await using (var readCommand = connection.CreateCommand())
        {
            readCommand.Transaction = transaction;
            readCommand.CommandText = """
                SELECT *
                FROM LocalSquarePaymentAttempts
                WHERE OperationKind = 'Refund'
                  AND RefundBusinessKey = $RefundBusinessKey
                  AND Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
                LIMIT 1;
                """;
            readCommand.Parameters.AddWithValue("$RefundBusinessKey", attempt.RefundBusinessKey);
            for (var i = 0; i < TerminalStatuses.Length; i++)
            {
                readCommand.Parameters.AddWithValue($"$TerminalStatus{i + 1}", TerminalStatuses[i]);
            }

            await using var reader = await readCommand.ExecuteReaderAsync(cancellationToken);
            persisted = await reader.ReadAsync(cancellationToken) ? ReadAttempt(reader) : null;
        }

        await transaction.CommitAsync(cancellationToken);
        return persisted ?? throw new InvalidOperationException("退款 attempt 原子落库后无法读取。");
    }

    public async Task<bool> TryBeginRefundSubmissionAsync(
        Guid attemptGuid,
        DateTimeOffset expectedUpdatedAt,
        string submissionToken,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteUpdateCountAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $RecoveringStatus,
                ResponseCode = NULL,
                ResponseText = NULL,
                SubmissionToken = $SubmissionToken,
                RecoveryPhase = $NoRecoveryPhase,
                RecoveryTargetStatus = NULL,
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind = 'Refund'
              AND Status = $PendingStatus
              AND UpdatedAt = $ExpectedUpdatedAt
              AND SubmissionToken IS NULL
              AND COALESCE(ResponseCode, '') IN ('', $ConfirmedNotRefunded)
              AND (
                    COALESCE(RecoveryPhase, $NoRecoveryPhase) = $NoRecoveryPhase
                    OR (
                        RecoveryPhase = $FinalizePending
                        AND RecoveryTargetStatus = $AbandonedStatus
                        AND ResponseCode = $ConfirmedNotRefunded
                    )
                  );
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$PendingStatus", LocalSquarePaymentAttemptStatus.Pending.ToString());
                command.Parameters.AddWithValue("$RecoveringStatus", LocalSquarePaymentAttemptStatus.Recovering.ToString());
                command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$SubmissionToken", submissionToken);
                command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
                command.Parameters.AddWithValue("$ConfirmedNotRefunded", CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded);
                command.Parameters.AddWithValue("$NoRecoveryPhase", CardRecoveryPhases.None);
                command.Parameters.AddWithValue("$FinalizePending", CardRecoveryPhases.FinalizePending);
                command.Parameters.AddWithValue("$AbandonedStatus", LocalSquarePaymentAttemptStatus.Abandoned.ToString());
            },
            cancellationToken) == 1;
    }

    public async Task<bool> TryMarkRefundCheckoutCreatedAsync(
        Guid attemptGuid,
        string submissionToken,
        string checkoutId,
        string? checkoutStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteRefundSubmissionUpdateAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET CheckoutId = $CheckoutId,
                CheckoutStatus = $CheckoutStatus,
                Status = $Status,
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind = 'Refund'
              AND SubmissionToken = $SubmissionToken
              AND Status IN ('Pending', 'CheckoutCreated', 'Recovering', 'CheckoutCompleted', 'Unknown')
              AND COALESCE(RecoveryPhase, 'None') <> 'FinalizePending'
              AND UPPER(TRIM(COALESCE(PaymentStatus, ''))) <> 'COMPLETED'
              AND COALESCE(ResponseCode, '') NOT IN ($ResolvedCode1, $ResolvedCode2);
            """,
            attemptGuid,
            submissionToken,
            command =>
            {
                command.Parameters.AddWithValue("$CheckoutId", checkoutId);
                command.Parameters.AddWithValue("$CheckoutStatus", (object?)checkoutStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.CheckoutCreated.ToString());
                command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
            },
            cancellationToken);
    }

    public async Task<bool> TryRecordRefundResponseAsync(
        Guid attemptGuid,
        string submissionToken,
        string refundId,
        string refundStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteRefundSubmissionUpdateAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                PaymentId = $RefundId,
                PaymentStatus = $RefundStatus,
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind = 'Refund'
              AND SubmissionToken = $SubmissionToken
              AND Status IN ($RecoveringStatus, $UnknownStatus)
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
              AND (
                    UPPER(TRIM(COALESCE(PaymentStatus, ''))) <> 'COMPLETED'
                    OR UPPER(TRIM($RefundStatus)) = 'COMPLETED'
                  )
              AND COALESCE(ResponseCode, '') NOT IN ($ResolvedCode1, $ResolvedCode2);
            """,
            attemptGuid,
            submissionToken,
            command =>
            {
                command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.Recovering.ToString());
                command.Parameters.AddWithValue("$RecoveringStatus", LocalSquarePaymentAttemptStatus.Recovering.ToString());
                command.Parameters.AddWithValue("$UnknownStatus", LocalSquarePaymentAttemptStatus.Unknown.ToString());
                command.Parameters.AddWithValue("$RefundId", refundId);
                command.Parameters.AddWithValue("$RefundStatus", refundStatus);
                command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
                command.Parameters.AddWithValue("$NoRecoveryPhase", CardRecoveryPhases.None);
                command.Parameters.AddWithValue("$FinalizePending", CardRecoveryPhases.FinalizePending);
            },
            cancellationToken);
    }

    public async Task<bool> TryRecordRefundResponseAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        string submissionToken,
        string refundId,
        string refundStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteRefundSubmissionUpdateAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                PaymentId = $RefundId,
                PaymentStatus = $RefundStatus,
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind = 'Refund'
              AND SubmissionToken = $SubmissionToken
              AND Status = $ExpectedStatus
              AND UpdatedAt = $ExpectedUpdatedAt
              AND Status IN ($RecoveringStatus, $UnknownStatus)
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
              AND (
                    UPPER(TRIM(COALESCE(PaymentStatus, ''))) <> 'COMPLETED'
                    OR UPPER(TRIM($RefundStatus)) = 'COMPLETED'
                  )
              AND COALESCE(ResponseCode, '') NOT IN ($ResolvedCode1, $ResolvedCode2);
            """,
            attemptGuid,
            submissionToken,
            command =>
            {
                command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.Recovering.ToString());
                command.Parameters.AddWithValue("$ExpectedStatus", expectedStatus.ToString());
                command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$RecoveringStatus", LocalSquarePaymentAttemptStatus.Recovering.ToString());
                command.Parameters.AddWithValue("$UnknownStatus", LocalSquarePaymentAttemptStatus.Unknown.ToString());
                command.Parameters.AddWithValue("$RefundId", refundId);
                command.Parameters.AddWithValue("$RefundStatus", refundStatus);
                command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
                command.Parameters.AddWithValue("$NoRecoveryPhase", CardRecoveryPhases.None);
                command.Parameters.AddWithValue("$FinalizePending", CardRecoveryPhases.FinalizePending);
            },
            cancellationToken);
    }

    public async Task<bool> TryMarkRefundPaymentVerifiedAsync(
        Guid attemptGuid,
        string submissionToken,
        string paymentId,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteRefundSubmissionUpdateAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                PaymentId = $PaymentId,
                PaymentStatus = $PaymentStatus,
                ResponseCode = $ResponseCode,
                ResponseText = $ResponseText,
                CompletedAt = $CompletedAt,
                UpdatedAt = $CompletedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind = 'Refund'
              AND SubmissionToken = $SubmissionToken
              AND Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
              AND COALESCE(ResponseCode, '') NOT IN ($ResolvedCode1, $ResolvedCode2);
            """,
            attemptGuid,
            submissionToken,
            command =>
            {
                command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.PaymentVerified.ToString());
                command.Parameters.AddWithValue("$PaymentId", paymentId);
                command.Parameters.AddWithValue("$PaymentStatus", paymentStatus);
                command.Parameters.AddWithValue("$ResponseCode", (object?)responseCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResponseText", (object?)responseText ?? DBNull.Value);
                command.Parameters.AddWithValue("$CompletedAt", completedAt.ToString("O"));
                for (var i = 0; i < TerminalStatuses.Length; i++)
                {
                    command.Parameters.AddWithValue($"$TerminalStatus{i + 1}", TerminalStatuses[i]);
                }

                command.Parameters.AddWithValue("$NoRecoveryPhase", CardRecoveryPhases.None);
                command.Parameters.AddWithValue("$FinalizePending", CardRecoveryPhases.FinalizePending);
            },
            cancellationToken);
    }

    public async Task<bool> TryMarkRefundPaymentVerifiedAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        string submissionToken,
        string paymentId,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteRefundSubmissionUpdateAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                PaymentId = $PaymentId,
                PaymentStatus = $PaymentStatus,
                ResponseCode = $ResponseCode,
                ResponseText = $ResponseText,
                RecoveryPhase = $FinalizePending,
                RecoveryTargetStatus = $RecoveryTargetStatus,
                CompletedAt = $CompletedAt,
                UpdatedAt = $CompletedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind = 'Refund'
              AND SubmissionToken = $SubmissionToken
              AND Status = $ExpectedStatus
              AND UpdatedAt = $ExpectedUpdatedAt
              AND Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
              AND COALESCE(ResponseCode, '') NOT IN ($ResolvedCode1, $ResolvedCode2);
            """,
            attemptGuid,
            submissionToken,
            command =>
            {
                command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.PaymentVerified.ToString());
                command.Parameters.AddWithValue("$ExpectedStatus", expectedStatus.ToString());
                command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$PaymentId", paymentId);
                command.Parameters.AddWithValue("$PaymentStatus", paymentStatus);
                command.Parameters.AddWithValue("$ResponseCode", (object?)responseCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResponseText", (object?)responseText ?? DBNull.Value);
                command.Parameters.AddWithValue("$CompletedAt", completedAt.ToString("O"));
                command.Parameters.AddWithValue("$RecoveryTargetStatus", LocalSquarePaymentAttemptStatus.OrderCompleted.ToString());
                command.Parameters.AddWithValue("$NoRecoveryPhase", CardRecoveryPhases.None);
                command.Parameters.AddWithValue("$FinalizePending", CardRecoveryPhases.FinalizePending);
                for (var i = 0; i < TerminalStatuses.Length; i++)
                {
                    command.Parameters.AddWithValue($"$TerminalStatus{i + 1}", TerminalStatuses[i]);
                }
            },
            cancellationToken);
    }

    public async Task<bool> TryMarkRefundFailedAsync(
        Guid attemptGuid,
        string submissionToken,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default,
        string? cancelReason = null)
    {
        return await ExecuteRefundSubmissionUpdateAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                CheckoutStatus = COALESCE($CheckoutStatus, CheckoutStatus),
                CancelReason = COALESCE($CancelReason, CancelReason),
                PaymentStatus = COALESCE($PaymentStatus, PaymentStatus),
                ResponseCode = $ResponseCode,
                ResponseText = $ResponseText,
                ResolvedAt = $ResolvedAt,
                UpdatedAt = $ResolvedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind = 'Refund'
              AND SubmissionToken = $SubmissionToken
              AND Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
              AND UPPER(TRIM(COALESCE(PaymentStatus, ''))) <> 'COMPLETED'
              AND COALESCE(ResponseCode, '') NOT IN ($ResolvedCode1, $ResolvedCode2);
            """,
            attemptGuid,
            submissionToken,
            command =>
            {
                command.Parameters.AddWithValue("$Status", status.ToString());
                command.Parameters.AddWithValue("$CheckoutStatus", (object?)checkoutStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$CancelReason", (object?)cancelReason ?? DBNull.Value);
                command.Parameters.AddWithValue("$PaymentStatus", (object?)paymentStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResponseCode", (object?)responseCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResponseText", (object?)responseText ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResolvedAt", resolvedAt.ToString("O"));
                for (var i = 0; i < TerminalStatuses.Length; i++)
                {
                    command.Parameters.AddWithValue($"$TerminalStatus{i + 1}", TerminalStatuses[i]);
                }

                command.Parameters.AddWithValue("$NoRecoveryPhase", CardRecoveryPhases.None);
                command.Parameters.AddWithValue("$FinalizePending", CardRecoveryPhases.FinalizePending);
            },
            cancellationToken);
    }

    public async Task<bool> TryMarkRefundFailedAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        string submissionToken,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default,
        string? cancelReason = null)
    {
        return await ExecuteUpdateCountAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                CheckoutStatus = COALESCE($CheckoutStatus, CheckoutStatus),
                CancelReason = COALESCE($CancelReason, CancelReason),
                PaymentStatus = COALESCE($PaymentStatus, PaymentStatus),
                ResponseCode = $ResponseCode,
                ResponseText = $ResponseText,
                ResolvedAt = $ResolvedAt,
                UpdatedAt = $ResolvedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind = 'Refund'
              AND SubmissionToken = $SubmissionToken
              AND Status = $ExpectedStatus
              AND UpdatedAt = $ExpectedUpdatedAt
              AND Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
              AND UPPER(TRIM(COALESCE(PaymentStatus, ''))) <> 'COMPLETED'
              AND COALESCE(ResponseCode, '') NOT IN ($SupervisorPaid, $SupervisorNotPaid, $SupervisorRefunded, $SupervisorNotRefunded);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$ExpectedStatus", expectedStatus.ToString());
                command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$SubmissionToken", submissionToken);
                command.Parameters.AddWithValue("$Status", status.ToString());
                command.Parameters.AddWithValue("$CheckoutStatus", (object?)checkoutStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$CancelReason", (object?)cancelReason ?? DBNull.Value);
                command.Parameters.AddWithValue("$PaymentStatus", (object?)paymentStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResponseCode", (object?)responseCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResponseText", (object?)responseText ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResolvedAt", resolvedAt.ToString("O"));
                AddAutomaticWriteGuardParameters(command);
            },
            cancellationToken) == 1;
    }

    public async Task<bool> TryPersistRefundFailureForFinalizationAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        string submissionToken,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(paymentStatus, "FAILED", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(paymentStatus, "REJECTED", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Square 退款失败终态必须是 FAILED 或 REJECTED。", nameof(paymentStatus));
        }

        // SQLite 端规范化当前状态，避免迟到的 FAILED/REJECTED 覆盖已保存的 COMPLETED 证据。
        return await ExecuteUpdateCountAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                PaymentStatus = $PaymentStatus,
                ResponseCode = CASE
                    WHEN NULLIF(TRIM(COALESCE(ResponseCode, '')), '') IS NULL THEN $ResponseCode
                    ELSE ResponseCode
                END,
                ResponseText = CASE
                    WHEN NULLIF(TRIM(COALESCE(ResponseText, '')), '') IS NULL THEN $ResponseText
                    ELSE ResponseText
                END,
                RecoveryPhase = $FinalizePending,
                RecoveryTargetStatus = $RecoveryTargetStatus,
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind = 'Refund'
              AND SubmissionToken = $SubmissionToken
              AND Status = $ExpectedStatus
              AND UpdatedAt = $ExpectedUpdatedAt
              AND (
                    Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
                    OR (
                        Status = $FailedStatus
                        AND UPPER(TRIM(COALESCE(PaymentStatus, ''))) IN ('FAILED', 'REJECTED')
                    )
                  )
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) = $NoRecoveryPhase
              AND UPPER(TRIM(COALESCE(PaymentStatus, ''))) <> 'COMPLETED'
              AND COALESCE(ResponseCode, '') NOT IN ($SupervisorPaid, $SupervisorNotPaid, $SupervisorRefunded, $SupervisorNotRefunded);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$ExpectedStatus", expectedStatus.ToString());
                command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$SubmissionToken", submissionToken);
                command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.Unknown.ToString());
                command.Parameters.AddWithValue("$FailedStatus", LocalSquarePaymentAttemptStatus.Failed.ToString());
                command.Parameters.AddWithValue("$PaymentStatus", paymentStatus.ToUpperInvariant());
                command.Parameters.AddWithValue("$ResponseCode", (object?)responseCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResponseText", (object?)responseText ?? DBNull.Value);
                command.Parameters.AddWithValue("$RecoveryTargetStatus", LocalSquarePaymentAttemptStatus.Abandoned.ToString());
                command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
                AddAutomaticWriteGuardParameters(command);
            },
            cancellationToken) == 1;
    }

    public async Task MarkCheckoutCreatedAsync(
        Guid attemptGuid,
        string checkoutId,
        string? checkoutStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await ExecuteGuardedUpdateAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET CheckoutId = $CheckoutId,
                CheckoutStatus = $CheckoutStatus,
                Status = $Status,
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind IN ('Sale', 'Refund', 'Create', 'Repayment')
              AND Status IN ('Pending', 'CheckoutCreated', 'Recovering', 'CheckoutCompleted', 'Unknown')
              AND Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
              AND COALESCE(ResponseCode, '') NOT IN ($SupervisorPaid, $SupervisorNotPaid, $SupervisorRefunded, $SupervisorNotRefunded);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$CheckoutId", checkoutId);
                command.Parameters.AddWithValue("$CheckoutStatus", (object?)checkoutStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.CheckoutCreated.ToString());
                command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
                AddAutomaticWriteGuardParameters(command);
            },
            cancellationToken);
    }

    public Task MarkRecoveringAsync(Guid attemptGuid, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        return UpdateCheckoutStatusAsync(
            attemptGuid,
            LocalSquarePaymentAttemptStatus.Recovering,
            checkoutStatus: null,
            cancelReason: null,
            updatedAt,
            cancellationToken);
    }

    public Task<bool> TryMarkRecoveringAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        return TryUpdateCheckoutStatusAsync(
            attemptGuid,
            expectedStatus,
            expectedUpdatedAt,
            LocalSquarePaymentAttemptStatus.Recovering,
            checkoutStatus: null,
            cancelReason: null,
            updatedAt,
            cancellationToken);
    }

    public async Task<bool> TryMarkCheckoutCreatedAsync(
        Guid attemptGuid,
        string submissionToken,
        string checkoutId,
        string? checkoutStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteUpdateCountAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET CheckoutId = $CheckoutId,
                CheckoutStatus = $CheckoutStatus,
                Status = $Status,
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid
              AND SubmissionToken = $SubmissionToken
              AND Status IN ($PendingStatus, $RecoveringStatus, $CheckoutCreatedStatus)
              -- 关键逻辑：迟到的 checkout 回调不得改写已被恢复服务或主管接管的行。
              -- 缺这两道护栏时 Status=Recovering 会命中上面的 IN 列表，于是把
              -- Status 改回 CheckoutCreated 并顶掉 UpdatedAt：对“确认已付款”会让
              -- 后续终结 CAS 失效，主管确认的收款推不到 OrderCompleted；
              -- 对“确认未付款”更糟——刚被清空的 CheckoutId 被重新写回，
              -- 等于复活了主管判定“未发生”的支付证据。
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
              AND COALESCE(ResponseCode, '') NOT IN (
                    $SupervisorPaid,
                    $SupervisorNotPaid,
                    $SupervisorRefunded,
                    $SupervisorNotRefunded
                  );
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$SubmissionToken", submissionToken);
                command.Parameters.AddWithValue("$CheckoutId", checkoutId);
                command.Parameters.AddWithValue("$CheckoutStatus", (object?)checkoutStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.CheckoutCreated.ToString());
                command.Parameters.AddWithValue("$PendingStatus", LocalSquarePaymentAttemptStatus.Pending.ToString());
                command.Parameters.AddWithValue("$RecoveringStatus", LocalSquarePaymentAttemptStatus.Recovering.ToString());
                command.Parameters.AddWithValue("$CheckoutCreatedStatus", LocalSquarePaymentAttemptStatus.CheckoutCreated.ToString());
                command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
                AddAutomaticWriteGuardParameters(command);
            },
            cancellationToken) == 1;
    }

    public async Task<bool> ResolveRefundAsync(
        CardRefundAttemptResolution resolution,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAttemptAsync(resolution.AttemptGuid, cancellationToken);
        return current is not null && await ResolveRefundCoreAsync(
            resolution,
            current.Status,
            current.UpdatedAt,
            journal: null,
            cancellationToken);
    }

    public async Task<bool> ResolveRefundWithJournalAsync(
        CardRefundAttemptResolution resolution,
        LocalFinancialSupervisorResolution journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Target != LocalFinancialSupervisorResolutionTarget.CardRefund ||
            journal.AttemptGuid != resolution.AttemptGuid)
        {
            throw new ArgumentException("主管结案 journal 与 Square 退款 attempt 不匹配。", nameof(journal));
        }

        var current = await GetAttemptAsync(resolution.AttemptGuid, cancellationToken);
        return current is not null && await ResolveRefundCoreAsync(
            resolution,
            current.Status,
            current.UpdatedAt,
            journal,
            cancellationToken);
    }

    public Task<bool> ResolveRefundWithJournalAsync(
        CardRefundAttemptResolution resolution,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        LocalFinancialSupervisorResolution journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Target != LocalFinancialSupervisorResolutionTarget.CardRefund ||
            journal.AttemptGuid != resolution.AttemptGuid)
        {
            throw new ArgumentException("主管结案 journal 与 Square 退款 attempt 不匹配。", nameof(journal));
        }

        return ResolveRefundCoreAsync(
            resolution,
            expectedStatus,
            expectedUpdatedAt,
            journal,
            cancellationToken);
    }

    public async Task<bool> ResolvePaymentWithJournalAsync(
        SquarePaymentResolution resolution,
        LocalFinancialSupervisorResolution journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Target != LocalFinancialSupervisorResolutionTarget.ActiveSession ||
            journal.AttemptGuid != resolution.AttemptGuid)
        {
            throw new ArgumentException("主管结案 journal 与 Square 付款 attempt 不匹配。", nameof(journal));
        }

        if (resolution.Decision == CardRecoverySupervisorDecision.ConfirmProcessed &&
            string.IsNullOrWhiteSpace(resolution.PaymentReference))
        {
            throw new ArgumentException("确认已付款必须提供真实银行或终端参考号。", nameof(resolution));
        }

        if (resolution.Decision == CardRecoverySupervisorDecision.ConfirmNotProcessed &&
            string.IsNullOrWhiteSpace(resolution.Evidence))
        {
            throw new ArgumentException("确认未付款必须提供银行证据。", nameof(resolution));
        }

        var sql = resolution.Decision switch
        {
            CardRecoverySupervisorDecision.ConfirmProcessed => """
                UPDATE LocalSquarePaymentAttempts
                SET Status = $Status,
                    SupervisorFinancialReference = $SupervisorFinancialReference,
                    -- 结案后吊销提交令牌：它是旧 worker 的 fencing token，
                    -- 留着它迟到的回调仍能通过 SubmissionToken 栅栏写回。
                    SubmissionToken = NULL,
                    ResponseCode = $ResponseCode,
                    ResponseText = $ResponseText,
                    RecoveryPhase = $RecoveryPhase,
                    RecoveryTargetStatus = $RecoveryTargetStatus,
                    CompletedAt = $ResolvedAt,
                    ResolvedAt = $ResolvedAt,
                    UpdatedAt = $ResolvedAt
                WHERE AttemptGuid = $AttemptGuid
                  AND OperationKind = 'Sale'
                  AND Status = $ExpectedStatus
                  AND UpdatedAt = $ExpectedUpdatedAt
                  AND Status IN ($OpenStatus1, $OpenStatus2, $OpenStatus3, $OpenStatus4, $OpenStatus5)
                  AND PaymentId IS NULL
                  AND PaymentStatus IS NULL
                  AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
                  AND COALESCE(ResponseCode, '') NOT IN ($ResolvedCode1, $ResolvedCode2);
                """,
            CardRecoverySupervisorDecision.ConfirmNotProcessed => """
                UPDATE LocalSquarePaymentAttempts
                SET Status = $Status,
                    CheckoutId = NULL,
                    CheckoutStatus = NULL,
                    CancelReason = NULL,
                    PaymentId = NULL,
                    PaymentStatus = NULL,
                    SupervisorFinancialReference = NULL,
                    -- 同上：确认未付款已把支付证据清空，必须一并吊销提交令牌，
                    -- 否则迟到的回调会把 CheckoutId 重新写回。
                    SubmissionToken = NULL,
                    ResponseCode = $ResponseCode,
                    ResponseText = $ResponseText,
                    RecoveryPhase = $RecoveryPhase,
                    RecoveryTargetStatus = $RecoveryTargetStatus,
                    CompletedAt = NULL,
                    OrderCompletedAt = NULL,
                    ResolvedAt = NULL,
                    UpdatedAt = $ResolvedAt
                WHERE AttemptGuid = $AttemptGuid
                  AND OperationKind = 'Sale'
                  AND Status = $ExpectedStatus
                  AND UpdatedAt = $ExpectedUpdatedAt
                  AND Status IN ($OpenStatus1, $OpenStatus2, $OpenStatus3, $OpenStatus4, $OpenStatus5)
                  AND PaymentId IS NULL
                  AND PaymentStatus IS NULL
                  AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
                  AND COALESCE(ResponseCode, '') NOT IN ($ResolvedCode1, $ResolvedCode2);
                """,
            _ => """
                UPDATE LocalSquarePaymentAttempts
                SET Status = $Status,
                    ResponseCode = $ResponseCode,
                    ResponseText = $ResponseText,
                    RecoveryPhase = $RecoveryPhase,
                    RecoveryTargetStatus = $RecoveryTargetStatus,
                    UpdatedAt = $ResolvedAt
                WHERE AttemptGuid = $AttemptGuid
                  AND OperationKind = 'Sale'
                  AND Status = $ExpectedStatus
                  AND UpdatedAt = $ExpectedUpdatedAt
                  AND Status IN ($OpenStatus1, $OpenStatus2, $OpenStatus3, $OpenStatus4, $OpenStatus5)
                  AND PaymentId IS NULL
                  AND PaymentStatus IS NULL
                  AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
                  AND COALESCE(ResponseCode, '') NOT IN ($ResolvedCode1, $ResolvedCode2);
                """
        };

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$AttemptGuid", resolution.AttemptGuid.ToString());
        command.Parameters.AddWithValue("$Status", resolution.Decision switch
        {
            CardRecoverySupervisorDecision.ConfirmProcessed => LocalSquarePaymentAttemptStatus.Recovering.ToString(),
            CardRecoverySupervisorDecision.ConfirmNotProcessed => LocalSquarePaymentAttemptStatus.Pending.ToString(),
            _ => LocalSquarePaymentAttemptStatus.Recovering.ToString()
        });
        command.Parameters.AddWithValue(
            "$SupervisorFinancialReference",
            (object?)Normalize(resolution.PaymentReference) ?? DBNull.Value);
        command.Parameters.AddWithValue("$ResponseCode", resolution.Decision switch
        {
            CardRecoverySupervisorDecision.ConfirmProcessed => ActiveSessionSupervisorResolutionCodes.ConfirmedPaid,
            CardRecoverySupervisorDecision.ConfirmNotProcessed => ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid,
            _ => ActiveSessionSupervisorResolutionCodes.ContinueWaiting
        });
        command.Parameters.AddWithValue("$ResponseText", BuildPaymentResolutionText(resolution));
        command.Parameters.AddWithValue("$ResolvedAt", resolution.ResolvedAt.ToString("O"));
        command.Parameters.AddWithValue("$ExpectedStatus", resolution.ExpectedStatus.ToString());
        command.Parameters.AddWithValue("$ExpectedUpdatedAt", resolution.ExpectedUpdatedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$RecoveryPhase",
            resolution.Decision == CardRecoverySupervisorDecision.ContinueWaiting
                ? CardRecoveryPhases.None
                : CardRecoveryPhases.FinalizePending);
        command.Parameters.AddWithValue(
            "$RecoveryTargetStatus",
            resolution.Decision switch
            {
                CardRecoverySupervisorDecision.ConfirmProcessed => LocalSquarePaymentAttemptStatus.OrderCompleted.ToString(),
                CardRecoverySupervisorDecision.ConfirmNotProcessed => LocalSquarePaymentAttemptStatus.Abandoned.ToString(),
                _ => DBNull.Value
            });
        command.Parameters.AddWithValue("$ResolvedCode1", ActiveSessionSupervisorResolutionCodes.ConfirmedPaid);
        command.Parameters.AddWithValue("$ResolvedCode2", ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid);
        command.Parameters.AddWithValue("$OpenStatus1", LocalSquarePaymentAttemptStatus.Pending.ToString());
        command.Parameters.AddWithValue("$OpenStatus2", LocalSquarePaymentAttemptStatus.CheckoutCreated.ToString());
        command.Parameters.AddWithValue("$OpenStatus3", LocalSquarePaymentAttemptStatus.Recovering.ToString());
        command.Parameters.AddWithValue("$OpenStatus4", LocalSquarePaymentAttemptStatus.CheckoutCompleted.ToString());
        command.Parameters.AddWithValue("$OpenStatus5", LocalSquarePaymentAttemptStatus.Unknown.ToString());
        command.Parameters.AddWithValue("$NoRecoveryPhase", CardRecoveryPhases.None);
        command.Parameters.AddWithValue("$FinalizePending", CardRecoveryPhases.FinalizePending);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await LocalFinancialSupervisorResolutionRepository.InsertAsync(
            connection,
            transaction,
            journal,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> TryTerminalizeNotPaidAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // 主管确认未付款后：只有仍处于 ConfirmedNotPaid 且 Status/UpdatedAt 未变才 CAS 终态化，
        // 使该 attempt 从异常队列消失，后续重刷创建全新 AttemptGuid/幂等键。
        command.CommandText = """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                ResolvedAt = $ResolvedAt,
                UpdatedAt = $ResolvedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind = 'Sale'
              AND Status = $ExpectedStatus
              AND UpdatedAt = $ExpectedUpdatedAt
              AND ResponseCode = $ConfirmedNotPaidCode
              AND Status IN ($PendingStatus, $RecoveringStatus);
            """;
        command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
        command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.Abandoned.ToString());
        command.Parameters.AddWithValue("$ResolvedAt", resolvedAt.ToString("O"));
        command.Parameters.AddWithValue("$ExpectedStatus", expectedStatus.ToString());
        command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$ConfirmedNotPaidCode", ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid);
        command.Parameters.AddWithValue("$PendingStatus", LocalSquarePaymentAttemptStatus.Pending.ToString());
        command.Parameters.AddWithValue("$RecoveringStatus", LocalSquarePaymentAttemptStatus.Recovering.ToString());

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static string BuildPaymentResolutionText(SquarePaymentResolution resolution)
    {
        var reason = Normalize(resolution.Reason) ?? string.Empty;
        var evidence = Normalize(resolution.Evidence);
        return evidence is null
            ? reason
            : string.IsNullOrWhiteSpace(reason)
                ? $"Evidence: {evidence}"
                : $"{reason} Evidence: {evidence}";
    }

    private async Task<bool> ResolveRefundCoreAsync(
        CardRefundAttemptResolution resolution,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        LocalFinancialSupervisorResolution? journal,
        CancellationToken cancellationToken)
    {
        // 写入确认退款前必须有真实 RefundReference，主管备注或证据不能替代。
        var refundReference = Normalize(resolution.RefundReference);
        if (resolution.Decision == CardRefundSupervisorDecision.ConfirmRefunded &&
            refundReference is null)
        {
            throw new ArgumentException(
                "Confirming a refund requires a real RefundReference.",
                nameof(resolution));
        }

        var sql = resolution.Decision switch
        {
            CardRefundSupervisorDecision.ConfirmRefunded => """
                UPDATE LocalSquarePaymentAttempts
                SET Status = $Status,
                    SupervisorFinancialReference = $RefundReference,
                    ResponseCode = $ResponseCode,
                    ResponseText = $ResponseText,
                    SubmissionToken = NULL,
                    RecoveryPhase = $RecoveryPhase,
                    RecoveryTargetStatus = $RecoveryTargetStatus,
                    CompletedAt = $ResolvedAt,
                    ResolvedAt = $ResolvedAt,
                    UpdatedAt = $ResolvedAt
                WHERE AttemptGuid = $AttemptGuid
                  AND OperationKind = 'Refund'
                  AND Status = $ExpectedStatus
                  AND UpdatedAt = $ExpectedUpdatedAt
                  AND UPPER(COALESCE(PaymentStatus, '')) NOT IN ('COMPLETED', 'FAILED', 'REJECTED')
                  AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
                  AND COALESCE(ResponseCode, '') NOT IN ($ResolvedCode1, $ResolvedCode2)
                  AND Status IN ($ReviewStatus1, $ReviewStatus2, $ReviewStatus3);
                """,
            CardRefundSupervisorDecision.ConfirmNotRefunded => """
                UPDATE LocalSquarePaymentAttempts
                SET Status = $Status,
                    CheckoutId = NULL,
                    CheckoutStatus = NULL,
                    CancelReason = NULL,
                    PaymentId = NULL,
                    PaymentStatus = NULL,
                    SupervisorFinancialReference = NULL,
                    ResponseCode = $ResponseCode,
                    ResponseText = $ResponseText,
                    SubmissionToken = NULL,
                    RecoveryPhase = $RecoveryPhase,
                    RecoveryTargetStatus = $RecoveryTargetStatus,
                    CompletedAt = NULL,
                    OrderCompletedAt = NULL,
                    ResolvedAt = NULL,
                    UpdatedAt = $ResolvedAt
                WHERE AttemptGuid = $AttemptGuid
                  AND OperationKind = 'Refund'
                  AND Status = $ExpectedStatus
                  AND UpdatedAt = $ExpectedUpdatedAt
                  AND PaymentId IS NULL
                  AND PaymentStatus IS NULL
                  AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
                  AND COALESCE(ResponseCode, '') NOT IN ($ResolvedCode1, $ResolvedCode2)
                  AND Status IN ($ReviewStatus1, $ReviewStatus2, $ReviewStatus3);
                """,
            _ => """
                UPDATE LocalSquarePaymentAttempts
                SET Status = $Status,
                    ResponseCode = $ResponseCode,
                    ResponseText = $ResponseText,
                    SubmissionToken = NULL,
                    RecoveryPhase = $RecoveryPhase,
                    RecoveryTargetStatus = $RecoveryTargetStatus,
                    UpdatedAt = $ResolvedAt
                WHERE AttemptGuid = $AttemptGuid
                  AND OperationKind = 'Refund'
                  AND Status = $ExpectedStatus
                  AND UpdatedAt = $ExpectedUpdatedAt
                  AND PaymentId IS NULL
                  AND PaymentStatus IS NULL
                  AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
                  AND COALESCE(ResponseCode, '') NOT IN ($ResolvedCode1, $ResolvedCode2)
                  AND Status IN ($ReviewStatus1, $ReviewStatus2, $ReviewStatus3);
                """
        };

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$AttemptGuid", resolution.AttemptGuid.ToString());
        command.Parameters.AddWithValue("$Status", resolution.Decision switch
        {
            CardRefundSupervisorDecision.ConfirmRefunded => LocalSquarePaymentAttemptStatus.Recovering.ToString(),
            CardRefundSupervisorDecision.ConfirmNotRefunded => LocalSquarePaymentAttemptStatus.Pending.ToString(),
            _ => LocalSquarePaymentAttemptStatus.Recovering.ToString()
        });
        command.Parameters.AddWithValue("$RefundReference", (object?)refundReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$ResponseCode", resolution.Decision switch
        {
            CardRefundSupervisorDecision.ConfirmRefunded => CardRefundSupervisorResolutionCodes.ConfirmedRefunded,
            CardRefundSupervisorDecision.ConfirmNotRefunded => CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded,
            _ => CardRefundSupervisorResolutionCodes.ContinueWaiting
        });
        command.Parameters.AddWithValue("$ResponseText", BuildResolutionText(resolution));
        command.Parameters.AddWithValue("$ResolvedAt", resolution.ResolvedAt.ToString("O"));
        command.Parameters.AddWithValue("$ExpectedStatus", expectedStatus.ToString());
        command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$RecoveryPhase",
            resolution.Decision == CardRefundSupervisorDecision.ContinueWaiting
                ? CardRecoveryPhases.None
                : CardRecoveryPhases.FinalizePending);
        command.Parameters.AddWithValue(
            "$RecoveryTargetStatus",
            resolution.Decision switch
            {
                CardRefundSupervisorDecision.ConfirmRefunded => LocalSquarePaymentAttemptStatus.OrderCompleted.ToString(),
                CardRefundSupervisorDecision.ConfirmNotRefunded => LocalSquarePaymentAttemptStatus.Abandoned.ToString(),
                _ => DBNull.Value
            });
        command.Parameters.AddWithValue("$NoRecoveryPhase", CardRecoveryPhases.None);
        command.Parameters.AddWithValue("$FinalizePending", CardRecoveryPhases.FinalizePending);
        command.Parameters.AddWithValue("$ResolvedCode1", CardRefundSupervisorResolutionCodes.ConfirmedRefunded);
        command.Parameters.AddWithValue("$ResolvedCode2", CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded);
        command.Parameters.AddWithValue("$ReviewStatus1", LocalSquarePaymentAttemptStatus.Recovering.ToString());
        command.Parameters.AddWithValue("$ReviewStatus2", LocalSquarePaymentAttemptStatus.Unknown.ToString());
        command.Parameters.AddWithValue("$ReviewStatus3", LocalSquarePaymentAttemptStatus.CheckoutCreated.ToString());

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

    public async Task UpdateCheckoutStatusAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? cancelReason,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAttemptAsync(attemptGuid, cancellationToken)
            ?? throw new InvalidOperationException("Square 支付 attempt 不存在。");
        if (!await TryUpdateCheckoutStatusAsync(
                attemptGuid,
                current.Status,
                current.UpdatedAt,
                status,
                checkoutStatus,
                cancelReason,
                updatedAt,
                cancellationToken))
        {
            throw new InvalidOperationException("支付 attempt 状态已变化，旧任务不得继续写入。");
        }
    }

    public async Task<bool> TryUpdateCheckoutStatusAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? cancelReason,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteUpdateCountAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                CheckoutStatus = COALESCE($CheckoutStatus, CheckoutStatus),
                CancelReason = COALESCE($CancelReason, CancelReason),
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind IN ('Sale', 'Refund', 'Create', 'Repayment')
              AND Status IN ('Pending', 'CheckoutCreated', 'Recovering', 'CheckoutCompleted', 'Unknown')
              AND Status = $ExpectedStatus
              AND UpdatedAt = $ExpectedUpdatedAt
              AND Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
              AND COALESCE(ResponseCode, '') NOT IN ($SupervisorPaid, $SupervisorNotPaid, $SupervisorRefunded, $SupervisorNotRefunded);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$ExpectedStatus", expectedStatus.ToString());
                command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$Status", status.ToString());
                command.Parameters.AddWithValue("$CheckoutStatus", (object?)checkoutStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$CancelReason", (object?)cancelReason ?? DBNull.Value);
                command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
                AddAutomaticWriteGuardParameters(command);
            },
            cancellationToken) == 1;
    }

    public async Task MarkPaymentVerifiedAsync(
        Guid attemptGuid,
        string paymentId,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAttemptAsync(attemptGuid, cancellationToken)
            ?? throw new InvalidOperationException("Square 支付 attempt 不存在。");
        if (!await TryMarkPaymentVerifiedAsync(
                attemptGuid,
                current.Status,
                current.UpdatedAt,
                paymentId,
                paymentStatus,
                responseCode,
                responseText,
                completedAt,
                cancellationToken))
        {
            throw new InvalidOperationException("支付 attempt 状态已变化，旧任务不得继续写入。");
        }
    }

    public async Task<bool> TryMarkPaymentVerifiedAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        string paymentId,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteUpdateCountAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                PaymentId = $PaymentId,
                PaymentStatus = $PaymentStatus,
                ResponseCode = $ResponseCode,
                ResponseText = $ResponseText,
                CompletedAt = $CompletedAt,
                UpdatedAt = $CompletedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind IN ('Sale', 'Refund', 'Create', 'Repayment')
              AND Status IN ('Pending', 'CheckoutCreated', 'Recovering', 'CheckoutCompleted', 'Unknown')
              AND Status = $ExpectedStatus
              AND UpdatedAt = $ExpectedUpdatedAt
              AND Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
              AND COALESCE(ResponseCode, '') NOT IN ($SupervisorPaid, $SupervisorNotPaid, $SupervisorRefunded, $SupervisorNotRefunded);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$ExpectedStatus", expectedStatus.ToString());
                command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.PaymentVerified.ToString());
                command.Parameters.AddWithValue("$PaymentId", paymentId);
                command.Parameters.AddWithValue("$PaymentStatus", paymentStatus);
                command.Parameters.AddWithValue("$ResponseCode", (object?)responseCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResponseText", (object?)responseText ?? DBNull.Value);
                command.Parameters.AddWithValue("$CompletedAt", completedAt.ToString("O"));
                AddAutomaticWriteGuardParameters(command);
            },
            cancellationToken) == 1;
    }

    public async Task<bool> TryPersistPaymentVerifiedRecoveryAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        string paymentId,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteUpdateCountAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                PaymentId = $PaymentId,
                PaymentStatus = $PaymentStatus,
                ResponseCode = $ResponseCode,
                ResponseText = $ResponseText,
                RecoveryPhase = $FinalizePending,
                RecoveryTargetStatus = $RecoveryTargetStatus,
                CompletedAt = $CompletedAt,
                UpdatedAt = $CompletedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind IN ('Sale', 'Refund', 'Create', 'Repayment')
              AND Status IN ('Pending', 'CheckoutCreated', 'Recovering', 'CheckoutCompleted', 'Unknown')
              AND Status = $ExpectedStatus
              AND UpdatedAt = $ExpectedUpdatedAt
              AND Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
              AND COALESCE(ResponseCode, '') NOT IN ($SupervisorPaid, $SupervisorNotPaid, $SupervisorRefunded, $SupervisorNotRefunded);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$ExpectedStatus", expectedStatus.ToString());
                command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.PaymentVerified.ToString());
                command.Parameters.AddWithValue("$PaymentId", paymentId);
                command.Parameters.AddWithValue("$PaymentStatus", paymentStatus);
                command.Parameters.AddWithValue("$ResponseCode", (object?)responseCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResponseText", (object?)responseText ?? DBNull.Value);
                command.Parameters.AddWithValue("$RecoveryTargetStatus", LocalSquarePaymentAttemptStatus.OrderCompleted.ToString());
                command.Parameters.AddWithValue("$CompletedAt", completedAt.ToString("O"));
                AddAutomaticWriteGuardParameters(command);
            },
            cancellationToken) == 1;
    }

    public async Task MarkFailedAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default,
        string? cancelReason = null)
    {
        var current = await GetAttemptAsync(attemptGuid, cancellationToken)
            ?? throw new InvalidOperationException("Square 支付 attempt 不存在。");
        if (!await TryMarkFailedAsync(
                attemptGuid,
                current.Status,
                current.UpdatedAt,
                status,
                checkoutStatus,
                paymentStatus,
                responseCode,
                responseText,
                resolvedAt,
                cancellationToken,
                cancelReason))
        {
            throw new InvalidOperationException("支付 attempt 状态已变化，旧任务不得继续写入。");
        }
    }

    public async Task<bool> TryMarkFailedAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default,
        string? cancelReason = null)
    {
        return await ExecuteUpdateCountAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                CheckoutStatus = COALESCE($CheckoutStatus, CheckoutStatus),
                CancelReason = COALESCE($CancelReason, CancelReason),
                PaymentStatus = COALESCE($PaymentStatus, PaymentStatus),
                ResponseCode = $ResponseCode,
                ResponseText = $ResponseText,
                ResolvedAt = $ResolvedAt,
                UpdatedAt = $ResolvedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind IN ('Sale', 'Refund', 'Create', 'Repayment')
              AND Status IN ('Pending', 'CheckoutCreated', 'Recovering', 'CheckoutCompleted', 'Unknown')
              AND Status = $ExpectedStatus
              AND UpdatedAt = $ExpectedUpdatedAt
              AND Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
              AND COALESCE(ResponseCode, '') NOT IN ($SupervisorPaid, $SupervisorNotPaid, $SupervisorRefunded, $SupervisorNotRefunded);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$ExpectedStatus", expectedStatus.ToString());
                command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$Status", status.ToString());
                command.Parameters.AddWithValue("$CheckoutStatus", (object?)checkoutStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$CancelReason", (object?)cancelReason ?? DBNull.Value);
                command.Parameters.AddWithValue("$PaymentStatus", (object?)paymentStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResponseCode", (object?)responseCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResponseText", (object?)responseText ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResolvedAt", resolvedAt.ToString("O"));
                AddAutomaticWriteGuardParameters(command);
            },
            cancellationToken) == 1;
    }

    public async Task MarkOrderCompletedAsync(Guid attemptGuid, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        var current = await GetAttemptAsync(attemptGuid, cancellationToken)
            ?? throw new InvalidOperationException("Square 支付 attempt 不存在。");
        if (!await TryMarkOrderCompletedAsync(
                attemptGuid,
                current.Status,
                current.UpdatedAt,
                completedAt,
                cancellationToken))
        {
            throw new InvalidOperationException("支付 attempt 状态已变化，旧任务不得继续写入。");
        }
    }

    public async Task<bool> TryMarkOrderCompletedAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteUpdateCountAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                OrderCompletedAt = $CompletedAt,
                UpdatedAt = $CompletedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind IN ('Sale', 'Refund', 'Create', 'Repayment')
              AND Status = $ExpectedStatus
              AND Status = $PaymentVerifiedStatus
              AND UpdatedAt = $ExpectedUpdatedAt
              AND Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) <> $FinalizePending
              AND COALESCE(ResponseCode, '') NOT IN ($SupervisorPaid, $SupervisorNotPaid, $SupervisorRefunded, $SupervisorNotRefunded);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$ExpectedStatus", expectedStatus.ToString());
                command.Parameters.AddWithValue("$PaymentVerifiedStatus", LocalSquarePaymentAttemptStatus.PaymentVerified.ToString());
                command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.OrderCompleted.ToString());
                command.Parameters.AddWithValue("$CompletedAt", completedAt.ToString("O"));
                AddAutomaticWriteGuardParameters(command);
            },
            cancellationToken) == 1;
    }

    public async Task<bool> TryBeginRecoveryFinalizationAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        LocalSquarePaymentAttemptStatus targetStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (!TerminalStatuses.Contains(targetStatus.ToString(), StringComparer.Ordinal))
        {
            throw new ArgumentException("Square 恢复最终目标必须是终态。", nameof(targetStatus));
        }

        return await ExecuteUpdateCountAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET RecoveryPhase = $FinalizePending,
                RecoveryTargetStatus = $TargetStatus,
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind IN ('Sale', 'Refund', 'Create', 'Repayment')
              AND Status IN ('Pending', 'CheckoutCreated', 'Recovering', 'CheckoutCompleted', 'Unknown', 'PaymentVerified')
              AND Status = $ExpectedStatus
              AND UpdatedAt = $ExpectedUpdatedAt
              AND Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
              AND COALESCE(RecoveryPhase, $NoRecoveryPhase) = $NoRecoveryPhase;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$ExpectedStatus", expectedStatus.ToString());
                command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$TargetStatus", targetStatus.ToString());
                command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
                AddAutomaticWriteGuardParameters(command);
            },
            cancellationToken) == 1;
    }

    public async Task<bool> TryCompleteRecoveryFinalizationAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        LocalSquarePaymentAttemptStatus targetStatus,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        if (!TerminalStatuses.Contains(targetStatus.ToString(), StringComparer.Ordinal))
        {
            throw new ArgumentException("Square 恢复最终目标必须是终态。", nameof(targetStatus));
        }

        return await ExecuteUpdateCountAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $TargetStatus,
                RecoveryPhase = $NoRecoveryPhase,
                RecoveryTargetStatus = NULL,
                OrderCompletedAt = CASE WHEN $TargetStatus = $OrderCompletedStatus THEN $CompletedAt ELSE OrderCompletedAt END,
                ResolvedAt = CASE WHEN $TargetStatus <> $OrderCompletedStatus THEN $CompletedAt ELSE ResolvedAt END,
                UpdatedAt = $CompletedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind IN ('Sale', 'Refund', 'Create', 'Repayment')
              AND Status = $ExpectedStatus
              AND UpdatedAt = $ExpectedUpdatedAt
              AND RecoveryPhase = $FinalizePending
              AND RecoveryTargetStatus = $TargetStatus;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$ExpectedStatus", expectedStatus.ToString());
                command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$TargetStatus", targetStatus.ToString());
                command.Parameters.AddWithValue("$OrderCompletedStatus", LocalSquarePaymentAttemptStatus.OrderCompleted.ToString());
                command.Parameters.AddWithValue("$CompletedAt", completedAt.ToString("O"));
                command.Parameters.AddWithValue("$NoRecoveryPhase", CardRecoveryPhases.None);
                command.Parameters.AddWithValue("$FinalizePending", CardRecoveryPhases.FinalizePending);
            },
            cancellationToken) == 1;
    }

    public async Task<LocalSquarePaymentAttempt?> GetLatestOpenAttemptAsync(
        string storeCode,
        string deviceCode,
        string? cashierId,
        string environment,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT *
            FROM LocalSquarePaymentAttempts
            WHERE StoreCode = $StoreCode
              AND DeviceCode = $DeviceCode
              AND ($CashierId IS NULL OR CashierId = $CashierId)
              AND Environment = $Environment
              AND OperationKind = $OperationKind
              AND Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
            ORDER BY UpdatedAt DESC, CreatedAt DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$CashierId", (object?)cashierId ?? DBNull.Value);
        command.Parameters.AddWithValue("$Environment", environment);
        command.Parameters.AddWithValue("$OperationKind", "Sale");
        for (var i = 0; i < TerminalStatuses.Length; i++)
        {
            command.Parameters.AddWithValue($"$TerminalStatus{i + 1}", TerminalStatuses[i]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAttempt(reader) : null;
    }

    public Task<LocalSquarePaymentAttempt?> GetLatestOpenSaleAttemptForTerminalAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken = default) =>
        GetLatestOpenAttemptAsync(
            storeCode,
            deviceCode,
            cashierId: null,
            environment,
            cancellationToken);

    public async Task<bool> IsRefundIdBoundToOtherAttemptAsync(
        string environment,
        string refundId,
        Guid excludingAttemptGuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM LocalSquarePaymentAttempts
            WHERE OperationKind = $Refund
              AND Environment = $Environment
              AND PaymentId = $RefundId
              AND AttemptGuid <> $AttemptGuid
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$Refund", "Refund");
        command.Parameters.AddWithValue("$Environment", environment);
        command.Parameters.AddWithValue("$RefundId", refundId);
        command.Parameters.AddWithValue("$AttemptGuid", excludingAttemptGuid.ToString());
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<LocalSquarePaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM LocalSquarePaymentAttempts WHERE AttemptGuid = $AttemptGuid;";
        command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAttempt(reader) : null;
    }

    public async Task<IReadOnlyList<LocalSquarePaymentAttempt>> GetOpenRefundAttemptsAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT *
            FROM LocalSquarePaymentAttempts
            WHERE StoreCode = $StoreCode
              AND DeviceCode = $DeviceCode
              AND Environment = $Environment
              AND OperationKind = $OperationKind
              AND (
                    Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
                    OR (
                        Status = $FailedStatus
                        AND UPPER(TRIM(COALESCE(PaymentStatus, ''))) IN ('FAILED', 'REJECTED')
                        AND COALESCE(RecoveryPhase, $NoRecoveryPhase) = $NoRecoveryPhase
                        AND RecoveryTargetStatus IS NULL
                        AND NULLIF(TRIM(COALESCE(SubmissionToken, '')), '') IS NOT NULL
                        AND COALESCE(ResponseCode, '') NOT IN (
                            $SupervisorPaid,
                            $SupervisorNotPaid,
                            $SupervisorRefunded,
                            $SupervisorNotRefunded,
                            $SupervisorContinueWaiting)
                    )
                  )
            ORDER BY UpdatedAt DESC, CreatedAt DESC;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$Environment", environment);
        command.Parameters.AddWithValue("$OperationKind", "Refund");
        for (var i = 0; i < TerminalStatuses.Length; i++)
        {
            command.Parameters.AddWithValue($"$TerminalStatus{i + 1}", TerminalStatuses[i]);
        }
        command.Parameters.AddWithValue("$FailedStatus", LocalSquarePaymentAttemptStatus.Failed.ToString());
        command.Parameters.AddWithValue("$NoRecoveryPhase", CardRecoveryPhases.None);
        command.Parameters.AddWithValue("$SupervisorPaid", ActiveSessionSupervisorResolutionCodes.ConfirmedPaid);
        command.Parameters.AddWithValue("$SupervisorNotPaid", ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid);
        command.Parameters.AddWithValue("$SupervisorRefunded", CardRefundSupervisorResolutionCodes.ConfirmedRefunded);
        command.Parameters.AddWithValue("$SupervisorNotRefunded", CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded);
        command.Parameters.AddWithValue("$SupervisorContinueWaiting", CardRefundSupervisorResolutionCodes.ContinueWaiting);

        var attempts = new List<LocalSquarePaymentAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            attempts.Add(ReadAttempt(reader));
        }

        return attempts;
    }

    // 展示历史严格限定门店、设备和环境，并限制200条；不参与金融恢复门禁。
    public async Task<IReadOnlyList<LocalSquarePaymentAttempt>> GetRecentAttemptsAsync(
        string storeCode, string deviceCode, string environment,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM LocalSquarePaymentAttempts
            WHERE StoreCode = $StoreCode AND DeviceCode = $DeviceCode AND Environment = $Environment
              AND OperationKind IN ('Sale', 'Refund', 'ActiveSession')
            ORDER BY UpdatedAt DESC, CreatedAt DESC LIMIT 200;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$Environment", environment);
        var attempts = new List<LocalSquarePaymentAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) attempts.Add(ReadAttempt(reader));
        return attempts;
    }

    public async Task<IReadOnlyList<LocalSquarePaymentAttempt>> GetRefundSettlementsAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // 结算跟踪只看退货单已完成的退款；未结退款仍走异常中心恢复，不在这里出现。
        command.CommandText = """
            SELECT * FROM LocalSquarePaymentAttempts
            WHERE StoreCode = $StoreCode AND DeviceCode = $DeviceCode AND Environment = $Environment
              AND OperationKind = 'Refund'
              AND Status = $OrderCompletedStatus
              AND UPPER(TRIM(COALESCE(PaymentStatus, ''))) IN ($Pending, $Rejected, $Failed)
            ORDER BY UpdatedAt DESC, CreatedAt DESC LIMIT 100;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$Environment", environment);
        command.Parameters.AddWithValue("$OrderCompletedStatus", LocalSquarePaymentAttemptStatus.OrderCompleted.ToString());
        command.Parameters.AddWithValue("$Pending", SquareRefundSettlementStatuses.Pending);
        command.Parameters.AddWithValue("$Rejected", SquareRefundSettlementStatuses.Rejected);
        command.Parameters.AddWithValue("$Failed", SquareRefundSettlementStatuses.Failed);
        var attempts = new List<LocalSquarePaymentAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) attempts.Add(ReadAttempt(reader));
        return attempts;
    }

    public async Task<bool> TryUpdateRefundSettlementStatusAsync(
        Guid attemptGuid,
        string expectedPaymentStatus,
        DateTimeOffset expectedUpdatedAt,
        string paymentStatus,
        string? responseText,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteUpdateCountAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET PaymentStatus = $PaymentStatus,
                ResponseText = COALESCE($ResponseText, ResponseText),
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid
              AND OperationKind = 'Refund'
              AND Status = $OrderCompletedStatus
              AND UPPER(TRIM(COALESCE(PaymentStatus, ''))) = $ExpectedPaymentStatus
              AND UpdatedAt = $ExpectedUpdatedAt;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$PaymentStatus", paymentStatus.Trim().ToUpperInvariant());
                command.Parameters.AddWithValue("$ResponseText", (object?)responseText ?? DBNull.Value);
                command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
                command.Parameters.AddWithValue("$OrderCompletedStatus", LocalSquarePaymentAttemptStatus.OrderCompleted.ToString());
                command.Parameters.AddWithValue("$ExpectedPaymentStatus", expectedPaymentStatus.Trim().ToUpperInvariant());
                command.Parameters.AddWithValue("$ExpectedUpdatedAt", expectedUpdatedAt.ToString("O"));
            },
            cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<LocalSquarePaymentAttempt>> GetOpenAttemptsAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Square 异常中心队列：同一终端/环境跨收银员列出全部未结 Sale 与 Refund。
        command.CommandText = """
            SELECT *
            FROM LocalSquarePaymentAttempts
            WHERE StoreCode = $StoreCode
              AND DeviceCode = $DeviceCode
              AND Environment = $Environment
              AND OperationKind IN ('Sale', 'Refund')
              AND (
                    Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
                    OR (
                        OperationKind = 'Refund'
                        AND Status = $FailedStatus
                        AND UPPER(TRIM(COALESCE(PaymentStatus, ''))) IN ('FAILED', 'REJECTED')
                        AND COALESCE(RecoveryPhase, $NoRecoveryPhase) = $NoRecoveryPhase
                        AND RecoveryTargetStatus IS NULL
                        AND NULLIF(TRIM(COALESCE(SubmissionToken, '')), '') IS NOT NULL
                        AND COALESCE(ResponseCode, '') NOT IN (
                            $SupervisorPaid,
                            $SupervisorNotPaid,
                            $SupervisorRefunded,
                            $SupervisorNotRefunded,
                            $SupervisorContinueWaiting)
                    )
                  )
            ORDER BY UpdatedAt DESC, CreatedAt DESC;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$Environment", environment);
        for (var i = 0; i < TerminalStatuses.Length; i++)
        {
            command.Parameters.AddWithValue($"$TerminalStatus{i + 1}", TerminalStatuses[i]);
        }
        command.Parameters.AddWithValue("$FailedStatus", LocalSquarePaymentAttemptStatus.Failed.ToString());
        command.Parameters.AddWithValue("$NoRecoveryPhase", CardRecoveryPhases.None);
        command.Parameters.AddWithValue("$SupervisorPaid", ActiveSessionSupervisorResolutionCodes.ConfirmedPaid);
        command.Parameters.AddWithValue("$SupervisorNotPaid", ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid);
        command.Parameters.AddWithValue("$SupervisorRefunded", CardRefundSupervisorResolutionCodes.ConfirmedRefunded);
        command.Parameters.AddWithValue("$SupervisorNotRefunded", CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded);
        command.Parameters.AddWithValue("$SupervisorContinueWaiting", CardRefundSupervisorResolutionCodes.ContinueWaiting);

        var attempts = new List<LocalSquarePaymentAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            attempts.Add(ReadAttempt(reader));
        }

        return attempts;
    }

    private async Task ExecuteUpdateAsync(
        string sql,
        Action<SqliteCommand> configure,
        CancellationToken cancellationToken)
    {
        await ExecuteUpdateCountAsync(sql, configure, cancellationToken);
    }

    private async Task ExecuteGuardedUpdateAsync(
        string sql,
        Action<SqliteCommand> configure,
        CancellationToken cancellationToken)
    {
        var affected = await ExecuteUpdateCountAsync(
            sql,
            command =>
            {
                configure(command);
                AddSupervisorResolvedCodeParameters(command);
            },
            cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException("支付 attempt 状态已变化，旧任务不得继续写入。");
        }
    }

    private async Task<bool> ExecuteRefundSubmissionUpdateAsync(
        string sql,
        Guid attemptGuid,
        string submissionToken,
        Action<SqliteCommand> configure,
        CancellationToken cancellationToken)
    {
        return await ExecuteUpdateCountAsync(
            sql,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$SubmissionToken", submissionToken);
                AddSupervisorResolvedCodeParameters(command);
                configure(command);
            },
            cancellationToken) == 1;
    }

    private async Task<int> ExecuteUpdateCountAsync(
        string sql,
        Action<SqliteCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddSupervisorResolvedCodeParameters(SqliteCommand command)
    {
        command.Parameters.AddWithValue("$ResolvedCode1", CardRefundSupervisorResolutionCodes.ConfirmedRefunded);
        command.Parameters.AddWithValue("$ResolvedCode2", CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded);
    }

    private static void AddAutomaticWriteGuardParameters(SqliteCommand command)
    {
        for (var i = 0; i < TerminalStatuses.Length; i++)
        {
            command.Parameters.AddWithValue($"$TerminalStatus{i + 1}", TerminalStatuses[i]);
        }

        command.Parameters.AddWithValue("$NoRecoveryPhase", CardRecoveryPhases.None);
        command.Parameters.AddWithValue("$FinalizePending", CardRecoveryPhases.FinalizePending);
        command.Parameters.AddWithValue("$SupervisorPaid", ActiveSessionSupervisorResolutionCodes.ConfirmedPaid);
        command.Parameters.AddWithValue("$SupervisorNotPaid", ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid);
        command.Parameters.AddWithValue("$SupervisorRefunded", CardRefundSupervisorResolutionCodes.ConfirmedRefunded);
        command.Parameters.AddWithValue("$SupervisorNotRefunded", CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded);
    }

    private static void AddAttemptParameters(SqliteCommand command, LocalSquarePaymentAttempt attempt)
    {
        command.Parameters.AddWithValue("$AttemptGuid", attempt.AttemptGuid.ToString());
        command.Parameters.AddWithValue("$CheckoutId", (object?)attempt.CheckoutId ?? DBNull.Value);
        command.Parameters.AddWithValue("$IdempotencyKey", attempt.IdempotencyKey);
        command.Parameters.AddWithValue("$DeviceId", attempt.DeviceId);
        command.Parameters.AddWithValue("$LocationId", attempt.LocationId);
        command.Parameters.AddWithValue("$Environment", attempt.Environment);
        command.Parameters.AddWithValue("$Amount", attempt.Amount);
        command.Parameters.AddWithValue("$AmountCents", attempt.AmountCents);
        command.Parameters.AddWithValue("$Currency", attempt.Currency);
        command.Parameters.AddWithValue("$Status", attempt.Status.ToString());
        command.Parameters.AddWithValue("$CheckoutStatus", (object?)attempt.CheckoutStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$CancelReason", (object?)attempt.CancelReason ?? DBNull.Value);
        // Square attempt 的草稿与 Linkly 分开存，避免 checkout_id 被误当 Linkly session 使用。
        command.Parameters.AddWithValue("$OrderDraftJson", attempt.OrderDraftJson);
        command.Parameters.AddWithValue("$StoreCode", attempt.StoreCode);
        command.Parameters.AddWithValue("$DeviceCode", attempt.DeviceCode);
        command.Parameters.AddWithValue("$CashierId", attempt.CashierId);
        command.Parameters.AddWithValue("$PaymentId", (object?)attempt.PaymentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$PaymentStatus", (object?)attempt.PaymentStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$ResponseCode", (object?)attempt.ResponseCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$ResponseText", (object?)attempt.ResponseText ?? DBNull.Value);
        command.Parameters.AddWithValue("$CreatedAt", attempt.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$UpdatedAt", attempt.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$CompletedAt", attempt.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$OrderCompletedAt", attempt.OrderCompletedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$ResolvedAt", attempt.ResolvedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$OperationKind", attempt.OperationKind);
        command.Parameters.AddWithValue("$OperationGuid", attempt.OperationGuid?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$SubmissionToken", (object?)attempt.SubmissionToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$RefundBusinessKey", (object?)attempt.RefundBusinessKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$RecoveryPhase", attempt.RecoveryPhase);
        command.Parameters.AddWithValue(
            "$RecoveryTargetStatus",
            attempt.RecoveryTargetStatus?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$SupervisorFinancialReference",
            (object?)attempt.SupervisorFinancialReference ?? DBNull.Value);
    }

    private static string BuildResolutionText(CardRefundAttemptResolution resolution)
    {
        var evidence = Normalize(resolution.Evidence);
        return evidence is null
            ? resolution.Reason
            : $"{resolution.Reason} Evidence: {evidence}";
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static LocalSquarePaymentAttempt ReadAttempt(SqliteDataReader reader)
    {
        return new LocalSquarePaymentAttempt(
            ReadGuid(reader, "AttemptGuid"),
            ReadNullableString(reader, "CheckoutId"),
            ReadString(reader, "IdempotencyKey"),
            ReadString(reader, "DeviceId"),
            ReadString(reader, "LocationId"),
            ReadString(reader, "Environment"),
            ReadDecimal(reader, "Amount"),
            ReadInt64(reader, "AmountCents"),
            ReadString(reader, "Currency"),
            Enum.Parse<LocalSquarePaymentAttemptStatus>(ReadString(reader, "Status")),
            ReadNullableString(reader, "CheckoutStatus"),
            ReadNullableString(reader, "CancelReason"),
            ReadString(reader, "OrderDraftJson"),
            ReadString(reader, "StoreCode"),
            ReadString(reader, "DeviceCode"),
            ReadString(reader, "CashierId"),
            ReadNullableString(reader, "PaymentId"),
            ReadNullableString(reader, "PaymentStatus"),
            ReadNullableString(reader, "ResponseCode"),
            ReadNullableString(reader, "ResponseText"),
            ReadDateTimeOffset(reader, "CreatedAt"),
            ReadDateTimeOffset(reader, "UpdatedAt"),
            ReadNullableDateTimeOffset(reader, "CompletedAt"),
            ReadNullableDateTimeOffset(reader, "OrderCompletedAt"),
            ReadNullableDateTimeOffset(reader, "ResolvedAt"),
            ReadNullableString(reader, "OperationKind") ?? "Sale",
            ReadNullableGuid(reader, "OperationGuid"),
            ReadNullableString(reader, "SubmissionToken"),
            ReadNullableString(reader, "RefundBusinessKey"),
            ReadNullableString(reader, "RecoveryPhase") ?? CardRecoveryPhases.None,
            ReadNullableEnum<LocalSquarePaymentAttemptStatus>(reader, "RecoveryTargetStatus"),
            ReadNullableString(reader, "SupervisorFinancialReference"));
    }

    private static Guid ReadGuid(SqliteDataReader reader, string name) => Guid.Parse(ReadString(reader, name));

    private static string ReadString(SqliteDataReader reader, string name) => reader.GetString(reader.GetOrdinal(name));

    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static decimal ReadDecimal(SqliteDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value switch
        {
            decimal decimalValue => decimalValue,
            double doubleValue => Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture),
            long longValue => longValue,
            int intValue => intValue,
            string stringValue => decimal.Parse(stringValue, CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };
    }

    private static long ReadInt64(SqliteDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            string stringValue => long.Parse(stringValue, CultureInfo.InvariantCulture),
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
        };
    }

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, string name)
    {
        return DateTimeOffset.Parse(ReadString(reader, name), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    private static Guid? ReadNullableGuid(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value) ? null : Guid.Parse(value);
    }

    private static TEnum? ReadNullableEnum<TEnum>(SqliteDataReader reader, string name)
        where TEnum : struct, Enum
    {
        var value = ReadNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value) ? null : Enum.Parse<TEnum>(value);
    }
}
