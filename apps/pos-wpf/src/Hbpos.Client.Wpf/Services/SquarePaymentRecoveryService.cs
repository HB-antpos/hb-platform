using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface ISquarePaymentRecoveryService
{
    Task<CardPaymentRecoveryResult> RecoverLatestAsync(
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default);

    Task<CardRefundSupervisorResolutionResult> ResolveRefundAsync(
        CardRefundSupervisorResolution resolution,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CardRefundSupervisorResolutionResult(
            false,
            "Square refund supervisor resolution is unavailable."));

    Task<IReadOnlyList<CardRecoveryQueueItem>> ListHistoryAsync(
        PosSessionState session, CancellationToken cancellationToken = default) =>
        ListOpenAsync(session, cancellationToken);

    Task<IReadOnlyList<CardRecoveryQueueItem>> ListOpenAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default);

    Task<CardPaymentRecoveryResult> RecoverAttemptAsync(
        Guid attemptGuid,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default);

    Task<CardRecoveryResolutionResult> ResolveAttemptAsync(
        Guid attemptGuid,
        CardRecoverySupervisorDecision decision,
        string reason,
        string? evidence,
        string? reference,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default);
}

public sealed class SquarePaymentRecoveryService(
    ILocalSquarePaymentAttemptRepository attemptRepository,
    ICardTerminalSettingsProvider settingsProvider,
    ISquareTerminalPaymentClient squareTerminalPaymentClient,
    CashCheckoutService checkout,
    ILocalOrderRepository orderRepository,
    ILocalizationService? localization = null,
    FinancialSupervisorAuditReplayService? supervisorAuditReplay = null,
    ISharedHeldOrderRepository? sharedHeldOrderRepository = null,
    ISquareRefundSettlementService? refundSettlementService = null) : ISquarePaymentRecoveryService
{
    /// <summary>
    /// 退货单已完成、退款仍在 Square 结算（PENDING）。只用于列表展示，不是数据库状态枚举。
    /// </summary>
    internal const string SettlementPendingStatus = "SettlementPending";

    /// <summary>退货单已完成，但 Square 事后拒绝/失败了这笔退款，需要主管改用其他方式退给顾客。</summary>
    internal const string SettlementRejectedStatus = "SettlementRejected";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static bool IsSettlementRefund(LocalSquarePaymentAttempt attempt) =>
        attempt.Status == LocalSquarePaymentAttemptStatus.OrderCompleted &&
        string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase);

    // 把退款结算状态映射成列表状态；其余记录保持原有状态文本。
    private static string DisplayStatus(LocalSquarePaymentAttempt attempt)
    {
        if (IsSettlementRefund(attempt))
        {
            if (SquareRefundSettlementStatuses.IsPending(attempt.PaymentStatus))
            {
                return SettlementPendingStatus;
            }

            if (SquareRefundSettlementStatuses.IsUnhandledFailure(attempt.PaymentStatus))
            {
                return SettlementRejectedStatus;
            }
        }

        return string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal)
            ? CardRecoveryPhases.FinalizePending
            : attempt.Status.ToString();
    }

    private async Task<IReadOnlyList<LocalSquarePaymentAttempt>> GetSettlementFailuresAsync(
        PosSessionState session,
        string environment,
        CancellationToken cancellationToken)
    {
        var settlements = await RunLocalStoreAsync(
            () => attemptRepository.GetRefundSettlementsAsync(
                session.StoreCode,
                session.DeviceCode,
                environment,
                cancellationToken),
            cancellationToken);
        return settlements
            .Where(item => SquareRefundSettlementStatuses.IsUnhandledFailure(item.PaymentStatus))
            .ToArray();
    }

    private readonly ISharedHeldOrderPaymentSourceResolver? _heldOrderPaymentSourceResolver =
        sharedHeldOrderRepository is null
            ? null
            : new SharedHeldOrderPaymentSourceResolver(
                sharedHeldOrderRepository,
                new SharedHeldOrderReverseMapper());

    private async Task<LocalHeldOrderCompletionContext?> TryResolveHeldOrderAsync(
        PosSessionState session,
        PosCartSnapshot cartSnapshot,
        CancellationToken cancellationToken)
    {
        if (_heldOrderPaymentSourceResolver is null)
        {
            // 来源解析器缺失时，已显式绑定共享 claim 的购物车绝不能静默降级为普通订单。
            if (cartSnapshot.SharedHeldOrderClaimId is not null)
            {
                throw new InvalidDataException(
                    "共享挂单购物车 binding 但来源解析器不可用，拒绝降级为普通订单。");
            }

            return null;
        }

        // 恢复路径必须使用 payment draft 中冻结的 CartSnapshot，不能用当前 UI 购物车。
        return await _heldOrderPaymentSourceResolver.TryResolveAsync(
            session,
            cartSnapshot,
            cancellationToken);
    }

    public async Task<CardPaymentRecoveryResult> RecoverLatestAsync(
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        if (settings.Processor != CardProcessorKind.Square)
        {
            return CardPaymentRecoveryResult.None;
        }

        var storeCode = session.StoreCode;
        var deviceCode = session.DeviceCode;
        var environment = settings.Environment.ToString();
        var refundAttempts = await RunLocalStoreAsync(
            () => attemptRepository.GetOpenRefundAttemptsAsync(
                storeCode,
                deviceCode,
                environment,
                cancellationToken),
            cancellationToken);
        var refundAttempt = refundAttempts.FirstOrDefault();
        if (refundAttempt is not null)
        {
            return await RecoverRefundAttemptAsync(
                cart,
                session,
                settings,
                refundAttempt,
                cancellationToken);
        }

        var attempt = await RunLocalStoreAsync(
            () => attemptRepository.GetLatestOpenSaleAttemptForTerminalAsync(
                storeCode,
                deviceCode,
                environment,
                cancellationToken),
            cancellationToken);
        if (attempt is null)
        {
            return CardPaymentRecoveryResult.None;
        }

        return await RecoverSaleAttemptAsync(cart, session, settings, attempt, cancellationToken);
    }

    public async Task<IReadOnlyList<CardRecoveryQueueItem>> ListHistoryAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var attempts = await RunLocalStoreAsync(
            () => attemptRepository.GetRecentAttemptsAsync(
                session.StoreCode,
                session.DeviceCode,
                settings.Environment.ToString(),
                cancellationToken),
            cancellationToken);
        // 未结记录不受历史条数上限影响；历史数据只用于展示，不能恢复已关闭交易。
        var open = await ListOpenAsync(session, cancellationToken);
        var openKeys = open.Select(item => item.Key).ToHashSet();
        var recent = attempts
            .Select(ToQueueItem)
            .Select(item => item with { IsOpen = openKeys.Contains(item.Key) })
            .ToArray();
        return open.Concat(recent.Where(item => !openKeys.Contains(item.Key)))
            .OrderByDescending(item => item.UpdatedAt).ToArray();
    }

    public async Task<IReadOnlyList<CardRecoveryQueueItem>> ListOpenAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var attempts = await RunLocalStoreAsync(
            () => attemptRepository.GetOpenAttemptsAsync(
                session.StoreCode,
                session.DeviceCode,
                settings.Environment.ToString(),
                cancellationToken),
            cancellationToken);
        // 退货单已完成但 Square 事后拒绝的退款也需要主管处理（改用其他方式退给顾客），一并列入待处理。
        var settlementFailures = await GetSettlementFailuresAsync(
            session,
            settings.Environment.ToString(),
            cancellationToken);
        return attempts
            .Concat(settlementFailures)
            .Select(ToQueueItem)
            .OrderByDescending(item => item.UpdatedAt)
            .ToArray();
    }

    private static CardRecoveryQueueItem ToQueueItem(LocalSquarePaymentAttempt attempt) =>
        new(
            CardProcessorKind.Square,
            attempt.AttemptGuid,
            attempt.OperationKind,
            attempt.Amount,
            attempt.StoreCode,
            attempt.DeviceCode,
            attempt.CashierId,
            attempt.Environment,
            DisplayStatus(attempt),
            attempt.CreatedAt,
            attempt.UpdatedAt,
            attempt.OrderDraftJson,
            null,
            null,
            attempt.CheckoutId,
            attempt.ResponseCode,
            attempt.ResponseText,
            null,
            attempt.PaymentId,
            attempt.OperationGuid,
            attempt.PaymentStatus);

    public async Task<CardPaymentRecoveryResult> RecoverAttemptAsync(
        Guid attemptGuid,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var attempt = await RunLocalStoreAsync(
            () => attemptRepository.GetAttemptAsync(attemptGuid, cancellationToken),
            cancellationToken);
        if (attempt is null ||
            !string.Equals(attempt.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.Environment, settings.Environment.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return CardPaymentRecoveryResult.None;
        }

        // 退货单已完成的退款：只查询同一笔退款的结算结果，绝不重新发起退款，也不再改动退货单。
        if (IsSettlementRefund(attempt) &&
            (SquareRefundSettlementStatuses.IsPending(attempt.PaymentStatus) ||
             SquareRefundSettlementStatuses.IsUnhandledFailure(attempt.PaymentStatus)))
        {
            return await RecoverRefundSettlementAsync(attempt, session, cancellationToken);
        }

        // 普通终态不可再恢复；唯一例外是旧版本/崩溃留下的精确退款失败中间态，
        // 它只允许进入本地 CAS 修复，不能重新调用 Square 金融 API。
        if (IsTerminalSquareStatus(attempt.Status) &&
            !IsUnfinalizedTerminalRefundFailure(attempt))
        {
            return CardPaymentRecoveryResult.None;
        }

        if (string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase))
        {
            return await RecoverRefundAttemptAsync(cart, session, settings, attempt, cancellationToken);
        }

        return await RecoverSaleAttemptAsync(cart, session, settings, attempt, cancellationToken);
    }

    public async Task<CardRecoveryResolutionResult> ResolveAttemptAsync(
        Guid attemptGuid,
        CardRecoverySupervisorDecision decision,
        string reason,
        string? evidence,
        string? reference,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var attempt = await RunLocalStoreAsync(
            () => attemptRepository.GetAttemptAsync(attemptGuid, cancellationToken),
            cancellationToken);
        if (attempt is null ||
            !string.Equals(attempt.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.Environment, settings.Environment.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return new CardRecoveryResolutionResult(
                false,
                "The unresolved attempt no longer matches this terminal and cannot be changed.");
        }

        if (IsSettlementRefund(attempt) && SquareRefundSettlementStatuses.IsUnhandledFailure(attempt.PaymentStatus))
        {
            return await AcknowledgeSettlementFailureAsync(attempt, decision, reason, evidence, session, cancellationToken);
        }

        if (string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase))
        {
            var refundResult = await ResolveRefundAsync(
                new CardRefundSupervisorResolution(
                    attempt.AttemptGuid,
                    CardProcessorKind.Square,
                    MapRefundDecision(decision),
                    reason,
                    evidence,
                    reference),
                cart,
                session,
                cancellationToken);
            return new CardRecoveryResolutionResult(
                refundResult.Succeeded,
                refundResult.Message,
                refundResult.RecoveryResult,
                refundResult.RetryAllowed,
                refundResult.LockRetained,
                refundResult.ResolutionPersisted,
                refundResult.ResolutionApplied);
        }

        return await ResolveSquareSaleAsync(
            attempt,
            decision,
            reason,
            evidence,
            reference,
            cart,
            session,
            cancellationToken);
    }

    private async Task<CardPaymentRecoveryResult> RecoverRefundSettlementAsync(
        LocalSquarePaymentAttempt attempt,
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        var details = BuildRefundDialogDetails(attempt);
        if (SquareRefundSettlementStatuses.IsUnhandledFailure(attempt.PaymentStatus))
        {
            // Square 已拒绝：状态已固化，不再查询；提示主管改用其他方式退给顾客并在人工核对里确认。
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.settlement.rejected", "Square did not complete this refund. Refund the customer by cash or another method, then confirm it here."),
                DialogDetails: details,
                RefundDetails: BuildRefundDetails(attempt));
        }

        if (refundSettlementService is null)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Checking,
                T("cardRecovery.settlement.pending", "Square accepted this refund and is still settling it. No action is needed; the status updates automatically."),
                DialogDetails: details,
                RefundDetails: BuildRefundDetails(attempt));
        }

        var result = await refundSettlementService.CheckPendingAsync(session, attempt.AttemptGuid, cancellationToken);
        if (result.Completed > 0)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Checking,
                T("cardRecovery.settlement.completed", "Square completed the refund. The money is on its way to the customer's card."),
                DialogDetails: details,
                RefundDetails: BuildRefundDetails(attempt));
        }

        if (result.Failed > 0)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.settlement.rejected", "Square did not complete this refund. Refund the customer by cash or another method, then confirm it here."),
                DialogDetails: details,
                RefundDetails: BuildRefundDetails(attempt));
        }

        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Checking,
            T("cardRecovery.settlement.pending", "Square accepted this refund and is still settling it. No action is needed; the status updates automatically."),
            DialogDetails: details,
            RefundDetails: BuildRefundDetails(attempt));
    }

    private async Task<CardRecoveryResolutionResult> AcknowledgeSettlementFailureAsync(
        LocalSquarePaymentAttempt attempt,
        CardRecoverySupervisorDecision decision,
        string reason,
        string? evidence,
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        if (decision != CardRecoverySupervisorDecision.ConfirmProcessed)
        {
            return new CardRecoveryResolutionResult(
                false,
                T("cardRecovery.settlement.onlyAcknowledge", "Square rejected this refund after the return was completed. Only \"Refunded by other means\" applies."),
                LockRetained: true);
        }

        if (refundSettlementService is null)
        {
            return new CardRecoveryResolutionResult(false, "Refund settlement tracking is unavailable.", LockRetained: true);
        }

        var note = string.Join(
            " ",
            new[] { reason, evidence }.Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text!.Trim()));
        var acknowledged = await refundSettlementService.AcknowledgeFailureAsync(
            attempt.AttemptGuid,
            session,
            note,
            cancellationToken);
        return acknowledged
            ? new CardRecoveryResolutionResult(
                true,
                T("cardRecovery.settlement.acknowledged", "Recorded: the customer was refunded by other means."),
                ResolutionPersisted: true,
                ResolutionApplied: true)
            : new CardRecoveryResolutionResult(
                false,
                T("cardRecovery.settlement.acknowledgeFailed", "The refund status changed. Refresh and try again."),
                LockRetained: true);
    }

    private async Task<CardRecoveryResolutionResult> ResolveSquareSaleAsync(
        LocalSquarePaymentAttempt attempt,
        CardRecoverySupervisorDecision decision,
        string reason,
        string? evidence,
        string? reference,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
        var normalizedEvidence = string.IsNullOrWhiteSpace(evidence) ? null : evidence.Trim();
        var normalizedReference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();

        var verifiedPaymentReference = attempt.PaymentId ?? attempt.SupervisorFinancialReference;
        var hasVerifiedPayment =
            attempt.Status == LocalSquarePaymentAttemptStatus.PaymentVerified &&
            !string.IsNullOrWhiteSpace(attempt.PaymentId) &&
            !string.IsNullOrWhiteSpace(attempt.PaymentStatus);
        var hasPendingOrderFinalization =
            string.Equals(
                attempt.RecoveryPhase,
                CardRecoveryPhases.FinalizePending,
                StringComparison.Ordinal) &&
            attempt.RecoveryTargetStatus == LocalSquarePaymentAttemptStatus.OrderCompleted &&
            !string.IsNullOrWhiteSpace(verifiedPaymentReference);
        if (hasVerifiedPayment || hasPendingOrderFinalization)
        {
            // Square 已有真实付款或精确 FinalizePending owner 时，主管按钮不能把它改写为未付款。
            var verifiedDraft = TryDeserializeDraft(attempt);
            var verifiedCompletion = verifiedDraft is null
                ? new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    UnknownResultMessage())
                : await CompleteVerifiedAttemptAsync(
                    attempt,
                    verifiedDraft,
                    verifiedPaymentReference!,
                    attempt.PaymentStatus ?? ActiveSessionSupervisorResolutionCodes.ConfirmedPaid,
                    cardBrand: null,
                    maskedCardNumber: null,
                    authCode: null,
                    CancellationToken.None);
            var verifiedCompletionSucceeded =
                verifiedCompletion.Outcome == CardPaymentRecoveryOutcome.OrderCompleted;
            return new CardRecoveryResolutionResult(
                verifiedCompletionSucceeded,
                verifiedCompletionSucceeded ? verifiedCompletion.Message : ResolutionPendingMessage(),
                verifiedCompletion,
                LockRetained: await IsResolutionLockRetainedAsync(
                    attempt.AttemptGuid,
                    verifiedCompletionSucceeded),
                ResolutionPersisted: IsPersistedSquareSupervisorResolution(attempt));
        }

        if (IsTerminalSquareStatus(attempt.Status) ||
            !string.IsNullOrWhiteSpace(attempt.PaymentId) ||
            !string.IsNullOrWhiteSpace(attempt.PaymentStatus) ||
            string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal))
        {
            return new CardRecoveryResolutionResult(
                false,
                "The Square payment already has a newer financial result and cannot be changed.",
                LockRetained: !IsTerminalSquareStatus(attempt.Status),
                ResolutionPersisted: IsPersistedSquareSupervisorResolution(attempt));
        }

        if (decision == CardRecoverySupervisorDecision.ConfirmProcessed && normalizedReference is null)
        {
            return new CardRecoveryResolutionResult(
                false,
                "Enter the real bank or terminal payment reference before confirming payment.",
                LockRetained: true);
        }

        if (decision == CardRecoverySupervisorDecision.ConfirmNotProcessed && normalizedEvidence is null)
        {
            return new CardRecoveryResolutionResult(
                false,
                "Enter bank evidence confirming that no payment was processed.",
                LockRetained: true);
        }

        var resolvedAt = DateTimeOffset.UtcNow;
        var journal = BuildSquareSaleSupervisorJournal(
            attempt,
            decision,
            normalizedReason,
            normalizedEvidence,
            normalizedReference,
            session,
            resolvedAt);
        var applied = await RunLocalStoreAsync(
            () => attemptRepository.ResolvePaymentWithJournalAsync(
                new SquarePaymentResolution(
                    attempt.AttemptGuid,
                    decision,
                    normalizedReason,
                    normalizedEvidence,
                    normalizedReference,
                    attempt.Status,
                    attempt.UpdatedAt,
                    resolvedAt),
                journal,
                CancellationToken.None),
            CancellationToken.None);
        if (!applied)
        {
            var winner = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                CancellationToken.None);
            if (winner is not null &&
                string.Equals(
                    winner.ResponseCode,
                    ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid,
                    StringComparison.Ordinal))
            {
                var restoredWinner = await RunPersistedResolutionRecoveryAsync(
                    winner.AttemptGuid,
                    () => RecoverSupervisorNotPaidSaleAsync(cart, winner, CancellationToken.None));
                var restoredSucceeded = restoredWinner.Outcome == CardPaymentRecoveryOutcome.DraftRestored;
                var lockRetained = await IsResolutionLockRetainedAsync(winner.AttemptGuid, restoredSucceeded);
                return new CardRecoveryResolutionResult(
                    restoredSucceeded,
                    restoredSucceeded ? restoredWinner.Message : ResolutionPendingMessage(),
                    restoredWinner,
                    // RetryAllowed 专用于已发布的原退货草稿；销售草稿恢复只由 Succeeded 表示。
                    RetryAllowed: false,
                    LockRetained: lockRetained,
                    ResolutionPersisted: true);
            }

            if (winner is not null &&
                string.Equals(
                    winner.ResponseCode,
                    ActiveSessionSupervisorResolutionCodes.ConfirmedPaid,
                    StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(winner.SupervisorFinancialReference))
            {
                var winnerDraft = TryDeserializeDraft(winner);
                var completedWinner = winnerDraft is null
                    ? new CardPaymentRecoveryResult(
                        CardPaymentRecoveryOutcome.Unknown,
                        ResolutionPendingMessage())
                    : await RunPersistedResolutionRecoveryAsync(
                        winner.AttemptGuid,
                        () => CompleteVerifiedAttemptAsync(
                            winner,
                            winnerDraft,
                            winner.SupervisorFinancialReference,
                            ActiveSessionSupervisorResolutionCodes.ConfirmedPaid,
                            cardBrand: null,
                            maskedCardNumber: null,
                            authCode: null,
                            CancellationToken.None));
                var winnerSucceeded = completedWinner.Outcome == CardPaymentRecoveryOutcome.OrderCompleted;
                var lockRetained = await IsResolutionLockRetainedAsync(winner.AttemptGuid, winnerSucceeded);
                return new CardRecoveryResolutionResult(
                    winnerSucceeded,
                    winnerSucceeded ? completedWinner.Message : ResolutionPendingMessage(),
                    completedWinner,
                    LockRetained: lockRetained,
                    ResolutionPersisted: true);
            }

            if (winner is not null &&
                string.Equals(
                    winner.ResponseCode,
                    ActiveSessionSupervisorResolutionCodes.ContinueWaiting,
                    StringComparison.Ordinal))
            {
                return new CardRecoveryResolutionResult(
                    true,
                    T("cardRecovery.square.supervisorWaiting", "The Square payment remains locked. Run recovery again after the bank result is available."),
                    LockRetained: true,
                    ResolutionPersisted: true);
            }

            return new CardRecoveryResolutionResult(
                false,
                "The Square payment state changed before the supervisor decision was saved. The winning state was retained.",
                LockRetained: winner is null || !IsTerminalSquareStatus(winner.Status),
                ResolutionPersisted: winner is not null && IsPersistedSquareSupervisorResolution(winner));
        }

        if (supervisorAuditReplay is not null)
        {
            try
            {
                await supervisorAuditReplay.PersistAfterCommitAsync(journal, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                TryWriteRecoveryLog(
                    $"supervisor payment audit replay failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                    ex,
                    attempt.AttemptGuid,
                    error: false);
            }
        }

        LocalSquarePaymentAttempt? updatedAttempt;
        try
        {
            updatedAttempt = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWriteRecoveryLog(
                $"supervisor payment post-commit read failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                ex,
                attempt.AttemptGuid,
                error: false);
            return new CardRecoveryResolutionResult(
                false,
                ResolutionPendingMessage(),
                LockRetained: true,
                ResolutionPersisted: true,
                ResolutionApplied: true);
        }

        if (updatedAttempt is null)
        {
            TryWriteRecoveryLog(
                $"supervisor payment post-commit read missing attemptGuid={attempt.AttemptGuid}");
            return new CardRecoveryResolutionResult(
                false,
                ResolutionPendingMessage(),
                LockRetained: true,
                ResolutionPersisted: true,
                ResolutionApplied: true);
        }

        if (decision == CardRecoverySupervisorDecision.ContinueWaiting)
        {
            return new CardRecoveryResolutionResult(
                true,
                T("cardRecovery.square.supervisorWaiting", "The Square payment remains locked. Run recovery again after the bank result is available."),
                LockRetained: true,
                ResolutionPersisted: true,
                ResolutionApplied: true);
        }

        if (decision == CardRecoverySupervisorDecision.ConfirmNotProcessed)
        {
            var restored = await RunPersistedResolutionRecoveryAsync(
                updatedAttempt.AttemptGuid,
                () => RecoverSupervisorNotPaidSaleAsync(cart, updatedAttempt, CancellationToken.None));
            var restoredSucceeded = restored.Outcome == CardPaymentRecoveryOutcome.DraftRestored;
            var lockRetained = await IsResolutionLockRetainedAsync(
                updatedAttempt.AttemptGuid,
                restoredSucceeded);
            return new CardRecoveryResolutionResult(
                restoredSucceeded,
                restoredSucceeded ? restored.Message : ResolutionPendingMessage(),
                restored,
                RetryAllowed: false,
                LockRetained: lockRetained,
                ResolutionPersisted: true,
                ResolutionApplied: true);
        }

        // ConfirmProcessed：用持久化完整 draft 独立完成订单，绝不触碰当前活动新购物车。
        var updatedDraft = TryDeserializeDraft(updatedAttempt);
        var completed = updatedDraft is null ||
            string.IsNullOrWhiteSpace(updatedAttempt.SupervisorFinancialReference)
            ? new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ResolutionPendingMessage())
            : await RunPersistedResolutionRecoveryAsync(
                updatedAttempt.AttemptGuid,
                () => CompleteVerifiedAttemptAsync(
                    updatedAttempt,
                    updatedDraft,
                    updatedAttempt.SupervisorFinancialReference,
                    ActiveSessionSupervisorResolutionCodes.ConfirmedPaid,
                    cardBrand: null,
                    maskedCardNumber: null,
                    authCode: null,
                    CancellationToken.None));
        var completedSucceeded = completed.Outcome == CardPaymentRecoveryOutcome.OrderCompleted;
        var completedLockRetained = await IsResolutionLockRetainedAsync(
            updatedAttempt.AttemptGuid,
            completedSucceeded);
        return new CardRecoveryResolutionResult(
            completedSucceeded,
            completedSucceeded ? completed.Message : ResolutionPendingMessage(),
            completed,
            LockRetained: completedLockRetained,
            ResolutionPersisted: true,
            ResolutionApplied: true);
    }

    private static bool IsSupervisorNotPaidSale(LocalSquarePaymentAttempt attempt) =>
        string.Equals(attempt.OperationKind, "Sale", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(attempt.ResponseCode, ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid, StringComparison.Ordinal);

    private static bool IsPersistedSquareSupervisorResolution(LocalSquarePaymentAttempt attempt)
    {
        // OperationKind 与 supervisor code 不一致也必须失败关闭；自动 CAS 同样拒绝全部主管金融结论。
        return string.Equals(attempt.ResponseCode, ActiveSessionSupervisorResolutionCodes.ConfirmedPaid, StringComparison.Ordinal) ||
            string.Equals(attempt.ResponseCode, ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid, StringComparison.Ordinal) ||
            string.Equals(attempt.ResponseCode, ActiveSessionSupervisorResolutionCodes.ContinueWaiting, StringComparison.Ordinal) ||
            string.Equals(attempt.ResponseCode, CardRefundSupervisorResolutionCodes.ConfirmedRefunded, StringComparison.Ordinal) ||
            string.Equals(attempt.ResponseCode, CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded, StringComparison.Ordinal) ||
            string.Equals(attempt.ResponseCode, CardRefundSupervisorResolutionCodes.ContinueWaiting, StringComparison.Ordinal);
    }

    private static bool IsTerminalSquareStatus(LocalSquarePaymentAttemptStatus status) =>
        status is LocalSquarePaymentAttemptStatus.Canceled or
            LocalSquarePaymentAttemptStatus.TimedOut or
            LocalSquarePaymentAttemptStatus.Failed or
            LocalSquarePaymentAttemptStatus.OrderCompleted or
            LocalSquarePaymentAttemptStatus.Abandoned;

    private string ResolutionPendingMessage() =>
        T(
            "cardRecovery.resolutionSavedPending",
            "The supervisor decision was saved, but recovery is still pending. Run recovery again before taking another payment or refund.");

    private async Task<CardPaymentRecoveryResult> RunPersistedResolutionRecoveryAsync(
        Guid attemptGuid,
        Func<Task<CardPaymentRecoveryResult>> recoveryAsync)
    {
        try
        {
            return await recoveryAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"persisted supervisor resolution recovery failed attemptGuid={attemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ResolutionPendingMessage());
        }
    }

    private async Task<bool> IsResolutionLockRetainedAsync(
        Guid attemptGuid,
        bool succeeded)
    {
        if (!succeeded)
        {
            return true;
        }

        try
        {
            var current = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
                CancellationToken.None);
            return current is null ||
                string.Equals(
                    current.RecoveryPhase,
                    CardRecoveryPhases.FinalizePending,
                    StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"supervisor resolution lock check failed attemptGuid={attemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attemptGuid.ToString("D")),
                ex);
            return true;
        }
    }

    private async Task<LocalSquarePaymentAttempt?> EnsureRecoveryFinalizationAsync(
        LocalSquarePaymentAttempt attempt,
        LocalSquarePaymentAttemptStatus targetStatus,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                attempt.RecoveryPhase,
                CardRecoveryPhases.FinalizePending,
                StringComparison.Ordinal))
        {
            return attempt.RecoveryTargetStatus == targetStatus ? attempt : null;
        }

        if (attempt.Status == targetStatus && IsTerminalSquareStatus(attempt.Status))
        {
            return attempt;
        }

        var phaseStartedAt = DateTimeOffset.UtcNow;
        var started = await RunLocalStoreAsync(
            () => attemptRepository.TryBeginRecoveryFinalizationAsync(
                attempt.AttemptGuid,
                attempt.Status,
                attempt.UpdatedAt,
                targetStatus,
                phaseStartedAt,
                CancellationToken.None),
            CancellationToken.None);
        if (started)
        {
            return attempt with
            {
                RecoveryPhase = CardRecoveryPhases.FinalizePending,
                RecoveryTargetStatus = targetStatus,
                UpdatedAt = phaseStartedAt
            };
        }

        var winner = await RunLocalStoreAsync(
            () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
            CancellationToken.None);
        return winner is not null &&
            (winner.Status == targetStatus ||
             (string.Equals(
                  winner.RecoveryPhase,
                  CardRecoveryPhases.FinalizePending,
                  StringComparison.Ordinal) &&
              winner.RecoveryTargetStatus == targetStatus))
            ? winner
            : null;
    }

    private async Task<bool> CompleteRecoveryFinalizationAsync(
        LocalSquarePaymentAttempt attempt,
        LocalSquarePaymentAttemptStatus targetStatus,
        CancellationToken cancellationToken)
    {
        try
        {
            if (attempt.Status == targetStatus && IsTerminalSquareStatus(attempt.Status))
            {
                return true;
            }

            var completedAt = DateTimeOffset.UtcNow;
            var completed = await RunLocalStoreAsync(
                () => attemptRepository.TryCompleteRecoveryFinalizationAsync(
                    attempt.AttemptGuid,
                    attempt.Status,
                    attempt.UpdatedAt,
                    targetStatus,
                    completedAt,
                    CancellationToken.None),
                CancellationToken.None);
            if (completed)
            {
                return true;
            }

            var winner = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                CancellationToken.None);
            return winner?.Status == targetStatus &&
                !string.Equals(
                    winner.RecoveryPhase,
                    CardRecoveryPhases.FinalizePending,
                    StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "SquareRecovery",
                $"recovery finalization failed attemptGuid={attempt.AttemptGuid} target={targetStatus} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return false;
        }
    }

    private async Task<CardPaymentRecoveryResult> RecoverSupervisorNotPaidSaleAsync(
        PosCartService cart,
        LocalSquarePaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        // 在第一次 await 前固定 provider + attempt owner；后续重启/交接只能操作这条销售恢复。
        var attemptKey = new CardRecoveryAttemptKey(CardProcessorKind.Square, attempt.AttemptGuid);
        if (!cart.IsEmpty)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.square.notPaidCartNotEmpty", "The previous Square payment was confirmed not paid, but the current cart is not empty. Clear the current cart before restoring the original order."));
        }

        var expectedCartRevision = cart.Revision;

        var draft = TryDeserializeDraft(attempt);
        if (draft is null)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.square.notPaidDraftInvalid", "The Square payment draft is invalid and cannot be restored."));
        }

        LocalSquarePaymentAttempt? finalizePending;
        try
        {
            finalizePending = await EnsureRecoveryFinalizationAsync(
                attempt,
                LocalSquarePaymentAttemptStatus.Abandoned,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"not-paid finalization prepare failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.square.notPaidTerminalizeFailed", "The previous Square payment could not be finalized. Run recovery again."));
        }

        if (finalizePending is null)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.square.notPaidTerminalizeFailed", "The previous Square payment could not be finalized. Run recovery again."));
        }

        PosCartRecoveryPublicationResult publication;
        try
        {
            publication = cart.TryPublishRecoverySnapshot(
                attemptKey,
                expectedCartRevision,
                draft.CartSnapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"not-paid restore failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.square.notPaidRestoreFailed", "The previous Square payment could not be restored. Run recovery again."));
        }

        if (!publication.Succeeded)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.square.notPaidRestoreFailed", "The previous Square payment could not be restored. Run recovery again."));
        }

        // DraftRestored 只是活动购物车已发布；UI 交接前必须保留 FinalizePending 和精确 owner，
        // 这样进程退出后可从本地 draft 重放，而不会再次查询或提交 Square。
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.DraftRestored,
            T("cardRecovery.square.notPaidRetryAllowed", "The bank confirmed that no payment was processed. The original order is ready to retry with the same operation."),
            TenderedAmount: draft.CurrentTenders.Sum(tender => tender.Amount),
            RestoredTenders: draft.CurrentTenders,
            HasPostCommitWarning: publication.NotificationWarning)
        {
            DraftHandoffKey = attemptKey
        };
    }

    private async Task<CardPaymentRecoveryResult> RestoreCanceledSaleAsync(
        PosCartService cart,
        LocalSquarePaymentAttempt attempt,
        CardPaymentOrderDraft draft,
        string? cancelReason,
        CancellationToken cancellationToken)
    {
        // 取消证据必须先落到本地，才能在交接前重启后拒绝再次 Square GET。
        var attemptKey = new CardRecoveryAttemptKey(CardProcessorKind.Square, attempt.AttemptGuid);
        if (!IsCanceledSaleDraftHandoff(attempt))
        {
            if (string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal))
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    UnknownResultMessage());
            }

            var evidenceAt = DateTimeOffset.UtcNow;
            var evidencePersisted = await RunLocalStoreAsync(
                () => attemptRepository.TryUpdateCheckoutStatusAsync(
                    attempt.AttemptGuid,
                    attempt.Status,
                    attempt.UpdatedAt,
                    attempt.Status,
                    "CANCELED",
                    cancelReason,
                    evidenceAt,
                    CancellationToken.None),
                CancellationToken.None);
            if (!evidencePersisted)
            {
                var winner = await RunLocalStoreAsync(
                    () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                    CancellationToken.None);
                if (winner is null)
                {
                    return new CardPaymentRecoveryResult(
                        CardPaymentRecoveryOutcome.Unknown,
                        UnknownResultMessage());
                }

                if (!IsCanceledSaleDraftHandoff(winner))
                {
                    var winningPaymentReference = winner.PaymentId ?? winner.SupervisorFinancialReference;
                    var winningDraft = TryDeserializeDraft(winner);
                    if ((winner.Status == LocalSquarePaymentAttemptStatus.PaymentVerified ||
                         string.Equals(
                             winner.ResponseCode,
                             ActiveSessionSupervisorResolutionCodes.ConfirmedPaid,
                             StringComparison.Ordinal)) &&
                        !string.IsNullOrWhiteSpace(winningPaymentReference) &&
                        winningDraft is not null)
                    {
                        // CANCELED 查询结果只能提交自己的 CAS；若主管/付款验证已抢先落库，
                        // 必须服从最新金融事实，不能恢复相反的未付款草稿。
                        return await CompleteVerifiedAttemptAsync(
                            winner,
                            winningDraft,
                            winningPaymentReference,
                            winner.PaymentStatus ?? ActiveSessionSupervisorResolutionCodes.ConfirmedPaid,
                            cardBrand: null,
                            maskedCardNumber: null,
                            authCode: null,
                            CancellationToken.None);
                    }

                    return new CardPaymentRecoveryResult(
                        CardPaymentRecoveryOutcome.Unknown,
                        UnknownResultMessage());
                }

                attempt = winner;
            }
            else
            {
                attempt = attempt with
                {
                    CheckoutStatus = "CANCELED",
                    CancelReason = cancelReason ?? attempt.CancelReason,
                    UpdatedAt = evidenceAt
                };
            }
        }

        if (!IsCanceledSaleDraftHandoff(attempt))
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        var expectedRevision = cart.Revision;
        var finalizePending = await EnsureRecoveryFinalizationAsync(
            attempt,
            LocalSquarePaymentAttemptStatus.Canceled,
            cancellationToken);
        if (finalizePending is null)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        var publication = cart.TryPublishRecoverySnapshot(
            attemptKey,
            expectedRevision,
            draft.CartSnapshot);
        if (!publication.Succeeded)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                CurrentCartNotEmptyMessage());
        }

        // 取消草稿也要等 UI 完成交接后才 CAS 到 Canceled 并释放精确 owner。
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.DraftRestored,
            Format(
                "cardRecovery.square.cancelled",
                "The previous Square card payment was not completed: {0}. The order has been restored. Select a payment method again.",
                cancelReason ?? "CANCELED"),
            TenderedAmount: draft.CurrentTenders.Sum(tender => tender.Amount),
            RestoredTenders: draft.CurrentTenders,
            HasPostCommitWarning: publication.NotificationWarning)
        {
            DraftHandoffKey = attemptKey
        };
    }

    private static CardPaymentOrderDraft? TryDeserializeDraft(LocalSquarePaymentAttempt attempt)
    {
        // 所有恢复分支先在隔离购物车完成语义物化，禁止无效草稿触碰活动购物车。
        return CardRecoveryCartMaterializer.TryPrepare(
            attempt.OrderDraftJson,
            JsonOptions,
            out var draft)
            ? draft
            : null;
    }

    private static LocalFinancialSupervisorResolution BuildSquareSaleSupervisorJournal(
        LocalSquarePaymentAttempt attempt,
        CardRecoverySupervisorDecision decision,
        string reason,
        string? evidence,
        string? reference,
        PosSessionState session,
        DateTimeOffset resolvedAt)
    {
        var authorizer = OperationAuthorizationScope.CurrentAuthorizingSession ?? session.CashierSession;
        var operatorCashierId = authorizer?.CashierId ?? session.CashierId;
        var operatorUserGuid = authorizer?.UserGuid ?? session.CashierSession?.UserGuid;
        var operatorName = authorizer?.CashierName ?? session.CashierName;
        var resolutionGuid = Guid.NewGuid();
        var auditEventId = Guid.NewGuid();
        var auditEvent = new OperationAuditEventDto
        {
            EventId = auditEventId,
            OccurredAtUtc = resolvedAt,
            OperationType = "CARD_PAYMENT_SUPERVISOR_RESOLUTION",
            Outcome = "Succeeded",
            CashierId = operatorCashierId,
            UserGuid = operatorUserGuid,
            CashierName = operatorName,
            StoreCode = attempt.StoreCode,
            DeviceCode = attempt.DeviceCode,
            CorrelationId = attempt.AttemptGuid.ToString("D"),
            PaymentMethod = CardProcessorKind.Square.ToString(),
            ReasonCode = decision.ToString(),
            SafeMessage = reason,
            PaymentAmount = Math.Abs(attempt.Amount),
            Properties = new Dictionary<string, string?>
            {
                ["attemptGuid"] = attempt.AttemptGuid.ToString("D"),
                ["operationGuid"] = attempt.OperationGuid?.ToString("D"),
                ["checkoutId"] = attempt.CheckoutId,
                ["evidence"] = evidence,
                ["financialReference"] = reference
            }
        };
        return new LocalFinancialSupervisorResolution(
            resolutionGuid,
            LocalFinancialSupervisorResolutionTarget.ActiveSession,
            CardProcessorKind.Square.ToString(),
            attempt.Environment,
            attempt.StoreCode,
            attempt.DeviceCode,
            attempt.AttemptGuid,
            null,
            attempt.OperationGuid,
            attempt.CheckoutId ?? attempt.IdempotencyKey,
            decision.ToString(),
            operatorCashierId,
            operatorUserGuid,
            operatorName,
            reason,
            evidence,
            reference,
            null,
            resolvedAt,
            auditEventId,
            JsonSerializer.Serialize(auditEvent, JsonOptions));
    }

    private static CardRefundSupervisorDecision MapRefundDecision(CardRecoverySupervisorDecision decision) => decision switch
    {
        CardRecoverySupervisorDecision.ConfirmProcessed => CardRefundSupervisorDecision.ConfirmRefunded,
        CardRecoverySupervisorDecision.ConfirmNotProcessed => CardRefundSupervisorDecision.ConfirmNotRefunded,
        _ => CardRefundSupervisorDecision.ContinueWaiting
    };

    private async Task<CardPaymentRecoveryResult> RecoverRefundAttemptAsync(
        PosCartService cart,
        PosSessionState session,
        CardTerminalSettings settings,
        LocalSquarePaymentAttempt refundAttempt,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(refundAttempt.PaymentId) &&
            string.Equals(
                refundAttempt.PaymentStatus?.Trim(),
                "COMPLETED",
                StringComparison.OrdinalIgnoreCase))
        {
            // 本地已保存退款完成证据时直接重放完成，不能再用 Square GET 的迟到状态覆盖它。
            return await CompleteSupervisorConfirmedRefundAsync(
                cart,
                session,
                refundAttempt,
                cancellationToken);
        }

        if (refundAttempt.Status == LocalSquarePaymentAttemptStatus.PaymentVerified)
        {
            // 已进入 PaymentVerified 却缺少完整本地证据时必须失败关闭，不能重新查询远端覆盖该事实。
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile Square and the original sale."),
                DialogDetails: BuildRefundDialogDetails(refundAttempt),
                RefundDetails: BuildRefundDetails(refundAttempt));
        }

        if (IsUnfinalizedTerminalRefundFailure(refundAttempt))
        {
            // FAILED/REJECTED 已经是本地金融终态。即使上次在建立 FinalizePending 前退出，
            // 本次也必须先补齐本地 handoff，不能重新依赖 Square GET 才允许恢复订单。
            return await RecoverPersistedTerminalRefundFailureAsync(
                cart,
                session,
                refundAttempt);
        }

        if (string.Equals(
                refundAttempt.RecoveryPhase,
                CardRecoveryPhases.FinalizePending,
                StringComparison.Ordinal))
        {
            if (refundAttempt.RecoveryTargetStatus == LocalSquarePaymentAttemptStatus.OrderCompleted)
            {
                return await CompleteSupervisorConfirmedRefundAsync(
                    cart,
                    session,
                    refundAttempt,
                    cancellationToken);
            }

            if (refundAttempt.RecoveryTargetStatus == LocalSquarePaymentAttemptStatus.Abandoned)
            {
                return await RecoverFinalizePendingAlternativeRefundAsync(
                    cart,
                    refundAttempt,
                    cancellationToken);
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile Square and the original sale."),
                DialogDetails: BuildRefundDialogDetails(refundAttempt),
                RefundDetails: BuildRefundDetails(refundAttempt));
        }

        if (string.Equals(
                refundAttempt.ResponseCode,
                CardRefundSupervisorResolutionCodes.ConfirmedRefunded,
                StringComparison.Ordinal))
        {
            return await CompleteSupervisorConfirmedRefundAsync(
                cart,
                session,
                refundAttempt,
                cancellationToken);
        }

        if (refundAttempt.Status == LocalSquarePaymentAttemptStatus.Pending &&
            string.Equals(
                refundAttempt.ResponseCode,
                CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded,
                StringComparison.Ordinal))
        {
            return await RestoreSupervisorApprovedRetryAsync(cart, refundAttempt, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(refundAttempt.PaymentId) &&
            !string.IsNullOrWhiteSpace(refundAttempt.SubmissionToken) &&
            refundAttempt.Status is LocalSquarePaymentAttemptStatus.Unknown or LocalSquarePaymentAttemptStatus.Recovering)
        {
            // 退款请求已越过提交边界却没拿到退款号（回执丢失）：先去 Square 按原付款找回已受理的退款，
            // 找到并绑定后沿用下面的自动恢复路径；找不到或有歧义则保持原样交给主管对账。
            var discovered = await TryDiscoverUnboundRefundAsync(settings, refundAttempt, cancellationToken);
            if (discovered is not null)
            {
                refundAttempt = discovered;
            }
        }

        if (!string.IsNullOrWhiteSpace(refundAttempt.PaymentId) &&
            !string.IsNullOrWhiteSpace(refundAttempt.SubmissionToken))
        {
            var automaticRecovery = await TryRecoverSquareRefundAsync(
                cart,
                session,
                settings,
                refundAttempt,
                cancellationToken);
            if (automaticRecovery is not null)
            {
                return automaticRecovery;
            }
        }

        if (refundAttempt.Status == LocalSquarePaymentAttemptStatus.Pending)
        {
            var recoveringAt = DateTimeOffset.UtcNow;
            var marked = await RunLocalStoreAsync(
                () => attemptRepository.TryMarkRecoveringAsync(
                    refundAttempt.AttemptGuid,
                    refundAttempt.Status,
                    refundAttempt.UpdatedAt,
                    recoveringAt,
                    CancellationToken.None),
                CancellationToken.None);
            if (!marked)
            {
                var winner = await RunLocalStoreAsync(
                    () => attemptRepository.GetAttemptAsync(refundAttempt.AttemptGuid, CancellationToken.None),
                    CancellationToken.None);
                return winner is not null &&
                    (winner.Status != refundAttempt.Status ||
                     winner.UpdatedAt != refundAttempt.UpdatedAt ||
                     !string.Equals(winner.RecoveryPhase, refundAttempt.RecoveryPhase, StringComparison.Ordinal))
                    ? await RecoverRefundAttemptAsync(cart, session, settings, winner, CancellationToken.None)
                    : new CardPaymentRecoveryResult(
                        CardPaymentRecoveryOutcome.Unknown,
                        T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile Square and the original sale."),
                        DialogDetails: BuildRefundDialogDetails(refundAttempt),
                        RefundDetails: BuildRefundDetails(refundAttempt));
            }

            refundAttempt = refundAttempt with
            {
                Status = LocalSquarePaymentAttemptStatus.Recovering,
                UpdatedAt = recoveringAt
            };
        }

        ConsoleLog.Write(
            "SquareRecovery",
            $"open refund requires reconciliation attemptGuid={refundAttempt.AttemptGuid} idempotencyKey={refundAttempt.IdempotencyKey} amount={refundAttempt.Amount:0.00}");
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile Square and the original sale."),
            DialogDetails: BuildRefundDialogDetails(refundAttempt),
            RefundDetails: BuildRefundDetails(refundAttempt));
    }

    private async Task<CardPaymentRecoveryResult> RecoverFinalizePendingAlternativeRefundAsync(
        PosCartService cart,
        LocalSquarePaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        var draft = TryDeserializeDraft(attempt);
        if (draft is null)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ResolutionPendingMessage(),
                DialogDetails: BuildRefundDialogDetails(attempt),
                RefundDetails: BuildRefundDetails(attempt));
        }

        LocalOrder? existingOrder;
        try
        {
            existingOrder = await RunLocalStoreAsync(
                () => orderRepository.GetOrderAsync(draft.OrderGuid, CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"alternative refund existing order lookup failed attemptGuid={attempt.AttemptGuid} orderGuid={draft.OrderGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ResolutionPendingMessage(),
                DialogDetails: BuildRefundDialogDetails(attempt),
                RefundDetails: BuildRefundDetails(attempt));
        }

        if (existingOrder is null)
        {
            // 未落单时只交给恢复草稿路径；FAILED/REJECTED 仍必须保留 FinalizePending 和 owner。
            return await RestoreSupervisorApprovedRetryAsync(cart, attempt, cancellationToken);
        }

        if (!MatchesPersistedAlternativeRefundOrder(attempt, draft, existingOrder))
        {
            // 同 OrderGuid 但身份、退款行或付款证据不一致时，既不能清锁，也不能污染当前购物车。
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.existingOrderMismatch", "A saved order uses the refund draft order ID, but its store, refund lines, amount, or tenders do not match. The refund remains locked for supervisor review."),
                DialogDetails: BuildRefundDialogDetails(attempt),
                RefundDetails: BuildRefundDetails(attempt));
        }

        if (!HasCompleteAlternativeRefundSettlement(existingOrder.ActualAmount, existingOrder.Payments))
        {
            if (!HasPendingAlternativeRefundVoucherSettlement(
                    existingOrder.ActualAmount,
                    existingOrder.Payments))
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    ResolutionPendingMessage(),
                    DialogDetails: BuildRefundDialogDetails(attempt),
                    RefundDetails: BuildRefundDetails(attempt));
            }

            // 订单已经耐久保存但发券尚未完成：重建同一 owner 和同一幂等 tender，
            // 让付款页再次确认时沿既有 CompletePaymentAsync 路径重放发券，而不是永远锁死在恢复中心。
            var publication = cart.TryPublishRecoverySnapshot(
                new CardRecoveryAttemptKey(CardProcessorKind.Square, attempt.AttemptGuid),
                cart.Revision,
                draft.CartSnapshot);
            if (!publication.Succeeded)
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.square.currentCartNotEmpty", "The previous Square card result needs handling, but the current cart already contains items. Complete or clear the current cart before recovering the previous order."),
                    DialogDetails: BuildRefundDialogDetails(attempt),
                    RefundDetails: BuildRefundDetails(attempt),
                    HasPostCommitWarning: publication.NotificationWarning);
            }

            var restoredTenders = existingOrder.Payments
                .Select(payment => new PaymentTender(
                    payment.Method,
                    payment.Amount,
                    payment.Reference,
                    CardTransactions: payment.CardTransactions,
                    IdempotencyKey: payment.IdempotencyKey))
                .ToArray();
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.DraftRestored,
                T("payment.status.retryVoucherUpload", "Retry the voucher order upload before changing tenders."),
                TenderedAmount: restoredTenders.Sum(tender => tender.Amount),
                DialogDetails: BuildRefundDialogDetails(attempt),
                RestoredTenders: restoredTenders,
                RefundDetails: BuildRefundDetails(attempt),
                HasPostCommitWarning: publication.NotificationWarning)
            {
                RequiresAlternativeRefundMethod = true
            };
        }

        LocalSquarePaymentAttempt? finalizePending;
        try
        {
            finalizePending = await EnsureRecoveryFinalizationAsync(
                attempt,
                LocalSquarePaymentAttemptStatus.Abandoned,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"alternative refund existing order finalization prepare failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ResolutionPendingMessage(),
                DialogDetails: BuildRefundDialogDetails(attempt),
                RefundDetails: BuildRefundDetails(attempt));
        }

        if (finalizePending is null ||
            !await CompleteRecoveryFinalizationAsync(
                finalizePending,
                LocalSquarePaymentAttemptStatus.Abandoned,
                CancellationToken.None))
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ResolutionPendingMessage(),
                DialogDetails: BuildRefundDialogDetails(attempt),
                RefundDetails: BuildRefundDetails(attempt));
        }

        // 新进程没有内存中的 owner 是正常情况；只有确实属于本 attempt 的 owner 才允许释放。
        var attemptKey = new CardRecoveryAttemptKey(CardProcessorKind.Square, attempt.AttemptGuid);
        var ownershipReleased = cart.RecoveryOwnerAttemptKey is null
            ? cart.RecoveryOwnerAttemptGuid is null
            : cart.RecoveryOwnerAttemptKey == attemptKey &&
              cart.CompleteRecoveryPublication(attemptKey);
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.OrderCompleted,
            T("cardRecovery.refund.alternativeCompleted", "The alternative refund order was already saved and has been recovered."),
            existingOrder,
            DialogDetails: BuildRefundDialogDetails(attempt),
            RefundDetails: BuildRefundDetails(attempt),
            HasPostCommitWarning: !ownershipReleased);
    }

    private async Task<CardPaymentRecoveryResult> RecoverSaleAttemptAsync(
        PosCartService cart,
        PosSessionState session,
        CardTerminalSettings settings,
        LocalSquarePaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        // 主管确认未付款：在 MarkRecovering/远端 checkout 查询之前恢复并建立本地 handoff，
        // 避免缺 CheckoutId 遮蔽该主管状态导致永久 Unknown。
        if (IsSupervisorNotPaidSale(attempt))
        {
            return await RecoverSupervisorNotPaidSaleAsync(cart, attempt, cancellationToken);
        }

        if (IsCanceledSaleDraftHandoff(attempt))
        {
            // 上次可能只来得及保存 CANCELED 证据；远端查询前先从本地补齐 FinalizePending，
            // 确保离线重启也能恢复草稿且不会再次访问 Square。
            var canceledDraft = TryDeserializeDraft(attempt);
            return canceledDraft is null
                ? new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    UnknownResultMessage())
                : await RestoreCanceledSaleAsync(
                    cart,
                    attempt,
                    canceledDraft,
                    attempt.CancelReason,
                    cancellationToken);
        }

        CardPaymentOrderDraft? draft = null;
        if (string.Equals(
                attempt.RecoveryPhase,
                CardRecoveryPhases.FinalizePending,
                StringComparison.Ordinal))
        {
            if (attempt.RecoveryTargetStatus == LocalSquarePaymentAttemptStatus.Abandoned)
            {
                return await RecoverSupervisorNotPaidSaleAsync(cart, attempt, cancellationToken);
            }

            if (attempt.RecoveryTargetStatus == LocalSquarePaymentAttemptStatus.Canceled)
            {
                draft = TryDeserializeDraft(attempt);
                return draft is null
                    ? new CardPaymentRecoveryResult(
                        CardPaymentRecoveryOutcome.Unknown,
                        UnknownResultMessage())
                    : await RestoreCanceledSaleAsync(
                        cart,
                        attempt,
                        draft,
                        attempt.CancelReason,
                        cancellationToken);
            }

            if (attempt.RecoveryTargetStatus == LocalSquarePaymentAttemptStatus.OrderCompleted)
            {
                var pendingReference = attempt.PaymentId ?? attempt.SupervisorFinancialReference;
                draft = TryDeserializeDraft(attempt);
                if (!string.IsNullOrWhiteSpace(pendingReference) && draft is not null)
                {
                    return await CompleteVerifiedAttemptAsync(
                        attempt,
                        draft,
                        pendingReference,
                        attempt.PaymentStatus ?? ActiveSessionSupervisorResolutionCodes.ConfirmedPaid,
                        cardBrand: null,
                        maskedCardNumber: null,
                        authCode: null,
                        cancellationToken);
                }
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        if (attempt.Status == LocalSquarePaymentAttemptStatus.PaymentVerified &&
            !string.IsNullOrWhiteSpace(attempt.PaymentId) &&
            !string.IsNullOrWhiteSpace(attempt.PaymentStatus))
        {
            draft = TryDeserializeDraft(attempt);
            if (draft is null)
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    UnknownResultMessage());
            }

            return await CompleteVerifiedAttemptAsync(
                attempt,
                draft,
                attempt.PaymentId!,
                attempt.PaymentStatus!,
                cardBrand: null,
                maskedCardNumber: null,
                authCode: null,
                cancellationToken);
        }

        var recoveringAt = DateTimeOffset.UtcNow;
        var markedRecovering = await RunLocalStoreAsync(
            () => attemptRepository.TryMarkRecoveringAsync(
                attempt.AttemptGuid,
                attempt.Status,
                attempt.UpdatedAt,
                recoveringAt,
                cancellationToken),
            cancellationToken);
        if (!markedRecovering)
        {
            var winner = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                CancellationToken.None);
            if (winner is not null &&
                (winner.Status != attempt.Status || winner.UpdatedAt != attempt.UpdatedAt ||
                 !string.Equals(winner.RecoveryPhase, attempt.RecoveryPhase, StringComparison.Ordinal)))
            {
                return await RecoverSaleAttemptAsync(cart, session, settings, winner, CancellationToken.None);
            }

            return new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.Unknown, UnknownResultMessage());
        }

        attempt = attempt with
        {
            Status = LocalSquarePaymentAttemptStatus.Recovering,
            UpdatedAt = recoveringAt
        };
        var checkingMessage = Format(
            "cardRecovery.square.checking",
            "A previous Square card transaction for {0:C2} was in progress before the POS closed. Checking the card terminal status.",
            attempt.Amount);

        if (string.IsNullOrWhiteSpace(attempt.CheckoutId))
        {
            ConsoleLog.Write(
                "SquareRecovery",
                $"checkout id missing; payment remains locked attemptGuid={attempt.AttemptGuid}");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T(
                    "cardRecovery.square.missingCheckoutId",
                    "The previous Square payment result is unknown because its checkout reference was not saved. Do not take payment again; ask a supervisor to reconcile Square."),
                DialogDetails: new CardPaymentRecoveryDialogDetails(
                    attempt.CheckoutId,
                    attempt.IdempotencyKey,
                    attempt.ResponseCode,
                    attempt.ResponseText,
                    attempt.Amount,
                    attempt.UpdatedAt));
        }

        draft = TryDeserializeDraft(attempt);
        if (draft is null)
        {
            ConsoleLog.Write(
                "SquareRecovery",
                $"sale draft invalid; payment remains locked attemptGuid={attempt.AttemptGuid}");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        SquareCheckoutStatusResult checkoutStatus;
        try
        {
            checkoutStatus = await squareTerminalPaymentClient.GetCheckoutAsync(settings, attempt.CheckoutId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"checkout lookup failed attemptGuid={attempt.AttemptGuid} checkoutId={attempt.CheckoutId} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        if (IsSquarePendingStatus(checkoutStatus.Status))
        {
            var pendingAt = DateTimeOffset.UtcNow;
            var persisted = await RunLocalStoreAsync(
                () => attemptRepository.TryUpdateCheckoutStatusAsync(
                    attempt.AttemptGuid,
                    attempt.Status,
                    attempt.UpdatedAt,
                    LocalSquarePaymentAttemptStatus.Recovering,
                    checkoutStatus.Status,
                    checkoutStatus.CancelReason,
                    pendingAt,
                    cancellationToken),
                cancellationToken);
            if (!persisted)
            {
                var winner = await RunLocalStoreAsync(
                    () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                    CancellationToken.None);
                return winner is null
                    ? CardPaymentRecoveryResult.None
                    : await RecoverSaleAttemptAsync(cart, session, settings, winner, CancellationToken.None);
            }

            return new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.Checking, checkingMessage);
        }

        if (string.Equals(checkoutStatus.Status, "CANCELED", StringComparison.OrdinalIgnoreCase))
        {
            if (TryDeferForCurrentCart(cart, attempt, "checkout-final-CANCELED", out var deferredResult))
            {
                return deferredResult;
            }

            return await RestoreCanceledSaleAsync(
                cart,
                attempt,
                draft,
                checkoutStatus.CancelReason,
                cancellationToken);
        }

        if (!string.Equals(checkoutStatus.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            var failedAt = DateTimeOffset.UtcNow;
            var failedPersisted = await RunLocalStoreAsync(
                () => attemptRepository.TryMarkFailedAsync(
                    attempt.AttemptGuid,
                    attempt.Status,
                    attempt.UpdatedAt,
                    LocalSquarePaymentAttemptStatus.Unknown,
                    checkoutStatus.Status,
                    attempt.PaymentStatus,
                    null,
                    $"Unexpected checkout status {checkoutStatus.Status}.",
                    failedAt,
                    cancellationToken),
                cancellationToken);
            if (!failedPersisted)
            {
                var winner = await RunLocalStoreAsync(
                    () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                    CancellationToken.None);
                return winner is null
                    ? CardPaymentRecoveryResult.None
                    : await RecoverSaleAttemptAsync(cart, session, settings, winner, CancellationToken.None);
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        var paymentId = checkoutStatus.PaymentIds.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(paymentId))
        {
            var missingPaymentAt = DateTimeOffset.UtcNow;
            var missingPaymentPersisted = await RunLocalStoreAsync(
                () => attemptRepository.TryMarkFailedAsync(
                    attempt.AttemptGuid,
                    attempt.Status,
                    attempt.UpdatedAt,
                    LocalSquarePaymentAttemptStatus.Unknown,
                    checkoutStatus.Status,
                    attempt.PaymentStatus,
                    null,
                    "Square checkout did not return a payment id.",
                    missingPaymentAt,
                    cancellationToken),
                cancellationToken);
            if (!missingPaymentPersisted)
            {
                var winner = await RunLocalStoreAsync(
                    () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                    CancellationToken.None);
                return winner is null
                    ? CardPaymentRecoveryResult.None
                    : await RecoverSaleAttemptAsync(cart, session, settings, winner, CancellationToken.None);
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        SquarePaymentStatusResult payment;
        try
        {
            payment = await squareTerminalPaymentClient.GetPaymentAsync(settings, paymentId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"payment lookup failed attemptGuid={attempt.AttemptGuid} checkoutId={attempt.CheckoutId} paymentId={paymentId} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        var verification = SquarePaymentVerifier.Verify(
            payment.Status,
            payment.AmountCents,
            payment.Currency,
            attempt.AmountCents,
            attempt.Currency);
        if (!verification.Verified)
        {
            var verificationFailedAt = DateTimeOffset.UtcNow;
            var verificationFailurePersisted = await RunLocalStoreAsync(
                () => attemptRepository.TryMarkFailedAsync(
                    attempt.AttemptGuid,
                    attempt.Status,
                    attempt.UpdatedAt,
                    LocalSquarePaymentAttemptStatus.Unknown,
                    checkoutStatus.Status,
                    payment.Status,
                    null,
                    verification.Message,
                    verificationFailedAt,
                    cancellationToken),
                cancellationToken);
            if (!verificationFailurePersisted)
            {
                var winner = await RunLocalStoreAsync(
                    () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                    CancellationToken.None);
                return winner is null
                    ? CardPaymentRecoveryResult.None
                    : await RecoverSaleAttemptAsync(cart, session, settings, winner, CancellationToken.None);
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                verification.Failure == SquarePaymentVerificationFailure.Amount
                    ? T("cardRecovery.square.amountMismatch", "The payment amount returned by Square does not match the order amount. The order was not saved automatically. Ask a supervisor to confirm.")
                    : UnknownResultMessage());
        }

        var verifiedAt = DateTimeOffset.UtcNow;
        var paymentPersisted = await RunLocalStoreAsync(
            () => attemptRepository.TryPersistPaymentVerifiedRecoveryAsync(
                attempt.AttemptGuid,
                attempt.Status,
                attempt.UpdatedAt,
                payment.PaymentId,
                payment.Status,
                null,
                "Payment verified during recovery.",
                verifiedAt,
                CancellationToken.None),
            CancellationToken.None);
        if (!paymentPersisted)
        {
            var winner = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                CancellationToken.None);
            return winner is not null &&
                (winner.Status != attempt.Status ||
                 winner.UpdatedAt != attempt.UpdatedAt ||
                 !string.Equals(winner.RecoveryPhase, attempt.RecoveryPhase, StringComparison.Ordinal))
                ? await RecoverSaleAttemptAsync(cart, session, settings, winner, CancellationToken.None)
                : new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    UnknownResultMessage());
        }

        attempt = attempt with
        {
            Status = LocalSquarePaymentAttemptStatus.PaymentVerified,
            PaymentId = payment.PaymentId,
            PaymentStatus = payment.Status,
            ResponseCode = null,
            ResponseText = "Payment verified during recovery.",
            CompletedAt = verifiedAt,
            UpdatedAt = verifiedAt,
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.OrderCompleted
        };
        return await CompleteVerifiedAttemptAsync(
            attempt,
            draft,
            payment.PaymentId,
            payment.Status,
            payment.CardBrand,
            payment.MaskedCardNumber,
            payment.AuthCode,
            cancellationToken);
    }

    // 本地时钟可能与 Square 相差数十秒甚至更多，窗口取宽，仍要求退款创建时间落在该次 attempt 前后。
    private static readonly TimeSpan RefundDiscoveryWindowBefore = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefundDiscoveryWindowAfter = TimeSpan.FromMinutes(10);

    private static string? TryGetOriginalSquarePaymentId(LocalSquarePaymentAttempt attempt)
    {
        try
        {
            var originalReference = DeserializeDraft(attempt).OriginalReference;
            if (!string.IsNullOrWhiteSpace(originalReference) &&
                originalReference.Trim().StartsWith("SQ:", StringComparison.OrdinalIgnoreCase))
            {
                return originalReference.Trim()[3..].Trim();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // 草稿损坏时不能自动认定退款终态，保留主管核对路径。
        }

        return null;
    }

    /// <summary>
    /// 退款请求已被 Square 受理但本地没收到退款号时，按原付款的 refund_ids 找回它。
    /// 只有“恰好一笔”且金额、币种、原付款、创建时间窗口都吻合、又没被其他 attempt 占用的退款才会绑定；
    /// 任何查询失败、旧后端缺字段或存在歧义都返回 null，由主管对账兜底。
    /// </summary>
    private async Task<LocalSquarePaymentAttempt?> TryDiscoverUnboundRefundAsync(
        CardTerminalSettings settings,
        LocalSquarePaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        var originalPaymentId = TryGetOriginalSquarePaymentId(attempt);
        if (string.IsNullOrWhiteSpace(originalPaymentId))
        {
            return null;
        }

        try
        {
            var payment = await squareTerminalPaymentClient.GetPaymentAsync(settings, originalPaymentId, cancellationToken);
            if (payment.RefundIds is not { Count: > 0 } refundIds)
            {
                return null;
            }

            var windowStart = attempt.CreatedAt - RefundDiscoveryWindowBefore;
            var windowEnd = attempt.CreatedAt + RefundDiscoveryWindowAfter;
            var candidates = new List<SquareRefundStatusResult>();
            foreach (var refundId in refundIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
            {
                var refund = await squareTerminalPaymentClient.GetRefundAsync(settings, refundId, cancellationToken);
                if (string.Equals(refund.RefundId, refundId, StringComparison.Ordinal) &&
                    string.Equals(refund.PaymentId, originalPaymentId, StringComparison.Ordinal) &&
                    refund.AmountCents == attempt.AmountCents &&
                    string.Equals(refund.Currency, attempt.Currency, StringComparison.OrdinalIgnoreCase) &&
                    refund.CreatedAt is { } createdAt &&
                    createdAt >= windowStart &&
                    createdAt <= windowEnd &&
                    !await RunLocalStoreAsync(
                        () => attemptRepository.IsRefundIdBoundToOtherAttemptAsync(
                            attempt.Environment,
                            refundId,
                            attempt.AttemptGuid,
                            CancellationToken.None),
                        CancellationToken.None))
                {
                    candidates.Add(refund);
                }
            }

            if (candidates.Count != 1)
            {
                ConsoleLog.Write(
                    "SquareRecovery",
                    $"unbound refund discovery inconclusive attemptGuid={attempt.AttemptGuid} candidates={candidates.Count} refundIds={refundIds.Count}");
                return null;
            }

            var found = candidates[0];
            var bound = await RunLocalStoreAsync(
                () => attemptRepository.TryRecordRefundResponseAsync(
                    attempt.AttemptGuid,
                    attempt.Status,
                    attempt.UpdatedAt,
                    attempt.SubmissionToken!,
                    found.RefundId,
                    found.Status.ToUpperInvariant(),
                    DateTimeOffset.UtcNow,
                    CancellationToken.None),
                CancellationToken.None);
            ConsoleLog.Write(
                "SquareRecovery",
                $"unbound refund discovered attemptGuid={attempt.AttemptGuid} refundId={found.RefundId} status={found.Status} bound={bound}");
            return bound
                ? await RunLocalStoreAsync(
                    () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                    CancellationToken.None)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"unbound refund discovery failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return null;
        }
    }

    private async Task<CardPaymentRecoveryResult?> TryRecoverSquareRefundAsync(
        PosCartService cart,
        PosSessionState session,
        CardTerminalSettings settings,
        LocalSquarePaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        SquareRefundStatusResult refund;
        try
        {
            refund = await squareTerminalPaymentClient.GetRefundAsync(
                settings,
                attempt.PaymentId!,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"refund lookup failed attemptGuid={attempt.AttemptGuid} refundId={attempt.PaymentId} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return null;
        }

        var originalPaymentId = TryGetOriginalSquarePaymentId(attempt);

        if (!string.Equals(refund.RefundId, attempt.PaymentId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(originalPaymentId) ||
            !string.Equals(refund.PaymentId, originalPaymentId, StringComparison.Ordinal) ||
            refund.AmountCents != attempt.AmountCents ||
            !string.Equals(refund.Currency, attempt.Currency, StringComparison.OrdinalIgnoreCase))
        {
            ConsoleLog.Write(
                "SquareRecovery",
                $"refund identity mismatch attemptGuid={attempt.AttemptGuid} refundId={attempt.PaymentId}");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile Square and the original sale."),
                DialogDetails: BuildRefundDialogDetails(attempt),
                RefundDetails: BuildRefundDetails(attempt));
        }

        if (string.Equals(refund.Status, "FAILED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(refund.Status, "REJECTED", StringComparison.OrdinalIgnoreCase))
        {
            var failedAt = DateTimeOffset.UtcNow;
            const string failureResponseText = "Square refund reached a terminal failure.";
            var failurePersisted = await RunLocalStoreAsync(
                () => attemptRepository.TryPersistRefundFailureForFinalizationAsync(
                    attempt.AttemptGuid,
                    attempt.Status,
                    attempt.UpdatedAt,
                    attempt.SubmissionToken!,
                    refund.Status,
                    responseCode: null,
                    responseText: failureResponseText,
                    failedAt,
                    CancellationToken.None),
                CancellationToken.None);
            if (!failurePersisted)
            {
                var winner = await RunLocalStoreAsync(
                    () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                    CancellationToken.None);
                if (winner is not null &&
                    (winner.Status != attempt.Status ||
                     winner.UpdatedAt != attempt.UpdatedAt ||
                     !string.Equals(winner.RecoveryPhase, attempt.RecoveryPhase, StringComparison.Ordinal)))
                {
                    return await ResolveSquareRefundWriteWinnerAsync(
                        cart,
                        session,
                        winner,
                        CancellationToken.None);
                }

                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile Square and the original sale."),
                    DialogDetails: BuildRefundDialogDetails(attempt),
                    RefundDetails: BuildRefundDetails(attempt));
            }

            // Square 的失败终态已固化；后续只重放本地草稿，不再重新查询或发起退款。
            var failedAttempt = attempt with
            {
                Status = LocalSquarePaymentAttemptStatus.Unknown,
                PaymentStatus = refund.Status.ToUpperInvariant(),
                ResponseText = string.IsNullOrWhiteSpace(attempt.ResponseText)
                    ? failureResponseText
                    : attempt.ResponseText,
                UpdatedAt = failedAt,
                RecoveryPhase = CardRecoveryPhases.FinalizePending,
                RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.Abandoned
            };
            return await RestoreSupervisorApprovedRetryAsync(
                cart,
                failedAttempt,
                CancellationToken.None);
        }

        // Square 官方定义 PENDING 为“已受理、正在划款并退回原卡”（多数几小时内完成，最长 14 天），
        // 与 COMPLETED 一样完成退货单；PaymentStatus 保留 PENDING，交给退款结算跟踪继续查询最终结果。
        var isAcceptedPending = string.Equals(refund.Status, "PENDING", StringComparison.OrdinalIgnoreCase);
        if (!isAcceptedPending &&
            !string.Equals(refund.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile Square and the original sale."),
                DialogDetails: BuildRefundDialogDetails(attempt),
                RefundDetails: BuildRefundDetails(attempt));
        }

        var verifiedResponseText = isAcceptedPending
            ? "Square accepted the refund; settlement is pending."
            : "Square refund status confirmed by lookup.";
        var completedAt = DateTimeOffset.UtcNow;
        var persisted = await RunLocalStoreAsync(
            () => attemptRepository.TryMarkRefundPaymentVerifiedAsync(
                attempt.AttemptGuid,
                attempt.Status,
                attempt.UpdatedAt,
                attempt.SubmissionToken!,
                refund.RefundId,
                refund.Status.ToUpperInvariant(),
                responseCode: null,
                responseText: verifiedResponseText,
                completedAt,
                CancellationToken.None),
            CancellationToken.None);
        if (!persisted)
        {
            var winner = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                CancellationToken.None);
            if (winner is not null &&
                (winner.Status != attempt.Status ||
                 winner.UpdatedAt != attempt.UpdatedAt ||
                 !string.Equals(winner.RecoveryPhase, attempt.RecoveryPhase, StringComparison.Ordinal)))
            {
                return await ResolveSquareRefundWriteWinnerAsync(
                    cart,
                    session,
                    winner,
                    CancellationToken.None);
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile Square and the original sale."),
                DialogDetails: BuildRefundDialogDetails(attempt),
                RefundDetails: BuildRefundDetails(attempt));
        }

        var verifiedAttempt = attempt with
        {
            Status = LocalSquarePaymentAttemptStatus.PaymentVerified,
            PaymentStatus = refund.Status.ToUpperInvariant(),
            ResponseCode = null,
            ResponseText = verifiedResponseText,
            CompletedAt = completedAt,
            UpdatedAt = completedAt,
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.OrderCompleted
        };
        return await CompleteSupervisorConfirmedRefundAsync(
            cart,
            session,
            verifiedAttempt,
            CancellationToken.None);
    }

    private async Task<CardPaymentRecoveryResult> RecoverPersistedTerminalRefundFailureAsync(
        PosCartService cart,
        PosSessionState session,
        LocalSquarePaymentAttempt attempt)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        if (updatedAt <= attempt.UpdatedAt)
        {
            updatedAt = attempt.UpdatedAt.AddTicks(1);
        }

        try
        {
            await RunLocalStoreAsync(
                () => attemptRepository.TryPersistRefundFailureForFinalizationAsync(
                    attempt.AttemptGuid,
                    attempt.Status,
                    attempt.UpdatedAt,
                    attempt.SubmissionToken!,
                    attempt.PaymentStatus!.Trim().ToUpperInvariant(),
                    attempt.ResponseCode,
                    attempt.ResponseText,
                    updatedAt,
                    CancellationToken.None),
                CancellationToken.None);

            // CAS 成败都重读数据库，只服从真实赢家；禁止用内存快照伪造已耐久的恢复阶段。
            var winner = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(attempt.AttemptGuid, CancellationToken.None),
                CancellationToken.None);
            if (winner is not null)
            {
                return await ResolveSquareRefundWriteWinnerAsync(
                    cart,
                    session,
                    winner,
                    CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"local refund failure handoff repair failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
        }

        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile Square and the original sale."),
            DialogDetails: BuildRefundDialogDetails(attempt),
            RefundDetails: BuildRefundDetails(attempt));
    }

    private async Task<CardPaymentRecoveryResult> ResolveSquareRefundWriteWinnerAsync(
        PosCartService cart,
        PosSessionState session,
        LocalSquarePaymentAttempt winner,
        CancellationToken cancellationToken)
    {
        // CAS 失利后只能重放本地已持久化阶段；不得再次进入 Square GET 覆盖并发赢家。
        if (string.Equals(
                winner.PaymentStatus?.Trim(),
                "COMPLETED",
                StringComparison.OrdinalIgnoreCase))
        {
            return await CompleteSupervisorConfirmedRefundAsync(
                cart,
                session,
                winner,
                cancellationToken);
        }

        if (string.Equals(
                winner.RecoveryPhase,
                CardRecoveryPhases.FinalizePending,
                StringComparison.Ordinal))
        {
            if (winner.RecoveryTargetStatus == LocalSquarePaymentAttemptStatus.OrderCompleted)
            {
                return await CompleteSupervisorConfirmedRefundAsync(
                    cart,
                    session,
                    winner,
                    cancellationToken);
            }

            if (winner.RecoveryTargetStatus == LocalSquarePaymentAttemptStatus.Abandoned)
            {
                return await RecoverFinalizePendingAlternativeRefundAsync(
                    cart,
                    winner,
                    cancellationToken);
            }
        }

        if (winner.Status == LocalSquarePaymentAttemptStatus.OrderCompleted ||
            string.Equals(
                winner.ResponseCode,
                CardRefundSupervisorResolutionCodes.ConfirmedRefunded,
                StringComparison.Ordinal))
        {
            return await CompleteSupervisorConfirmedRefundAsync(
                cart,
                session,
                winner,
                cancellationToken);
        }

        if (string.Equals(
                winner.ResponseCode,
                CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded,
                StringComparison.Ordinal))
        {
            return await RestoreSupervisorApprovedRetryAsync(cart, winner, cancellationToken);
        }

        if (string.Equals(winner.PaymentStatus, "PENDING", StringComparison.OrdinalIgnoreCase))
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Checking,
                T("cardRecovery.refund.squarePending", "Square is still processing the refund. Do not refund again; run recovery later."),
                DialogDetails: BuildRefundDialogDetails(winner),
                RefundDetails: BuildRefundDetails(winner));
        }

        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile Square and the original sale."),
            DialogDetails: BuildRefundDialogDetails(winner),
            RefundDetails: BuildRefundDetails(winner));
    }

    public async Task<CardRefundSupervisorResolutionResult> ResolveRefundAsync(
        CardRefundSupervisorResolution resolution,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        if (resolution.Processor != CardProcessorKind.Square)
        {
            return new CardRefundSupervisorResolutionResult(false, "The selected refund does not belong to Square.");
        }

        // Square 的真实退款参考号是金融门禁；必须先于共享“备注或参考号”规则返回本地化错误。
        if (resolution.Decision == CardRefundSupervisorDecision.ConfirmRefunded &&
            string.IsNullOrWhiteSpace(resolution.RefundReference))
        {
            return new CardRefundSupervisorResolutionResult(
                false,
                T(
                    "cardRecovery.refund.squareReferenceRequired",
                    "A real Square refund reference is required before confirming the refund."));
        }

        if (!CardRefundSupervisorResolutionRules.TryNormalize(resolution, out var normalized, out var validationError))
        {
            return new CardRefundSupervisorResolutionResult(false, validationError);
        }

        normalized = CardRefundSupervisorResolutionRules.WithDefaultReason(normalized);

        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var attempt = await RunLocalStoreAsync(
            () => attemptRepository.GetAttemptAsync(normalized.AttemptGuid, cancellationToken),
            cancellationToken);
        if (attempt is null ||
            !string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.Environment, settings.Environment.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return new CardRefundSupervisorResolutionResult(
                false,
                "The unresolved Square refund no longer matches this terminal and cannot be changed.");
        }

        if (IsTerminalSquareStatus(attempt.Status) ||
            attempt.Status == LocalSquarePaymentAttemptStatus.PaymentVerified ||
            !string.IsNullOrWhiteSpace(attempt.PaymentId) ||
            !string.IsNullOrWhiteSpace(attempt.PaymentStatus) ||
            string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal))
        {
            return new CardRefundSupervisorResolutionResult(
                false,
                "The Square refund already has a newer financial result and cannot be changed.",
                LockRetained: !IsTerminalSquareStatus(attempt.Status),
                ResolutionPersisted: IsPersistedSquareSupervisorResolution(attempt));
        }

        if (normalized.Decision == CardRefundSupervisorDecision.ConfirmNotRefunded)
        {
            try
            {
                var retryDraft = TryDeserializeDraft(attempt) ??
                    throw new InvalidOperationException("Square refund draft is missing.");
                _ = ValidateAuthoritativeDraft(attempt, retryDraft);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                TryWriteRecoveryLog(
                    $"not-refunded authoritative draft invalid attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                    ex,
                    attempt.AttemptGuid,
                    error: false);
                return new CardRefundSupervisorResolutionResult(
                    false,
                    T("cardRecovery.refund.retryDraftInvalid", "The bank confirmed no refund, but POS could not rebuild the original return. Do not retry until support checks this attempt."),
                    RetryAllowed: false,
                    LockRetained: true);
            }
        }

        var resolvedAt = DateTimeOffset.UtcNow;
        var journal = BuildRefundSupervisorJournal(attempt, normalized, session, resolvedAt);
        var applied = await RunLocalStoreAsync(
            () => attemptRepository.ResolveRefundWithJournalAsync(
                new CardRefundAttemptResolution(
                    normalized.AttemptGuid,
                    normalized.Decision,
                    normalized.Reason,
                    normalized.Evidence,
                    normalized.RefundReference,
                    RetryTxnRef: null,
                    resolvedAt),
                attempt.Status,
                attempt.UpdatedAt,
                journal,
                CancellationToken.None),
            CancellationToken.None);
        if (!applied)
        {
            var winner = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(normalized.AttemptGuid, CancellationToken.None),
                CancellationToken.None);
            if (winner is not null &&
                string.Equals(
                    winner.ResponseCode,
                    CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded,
                    StringComparison.Ordinal))
            {
                var retryWinner = await RunPersistedResolutionRecoveryAsync(
                    winner.AttemptGuid,
                    () => RestoreSupervisorApprovedRetryAsync(
                        cart,
                        winner,
                        CancellationToken.None));
                var retryAllowed = retryWinner.Outcome == CardPaymentRecoveryOutcome.DraftRestored;
                var lockRetained = await IsResolutionLockRetainedAsync(winner.AttemptGuid, retryAllowed);
                return new CardRefundSupervisorResolutionResult(
                    retryAllowed,
                    retryAllowed ? retryWinner.Message : ResolutionPendingMessage(),
                    retryWinner,
                    RetryAllowed: retryAllowed,
                    LockRetained: lockRetained,
                    ResolutionPersisted: true);
            }

            if (winner is not null &&
                string.Equals(
                    winner.ResponseCode,
                    CardRefundSupervisorResolutionCodes.ConfirmedRefunded,
                    StringComparison.Ordinal))
            {
                var completedWinner = await RunPersistedResolutionRecoveryAsync(
                    winner.AttemptGuid,
                    () => CompleteSupervisorConfirmedRefundAsync(
                        cart,
                        session,
                        winner,
                        CancellationToken.None));
                var winnerSucceeded = completedWinner.Outcome is
                    CardPaymentRecoveryOutcome.OrderCompleted or
                    CardPaymentRecoveryOutcome.DraftRestored;
                var lockRetained = await IsResolutionLockRetainedAsync(winner.AttemptGuid, winnerSucceeded);
                return new CardRefundSupervisorResolutionResult(
                    winnerSucceeded,
                    winnerSucceeded ? completedWinner.Message : ResolutionPendingMessage(),
                    completedWinner,
                    LockRetained: lockRetained,
                    ResolutionPersisted: true);
            }

            return new CardRefundSupervisorResolutionResult(
                false,
                "The refund state changed before the supervisor decision was saved. The winning state was retained.",
                LockRetained: winner is null || !IsTerminalSquareStatus(winner.Status),
                ResolutionPersisted: winner is not null && IsPersistedSquareSupervisorResolution(winner));
        }

        if (supervisorAuditReplay is not null)
        {
            try
            {
                await supervisorAuditReplay.PersistAfterCommitAsync(journal, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                TryWriteRecoveryLog(
                    $"supervisor refund audit replay failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                    ex,
                    attempt.AttemptGuid,
                    error: false);
            }
        }

        LocalSquarePaymentAttempt? updatedAttempt;
        try
        {
            updatedAttempt = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(normalized.AttemptGuid, CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWriteRecoveryLog(
                $"supervisor refund post-commit read failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                ex,
                attempt.AttemptGuid,
                error: false);
            return new CardRefundSupervisorResolutionResult(
                false,
                ResolutionPendingMessage(),
                LockRetained: true,
                ResolutionPersisted: true,
                ResolutionApplied: true);
        }

        if (updatedAttempt is null)
        {
            TryWriteRecoveryLog(
                $"supervisor refund post-commit read missing attemptGuid={attempt.AttemptGuid}");
            return new CardRefundSupervisorResolutionResult(
                false,
                ResolutionPendingMessage(),
                LockRetained: true,
                ResolutionPersisted: true,
                ResolutionApplied: true);
        }

        TryWriteRecoveryLog(
            $"supervisor refund resolution saved attemptGuid={attempt.AttemptGuid} decision={normalized.Decision} idempotencyKey={attempt.IdempotencyKey}");

        if (normalized.Decision == CardRefundSupervisorDecision.ContinueWaiting)
        {
            return new CardRefundSupervisorResolutionResult(
                true,
                T("cardRecovery.refund.waitingSaved", "The refund remains locked. Run recovery again after the bank result is available."),
                LockRetained: true,
                ResolutionPersisted: true,
                ResolutionApplied: true);
        }

        if (normalized.Decision == CardRefundSupervisorDecision.ConfirmNotRefunded)
        {
            var recovery = await RunPersistedResolutionRecoveryAsync(
                updatedAttempt.AttemptGuid,
                () => RestoreSupervisorApprovedRetryAsync(
                    cart,
                    updatedAttempt,
                    CancellationToken.None));
            var retryAllowed = recovery.Outcome == CardPaymentRecoveryOutcome.DraftRestored;
            var lockRetained = await IsResolutionLockRetainedAsync(updatedAttempt.AttemptGuid, retryAllowed);
            return new CardRefundSupervisorResolutionResult(
                retryAllowed,
                retryAllowed ? recovery.Message : ResolutionPendingMessage(),
                recovery,
                RetryAllowed: retryAllowed,
                LockRetained: lockRetained,
                ResolutionPersisted: true,
                ResolutionApplied: true);
        }

        var completed = await RunPersistedResolutionRecoveryAsync(
            updatedAttempt.AttemptGuid,
            () => CompleteSupervisorConfirmedRefundAsync(
                cart,
                session,
                updatedAttempt,
                CancellationToken.None));
        var completedSucceeded = completed.Outcome is
            CardPaymentRecoveryOutcome.OrderCompleted or
            CardPaymentRecoveryOutcome.DraftRestored;
        var completedLockRetained = await IsResolutionLockRetainedAsync(
            updatedAttempt.AttemptGuid,
            completedSucceeded);
        return new CardRefundSupervisorResolutionResult(
            completedSucceeded,
            completedSucceeded ? completed.Message : ResolutionPendingMessage(),
            completed,
            LockRetained: completedLockRetained,
            ResolutionPersisted: true,
            ResolutionApplied: true);
    }

    private async Task<CardPaymentRecoveryResult> CompleteSupervisorConfirmedRefundAsync(
        PosCartService cart,
        PosSessionState currentSession,
        LocalSquarePaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        var dialogDetails = BuildRefundDialogDetails(attempt);
        if (!CardRecoveryCartMaterializer.TryPrepare(
                attempt.OrderDraftJson,
                JsonOptions,
                out var preparedDraft) ||
            preparedDraft is null)
        {
            ConsoleLog.Write(
                "SquareRecovery",
                $"confirmed refund draft invalid attemptGuid={attempt.AttemptGuid}");
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.confirmedDraftInvalid", "The refund is confirmed, but POS could not rebuild the original return. Do not refund again; contact support."),
                DialogDetails: dialogDetails);
        }

        CardPaymentOrderDraft draft;
        try
        {
            draft = ValidateAuthoritativeDraft(attempt, preparedDraft);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWriteRecoveryLog(
                $"confirmed refund authoritative draft invalid attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                ex,
                attempt.AttemptGuid,
                error: false);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.confirmedDraftInvalid", "The refund is confirmed, but POS could not rebuild the original return. Do not refund again; contact support."),
                DialogDetails: dialogDetails,
                RefundDetails: BuildRefundDetails(attempt));
        }

        if (string.IsNullOrWhiteSpace(draft.OriginalReference))
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.confirmedDraftInvalid", "The refund is confirmed, but POS could not rebuild the original return. Do not refund again; contact support."),
                DialogDetails: dialogDetails);
        }

        var financialReference = string.Equals(
                attempt.ResponseCode,
                CardRefundSupervisorResolutionCodes.ConfirmedRefunded,
                StringComparison.Ordinal)
            ? attempt.SupervisorFinancialReference
            : attempt.PaymentId ?? attempt.SupervisorFinancialReference;
        if (string.IsNullOrWhiteSpace(financialReference))
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.confirmedDraftInvalid", "The refund is confirmed, but POS could not rebuild the original return. Do not refund again; contact support."),
                DialogDetails: dialogDetails);
        }

        var cardTender = new PaymentTender(
            PaymentMethodKind.Card,
            -Math.Abs(draft.CardAmount),
            CardRefundReference.Format(financialReference, draft.OriginalReference),
            IdempotencyKey: $"SQUARE_ATTEMPT:{attempt.AttemptGuid:N}");
        var tenders = draft.CurrentTenders.Concat([cardTender]).ToList();

        LocalOrder? existingOrder;
        try
        {
            existingOrder = await RunLocalStoreAsync(
                () => orderRepository.GetOrderAsync(draft.OrderGuid, CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"confirmed refund existing order lookup failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ResolutionPendingMessage(),
                DialogDetails: dialogDetails);
        }

        if (existingOrder is not null)
        {
            // 订单可能已在崩溃前落盘；必须先核验精确 Card attempt 证据，绝不能再次发布退款草稿。
            if (!HasExactSquareAttemptTender(existingOrder, attempt.AttemptGuid))
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    ResolutionPendingMessage(),
                    DialogDetails: dialogDetails);
            }

            var existingFinalizePending = await EnsureRecoveryFinalizationAsync(
                attempt,
                LocalSquarePaymentAttemptStatus.OrderCompleted,
                cancellationToken);
            if (existingFinalizePending is null ||
                !await CompleteRecoveryFinalizationAsync(
                    existingFinalizePending,
                    LocalSquarePaymentAttemptStatus.OrderCompleted,
                    CancellationToken.None))
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    ResolutionPendingMessage(),
                    DialogDetails: dialogDetails);
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.OrderCompleted,
                T("cardRecovery.refund.confirmedCompleted", "The confirmed card refund was recovered and the return was saved."),
                existingOrder,
                tenders.Sum(tender => tender.Amount),
                UpdatedSession: currentSession,
                DialogDetails: dialogDetails);
        }

        if (IsApprovedTenderPartial(draft, tenders))
        {
            if (!cart.IsEmpty)
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.square.currentCartNotEmpty", "The confirmed refund is saved, but the current cart is not empty. Complete or clear it, then run recovery again."),
                    DialogDetails: dialogDetails);
            }

            var expectedRevision = cart.Revision;
            var finalizePending = await EnsureRecoveryFinalizationAsync(
                attempt,
                LocalSquarePaymentAttemptStatus.OrderCompleted,
                cancellationToken);
            if (finalizePending is null)
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile Square and the original sale."),
                    DialogDetails: dialogDetails);
            }

            var publication = cart.TryPublishRecoverySnapshot(
                new CardRecoveryAttemptKey(CardProcessorKind.Square, attempt.AttemptGuid),
                expectedRevision,
                draft.CartSnapshot);
            if (!publication.Succeeded)
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    T("cardRecovery.square.currentCartNotEmpty", "The confirmed refund is saved, but the current cart is not empty. Complete or clear it, then run recovery again."),
                    DialogDetails: dialogDetails);
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.DraftRestored,
                T("cardRecovery.refund.confirmedTenderRestored", "The confirmed card refund was restored. Complete the remaining refund methods without refunding this card again."),
                TenderedAmount: tenders.Sum(tender => tender.Amount),
                DialogDetails: dialogDetails,
                RestoredTenders: tenders,
                HasPostCommitWarning: publication.NotificationWarning);
        }

        var fullFinalizePending = await EnsureRecoveryFinalizationAsync(
            attempt,
            LocalSquarePaymentAttemptStatus.OrderCompleted,
            cancellationToken);
        if (fullFinalizePending is null)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile Square and the original sale."),
                DialogDetails: dialogDetails);
        }

        // 完整退款在隔离购物车中重建并保存，绝不覆盖当前活动购物车。
        var recoveryCart = new PosCartService();
        recoveryCart.RestoreSnapshot(draft.CartSnapshot);
        PaymentCheckoutResult checkoutResult;
        try
        {
            var cashTenderedAmount = tenders
                .Where(tender => tender.Method == PaymentMethodKind.Cash)
                .Sum(tender => tender.Amount);
            checkoutResult = checkout.CreatePaymentOrder(recoveryCart, draft.Session, tenders, cashTenderedAmount);
        }
        catch (InvalidOperationException ex)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"confirmed refund checkout restore deferred attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile Square and the original sale."),
                DialogDetails: dialogDetails);
        }

        var order = checkoutResult.Order with { OrderGuid = draft.OrderGuid };
        try
        {
            // 仅新建订单时解析取单来源；订单已存在的分支已在上方完成精确 tender 校验。
            var heldOrder = await TryResolveHeldOrderAsync(
                draft.Session,
                draft.CartSnapshot,
                CancellationToken.None);
            await RunLocalStoreAsync(
                () => heldOrder is null
                    ? orderRepository.SavePendingOrderAsync(order, CancellationToken.None)
                    : orderRepository.SavePendingOrderWithHeldSourceAsync(
                        order,
                        heldOrder,
                        CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "SquareRecovery",
                $"confirmed refund order save failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ResolutionPendingMessage(),
                DialogDetails: dialogDetails);
        }

        var finalizedFullRefund = await CompleteRecoveryFinalizationAsync(
            fullFinalizePending,
            LocalSquarePaymentAttemptStatus.OrderCompleted,
            CancellationToken.None);
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.OrderCompleted,
            T("cardRecovery.refund.confirmedCompleted", "The confirmed card refund was recovered and the return was saved."),
            order,
            tenders.Sum(tender => tender.Amount),
            checkoutResult.ChangeAmount,
            currentSession,
            dialogDetails,
            HasPostCommitWarning: !finalizedFullRefund);
    }

    private async Task<CardPaymentRecoveryResult> RestoreSupervisorApprovedRetryAsync(
        PosCartService cart,
        LocalSquarePaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        var attemptKey = new CardRecoveryAttemptKey(CardProcessorKind.Square, attempt.AttemptGuid);
        var ownsPublishedRecovery = cart.RecoveryOwnerAttemptKey == attemptKey;
        if (!ownsPublishedRecovery && !cart.IsEmpty)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.square.currentCartNotEmpty", "The bank confirmed that no refund was processed, but the current cart is not empty. Complete or clear it, then run recovery again."),
                DialogDetails: BuildRefundDialogDetails(attempt));
        }

        var draft = TryDeserializeDraft(attempt);
        if (draft is null)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                ResolutionPendingMessage(),
                DialogDetails: BuildRefundDialogDetails(attempt));
        }

        try
        {
            draft = ValidateAuthoritativeDraft(attempt, draft);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWriteRecoveryLog(
                $"not-refunded retry authoritative draft invalid attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                ex,
                attempt.AttemptGuid,
                error: false);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.retryDraftInvalid", "The bank confirmed no refund, but POS could not rebuild the original return. Do not retry until support checks this attempt."),
                DialogDetails: BuildRefundDialogDetails(attempt));
        }

        var expectedRevision = cart.Revision;
        var finalizePending = await EnsureRecoveryFinalizationAsync(
            attempt,
            LocalSquarePaymentAttemptStatus.Abandoned,
            cancellationToken);
        if (finalizePending is null)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.refund.requiresReview", "A previous card refund is still unresolved. Do not refund again; ask a supervisor to reconcile Square and the original sale."),
                DialogDetails: BuildRefundDialogDetails(attempt));
        }

        // 重启或重复恢复时，精确 owner 表示快照已经发布；不得再次要求空购物车或释放别人的 owner。
        var publication = ownsPublishedRecovery
            ? new PosCartRecoveryPublicationResult(true, false, cart.Revision)
            : cart.TryPublishRecoverySnapshot(
                attemptKey,
                expectedRevision,
                draft.CartSnapshot);
        if (!publication.Succeeded)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                T("cardRecovery.square.currentCartNotEmpty", "The bank confirmed that no refund was processed, but the current cart is not empty. Complete or clear it, then run recovery again."),
                DialogDetails: BuildRefundDialogDetails(attempt));
        }

        // FAILED/REJECTED 的金融失败证据已经持久化；这里只发布可用的替代退款草稿，
        // 必须等现金/代金券订单保存路径完成 CAS 后才允许 Abandoned 和释放 owner。
        var requiresAlternativeRefundMethod =
            string.Equals(attempt.PaymentStatus?.Trim(), "FAILED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attempt.PaymentStatus?.Trim(), "REJECTED", StringComparison.OrdinalIgnoreCase);
        var message = requiresAlternativeRefundMethod
            ? T(
                "cardRecovery.refund.squareAlternativeMethodRequired",
                "Square refund failed or was rejected. The original return has been restored and must be refunded using cash, a voucher, or another non-card method.")
            : T(
                "cardRecovery.refund.retryAllowed",
                "The bank confirmed that no refund was processed. The original return is ready to retry with the same operation.");
        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.DraftRestored,
            message,
            TenderedAmount: draft.CurrentTenders.Sum(tender => tender.Amount),
            DialogDetails: BuildRefundDialogDetails(attempt),
            RestoredTenders: draft.CurrentTenders,
            HasPostCommitWarning: publication.NotificationWarning)
        {
            RequiresAlternativeRefundMethod = requiresAlternativeRefundMethod,
            DraftHandoffKey = !requiresAlternativeRefundMethod &&
                string.Equals(
                    attempt.ResponseCode,
                    CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded,
                    StringComparison.Ordinal)
                    ? new CardRecoveryAttemptKey(CardProcessorKind.Square, attempt.AttemptGuid)
                    : null
        };
    }

    /// <summary>
    /// UI 已完整接收可交接的恢复草稿后，才终结旧 attempt 并释放 publication。
    /// FAILED/REJECTED 替代退款和已批准 tender 的订单恢复仍由订单落库路径收尾。
    /// </summary>
    internal async Task<bool> CompleteDraftHandoffAsync(
        Guid attemptGuid,
        PosCartService cart,
        CancellationToken cancellationToken = default)
    {
        var attemptKey = new CardRecoveryAttemptKey(CardProcessorKind.Square, attemptGuid);
        LocalSquarePaymentAttempt? attempt;
        try
        {
            attempt = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"square draft handoff read failed attemptGuid={attemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attemptGuid.ToString("D")),
                ex);
            return false;
        }

        if (!TryGetDraftHandoffTargetStatus(attempt, out var targetStatus))
        {
            return false;
        }

        var current = attempt!;
        var isTerminal = IsCompletedDraftHandoff(current, targetStatus);
        if (!isTerminal && !IsPendingDraftHandoff(current, targetStatus))
        {
            return false;
        }

        if (cart.RecoveryOwnerAttemptKey is not CardRecoveryAttemptKey ownerKey)
        {
            return isTerminal;
        }

        if (ownerKey != attemptKey)
        {
            return false;
        }

        if (!isTerminal &&
            !await CompleteRecoveryFinalizationAsync(
                 current,
                 targetStatus,
                 CancellationToken.None))
        {
            return false;
        }

        LocalSquarePaymentAttempt? persistedWinner;
        try
        {
            persistedWinner = await RunLocalStoreAsync(
                () => attemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"square draft handoff verification failed attemptGuid={attemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attemptGuid.ToString("D")),
                ex);
            return false;
        }

        if (!TryGetDraftHandoffTargetStatus(persistedWinner, out var persistedTargetStatus) ||
            persistedTargetStatus != targetStatus ||
            !IsCompletedDraftHandoff(persistedWinner!, targetStatus))
        {
            return false;
        }

        // 数据库终态确认后才释放精确 owner；其它 attempt 的 publication 永远不能被本次交接触碰。
        return cart.CompleteRecoveryPublication(attemptKey);
    }

    private static bool IsConfirmedNotRefundedDraftHandoff(LocalSquarePaymentAttempt? attempt) =>
        attempt is not null &&
        string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            attempt.ResponseCode,
            CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded,
            StringComparison.Ordinal) &&
        !string.Equals(attempt.PaymentStatus?.Trim(), "FAILED", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(attempt.PaymentStatus?.Trim(), "REJECTED", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetDraftHandoffTargetStatus(
        LocalSquarePaymentAttempt? attempt,
        out LocalSquarePaymentAttemptStatus targetStatus)
    {
        if (IsConfirmedNotRefundedDraftHandoff(attempt) ||
            IsSupervisorNotPaidSaleDraftHandoff(attempt))
        {
            targetStatus = LocalSquarePaymentAttemptStatus.Abandoned;
            return true;
        }

        if (IsCanceledSaleDraftHandoff(attempt))
        {
            targetStatus = LocalSquarePaymentAttemptStatus.Canceled;
            return true;
        }

        targetStatus = default;
        return false;
    }

    private static bool IsSupervisorNotPaidSaleDraftHandoff(LocalSquarePaymentAttempt? attempt) =>
        attempt is not null &&
        string.Equals(attempt.OperationKind, "Sale", StringComparison.Ordinal) &&
        string.Equals(
            attempt.ResponseCode,
            ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid,
            StringComparison.Ordinal) &&
        string.IsNullOrWhiteSpace(attempt.PaymentId) &&
        string.IsNullOrWhiteSpace(attempt.PaymentStatus);

    private static bool IsCanceledSaleDraftHandoff(LocalSquarePaymentAttempt? attempt) =>
        attempt is not null &&
        string.Equals(attempt.OperationKind, "Sale", StringComparison.Ordinal) &&
        string.Equals(attempt.CheckoutStatus?.Trim(), "CANCELED", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(attempt.PaymentId) &&
        string.IsNullOrWhiteSpace(attempt.PaymentStatus) &&
        (string.IsNullOrWhiteSpace(attempt.ResponseCode) ||
         string.Equals(
             attempt.ResponseCode,
             ActiveSessionSupervisorResolutionCodes.ContinueWaiting,
             StringComparison.Ordinal));

    private static bool IsUnfinalizedTerminalRefundFailure(LocalSquarePaymentAttempt? attempt) =>
        attempt is not null &&
        string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(attempt.SubmissionToken) &&
        (attempt.Status is LocalSquarePaymentAttemptStatus.Pending or
            LocalSquarePaymentAttemptStatus.Recovering or
            LocalSquarePaymentAttemptStatus.Unknown or
            LocalSquarePaymentAttemptStatus.Failed) &&
        (string.Equals(attempt.PaymentStatus?.Trim(), "FAILED", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(attempt.PaymentStatus?.Trim(), "REJECTED", StringComparison.OrdinalIgnoreCase)) &&
        string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal) &&
        attempt.RecoveryTargetStatus is null &&
        !IsPersistedSquareSupervisorResolution(attempt);

    private static bool IsPendingDraftHandoff(
        LocalSquarePaymentAttempt attempt,
        LocalSquarePaymentAttemptStatus targetStatus) =>
        !IsTerminalSquareStatus(attempt.Status) &&
        attempt.Status != LocalSquarePaymentAttemptStatus.PaymentVerified &&
        string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) &&
        attempt.RecoveryTargetStatus == targetStatus;

    private static bool IsCompletedDraftHandoff(
        LocalSquarePaymentAttempt attempt,
        LocalSquarePaymentAttemptStatus targetStatus) =>
        attempt.Status == targetStatus &&
        string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal) &&
        attempt.RecoveryTargetStatus is null;

    private static CardPaymentRecoveryDialogDetails BuildRefundDialogDetails(LocalSquarePaymentAttempt attempt) =>
        new(
            attempt.CheckoutId,
            attempt.IdempotencyKey,
            attempt.ResponseCode,
            attempt.ResponseText,
            attempt.Amount,
            attempt.UpdatedAt);

    private static CardRefundRecoveryDetails BuildRefundDetails(LocalSquarePaymentAttempt attempt)
    {
        string? originalReference = null;
        try
        {
            originalReference = DeserializeDraft(attempt).OriginalReference;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // 草稿损坏时仍保留 attempt 身份供主管审计，不能因为缺少展示字段释放退款锁。
        }

        return new CardRefundRecoveryDetails(
            attempt.AttemptGuid,
            CardProcessorKind.Square,
            attempt.OperationGuid,
            attempt.Amount,
            originalReference);
    }

    private static bool IsApprovedTenderPartial(
        CardPaymentOrderDraft draft,
        IReadOnlyList<PaymentTender> tenders)
    {
        var actualAmount = decimal.Round(draft.ActualAmount, 2, MidpointRounding.AwayFromZero);
        var tenderTotal = decimal.Round(
            tenders.Sum(tender => tender.Amount),
            2,
            MidpointRounding.AwayFromZero);
        return actualAmount < 0m && tenderTotal < 0m && tenderTotal > actualAmount;
    }

    private static bool HasExactSquareAttemptTender(LocalOrder order, Guid attemptGuid) =>
        order.Payments.Any(payment =>
            payment.Method == PaymentMethodKind.Card &&
            string.Equals(
                payment.IdempotencyKey,
                SquareAttemptTenderKey(attemptGuid),
                StringComparison.Ordinal));

    internal static bool MatchesPersistedAlternativeRefundOrder(
        LocalSquarePaymentAttempt attempt,
        CardPaymentOrderDraft draft,
        LocalOrder order)
    {
        var snapshotLines = draft.CartSnapshot.Lines;
        if (draft.Session is null ||
            snapshotLines is not { Count: > 0 } ||
            order.OrderGuid != draft.OrderGuid ||
            attempt.OperationGuid is not null && attempt.OperationGuid.Value != draft.OrderGuid ||
            !SameTerminal(attempt.StoreCode, draft.Session.StoreCode) ||
            !SameTerminal(attempt.DeviceCode, draft.Session.DeviceCode) ||
            !SameTerminal(order.StoreCode, attempt.StoreCode) ||
            !SameTerminal(order.DeviceCode, attempt.DeviceCode) ||
            !MoneyEquals(Math.Abs(attempt.Amount), Math.Abs(draft.CardAmount)) ||
            !MoneyEquals(order.ActualAmount, draft.ActualAmount) ||
            !MoneyEquals(
                order.ActualAmount,
                snapshotLines.Sum(ExpectedSignedLineAmount)) ||
            !MoneyEquals(
                order.TotalAmount,
                snapshotLines.Sum(line => SignedAmount(line.Quantity * line.UnitPrice, line.Kind))) ||
            !MoneyEquals(order.DiscountAmount, snapshotLines.Sum(line => line.DiscountAmount)) ||
            order.Lines.Count != snapshotLines.Count ||
            snapshotLines.Any(line =>
                line.Kind != CartLineKind.Return ||
                string.IsNullOrWhiteSpace(line.ReturnSourceKey) ||
                !SameTerminal(line.StoreCode, attempt.StoreCode)))
        {
            return false;
        }

        // 退款行按来源逐一匹配，防止同一个 OrderGuid 被另一组商品或来源复用。
        var unmatchedLines = order.Lines.ToList();
        foreach (var expectedLine in snapshotLines)
        {
            var matchingIndex = unmatchedLines.FindIndex(line =>
                MatchesPersistedRefundLine(expectedLine, line));
            if (matchingIndex < 0)
            {
                return false;
            }

            unmatchedLines.RemoveAt(matchingIndex);
        }

        return MatchesPersistedRefundTenders(order.Payments, draft.CurrentTenders);
    }

    private static bool MatchesPersistedRefundLine(
        PosCartLineSnapshot expected,
        LocalOrderLine actual) =>
        expected.Kind == CartLineKind.Return &&
        actual.Kind == OrderLineKind.Return &&
        string.Equals(expected.ProductCode, actual.ProductCode, StringComparison.Ordinal) &&
        string.Equals(expected.ReferenceCode, actual.ReferenceCode, StringComparison.Ordinal) &&
        string.Equals(expected.DisplayName, actual.DisplayName, StringComparison.Ordinal) &&
        string.Equals(expected.LookupCode, actual.LookupCode, StringComparison.Ordinal) &&
        string.Equals(expected.ItemNumber, actual.ItemNumber, StringComparison.Ordinal) &&
        MoneyEquals(expected.Quantity, actual.Quantity) &&
        MoneyEquals(expected.UnitPrice, actual.UnitPrice) &&
        MoneyEquals(expected.DiscountAmount, actual.DiscountAmount) &&
        MoneyEquals(ExpectedSignedLineAmount(expected), actual.ActualAmount) &&
        string.Equals(expected.ReturnSourceKey, actual.ReturnSourceKey, StringComparison.Ordinal) &&
        expected.OriginalOrderGuid == actual.OriginalOrderGuid &&
        expected.OriginalOrderLineGuid == actual.OriginalOrderDetailGuid &&
        expected.PriceSource == actual.PriceSource;

    private static bool MatchesPersistedRefundTenders(
        IReadOnlyList<LocalPayment> existingPayments,
        IReadOnlyList<PaymentTender> draftTenders)
    {
        if (existingPayments.Count <= draftTenders.Count)
        {
            return false;
        }

        // 与替代退款保存路径相同：草稿已有 tender 必须保持顺序和全部字段，
        // 草稿之后新增的 tender 只能是 Cash/Voucher，绝不能凭空增加 Card。
        for (var index = 0; index < draftTenders.Count; index++)
        {
            if (!MatchesPersistedRefundTender(draftTenders[index], existingPayments[index]))
            {
                return false;
            }
        }

        return existingPayments
            .Skip(draftTenders.Count)
            .All(payment => payment.Method is PaymentMethodKind.Cash or PaymentMethodKind.Voucher);
    }

    private static bool HasCompleteAlternativeRefundSettlement(
        decimal actualAmount,
        IReadOnlyList<LocalPayment> payments)
    {
        // 负向 voucher 只有拿到发券 reference 才代表外部结算完成；pending 订单只能保留恢复锁。
        return HasBalancedAlternativeRefundSettlement(actualAmount, payments) &&
            !payments.Any(payment =>
                payment.Method == PaymentMethodKind.Voucher &&
                payment.Amount < 0m &&
                !HasIssuedVoucherRefundReference(payment.Reference));
    }

    private static bool HasPendingAlternativeRefundVoucherSettlement(
        decimal actualAmount,
        IReadOnlyList<LocalPayment> payments) =>
        HasBalancedAlternativeRefundSettlement(actualAmount, payments) &&
        payments.Any(payment =>
            payment.Method == PaymentMethodKind.Voucher &&
            payment.Amount < 0m &&
            !HasIssuedVoucherRefundReference(payment.Reference));

    private static bool HasBalancedAlternativeRefundSettlement(
        decimal actualAmount,
        IReadOnlyList<LocalPayment> payments)
    {
        if (actualAmount >= 0m ||
            payments.Count == 0 ||
            payments.Any(payment => payment.Amount >= 0m))
        {
            return false;
        }

        var refundAmount = Math.Abs(decimal.Round(actualAmount, 2, MidpointRounding.AwayFromZero));
        var nonCashRefundTotal = Math.Abs(decimal.Round(
            payments
                .Where(payment => payment.Method != PaymentMethodKind.Cash)
                .Sum(payment => payment.Amount),
            2,
            MidpointRounding.AwayFromZero));
        if (nonCashRefundTotal > refundAmount)
        {
            return false;
        }

        var hasCashRefund = payments.Any(payment => payment.Method == PaymentMethodKind.Cash);
        var requiredTotal = hasCashRefund
            ? -decimal.Round(
                nonCashRefundTotal +
                new CashRoundingPolicy().CalculateRoundedCashDue(refundAmount, nonCashRefundTotal),
                2,
                MidpointRounding.AwayFromZero)
            : actualAmount;
        return MoneyEquals(payments.Sum(payment => payment.Amount), requiredTotal);
    }

    private static bool MatchesPersistedRefundTender(
        PaymentTender expected,
        LocalPayment actual)
    {
        var expectedTransactions = expected.CardTransactions ?? Array.Empty<CardTransactionDto>();
        var actualTransactions = actual.CardTransactions ?? Array.Empty<CardTransactionDto>();
        return expected.Method == actual.Method &&
            MoneyEquals(expected.Amount, actual.Amount) &&
            MatchesPersistedRefundReference(expected, actual) &&
            string.Equals(
                NormalizeRecoveryOptional(expected.IdempotencyKey),
                NormalizeRecoveryOptional(actual.IdempotencyKey),
                StringComparison.Ordinal) &&
            expectedTransactions.SequenceEqual(actualTransactions);
    }

    private static bool MatchesPersistedRefundReference(
        PaymentTender expected,
        LocalPayment actual)
    {
        var expectedReference = NormalizeRecoveryOptional(expected.Reference);
        var actualReference = NormalizeRecoveryOptional(actual.Reference);
        return string.Equals(expectedReference, actualReference, StringComparison.Ordinal) ||
            expected.Method == PaymentMethodKind.Voucher &&
            string.Equals(expectedReference, "VOUCHER_REFUND_PENDING", StringComparison.OrdinalIgnoreCase) &&
            HasIssuedVoucherRefundReference(actualReference);
    }

    private static bool HasIssuedVoucherRefundReference(string? reference) =>
        !string.IsNullOrWhiteSpace(reference) &&
        !string.Equals(reference.Trim(), "VOUCHER_REFUND_PENDING", StringComparison.OrdinalIgnoreCase);

    private static decimal ExpectedSignedLineAmount(PosCartLineSnapshot line) =>
        SignedAmount(
            decimal.Round(
                (line.Quantity * line.UnitPrice) - line.DiscountAmount,
                2,
                MidpointRounding.AwayFromZero),
            line.Kind);

    private static decimal SignedAmount(decimal amount, CartLineKind kind) =>
        kind == CartLineKind.Return ? -amount : amount;

    private static bool SameTerminal(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool MoneyEquals(decimal left, decimal right) =>
        decimal.Round(left, 2, MidpointRounding.AwayFromZero) ==
        decimal.Round(right, 2, MidpointRounding.AwayFromZero);

    private static string? NormalizeRecoveryOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CardPaymentOrderDraft ValidateAuthoritativeDraft(
        LocalSquarePaymentAttempt attempt,
        CardPaymentOrderDraft draft)
    {
        var isSale = string.Equals(attempt.OperationKind, "Sale", StringComparison.OrdinalIgnoreCase);
        var isRefund = string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase);
        if (!isSale && !isRefund)
        {
            throw new InvalidOperationException("Square attempt operation kind is invalid.");
        }

        var validationCart = new PosCartService();
        validationCart.RestoreSnapshot(draft.CartSnapshot);
        var expectedTxnType = isRefund ? "R" : "P";
        if (draft.OrderGuid == Guid.Empty ||
            !SameTerminal(draft.Session.StoreCode, attempt.StoreCode) ||
            !SameTerminal(draft.Session.DeviceCode, attempt.DeviceCode) ||
            !SameTerminal(draft.Session.CashierId, attempt.CashierId) ||
            !SameTerminal(draft.TxnType, expectedTxnType))
        {
            throw new InvalidOperationException("Square payment draft identity does not match its attempt.");
        }

        // 历史销售 attempt 允许 OperationGuid 为空；退款必须存在，非空时始终精确匹配 draft。
        if ((attempt.OperationGuid is not null && attempt.OperationGuid != draft.OrderGuid) ||
            (isRefund && attempt.OperationGuid is null))
        {
            throw new InvalidOperationException("Square payment draft order identity does not match its attempt.");
        }

        var attemptAmount = RoundRecoveryCurrency(attempt.Amount);
        var cardAmount = RoundRecoveryCurrency(draft.CardAmount);
        var actualAmount = RoundRecoveryCurrency(draft.ActualAmount);
        var cartActualAmount = RoundRecoveryCurrency(validationCart.ActualAmount);
        var expectedAmountCents = checked(decimal.ToInt64(decimal.Round(
            attemptAmount * 100m,
            0,
            MidpointRounding.AwayFromZero)));
        if (attemptAmount <= 0m ||
            cardAmount <= 0m ||
            (isSale ? cardAmount != attemptAmount : cardAmount > attemptAmount) ||
            attempt.AmountCents != expectedAmountCents ||
            actualAmount != cartActualAmount)
        {
            throw new InvalidOperationException("Square payment draft amount does not match its attempt or cart snapshot.");
        }

        var currentTenderTotal = RoundRecoveryCurrency(draft.CurrentTenders.Sum(tender => tender.Amount));
        if (isSale)
        {
            if (actualAmount <= 0m || draft.CurrentTenders.Any(tender => tender.Amount <= 0m))
            {
                throw new InvalidOperationException("Square sale draft contains invalid tender signs.");
            }

            var nonCashTotal = RoundRecoveryCurrency(draft.CurrentTenders
                .Where(tender => tender.Method != PaymentMethodKind.Cash)
                .Sum(tender => tender.Amount));
            if (RoundRecoveryCurrency(nonCashTotal + cardAmount) > actualAmount ||
                RoundRecoveryCurrency(currentTenderTotal + cardAmount) < actualAmount)
            {
                throw new InvalidOperationException("Square sale draft tender totals are inconsistent.");
            }

            return draft;
        }

        if (actualAmount >= 0m ||
            string.IsNullOrWhiteSpace(draft.OriginalReference) ||
            draft.CurrentTenders.Any(tender => tender.Amount >= 0m) ||
            RoundRecoveryCurrency(currentTenderTotal - cardAmount) < actualAmount)
        {
            throw new InvalidOperationException("Square refund draft contains invalid amount or tender semantics.");
        }

        var nonCashRefundTotal = Math.Abs(RoundRecoveryCurrency(draft.CurrentTenders
            .Where(tender => tender.Method != PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount)));
        if (RoundRecoveryCurrency(nonCashRefundTotal + cardAmount) > Math.Abs(actualAmount))
        {
            throw new InvalidOperationException("Square refund draft non-cash tenders exceed the return amount.");
        }

        if (!string.IsNullOrWhiteSpace(attempt.RefundBusinessKey) &&
            !string.Equals(
                attempt.RefundBusinessKey.Trim(),
                BuildRecoveryRefundBusinessKey(attempt, draft),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Square refund business identity does not match its draft.");
        }

        return draft;
    }

    private static decimal RoundRecoveryCurrency(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static string BuildRecoveryRefundBusinessKey(
        LocalSquarePaymentAttempt attempt,
        CardPaymentOrderDraft draft)
    {
        var canonical = string.Join(
            "\n",
            "ordinary-card-refund-v1",
            "SQUARE",
            attempt.Environment.Trim().ToUpperInvariant(),
            attempt.StoreCode.Trim().ToUpperInvariant(),
            attempt.DeviceCode.Trim().ToUpperInvariant(),
            draft.OriginalReference!.Trim().ToUpperInvariant(),
            attempt.AmountCents.ToString(CultureInfo.InvariantCulture),
            attempt.Currency.Trim().ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string SquareAttemptTenderKey(Guid attemptGuid) =>
        $"SQUARE_ATTEMPT:{attemptGuid:N}";

    private async Task<CardPaymentRecoveryResult> CompleteVerifiedAttemptAsync(
        LocalSquarePaymentAttempt attempt,
        CardPaymentOrderDraft draft,
        string paymentId,
        string paymentStatus,
        string? cardBrand,
        string? maskedCardNumber,
        string? authCode,
        CancellationToken cancellationToken)
    {
        if (!CardRecoveryCartMaterializer.TryPrepare(
                attempt.OrderDraftJson,
                JsonOptions,
                out var preparedDraft) ||
            preparedDraft is null)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        try
        {
            draft = ValidateAuthoritativeDraft(attempt, preparedDraft);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWriteRecoveryLog(
                $"verified payment authoritative draft invalid attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                ex,
                attempt.AttemptGuid,
                error: false);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        LocalOrder? existingOrder;
        try
        {
            existingOrder = await RunLocalStoreAsync(
                () => orderRepository.GetOrderAsync(draft.OrderGuid, CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"verified payment existing order lookup failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        if (existingOrder is not null)
        {
            if (!HasExactSquareAttemptTender(existingOrder, attempt.AttemptGuid))
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    UnknownResultMessage());
            }

            LocalSquarePaymentAttempt? existingFinalizePending;
            try
            {
                existingFinalizePending = await EnsureRecoveryFinalizationAsync(
                    attempt,
                    LocalSquarePaymentAttemptStatus.OrderCompleted,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
            {
                ConsoleLog.WriteWarning(
                    "SquareRecovery",
                    $"verified existing order finalization prepare failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                    new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                    ex);
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    UnknownResultMessage());
            }

            if (existingFinalizePending is null ||
                !await CompleteRecoveryFinalizationAsync(
                    existingFinalizePending,
                    LocalSquarePaymentAttemptStatus.OrderCompleted,
                    CancellationToken.None))
            {
                return new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.Unknown,
                    UnknownResultMessage());
            }

            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.OrderCompleted,
                T("cardRecovery.square.approved", "The previous Square card payment was successful. The order has been recovered and saved automatically."),
                existingOrder);
        }

        LocalSquarePaymentAttempt? finalizePending;
        try
        {
            finalizePending = await EnsureRecoveryFinalizationAsync(
                attempt,
                LocalSquarePaymentAttemptStatus.OrderCompleted,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"verified payment finalization prepare failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        if (finalizePending is null)
        {
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        var supervisorConfirmed =
            string.Equals(
                attempt.ResponseCode,
                ActiveSessionSupervisorResolutionCodes.ConfirmedPaid,
                StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(attempt.PaymentId) &&
            !string.IsNullOrWhiteSpace(attempt.SupervisorFinancialReference);
        var tenderReference = supervisorConfirmed ? paymentId : $"SQ:{paymentId}";
        var transactionProcessor = supervisorConfirmed ? "Square Supervisor" : "Square";
        var transactionResponseText = supervisorConfirmed
            ? "Supervisor confirmed paid."
            : paymentStatus;

        var recoveryCart = new PosCartService();
        recoveryCart.RestoreSnapshot(draft.CartSnapshot);
        var cardTender = new PaymentTender(
            PaymentMethodKind.Card,
            Math.Abs(draft.CardAmount),
            tenderReference,
            CardTransactions:
            [
                new CardTransactionDto(
                    transactionProcessor,
                    paymentId,
                    authCode,
                    cardBrand,
                    null,
                    maskedCardNumber,
                    null,
                    null,
                    transactionResponseText,
                    null,
                    DateTimeOffset.UtcNow,
                    Math.Abs(draft.CardAmount),
                    null)
            ],
            IdempotencyKey: $"SQUARE_ATTEMPT:{attempt.AttemptGuid:N}");
        var tenders = draft.CurrentTenders.Concat([cardTender]).ToList();
        var cashTenderedAmount = tenders
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount);
        PaymentCheckoutResult checkoutResult;
        try
        {
            checkoutResult = checkout.CreatePaymentOrder(recoveryCart, draft.Session, tenders, cashTenderedAmount);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"verified payment order rebuild failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        var order = checkoutResult.Order with { OrderGuid = draft.OrderGuid };
        try
        {
            // 这里只处理首次保存；已有订单必须先通过上方的精确 tender key 分支。
            var heldOrder = await TryResolveHeldOrderAsync(
                draft.Session,
                draft.CartSnapshot,
                CancellationToken.None);
            await RunLocalStoreAsync(
                () => heldOrder is null
                    ? orderRepository.SavePendingOrderAsync(order, CancellationToken.None)
                    : orderRepository.SavePendingOrderWithHeldSourceAsync(
                        order,
                        heldOrder,
                        CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "SquareRecovery",
                $"verified payment order save failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                UnknownResultMessage());
        }

        var hasPostCommitWarning = false;
        try
        {
            hasPostCommitWarning = !await CompleteRecoveryFinalizationAsync(
                finalizePending,
                LocalSquarePaymentAttemptStatus.OrderCompleted,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            hasPostCommitWarning = true;
            ConsoleLog.WriteWarning(
                "SquareRecovery",
                $"verified payment order saved but attempt finalization failed attemptGuid={attempt.AttemptGuid} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
        }

        return new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.OrderCompleted,
            T("cardRecovery.square.approved", "The previous Square card payment was successful. The order has been recovered and saved automatically."),
            order,
            HasPostCommitWarning: hasPostCommitWarning);
    }

    private bool TryDeferForCurrentCart(
        PosCartService cart,
        LocalSquarePaymentAttempt attempt,
        string reason,
        out CardPaymentRecoveryResult result)
    {
        if (cart.IsEmpty)
        {
            result = CardPaymentRecoveryResult.None;
            return false;
        }

        // 褰撳墠璐墿杞﹀凡鏈夋柊璁㈠崟鏃讹紝涓嶆仮澶嶆棫鑽夌銆佷笉淇濆瓨璁㈠崟锛屼篃涓嶆妸鏃?attempt 鏍囪涓哄凡澶勭悊銆?
        ConsoleLog.Write(
            "SquareRecovery",
            $"defer recovery because current cart is not empty attemptGuid={attempt.AttemptGuid} checkoutId={attempt.CheckoutId ?? "<null>"} reason={reason}");
        result = new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.Unknown, CurrentCartNotEmptyMessage());
        return true;
    }

    private static CardPaymentOrderDraft DeserializeDraft(LocalSquarePaymentAttempt attempt)
    {
        return JsonSerializer.Deserialize<CardPaymentOrderDraft>(attempt.OrderDraftJson, JsonOptions)
            ?? throw new InvalidOperationException("Square payment order draft is invalid.");
    }

    private static bool IsSquarePendingStatus(string status)
    {
        return string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "CANCEL_REQUESTED", StringComparison.OrdinalIgnoreCase);
    }

    private string CurrentCartNotEmptyMessage()
    {
        return T("cardRecovery.square.currentCartNotEmpty", "The previous Square card result needs handling, but the current cart already contains items. Complete or clear the current cart before recovering the previous order.");
    }

    private string UnknownResultMessage()
    {
        return T("cardRecovery.square.unknown", "The previous Square card result cannot be confirmed. Ask a supervisor to confirm the Square backend status before continuing.");
    }

    private static LocalFinancialSupervisorResolution BuildRefundSupervisorJournal(
        LocalSquarePaymentAttempt attempt,
        CardRefundSupervisorResolution resolution,
        PosSessionState session,
        DateTimeOffset resolvedAt)
    {
        var authorizer = OperationAuthorizationScope.CurrentAuthorizingSession ?? session.CashierSession;
        var operatorCashierId = authorizer?.CashierId ?? session.CashierId;
        var operatorUserGuid = authorizer?.UserGuid ?? session.CashierSession?.UserGuid;
        var operatorName = authorizer?.CashierName ?? session.CashierName;
        var resolutionGuid = Guid.NewGuid();
        var auditEventId = Guid.NewGuid();
        var auditEvent = new OperationAuditEventDto
        {
            EventId = auditEventId,
            OccurredAtUtc = resolvedAt,
            OperationType = "CARD_REFUND_SUPERVISOR_RESOLUTION",
            Outcome = resolution.Decision.ToString(),
            CashierId = operatorCashierId,
            UserGuid = operatorUserGuid,
            CashierName = operatorName,
            StoreCode = attempt.StoreCode,
            DeviceCode = attempt.DeviceCode,
            CorrelationId = attempt.AttemptGuid.ToString("D"),
            PaymentMethod = CardProcessorKind.Square.ToString(),
            ReasonCode = resolution.Decision.ToString(),
            SafeMessage = resolution.Reason,
            PaymentAmount = Math.Abs(attempt.Amount),
            Properties = new Dictionary<string, string?>
            {
                ["attemptGuid"] = attempt.AttemptGuid.ToString("D"),
                ["operationGuid"] = attempt.OperationGuid?.ToString("D"),
                ["checkoutId"] = attempt.CheckoutId,
                ["evidence"] = resolution.Evidence,
                ["financialReference"] = resolution.RefundReference,
                ["retryReference"] = attempt.IdempotencyKey
            }
        };
        return new LocalFinancialSupervisorResolution(
            resolutionGuid,
            LocalFinancialSupervisorResolutionTarget.CardRefund,
            CardProcessorKind.Square.ToString(),
            attempt.Environment,
            attempt.StoreCode,
            attempt.DeviceCode,
            attempt.AttemptGuid,
            null,
            attempt.OperationGuid,
            attempt.CheckoutId,
            resolution.Decision.ToString(),
            operatorCashierId,
            operatorUserGuid,
            operatorName,
            resolution.Reason,
            resolution.Evidence,
            resolution.RefundReference,
            attempt.IdempotencyKey,
            resolvedAt,
            auditEventId,
            JsonSerializer.Serialize(auditEvent, JsonOptions));
    }

    private string T(string key, string fallback)
    {
        var value = localization?.T(key);
        return string.IsNullOrWhiteSpace(value) || value == $"[[{key}]]" ? fallback : value;
    }

    private string Format(string key, string fallback, params object[] args)
    {
        var template = T(key, fallback);
        return string.Format(localization?.CurrentCulture ?? System.Globalization.CultureInfo.CurrentCulture, template, args);
    }

    private static void TryWriteRecoveryLog(
        string message,
        Exception? exception = null,
        Guid? attemptGuid = null,
        bool error = false)
    {
        try
        {
            // 带异常的失败记 Warning（订单/最终状态落库失败需人工处理时记 Error），以 attemptGuid 作 TraceId；其余保持 Information。
            var context = new ApplicationLogContext(TraceId: attemptGuid?.ToString("D"));
            if (exception is null)
            {
                ConsoleLog.Write("SquareRecovery", message);
            }
            else if (error)
            {
                ConsoleLog.WriteError("SquareRecovery", message, context, exception);
            }
            else
            {
                ConsoleLog.WriteWarning("SquareRecovery", message, context, exception);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 主管决定已提交后，诊断日志只能尽力写入，不能把成功的提交改写成失败。
        }
    }

    private static Task RunLocalStoreAsync(Func<Task> operation, CancellationToken cancellationToken) =>
        Task.Run(operation, cancellationToken);

    private static Task<T> RunLocalStoreAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken) =>
        Task.Run(operation, cancellationToken);
}
